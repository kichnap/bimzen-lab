#########################################################################################################################
# ПАНЧИК.РФ                                                                                                             #
# Скрипт vpn                                                                                                            #
# Версия: 2.5 (19.08.2026)                                                                                              #
# Только для сотрудников BIM-отдела.                                                                                    #
# Скрипт выполняет следующие функции:                                                                                   #
#   - подключение и отключение туннелей VPN перед работой пайплайна                                                     #
#   - список туннелей: у каждого свой выключатель, свой клиент и своя проверка доступности узла                         #
#   - поддержка клиентов: SoftEther (vpncmd.exe), встроенный VPN Windows (rasdial.exe),                                 #
#     WireGuard (служба туннеля), OpenVPN (openvpn.exe с интерфейсом управления)                                        #
#                                                                                                                       #
# WireGuard и OpenVPN требуют прав администратора: в задаче Планировщика нужен                                          #
# режим «Выполнить с наивысшими правами».                                                                               #
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
    Таблица клиентов VPN. Добавить нового клиента — значит добавить сюда запись
    и функции, названные в ней. Имена функций хранятся строками, а не ссылками:
    таблица описана до того, как функции объявлены.

    Title          — как клиент называется в логе
    NameProperty   — из какого ключа настроек берется имя подключения
    NeedsElevation — нужны ли права администратора
    Ready          — проверка, что клиент установлен и готов принимать команды
    GetState       — Connected | Disconnected | NotFound | Unavailable
    Connect        — $true, если команда подключения принята клиентом
    Disconnect     — $true, если команда отключения выполнена
#>
$script:VpnProviders = @{
    "SoftEther" = [pscustomobject]@{
        Title          = "SoftEther VPN Client"
        NameProperty   = "AccountName"
        NeedsElevation = $false
        Ready          = "Test-SoftEtherVpnReady"
        GetState       = "Get-SoftEtherVpnState"
        Connect        = "Connect-SoftEtherVpn"
        Disconnect     = "Disconnect-SoftEtherVpn"
    }
    "Rasdial" = [pscustomobject]@{
        Title          = "встроенный VPN Windows"
        NameProperty   = "RasEntryName"
        NeedsElevation = $false
        Ready          = "Test-RasdialVpnReady"
        GetState       = "Get-RasdialVpnState"
        Connect        = "Connect-RasdialVpn"
        Disconnect     = "Disconnect-RasdialVpn"
    }
    "WireGuard" = [pscustomobject]@{
        Title          = "WireGuard"
        NameProperty   = "Tunnel"
        NeedsElevation = $true
        Ready          = "Test-WireGuardVpnReady"
        GetState       = "Get-WireGuardVpnState"
        Connect        = "Connect-WireGuardVpn"
        Disconnect     = "Disconnect-WireGuardVpn"
    }
    "OpenVpn" = [pscustomobject]@{
        Title          = "OpenVPN"
        NameProperty   = "Profile"
        NeedsElevation = $true
        Ready          = "Test-OpenVpnReady"
        GetState       = "Get-OpenVpnState"
        Connect        = "Connect-OpenVpn"
        Disconnect     = "Disconnect-OpenVpn"
    }
}

# Процессы openvpn.exe, запущенные пайплайном: имя туннеля -> идентификатор процесса.
# Нужны как запасной путь отключения, если порт управления не отвечает.
$script:OpenVpnProcesses = @{}

#########################################################################################################################
#                                          Разбор настроек из config.json                                               #
#########################################################################################################################

<#
.SYNOPSIS
    Возвращает значение ключа настроек или значение по умолчанию.

.DESCRIPTION
    Отдельная функция нужна из-за $false: сравнение вида "значение -ne ''"
    приводит пустую строку к [bool] и отбрасывает честно заданное false,
    из-за чего выключенный туннель молча оставался бы включенным.
#>
function Get-VpnValue {
    param (
        $Item,
        [string]$Name,
        $Default
    )

    if ($null -eq $Item) { return $Default }

    $property = $Item.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return $Default }

    $value = $property.Value
    if ($value -is [string] -and [string]::IsNullOrWhiteSpace($value)) { return $Default }

    return $value
}

<#
.SYNOPSIS
    Приводит одну запись списка туннелей к объекту с полным набором свойств.
#>
function ConvertTo-VpnConnection {
    param (
        $Item,
        $Defaults,
        [int]$Index,
        [switch]$Legacy
    )

    # В настройках старого образца ключ Enabled блока Vpn означает «поднимать ли
    # VPN вообще», а не «использовать ли этот туннель». Единственный туннель
    # такого файла всегда включен, иначе при Enabled: false список оказался бы
    # пустым и пайплайну было бы нечего подключать.
    $enabled = if ($Legacy) { $true } else { [bool](Get-VpnValue -Item $Item -Name "Enabled" -Default $true) }

    $connection = [pscustomobject]@{
        Name                  = [string](Get-VpnValue -Item $Item -Name "Name"                  -Default "")
        Enabled               = $enabled
        Required              = [bool]  (Get-VpnValue -Item $Item -Name "Required"              -Default $true)
        Type                  = [string](Get-VpnValue -Item $Item -Name "Type"                  -Default "SoftEther")
        AccountName           = [string](Get-VpnValue -Item $Item -Name "AccountName"           -Default "")
        RasEntryName          = [string](Get-VpnValue -Item $Item -Name "RasEntryName"          -Default "")
        VpnCmdPath            = [string](Get-VpnValue -Item $Item -Name "VpnCmdPath"            -Default "C:\Program Files\SoftEther VPN Client\vpncmd.exe")
        PasswordEnvVar        = [string](Get-VpnValue -Item $Item -Name "PasswordEnvVar"        -Default "")
        Tunnel                = [string](Get-VpnValue -Item $Item -Name "Tunnel"                -Default "")
        WireGuardPath         = [string](Get-VpnValue -Item $Item -Name "WireGuardPath"         -Default "C:\Program Files\WireGuard\wireguard.exe")
        Profile               = [string](Get-VpnValue -Item $Item -Name "Profile"               -Default "")
        ProfilePath           = [string](Get-VpnValue -Item $Item -Name "ProfilePath"           -Default "")
        OpenVpnPath           = [string](Get-VpnValue -Item $Item -Name "OpenVpnPath"           -Default "C:\Program Files\OpenVPN\bin\openvpn.exe")
        ManagementPort        = [int]   (Get-VpnValue -Item $Item -Name "ManagementPort"        -Default 25340)
        TestHost              = [string](Get-VpnValue -Item $Item -Name "TestHost"              -Default "")
        TestPort              = [int]   (Get-VpnValue -Item $Item -Name "TestPort"              -Default 0)
        ConnectTimeoutSeconds = [int]   (Get-VpnValue -Item $Item -Name "ConnectTimeoutSeconds" -Default $Defaults.ConnectTimeoutSeconds)
        ConnectAttempts       = [int]   (Get-VpnValue -Item $Item -Name "ConnectAttempts"       -Default $Defaults.ConnectAttempts)
        DisconnectAfterRun    = [bool]  (Get-VpnValue -Item $Item -Name "DisconnectAfterRun"    -Default $Defaults.DisconnectAfterRun)
    }

    # Для OpenVPN профиль можно задать либо именем, либо готовым путем к .ovpn.
    # Если задан только путь, имя профиля берем из имени файла.
    if ([string]::IsNullOrWhiteSpace($connection.Profile) -and -not [string]::IsNullOrWhiteSpace($connection.ProfilePath)) {
        $connection.Profile = [System.IO.Path]::GetFileNameWithoutExtension($connection.ProfilePath)
    }

    # Имя в логе. Если человек его не задал, показываем имя подключения в клиенте,
    # а в последнюю очередь — порядковый номер: в логе туннели должны различаться.
    if ([string]::IsNullOrWhiteSpace($connection.Name)) {
        $target = Get-VpnTargetName -Connection $connection
        $connection.Name = if ([string]::IsNullOrWhiteSpace($target)) { "Туннель $Index" } else { $target }
    }

    return $connection
}

<#
.SYNOPSIS
    Возвращает настройки VPN из config.json: общие значения и список туннелей.

.DESCRIPTION
    Старый config.json с одним подключением (ключи Type, AccountName, RasEntryName
    в блоке Vpn) сворачивается в список из одного туннеля. Дальше весь код работает
    только со списком — второго пути исполнения в скрипте нет.
#>
function Get-VpnSettings {
    param (
        $Config
    )

    $vpn = $Config.Vpn

    $settings = [pscustomobject]@{
        Enabled               = [bool](Get-VpnValue -Item $vpn -Name "Enabled"               -Default $false)
        ConnectTimeoutSeconds = [int] (Get-VpnValue -Item $vpn -Name "ConnectTimeoutSeconds" -Default 90)
        ConnectAttempts       = [int] (Get-VpnValue -Item $vpn -Name "ConnectAttempts"       -Default 2)
        DisconnectAfterRun    = [bool](Get-VpnValue -Item $vpn -Name "DisconnectAfterRun"    -Default $true)
        Connections           = @()
    }

    if ($null -eq $vpn) { return $settings }

    $isList = ($null -ne $vpn.Connections -and @($vpn.Connections).Count -gt 0)

    # Настройки старого образца: единственный туннель описан ключами блока Vpn
    $items = if ($isList) { @($vpn.Connections) } else { @($vpn) }

    $connections = @()
    $index = 0
    foreach ($item in $items) {
        $index++
        $connections += ConvertTo-VpnConnection -Item $item -Defaults $settings -Index $index -Legacy:(-not $isList)
    }
    $settings.Connections = $connections

    return $settings
}

<#
.SYNOPSIS
    Оставляет включенными только названные туннели (ключ -VpnOnly при ручном запуске).

.DESCRIPTION
    Нужно, чтобы проверить один канал, не редактируя боевой config.json.
    Имя сверяется с ключом Name туннеля и с именем подключения в клиенте.
#>
function Select-VpnConnections {
    param (
        $Settings,
        [string[]]$Only,
        [string]$LogFile
    )

    if ($null -eq $Only -or $Only.Count -eq 0) { return $Settings }

    $wanted = @($Only | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim() })
    if ($wanted.Count -eq 0) { return $Settings }

    foreach ($connection in $Settings.Connections) {
        $match = $wanted | Where-Object {
            $_ -eq $connection.Name -or $_ -eq (Get-VpnTargetName -Connection $connection)
        }
        $connection.Enabled = @($match).Count -gt 0
    }

    foreach ($name in $wanted) {
        $found = $Settings.Connections | Where-Object {
            $name -eq $_.Name -or $name -eq (Get-VpnTargetName -Connection $_)
        }
        if (@($found).Count -eq 0) {
            Write-Log -Message "VPN: туннель '$name' из ключа -VpnOnly в настройках не найден" -LogFile $LogFile -Level WARN
        }
    }

    $selected = @($Settings.Connections | Where-Object { $_.Enabled } | ForEach-Object { $_.Name })
    Write-Log -Message "VPN: ключом -VpnOnly оставлены туннели: $($selected -join ', ')" -LogFile $LogFile

    return $Settings
}

#########################################################################################################################
#                                            Общие вспомогательные функции                                              #
#########################################################################################################################

<#
.SYNOPSIS
    Возвращает описание клиента VPN по типу подключения или $null, если тип неизвестен.
#>
function Get-VpnProvider {
    param (
        $Connection
    )

    if ([string]::IsNullOrWhiteSpace($Connection.Type)) { return $null }
    return $script:VpnProviders[$Connection.Type]
}

<#
.SYNOPSIS
    Возвращает имя подключения так, как оно заведено в клиенте VPN.
#>
function Get-VpnTargetName {
    param (
        $Connection
    )

    $provider = Get-VpnProvider -Connection $Connection
    if ($null -eq $provider) { return "" }

    return [string]$Connection.($provider.NameProperty)
}

<#
.SYNOPSIS
    Собирает название туннеля для лога: имя из настроек, клиент и имя подключения.
#>
function Get-VpnConnectionTitle {
    param (
        $Connection
    )

    $provider = Get-VpnProvider -Connection $Connection
    $client = if ($null -eq $provider) { $Connection.Type } else { $provider.Title }
    $target = Get-VpnTargetName -Connection $Connection

    if ([string]::IsNullOrWhiteSpace($target) -or $target -eq $Connection.Name) {
        return "'$($Connection.Name)' ($client)"
    }
    return "'$($Connection.Name)' ($client, подключение '$target')"
}

<#
.SYNOPSIS
    Запущен ли скрипт с правами администратора.

.DESCRIPTION
    Часть клиентов VPN без прав администратора не поднимет туннель. Если права
    определить не удалось, считаем, что они есть: лучше дойти до понятной ошибки
    клиента, чем отказаться работать по своей же неудачной проверке.
#>
function Test-VpnElevated {
    try {
        $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
        return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch {
        return $true
    }
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
    Ждет, пока туннель перейдет в состояние Connected и станет доступен целевой узел.
#>
function Wait-VpnConnected {
    param (
        $Connection,
        [string]$LogFile
    )

    $title = Get-VpnConnectionTitle -Connection $Connection
    $deadline = (Get-Date).AddSeconds($Connection.ConnectTimeoutSeconds)

    # Канал поднимается в два приема: сначала клиент устанавливает соединение,
    # затем становится доступен узел за ним. Момент, когда клиент уже подключен,
    # отмечаем в логе: иначе отказ по таймауту не отличить от отказа сети.
    $clientConnected = $false

    while ((Get-Date) -lt $deadline) {
        if ((Get-VpnConnectionState -Connection $Connection -LogFile $LogFile) -eq "Connected") {
            if (Test-VpnTarget -TestHost $Connection.TestHost -TestPort $Connection.TestPort) {
                $target = if ($Connection.TestHost) { ", узел $($Connection.TestHost) доступен" } else { "" }
                Write-Log -Message "VPN: туннель $title подключен$target" -LogFile $LogFile
                return $true
            }
            if (-not $clientConnected) {
                Write-Log -Message "VPN: туннель $title подключен, ждем доступности узла $($Connection.TestHost)" -LogFile $LogFile
                $clientConnected = $true
            }
        }
        Start-Sleep -Seconds 3
    }

    if ($clientConnected) {
        Write-Log -Message "VPN: туннель $title подключен, но узел $($Connection.TestHost) не ответил за $($Connection.ConnectTimeoutSeconds) с" -LogFile $LogFile -Level ERROR
    } else {
        Write-Log -Message "VPN: туннель $title не подключился за $($Connection.ConnectTimeoutSeconds) с" -LogFile $LogFile -Level ERROR
    }
    return $false
}

<#
.SYNOPSIS
    Проверяет доступность целевого узла для уже установленного подключения.
#>
function Wait-VpnTarget {
    param (
        $Connection,
        [string]$LogFile
    )

    if (Test-VpnTarget -TestHost $Connection.TestHost -TestPort $Connection.TestPort) { return $true }

    $title = Get-VpnConnectionTitle -Connection $Connection
    Write-Log -Message "VPN: туннель $title подключен, но узел $($Connection.TestHost) недоступен" -LogFile $LogFile -Level ERROR
    return $false
}

#########################################################################################################################
#                                              SoftEther VPN Client                                                     #
#########################################################################################################################

<#
.SYNOPSIS
    Собирает общие аргументы vpncmd, включая пароль управления из переменной окружения.
#>
function Get-VpnCmdArguments {
    param (
        $Connection,
        [string]$Command
    )

    $arguments = "/client localhost"

    if (-not [string]::IsNullOrWhiteSpace($Connection.PasswordEnvVar)) {
        $password = [Environment]::GetEnvironmentVariable($Connection.PasswordEnvVar)
        if (-not [string]::IsNullOrWhiteSpace($password)) {
            $arguments += " /password:$password"
        }
    }

    return "$arguments /cmd $Command"
}

<#
.SYNOPSIS
    Проверяет, что утилита vpncmd на месте и служба клиента SoftEther работает.

.DESCRIPTION
    Под Планировщиком заданий служба должна стартовать автоматически: настройки
    подключений хранятся в ней, и без нее команды vpncmd выполнять некому.
#>
function Test-SoftEtherVpnReady {
    param (
        $Connection,
        [string]$LogFile
    )

    if (-not (Test-Path $Connection.VpnCmdPath)) {
        Write-Log -Message "VPN: не найдена утилита vpncmd по пути $($Connection.VpnCmdPath)" -LogFile $LogFile -Level ERROR
        return $false
    }

    $service = Get-Service -Name "SEVPNCLIENT" -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        Write-Log -Message "VPN: служба SoftEther VPN Client (SEVPNCLIENT) не установлена на этой машине" -LogFile $LogFile -Level ERROR
        return $false
    }
    if ($service.Status -ne "Running") {
        Write-Log -Message "VPN: служба SEVPNCLIENT остановлена (статус $($service.Status)). Запустите ее командой: Start-Service SEVPNCLIENT" -LogFile $LogFile -Level ERROR
        return $false
    }

    return $true
}

<#
.SYNOPSIS
    Определяет состояние подключения SoftEther по коду возврата vpncmd.
#>
function Get-SoftEtherVpnState {
    param (
        $Connection,
        [string]$LogFile
    )

    if (-not (Test-Path $Connection.VpnCmdPath)) { return "Unavailable" }

    $arguments = Get-VpnCmdArguments -Connection $Connection -Command "AccountStatusGet `"$($Connection.AccountName)`""
    $result = Invoke-ExternalProcess -FilePath $Connection.VpnCmdPath -Arguments $arguments -TimeoutSeconds 60

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
    Отдает клиенту SoftEther команду подключения.
#>
function Connect-SoftEtherVpn {
    param (
        $Connection,
        [string]$LogFile
    )

    $arguments = Get-VpnCmdArguments -Connection $Connection -Command "AccountConnect `"$($Connection.AccountName)`""
    $result = Invoke-ExternalProcess -FilePath $Connection.VpnCmdPath -Arguments $arguments -TimeoutSeconds $Connection.ConnectTimeoutSeconds

    if ($result.ExitCode -ne 0) {
        Write-Log -Message "VPN: команда подключения завершилась с кодом $($result.ExitCode).`n$($result.StdOut)`n$($result.StdErr)" -LogFile $LogFile -Level ERROR
        return $false
    }

    return $true
}

<#
.SYNOPSIS
    Отдает клиенту SoftEther команду отключения.
#>
function Disconnect-SoftEtherVpn {
    param (
        $Connection,
        [string]$LogFile
    )

    $arguments = Get-VpnCmdArguments -Connection $Connection -Command "AccountDisconnect `"$($Connection.AccountName)`""
    $result = Invoke-ExternalProcess -FilePath $Connection.VpnCmdPath -Arguments $arguments -TimeoutSeconds 60

    if ($result.ExitCode -ne 0) {
        Write-Log -Message "VPN: не удалось отключить '$($Connection.AccountName)', код $($result.ExitCode).`n$($result.StdOut)`n$($result.StdErr)" -LogFile $LogFile -Level WARN
        return $false
    }

    return $true
}

#########################################################################################################################
#                                            Встроенный VPN Windows                                                     #
#########################################################################################################################

<#
.SYNOPSIS
    Для встроенного VPN проверять нечего: rasdial.exe входит в состав Windows.
#>
function Test-RasdialVpnReady {
    param (
        $Connection,
        [string]$LogFile
    )

    return $true
}

<#
.SYNOPSIS
    Определяет состояние встроенного подключения Windows.

.DESCRIPTION
    Подключение может быть заведено как для пользователя, так и для всех пользователей,
    поэтому проверяются оба варианта.
#>
function Get-RasdialVpnState {
    param (
        $Connection,
        [string]$LogFile
    )

    try {
        $entry = Get-VpnConnection -Name $Connection.RasEntryName -ErrorAction SilentlyContinue
        if ($null -eq $entry) {
            $entry = Get-VpnConnection -Name $Connection.RasEntryName -AllUserConnection -ErrorAction SilentlyContinue
        }
        if ($null -eq $entry) { return "NotFound" }
        if ($entry.ConnectionStatus -eq "Connected") { return "Connected" }
        return "Disconnected"
    } catch {
        return "Unavailable"
    }
}

<#
.SYNOPSIS
    Поднимает встроенное подключение Windows через rasdial.
#>
function Connect-RasdialVpn {
    param (
        $Connection,
        [string]$LogFile
    )

    $result = Invoke-ExternalProcess -FilePath "rasdial.exe" -Arguments "`"$($Connection.RasEntryName)`"" -TimeoutSeconds $Connection.ConnectTimeoutSeconds

    if ($result.ExitCode -ne 0) {
        Write-Log -Message "VPN: команда подключения завершилась с кодом $($result.ExitCode).`n$($result.StdOut)`n$($result.StdErr)" -LogFile $LogFile -Level ERROR
        return $false
    }

    return $true
}

<#
.SYNOPSIS
    Разрывает встроенное подключение Windows.
#>
function Disconnect-RasdialVpn {
    param (
        $Connection,
        [string]$LogFile
    )

    $result = Invoke-ExternalProcess -FilePath "rasdial.exe" -Arguments "`"$($Connection.RasEntryName)`" /disconnect" -TimeoutSeconds 60

    if ($result.ExitCode -ne 0) {
        Write-Log -Message "VPN: не удалось отключить '$($Connection.RasEntryName)', код $($result.ExitCode).`n$($result.StdOut)`n$($result.StdErr)" -LogFile $LogFile -Level WARN
        return $false
    }

    return $true
}

#########################################################################################################################
#                                                    WireGuard                                                          #
#########################################################################################################################

<#
.SYNOPSIS
    Возвращает имя службы Windows, которой в WireGuard соответствует туннель.

.DESCRIPTION
    Каждый туннель WireGuard — отдельная служба вида WireGuardTunnel$<имя>.
    Она создается один раз командой /installtunnelservice, дальше туннель
    включается и выключается как обычная служба.
#>
function Get-WireGuardServiceName {
    param (
        $Connection
    )

    return "WireGuardTunnel`$$($Connection.Tunnel)"
}

<#
.SYNOPSIS
    Проверяет, что служба туннеля WireGuard заведена на этой машине.

.DESCRIPTION
    Службу пайплайн не создает сам: это изменение системы, его делает человек.
    Если службы нет, в лог кладется готовая команда установки.
#>
function Test-WireGuardVpnReady {
    param (
        $Connection,
        [string]$LogFile
    )

    $serviceName = Get-WireGuardServiceName -Connection $Connection
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

    if ($null -eq $service) {
        Write-Log -Message ("VPN: служба туннеля WireGuard '$($Connection.Tunnel)' не заведена на этой машине. " +
            "Установите ее один раз от имени администратора: & `"$($Connection.WireGuardPath)`" /installtunnelservice `"<путь к $($Connection.Tunnel).conf>`"") -LogFile $LogFile -Level ERROR
        return $false
    }

    return $true
}

<#
.SYNOPSIS
    Определяет состояние туннеля WireGuard по состоянию его службы.

.DESCRIPTION
    Запущенная служба означает поднятый интерфейс. Что канал действительно
    работает, подтверждает общая проверка доступности узла (TestHost).
#>
function Get-WireGuardVpnState {
    param (
        $Connection,
        [string]$LogFile
    )

    $service = Get-Service -Name (Get-WireGuardServiceName -Connection $Connection) -ErrorAction SilentlyContinue
    if ($null -eq $service) { return "NotFound" }
    if ($service.Status -eq "Running") { return "Connected" }

    return "Disconnected"
}

<#
.SYNOPSIS
    Включает туннель WireGuard: запускает его службу.
#>
function Connect-WireGuardVpn {
    param (
        $Connection,
        [string]$LogFile
    )

    $serviceName = Get-WireGuardServiceName -Connection $Connection

    try {
        Start-Service -Name $serviceName -ErrorAction Stop
        return $true
    } catch {
        Write-Log -Message "VPN: не удалось запустить службу $serviceName. $($_.Exception.Message)" -LogFile $LogFile -Level ERROR
        return $false
    }
}

<#
.SYNOPSIS
    Выключает туннель WireGuard: останавливает его службу.
#>
function Disconnect-WireGuardVpn {
    param (
        $Connection,
        [string]$LogFile
    )

    $serviceName = Get-WireGuardServiceName -Connection $Connection

    try {
        Stop-Service -Name $serviceName -ErrorAction Stop
        return $true
    } catch {
        Write-Log -Message "VPN: не удалось остановить службу $serviceName. $($_.Exception.Message)" -LogFile $LogFile -Level WARN
        return $false
    }
}

#########################################################################################################################
#                                                      OpenVPN                                                          #
#########################################################################################################################

<#
.SYNOPSIS
    Находит файл профиля .ovpn по имени или возвращает заданный путь.

.DESCRIPTION
    Профили лежат либо в личной папке пользователя, либо в папке установки
    OpenVPN, причем как отдельным файлом, так и в подпапке со своим именем.
    Перебираем оба варианта, а затем ищем файл рекурсивно.
#>
function Get-OpenVpnProfilePath {
    param (
        $Connection
    )

    if (-not [string]::IsNullOrWhiteSpace($Connection.ProfilePath)) { return $Connection.ProfilePath }

    $name = $Connection.Profile
    if ([string]::IsNullOrWhiteSpace($name)) { return "" }

    $roots = @(
        (Join-Path $env:USERPROFILE "OpenVPN\config"),
        "C:\Program Files\OpenVPN\config"
    )

    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }

        foreach ($candidate in @((Join-Path $root "$name\$name.ovpn"), (Join-Path $root "$name.ovpn"))) {
            if (Test-Path $candidate) { return $candidate }
        }

        $found = Get-ChildItem -Path $root -Filter "$name.ovpn" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $found) { return $found.FullName }
    }

    return ""
}

<#
.SYNOPSIS
    Отдает команды интерфейсу управления openvpn.exe и возвращает его ответ.

.DESCRIPTION
    Интерфейс управления — единственный штатный способ спросить у openvpn его
    состояние и попросить корректно завершиться. Молчащий порт означает, что
    запущенного нами процесса нет: это не ошибка, а одно из состояний.

.OUTPUTS
    Available — удалось ли соединиться с портом управления
    Output    — все, что успел ответить openvpn
#>
function Invoke-OpenVpnManagement {
    param (
        $Connection,
        [string[]]$Commands,
        [int]$TimeoutMilliseconds = 5000
    )

    $answer = [pscustomobject]@{ Available = $false; Output = "" }
    $client = New-Object System.Net.Sockets.TcpClient

    try {
        $async = $client.BeginConnect("127.0.0.1", $Connection.ManagementPort, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne($TimeoutMilliseconds, $false)) { return $answer }
        $client.EndConnect($async)

        $stream = $client.GetStream()
        $stream.ReadTimeout = $TimeoutMilliseconds
        $writer = New-Object System.IO.StreamWriter($stream)
        $writer.AutoFlush = $true
        $reader = New-Object System.IO.StreamReader($stream)

        $text = ""

        # Приветствие приходит само, до всяких команд — вычитываем его
        Start-Sleep -Milliseconds 200
        while ($stream.DataAvailable) { $text += $reader.ReadLine() + "`n" }

        foreach ($command in $Commands) {
            $writer.WriteLine($command)
            Start-Sleep -Milliseconds 300
            while ($stream.DataAvailable) { $text += $reader.ReadLine() + "`n" }
        }

        $answer.Available = $true
        $answer.Output = $text
        return $answer
    } catch {
        return $answer
    } finally {
        $client.Close()
    }
}

<#
.SYNOPSIS
    Есть ли запущенный openvpn.exe, работающий с этим профилем.

.DESCRIPTION
    Нужно, чтобы не поднять второй экземпляр того же туннеля, если его уже
    поднял человек через клиент OpenVPN GUI. Чтение командной строки чужого
    процесса требует прав администратора — они у пайплайна и так нужны.
#>
function Test-OpenVpnProcess {
    param (
        [string]$ProfilePath
    )

    if ([string]::IsNullOrWhiteSpace($ProfilePath)) { return $false }

    try {
        $leaf = Split-Path $ProfilePath -Leaf
        $processes = Get-CimInstance -ClassName Win32_Process -Filter "Name='openvpn.exe'" -ErrorAction Stop
        foreach ($process in $processes) {
            if ($process.CommandLine -and $process.CommandLine -like "*$leaf*") { return $true }
        }
        return $false
    } catch {
        return $false
    }
}

<#
.SYNOPSIS
    Проверяет, что openvpn.exe на месте, профиль найден и годится для расписания.

.DESCRIPTION
    Профиль с голой директивой auth-user-pass спрашивает логин и пароль в диалоге.
    По расписанию отвечать на него некому, а класть пароль в файл рядом со
    скриптами нельзя, поэтому такой профиль отклоняется сразу, а не по таймауту.
#>
function Test-OpenVpnReady {
    param (
        $Connection,
        [string]$LogFile
    )

    if (-not (Test-Path $Connection.OpenVpnPath)) {
        Write-Log -Message "VPN: не найдена утилита openvpn.exe по пути $($Connection.OpenVpnPath)" -LogFile $LogFile -Level ERROR
        return $false
    }

    $profilePath = Get-OpenVpnProfilePath -Connection $Connection
    if ([string]::IsNullOrWhiteSpace($profilePath)) {
        Write-Log -Message "VPN: профиль OpenVPN '$($Connection.Profile)' не найден ни в папке пользователя, ни в папке установки OpenVPN. Укажите путь к файлу ключом ProfilePath" -LogFile $LogFile -Level ERROR
        return $false
    }
    if (-not (Test-Path $profilePath)) {
        Write-Log -Message "VPN: файл профиля OpenVPN не найден: $profilePath" -LogFile $LogFile -Level ERROR
        return $false
    }

    $interactive = Get-Content -Path $profilePath -ErrorAction SilentlyContinue | Where-Object { $_ -match '^\s*auth-user-pass\s*$' }
    if (@($interactive).Count -gt 0) {
        Write-Log -Message ("VPN: профиль '$($Connection.Profile)' требует ввода логина и пароля (директива auth-user-pass без файла). " +
            "По расписанию ответить на запрос некому, а пароль в папке со скриптами не хранится. " +
            "Для расписания нужен профиль с авторизацией по сертификату") -LogFile $LogFile -Level ERROR
        return $false
    }

    return $true
}

<#
.SYNOPSIS
    Определяет состояние туннеля OpenVPN.

.DESCRIPTION
    Сначала спрашиваем интерфейс управления — так отвечает процесс, поднятый
    пайплайном. Если порт молчит, туннель мог поднять человек через клиент
    OpenVPN GUI: тогда смотрим, нет ли openvpn.exe с этим профилем.
#>
function Get-OpenVpnState {
    param (
        $Connection,
        [string]$LogFile
    )

    $profilePath = Get-OpenVpnProfilePath -Connection $Connection
    if ([string]::IsNullOrWhiteSpace($profilePath) -or -not (Test-Path $profilePath)) { return "NotFound" }

    $management = Invoke-OpenVpnManagement -Connection $Connection -Commands @("state")
    if ($management.Available) {
        if ($management.Output -match "CONNECTED,SUCCESS") { return "Connected" }
        return "Disconnected"
    }

    if (Test-OpenVpnProcess -ProfilePath $profilePath) { return "Connected" }

    return "Disconnected"
}

<#
.SYNOPSIS
    Поднимает туннель OpenVPN своим процессом openvpn.exe.

.DESCRIPTION
    Рабочим каталогом ставится папка профиля: пути к сертификатам и ключам
    внутри .ovpn заданы относительно нее, из другого каталога профиль
    не соберется. Интерфейс управления нужен, чтобы потом спросить состояние
    и корректно завершить процесс; порт у каждого туннеля свой.
#>
function Connect-OpenVpn {
    param (
        $Connection,
        [string]$LogFile
    )

    $profilePath = Get-OpenVpnProfilePath -Connection $Connection
    $profileDir = Split-Path -Parent $profilePath

    # Свой лог openvpn кладем рядом с логом пайплайна: разбирать отказ канала
    # по журналу самого клиента гораздо проще, чем по коду возврата
    $logDir = Split-Path -Parent $LogFile
    $safeName = ($Connection.Name -replace '[^\w\.\-]', '_')
    $openVpnLog = Join-Path $logDir "openvpn-$safeName.log"

    $arguments = "--config `"$profilePath`" --management 127.0.0.1 $($Connection.ManagementPort) --log `"$openVpnLog`""

    try {
        $process = Start-Process -FilePath $Connection.OpenVpnPath -ArgumentList $arguments `
            -WorkingDirectory $profileDir -WindowStyle Hidden -PassThru -ErrorAction Stop
        $script:OpenVpnProcesses[$Connection.Name] = $process.Id
        Write-Log -Message "VPN: запущен openvpn.exe (PID $($process.Id)), журнал клиента: $openVpnLog" -LogFile $LogFile
        return $true
    } catch {
        Write-Log -Message "VPN: не удалось запустить openvpn.exe. $($_.Exception.Message)" -LogFile $LogFile -Level ERROR
        return $false
    }
}

<#
.SYNOPSIS
    Разрывает туннель OpenVPN.

.DESCRIPTION
    Штатный путь — SIGTERM через интерфейс управления: openvpn снимает маршруты
    и отдает адаптер сам. Принудительное завершение процесса оставлено запасным
    вариантом и применяется только к процессу, который запустил сам пайплайн.
#>
function Disconnect-OpenVpn {
    param (
        $Connection,
        [string]$LogFile
    )

    $management = Invoke-OpenVpnManagement -Connection $Connection -Commands @("signal SIGTERM")
    if ($management.Available) {
        $script:OpenVpnProcesses.Remove($Connection.Name)
        return $true
    }

    $processId = $script:OpenVpnProcesses[$Connection.Name]
    if ($null -eq $processId) {
        Write-Log -Message "VPN: порт управления openvpn не отвечает, а процесс этого туннеля пайплайном не запускался — отключать нечего" -LogFile $LogFile -Level WARN
        return $false
    }

    try {
        Stop-Process -Id $processId -Force -ErrorAction Stop
        $script:OpenVpnProcesses.Remove($Connection.Name)
        Write-Log -Message "VPN: порт управления не ответил, процесс openvpn.exe (PID $processId) завершен принудительно" -LogFile $LogFile -Level WARN
        return $true
    } catch {
        Write-Log -Message "VPN: не удалось завершить процесс openvpn.exe (PID $processId). $($_.Exception.Message)" -LogFile $LogFile -Level WARN
        return $false
    }
}

#########################################################################################################################
#                                          Работа с одним туннелем                                                      #
#########################################################################################################################

<#
.SYNOPSIS
    Определяет состояние туннеля средствами его клиента.

.OUTPUTS
    Connected | Disconnected | NotFound | Unavailable
#>
function Get-VpnConnectionState {
    param (
        $Connection,
        [string]$LogFile
    )

    $provider = Get-VpnProvider -Connection $Connection
    if ($null -eq $provider) { return "Unavailable" }

    return & $provider.GetState -Connection $Connection -LogFile $LogFile
}

<#
.SYNOPSIS
    Поднимает один туннель.

.OUTPUTS
    Объект со свойствами:
      Connection        — сам туннель
      Connected         — канал поднят и целевой узел доступен
      AlreadyConnected  — туннель был поднят до запуска пайплайна
                          (такой по завершении не разрывается)
#>
function Connect-VpnConnection {
    param (
        $Connection,
        [string]$LogFile
    )

    $answer = [pscustomobject]@{ Connection = $Connection; Connected = $false; AlreadyConnected = $false }
    $title = Get-VpnConnectionTitle -Connection $Connection

    $provider = Get-VpnProvider -Connection $Connection
    if ($null -eq $provider) {
        $known = ($script:VpnProviders.Keys | Sort-Object) -join ", "
        Write-Log -Message "VPN: у туннеля '$($Connection.Name)' неизвестный клиент '$($Connection.Type)'. Известные: $known" -LogFile $LogFile -Level ERROR
        return $answer
    }

    if ([string]::IsNullOrWhiteSpace((Get-VpnTargetName -Connection $Connection))) {
        Write-Log -Message "VPN: у туннеля '$($Connection.Name)' не задано имя подключения (ключ $($provider.NameProperty))" -LogFile $LogFile -Level ERROR
        return $answer
    }

    if ($provider.NeedsElevation -and -not (Test-VpnElevated)) {
        Write-Log -Message "VPN: туннелю $title нужны права администратора. В задаче Планировщика включите «Выполнить с наивысшими правами»" -LogFile $LogFile -Level ERROR
        return $answer
    }

    if (-not (& $provider.Ready -Connection $Connection -LogFile $LogFile)) { return $answer }

    $state = Get-VpnConnectionState -Connection $Connection -LogFile $LogFile

    if ($state -eq "NotFound") {
        Write-Log -Message "VPN: туннель $title не заведен в клиенте VPN" -LogFile $LogFile -Level ERROR
        return $answer
    }
    if ($state -eq "Unavailable") {
        Write-Log -Message "VPN: не удалось определить состояние туннеля $title" -LogFile $LogFile -Level ERROR
        return $answer
    }

    if ($state -eq "Connected") {
        # Туннель подняли до нас (например, вручную) — по завершении не разрываем
        Write-Log -Message "VPN: туннель $title уже поднят, используем его" -LogFile $LogFile
        $answer.AlreadyConnected = $true
        $answer.Connected = Wait-VpnTarget -Connection $Connection -LogFile $LogFile
        return $answer
    }

    # Одной попытки мало. Клиент рапортует об установленном соединении сразу,
    # но виртуальный адаптер получает адрес по DHCP заметно позже, и с первого
    # раза адрес приходит не всегда: канал остается поднятым и мертвым.
    # Переподключение — то же, что человек делает руками, — это чинит.
    $attempts = [Math]::Max(1, $Connection.ConnectAttempts)

    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        if ($attempt -gt 1) {
            Write-Log -Message "VPN: туннель $title не заработал, пробуем переподключиться (попытка $attempt из $attempts)" -LogFile $LogFile -Level WARN
            Disconnect-VpnConnection -Connection $Connection -LogFile $LogFile
            Start-Sleep -Seconds 3
        }

        $suffix = if ($attempts -gt 1) { " (попытка $attempt из $attempts)" } else { "" }
        Write-Log -Message "VPN: подключаем туннель $title$suffix" -LogFile $LogFile

        if (-not (& $provider.Connect -Connection $Connection -LogFile $LogFile)) { continue }

        # Команда подключения возвращает управление сразу, поэтому ждем фактической
        # установки соединения и доступности целевого узла
        if (Wait-VpnConnected -Connection $Connection -LogFile $LogFile) {
            $answer.Connected = $true
            return $answer
        }
    }

    # Попытки исчерпаны. Оставлять канал поднятым нельзя: пайплайн им
    # не пользуется, а на рабочей станции остается лишний маршрут в чужую сеть.
    Write-Log -Message "VPN: туннель $title не заработал за $attempts $(if ($attempts -eq 1) { 'попытку' } else { 'попытки' }), отключаем поднятое" -LogFile $LogFile -Level WARN
    Disconnect-VpnConnection -Connection $Connection -LogFile $LogFile

    return $answer
}

<#
.SYNOPSIS
    Разрывает один туннель средствами его клиента.
#>
function Disconnect-VpnConnection {
    param (
        $Connection,
        [string]$LogFile
    )

    $title = Get-VpnConnectionTitle -Connection $Connection
    $provider = Get-VpnProvider -Connection $Connection
    if ($null -eq $provider) { return }

    Write-Log -Message "VPN: отключаем туннель $title" -LogFile $LogFile

    try {
        if (& $provider.Disconnect -Connection $Connection -LogFile $LogFile) {
            Write-Log -Message "VPN: туннель $title отключен" -LogFile $LogFile
        }
    } catch {
        Write-Log -Message "VPN: исключение при отключении туннеля $($title): $($_.Exception.Message)" -LogFile $LogFile -Level WARN
    }
}

#########################################################################################################################
#                                          Работа с набором туннелей                                                    #
#########################################################################################################################

<#
.SYNOPSIS
    Поднимает все включенные туннели по порядку.

.DESCRIPTION
    Если обязательный туннель не поднялся, поднятые до него разрываются
    в обратном порядке: оставлять на рабочей станции половину каналов нельзя,
    а разрывать их надо с последнего — маршруты туннелей пересекаются.

.OUTPUTS
    Объект со свойствами:
      Success     — все обязательные туннели подняты
      Connections — результат по каждому туннелю (см. Connect-VpnConnection)
#>
function Connect-PipelineVpn {
    param (
        $Settings,
        [string]$LogFile
    )

    $answer = [pscustomobject]@{ Success = $true; Connections = @() }

    $enabled = @($Settings.Connections | Where-Object { $_.Enabled })
    if ($enabled.Count -eq 0) {
        Write-Log -Message "VPN: подключение включено в настройках, но ни один туннель не отмечен" -LogFile $LogFile -Level ERROR
        $answer.Success = $false
        return $answer
    }

    Write-Log -Message "VPN: туннелей к подключению — $($enabled.Count): $(($enabled | ForEach-Object { $_.Name }) -join ', ')" -LogFile $LogFile

    foreach ($connection in $enabled) {
        $result = Connect-VpnConnection -Connection $connection -LogFile $LogFile
        $answer.Connections += $result

        if ($result.Connected) { continue }

        $title = Get-VpnConnectionTitle -Connection $connection

        if (-not $connection.Required) {
            Write-Log -Message "VPN: туннель $title не поднят, но помечен необязательным — работа продолжается" -LogFile $LogFile -Level WARN
            continue
        }

        # Оставлять поднятой половину каналов нельзя: разрываем то, что успели поднять сами
        $raised = @($answer.Connections | Where-Object { $_.Connected -and -not $_.AlreadyConnected })
        if ($raised.Count -gt 0) {
            Write-Log -Message "VPN: туннель $title обязателен и не поднят, разрываем поднятые до него туннели ($($raised.Count))" -LogFile $LogFile -Level ERROR
            Disconnect-PipelineVpn -Settings $Settings -LogFile $LogFile -State $answer
        } else {
            Write-Log -Message "VPN: туннель $title обязателен и не поднят" -LogFile $LogFile -Level ERROR
        }
        $answer.Success = $false
        return $answer
    }

    return $answer
}

<#
.SYNOPSIS
    Разрывает туннели, поднятые пайплайном. Поднятое не нами не трогается.

.DESCRIPTION
    Порядок обратный порядку подключения: поднятый последним туннель мог
    перехватить маршрут по умолчанию, и разрывать его надо первым.
#>
function Disconnect-PipelineVpn {
    param (
        $Settings,
        [string]$LogFile,
        $State
    )

    if ($null -eq $State) { return }

    $items = @($State.Connections | Where-Object { $_.Connected })

    for ($i = $items.Count - 1; $i -ge 0; $i--) {
        $item = $items[$i]
        $connection = $item.Connection
        $title = Get-VpnConnectionTitle -Connection $connection

        if ($item.AlreadyConnected) {
            Write-Log -Message "VPN: туннель $title был поднят до запуска пайплайна, оставляем включенным" -LogFile $LogFile
            continue
        }
        if (-not $connection.DisconnectAfterRun) {
            Write-Log -Message "VPN: туннель $title оставлен включенным настройкой DisconnectAfterRun" -LogFile $LogFile
            continue
        }

        Disconnect-VpnConnection -Connection $connection -LogFile $LogFile
        $item.Connected = $false
    }
}
