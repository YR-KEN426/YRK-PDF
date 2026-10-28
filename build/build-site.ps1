# Builds the download pages for GitHub Pages from docs\release-repo\site-src:
#   index.html    (Thai)    -> docs\release-repo\site\index.html
#   en\index.html (English) -> docs\release-repo\site\en\index.html
# In the sources, {{include:file}} is replaced by that file's text (the shared style.css), {{img:path}} by
# that picture from site-src\img and {{font:path}} by that font from site-src\fonts (IBM Plex Sans Thai, SIL
# Open Font License) as data: URIs, so each page is one file with nothing loaded from elsewhere.
# Upload the results as docs/index.html and docs/en/index.html of the releases repository.
#
#   powershell -ExecutionPolicy Bypass -File build\build-site.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root "docs\release-repo\site-src"
$site = Join-Path $root "docs\release-repo\site"
$utf8 = New-Object Text.UTF8Encoding $false

# {{release:...}}: the latest published release, read from GitHub now (the pages themselves contact nobody).
# Rebuild and upload the pages after every release so the version shown matches the download links.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$latest = Invoke-RestMethod "https://api.github.com/repos/YR-KEN426/yrk-easy-pdf-release/releases/latest" -Headers @{ "User-Agent" = "YRK-build-site" }
$setup = @($latest.assets) | Where-Object { $_.name -like "*Setup.exe" } | Select-Object -First 1
$published = ([datetime]$latest.published_at).ToLocalTime()
$release = @{
    "version" = $latest.tag_name.TrimStart("v")
    "date-th" = $published.ToString("d MMM yyyy", [Globalization.CultureInfo]::GetCultureInfo("th-TH"))
    "date-en" = $published.ToString("d MMM yyyy", [Globalization.CultureInfo]::GetCultureInfo("en-GB"))
    "size"    = [math]::Round($setup.size / 1MB).ToString()
}
"Latest release: $($release.version), $($release.'date-en'), Setup $($release.size) MB"

foreach ($page in @("index.html", "en\index.html")) {
    $html = [IO.File]::ReadAllText((Join-Path $source $page), [Text.Encoding]::UTF8)
    $html = [regex]::Replace($html, "\{\{include:([^}]+)\}\}", {
        param($m)
        [IO.File]::ReadAllText((Join-Path $source $m.Groups[1].Value), [Text.Encoding]::UTF8)
    })
    $html = [regex]::Replace($html, "\{\{release:([^}]+)\}\}", {
        param($m)
        $value = $release[$m.Groups[1].Value]
        if ($null -eq $value) { throw "Unknown release field: $($m.Groups[1].Value)" }
        $value
    })
    $html = [regex]::Replace($html, "\{\{font:([^}]+)\}\}", {
        param($m)
        $file = Join-Path $source ("fonts\" + $m.Groups[1].Value)
        if (-not (Test-Path $file)) { throw "Missing font: $file" }
        "data:font/woff2;base64," + [Convert]::ToBase64String([IO.File]::ReadAllBytes($file))
    })
    $html = [regex]::Replace($html, "\{\{img:([^}]+)\}\}", {
        param($m)
        $file = Join-Path $source ("img\" + $m.Groups[1].Value)
        if (-not (Test-Path $file)) { throw "Missing picture: $file" }
        $type = if ($file -like "*.png") { "image/png" } else { "image/jpeg" }
        "data:$type;base64," + [Convert]::ToBase64String([IO.File]::ReadAllBytes($file))
    })
    $output = Join-Path $site $page
    New-Item -ItemType Directory -Force (Split-Path $output) | Out-Null
    [IO.File]::WriteAllText($output, $html, $utf8)
    "Wrote $output ($([math]::Round((Get-Item $output).Length / 1KB)) KB)"
}
