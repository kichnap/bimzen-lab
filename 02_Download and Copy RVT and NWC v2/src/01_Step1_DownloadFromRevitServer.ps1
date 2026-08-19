#####################################################################################################
# ПАНЧИК.РФ                                                                                         #
# Скрипт DownloadFromRevitServer                                                                    #
# Версия: 2.0 (12.08.2026)                                                                          #
# Только для сотрудников BIM-отдела.                                                                #
# Скрипт выполняет следующие функции:                                                               #
#   - скачивание моделей с RevitServer с помощью утилиты RevitServerTool.exe                        #
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

$taskName = "DownloadFromRevitServer"
$ErrorActionPreference = "Continue" # Продолжать выполнение при ошибках

Write-LogDivider -LogFile $logPath -Title "Начало работы $taskName"

# Получаем путь к расположению скрипта
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Читаем настройки из файла config.json по пути $configPath
$config = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
$revitServerName = $config.RevitServerName
$revitServerToolPath = $config.RevitServerToolPath

# Читаем пути к моделя на RevitServer из файла config.json по пути $configPath
$revitModelsPath = $config.RevitModelsPath

# Проверяем доступность RevitServer'а
# Скрипт сообщает о недоступности сервера, но все равно пытается скачать, т.к. 
# если сервер находится под защитой не всегда получается проверить его доступность
if (Test-Connection -ComputerName $revitServerName -Count 1 -Quiet) {
    Write-Log -Message ($taskName + ": RevitServer по адресу $revitServerName доступен") -LogFile $logPath
} else {
    Write-Log -Message ($taskName + ": RevitServer по адресу $revitServerName недоступен") -LogFile $logPath -Level ERROR 
	#Write-LogDivider -LogFile $logPath -Title "Конец работы $taskName"
	#return
}

# Максимальное время выгрузки одной модели. Защищает пайплайн от зависания,
# если RevitServerTool перестал отвечать. Задается константой, а не в config.json:
# Settings.html пересобирает конфигурацию из полей формы и удалил бы незнакомый ключ.
$downloadTimeoutSeconds = 60 * 60

# Счетчики для статистики
$totalModels = @($revitModelsPath).Count
$processedModels = 0
$failedDownloads = 0

Write-Log -Message ($taskName + ": Обработка $totalModels -х указанных моделей с RevitServer...") -LogFile $logPath

# Проверка, что утилита RevitServerTool существует
if (-not (Test-Path $revitServerToolPath)) {
    Write-Log -Message ($taskName + ": Утилита RevitServerTool не найдена по указанному пути: $revitServerToolPath") -LogFile $logPath -Level ERROR
    return
}

# Функция для скачивания моделей с RevitServer отлавливанием ошибок и выводом в лог.
# Возвращает $true при успешной загрузке и $false при ошибке — счетчики ведет вызывающий цикл.
function Invoke-RevitServerTool {
    param (
        [string]$toolPath,
        [string]$modelPath,
        [string]$serverName,
        [string]$destinationPath,
        [string]$modelName,
        [string]$logPath,
        [int]$timeoutSeconds
    )

    $arguments = "createLocalRVT `"$modelPath`" -server $serverName -destination `"$destinationPath`" -overwrite"

    try {
        $result = Invoke-ExternalProcess -FilePath $toolPath -Arguments $arguments -TimeoutSeconds $timeoutSeconds

        $output = "$($result.StdOut)`n$($result.StdErr)".Trim()
        if ($output) {
            Write-Log -Message $output -LogFile $logPath
        }

        if ($result.TimedOut) {
            Write-Log -Message ($taskName + ": превышено время загрузки модели $modelName ($timeoutSeconds с), процесс принудительно завершен") -LogFile $logPath -Level ERROR
            return $false
        }

        # Успех определяется кодом возврата утилиты; поиск "ERROR:" в тексте оставлен
        # как дополнительная проверка на случай, если утилита вернет 0 при ошибке.
        if ($result.ExitCode -ne 0 -or $output -match "ERROR:") {
            Write-Log -Message ($taskName + ": ошибка при загрузке модели $modelName (код возврата $($result.ExitCode))") -LogFile $logPath -Level ERROR
            return $false
        }

        Write-Log -Message ($taskName + ": модель $modelName успешно загружена") -LogFile $logPath
        return $true
    }
    catch {
        Write-Log -Message "Исключение при запуске утилиты для модели ${modelName}:`nТип: $($_.Exception.GetType().Name)`nСообщение: $($_.Exception.Message)" -LogFile $logPath -Level ERROR
        return $false
    }
}

# Загрузка моделей в цикле
foreach ($modelPath in $revitModelsPath) {
    $modelName = [System.IO.Path]::GetFileName($modelPath)
    $modelLocalPath = Join-Path -Path $localRVTFolder -ChildPath $modelName

    Write-Log -Message ($taskName + ": Подготовка к загрузке модели: $modelPath")-LogFile $logPath

    $downloaded = Invoke-RevitServerTool -toolPath $revitServerToolPath `
                           -modelPath $modelPath `
                           -serverName $revitServerName `
                           -destinationPath $modelLocalPath `
                           -modelName $modelName `
                           -logPath $logPath `
                           -timeoutSeconds $downloadTimeoutSeconds

    if ($downloaded -eq $true) { $processedModels++ } else { $failedDownloads++ }
}

Write-Log -Message ($taskName + ": всего указано $totalModels моделей для обработки") -LogFile $logPath
if ($processedModels -eq 0) {
    Write-Log -Message ($taskName + ": модели не загружены") -LogFile $logPath -Level WARN
} else {
    Write-Log -Message ($taskName + ": успешно загружено $processedModels моделей") -LogFile $logPath
}
if ($failedDownloads -gt 0) {
    Write-Log -Message ($taskName + ": незагружено $failedDownloads моделей") -LogFile $logPath -Level WARN
} 

Write-LogDivider -LogFile $logPath -Title "Конец работы $taskName"