# Builds a separate test MSIX; certificate trust and installation are manual steps.
#   powershell -File build\make-test-msix.ps1 -SkipPack
param(
    [string]$OutDir = (Join-Path $PSScriptRoot "..\msix-test"),
    [string]$SdkBin,
    [switch]$SkipPack
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $root
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { $env:Path = "C:\Program Files\dotnet;" + $env:Path }
$OutDir = [IO.Path]::GetFullPath($OutDir)
$layout = Join-Path $OutDir "layout"
$msix = Join-Path $OutDir "YrkPdfTest.msix"
$cer = Join-Path $OutDir "YrkPdfTest.cer"

$props = [xml](Get-Content (Join-Path $root "Directory.Build.props") -Raw)
$version = $props.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Version '$version' must look like 1.2.3." }
$packageVersion = "$version.0"

# Layout-only builds work without the Windows SDK.
if (-not $SkipPack) {
    if (-not $SdkBin) {
        $sdkRoot = "C:\Program Files (x86)\Windows Kits\10\bin"
        if (Test-Path $sdkRoot) {
            $SdkBin = Get-ChildItem $sdkRoot -Directory | Where-Object {
                $_.Name -match '^\d+\.\d+\.\d+\.\d+$' -and
                (Test-Path (Join-Path $_.FullName "x64\makeappx.exe")) -and
                (Test-Path (Join-Path $_.FullName "x64\signtool.exe"))
            } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 |
                ForEach-Object { Join-Path $_.FullName "x64" }
        }
    }
    if (-not $SdkBin -or -not (Test-Path (Join-Path $SdkBin "makeappx.exe")) -or
        -not (Test-Path (Join-Path $SdkBin "signtool.exe"))) {
        throw "Windows SDK tools not found. Install the Windows SDK (makeappx.exe and signtool.exe), or supply -SdkBin. Use -SkipPack for layout only."
    }
    $SdkBin = (Resolve-Path $SdkBin).Path
}

Write-Host "== Publish YRK PDF $packageVersion (test)" -ForegroundColor Cyan
# Resolve and check the exact cleanup target before recursively removing it.
$layout = [IO.Path]::GetFullPath($layout)
if ([IO.Path]::GetDirectoryName($layout) -ne $OutDir.TrimEnd('\')) { throw "Invalid layout path: $layout" }
if (Test-Path $layout) { Remove-Item -LiteralPath $layout -Recurse -Force }
dotnet publish src\Yrk.PdfStudio.App -c Release -r win-x64 --self-contained true --nologo `
    -p:Version=$version -p:YrkStore=true -p:PublishReadyToRun=true -o $layout
if ($LASTEXITCODE) { throw "Publish failed (exit code $LASTEXITCODE)." }

$manifestPath = Join-Path $layout "AppxManifest.xml"
$template = Get-Content (Join-Path $PSScriptRoot "msix\AppxManifest.xml") -Raw
$template.Replace('{{VERSION}}', $packageVersion) | Set-Content $manifestPath -Encoding UTF8
$manifest = New-Object System.Xml.XmlDocument
$manifest.Load($manifestPath)

# Read the ICO directory and isolate the largest frame (prefer higher bit depth on ties).
Add-Type -AssemblyName System.Drawing
$icoBytes = [IO.File]::ReadAllBytes((Join-Path $root "src\Yrk.PdfStudio.App\Assets\app.ico"))
$frames = for ($i = 0; $i -lt [BitConverter]::ToUInt16($icoBytes, 4); $i++) {
    $entry = 6 + 16 * $i
    $width = if ($icoBytes[$entry] -eq 0) { 256 } else { [int]$icoBytes[$entry] }
    $height = if ($icoBytes[$entry + 1] -eq 0) { 256 } else { [int]$icoBytes[$entry + 1] }
    [pscustomobject]@{ Entry = $entry; Area = $width * $height; Bits = [BitConverter]::ToUInt16($icoBytes, $entry + 6) }
}
$frame = $frames | Sort-Object Area, Bits -Descending | Select-Object -First 1
$length = [BitConverter]::ToUInt32($icoBytes, $frame.Entry + 8)
$offset = [BitConverter]::ToUInt32($icoBytes, $frame.Entry + 12)
$single = New-Object byte[] (22 + $length)
[Array]::Copy($icoBytes, 0, $single, 0, 6)
$single[4] = 1
$single[5] = 0
[Array]::Copy($icoBytes, $frame.Entry, $single, 6, 16)
[Array]::Copy([BitConverter]::GetBytes([uint32]22), 0, $single, 18, 4)
[Array]::Copy($icoBytes, $offset, $single, 22, $length)
$isPng = $icoBytes[$offset] -eq 137 -and $icoBytes[$offset + 1] -eq 80 -and
    $icoBytes[$offset + 2] -eq 78 -and $icoBytes[$offset + 3] -eq 71
if ($isPng) {
    # GDI+ Icon.ToBitmap can fail on PNG-compressed ICO frames; decode the PNG directly.
    $png = New-Object byte[] $length
    [Array]::Copy($icoBytes, $offset, $png, 0, $length)
    $stream = New-Object IO.MemoryStream(,$png)
}
else { $stream = New-Object IO.MemoryStream(,$single) }
$icon = $null
$source = $null
try {
    if ($isPng) { $source = [Drawing.Image]::FromStream($stream) }
    else {
        $icon = New-Object Drawing.Icon($stream)
        $source = $icon.ToBitmap()
    }
    $assets = Join-Path $layout "Assets"
    New-Item $assets -ItemType Directory -Force | Out-Null
    foreach ($logo in @(
        @{ Name = "Square44x44.png"; Width = 44; Height = 44 },
        @{ Name = "Square150x150.png"; Width = 150; Height = 150 },
        @{ Name = "Wide310x150.png"; Width = 310; Height = 150 },
        @{ Name = "StoreLogo.png"; Width = 50; Height = 50 }
    )) {
        $bitmap = New-Object Drawing.Bitmap($logo.Width, $logo.Height)
        $graphics = $null
        try {
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $scale = [Math]::Min($logo.Width / $source.Width, $logo.Height / $source.Height)
            $w = [int][Math]::Round($source.Width * $scale)
            $h = [int][Math]::Round($source.Height * $scale)
            $graphics.DrawImage($source, [int](($logo.Width - $w) / 2), [int](($logo.Height - $h) / 2), $w, $h)
            $bitmap.Save((Join-Path $assets $logo.Name), [Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            if ($graphics) { $graphics.Dispose() }
            $bitmap.Dispose()
        }
    }
}
finally {
    if ($source) { $source.Dispose() }
    if ($icon) { $icon.Dispose() }
    $stream.Dispose()
}

Write-Host "Layout: $layout"
Write-Host "Manifest: $manifestPath"
Write-Host "Logos: $assets"
if ($SkipPack) {
    Write-Host "== Layout ready; rerun without -SkipPack to pack and sign." -ForegroundColor Green
    return
}

Write-Host "== Pack and sign" -ForegroundColor Cyan
& (Join-Path $SdkBin "makeappx.exe") pack /d $layout /p $msix /o
if ($LASTEXITCODE) { throw "MSIX packing failed (exit code $LASTEXITCODE)." }
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
    $_.Subject -ceq "CN=YRK PDF Test" -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date)
} | Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=YRK PDF Test" `
        -KeyUsage DigitalSignature -FriendlyName "YRK PDF Test" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") `
        -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(1)
}
# Export only the public certificate. Trust and app installation remain manual.
Export-Certificate -Cert $cert -FilePath $cer -Force | Out-Null
& (Join-Path $SdkBin "signtool.exe") sign /fd SHA256 /sha1 $cert.Thumbprint $msix
if ($LASTEXITCODE) { throw "MSIX signing failed (exit code $LASTEXITCODE)." }

Write-Host "== Done" -ForegroundColor Green
Write-Host "MSIX: $msix"
Write-Host "Public certificate: $cer"
Write-Host "Ken: run these commands yourself (admin PowerShell):"
Write-Host "  Import-Certificate -FilePath `"$cer`" -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
Write-Host "  Add-AppxPackage `"$msix`""
Write-Host "Removal: Get-AppxPackage YRK.EasyPdf.Test | Remove-AppxPackage"
Write-Host "Run WACK from the Windows App Cert Kit."
