#####################################################################################################
# СВМ-ПРОЕКТ.BIM-Отдел                                                                              #
# Скрипт SyncFromPartnersNWC                                                                        #
# Версия: 1.1 (28.05.2025)                                                                          #
# Только для сотрудников BIM-отдела.                                                                #
# Скрипт выполняет следующие функции:                                                               #
#   - скачивание NWC файлов с файлового сервера партнеров на рабочий сервер                         #
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

$taskName = "SyncFromPartnersNWC"
$ErrorActionPreference = "Continue" # Продолжать выполнение при ошибках
$Extension = ".nwc"
$SourceFolder = $config.PartnersNWCFolder
$TargetFolder = $config.FileServerNWCFolder
$NamePatterns = $config.ModelsNWCFromPartners

Write-LogDivider -LogFile $logPath -Title "Начало работы $taskName"

# Получаем путь к расположению скрипта
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Читаем настройки из файла config.json по пути $configPath
$config = Get-Content $configPath -Raw  -Encoding UTF8 | ConvertFrom-Json

# Проверка исходной папки
if (-not (Test-Path $SourceFolder)) {
	Write-Log -Message ($taskName + ": не найдена исходная папка, проверьте путь $SourceFolder") -LogFile $logPath -Level ERROR
    exit 1
}

# Проверка целевой папки
if (-not (Test-Path $TargetFolder)) {
	Write-Log -Message ($taskName + ": не найдена целевая папка, проверьте путь $TargetFolder") -LogFile $logPath -Level ERROR
    exit 1
}

# Поиск файлов с нужным расширением и совпадением по шаблону
$files = Get-ChildItem -Path $SourceFolder -Filter "*$Extension" -File | Where-Object {
    foreach ($pattern in $NamePatterns) {
        if ($_.Name -like "*$pattern*") { return $true }
    }
    return $false
}

$total = $files.Count
$success = 0
$failed = 0

# Копирование файлов
foreach ($file in $files) {
    $targetPath = Join-Path $TargetFolder $file.Name

    try {
        Copy-Item $file.FullName -Destination $targetPath -Force
		Write-Log -Message ($taskName + ": успешно скопирован файл $($file.Name)") -LogFile $logPath
        $success++
    } catch {
		Write-Log -Message ($taskName + ": ошибка при копировании файла $($file.Name) : $($_.Exception.Message)") -LogFile $logPath -Level ERROR
        $failed++
    }
}

# Статистика
Write-Log -Message ($taskName + ": === Результат ===") -LogFile $logPath
Write-Log -Message ($taskName + ": всего найдено $total файлов") -LogFile $logPath
Write-Log -Message ($taskName + ": успешно скопировано $success файлов") -LogFile $logPath
Write-Log -Message ($taskName + ": не скопировано $failed файлов") -LogFile $logPath -Level WARN

Write-LogDivider -LogFile $logPath -Title "Конец работы $taskName"