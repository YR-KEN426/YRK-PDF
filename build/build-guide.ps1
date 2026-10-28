# Builds the user guide PDFs from docs\guide (th.html, en.html) with Microsoft Edge (headless, Print to PDF):
#   -> src\Yrk.PdfStudio.App\Assets\Guide\YRK-PDF-Guide-th.pdf and -en.pdf (shipped with the program, opened from Help)
# {{version}} in the pages becomes the version in Directory.Build.props. Rebuild before each release.
#
#   powershell -ExecutionPolicy Bypass -File build\build-guide.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root "docs\guide"
$target = Join-Path $root "src\Yrk.PdfStudio.App\Assets\Guide"
$utf8 = New-Object Text.UTF8Encoding $false

$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw "Microsoft Edge was not found." }

$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
New-Item -ItemType Directory -Force $target | Out-Null
# A profile of its own, so an Edge window that is open does not take the job.
$profile = Join-Path ([IO.Path]::GetTempPath()) "yrk-guide-edge"

foreach ($lang in @("th", "en")) {
    $html = [IO.File]::ReadAllText((Join-Path $source "$lang.html"), [Text.Encoding]::UTF8).Replace("{{version}}", $version)
    # Next to the source, so its pictures and fonts are found by the same relative paths.
    $page = Join-Path $source "~build-$lang.html"
    [IO.File]::WriteAllText($page, $html, $utf8)
    $pdf = Join-Path $target "YRK-PDF-Guide-$lang.pdf"
    try {
        $url = ([Uri]$page).AbsoluteUri
        $args = @("--headless", "--disable-gpu", "--no-first-run", "--user-data-dir=`"$profile`"", "--no-pdf-header-footer",
            "--run-all-compositor-stages-before-draw", "--virtual-time-budget=10000", "--print-to-pdf=`"$pdf`"", $url)
        Start-Process -FilePath $edge -ArgumentList $args -Wait -WindowStyle Hidden
    }
    finally {
        Remove-Item $page -ErrorAction SilentlyContinue
    }

    if (-not (Test-Path $pdf)) { throw "Edge did not write $pdf" }
    "{0}: {1:N0} KB" -f (Split-Path $pdf -Leaf), ((Get-Item $pdf).Length / 1KB)
}
