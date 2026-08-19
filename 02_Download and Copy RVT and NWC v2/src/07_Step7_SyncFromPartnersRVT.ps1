#####################################################################################################
# ПАНЧИК.РФ                                                                                         #
# Скрипт SyncFromPartnersRVT                                                                        #
# Версия: 2.0 (12.08.2026)                                                                          #
# Только для сотрудников BIM-отдела.                                                                #
# Скрипт выполняет следующие функции:                                                               #
#   - скачивание RVT моделей с файлового сервера партнеров на рабочий сервер                        #
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

$taskName = "SyncFromPartnersRVT"
$ErrorActionPreference = "Continue" # Продолжать выполнение при ошибках

Write-LogDivider -LogFile $logPath -Title "Начало работы $taskName"

# Читаем настройки из файла config.json по пути $configPath.
# Чтение обязательно ДО использования $config: раньше переменные заполнялись выше
# по тексту и работали только потому, что $config был случайно виден
# из области видимости оркестратора.
$config = Get-Content $configPath -Raw  -Encoding UTF8 | ConvertFrom-Json

$Extension = ".rvt"
$SourceFolder = $config.PartnersRevitFolder
$TargetFolder = $config.FileServerRevitFolder
# Пустые шаблоны отбрасываются: маска "**" совпадает с любым именем файла,
# и вместо моделей смежников скопировалось бы все содержимое папки заказчика.
$NamePatterns = Get-CleanPatternList -Patterns $config.ModelsRVTFromPartners

# Проверка исходной папки
if ([string]::IsNullOrWhiteSpace($SourceFolder) -or -not (Test-Path $SourceFolder)) {
	Write-Log -Message ($taskName + ": не найдена исходная папка, проверьте путь $SourceFolder") -LogFile $logPath -Level ERROR
	Write-LogDivider -LogFile $logPath -Title "Конец работы $taskName"
	return
}

# Проверка целевой папки
if ([string]::IsNullOrWhiteSpace($TargetFolder) -or -not (Test-Path $TargetFolder)) {
	Write-Log -Message ($taskName + ": не найдена целевая папка, проверьте путь $TargetFolder") -LogFile $logPath -Level ERROR
	Write-LogDivider -LogFile $logPath -Title "Конец работы $taskName"
	return
}

# Без списка шаблонов копировать нечего: пустой список означает,
# что модели смежников для скачивания не заданы.
if ($NamePatterns.Count -eq 0) {
	Write-Log -Message ($taskName + ": не задан список моделей смежников (ModelsRVTFromPartners), копирование пропущено") -LogFile $logPath -Level WARN
	Write-LogDivider -LogFile $logPath -Title "Конец работы $taskName"
	return
}

# Ищем модели с нужным расширением и совпадением по шаблону
$files = @(Get-ChildItem -Path $SourceFolder -Filter "*$Extension" -File | Where-Object {
    foreach ($pattern in $NamePatterns) {
        if ($_.Name -like "*$pattern*") { return $true }
    }
    return $false
})

$total = $files.Count
$success = 0
$failed = 0

# Копирование моделей
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
if ($failed -gt 0) {
    Write-Log -Message ($taskName + ": не скопировано $failed файлов") -LogFile $logPath -Level WARN
}

Write-LogDivider -LogFile $logPath -Title "Конец работы $taskName"
