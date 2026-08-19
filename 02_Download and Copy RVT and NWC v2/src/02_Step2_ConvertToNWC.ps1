#####################################################################################################
# ПАНЧИК.РФ                                                                                         #
# Скрипт ConvertToNWC                                                                               #
# Версия: 2.0 (12.08.2026)                                                                          #
# Только для сотрудников BIM-отдела.                                                                #
# Скрипт выполняет следующие функции:                                                               #
#   - конвертирование моделей из RVT формата в NWC с помощью утилиты FiletoolsTaskRunner.exe        #
#                                                                                                   #
# Скрипт является частью пайплайна DownloadAndCopyRVTNWC и не может работать отдельно!              #
#                                                                                                   #
#####################################################################################################

param(
    [string]$configPath,
    [string]$logPath,
    [string]$localRVTFolder,
    [string]$localNWCFolder
)

$taskName = "ConvertToNWC"
$ErrorActionPreference = "Continue" # Продолжать выполнение при ошибках

Write-LogDivider -LogFile $logPath -Title "Начало работы $taskName"

# Получаем путь к расположению скрипта
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Читаем настройки из файла config.json по пути $configPath
$config = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
$navisworksToolPath = $config.NavisworksToolPath

# Максимальное время конвертации одной модели. Защищает пайплайн от зависания,
# если FiletoolsTaskRunner перестал отвечать. Задается константой, а не в config.json:
# Settings.html пересобирает конфигурацию из полей формы и удалил бы незнакомый ключ.
$convertTimeoutSeconds = 2 * 60 * 60

# Создание списка файлов RVT
$rvtFiles = @(Get-ChildItem -Path $localRVTFolder -Filter *.rvt -File)

$converted = 0
$failed = 0

if ($rvtFiles.Count -eq 0) {
    Write-Log -Message ($taskName + ": файлы моделей *.rvt не найдены в папке " + $localRVTFolder) -LogFile $logPath -Level ERROR
    Write-LogDivider -LogFile $logPath -Title "Конец работы $taskName"
    return # Прерываем шаг; статус пайплайна определит оркестратор по записи ERROR
}

foreach ($rvt in $rvtFiles) {
    $rvtPath = $rvt.FullName
    $tempList = Join-Path $localNWCFolder "$($rvt.BaseName)_list.txt"
    Set-Content -Path $tempList -Value $rvtPath -Encoding UTF8

    $outNWD = Join-Path $localNWCFolder "$($rvt.BaseName).nwd"
    $logFileModel = Join-Path $localNWCFolder "$($rvt.BaseName)_log.txt"

    $arguments = "/i `"$tempList`" /of `"$outNWD`" /log `"$logFileModel`" /over"

    Write-Log -Message "ConvertToNWC: запускаем конвертацию файла $($rvt.Name)." -LogFile $logPath

    # Потоки вывода читаются асинхронно внутри Invoke-ExternalProcess: чтение после
    # WaitForExit приводило к вечному зависанию на выводе больше буфера канала.
    $result = Invoke-ExternalProcess -FilePath $navisworksToolPath -Arguments $arguments -TimeoutSeconds $convertTimeoutSeconds

    if ($result.StdOut) {
        Write-Log -Message "ConvertToNWC: STDOUT: $($result.StdOut)" -LogFile $logPath
    }
    if ($result.StdErr) {
        Write-Log -Message "ConvertToNWC: STDERR: $($result.StdErr)" -LogFile $logPath -Level ERROR
    }
    if ($result.TimedOut) {
        Write-Log -Message "ConvertToNWC: превышено время конвертации файла $($rvt.Name) ($convertTimeoutSeconds с), процесс принудительно завершен" -LogFile $logPath -Level ERROR
        $failed++
    } elseif ($result.ExitCode -ne 0) {
        Write-Log -Message "ConvertToNWC: конвертация файла $($rvt.Name) завершилась с кодом возврата $($result.ExitCode)" -LogFile $logPath -Level ERROR
        $failed++
    } else {
        $converted++
    }

    # Читаем модельный лог и пишем в общий лог
    if (Test-Path $logFileModel) {
        $logContent = Get-Content $logFileModel -Raw -Encoding UTF8
        if ($logContent.Trim()) {
            # Если в лог-файле есть ERROR/Ошибка — пометим как ERROR, иначе INFO
            if ($logContent -match "(ERROR|Ошибка)") {
                Write-Log -Message "ConvertToNWC: LOGFILE ($($rvt.BaseName)):`n$logContent" -LogFile $logPath -Level ERROR
            }
            else {
                Write-Log -Message "ConvertToNWC: LOGFILE ($($rvt.BaseName)):`n$logContent" -LogFile $logPath
            }
        }
    }

    # Удаляем временный список
    Remove-Item $tempList -Force -ErrorAction SilentlyContinue

    # Удаляем адресно только служебный NWD, созданный для этой модели.
    # Раньше в конце шага удалялись ВСЕ *.nwd из папки NWC — если бы в ней
    # оказалась сводная модель, она была бы уничтожена.
    if (Test-Path $outNWD) {
        Remove-Item $outNWD -Force -ErrorAction SilentlyContinue
        Write-Log -Message "ConvertToNWC: удалён временный NWD $([System.IO.Path]::GetFileName($outNWD))" -LogFile $logPath
    }
}

# Перемещаем все .nwc файлы
$nwcFiles = Get-ChildItem -Path $localRVTFolder -Filter *.nwc -File
foreach ($file in $nwcFiles) {
    $destination = Join-Path $localNWCFolder $file.Name
    Move-Item -Path $file.FullName -Destination $destination -Force
}
Write-Log -Message ($taskName + ": файлы *.nwc перемещены в папку " + $localNWCFolder) -LogFile $logPath

# Итоги
Write-Log -Message ($taskName + ": === Результаты ===") -LogFile $logPath
Write-Log -Message ($taskName + ": всего моделей для конвертации $($rvtFiles.Count)") -LogFile $logPath
Write-Log -Message ($taskName + ": успешно сконвертировано $converted") -LogFile $logPath
if ($failed -gt 0) {
    Write-Log -Message ($taskName + ": не сконвертировано $failed") -LogFile $logPath -Level WARN
}

Write-LogDivider -LogFile $logPath -Title "Конец работы $taskName"