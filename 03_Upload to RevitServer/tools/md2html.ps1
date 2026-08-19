<#
.SYNOPSIS
    Собирает автономную HTML-страницу из markdown.

.DESCRIPTION
    Нужен, чтобы инструкция для коллег жила в .md рядом с кодом, а на руки
    выдавалась готовой страницей: markdown в блокноте читается плохо, а ставить
    людям что-то для просмотра — лишний разговор.

    Поддерживается то подмножество markdown, которым написана наша документация:
    заголовки, абзацы, списки, таблицы, блоки кода, цитаты, разделители,
    жирный, курсив, код и ссылки. Ничего сверх — конвертер должен быть
    предсказуемым, а не всеядным.

    Результат — один файл без внешних ссылок: шрифты системные, стили внутри.
    Оформление то же, что у Settings.html.

.EXAMPLE
    .\tools\md2html.ps1 -Source docs\Инструкция.md -Target dist\Инструкция.html
#>
param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Target,
    [string]$Title
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Source)) { throw "Не найден исходник: $Source" }

$md = Get-Content $Source -Raw -Encoding UTF8
$lines = $md -split "`r?`n"

if (-not $Title) {
    $h1 = $lines | Where-Object { $_ -match '^#\s+' } | Select-Object -First 1
    $Title = if ($h1) { ($h1 -replace '^#\s+','').Trim() } else { [IO.Path]::GetFileNameWithoutExtension($Source) }
}

function Esc([string]$s) {
    $s -replace '&','&amp;' -replace '<','&lt;' -replace '>','&gt;'
}

# Разметка внутри строки. Порядок важен: код прячется первым и дальше
# не трогается, иначе `**` внутри примера команды станет жирным.
function Inline([string]$s) {
    $s = Esc $s

    $stash = New-Object 'System.Collections.Generic.List[string]'
    while ($s -match '`([^`]+)`') {
        $stash.Add('<code>' + $Matches[1] + '</code>')
        # -replace здесь не годится: в тексте кода бывают $ и \, которые
        # подстановка в правой части трактует как ссылки на группы.
        $s = [regex]::Replace($s, '`([^`]+)`', "%%C$($stash.Count - 1)%%", 1)
    }

    $s = [regex]::Replace($s, '\[([^\]]+)\]\(([^)]+)\)', '<a href="$2">$1</a>')
    $s = [regex]::Replace($s, '\*\*([^*]+)\*\*', '<strong>$1</strong>')
    $s = [regex]::Replace($s, '(?<![\w*])\*([^*]+)\*(?![\w*])', '<em>$1</em>')

    for ($k = 0; $k -lt $stash.Count; $k++) { $s = $s.Replace("%%C$k%%", $stash[$k]) }
    $s
}

$out = New-Object System.Text.StringBuilder
$listStack = New-Object 'System.Collections.Generic.List[string]'   # 'ul' | 'ol'
$inCode = $false
$inQuote = $false
$para = New-Object 'System.Collections.Generic.List[string]'

function CloseParagraph {
    if ($script:para.Count) {
        [void]$script:out.AppendLine('<p>' + (Inline ($script:para -join ' ')) + '</p>')
        $script:para.Clear()
    }
}
function PopList {
    $last = $script:listStack.Count - 1
    [void]$script:out.AppendLine("</$($script:listStack[$last])>")
    $script:listStack.RemoveAt($last)
}
function CloseLists { while ($script:listStack.Count) { PopList } }
function CloseQuote {
    if ($script:inQuote) { [void]$script:out.AppendLine('</blockquote>'); $script:inQuote = $false }
}
function CloseAll { CloseParagraph; CloseLists; CloseQuote }

for ($n = 0; $n -lt $lines.Count; $n++) {
    $line = $lines[$n]

    # Блок кода
    if ($line -match '^\s*```') {
        if ($inCode) { [void]$out.AppendLine('</code></pre>'); $inCode = $false }
        else { CloseAll; [void]$out.AppendLine('<pre><code>'); $inCode = $true }
        continue
    }
    if ($inCode) { [void]$out.AppendLine((Esc $line)); continue }

    # Таблица: строка с | и следующая из дефисов
    if ($line -match '^\s*\|' -and $n + 1 -lt $lines.Count -and $lines[$n+1] -match '^\s*\|[\s\-:|]+\|\s*$') {
        CloseAll
        $cells = ($line.Trim() -replace '^\|','' -replace '\|$','') -split '\|'
        [void]$out.AppendLine('<div class="tablewrap"><table><thead><tr>')
        foreach ($c in $cells) { [void]$out.AppendLine('<th>' + (Inline $c.Trim()) + '</th>') }
        [void]$out.AppendLine('</tr></thead><tbody>')
        $n += 2
        while ($n -lt $lines.Count -and $lines[$n] -match '^\s*\|') {
            $row = ($lines[$n].Trim() -replace '^\|','' -replace '\|$','') -split '\|'
            [void]$out.AppendLine('<tr>')
            foreach ($c in $row) { [void]$out.AppendLine('<td>' + (Inline $c.Trim()) + '</td>') }
            [void]$out.AppendLine('</tr>')
            $n++
        }
        $n--
        [void]$out.AppendLine('</tbody></table></div>')
        continue
    }

    # Заголовки
    if ($line -match '^(#{1,4})\s+(.*)$') {
        CloseAll
        $level = $Matches[1].Length
        [void]$out.AppendLine("<h$level>" + (Inline $Matches[2].Trim()) + "</h$level>")
        continue
    }

    # Разделитель
    if ($line -match '^\s*---+\s*$') { CloseAll; [void]$out.AppendLine('<hr>'); continue }

    # Цитата
    if ($line -match '^\s*>\s?(.*)$') {
        CloseParagraph; CloseLists
        if (-not $inQuote) { [void]$out.AppendLine('<blockquote>'); $inQuote = $true }
        [void]$out.AppendLine('<p>' + (Inline $Matches[1]) + '</p>')
        continue
    }
    if ($inQuote -and $line.Trim() -eq '') { CloseQuote; continue }

    # Списки
    if ($line -match '^(\s*)([-*])\s+(.*)$' -or $line -match '^(\s*)(\d+\.)\s+(.*)$') {
        $indent = $Matches[1].Length
        $marker = $Matches[2]
        $text   = $Matches[3]
        $tag = if ($marker -match '^\d') { 'ol' } else { 'ul' }
        CloseParagraph
        $want = [Math]::Floor($indent / 2) + 1
        while ($listStack.Count -gt $want) { PopList }
        if ($listStack.Count -lt $want) {
            [void]$out.AppendLine("<$tag>")
            $listStack.Add($tag)
        }
        [void]$out.AppendLine('<li>' + (Inline $text) + '</li>')
        continue
    }

    # Пустая строка — конец абзаца и списков
    if ($line.Trim() -eq '') { CloseAll; continue }

    $para.Add($line.Trim())
}
CloseAll
if ($inCode) { [void]$out.AppendLine('</code></pre>') }

$css = @'
:root{--ink:#1C2126;--ink-2:#4A545E;--ink-3:#79848F;--planshet:#E4E7EA;--sheet:#FFFFFF;
--line:#C9D0D6;--line-soft:#E3E7EA;--up:#0B6E7F;--up-soft:#E4F1F3;--stop:#B02A3F;
--warn:#8A6A00;--warn-soft:#FBF3DC;
--font-display:"Bahnschrift SemiCondensed","Bahnschrift","Segoe UI Semibold","Segoe UI",sans-serif;
--font-ui:"Segoe UI",system-ui,sans-serif;--font-mono:Consolas,"Cascadia Mono","Courier New",monospace;--radius:3px}
*{box-sizing:border-box}
body{margin:0;background:var(--planshet);color:var(--ink);font-family:var(--font-ui);font-size:15.5px;line-height:1.6}
.bar{position:sticky;top:0;z-index:10;display:flex;align-items:baseline;gap:12px;
padding:14px 24px;background:var(--ink);color:#F2F4F6;flex-wrap:wrap}
.bar__name{font-family:var(--font-display);font-size:19px;letter-spacing:.08em;text-transform:uppercase}
.bar__sub{font-size:12px;color:#98A3AC}
.sheet{max-width:860px;margin:26px auto 90px;background:var(--sheet);border:1px solid var(--line);
border-radius:var(--radius);padding:38px 46px 54px}
h1{font-family:var(--font-display);font-size:31px;letter-spacing:.03em;margin:0 0 6px;line-height:1.15}
h2{font-family:var(--font-display);font-size:22px;letter-spacing:.05em;text-transform:uppercase;
margin:38px 0 10px;padding-top:14px;border-top:1px solid var(--line-soft)}
h3{font-size:17px;margin:26px 0 6px}
h4{font-size:15px;margin:20px 0 4px;color:var(--ink-2)}
p{margin:0 0 12px;max-width:70ch}
a{color:var(--up)}
ul,ol{margin:0 0 14px;padding-left:22px;max-width:70ch}
li{margin:0 0 5px}
code{font-family:var(--font-mono);font-size:.88em;background:#F1F4F5;border:1px solid var(--line-soft);
border-radius:2px;padding:1px 4px}
pre{background:#12171B;border-radius:var(--radius);padding:14px 16px;overflow-x:auto;margin:0 0 16px}
pre code{font-family:var(--font-mono);font-size:12.8px;line-height:1.65;background:none;border:0;
padding:0;color:#D6DDE2;white-space:pre}
blockquote{margin:0 0 16px;padding:12px 16px;background:var(--warn-soft);
border-left:3px solid var(--warn);border-radius:var(--radius)}
blockquote p{margin:0}
hr{border:0;border-top:1px solid var(--line-soft);margin:26px 0}
.tablewrap{overflow-x:auto;margin:0 0 18px}
table{border-collapse:collapse;width:100%;font-size:14px}
th{font-family:var(--font-display);font-size:12px;letter-spacing:.1em;text-transform:uppercase;
text-align:left;padding:8px 12px;border-bottom:2px solid var(--ink);white-space:nowrap}
td{padding:8px 12px;border-bottom:1px solid var(--line-soft);vertical-align:top}
tr:last-child td{border-bottom:0}
@media print{body{background:#fff}.bar{display:none}.sheet{border:0;margin:0;padding:0;max-width:none}}
@media (max-width:720px){.sheet{padding:24px 20px 40px;margin:14px}}
'@

$html = @"
<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>$(Esc $Title)</title>
<style>
$css
</style>
</head>
<body>
<header class="bar">
  <span class="bar__name">RvsUpload</span>
  <span class="bar__sub">{{ORG}} · {{UNIT}} · документация</span>
</header>
<main class="sheet">
$($out.ToString())
<hr>
<p style="font-size:12.5px;color:var(--ink-3)">
RvsUpload · разработчик {{DEV}} · {{DEV_URL}}<br>
Собрано для: {{ORG}}, {{UNIT}}
</p>
</main>
</body>
</html>
"@

$dir = Split-Path -Parent $Target
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

# UTF-8 без BOM: браузер берёт кодировку из <meta charset>, а BOM в HTML
# некоторые старые просмотрщики показывают как мусорный символ в начале.
[IO.File]::WriteAllText((Resolve-Path -LiteralPath $dir).Path + "\" + (Split-Path -Leaf $Target),
                        $html, (New-Object Text.UTF8Encoding $false))

Write-Host "  $Target"
