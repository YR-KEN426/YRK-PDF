# Lists the English UI texts of the app that have no Thai translation in Localization\th.json.
# Texts come from XAML (Text, Content, ToolTip, Header, Title, AutomationProperties.Name) and from C#
# (Loc.T / Loc.F arguments and texts passed to the status bar, dialogs and progress, which translate
# themselves). Run it after adding UI text:
#   powershell -File build\check-translations.ps1 [-All] [-OutFile list.txt]
# Line breaks inside a text are shown as \n, the way th.json writes them.
param([switch]$All, [string]$OutFile)
$ErrorActionPreference = "Stop"
$app = Join-Path $PSScriptRoot "..\src\Yrk.PdfStudio.App"
$found = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
$ellipsis = [string][char]0x2026

function Add-Text([string]$text) {
    # Kept exactly as written (leading spaces too): the text is the key.
    if ($text.Trim().Length -lt 2 -or $text -notmatch '[A-Za-z]{2}') { return }
    if ($text -match '^(\{|&#x|pack:|http|[A-Za-z]:\\)' -or $text -cmatch '^[A-Z][a-z]+[A-Z]\w*$') { return }
    [void]$found.Add($text)
}

# XAML: literal attribute values (including <Run Text="..."/>).
$xamlAttribute = '(?:\s|^)(?:Text|Content|ToolTip|Header|Title|AutomationProperties\.Name)="([^"{]*)"'
Get-ChildItem $app -Recurse -Include *.xaml | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | ForEach-Object {
    $xaml = [IO.File]::ReadAllText($_.FullName)
    foreach ($m in [regex]::Matches($xaml, $xamlAttribute)) {
        Add-Text ([System.Net.WebUtility]::HtmlDecode($m.Groups[1].Value))
    }
}

# C#: string literals in places that are translated.
$literal = '"((?:[^"\\\r\n]|\\.)*)"'
$sinks = 'Loc\.[TF]\(|StatusMessage = |SearchStatus = |MeasureResult = |Summary = |ShowError\(|ShowInfo\(|Confirm\(|AskYesNo\(|RunBusyAsync\(|RunPageToolAsync\(|TransformToFileAsync\(|ReportOperation\(|PickSavePdfFile\(|PickPdfFile\(|Placement\(|Header = |ToolTip = |Content = |\.Text = |Title = |=> "|\? "|: "|\(\s*"|^\s*"'
Get-ChildItem $app -Recurse -Include *.cs | Where-Object { $_.FullName -notmatch '\\(obj|bin|Localization)\\' } | ForEach-Object {
    foreach ($line in [IO.File]::ReadAllLines($_.FullName)) {
        if ($line -match '^\s*(//|\[|using )' -or $line -match 'LogMessage|nameof\(|DllImport|GetManifestResource|FindResource|Registry|Path\.Combine|\$"') { continue }
        if ($line -notmatch $sinks) { continue }
        foreach ($m in [regex]::Matches($line, $literal)) {
            $value = [regex]::Unescape($m.Groups[1].Value)
            if ($value.Contains(' ') -or $value.Contains($ellipsis) -or $line -match 'Loc\.[TF]\(\s*"' -or $line -match '=> "|\? "|: "') { Add-Text $value }
        }
    }
}

# Keys of th.json (it has comments, so it is read line by line rather than with ConvertFrom-Json).
$keys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($line in [IO.File]::ReadAllLines((Join-Path $app "Localization\th.json"))) {
    if ($line -match '^\s*"((?:[^"\\]|\\.)*)"\s*:') { [void]$keys.Add([regex]::Unescape($Matches[1])) }
}
# Left in English on purpose: names (app, fonts, Digital ID), file-name parts, formats, and the text drawn
# into a signed document's appearance.
$english = @(
    'YRK PDF', 'Digital ID', 'Digital IDs', ('Language / ' + [char]0x0E20 + [char]0x0E32 + [char]0x0E29 + [char]0x0E32),
    'Aa', '.pdf', ' and more', 'JIS ', 'Regular', 'd MMMM yyyy', 'Date: ', 'Reason: ', 'Digitally signed by ',
    '|All files (*.*)|*.*', 'AutoCAD drawings (*.dwg;*.dxf)|*.dwg;*.dxf|Illustrator files (*.ai)|*.ai|All files (*.*)|*.*',
    'Brush Script MT', 'Dancing Script', 'Freestyle Script', 'Great Vibes', 'Ink Free', 'Lucida Handwriting',
    'Segoe Print', 'Segoe Script', 'Leelawadee UI', 'Leelawadee UI, Segoe UI', 'Segoe UI', 'jpeg', 'png')
foreach ($text in $english) { [void]$keys.Add($text) }
$missing = @($found | Where-Object { -not $keys.Contains($_) })
$list = if ($All) { @($found) } else { $missing }
$lines = [string[]]@($list | ForEach-Object { $_.Replace("`r", "").Replace("`n", '\n') })
if ($OutFile) { [IO.File]::WriteAllLines($OutFile, $lines, [Text.UTF8Encoding]::new($false)) } else { $lines }
Write-Host ("{0} texts, {1} without Thai" -f $found.Count, $missing.Count) -ForegroundColor Cyan
