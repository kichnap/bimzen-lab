<#
.SYNOPSIS
    Устанавливает надстройку RvsUpload во все найденные версии Revit.

.DESCRIPTION
    Этот файл предназначен для готовой поставки: он лежит рядом с папкой bin
    и работает на машине, где нет ни исходников, ни dotnet.

    Что делает:
      1. находит установленные Revit;
      2. кладёт надстройку в %ProgramData%\Autodesk\Revit\Addins\<год>;
      3. убирает копии от прежних установок, иначе Revit сочтёт их дубликатом
         и не загрузит ни одну;
      4. проверяет, что получилось, и говорит, что делать дальше.

    Прав администратора не требует: %ProgramData% доступен на запись.
    В системе больше ничего не меняется — ни реестра, ни служб, ни переменных.

.EXAMPLE
    .\Установить.ps1
    .\Установить.ps1 -RevitVersions 2024,2025
    .\Установить.ps1 -Удалить
#>
param(
    [int[]]$RevitVersions = @(),
    [switch]$Удалить
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
Set-Location $PSScriptRoot

function Строка($t, $c = "Gray") { Write-Host $t -ForegroundColor $c }
function Заголовок($t) { Write-Host ""; Write-Host $t -ForegroundColor Cyan }

$addinsRoot = Join-Path $env:ProgramData "Autodesk\Revit\Addins"
$binRoot = Join-Path $PSScriptRoot "bin\addins"

# --- Какие версии Revit есть на машине ------------------------------------
Заголовок "Ищу установленные Revit"

$установленные = Get-ChildItem "C:\Program Files\Autodesk" -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^Revit (\d{4})$' -and (Test-Path (Join-Path $_.FullName "Revit.exe")) } |
    ForEach-Object { [int]($_.Name -replace 'Revit ', '') } |
    Sort-Object

if ($установленные.Count -eq 0) {
    Строка "  Revit на этой машине не найден." Red
    Строка "  Утилите нужен установленный Revit — ставить надстройку некуда." Red
    exit 1
}
Строка ("  Найдено: " + ($установленные -join ", "))

if ($RevitVersions.Count -gt 0) {
    $пропущены = $RevitVersions | Where-Object { $_ -notin $установленные }
    foreach ($v in $пропущены) { Строка "  Revit $v не установлен — пропускаю." Yellow }
    $цель = $RevitVersions | Where-Object { $_ -in $установленные }
} else {
    $цель = $установленные
}

# --- Удаление --------------------------------------------------------------
if ($Удалить) {
    Заголовок "Удаляю надстройку"
    foreach ($v in $цель) {
        $dir = Join-Path $addinsRoot $v
        foreach ($n in @("RvsUpload.addin", "RvsUpload.Addin.dll")) {
            $p = Join-Path $dir $n
            if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force; Строка "  убрано: $p" }
        }
    }
    Строка "`nГотово. Настройки и журналы не тронуты." Green
    exit 0
}

# --- Что есть в поставке ---------------------------------------------------
if (-not (Test-Path $binRoot)) {
    Строка "  Не найдена папка $binRoot" Red
    Строка "  Похоже, поставка распакована не полностью." Red
    exit 1
}

# Надстроек в поставке две, по числу сред выполнения: Revit до 2024
# включительно работает на .NET Framework, с 2025 — на .NET 8. Внутри
# среды сборка одна на все годы, и это не упрощение: RevitAPI.dll
# не подписана строгим именем, версия ссылки при загрузке не проверяется.
function Среда([int]$v) { if ($v -ge 2025) { "net8" } else { "net48" } }

$впоставке = Get-ChildItem $binRoot -Directory | ForEach-Object { $_.Name }
Строка ("  В поставке: " + ($впоставке -join ", ") +
        "  (net48 — Revit 2021–2024, net8 — 2025 и новее)")

$нет = $цель | Where-Object { (Среда $_) -notin $впоставке }
foreach ($v in $нет) {
    Строка "  Revit ${v}: в поставке нет надстройки под $(Среда $v) — пропускаю." Yellow
}
$цель = $цель | Where-Object { (Среда $_) -in $впоставке }

$непроверенные = $цель | Where-Object { $_ -lt 2021 -or $_ -gt 2026 }
foreach ($v in $непроверенные) {
    Строка "  Revit ${v}: поставлю, но на этой версии утилита не проверялась." Yellow
}

if ($цель.Count -eq 0) {
    Строка "`nСтавить нечего: поставка не покрывает установленные версии Revit." Red
    exit 1
}

# --- Запущенный Revit держит файл надстройки ------------------------------
#
# Заменить сборку под работающим Revit нельзя, и это не досадная мелочь:
# у человека в этом сеансе может быть несохранённая работа. Поэтому мы
# не закрываем Revit сами, а называем версии и просим закрыть.
Заголовок "Проверяю, не запущен ли Revit"

$занятые = @()
foreach ($p in @(Get-Process Revit -ErrorAction SilentlyContinue)) {
    try { $путь = $p.MainModule.FileName } catch { $путь = "" }
    if ($путь -match 'Revit (\d{4})\\Revit\.exe') { $занятые += [int]$Matches[1] }
    elseif ($путь) { $занятые += 0 }
}
$занятые = $занятые | Sort-Object -Unique

if ($занятые.Count) {
    $имена = $занятые | ForEach-Object { if ($_) { "Revit $_" } else { "Revit (версия не определена)" } }
    Строка ("  Запущен: " + ($имена -join ", ")) Yellow
    $конфликт = $цель | Where-Object { $_ -in $занятые }
    if ($занятые -contains 0) { $конфликт = $цель }
    if ($конфликт) {
        Строка ""
        Строка "  Закройте Revit и запустите установку заново." Red
        Строка "  Пока он работает, файл надстройки заменить нельзя, а завершать" Red
        Строка "  его за вас нельзя тем более: в сеансе может быть несохранённая работа." Red
        exit 1
    }
    Строка "  Версий, которые ставим, среди запущенных нет — продолжаю." Gray
} else {
    Строка "  Не запущен." Gray
}

# --- Установка -------------------------------------------------------------
Заголовок "Ставлю надстройку"

foreach ($v in $цель) {
    $откуда = Join-Path $binRoot (Среда $v)
    $куда = Join-Path $addinsRoot $v
    New-Item -ItemType Directory -Force -Path $куда | Out-Null

    # Два манифеста с одним идентификатором в разных папках Revit считает
    # дубликатом и не грузит ни один. Подчищаем следы прежних установок.
    $другие = @(
        (Join-Path $env:APPDATA "Autodesk\Revit\Addins\$v"),
        "C:\Program Files\Autodesk\Revit $v\AddIns"
    )
    foreach ($loc in $другие) {
        foreach ($n in @("RvsUpload.addin", "RvsUpload.Addin.dll")) {
            $stale = Join-Path $loc $n
            if (Test-Path -LiteralPath $stale -PathType Leaf) {
                Remove-Item -LiteralPath $stale -Force -ErrorAction SilentlyContinue
                Строка "  убрана копия от прежней установки: $stale" Yellow
            }
        }
    }

    Copy-Item (Join-Path $откуда "RvsUpload.Addin.dll") $куда -Force
    Copy-Item (Join-Path $откуда "RvsUpload.addin") $куда -Force
    Строка "  Revit ${v}: $куда" Green
}

# --- Проверка --------------------------------------------------------------
Заголовок "Проверяю"

$ошибки = 0
foreach ($v in $цель) {
    $куда = Join-Path $addinsRoot $v
    foreach ($n in @("RvsUpload.addin", "RvsUpload.Addin.dll")) {
        if (-not (Test-Path (Join-Path $куда $n))) {
            Строка "  Revit ${v}: нет файла $n" Red
            $ошибки++
        }
    }
}

$exe = Join-Path $PSScriptRoot "bin\RvsUpload.exe"
if (-not (Test-Path $exe)) { Строка "  Не найден $exe" Red; $ошибки++ }

if ($ошибки) { Строка "`nУстановка прошла не полностью: ошибок $ошибки." Red; exit 1 }
Строка "  Всё на месте." Green

# --- Что дальше ------------------------------------------------------------
Write-Host ""
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host " ОСТАЛСЯ ОДИН ШАГ, И ОН ОБЯЗАТЕЛЬНЫЙ" -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host " Запустите Revit вручную. Он спросит про загрузку неподписанной"
Write-Host " надстройки — ответьте " -NoNewline
Write-Host "«Всегда загружать»" -ForegroundColor Green -NoNewline
Write-Host "."
Write-Host ""
Write-Host " Пока ответа нет, Revit не грузит НИ ОДНУ стороннюю надстройку,"
Write-Host " и заливка будет завершаться сообщением «Revit не дошёл до аддина»."
Write-Host ""
Write-Host " Дальше: откройте Инструкция.html или Настройки.html." -ForegroundColor Gray
Write-Host ""
Write-Host " Собрано для: {{ORG}}, {{UNIT}}" -ForegroundColor DarkGray
Write-Host " Разработчик: {{DEV}}, {{DEV_URL}}" -ForegroundColor DarkGray
Write-Host ""
