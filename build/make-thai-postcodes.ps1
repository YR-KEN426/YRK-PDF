param(
    [string]$SourcePath = (Join-Path $PSScriptRoot '../third-party-data/thailand-geography-json/subdistricts.json'),
    [string]$OutputPath = (Join-Path $PSScriptRoot '../src/Yrk.PdfStudio.Core/Pdf/ETax/thai-postcodes.txt')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$expectedHash = '280886da9c551e2222edcfa734fbb15f9613ed1346033b7b282a3fca18399e9b'
# Verify the exact bytes that will be parsed. The source is data, never PowerShell code.
$bytes = [System.IO.File]::ReadAllBytes($SourcePath)
$sha = [System.Security.Cryptography.SHA256]::Create()
try {
    $actualHash = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
} finally {
    $sha.Dispose()
}
if ($actualHash -ne $expectedHash) {
    throw "Source SHA-256 mismatch: expected $expectedHash, got $actualHash."
}

function Convert-Code($Value, [int]$Digits, [string]$Field) {
    if (($Value -isnot [int] -and $Value -isnot [long]) -or $Value -lt 0) {
        throw "$Field must be a non-negative JSON integer."
    }
    $code = $Value.ToString(('D' + $Digits), [System.Globalization.CultureInfo]::InvariantCulture)
    if ($code -cnotmatch ('^[0-9]{' + $Digits + '}$')) {
        throw "Invalid ${Field}: $code"
    }
    return $code
}

function Assert-Detail($Value, [string]$Field) {
    if ($Value -isnot [string] -or $Value -match '[\t\r\n]') {
        throw "$Field must be a string without tabs or line breaks."
    }
}

$utf8 = New-Object System.Text.UTF8Encoding($false, $true)
$data = ConvertFrom-Json -InputObject ($utf8.GetString($bytes))
if ($data -isnot [array] -or $data.Count -eq 0) { throw 'Expected a non-empty JSON array.' }
$areaPath = Join-Path $PSScriptRoot '../src/Yrk.PdfStudio.Core/Pdf/ETax/thai-areas.txt'
$areas = @{}
foreach ($line in [System.IO.File]::ReadAllLines($areaPath, $utf8)) {
    if ($line -cmatch '^([0-9]{6})\t[^\t]+$') { $areas[$Matches[1]] = $true }
}
if ($areas.Count -eq 0) { throw 'No subdistricts found in thai-areas.txt.' }

$seen = @{}
$included = @{}
$extra = [System.Collections.Generic.List[string]]::new()
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# Thai postal codes: thailand-geography-data/thailand-geography-json, commit 021d18e9a481bc141ea87de4ddeebe843cff2f12; Thailand Post, May 2018. Columns: subdistrictCode, postcode, detailTh, detailEn (tab-separated); primary first.')
foreach ($area in ($data | Sort-Object subdistrictCode)) {
    $code = Convert-Code $area.subdistrictCode 6 'subdistrictCode'
    if ($seen.ContainsKey($code)) { throw "Duplicate subdistrictCode: $code" }
    $seen[$code] = $true
    $main = Convert-Code $area.postalCode 5 'postalCode'
    if ($area.postalCodes -isnot [array] -or $area.postalCodes.Count -eq 0) {
        throw "Missing postalCodes for $code"
    }
    $primaryCount = 0
    $postcodes = @{}
    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in $area.postalCodes) {
        $postcode = Convert-Code $entry.postalCode 5 'postalCodes.postalCode'
        Assert-Detail $entry.detailTh 'detailTh'
        Assert-Detail $entry.detailEn 'detailEn'
        if ($entry.isPrimary -isnot [bool]) { throw "Invalid isPrimary for $code" }
        if ($postcodes.ContainsKey($postcode)) { throw "Duplicate postcode for ${code}: $postcode" }
        $postcodes[$postcode] = $true
        if ($entry.isPrimary) {
            $primaryCount++
            if ($postcode -cne $main) { throw "Primary postcode disagrees with postalCode for $code" }
        }
        $rows.Add([pscustomobject]@{
            Postcode = $postcode; Primary = $entry.isPrimary
            Line = "$code`t$postcode`t$($entry.detailTh)`t$($entry.detailEn)"
        })
    }
    if ($primaryCount -ne 1) { throw "Expected exactly one primary postcode for $code" }
    # Validate even the entries not used by our Revenue Department area list.
    if (-not $areas.ContainsKey($code)) { $extra.Add($code); continue }
    $included[$code] = $true
    foreach ($row in ($rows | Sort-Object @{Expression='Primary'; Descending=$true}, Postcode)) {
        $lines.Add($row.Line)
    }
}

$missing = @($areas.Keys | Where-Object { -not $included.ContainsKey($_) } | Sort-Object)
# Write only after the entire input has passed validation; use stable LF and UTF-8 without BOM.
[System.IO.File]::WriteAllText($OutputPath, (($lines -join "`n") + "`n"), $utf8)
Write-Output "Included subdistricts: $($included.Count)"
Write-Output "Missing from postal data ($($missing.Count)): $($missing -join ', ')"
Write-Output "Not in thai-areas.txt, skipped ($($extra.Count)): $($extra -join ', ')"
Write-Output "Output bytes: $((Get-Item -LiteralPath $OutputPath).Length)"
