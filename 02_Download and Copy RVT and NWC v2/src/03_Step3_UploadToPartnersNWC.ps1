#####################################################################################################
# ПАНЧИК.РФ                                                                                         #
# Скрипт UploadToPartnersNWC                                                                        #
# Версия: 2.0 (12.08.2026)                                                                          #
# Только для сотрудников BIM-отдела.                                                                #
# Скрипт выполняет следующие функции:                                                               #
#   - загрузка NWC файлов на файловый сервер партнеров                                              #
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

$taskName = "UploadToPartnersNWC"
$ErrorActionPreference = "Continue" # Продолжать выполнение при ошибках

Write-LogDivider -LogFile $logPath -Title "Начало работы $taskName"

# Читаем настройки из файла config.json по пути $configPath.
# Чтение обязательно ДО использования $config: раньше переменные заполнялись выше
# по тексту и работали только потому, что $config был случайно виден
# из области видимости оркестратора.
$config = Get-Content $configPath -Raw  -Encoding UTF8 | ConvertFrom-Json

$Extension = ".nwc"
$SourceFolder = $localNWCFolder
$TargetFolder = $config.PartnersNWCFolder
# Пустые шаблоны отбрасываются: маска "**" совпадает с любым именем файла
# и отменила бы выгрузку вообще всех моделей заказчику.
$StopPatterns = Get-CleanPatternList -Patterns $config.StopUploadModelsToPartners

# Проверка исходной папки
if ([string]::IsNullOrWhiteSpace($SourceFolder) -or -not (Test-Path $SourceFolder)) {
	Write-Log -Message ($taskName + ": не найдена исходная папка $SourceFolder") -LogFile $logPath -Level ERROR
	Write-LogDivider -LogFile $logPath -Title "Конец работы $taskName"
	return
}

# Проверка целевой папки
if ([string]::IsNullOrWhiteSpace($TargetFolder) -or -not (Test-Path $TargetFolder)) {
	Write-Log -Message ($taskName + ": не найдена целевая папка, проверьте путь $TargetFolder") -LogFile $logPath -Level ERROR
	Write-LogDivider -LogFile $logPath -Title "Конец работы $taskName"
	return
}

# Получаем файлы с нужным расширением
$files = @(Get-ChildItem -Path $SourceFolder -Filter "*$Extension" -File | Where-Object {
    $name = $_.Name
    foreach ($stop in $StopPatterns) {
        if ($name -like "*$stop*") {
            Write-Log -Message ($taskName + ": исключён файл $name (совпадает с шаблоном исключения '$stop')") -LogFile $logPath -Level WARN
            return $false
        }
    }
    return $true
})

$total = $files.Count
$success = 0
$failed = 0

# Копирование
foreach ($file in $files) {
    # Получаем имя без расширения и новое имя с суффиксом (если нужно)
    $nameWithoutExt = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
    $newFileName = if ($config.SuffixNWCPartners) { "$nameWithoutExt$($config.SuffixNWCPartners)$Extension" } else { $file.Name }
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
if ($failed -gt 0) {
    Write-Log -Message ($taskName + ": не скопировано $failed файлов") -LogFile $logPath -Level WARN
}

Write-LogDivider -LogFile $logPath -Title "Конец работы $taskName"
