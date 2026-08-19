#####################################################################################################
# СВМ-ПРОЕКТ.BIM-Отдел                                                                              #
# Скрипт UploadToPartnersRVT                                                                        #
# Версия: 1.3 (12.07.2025)                                                                          #
# Только для сотрудников BIM-отдела.                                                                #
# Скрипт выполняет следующие функции:                                                               #
#   - загрузка RVT моделей на файловый сервер партнеров                                             #
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

$taskName = "UploadToPartnersRVT"
$ErrorActionPreference = "Continue" # Продолжать выполнение при ошибках
$Extension = ".rvt"
$SourceFolder = $localRVTFolder
$TargetFolder = $config.PartnersRevitFolder
$stopPatterns = $config.StopUploadModelsToPartners

Write-LogDivider -LogFile $logPath -Title "Начало работы $taskName"

# Получаем путь к расположению скрипта
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Читаем настройки из файла config.json по пути $configPath
$config = Get-Content $configPath -Raw  -Encoding UTF8 | ConvertFrom-Json

# Проверка исходной папки
if (-not (Test-Path $SourceFolder)) {
	Write-Log -Message ($taskName + ": не найдена исходная папка $SourceFolder") -LogFile $logPath -Level ERROR
    exit 1
}

# Проверка целевой папки
if (-not (Test-Path $TargetFolder)) {
	Write-Log -Message ($taskName + ": не найдена целевая папка, проверьте путь $TargetFolder") -LogFile $logPath -Level ERROR
    exit 1
}

# Получаем файлы с нужным расширением
$files = Get-ChildItem -Path $SourceFolder -Filter "*$Extension" -File | Where-Object {
    $name = $_.Name
    foreach ($stop in $stopPatterns) {
        if ($name -like "*$stop*") {
            Write-Log -Message ($taskName + ": исключён файл $name (совпадает с шаблоном исключения '$stop')") -LogFile $logPath -Level WARN
            return $false
        }
    }
    return $true
}

$total = $files.Count
$success = 0
$failed = 0

# Копирование
foreach ($file in $files) {
    # Получаем имя без расширения и новое имя с суффиксом (если нужно)
    $nameWithoutExt = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
    $newFileName = if ($config.SuffixRVTPartners) { "$nameWithoutExt$($config.SuffixRVTPartners)$Extension" } else { $file.Name }
    $targetPath = Join-Path $TargetFolder $newFileName

    try {
		Write-Log -Message ($taskName + ": копируем $($file.Name) >> $newFileName") -LogFile $logPath        
		Copy-Item $file.FullName -Destination $targetPath -Force
		Write-Log -Message ($taskName + ": успешно скопирован файл $($file.Name) >>> $newFileName") -LogFile $logPath
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
