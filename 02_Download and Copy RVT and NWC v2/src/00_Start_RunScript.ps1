##########################################################################################################################################
# ПАНЧИК.РФ                                                                                                                              #
# Пайплайн DownloadAndCopyRVTNWC                                                                                                         #
# Версия: 2.3 (13.08.2026)                                                                                                               #
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
#   - common.ps1                            - общие функции: запуск внешних утилит, нормализация фильтров                                #
#   - vpn.ps1                               - подключение и отключение VPN (SoftEther или встроенный VPN Windows)                        #
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

# Версия пайплайна задается в одном месте и подставляется в заголовок лога
$pipelineVersion = "2.3 (13.08.2026)"

# Папка логов создается рядом со скриптом, а не в текущем рабочем каталоге:
# при запуске из Планировщика заданий рабочим каталогом является C:\Windows\System32
$logDir = Join-Path -Path $scriptDir -ChildPath "Logs"
$taskName = "DownloadAndCopyRVTNWC"
$taskNameFull = "DownloadAndCopyRVTNWC v$pipelineVersion"
$timestamp = Get-Date -Format "yyyy-MM-dd_HH-mm"
$logPath = Join-Path -Path $logDir -ChildPath "$taskName`_$timestamp.log"

if (-not (Test-Path $logDir)) {
    New-Item -Path $logDir -ItemType Directory | Out-Null
}

# Логирование
. $scriptDir\log.ps1
# Общие функции: запуск внешних утилит, нормализация фильтров
. $scriptDir\common.ps1
# Подключение и отключение VPN
. $scriptDir\vpn.ps1

# Счетчик ошибок пайплайна. Увеличивается внутри Write-Log при уровне ERROR
# и используется для формирования итогового статуса и кода возврата.
Reset-LogErrorCount

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
    $localRVTFolder = Join-Path -Path $scriptDir -ChildPath "RVT"
}
if (-not (Test-Path $localRVTFolder)) {
    New-Item -Path $localRVTFolder -ItemType Directory | Out-Null
}
Write-Log -Message ($taskName + ": папка для скачанных с RevitServer RVT-файлов $localRVTFolder") -LogFile $logPath

# Проверка на существование папки для NWC и создание, если она не существует
if ([string]::IsNullOrWhiteSpace($localNWCFolder)) {
    $localNWCFolder = Join-Path -Path $scriptDir -ChildPath "NWC"
}
if (-not (Test-Path $localNWCFolder)) {
    New-Item -Path $localNWCFolder -ItemType Directory | Out-Null
}
Write-Log -Message ($taskName + ": папка для сконвертированных NWC-файлов $localNWCFolder") -LogFile $logPath

# Функции скрипта. При необходимости можно удалить лишние пункты
$steps = $config.SelectedScripts

$failedSteps = @()

# VPN. Если включен в настройках, поднимается до шагов и разрывается после них.
# Файловые серверы заказчика и RevitServer доступны только через VPN, поэтому
# при неудачном подключении шаги не запускаются — иначе пайплайн отработал бы
# вхолостую и завалил лог ошибками доступа к папкам.
$vpnSettings = Get-VpnSettings -Config $config
$vpnConnected = $false
$vpnWasAlreadyConnected = $false

if ($vpnSettings.Enabled) {
    Write-LogDivider -LogFile $logPath -Title "Подключение VPN"
    $vpnResult = Connect-PipelineVpn -Settings $vpnSettings -LogFile $logPath
    $vpnConnected = $vpnResult.Success
    $vpnWasAlreadyConnected = $vpnResult.AlreadyConnected
} else {
    Write-Log -Message ($taskName + ": VPN отключен в настройках, подключение не выполняется") -LogFile $logPath
}

try {
    if ($vpnSettings.Enabled -and -not $vpnConnected) {
        Write-Log -Message ($taskName + ": шаги пайплайна пропущены, так как не удалось подключить VPN") -LogFile $logPath -Level ERROR
    } else {
        foreach ($step in $steps) {
            $stepPath = Join-Path -Path $scriptDir -ChildPath $step
            if (Test-Path $stepPath) {
                Write-Log -Message ($taskName + ": запуск скрипта $step") -LogFile $logPath
                $errorsBefore = Get-LogErrorCount

                # Шаг выполняется в отдельной области видимости. Терминирующая ошибка внутри шага
                # не должна обрывать весь пайплайн: перехватываем ее и переходим к следующему шагу.
                try {
                    & "$stepPath" -configPath $configPath -logPath $logPath -localRVTFolder $localRVTFolder -localNWCFolder $localNWCFolder
                } catch {
                    Write-Log -Message ($taskName + ": скрипт $step прерван необработанной ошибкой.`nТип: $($_.Exception.GetType().Name)`nСообщение: $($_.Exception.Message)") -LogFile $logPath -Level ERROR
                }

                # Шаг, запущенный через "&", не может вернуть код возврата в оркестратор,
                # поэтому статус определяем по количеству записей уровня ERROR, добавленных шагом.
                $stepErrors = (Get-LogErrorCount) - $errorsBefore
                if ($stepErrors -gt 0) {
                    $failedSteps += $step
                    Write-Log -Message ($taskName + ": завершен скрипт $step, ошибок: $stepErrors") -LogFile $logPath -Level WARN
                } else {
                    Write-Log -Message ($taskName + ": завершен скрипт $step без ошибок") -LogFile $logPath
                }
            } else {
                Write-Log -Message ($taskName + ": скрипт $step не найден") -LogFile $logPath -Level WARN
            }
        }
    }
}
finally {
    # VPN разрывается даже если пайплайн прерван ошибкой, иначе на рабочей станции
    # остается поднятое соединение с сетью заказчика
    if ($vpnSettings.Enabled -and $vpnConnected) {
        Write-LogDivider -LogFile $logPath -Title "Отключение VPN"
        Disconnect-PipelineVpn -Settings $vpnSettings -LogFile $logPath -AlreadyConnected $vpnWasAlreadyConnected
    }
}

# Итоговый статус пайплайна
$totalErrors = Get-LogErrorCount
if ($totalErrors -eq 0) {
    Write-Log -Message ($taskName + ": пайплайн DownloadAndCopyRVTNWC завершен успешно") -LogFile $logPath
    $exitCode = 0
} else {
    $problems = if ($failedSteps.Count -gt 0) { "Проблемные шаги: $($failedSteps -join ', ')" } else { "Ошибки вне шагов, подробности выше в логе" }
    Write-Log -Message ($taskName + ": пайплайн DownloadAndCopyRVTNWC завершен с ошибками ($totalErrors). $problems") -LogFile $logPath -Level ERROR
    $exitCode = 1
}

Write-LogTop -LogFile $logPath -Title "Конец работы $taskNameFull"

# Код возврата виден Планировщику заданий и системам мониторинга
exit $exitCode