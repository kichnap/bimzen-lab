#########################################################################################################################
# СВМ-ПРОЕКТ.BIM-Отдел                                                                                                  #
# Скрипт log                                                                                                            #
# Версия: 1.0 (17.04.2025)                                                                                              #
# Только для сотрудников BIM-отдела.                                                                                    #
# Скрипт выполняет следующие функции:                                                                                   #
#   - ведения логов работы других скриптов                                                                              #
#                                                                                                                       #
# Скрипт является частью пайплайна DownloadAndCopyRVTNWC и не может работать отдельно!                                  #
#                                                                                                                       #
#########################################################################################################################

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
        $titleLine = "║ ---[ $Title ]" + ("-" * (71 - $Title.Length))+" ║"
        $output = @($linetop, $titleLine, $linebottom)
    } else {
        $output = @($line, $line)
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
        $titleLine = "│ --------[ $Title ]" + ("-" * (66 - $Title.Length))+" │"
        $output = @($linetop, $titleLine, $linebottom)
    } else {
        $output = @($line, $line)
    }

    $output | Out-File -FilePath $LogFile -Append -Encoding UTF8
    $output | ForEach-Object { Write-Host $_ -ForegroundColor DarkGray }
}
