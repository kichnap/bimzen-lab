<#
    Сборка релиза пайплайна DownloadAndCopyRVTNWC для передачи контрагентам.

    Что делает:
      - копирует исходники из src\ в dist\;
      - удаляет из .ps1 и Settings.html все комментарии (код остаётся читаемым —
        имена и структура не меняются, поэтому антивирусы к сборке не придираются);
      - добавляет в начало каждого файла лицензионный заголовок;
      - заново собирает docs\Инструкция.pdf из docs\Инструкция.md.

    Это наш инструмент разработки, в поставку он НЕ входит. Исходники в src\
    остаются с комментариями — правим всегда их, а не dist\.

    Запуск:  powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
#>

param(
    # Пусто — версия берётся из $pipelineVersion в 00_Start_RunScript.ps1.
    # Держать её здесь вторым списком нельзя: копия рассинхронизируется молча,
    # и в поставку уедет лицензионный заголовок с чужим номером версии.
    [string]$Version,
    # Организация, для которой собирается поставка. Пусто — умолчание
    # из branding.psd1 в корне репозитория. Разработчик этим ключом
    # не меняется: организаций, для которых собирают поставку, много,
    # а автор один.
    [string]$Organization,
    [string]$Unit
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir    = Join-Path $scriptDir "src"
$docsDir   = Join-Path $scriptDir "docs"
$distDir   = Join-Path $scriptDir "dist"
$buildDate = Get-Date -Format "dd.MM.yyyy"

$utf8Bom   = New-Object System.Text.UTF8Encoding($true)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# Версия пайплайна задана в оркестраторе — оттуда её и берём
if ([string]::IsNullOrWhiteSpace($Version)) {
    $starter = Get-Content (Join-Path $srcDir "00_Start_RunScript.ps1") -Raw -Encoding UTF8
    $match = [regex]::Match($starter, '\$pipelineVersion\s*=\s*"(?<version>\d+\.\d+)')
    if (-not $match.Success) {
        throw "Не удалось прочитать `$pipelineVersion из 00_Start_RunScript.ps1 — укажите версию ключом -Version."
    }
    $Version = $match.Groups["version"].Value
}
Write-Host "Версия: $Version"

# Файлы, которые входят в поставку. Старые Инструкция.docx/.pdf и docs\ не копируем.
$shipScripts = @(
    "00_Start_RunScript.ps1",
    "01_Step1_DownloadFromRevitServer.ps1",
    "02_Step2_ConvertToNWC.ps1",
    "03_Step3_UploadToPartnersNWC.ps1",
    "04_Step4_UploadToPartnersRVT.ps1",
    "05_Step5_UploadToFileServerNWC.ps1",
    "06_Step6_SyncFromPartnersNWC.ps1",
    "07_Step7_SyncFromPartnersRVT.ps1",
    "common.ps1",
    "log.ps1",
    "vpn.ps1"
)

# --- Организация и разработчик ------------------------------------------------
. (Join-Path $scriptDir "..\tools\Branding.ps1")
$brand = Get-Branding -Organization $Organization -Unit $Unit
Write-Host "Организация: $($brand.ORG), $($brand.UNIT)"
Write-Host "Разработчик: $($brand.DEV)"

# --- Лицензионный заголовок -------------------------------------------------
$licenseText = @(
    "Пайплайн DownloadAndCopyRVTNWC. Версия $Version. Сборка от $buildDate.",
    "Собрано для: $($brand.ORG), $($brand.UNIT).",
    "(c) $($brand.YEAR) $($brand.DEV) ($($brand.DEV_URL)).",
    "",
    "Распространяется по лицензии MIT: использование, копирование и модификация",
    "разрешены при сохранении этого уведомления об авторстве. Полный текст",
    "лицензии — в файле LICENSE репозитория разработчика."
)

function Get-Ps1Header {
    $line = "#" * 100
    $out = @($line)
    foreach ($l in $licenseText) { $out += ("# " + $l).TrimEnd() }
    $out += $line
    return ($out -join "`r`n") + "`r`n`r`n"
}

function Get-HtmlHeader {
    $out = @("<!--")
    foreach ($l in $licenseText) { $out += ("  " + $l).TrimEnd() }
    $out += "-->"
    return ($out -join "`r`n")
}

# --- Удаление комментариев из PowerShell ------------------------------------
# Используем штатный токенизатор PowerShell: он точно отличает комментарий от
# символа # внутри строки или here-string, поэтому строки и код не пострадают.
function Remove-Ps1Comments {
    param([string]$Code)

    $errors = $null
    $tokens = [System.Management.Automation.PSParser]::Tokenize($Code, [ref]$errors)
    if ($errors -and $errors.Count -gt 0) {
        throw "Не удалось разобрать PowerShell: $($errors[0].Message)"
    }

    $chars = $Code.ToCharArray()
    foreach ($t in $tokens) {
        if ($t.Type -eq [System.Management.Automation.PSTokenType]::Comment) {
            for ($k = $t.Start; $k -lt ($t.Start + $t.Length); $k++) { $chars[$k] = [char]0 }
        }
    }
    $stripped = ($chars | Where-Object { $_ -ne [char]0 }) -join ""

    # Убираем осиротевшие пробелы в конце строк и схлопываем пустые строки
    $lines = $stripped -replace "`r`n", "`n" -split "`n"
    $result = New-Object System.Collections.Generic.List[string]
    $blank = $true   # чтобы съесть пустые строки в самом начале
    foreach ($line in $lines) {
        $trimmed = $line.TrimEnd()
        if ($trimmed -eq "") {
            if (-not $blank) { $result.Add(""); $blank = $true }
        } else {
            $result.Add($trimmed); $blank = $false
        }
    }
    while ($result.Count -gt 0 -and $result[$result.Count - 1] -eq "") { $result.RemoveAt($result.Count - 1) }
    return ($result -join "`r`n")
}

# --- Удаление комментариев из HTML/CSS/JS -----------------------------------
# Посимвольный разбор с учётом строк ('...', "...", `...`): комментарии
# // /* */ <!-- --> удаляются, а такие же последовательности внутри строк
# и регулярных выражений остаются нетронутыми.
function Remove-WebComments {
    param([string]$Text)

    $sb = New-Object System.Text.StringBuilder
    $n = $Text.Length
    $i = 0
    $state = "code"   # code | sq | dq | tpl | line | block | html | regex
    $lastSig = [char]0        # последний значимый символ кода — по нему отличаем регэксп от деления
    $reClass = $false         # внутри [...] регулярного выражения '/' его не закрывает
    # После этих символов '/' начинает регулярное выражение, а не деление
    $regexStart = "(,=:[!&|?{;"

    while ($i -lt $n) {
        $c  = $Text[$i]
        $c2 = if ($i + 1 -lt $n) { $Text[$i + 1] } else { [char]0 }

        if ($state -eq "code") {
            if ($c -eq "<" -and ($i + 4 -le $n) -and $Text.Substring($i, 4) -eq "<!--") {
                $state = "html"; $i += 4
            } elseif ($c -eq "/" -and $c2 -eq "/") {
                # // и /* всегда комментарий (регэксп не может начинаться с //)
                $state = "line"; $i += 2
            } elseif ($c -eq "/" -and $c2 -eq "*") {
                $state = "block"; $i += 2
            } elseif ($c -eq "/" -and ($lastSig -eq [char]0 -or $regexStart.IndexOf([string]$lastSig) -ge 0)) {
                # Регулярное выражение: сохраняем как есть, чтобы '/' внутри него
                # (например /\//g) не приняли за начало комментария
                [void]$sb.Append($c); $state = "regex"; $reClass = $false; $lastSig = "/"; $i++
            } elseif ($c -eq "'") {
                [void]$sb.Append($c); $state = "sq"; $i++
            } elseif ($c -eq '"') {
                [void]$sb.Append($c); $state = "dq"; $i++
            } elseif ($c -eq [char]96) {          # обратная кавычка — шаблонная строка JS
                [void]$sb.Append($c); $state = "tpl"; $i++
            } else {
                [void]$sb.Append($c); $i++
                if ($c -notmatch '\s') { $lastSig = $c }
            }
        } elseif ($state -eq "regex") {
            [void]$sb.Append($c)
            if ($c -eq "\") {
                if ($i + 1 -lt $n) { [void]$sb.Append($c2) }
                $i += 2
            } else {
                if ($c -eq "[") { $reClass = $true }
                elseif ($c -eq "]") { $reClass = $false }
                elseif ($c -eq "/" -and -not $reClass) { $state = "code"; $lastSig = "x" }
                $i++
            }
        } elseif ($state -eq "sq" -or $state -eq "dq" -or $state -eq "tpl") {
            $q = if ($state -eq "sq") { "'" } elseif ($state -eq "dq") { '"' } else { [char]96 }
            if ($c -eq "\") {
                [void]$sb.Append($c)
                if ($i + 1 -lt $n) { [void]$sb.Append($c2) }
                $i += 2
            } elseif ($c -eq $q) {
                [void]$sb.Append($c); $state = "code"; $lastSig = "x"; $i++
            } else {
                [void]$sb.Append($c); $i++
            }
        } elseif ($state -eq "line") {
            if ($c -eq "`n") { [void]$sb.Append($c); $state = "code" }
            $i++
        } elseif ($state -eq "block") {
            if ($c -eq "*" -and $c2 -eq "/") { $state = "code"; $i += 2 } else { $i++ }
        } elseif ($state -eq "html") {
            if ($c -eq "-" -and ($i + 3 -le $n) -and $Text.Substring($i, 3) -eq "-->") { $state = "code"; $i += 3 } else { $i++ }
        }
    }

    # Схлопываем пустые строки, оставшиеся после вырезанных комментариев
    $lines = $sb.ToString() -replace "`r`n", "`n" -split "`n"
    $result = New-Object System.Collections.Generic.List[string]
    $blank = $false
    foreach ($line in $lines) {
        $t = $line.TrimEnd()
        if ($t -eq "") {
            if (-not $blank) { $result.Add(""); $blank = $true }
        } else {
            $result.Add($t); $blank = $false
        }
    }
    return ($result -join "`r`n")
}

# --- Markdown -> HTML (подмножество, которого хватает для инструкции) --------
function Convert-Inline {
    param([string]$Text)
    $t = $Text -replace "&", "&amp;" -replace "<", "&lt;" -replace ">", "&gt;"
    $codes = New-Object System.Collections.Generic.List[string]
    $t = [regex]::Replace($t, '`([^`]+)`', {
        param($m) $codes.Add($m.Groups[1].Value); "$([char]0xE000)$($codes.Count - 1)$([char]0xE000)"
    })
    $t = [regex]::Replace($t, '\*\*([^*]+)\*\*', '<strong>$1</strong>')
    $t = [regex]::Replace($t, '(?<!\*)\*([^*]+)\*(?!\*)', '<em>$1</em>')
    $t = [regex]::Replace($t, "$([char]0xE000)(\d+)$([char]0xE000)", {
        param($m) "<code>" + $codes[[int]$m.Groups[1].Value] + "</code>"
    })
    return $t
}

function Convert-MarkdownToHtml {
    param([string]$Markdown)

    $lines = $Markdown -replace "`r`n", "`n" -split "`n"
    $out = New-Object System.Collections.Generic.List[string]
    $i = 0
    $n = $lines.Count

    while ($i -lt $n) {
        $line = $lines[$i]

        if ($line.Trim() -eq "") { $i++; continue }

        $mImg = [regex]::Match($line.Trim(), '^!\[(.*?)\]\((.*?)\)$')
        if ($mImg.Success) {
            $alt = $mImg.Groups[1].Value; $src = $mImg.Groups[2].Value
            $cap = if ($alt -ne "") { "<figcaption>" + ($alt -replace "&", "&amp;" -replace "<", "&lt;") + "</figcaption>" } else { "" }
            $out.Add("<figure><img src=`"$src`" alt=`"$($alt -replace '"','')`">$cap</figure>")
            $i++; continue
        }

        $mH = [regex]::Match($line, '^(#{1,4})\s+(.*)$')
        if ($mH.Success) {
            $lvl = $mH.Groups[1].Value.Length
            $out.Add("<h$lvl>" + (Convert-Inline $mH.Groups[2].Value) + "</h$lvl>")
            $i++; continue
        }

        if ($line -match '^-{3,}\s*$') { $out.Add("<hr>"); $i++; continue }

        if ($line.TrimStart().StartsWith("|")) {
            $block = New-Object System.Collections.Generic.List[string]
            while ($i -lt $n -and $lines[$i].TrimStart().StartsWith("|")) { $block.Add($lines[$i].Trim()); $i++ }
            $rows = @()
            foreach ($r in $block) { $rows += , (($r.Trim("|") -split '\|') | ForEach-Object { $_.Trim() }) }
            $header = $rows[0]
            $isSep = ($rows.Count -gt 1) -and (($rows[1] -join "") -match '^[-:\s|]+$')
            $body = if ($isSep) { $rows[2..($rows.Count - 1)] } else { $rows[1..($rows.Count - 1)] }
            $t = New-Object System.Collections.Generic.List[string]
            $t.Add("<table><thead><tr>")
            foreach ($c in $header) { $t.Add("<th>" + (Convert-Inline $c) + "</th>") }
            $t.Add("</tr></thead><tbody>")
            foreach ($r in $body) {
                $cells = ($r | ForEach-Object { "<td>" + (Convert-Inline $_) + "</td>" }) -join ""
                $t.Add("<tr>$cells</tr>")
            }
            $t.Add("</tbody></table>")
            $out.Add(($t -join ""))
            continue
        }

        if ($line.TrimStart().StartsWith(">")) {
            $block = New-Object System.Collections.Generic.List[string]
            while ($i -lt $n -and $lines[$i].TrimStart().StartsWith(">")) {
                $block.Add(($lines[$i] -replace '^\s*>\s?', '')); $i++
            }
            $out.Add("<blockquote>" + (Convert-Inline ($block -join " ")) + "</blockquote>")
            continue
        }

        if ($line -match '^\s*[-*]\s+') {
            $items = New-Object System.Collections.Generic.List[string]
            while ($i -lt $n) {
                $l = $lines[$i]
                if ($l -match '^\s*[-*]\s+') {
                    $items.Add(($l -replace '^\s*[-*]\s+', '')); $i++
                } elseif ($l.Trim() -ne "" -and $items.Count -gt 0 -and ($l -notmatch '^(#{1,4}\s|!\[|\||>|-{3,}\s*$)')) {
                    # Перенос строки внутри пункта: приклеиваем к текущему пункту,
                    # иначе жирный/код, разорванный переносом, ломается на два блока.
                    $items[$items.Count - 1] = $items[$items.Count - 1] + " " + $l.Trim(); $i++
                } else {
                    break
                }
            }
            $li = ($items | ForEach-Object { "<li>" + (Convert-Inline $_) + "</li>" }) -join ""
            $out.Add("<ul>$li</ul>")
            continue
        }

        $para = New-Object System.Collections.Generic.List[string]
        $para.Add($line); $i++
        while ($i -lt $n -and $lines[$i].Trim() -ne "" -and $lines[$i] -notmatch '^(#{1,4}\s|!\[|\||\s*[-*]\s|>|-{3,}\s*$)') {
            $para.Add($lines[$i]); $i++
        }
        $out.Add("<p>" + (Convert-Inline ($para -join " ")) + "</p>")
    }

    $css = @'
@page { size: A4; margin: 18mm 16mm 16mm 16mm; }
* { box-sizing: border-box; }
body { font-family: "Segoe UI", system-ui, sans-serif; color: #1C2126; font-size: 10.5pt; line-height: 1.5; margin: 0; }
h1 { font-size: 22pt; margin: 0 0 4pt; }
h2 { font-size: 15pt; margin: 20pt 0 6pt; padding-bottom: 3pt; border-bottom: 2px solid #0B6E7F; color: #0B2A30; }
h3 { font-size: 12pt; margin: 14pt 0 4pt; color: #14343A; }
h2, h3 { break-after: avoid; }
p { margin: 0 0 7pt; }
ul { margin: 0 0 8pt; padding-left: 18pt; }
li { margin: 2pt 0; }
code { font-family: Consolas, "Courier New", monospace; font-size: 9.5pt; background: #EEF1F3; padding: 1px 4px; border-radius: 3px; }
strong { font-weight: 600; }
hr { border: none; border-top: 1px solid #D5DBDF; margin: 14pt 0; }
table { border-collapse: collapse; width: 100%; margin: 4pt 0 10pt; font-size: 9.5pt; }
th, td { border: 1px solid #C9D0D6; padding: 4pt 7pt; text-align: left; vertical-align: top; }
th { background: #EAF1F2; font-weight: 600; }
tr { break-inside: avoid; }
figure { margin: 8pt 0 12pt; break-inside: avoid; text-align: center; }
figure img { max-width: 100%; border: 1px solid #D5DBDF; border-radius: 3px; }
figcaption { font-size: 8.5pt; color: #6B767E; margin-top: 4pt; font-style: italic; }
blockquote { margin: 8pt 0; padding: 7pt 12pt; background: #FBF3DC; border-left: 3px solid #8A6A00; border-radius: 2px; }
blockquote p { margin: 0; }
h1 + blockquote { background: #EAF1F2; border-left-color: #0B6E7F; }
'@

    return "<!doctype html><html lang=`"ru`"><head><meta charset=`"utf-8`"><title>Инструкция</title><style>$css</style></head><body>" + ($out -join "`n") + "</body></html>"
}

function Find-Browser {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Microsoft\Edge\Application\msedge.exe"),
        (Join-Path $env:ProgramFiles "Microsoft\Edge\Application\msedge.exe"),
        (Join-Path $env:ProgramFiles "Google\Chrome\Application\chrome.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Google\Chrome\Application\chrome.exe")
    )
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
    return $null
}

function Build-Pdf {
    param([string]$MdPath, [string]$PdfPath)

    $md = [System.IO.File]::ReadAllText($MdPath, [System.Text.Encoding]::UTF8)
    $html = Convert-MarkdownToHtml -Markdown $md

    $browser = Find-Browser
    if (-not $browser) {
        throw "Не найден Microsoft Edge или Google Chrome — без них PDF не собрать."
    }

    # Рендерим во временной папке без пробелов и кириллицы в пути: Edge не умеет
    # надёжно принимать такие пути в аргументах командной строки. Картинки копируем
    # рядом, чтобы относительные ссылки images\... разрешились, — Edge впечатает их в PDF.
    $work = Join-Path $env:TEMP ("dacrvt_pdf_" + [guid]::NewGuid().ToString("N"))
    New-Item -Path $work -ItemType Directory | Out-Null
    $imgSrc = Join-Path $docsDir "images"
    if (Test-Path $imgSrc) { Copy-Item $imgSrc (Join-Path $work "images") -Recurse -Force }

    $workHtml = Join-Path $work "doc.html"
    $workPdf  = Join-Path $work "out.pdf"
    [System.IO.File]::WriteAllText($workHtml, $html, $utf8NoBom)

    $userData = Join-Path $work "profile"
    $url = "file:///" + $workHtml.Replace('\', '/')
    $procArgs = @(
        "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
        "--user-data-dir=$userData", "--no-pdf-header-footer",
        "--virtual-time-budget=5000",
        "--print-to-pdf=$workPdf", $url
    )

    # Start-Process не превращает предупреждения Edge в stderr в ошибку PowerShell.
    $errLog = Join-Path $work "edge_err.log"
    $proc = Start-Process -FilePath $browser -ArgumentList $procArgs -Wait -PassThru -NoNewWindow -RedirectStandardError $errLog

    if (Test-Path $workPdf) {
        if (Test-Path $PdfPath) { Remove-Item $PdfPath -Force }
        Copy-Item $workPdf $PdfPath -Force
    }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue

    if (-not (Test-Path $PdfPath)) {
        throw "Edge/Chrome не создал PDF (код $($proc.ExitCode)): $PdfPath"
    }
}

# ============================================================================
# Сборка
# ============================================================================
Write-Host "Сборка релиза DownloadAndCopyRVTNWC $Version от $buildDate" -ForegroundColor Cyan

if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
New-Item -Path $distDir -ItemType Directory | Out-Null

$ps1Header  = Get-Ps1Header
$htmlHeader = Get-HtmlHeader

# 1. Скрипты .ps1
foreach ($name in $shipScripts) {
    $inPath = Join-Path $srcDir $name
    if (-not (Test-Path $inPath)) { throw "Нет исходника: $inPath" }
    $code = [System.IO.File]::ReadAllText($inPath, [System.Text.Encoding]::UTF8)
    $stripped = Remove-Ps1Comments -Code $code
    [System.IO.File]::WriteAllText((Join-Path $distDir $name), $ps1Header + $stripped + "`r`n", $utf8Bom)
    Write-Host "  .ps1  $name" -ForegroundColor DarkGray
}

# 2. Settings.html
$htmlPath = Join-Path $srcDir "Settings.html"
$html = [System.IO.File]::ReadAllText($htmlPath, [System.Text.Encoding]::UTF8)
$html = Remove-WebComments -Text $html
# Лицензию вставляем внутрь <head>, чтобы не сбить режим рендеринга комментарием перед DOCTYPE.
# Шаблон <head(\s...)?> не должен ловить <header ...>, а вставку делаем ровно один раз.
$headRx = [regex]::new('<head(\s[^>]*)?>', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
$html = $headRx.Replace($html, "`$0`r`n$htmlHeader", 1)
[System.IO.File]::WriteAllText((Join-Path $distDir "Settings.html"), $html, $utf8NoBom)
Write-Host "  html  Settings.html" -ForegroundColor DarkGray

# 3. config.json — в поставку идёт ОБЕЗЛИЧЕННЫЙ шаблон, а не наш боевой конфиг.
#    Боевой config.json с реальными адресами и путями остаётся только в src\.
$templatePath = Join-Path $srcDir "config.template.json"
$realConfig   = Join-Path $srcDir "config.json"
if (-not (Test-Path $templatePath)) {
    throw "Не найден обезличенный шаблон $templatePath. В поставку нельзя класть боевой config.json."
}

# Сверяем ключи шаблона и боевого конфига: если в config.json появился ключ,
# которого нет в шаблоне, предупреждаем — иначе контрагент получит неполный конфиг.
try {
    $tplKeys  = ($templatePath | ForEach-Object { (Get-Content $_ -Raw -Encoding UTF8 | ConvertFrom-Json).PSObject.Properties.Name })
    $realKeys = ($realConfig   | ForEach-Object { (Get-Content $_ -Raw -Encoding UTF8 | ConvertFrom-Json).PSObject.Properties.Name })
    $missing = @($realKeys | Where-Object { $tplKeys -notcontains $_ })
    if ($missing.Count -gt 0) {
        Write-Host "  ВНИМАНИЕ: в config.template.json нет ключей: $($missing -join ', ')" -ForegroundColor Yellow
    }
} catch {
    Write-Host "  ВНИМАНИЕ: не удалось сверить ключи шаблона и конфига: $($_.Exception.Message)" -ForegroundColor Yellow
}

Copy-Item $templatePath (Join-Path $distDir "config.json") -Force
Write-Host "  json  config.json (обезличенный шаблон)" -ForegroundColor DarkGray

# 4. Инструкция.pdf — пересобираем из Markdown
#
# Подстановку организации делаем ДО сборки PDF, на временной копии:
# PDF двоичный, и общая проверка в конце его не просматривает — сырая
# {{ORG}} уехала бы к заказчику незамеченной.
$mdPath  = Join-Path $docsDir "Инструкция.md"
$pdfPath = Join-Path $distDir "Инструкция.pdf"

$mdBranded = Join-Path ([IO.Path]::GetTempPath()) ("Инструкция_" + [guid]::NewGuid().ToString('N') + ".md")
Copy-Item $mdPath $mdBranded -Force
[void](Expand-Branding -Path $mdBranded -Branding $brand)
Assert-NoBrandingTokens -Path $mdBranded

try {
    Build-Pdf -MdPath $mdBranded -PdfPath $pdfPath
} finally {
    Remove-Item $mdBranded -Force -ErrorAction SilentlyContinue
}
Write-Host "  pdf   Инструкция.pdf ($([math]::Round((Get-Item $pdfPath).Length / 1KB)) КБ)" -ForegroundColor DarkGray

# 5. Подстановка организации по всей поставке сразу
#
# Одним проходом в конце, а не при копировании каждого файла: так нельзя
# забыть новый файл. Проверка после подстановки обязательна — {{ORG}},
# уехавшая к заказчику, выглядит хуже, чем упавшая сборка.
$заменено = Expand-Branding -Path $distDir -Branding $brand
Assert-NoBrandingTokens -Path $distDir
Write-Host "  org   подстановка организации: файлов изменено $заменено" -ForegroundColor DarkGray

Write-Host "Готово: $distDir" -ForegroundColor Green
Write-Host "Собрано для: $($brand.ORG), $($brand.UNIT). Разработчик: $($brand.DEV)." -ForegroundColor Gray
