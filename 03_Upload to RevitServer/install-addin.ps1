<#
.SYNOPSIS
    Устанавливает аддин для указанной версии Revit.

.DESCRIPTION
    -Scope AllUsers (по умолчанию, рабочий вариант)
        %ProgramData%\Autodesk\Revit\Addins\<год>\
        Отсюда аддин загружается штатно. Именно поэтому RvsUpload.exe запускает
        Revit БЕЗ journal-файла: с journal Revit грузит только подписанные
        «внутренние» аддины из своей папки установки.

    -Scope RevitInstall
        C:\Program Files\Autodesk\Revit <год>\AddIns\<Имя>\
        НЕ РАБОТАЕТ для нашего аддина. Оставлено как задокументированный тупик,
        чтобы никто не пошёл этим путём заново. Аддины в папке установки Revit
        считает «внутренними» и требует подписи Autodesk, о чём пишет в журнал:
            DBG_WARN: The assembly -RvsUpload.addin- in internal addin
            ...\RvsUpload.Addin.dll is not signed as internal addin.
        Обычным сертификатом код-подписи это не обходится.

.EXAMPLE
    .\install-addin.ps1 -RevitVersion 2021
#>
param(
    [Parameter(Mandatory=$true)][int]$RevitVersion,
    [ValidateSet("AllUsers","RevitInstall")][string]$Scope = "AllUsers",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

if ($Scope -eq "RevitInstall") {
    $root = "C:\Program Files\Autodesk\Revit $RevitVersion\AddIns"
    if (-not (Test-Path $root)) {
        throw "Не найдена папка аддинов Revit: $root. Установлен ли Revit $RevitVersion?"
    }

    # ВАЖНО: в папке установки Revit манифесты лежат В ПОДПАПКАХ, а не в корне.
    # Все 39 манифестов Autodesk устроены так: AddIns\<Имя>\<Имя>.addin рядом
    # с DLL. Манифест, положенный прямо в AddIns\, Revit не подхватывает —
    # проверено на живом Revit 2021.
    #
    # Коллизии имён здесь нет: подпапка RvsUpload и файл RvsUpload.addin
    # внутри неё находятся на разных уровнях.
    $addinsDir = Join-Path $root "RvsUpload"

    $isAdmin = ([Security.Principal.WindowsPrincipal] `
        [Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

    if (-not $isAdmin) {
        throw ("Режим -Scope RevitInstall пишет в каталог установленного Revit " +
               "и требует прав администратора. Запустите PowerShell от имени " +
               "администратора и повторите команду.")
    }
} else {
    $addinsDir = Join-Path $env:ProgramData "Autodesk\Revit\Addins\$RevitVersion"
}

$tfm = if ($RevitVersion -ge 2025) { "net8.0-windows" } else { "net48" }
$buildDir = Join-Path $PSScriptRoot "src\RvsUpload.Addin\bin\$Configuration\$tfm"

if (-not (Test-Path $buildDir)) {
    throw "Не найдены собранные файлы: $buildDir. Сначала выполните: dotnet build -c $Configuration -p:RevitVersion=$RevitVersion"
}

New-Item -ItemType Directory -Force -Path $addinsDir | Out-Null

# Сборка и манифест кладутся рядом, без подпапки.
#
# Подпапка тут невозможна в принципе: она называлась бы RvsUpload.Addin, а
# манифест — RvsUpload.addin, и в Windows эти имена различаются только регистром,
# то есть конфликтуют. Прежняя версия скрипта на этом и падала:
#   "The target file '...\RvsUpload.addin' is a directory, not a file."
# Отдельная папка и не нужна — аддин состоит из единственной сборки
# (JSON берётся из BCL, внешних пакетов нет).
$staleDir = Join-Path $addinsDir "RvsUpload.Addin"
if ((Test-Path $staleDir) -and (Get-Item $staleDir).PSIsContainer) {
    Write-Host "Удаляю папку от прежней схемы установки: $staleDir" -ForegroundColor Yellow
    Remove-Item $staleDir -Recurse -Force
}

# Два манифеста с одним AddInId в разных папках Revit игнорирует как дубликат,
# поэтому подчищаем следы прежних установок в других местах.
$otherLocations = @(
    (Join-Path $env:ProgramData "Autodesk\Revit\Addins\$RevitVersion"),
    (Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"),
    "C:\Program Files\Autodesk\Revit $RevitVersion\AddIns"
) | Where-Object { $_ -ne $addinsDir }

foreach ($loc in $otherLocations) {
    foreach ($name in @("RvsUpload.addin", "RvsUpload.Addin.dll")) {
        $stale = Join-Path $loc $name
        if (Test-Path -LiteralPath $stale -PathType Leaf) {
            Write-Host "Удаляю копию от прежней установки: $stale" -ForegroundColor Yellow
            Remove-Item -LiteralPath $stale -Force
        }
    }
}

Copy-Item (Join-Path $buildDir "RvsUpload.Addin.dll") $addinsDir -Force
Copy-Item (Join-Path $PSScriptRoot "src\RvsUpload.Addin\RvsUpload.addin") $addinsDir -Force

Write-Host "Аддин установлен в $addinsDir" -ForegroundColor Green

Write-Host ""
Write-Host "Если сборка аддина изменилась, при следующем запуске Revit один раз" -ForegroundColor Yellow
Write-Host "спросит про загрузку неподписанной надстройки — ответьте «Всегда загружать»." -ForegroundColor Yellow
Write-Host "Доверие привязано к сборке, поэтому вопрос повторяется после каждой пересборки." -ForegroundColor Yellow
