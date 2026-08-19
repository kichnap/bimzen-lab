#####################################################################################################
# СВМ-ПРОЕКТ.BIM-Отдел                                                                              #
# Скрипт ConvertToNWC                                                                               #
# Версия: 1.4 (25.09.2025)                                                                          #
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
#$ErrorActionPreference = "Continue" # Продолжать выполнение при ошибках

Write-LogDivider -LogFile $logPath -Title "Начало работы $taskName"

# Получаем путь к расположению скрипта
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Читаем настройки из файла config.json по пути $configPath
$config = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
$navisworksToolPath = $config.NavisworksToolPath

# Создание списка файлов RVT
$rvtFiles = Get-ChildItem -Path $localRVTFolder -Filter *.rvt -File

if ($rvtFiles.Count -eq 0) {
    Write-Log -Message ($taskName + ": файлы моделей *.rvt не найдены в папке " + $localRVTFolder) -LogFile $logPath -Level ERROR
    exit 1 # Завершает скрипт с кодом ошибки
}

foreach ($rvt in $rvtFiles) {
    $rvtPath = $rvt.FullName
    $tempList = Join-Path $localNWCFolder "$($rvt.BaseName)_list.txt"
    Set-Content -Path $tempList -Value $rvtPath -Encoding UTF8

    $outNWD = Join-Path $localNWCFolder "$($rvt.BaseName).nwd"
    $logFileModel = Join-Path $localNWCFolder "$($rvt.BaseName)_log.txt"

    $arguments = "/i `"$tempList`" /of `"$outNWD`" /log `"$logFileModel`" /over"

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo.FileName = $navisworksToolPath
    $proc.StartInfo.Arguments = $arguments
    $proc.StartInfo.UseShellExecute = $false
    $proc.StartInfo.RedirectStandardOutput = $true
    $proc.StartInfo.RedirectStandardError = $true
    $proc.StartInfo.CreateNoWindow = $true

    Write-Log -Message "ConvertToNWC: запускаем конвертацию файла $($rvt.Name)." -LogFile $logPath

    $null = $proc.Start()
    $proc.WaitForExit()

    $stdout = $proc.StandardOutput.ReadToEnd()
    $stderr = $proc.StandardError.ReadToEnd()

    Write-Log -Message "ConvertToNWC: STDOUT: $stdout" -LogFile $logPath
    if ($stderr) {
        Write-Log -Message "ConvertToNWC: STDERR: $stderr" -LogFile $logPath -Level ERROR
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
    Remove-Item $tempList -Force
}

# Перемещаем все .nwc файлы
$nwcFiles = Get-ChildItem -Path $localRVTFolder -Filter *.nwc -File
foreach ($file in $nwcFiles) {
    $destination = Join-Path $localNWCFolder $file.Name
    Move-Item -Path $file.FullName -Destination $destination -Force
}
Write-Log -Message ($taskName + ": файлы *.nwc перемещены в папку " + $localNWCFolder) -LogFile $logPath

# --- ДОБАВЛЕНО: удаляем все .nwd из папки NWC ---
$nwdFiles = Get-ChildItem -Path $localNWCFolder -Filter *.nwd -File
if ($nwdFiles.Count -gt 0) {
    foreach ($file in $nwdFiles) {
        Remove-Item $file.FullName -Force
        Write-Log -Message "ConvertToNWC: удалён временный NWD $($file.Name)" -LogFile $logPath
    }
}

Write-LogDivider -LogFile $logPath -Title "Конец работы $taskName"