##########################################################################################################################################
# СВМ-ПРОЕКТ.BIM-Отдел                                                                                                                   #
# Пайплайн DownloadAndCopyRVTNWC                                                                                                         #
# Версия: 1.3 (12.07.2025)                                                                                                               #
# Только для сотрудников BIM-отдела.                                                                                                     #
# Скрипт выполняет следующие функции:                                                                                                    #
#   - 01_Step1_DownloadFromRevitServer.ps1  - скачивание моделей с RevitServer с помощью утилиты RevitServerTool.exe                     #
#   - 02_Step2_ConvertToNWC.ps1             - конвертирование моделей из RVT формата в NWC с помощью утилиты FiletoolsTaskRunner.exe     #
#   - 03_Step3_UploadToPartnersNWC.ps1      - загрузка NWC файлов на файловый сервер партнеров                                           #
#   - 04_Step4_UploadToPartnersRVT.ps1      - загрузка RVT моделей на файловый сервер партнеров                                          #
#   - 05_Step5_UploadToFileServerNWC.ps1      - загрузка NWC файлов на файловый сервер                                                   #
#   - 06_Step6_SyncFromPartnersNWC.ps1      - скачивание NWC файлов с файлового сервера партнеров на рабочий сервер                      #
#   - 07_Step7_SyncFromPartnersRVT.ps1      - скачивание RVT моделей с файлового сервера партнеров на рабочий сервер                     #
#   - log.ps1                               - модуль логирования                                                                         #
#                                                                                                                                        #
# Для автоматизации запуска можно добавить данный скрипт в планировщик задач.                                                            #
#                                                                                                                                        #
# Настройки находятся в отдельном файле config.json в одной папке со скриптом.                                                           #
#                                                                                                                                        #
# Подробное описание работы смотри в файле Инструкция.pdf                                                                                #
#                                                                                                                                        #
##########################################################################################################################################

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$configPath = Join-Path $scriptDir "config.json"

$logDir = ".\Logs"
$taskName = "DownloadAndCopyRVTNWC"
$taskNameFull = "DownloadAndCopyRVTNWC v1.1 (28.05.2025)"
$timestamp = Get-Date -Format "yyyy-MM-dd_HH-mm"
$logPath = Join-Path -Path $logDir -ChildPath "$taskName`_$timestamp.log"

if (-not (Test-Path $logDir)) {
    New-Item -Path $logDir -ItemType Directory | Out-Null
}

# Логирование
. $scriptDir\log.ps1

Write-LogTop -LogFile $logPath -Title "Начало работы $taskNameFull"

Write-Log -Message ($taskName + ": пайплайн DownloadAndCopyRVTNWC начал работу") -LogFile $logPath

#Чтение настроек из JSON-файла config.json с выводом ошибки в лог
try {
    #$config = Get-Content $configPath -Raw | ConvertFrom-Json
	$config = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
} catch {
    $errorMessage = "Ошибка при чтении или преобразовании JSON файла: $configPath. Ошибка: `nТип: $($_.Exception.GetType().Name)`nСообщение: $($_.Exception.Message)"
    Write-Log -Message $errorMessage -LogFile $logPath -Level ERROR
    exit 1  # Останавливаем выполнение скрипта
}

$localRVTFolder = $config.LocalRVTFolder
$localNWCFolder = $config.LocalNWCFolder

# Проверка на существование папки для RVT и создание, если она не существует
if ([string]::IsNullOrWhiteSpace($localRVTFolder)) {
    $localRVTFolder = Join-Path -Path (Split-Path -Parent $MyInvocation.MyCommand.Path) -ChildPath "RVT"
}
if (-not (Test-Path $localRVTFolder)) {
    New-Item -Path $localRVTFolder -ItemType Directory | Out-Null
}
Write-Log -Message ($taskName + ": папка для скачанных с RevitServer RVT-файлов $localRVTFolder") -LogFile $logPath

# Проверка на существование папки для NWC и создание, если она не существует
if ([string]::IsNullOrWhiteSpace($localNWCFolder)) {
    $localNWCFolder = Join-Path -Path (Split-Path -Parent $MyInvocation.MyCommand.Path) -ChildPath "NWC"
}
if (-not (Test-Path $localNWCFolder)) {
    New-Item -Path $localNWCFolder -ItemType Directory | Out-Null
}
Write-Log -Message ($taskName + ": папка для сконвертированных NWC-файлов $localNWCFolder") -LogFile $logPath

# Функции скрипта. При необходимости можно удалить лишние пункты
$steps = $config.SelectedScripts

foreach ($step in $steps) {
    $stepPath = Join-Path -Path $scriptDir -ChildPath $step
    if (Test-Path $stepPath) {
        Write-Log -Message ($taskName + ": запуск скрипта $step") -LogFile $logPath
        & "$stepPath" -configPath $configPath -logPath $logPath -localRVTFolder $localRVTFolder -localNWCFolder $localNWCFolder
		Write-Log -Message ($taskName + ": завершен скрипт $step") -LogFile $logPath
    } else {
        Write-Log -Message ($taskName + ": скрипт $step не найден") -LogFile $logPath -Level WARN 
    }
}

Write-Log -Message ($taskName + ": пайплайн DownloadAndCopyRVTNWC завершен") -LogFile $logPath

Write-LogTop -LogFile $logPath -Title "Конец работы $taskNameFull"