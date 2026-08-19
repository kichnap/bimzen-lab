#########################################################################################################################
# ПАНЧИК.РФ                                                                                                             #
# Скрипт log                                                                                                            #
# Версия: 2.0 (12.08.2026)                                                                                              #
# Только для сотрудников BIM-отдела.                                                                                    #
# Скрипт выполняет следующие функции:                                                                                   #
#   - ведения логов работы других скриптов                                                                              #
#                                                                                                                       #
# Скрипт является частью пайплайна DownloadAndCopyRVTNWC и не может работать отдельно!                                  #
#                                                                                                                       #
#########################################################################################################################

# Общий счетчик ошибок. Заполняется в Write-Log и читается оркестратором,
# чтобы определить статус шага и всего пайплайна (шаг, запущенный через "&",
# не может вернуть код возврата вызывающему скрипту).
$Global:LogErrorCount = 0

function Reset-LogErrorCount {
    $Global:LogErrorCount = 0
}

function Get-LogErrorCount {
    return $Global:LogErrorCount
}

function Write-Log {
    param (
        [string]$Message,
        [string]$LogFile,
        [ValidateSet("INFO", "WARN", "ERROR")][string]$Level = "INFO"
    )

    $timestamp = Get-Date -Format 'u'
    $logLine = "[$timestamp][$Level] $Message"
    $logLine | Out-File -FilePath $LogFile -Append -Encoding UTF8

    if ($Level -eq "ERROR") {
        $Global:LogErrorCount++
        Write-Host $logLine -ForegroundColor Red
    } elseif ($Level -eq "WARN") {
        Write-Host $logLine -ForegroundColor Yellow
    } else {
        Write-Host $logLine
    }
}

function Write-LogTop {
    param (
        [string]$LogFile,
        [string]$Title = ""
    )
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $linetop = "╔"+"═" * 80+"╗"
	$linebottom = "╚"+"═" * 80+"╝"
    if ($Title -ne "") {
        # Ширина заполнителя не может быть отрицательной для длинного заголовка
        $padding = [Math]::Max(0, 71 - $Title.Length)
        $titleLine = "║ ---[ $Title ]" + ("-" * $padding)+" ║"
        $output = @($linetop, $titleLine, $linebottom)
    } else {
        $output = @($linetop, $linebottom)
    }

    $output | Out-File -FilePath $LogFile -Append -Encoding UTF8
    $output | ForEach-Object { Write-Host $_ -ForegroundColor DarkGray }
}

function Write-LogDivider {
    param (
        [string]$LogFile,
        [string]$Title = ""
    )
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $linetop = "┌"+"─" * 80+"┐"
	$linebottom = "└"+"─" * 80+"┘"
    if ($Title -ne "") {
        # Ширина заполнителя не может быть отрицательной для длинного заголовка
        $padding = [Math]::Max(0, 66 - $Title.Length)
        $titleLine = "│ --------[ $Title ]" + ("-" * $padding)+" │"
        $output = @($linetop, $titleLine, $linebottom)
    } else {
        $output = @($linetop, $linebottom)
    }

    $output | Out-File -FilePath $LogFile -Append -Encoding UTF8
    $output | ForEach-Object { Write-Host $_ -ForegroundColor DarkGray }
}
