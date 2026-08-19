#########################################################################################################################
# ПАНЧИК.РФ                                                                                                             #
# Скрипт common                                                                                                         #
# Версия: 2.3 (13.08.2026)                                                                                              #
# Только для сотрудников BIM-отдела.                                                                                    #
# Скрипт выполняет следующие функции:                                                                                   #
#   - безопасный запуск внешних консольных утилит (RevitServerTool, FiletoolsTaskRunner)                                #
#   - нормализация списков-фильтров из config.json                                                                      #
#                                                                                                                       #
# Скрипт является частью пайплайна DownloadAndCopyRVTNWC и не может работать отдельно!                                  #
#                                                                                                                       #
#########################################################################################################################

<#
.SYNOPSIS
    Запускает внешний процесс и возвращает его код возврата вместе с выводом.

.DESCRIPTION
    Оба потока вывода читаются ОДНОВРЕМЕННО через ReadToEndAsync, до ожидания
    завершения процесса. Это принципиально: при перенаправленных stdout/stderr
    вызов WaitForExit() раньше чтения приводит к взаимоблокировке, как только
    утилита выведет больше объема буфера канала (~4 КБ) — процесс блокируется
    на записи, а скрипт бесконечно ждет его завершения.

    Чтение сделано именно задачами .NET, а не через Register-ObjectEvent:
    обработчики событий PowerShell не выполняются, пока поток скрипта
    заблокирован в WaitForExit, и вывод терялся бы целиком.

.PARAMETER TimeoutSeconds
    Ограничение времени работы. 0 — без ограничения. По истечении процесс
    принудительно завершается, в результате выставляется TimedOut = $true.

.PARAMETER OutputEncoding
    Кодировка, в которой читаются stdout/stderr. По умолчанию — кодовая страница
    OEM (на русской Windows это 866). Консольные утилиты вроде RevitServerTool
    выводят кириллицу именно в OEM, и без этого в логе получаются кракозябры.
#>
function Invoke-ExternalProcess {
    param (
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string]$Arguments = "",
        [int]$TimeoutSeconds = 0,
        [System.Text.Encoding]$OutputEncoding = $null
    )

    # По умолчанию читаем вывод в кодировке консоли (OEM): RevitServerTool и другие
    # консольные утилиты пишут кириллицу в OEM-кодировке, а не в UTF-8/ANSI.
    if ($null -eq $OutputEncoding) {
        try {
            $oemCp = [System.Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage
            $OutputEncoding = [System.Text.Encoding]::GetEncoding($oemCp)
        } catch {
            $OutputEncoding = $null
        }
    }

    # Сколько ждать дочитывания потоков после завершения процесса.
    # Ограничение нужно потому, что дочерние процессы утилиты могут удерживать
    # открытыми унаследованные каналы вывода уже после ее остановки.
    $streamWaitMs = 30000

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo.FileName = $FilePath
    $proc.StartInfo.Arguments = $Arguments
    $proc.StartInfo.UseShellExecute = $false
    $proc.StartInfo.RedirectStandardOutput = $true
    $proc.StartInfo.RedirectStandardError = $true
    $proc.StartInfo.CreateNoWindow = $true
    if ($null -ne $OutputEncoding) {
        $proc.StartInfo.StandardOutputEncoding = $OutputEncoding
        $proc.StartInfo.StandardErrorEncoding = $OutputEncoding
    }

    $timedOut = $false
    try {
        [void]$proc.Start()

        # Оба потока начинают читаться сразу и параллельно
        $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
        $stderrTask = $proc.StandardError.ReadToEndAsync()

        if ($TimeoutSeconds -gt 0) {
            if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
                $timedOut = $true

                # Завершаем все дерево процессов: утилиты Autodesk запускают дочерние
                # процессы, которые наследуют каналы вывода и после Kill() самой утилиты
                # продолжают удерживать их открытыми.
                try { & taskkill.exe /PID $proc.Id /T /F 2>&1 | Out-Null } catch { }
                try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }

                # После принудительного завершения ждем потоки недолго:
                # остатки вывода уже не имеют ценности
                $streamWaitMs = 5000
                [void]$proc.WaitForExit($streamWaitMs)
            }
        } else {
            $proc.WaitForExit()
        }

        # Ждем завершения чтения потоков, но не бесконечно
        try {
            [void][System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]@($stdoutTask, $stderrTask), $streamWaitMs)
        } catch {
            # Ошибку чтения потока не считаем фатальной: код возврата важнее
        }

        $stdout = if ($stdoutTask.Status -eq [System.Threading.Tasks.TaskStatus]::RanToCompletion) { $stdoutTask.Result } else { "" }
        $stderr = if ($stderrTask.Status -eq [System.Threading.Tasks.TaskStatus]::RanToCompletion) { $stderrTask.Result } else { "" }

        $exitCode = if ($timedOut) { -1 } elseif ($proc.HasExited) { $proc.ExitCode } else { -1 }

        return [pscustomobject]@{
            ExitCode = $exitCode
            StdOut   = $stdout.Trim()
            StdErr   = $stderr.Trim()
            TimedOut = $timedOut
        }
    }
    finally {
        $proc.Dispose()
    }
}

<#
.SYNOPSIS
    Приводит список шаблонов из config.json к безопасному виду.

.DESCRIPTION
    Отбрасывает $null и пустые строки. Пустой шаблон в фильтрах превращается
    в маску "**", которая совпадает с любым именем файла: для стоп-листа это
    означало бы отмену выгрузки вообще всех моделей, а для белого списка —
    копирование всего содержимого папки.
#>
function Get-CleanPatternList {
    param (
        $Patterns
    )

    if ($null -eq $Patterns) { return @() }

    return @($Patterns | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim() })
}
