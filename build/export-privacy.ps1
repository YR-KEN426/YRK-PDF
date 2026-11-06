# Export the website's privacy sections as the app's offline policy resources.
param([switch]$Check)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$utf8 = New-Object Text.UTF8Encoding $false

function Get-InlineText($node) {
    return [regex]::Replace($node.InnerText, '\s+', ' ').Trim()
}

function Add-Blocks($node, $lines, [int]$depth = 0) {
    foreach ($child in $node.ChildNodes) {
        switch ($child.LocalName) {
            { $_ -in 'h2', 'h3', 'p' } {
                $prefix = switch ($child.LocalName) { 'h2' { '# ' } 'h3' { '## ' } default { '' } }
                $lines.Add($prefix + (Get-InlineText $child))
                $lines.Add('')
            }
            { $_ -in 'ul', 'ol' } {
                Add-Blocks $child $lines $depth
                if ($depth -eq 0) { $lines.Add('') }
            }
            'li' {
                # Keep nested lists separate from the parent item's inline text.
                $inline = $child.CloneNode($true)
                foreach ($list in @($inline.SelectNodes('./ul|./ol'))) { [void]$inline.RemoveChild($list) }
                $listNode = $child.ParentNode
                if ($listNode.LocalName -eq 'ol') {
                    $siblings = @($listNode.SelectNodes('./li'))
                    $marker = ([array]::IndexOf($siblings, $child) + 1).ToString() + '. '
                } else {
                    $marker = '- '
                }
                $lines.Add(('  ' * $depth) + $marker + (Get-InlineText $inline))
                foreach ($list in $child.SelectNodes('./ul|./ol')) {
                    Add-Blocks $list $lines ($depth + 1)
                }
            }
            'table' {
                foreach ($row in $child.SelectNodes('.//tr')) {
                    $cells = @($row.SelectNodes('./th|./td') | ForEach-Object { Get-InlineText $_ })
                    $lines.Add('| ' + ($cells -join ' | ') + ' |')
                }
                $lines.Add('')
            }
            default { if ($child.HasChildNodes) { Add-Blocks $child $lines $depth } }
        }
    }
}

$different = $false
foreach ($language in @('th', 'en')) {
    $page = if ($language -eq 'th') { 'index.html' } else { 'en\index.html' }
    $source = Join-Path $root "docs\release-repo\site-src\$page"
    $html = [IO.File]::ReadAllText($source, $utf8)
    $section = [regex]::Match($html, '(?s)<section\b[^>]*\bid="privacy"[^>]*>.*?</section>')
    if (-not $section.Success) { throw "Privacy section not found in $source" }
    # The source section is XML-compatible HTML. Decode HTML-only entities while
    # escaping XML metacharacters so neither literal text nor entities are lost.
    $xmlText = [regex]::Replace($section.Value, '&(?:#\d+|#x[\da-fA-F]+|[A-Za-z]+);', {
        param($match)
        [Security.SecurityElement]::Escape([Net.WebUtility]::HtmlDecode($match.Value))
    })
    $document = New-Object Xml.XmlDocument
    $document.LoadXml($xmlText)
    $lines = New-Object 'System.Collections.Generic.List[string]'
    Add-Blocks $document.DocumentElement $lines
    $text = (($lines -join "`n").TrimEnd() + "`n")
    $output = Join-Path $root "src\Yrk.PdfStudio.App\Assets\Privacy\privacy.$language.txt"
    if ($Check) {
        if (-not [IO.File]::Exists($output) -or [IO.File]::ReadAllText($output, $utf8) -cne $text) {
            Write-Host "Privacy policy out of sync: $output. Run build\export-privacy.ps1 to regenerate it." -ForegroundColor Red
            $different = $true
        }
    } else {
        [void][IO.Directory]::CreateDirectory((Split-Path $output -Parent))
        [IO.File]::WriteAllText($output, $text, $utf8)
        Write-Host "Exported privacy.$language.txt"
    }
}
if ($different) { exit 1 }
if ($Check) { Write-Host 'Privacy policies are in sync.' }
