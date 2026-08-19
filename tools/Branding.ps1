<#
.SYNOPSIS
    Подстановка названия организации в собранную поставку.

.DESCRIPTION
    Один продукт уходит в несколько организаций, и в каждой поставке должно
    стоять её название — в инструкции, на странице настроек и в шапках
    скриптов. При этом разработчик остаётся один и тот же всегда.

    Файл подключается точкой из build.ps1 любого проекта:

        . "$PSScriptRoot\..\tools\Branding.ps1"
        $brand = Get-Branding -Organization $Organization
        Expand-Branding -Path $dist -Branding $brand

    В ИСХОДНИКАХ стоят подстановки, а не готовые названия. Так видно, что
    строка меняется при сборке, и так её невозможно пропустить: забытая
    подстановка бросается в глаза, а забытая замена «по месту» — нет.

        {{ORG}}       организация, для которой собрана поставка
        {{UNIT}}      подразделение
        {{DEV}}       разработчик — всегда один и тот же
        {{DEV_URL}}   сайт разработчика
        {{YEAR}}      год сборки

    Ключ -Organization меняет только {{ORG}}. Заменить {{DEV}} нельзя
    намеренно: авторство продукта не является настройкой сборки.
#>

function Get-Branding {
    [CmdletBinding()]
    param(
        # Организация, для которой собирается поставка. Пусто — берём умолчание
        # из branding.psd1.
        [string]$Organization,
        [string]$Unit,
        # Путь к branding.psd1. По умолчанию — в корне репозитория, рядом
        # с папкой tools.
        [string]$BrandingFile
    )

    if (-not $BrandingFile) {
        $BrandingFile = Join-Path (Split-Path -Parent $PSScriptRoot) "branding.psd1"
    }
    if (-not (Test-Path $BrandingFile)) {
        throw "Не найден branding.psd1: $BrandingFile"
    }

    $data = Import-PowerShellDataFile -Path $BrandingFile

    foreach ($k in @('Developer', 'DeveloperUrl', 'Organization', 'Unit')) {
        if (-not $data.$k) { throw "В branding.psd1 не задан ключ $k" }
    }

    @{
        'ORG'     = if ($Organization) { $Organization } else { $data.Organization }
        'UNIT'    = if ($Unit)         { $Unit }         else { $data.Unit }
        'DEV'     = $data.Developer
        'DEV_URL' = $data.DeveloperUrl
        'YEAR'    = (Get-Date).Year.ToString()
    }
}

function Expand-Branding {
    [CmdletBinding()]
    param(
        # Папка поставки или отдельный файл.
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][hashtable]$Branding,
        # Что считаем текстом. Двоичные файлы трогать нельзя.
        [string[]]$Include = @('*.ps1','*.psd1','*.psm1','*.html','*.htm','*.md','*.txt','*.cmd','*.bat','*.json','*.xml','*.addin')
    )

    $files = if (Test-Path -LiteralPath $Path -PathType Container) {
        Get-ChildItem -LiteralPath $Path -Recurse -File -Include $Include
    } else {
        @(Get-Item -LiteralPath $Path)
    }

    $changed = 0
    foreach ($f in $files) {
        $bytes = [IO.File]::ReadAllBytes($f.FullName)
        $bom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)

        $text = [Text.Encoding]::UTF8.GetString($bytes)
        if ($bom) { $text = $text.Substring(1) }

        $new = $text
        foreach ($k in $Branding.Keys) { $new = $new.Replace("{{$k}}", $Branding[$k]) }
        if ($new -eq $text) { continue }

        # BOM сохраняем как был: у .ps1 он обязателен — без него PowerShell 5.1
        # читает файл как CP1251 и кириллица в строках превращается в мусор.
        [IO.File]::WriteAllText($f.FullName, $new, (New-Object Text.UTF8Encoding $bom))
        $changed++
    }
    $changed
}

function Assert-NoBrandingTokens {
    <#
        Проверка после подстановки: в готовой поставке подстановок остаться
        не должно. Незамеченная {{ORG}} в инструкции у заказчика выглядит
        хуже, чем упавшая сборка.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [string[]]$Include = @('*.ps1','*.psd1','*.psm1','*.html','*.htm','*.md','*.txt','*.cmd','*.bat','*.json','*.xml','*.addin')
    )

    $leftovers = Get-ChildItem -LiteralPath $Path -Recurse -File -Include $Include |
        Select-String -Pattern '\{\{[A-Z_]+\}\}' -AllMatches

    if ($leftovers) {
        $list = $leftovers | ForEach-Object { "  $($_.Filename):$($_.LineNumber)  $($_.Matches[0].Value)" }
        throw ("В поставке остались неподставленные значения:`n" + ($list -join "`n"))
    }
}
