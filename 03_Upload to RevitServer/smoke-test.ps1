<#
.SYNOPSIS
    Пошаговая проверка RvsUpload на реальном окружении.

.DESCRIPTION
    Разделено на уровни, потому что каждый следующий требует больше инфраструктуры.
    Если что-то ломается — станет понятно, на каком именно уровне, а не «Revit
    закрылся, ничего не произошло».

      L1  окружение: Revit найден, аддин установлен, GUID совпадают
      L2  Admin REST API: сервер отвечает, папка доступна
      L3  подготовка задания (--dry-run), Revit не запускается
      L4  реальная заливка тестовой модели + проверка через REST + уборка

.EXAMPLE
    .\smoke-test.ps1 -RevitVersion 2024 -Server RVTSRV01 -TestFolder "Sandbox" -Level 3
    .\smoke-test.ps1 -RevitVersion 2024 -Server RVTSRV01 -TestFolder "Sandbox" -Level 4 -SampleModel C:\test\Small.rvt
#>
param(
    [Parameter(Mandatory=$true)][int]$RevitVersion,
    [string]$Server,
    [string]$TestFolder = "RvsUploadSmokeTest",
    [string]$SampleModel,
    [ValidateRange(1,4)][int]$Level = 3,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$cli = "src\RvsUpload\bin\$Configuration\net48\RvsUpload.exe"
$failures = @()

function Check($name, [scriptblock]$block) {
    Write-Host -NoNewline "  $name ... "
    try {
        & $block
        Write-Host "OK" -ForegroundColor Green
    } catch {
        Write-Host "ПРОВАЛ" -ForegroundColor Red
        Write-Host "      $($_.Exception.Message)" -ForegroundColor DarkRed
        $script:failures += $name
    }
}

# ---------------- L1: окружение ----------------
Write-Host "`n[L1] Окружение" -ForegroundColor Cyan

Check "CLI собран" { if (-not (Test-Path $cli)) { throw "Нет $cli. Запустите build.ps1" } }

Check "Revit $RevitVersion установлен" {
    $exe = "C:\Program Files\Autodesk\Revit $RevitVersion\Revit.exe"
    if (-not (Test-Path $exe)) { throw "Не найден $exe" }
}

Check "Аддин установлен" {
    $m = Join-Path $env:ProgramData "Autodesk\Revit\Addins\$RevitVersion\RvsUpload.addin"
    if (-not (Test-Path $m)) { throw "Не найден манифест $m. Запустите install-addin.ps1" }
    $dll = Join-Path $env:ProgramData "Autodesk\Revit\Addins\$RevitVersion\RvsUpload.Addin.dll"
    if (-not (Test-Path $dll)) { throw "Не найдена сборка аддина $dll" }
}

Check "GUID манифеста совпадает с CLI" {
    $m = Join-Path $env:ProgramData "Autodesk\Revit\Addins\$RevitVersion\RvsUpload.addin"
    $installed = ([xml](Get-Content $m)).RevitAddIns.AddIn.AddInId.Trim()
    $source = ([xml](Get-Content "src\RvsUpload.Addin\RvsUpload.addin")).RevitAddIns.AddIn.AddInId.Trim()
    if ($installed -ne $source) {
        throw "Установлен GUID $installed, в исходниках $source — Revit молча ничего не выполнит"
    }
}

Check "--help отрабатывает" {
    & $cli --help | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "--help вернул $LASTEXITCODE" }
}

Check "невалидные аргументы дают код 1" {
    & $cli --source nope.rvt --bogus-flag 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 1) { throw "ожидался код 1, получен $LASTEXITCODE" }
}


# ---------------- L2: Revit Server ----------------
if ($Level -ge 2) {
    Write-Host "`n[L2] Revit Server" -ForegroundColor Cyan

    if (-not $Server) {
        Write-Warning "  -Server не задан, уровень 2+ пропущен"
        $Level = 1
    } else {
        $base = "http://$Server/RevitServerAdminRESTService$RevitVersion/AdminRESTService.svc"
        $headers = @{
            "User-Name"         = $env:USERNAME
            "User-Machine-Name" = $env:COMPUTERNAME
            "Operation-GUID"    = [guid]::NewGuid().ToString()
        }

        Check "сервер отвечает" {
            Invoke-RestMethod -Uri "$base/serverProperties" -Headers $headers -TimeoutSec 30 | Out-Null
        }

        Check "корень читается" {
            # Корень сервера обозначается одиночным '|' ОТДЕЛЬНЫМ сегментом пути.
            # '/|contents' и '/contents' дают 405 Method Not Allowed.
            Invoke-RestMethod -Uri "$base/|/contents" -Headers $headers -TimeoutSec 30 | Out-Null
        }
    }
}

# ---------------- L3: подготовка задания ----------------
if ($Level -ge 3) {
    Write-Host "`n[L3] Подготовка задания (--dry-run)" -ForegroundColor Cyan

    $dest = "RSN://$Server/$TestFolder/SmokeTest.rvt"
    $fakeSource = Join-Path $env:TEMP "rvsupload_smoke_fake.rvt"
    # Записываем байтами через .NET: -Encoding Byte есть только в PS 5.1 и принимает
    # массив байтов, а не строку; в PS 7 параметр вообще удалён (стал -AsByteStream).
    # Раньше здесь ошибка глушилась -ErrorAction SilentlyContinue, файл не создавался,
    # и проверка проходила лишь потому, что рядом стоит --skip-preflight.
    [System.IO.File]::WriteAllBytes($fakeSource, [byte[]][char[]]"not a real rvt")
    if (-not (Test-Path $fakeSource)) { throw "Не удалось создать файл-заглушку $fakeSource" }

    # Journal-файла в схеме нет: с ним Revit не грузит сторонние аддины вовсе.
    # Задание идёт файлом batch.json, путь к нему — переменной окружения.
    # Раньше здесь проверялось наличие ID_APP_EXIT в journal — проверка пережила
    # ту схему и искала файл, который больше не создаётся.
    Check "dry-run проходит и готовит задание" {
        $out = & $cli --source $fakeSource --dest $dest --revit-version $RevitVersion `
                      --skip-preflight --dry-run --keep-temp 2>&1
        if ($LASTEXITCODE -ne 0) { throw "код $LASTEXITCODE`n$out" }

        $workDir = ($out | Select-String "Рабочая папка: (.+)").Matches.Groups[1].Value.Trim()
        $batch = Join-Path $workDir "attempt1\batch.json"
        if (-not (Test-Path $batch)) { throw "Задание не создано: нет $batch" }

        $task = Get-Content $batch -Raw | ConvertFrom-Json
        if (-not $task.LogFile)    { throw "В задании нет LogFile" }
        if (-not $task.ResultFile) { throw "В задании нет ResultFile" }
        if (-not $task.SessionId)  { throw "В задании нет SessionId" }
        if ($task.Tasks.Count -ne 1) { throw "Ожидалось одно задание, в файле $($task.Tasks.Count)" }
        if ($task.Tasks[0].DestinationPath -ne $dest) {
            throw "DestinationPath = '$($task.Tasks[0].DestinationPath)', ожидался '$dest'"
        }
        Write-Host "" ; Write-Host "      задание: $batch" -ForegroundColor DarkGray
    }

    Check "journal НЕ создаётся" {
        # Не придирка: journal-файл ломает загрузку сторонних аддинов, и его
        # возвращение означало бы, что заливка молча перестала работать.
        $out = & $cli --source $fakeSource --dest $dest --revit-version $RevitVersion `
                      --skip-preflight --dry-run --keep-temp 2>&1
        $workDir = ($out | Select-String "Рабочая папка: (.+)").Matches.Groups[1].Value.Trim()
        $stray = Get-ChildItem -Path $workDir -Recurse -Filter "*.txt" -ErrorAction SilentlyContinue
        if ($stray) { throw "В рабочей папке появился journal: $($stray.Name -join ', ')" }
    }

    Check "несуществующий исходник ловится на pre-flight" {
        & $cli --source "C:\definitely\missing.rvt" --dest $dest --revit-version $RevitVersion 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { throw "ожидалась ошибка, получен код 0" }
    }

    Remove-Item $fakeSource -ErrorAction SilentlyContinue
}

# ---------------- L4: реальная заливка ----------------
if ($Level -ge 4) {
    Write-Host "`n[L4] Реальная заливка" -ForegroundColor Cyan

    if (-not $SampleModel -or -not (Test-Path $SampleModel)) {
        Write-Warning "  -SampleModel не задан или не найден — уровень 4 пропущен."
        Write-Warning "  Возьмите МАЛЕНЬКУЮ тестовую модель нужной версии и укажите её путь."
    } else {
        $modelName = "SmokeTest_$(Get-Date -Format yyyyMMdd_HHmmss).rvt"
        $dest = "RSN://$Server/$TestFolder/$modelName"

        # У Revit Server ТРИ разных разделителя пути, и это не описка:
        #   RSN-путь            → '/'   RSN://server/Папка/Модель.rvt
        #   Admin REST API      → '|'   .../Папка|Модель.rvt/modelInfo
        #   RevitServerTool.exe → '\'   "Папка\Модель.rvt"
        # Подстановка одной и той же строки во все три места даёт неверный путь
        # в двух из них — на вложенной папке это и вылезло.
        $restFolder = $TestFolder -replace '/', '|'
        $toolFolder = $TestFolder -replace '/', '\'

        Check "заливка завершается успешно" {
            # --retries 2 здесь не поблажка. Revit падает внутри себя при открытии
            # модели примерно в одном запуске из трёх (captureTryCrash 0xc0000005),
            # и штатный ответ продукта на это — повтор. Прогон без повторов проверял
            # бы конфигурацию, которую мы не поставляем.
            & $cli --source $SampleModel --dest $dest --revit-version $RevitVersion `
                   --create-folders --retries 2 --timeout 30 --keep-temp --log smoke-upload.log
            if ($LASTEXITCODE -ne 0) { throw "код $LASTEXITCODE, подробности в smoke-upload.log" }
        }

        Check "модель видна на сервере через REST" {
            $base = "http://$Server/RevitServerAdminRESTService$RevitVersion/AdminRESTService.svc"
            $headers = @{
                "User-Name" = $env:USERNAME; "User-Machine-Name" = $env:COMPUTERNAME
                "Operation-GUID" = [guid]::NewGuid().ToString()
            }
            Invoke-RestMethod -Uri "$base/$restFolder|$modelName/modelInfo" -Headers $headers | Out-Null
        }

        Check "повторная заливка без --overwrite отклоняется" {
            & $cli --source $SampleModel --dest $dest --revit-version $RevitVersion 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) { throw "ожидался отказ, получен код 0" }
        }

        # RevitServerTool.exe поставлялся с Revit 2020–2024 и в 2025 ОТСУТСТВУЕТ:
        # папки RevitServerToolCommand в установке 2025 нет вовсе. Это не наша
        # поломка и не повод валить прогон — но и молча пропускать нельзя:
        # на 2025+ обратное скачивание надо делать через Revit API
        # (WorksharingUtils.CreateNewLocal), а не через утилиту.
        $rst = "C:\Program Files\Autodesk\Revit $RevitVersion\RevitServerToolCommand\RevitServerTool.exe"
        if (-not (Test-Path $rst)) {
            Write-Warning "  RevitServerTool.exe не поставляется с Revit $RevitVersion — проверка обратного скачивания пропущена."
            Write-Warning "  Для $RevitVersion+ скачивание возможно только через Revit API."
        } else {
        Check "обратное скачивание через RevitServerTool работает" {
            $localOut = Join-Path $env:TEMP "rvsupload_roundtrip"
            # Чистим: остатки прошлого прогона мешают createLocalRVT, а свежесть
            # файла — единственный надёжный признак успеха, см. ниже.
            if (Test-Path $localOut) { Remove-Item $localOut -Recurse -Force }
            New-Item -ItemType Directory -Force -Path $localOut | Out-Null

            $local = Join-Path $localOut $modelName
            $out = & $rst createLocalRVT "$toolFolder\$modelName" -s $Server -d $local -o 2>&1

            # RevitServerTool возвращает 0 даже когда пишет «Cannot create the
            # local model» — по коду возврата судить нельзя. Проверяем факт файла.
            if (-not (Test-Path $local)) {
                throw "Локальная модель не создана.`n$($out -join "`n")"
            }
            $size = (Get-Item $local).Length
            if ($size -lt 1MB) { throw "Скачанный файл подозрительно мал: $size Б" }
            Write-Host "" ; Write-Host "      round-trip: $local ($([math]::Round($size/1MB,1)) МБ)" -ForegroundColor DarkGray
        }
        }

        Write-Host "`n  ВНИМАНИЕ: тестовая модель $dest осталась на сервере — удалите вручную." -ForegroundColor Yellow
    }
}

# ---------------- итог ----------------
Write-Host "`n================================" -ForegroundColor Cyan
if ($failures.Count -eq 0) {
    Write-Host "Все проверки уровня $Level пройдены." -ForegroundColor Green
    exit 0
} else {
    Write-Host "Провалено: $($failures.Count)" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
