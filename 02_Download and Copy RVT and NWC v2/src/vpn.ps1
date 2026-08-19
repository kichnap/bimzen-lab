#########################################################################################################################
# ПАНЧИК.РФ                                                                                                             #
# Скрипт vpn                                                                                                            #
# Версия: 2.1 (12.08.2026)                                                                                              #
# Только для сотрудников BIM-отдела.                                                                                    #
# Скрипт выполняет следующие функции:                                                                                   #
#   - подключение и отключение VPN перед работой пайплайна                                                              #
#   - поддержка SoftEther VPN Client (vpncmd.exe) и встроенного VPN Windows (rasdial.exe)                               #
#                                                                                                                       #
# Скрипт является частью пайплайна DownloadAndCopyRVTNWC и не может работать отдельно!                                  #
#                                                                                                                       #
# ВАЖНО: пароли в config.json не хранятся. Если у SoftEther VPN Client задан пароль                                     #
# управления, имя переменной окружения с паролем указывается в ключе PasswordEnvVar.                                    #
#                                                                                                                       #
#########################################################################################################################

# Коды возврата vpncmd для команды AccountStatusGet. Не зависят от языка интерфейса.
$script:VpnCmdExitConnected    = 0   # подключение установлено
$script:VpnCmdExitNotConnected = 37  # настройка подключения есть, но она не подключена
$script:VpnCmdExitNotFound     = 36  # настройки подключения с таким именем не существует

<#
.SYNOPSIS
    Возвращает настройки VPN из config.json с подставленными значениями по умолчанию.
#>
function Get-VpnSettings {
    param (
        $Config
    )

    $vpn = $Config.Vpn

    $settings = [pscustomobject]@{
        Enabled               = $false
        Type                  = "SoftEther"
        VpnCmdPath            = "C:\Program Files\SoftEther VPN Client\vpncmd.exe"
        AccountName           = ""
        RasEntryName          = ""
        PasswordEnvVar        = ""
        ConnectTimeoutSeconds = 90
        TestHost              = ""
        TestPort              = 0
        DisconnectAfterRun    = $true
    }

    if ($null -eq $vpn) { return $settings }

    foreach ($name in @($settings.PSObject.Properties.Name)) {
        $property = $vpn.PSObject.Properties[$name]
        if ($null -ne $property -and $null -ne $property.Value -and $property.Value -ne "") {
            $settings.$name = $property.Value
        }
    }

    # Значения из JSON приходят строками или числами — приводим к нужным типам
    $settings.Enabled               = [bool]$settings.Enabled
    $settings.DisconnectAfterRun    = [bool]$settings.DisconnectAfterRun
    $settings.ConnectTimeoutSeconds = [int]$settings.ConnectTimeoutSeconds
    $settings.TestPort              = [int]$settings.TestPort

    return $settings
}

<#
.SYNOPSIS
    Проверяет доступность узла за VPN: TCP-порт, если он задан, иначе ping.

.DESCRIPTION
    Проверка по порту надежнее ping: во многих сетях ICMP закрыт, а RevitServer
    при этом отвечает по HTTP. Если TestHost не задан, проверка пропускается.
#>
function Test-VpnTarget {
    param (
        [string]$TestHost,
        [int]$TestPort = 0,
        [int]$TimeoutMilliseconds = 5000
    )

    if ([string]::IsNullOrWhiteSpace($TestHost)) { return $true }

    if ($TestPort -gt 0) {
        $client = New-Object System.Net.Sockets.TcpClient
        try {
            $async = $client.BeginConnect($TestHost, $TestPort, $null, $null)
            if (-not $async.AsyncWaitHandle.WaitOne($TimeoutMilliseconds, $false)) { return $false }
            $client.EndConnect($async)
            return $true
        } catch {
            return $false
        } finally {
            $client.Close()
        }
    }

    return (Test-Connection -ComputerName $TestHost -Count 1 -Quiet)
}

<#
.SYNOPSIS
    Собирает общие аргументы vpncmd, включая пароль управления из переменной окружения.
#>
function Get-VpnCmdArguments {
    param (
        $Settings,
        [string]$Command
    )

    $arguments = "/client localhost"

    if (-not [string]::IsNullOrWhiteSpace($Settings.PasswordEnvVar)) {
        $password = [Environment]::GetEnvironmentVariable($Settings.PasswordEnvVar)
        if (-not [string]::IsNullOrWhiteSpace($password)) {
            $arguments += " /password:$password"
        }
    }

    return "$arguments /cmd $Command"
}

<#
.SYNOPSIS
    Определяет состояние VPN-подключения.

.OUTPUTS
    Connected | Disconnected | NotFound | Unavailable
#>
function Get-VpnState {
    param (
        $Settings,
        [string]$LogFile
    )

    if ($Settings.Type -eq "Rasdial") {
        # Для встроенного VPN Windows состояние берем из Get-VpnConnection.
        # Подключение может быть заведено как для пользователя, так и для всех пользователей.
        try {
            $connection = Get-VpnConnection -Name $Settings.RasEntryName -ErrorAction SilentlyContinue
            if ($null -eq $connection) {
                $connection = Get-VpnConnection -Name $Settings.RasEntryName -AllUserConnection -ErrorAction SilentlyContinue
            }
            if ($null -eq $connection) { return "NotFound" }
            if ($connection.ConnectionStatus -eq "Connected") { return "Connected" }
            return "Disconnected"
        } catch {
            return "Unavailable"
        }
    }

    if (-not (Test-Path $Settings.VpnCmdPath)) { return "Unavailable" }

    $arguments = Get-VpnCmdArguments -Settings $Settings -Command "AccountStatusGet `"$($Settings.AccountName)`""
    $result = Invoke-ExternalProcess -FilePath $Settings.VpnCmdPath -Arguments $arguments -TimeoutSeconds 60

    switch ($result.ExitCode) {
        $script:VpnCmdExitConnected    { return "Connected" }
        $script:VpnCmdExitNotConnected { return "Disconnected" }
        $script:VpnCmdExitNotFound     { return "NotFound" }
        default {
            Write-Log -Message "VPN: vpncmd вернул неожиданный код $($result.ExitCode) при запросе состояния.`n$($result.StdOut)" -LogFile $LogFile -Level WARN
            return "Unavailable"
        }
    }
}

<#
.SYNOPSIS
    Подключает VPN, если он еще не подключен.

.OUTPUTS
    Объект со свойствами:
      Success           — VPN подключен и целевой узел доступен
      AlreadyConnected  — подключение было установлено до запуска пайплайна
                          (такое подключение по завершении не разрывается)
#>
function Connect-PipelineVpn {
    param (
        $Settings,
        [string]$LogFile
    )

    $answer = [pscustomobject]@{ Success = $false; AlreadyConnected = $false }

    $connectionName = if ($Settings.Type -eq "Rasdial") { $Settings.RasEntryName } else { $Settings.AccountName }
    if ([string]::IsNullOrWhiteSpace($connectionName)) {
        Write-Log -Message "VPN: не задано имя подключения (AccountName для SoftEther или RasEntryName для встроенного VPN)" -LogFile $LogFile -Level ERROR
        return $answer
    }

    # Для SoftEther нужна работающая служба клиента — под Планировщиком заданий
    # она должна стартовать автоматически
    if ($Settings.Type -ne "Rasdial") {
        if (-not (Test-Path $Settings.VpnCmdPath)) {
            Write-Log -Message "VPN: не найдена утилита vpncmd по пути $($Settings.VpnCmdPath)" -LogFile $LogFile -Level ERROR
            return $answer
        }

        $service = Get-Service -Name "SEVPNCLIENT" -ErrorAction SilentlyContinue
        if ($null -eq $service) {
            Write-Log -Message "VPN: служба SoftEther VPN Client (SEVPNCLIENT) не установлена на этой машине" -LogFile $LogFile -Level ERROR
            return $answer
        }
        if ($service.Status -ne "Running") {
            Write-Log -Message "VPN: служба SEVPNCLIENT остановлена (статус $($service.Status)). Запустите ее командой: Start-Service SEVPNCLIENT" -LogFile $LogFile -Level ERROR
            return $answer
        }
    }

    $state = Get-VpnState -Settings $Settings -LogFile $LogFile

    if ($state -eq "NotFound") {
        Write-Log -Message "VPN: подключение '$connectionName' не заведено в клиенте VPN" -LogFile $LogFile -Level ERROR
        return $answer
    }
    if ($state -eq "Unavailable") {
        Write-Log -Message "VPN: не удалось определить состояние подключения '$connectionName'" -LogFile $LogFile -Level ERROR
        return $answer
    }

    if ($state -eq "Connected") {
        # Подключение подняли до нас (например, вручную) — по завершении не разрываем
        Write-Log -Message "VPN: подключение '$connectionName' уже установлено, используем его" -LogFile $LogFile
        $answer.AlreadyConnected = $true
        $answer.Success = Wait-VpnTarget -Settings $Settings -LogFile $LogFile
        return $answer
    }

    Write-Log -Message "VPN: подключаем '$connectionName' ($($Settings.Type))" -LogFile $LogFile

    if ($Settings.Type -eq "Rasdial") {
        $result = Invoke-ExternalProcess -FilePath "rasdial.exe" -Arguments "`"$connectionName`"" -TimeoutSeconds $Settings.ConnectTimeoutSeconds
    } else {
        $arguments = Get-VpnCmdArguments -Settings $Settings -Command "AccountConnect `"$connectionName`""
        $result = Invoke-ExternalProcess -FilePath $Settings.VpnCmdPath -Arguments $arguments -TimeoutSeconds $Settings.ConnectTimeoutSeconds
    }

    if ($result.ExitCode -ne 0) {
        Write-Log -Message "VPN: команда подключения завершилась с кодом $($result.ExitCode).`n$($result.StdOut)`n$($result.StdErr)" -LogFile $LogFile -Level ERROR
        return $answer
    }

    # Команда подключения возвращает управление сразу, поэтому ждем фактической
    # установки соединения и доступности целевого узла
    $answer.Success = Wait-VpnConnected -Settings $Settings -LogFile $LogFile
    return $answer
}

<#
.SYNOPSIS
    Ждет, пока подключение перейдет в состояние Connected и станет доступен целевой узел.
#>
function Wait-VpnConnected {
    param (
        $Settings,
        [string]$LogFile
    )

    $deadline = (Get-Date).AddSeconds($Settings.ConnectTimeoutSeconds)

    while ((Get-Date) -lt $deadline) {
        if ((Get-VpnState -Settings $Settings -LogFile $LogFile) -eq "Connected") {
            if (Test-VpnTarget -TestHost $Settings.TestHost -TestPort $Settings.TestPort) {
                $target = if ($Settings.TestHost) { ", узел $($Settings.TestHost) доступен" } else { "" }
                Write-Log -Message "VPN: подключение установлено$target" -LogFile $LogFile
                return $true
            }
        }
        Start-Sleep -Seconds 3
    }

    Write-Log -Message "VPN: подключение не установилось за $($Settings.ConnectTimeoutSeconds) с" -LogFile $LogFile -Level ERROR
    return $false
}

<#
.SYNOPSIS
    Проверяет доступность целевого узла для уже установленного подключения.
#>
function Wait-VpnTarget {
    param (
        $Settings,
        [string]$LogFile
    )

    if (Test-VpnTarget -TestHost $Settings.TestHost -TestPort $Settings.TestPort) { return $true }

    Write-Log -Message "VPN: подключение установлено, но узел $($Settings.TestHost) недоступен" -LogFile $LogFile -Level ERROR
    return $false
}

<#
.SYNOPSIS
    Отключает VPN. Подключение, поднятое не пайплайном, не разрывается.
#>
function Disconnect-PipelineVpn {
    param (
        $Settings,
        [string]$LogFile,
        [bool]$AlreadyConnected
    )

    $connectionName = if ($Settings.Type -eq "Rasdial") { $Settings.RasEntryName } else { $Settings.AccountName }

    if ($AlreadyConnected) {
        Write-Log -Message "VPN: подключение '$connectionName' было установлено до запуска пайплайна, оставляем включенным" -LogFile $LogFile
        return
    }
    if (-not $Settings.DisconnectAfterRun) {
        Write-Log -Message "VPN: отключение по завершении выключено настройкой DisconnectAfterRun" -LogFile $LogFile
        return
    }

    Write-Log -Message "VPN: отключаем '$connectionName'" -LogFile $LogFile

    try {
        if ($Settings.Type -eq "Rasdial") {
            $result = Invoke-ExternalProcess -FilePath "rasdial.exe" -Arguments "`"$connectionName`" /disconnect" -TimeoutSeconds 60
        } else {
            $arguments = Get-VpnCmdArguments -Settings $Settings -Command "AccountDisconnect `"$connectionName`""
            $result = Invoke-ExternalProcess -FilePath $Settings.VpnCmdPath -Arguments $arguments -TimeoutSeconds 60
        }

        if ($result.ExitCode -eq 0) {
            Write-Log -Message "VPN: подключение '$connectionName' отключено" -LogFile $LogFile
        } else {
            Write-Log -Message "VPN: не удалось отключить '$connectionName', код $($result.ExitCode).`n$($result.StdOut)`n$($result.StdErr)" -LogFile $LogFile -Level WARN
        }
    } catch {
        Write-Log -Message "VPN: исключение при отключении '$connectionName': $($_.Exception.Message)" -LogFile $LogFile -Level WARN
    }
}
