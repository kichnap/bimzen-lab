<#
.SYNOPSIS
    Сборка RvsUpload.

.DESCRIPTION
    Собирает Core, CLI и тесты (не требуют Revit), затем аддин под каждую
    указанную версию Revit. Аддин требует установленного Revit соответствующей
    версии — без него сборка этого проекта осмысленно провалится с внятной
    ошибкой из target'а CheckRevitSdk.

    С ключом -Package дополнительно собирает папку поставки в dist\ —
    то, что отдают коллегам: утилита, надстройки под все собранные версии
    Revit, установщик, страница настроек, инструкция в HTML и примеры.

    Ключ -Organization задаёт организацию, для которой собрана поставка:
    её название подставляется в инструкцию, страницу настроек и шапки
    скриптов. Разработчик при этом остаётся один и тот же всегда —
    он прописан в свойствах сборок и подстановке не подлежит.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -RevitVersions 2023,2024,2026
    .\build.ps1 -RevitVersions 2024 -SkipTests
    .\build.ps1 -Package
    .\build.ps1 -Package -Organization "ООО «Ромашка»" -Unit "Отдел BIM"
#>
param(
    [int[]]$RevitVersions = @(),
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$Package,
    # Пусто — берётся умолчание из branding.psd1 в корне репозитория.
    [string]$Organization,
    [string]$Unit
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

function Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

# --- Части, не зависящие от Revit ---
Step "Сборка RvsUpload.Core"
dotnet build src\RvsUpload.Core\RvsUpload.Core.csproj -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Не собрался Core" }

Step "Сборка RvsUpload (CLI)"
dotnet build src\RvsUpload\RvsUpload.csproj -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Не собрался CLI" }

if (-not $SkipTests) {
    Step "Тесты"
    dotnet test tests\RvsUpload.Tests\RvsUpload.Tests.csproj -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Тесты не прошли" }
}

# --- Аддин: по одной сборке на версию Revit ---
if ($RevitVersions.Count -eq 0) {
    Step "Автоопределение установленных версий Revit"
    $RevitVersions = Get-ChildItem "C:\Program Files\Autodesk" -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^Revit (\d{4})$' -and (Test-Path (Join-Path $_.FullName "RevitAPI.dll")) } |
        ForEach-Object { [int]($_.Name -replace 'Revit ', '') } |
        Sort-Object

    if ($RevitVersions.Count -eq 0) {
        Write-Warning "Revit не найден — аддин не собран. Собраны только Core, CLI и тесты."
        Write-Host "`nГотово (без аддина)." -ForegroundColor Yellow
        return
    }
    Write-Host "Найдено: $($RevitVersions -join ', ')"
}

foreach ($v in $RevitVersions) {
    Step "Сборка аддина под Revit $v"
    dotnet build src\RvsUpload.Addin\RvsUpload.Addin.csproj -c $Configuration -p:RevitVersion=$v
    if ($LASTEXITCODE -ne 0) { throw "Не собрался аддин для Revit $v" }
}

Step "Итог"
Write-Host "CLI:   src\RvsUpload\bin\$Configuration\net48\RvsUpload.exe"
foreach ($v in $RevitVersions) {
    $tfm = if ($v -ge 2025) { "net8.0-windows" } else { "net48" }
    Write-Host "Аддин Revit ${v}: src\RvsUpload.Addin\bin\$Configuration\$tfm\RvsUpload.Addin.dll"
}

if (-not $Package) {
    Write-Host "`nДалее: .\install-addin.ps1 -RevitVersion <год>" -ForegroundColor Green
    Write-Host "Папка для передачи коллегам: .\build.ps1 -Package" -ForegroundColor Gray
    return
}

# ============================================================================
# Поставка
#
# Собирается из того, что только что собрано, а не из заранее сложенных
# файлов: иначе однажды уедет версия одной из частей и заметят это у коллеги.
# Папка каждый раз пересоздаётся с нуля — остатки прошлой сборки в поставку
# попадать не должны.
# ============================================================================

Step "Сборка поставки"

. "$PSScriptRoot\..\tools\Branding.ps1"
$brand = Get-Branding -Organization $Organization -Unit $Unit
Write-Host "  Организация: $($brand.ORG), $($brand.UNIT)"
Write-Host "  Разработчик: $($brand.DEV)"

$version = ([xml](Get-Content "Directory.Build.props")).Project.PropertyGroup.Version
if (-not $version) { throw "Не удалось прочитать версию из Directory.Build.props" }

$dist = Join-Path $PSScriptRoot "dist\RvsUpload-$version"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist, "$dist\bin", "$dist\bin\addins", "$dist\Примеры" | Out-Null

# --- Утилита ---
$cliDir = "src\RvsUpload\bin\$Configuration\net48"
foreach ($f in @("RvsUpload.exe", "RvsUpload.exe.config", "RvsUpload.Core.dll")) {
    $p = Join-Path $cliDir $f
    if (Test-Path $p) { Copy-Item $p "$dist\bin" -Force }
}
Write-Host "  bin\RvsUpload.exe"

# --- Надстройки: по папке на СРЕДУ ВЫПОЛНЕНИЯ, а не на версию Revit ---
#
# Раскладка по годам выглядела бы аккуратнее, но врала бы: сборки под
# 2021–2024 получаются байт в байт одинаковыми. Так и должно быть —
# RevitAPI.dll не подписана строгим именем, поэтому версия ссылки при
# загрузке не проверяется, и одна сборка работает во всех этих Revit.
# Проверено живыми прогонами на 2022, 2023 и 2024 одним и тем же файлом.
#
# Разных сборок ровно две, по числу сред: net48 и net8.
foreach ($tfm in @("net48", "net8.0-windows")) {
    $src = "src\RvsUpload.Addin\bin\$Configuration\$tfm\RvsUpload.Addin.dll"
    if (-not (Test-Path $src)) { continue }
    $имя = if ($tfm -eq "net48") { "net48" } else { "net8" }
    $dstDir = "$dist\bin\addins\$имя"
    New-Item -ItemType Directory -Force -Path $dstDir | Out-Null
    Copy-Item $src $dstDir -Force
    Copy-Item "src\RvsUpload.Addin\RvsUpload.addin" $dstDir -Force
    $годы = if ($имя -eq "net48") { "Revit 2021–2024" } else { "Revit 2025 и новее" }
    Write-Host "  bin\addins\$имя  ($годы)"
}

# --- Установщик и страница настроек ---
Copy-Item "tools\Установить.ps1" $dist -Force
Copy-Item "Settings.html" "$dist\Настройки.html" -Force
Write-Host "  Установить.ps1"
Write-Host "  Настройки.html"

# --- Документация из markdown ---
& "$PSScriptRoot\tools\md2html.ps1" -Source "Инструкция.md" -Target "$dist\Инструкция.html"

# --- Примеры ---
Copy-Item "settings.template.json" "$dist\Примеры\settings.пример.json" -Force

$примеры = @{
    "1 Проверка без заливки.cmd" = @'
@echo off
rem Ничего не заливает: проверяет доступность сервера, длину имён и версию
rem модели. Revit при этом не запускается. С этого стоит начинать всегда.
rem
rem Пути в примерах латиницей намеренно: строка команды в .cmd читается
rem в кодировке консоли, и кириллица в ней ломается, если кодировка не та.
rem Свои пути с кириллицей надёжнее подставлять в .ps1 рядом.
cd /d "%~dp0.."

bin\RvsUpload.exe ^
  --source "C:\Models\Example.rvt" ^
  --dest "RSN://SERVER/Folder/Example.rvt" ^
  --revit-version 2024 ^
  --dry-run

pause
'@
    "2 Одна модель.cmd" = @'
@echo off
rem Заливка одной модели. Без --overwrite заливка поверх существующей
rem отклоняется - так случайный повтор не затрёт чужую работу.
cd /d "%~dp0.."

bin\RvsUpload.exe ^
  --source "C:\Models\Example.rvt" ^
  --dest "RSN://SERVER/Folder/Example.rvt" ^
  --revit-version 2024 ^
  --overwrite ^
  --retries 2 ^
  --log "%~dp0..\upload.log"

pause
'@
    "3 Пакет по списку.cmd" = @'
@echo off
rem Пакет заливается за ОДИН запуск Revit - поэтому он заметно быстрее,
rem чем те же модели по одной. Список - models.example.txt рядом.
cd /d "%~dp0.."

bin\RvsUpload.exe ^
  --list "%~dp0models.example.txt" ^
  --dest-folder "RSN://SERVER/Projects/2026" ^
  --revit-version 2024 ^
  --overwrite ^
  --create-folders ^
  --retries 2 ^
  --log "%~dp0..\upload.log"

pause
'@
    "4 Папка целиком.cmd" = @'
@echo off
rem Список поддерживать не нужно: что лежит в папке, то и заливается.
rem Резервные копии Revit и папки *_backup пропускаются.
rem
rem --structure задаёт, куда лягут модели:
rem   flat    всё в одну папку (по умолчанию)
rem   disk    повторить структуру подпапок с диска
rem   server  положить туда, где модель УЖЕ лежит на сервере
cd /d "%~dp0.."

bin\RvsUpload.exe ^
  --source-folder "C:\Export" ^
  --dest-folder "RSN://SERVER/Projects/2026" ^
  --structure disk ^
  --revit-version 2024 ^
  --overwrite ^
  --create-folders ^
  --retries 2 ^
  --log "%~dp0..\upload.log"

pause
'@
    "5 Вернуть модели на прежние места.cmd" = @'
@echo off
rem Модели скачали с сервера, обработали, и надо вернуть их ТУДА ЖЕ.
rem На диске они при этом могут лежать как угодно: утилита обходит дерево
rem сервера начиная с --dest-folder и ищет модель по имени.
rem
rem Что не нашлось или нашлось в двух папках - отбраковывается с записью
rem в лог. Остальные модели пакета заливаются.
cd /d "%~dp0.."

bin\RvsUpload.exe ^
  --source-folder "C:\Processed" ^
  --dest-folder "RSN://SERVER/Projects/2026" ^
  --structure server ^
  --revit-version 2024 ^
  --overwrite ^
  --retries 2 ^
  --log "%~dp0..\upload.log"

pause
'@
    "6 По файлу настроек.cmd" = @'
@echo off
rem Когда набор ключей устоялся: соберите settings.json страницей
rem Настройки.html и держите его ВНЕ папки утилиты - тогда обновление
rem утилиты его не затронет.
cd /d "%~dp0.."

bin\RvsUpload.exe --config "C:\BIM\settings.json"

pause
'@
}
# Кодировка .cmd — CP866 (OEM) без BOM, и chcp 866 первой строкой.
#
# BOM в начале .bat/.cmd интерпретатор командной строки выполняет как часть
# первой команды: пользователь получает «"я╗┐@echo" не является внутренней или
# внешней командой» и дальше всё сыплется.
#
# UTF-8 внутри .cmd нельзя, даже без BOM. Файл читается в кодировке консоли,
# и при 65001 интерпретатор сбивается на многобайтовых строках: позицию в файле
# он запоминает в байтах, а читает в символах, и следующая строка обрезается
# с середины. Именно так «bin\RvsUpload.exe ^» превращалось в «ce "C:\...\...rvt"».
#
# CP866 однобайтовая, сбоя нет, и консоль русской Windows по умолчанию тоже
# в 866 — кириллица в rem читается верно.
#
# chcp внутри файла НЕ ставим, хотя соблазн есть: после смены кодовой страницы
# интерпретатор теряет позицию в файле и обрезает следующую строку. Проверено:
# с «chcp 866» первой строкой ломался комментарий сразу под ней.
#
# Аргументы команд записаны латиницей намеренно. Если у окна кодовая страница
# не 866 (например 65001), комментарии превратятся в мусор, но сама команда
# останется читаемой и выполнится. Для такого случая рядом лежат .ps1 —
# им кодовая страница консоли безразлична.
$oem = [Text.Encoding]::GetEncoding(866)
foreach ($имя in $примеры.Keys) {
    # Переводы строк — обязательно CRLF. В here-string они такие же, как
    # в самом build.ps1, а он под git лежит с LF. Пакетный файл с LF
    # интерпретатор читает буфером по 512 байт и на границе буфера
    # перескакивает середину строки: в примере длиннее 512 байт из
    # комментария вываливался обрывок и выполнялся как команда.
    $текст = $примеры[$имя] -replace "`r?`n", "`r`n"

    # В CP866 нет длинного тире и кавычек-ёлочек: молча превратились бы в «?».
    # Лучше уронить сборку, чем отдать пользователю пример с мусором.
    if ($oem.GetString($oem.GetBytes($текст)) -ne $текст) {
        throw "Пример «$имя» содержит символы, которых нет в CP866. Замените длинное тире на дефис, ёлочки на обычные кавычки."
    }

    [IO.File]::WriteAllBytes("$dist\Примеры\$имя", $oem.GetBytes($текст))
}

# То же самое на PowerShell. Не дубль ради полноты: .cmd удобно запускать
# двойным щелчком, а из Планировщика заданий и из других скриптов вызывают
# PowerShell, и переписывать примеры на ходу — лишний повод ошибиться.
$примерыPs = @{
    "1 Проверка без заливки.ps1" = @'
# Ничего не заливает: проверяет доступность сервера, длину имён и версию
# модели. Revit при этом не запускается. С этого стоит начинать всегда.
$ErrorActionPreference = "Stop"
$exe = Join-Path $PSScriptRoot "..\bin\RvsUpload.exe"

& $exe --source "C:\Модели\Пример.rvt" `
       --dest "RSN://СЕРВЕР/Папка/Пример.rvt" `
       --revit-version 2024 `
       --dry-run

exit $LASTEXITCODE
'@
    "2 Одна модель.ps1" = @'
# Заливка одной модели. Без --overwrite заливка поверх существующей
# отклоняется — так случайный повтор не затрёт чужую работу.
$ErrorActionPreference = "Stop"
$exe = Join-Path $PSScriptRoot "..\bin\RvsUpload.exe"

& $exe --source "C:\Модели\Пример.rvt" `
       --dest "RSN://СЕРВЕР/Папка/Пример.rvt" `
       --revit-version 2024 `
       --overwrite `
       --retries 2 `
       --log (Join-Path $PSScriptRoot "..\upload.log")

exit $LASTEXITCODE
'@
    "3 Пакет по списку.ps1" = @'
# Пакет заливается за ОДИН запуск Revit — поэтому он заметно быстрее,
# чем те же модели по одной. Список — models.example.txt рядом.
$ErrorActionPreference = "Stop"
$exe = Join-Path $PSScriptRoot "..\bin\RvsUpload.exe"

& $exe --list (Join-Path $PSScriptRoot "models.example.txt") `
       --dest-folder "RSN://СЕРВЕР/Проекты/2026" `
       --revit-version 2024 `
       --overwrite `
       --create-folders `
       --retries 2 `
       --log (Join-Path $PSScriptRoot "..\upload.log")

exit $LASTEXITCODE
'@
    "4 Папка целиком.ps1" = @'
# Список поддерживать не нужно: что лежит в папке, то и заливается.
# Резервные копии Revit и папки *_backup пропускаются.
#
# --structure задаёт, куда лягут модели:
#   flat    всё в одну папку (по умолчанию)
#   disk    повторить структуру подпапок с диска
#   server  положить туда, где модель УЖЕ лежит на сервере
$ErrorActionPreference = "Stop"
$exe = Join-Path $PSScriptRoot "..\bin\RvsUpload.exe"

& $exe --source-folder "C:\Выгрузка" `
       --dest-folder "RSN://СЕРВЕР/Проекты/2026" `
       --structure disk `
       --revit-version 2024 `
       --overwrite `
       --create-folders `
       --retries 2 `
       --log (Join-Path $PSScriptRoot "..\upload.log")

exit $LASTEXITCODE
'@
    "5 Вернуть модели на прежние места.ps1" = @'
# Модели скачали с сервера, обработали, и надо вернуть их ТУДА ЖЕ.
# На диске они при этом могут лежать как угодно: утилита обходит дерево
# сервера начиная с --dest-folder и ищет модель по имени.
#
# Что не нашлось или нашлось в двух папках — отбраковывается с записью
# в лог. Остальные модели пакета заливаются: одна непонятная не отменяет
# всех.
$ErrorActionPreference = "Stop"
$exe = Join-Path $PSScriptRoot "..\bin\RvsUpload.exe"

& $exe --source-folder "C:\Обработано" `
       --dest-folder "RSN://СЕРВЕР/Проекты/2026" `
       --structure server `
       --revit-version 2024 `
       --overwrite `
       --retries 2 `
       --log (Join-Path $PSScriptRoot "..\upload.log")

exit $LASTEXITCODE
'@
    "6 По файлу настроек.ps1" = @'
# Когда набор ключей устоялся: соберите settings.json страницей
# Настройки.html и держите его ВНЕ папки утилиты — тогда обновление
# утилиты его не затронет.
$ErrorActionPreference = "Stop"
$exe = Join-Path $PSScriptRoot "..\bin\RvsUpload.exe"

& $exe --config "C:\BIM\settings.json"

exit $LASTEXITCODE
'@
}
foreach ($имя in $примерыPs.Keys) {
    # Строку запуска кладём в сам файл: в именах примеров есть пробелы,
    # и без кавычек «powershell -File» обрывает путь на первом пробеле,
    # отвечая «файл имеет расширение, отличное от PS1».
    $заголовок = @(
        "# Запуск из командной строки или Планировщика заданий:",
        "#   powershell -ExecutionPolicy Bypass -File `"$имя`"",
        "# Кавычки обязательны: в имени файла есть пробелы.",
        "# Двойным щелчком: правой кнопкой -> Выполнить с помощью PowerShell.",
        ""
    ) -join [Environment]::NewLine

    # UTF-8 с BOM обязателен: без него PowerShell 5.1 читает файл как CP1251
    # и кириллица в путях превращается в мусор.
    [IO.File]::WriteAllText("$dist\Примеры\$имя", $заголовок + $примерыPs[$имя], (New-Object Text.UTF8Encoding $true))
}

$список = @'
C:\Выгрузка\АР.rvt
C:\Выгрузка\КР.rvt
C:\Выгрузка\ОВ_корпус_2_стадия_П_ревизия_7.rvt|ОВ.rvt
'@
[IO.File]::WriteAllText("$dist\Примеры\models.example.txt", $список, (New-Object Text.UTF8Encoding $true))
Write-Host ("  Примеры\ ($($примеры.Count) на .cmd + $($примерыPs.Count) на .ps1 " +
            "+ список + настройки)")

# --- Первый файл, который откроют ---
$читать = @"
RvsUpload $version — заливка моделей на Revit Server

С ЧЕГО НАЧАТЬ
  1. Правой кнопкой по «Установить.ps1» → Выполнить с помощью PowerShell.
  2. Запустить Revit вручную и на вопрос про неподписанную надстройку
     ответить «Всегда загружать». Шаг обязательный: без него Revit
     не загрузит ни одной сторонней надстройки.
  3. Открыть «Инструкция.html».

ЧТО ЗДЕСЬ ЛЕЖИТ
  Установить.ps1     ставит надстройку во все найденные Revit
  Инструкция.html    как пользоваться, что делать при ошибках
  Настройки.html     собрать settings.json, открывается двойным щелчком
  Примеры\           готовые команды, поправьте пути в блокноте.
                     Один и тот же набор в двух видах: .cmd для запуска
                     двойным щелчком и .ps1 для Планировщика заданий.
                     В именах есть пробелы: в команде запуска .ps1
                     имя файла берите в кавычки
  bin\               сама утилита и надстройки

ПРОВЕРИТЬ, ЧТО ВСЁ РАБОТАЕТ
  Примеры\1 Проверка без заливки.cmd — ничего не заливает.

ЕСЛИ НЕ ЗАРАБОТАЛО
  Сообщение «Revit не дошёл до аддина» почти всегда означает шаг 2.

Собрано $(Get-Date -Format 'dd.MM.yyyy'). Проверено на Revit 2021-2026.
Собрано для: {{ORG}}, {{UNIT}}
Разработчик: {{DEV}}, {{DEV_URL}}
"@
[IO.File]::WriteAllText("$dist\Начать отсюда.txt", $читать, (New-Object Text.UTF8Encoding $true))
Write-Host "  Начать отсюда.txt"

# --- Подстановка организации ---
#
# Делается ОДИН раз и по всей папке сразу, а не при копировании каждого
# файла: так невозможно забыть новый файл поставки. Проверка после
# подстановки обязательна — {{ORG}}, доехавшая до заказчика, выглядит
# хуже, чем упавшая сборка.
$заменено = Expand-Branding -Path $dist -Branding $brand
Assert-NoBrandingTokens -Path $dist
Write-Host "  подстановка организации: файлов изменено $заменено"

# --- Архив ---
$zip = "$PSScriptRoot\dist\RvsUpload-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$dist\*" -DestinationPath $zip
$размер = [Math]::Round((Get-Item $zip).Length / 1MB, 1)

Step "Поставка готова"
Write-Host "  Папка: dist\RvsUpload-$version" -ForegroundColor Green
Write-Host "  Архив: dist\RvsUpload-$version.zip ($размер МБ)" -ForegroundColor Green
Write-Host "`nОтдавать коллегам можно архив: внутри всё нужное, ставить больше нечего." -ForegroundColor Gray
