<#
.SYNOPSIS
    Самопроверка Branding.ps1: подстановка, сохранение BOM, контроль остатков.

.DESCRIPTION
    Запуск (из корня репозитория или из tools):

        powershell -NoProfile -ExecutionPolicy Bypass -File tools\Branding.Tests.ps1

    Без внешних зависимостей (Pester не нужен): синтаксис Pester несовместим
    между версиями 3 и 5, а проверок здесь немного. Работает в Windows
    PowerShell 5.1 и PowerShell 7. Все файлы создаются во временной папке
    и удаляются по завершении. Код возврата: 0 — все проверки пройдены,
    1 — есть провалы.
#>

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Branding.ps1"

$script:passed = 0
$script:failed = 0

function Assert-True {
    param([bool]$Condition, [string]$Name)
    if ($Condition) {
        $script:passed++
        Write-Host "  [OK]   $Name"
    } else {
        $script:failed++
        Write-Host "  [FAIL] $Name" -ForegroundColor Red
    }
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("branding-tests-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null

try {
    # --- Get-Branding -------------------------------------------------------
    Write-Host "Get-Branding"
    $brand = Get-Branding
    Assert-True ($brand.DEV -eq 'ПАНЧИК.РФ') "разработчик читается из branding.psd1"
    Assert-True ($brand.DEV_URL -eq 'bimzen.ru') "сайт разработчика — bimzen.ru"
    Assert-True ($brand.YEAR -eq (Get-Date).Year.ToString()) "год — текущий"

    $brand2 = Get-Branding -Organization 'ООО «Ромашка»'
    Assert-True ($brand2.ORG -eq 'ООО «Ромашка»') "-Organization перекрывает организацию"
    Assert-True ($brand2.DEV -eq $brand.DEV) "-Organization не трогает разработчика"

    # --- Expand-Branding ----------------------------------------------------
    Write-Host "Expand-Branding"
    $utf8Bom   = New-Object Text.UTF8Encoding $true
    $utf8NoBom = New-Object Text.UTF8Encoding $false

    $withBom = Join-Path $tmp 'with-bom.ps1'
    [IO.File]::WriteAllText($withBom, "# Сборка для {{ORG}}, автор {{DEV}}`r`n", $utf8Bom)

    $noBom = Join-Path $tmp 'no-bom.md'
    [IO.File]::WriteAllText($noBom, "Разработчик: {{DEV}} ({{DEV_URL}})", $utf8NoBom)

    $untouched = Join-Path $tmp 'clean.txt'
    [IO.File]::WriteAllText($untouched, "здесь подстановок нет", $utf8NoBom)

    $binary = Join-Path $tmp 'image.png'
    [IO.File]::WriteAllBytes($binary, [byte[]](0x89, 0x50, 0x4E, 0x47, 0x7B, 0x7B))

    $changed = Expand-Branding -Path $tmp -Branding $brand2

    Assert-True ($changed -eq 2) "изменёнными посчитаны ровно 2 файла (фактически: $changed)"

    $bytes = [IO.File]::ReadAllBytes($withBom)
    Assert-True ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) "BOM у .ps1 сохранён"
    $text = [IO.File]::ReadAllText($withBom)
    Assert-True ($text.Contains('ООО «Ромашка»') -and $text.Contains('ПАНЧИК.РФ')) "подстановки в .ps1 выполнены"
    Assert-True (-not $text.Contains('{{')) "остатков подстановок в .ps1 нет"

    $bytes = [IO.File]::ReadAllBytes($noBom)
    Assert-True (-not ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) "файлу без BOM BOM не добавлен"
    Assert-True ([IO.File]::ReadAllText($noBom).Contains('bimzen.ru')) "подстановки в .md выполнены"

    Assert-True ([IO.File]::ReadAllText($untouched) -eq 'здесь подстановок нет') "файл без подстановок не изменён"

    $bytes = [IO.File]::ReadAllBytes($binary)
    Assert-True ($bytes.Length -eq 6 -and $bytes[0] -eq 0x89) "двоичный файл вне Include не тронут"

    # --- Assert-NoBrandingTokens -------------------------------------------
    Write-Host "Assert-NoBrandingTokens"
    Assert-NoBrandingTokens -Path $tmp
    Assert-True $true "чистая поставка проходит проверку"

    $leftover = Join-Path $tmp 'forgotten.md'
    [IO.File]::WriteAllText($leftover, "Забытый токен: {{ORG}}", $utf8NoBom)
    $threw = $false
    try { Assert-NoBrandingTokens -Path $tmp } catch { $threw = $true }
    Assert-True $threw "забытая {{ORG}} роняет проверку"
}
finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Итог: пройдено $script:passed, провалено $script:failed"
if ($script:failed -gt 0) { exit 1 }
exit 0
