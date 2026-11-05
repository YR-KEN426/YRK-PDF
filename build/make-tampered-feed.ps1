<#
.SYNOPSIS
Creates two deliberately invalid update feeds for local rejection tests.
.DESCRIPTION
Copies a feed without changing its source, then tampers with Update.exe in one
full package and the full-file signature in the other copy. No keys or network
are used. Reparse points are refused rather than followed. Requires PowerShell 5.1.
.EXAMPLE
powershell -File build\make-tampered-feed.ps1 -Feed C:\test\feed -FixFeedHash
.EXAMPLE
powershell -File build\make-tampered-feed.ps1 -Feed C:\test\feed -Force
Replaces only the two generated sibling folders. Feed metadata stays stale unless
-FixFeedHash is supplied; Velopack may reject on SHA/size before our signature
check, so a test without that switch might not reach our check.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Feed,
    [switch]$Force,
    [switch]$FixFeedHash
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Assert-NoReparseAncestors([string]$Path) {
    $current = Get-Item -LiteralPath $Path -Force
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse points are not allowed: $($current.FullName)"
        }
        $current = $current.Parent
    }
}

function Get-SafeTree([string]$Root) {
    foreach ($item in Get-ChildItem -LiteralPath $Root -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse points are not allowed: $($item.FullName)"
        }
        $item
        if ($item.PSIsContainer) { Get-SafeTree $item.FullName }
    }
}

function Get-ReadOnlyHash([string]$Path, [string]$Algorithm = 'SHA256') {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open,
        [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $hasher = [Security.Cryptography.HashAlgorithm]::Create($Algorithm)
    try {
        return [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '')
    }
    finally { $hasher.Dispose(); $stream.Dispose() }
}

function Get-SourceSnapshot([string]$Root) {
    Assert-NoReparseAncestors $Root
    $snapshot = @{}
    foreach ($item in @(Get-SafeTree $Root)) {
        if (!$item.PSIsContainer) {
            $snapshot.Add($item.FullName.Substring($Root.Length + 1),
                (Get-ReadOnlyHash $item.FullName))
        }
    }
    return $snapshot
}

function Assert-SafeOutput([string]$Path, [string]$Expected) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if (![string]::Equals($full, $Expected, [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($full, $feedPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe output path: $Path"
    }
    Assert-NoReparseAncestors ([IO.Path]::GetDirectoryName($full))
    if (Test-Path -LiteralPath $full) {
        $item = Get-Item -LiteralPath $full -Force
        $resolved = (Resolve-Path -LiteralPath $full).ProviderPath.TrimEnd('\', '/')
        if (!$item.PSIsContainer -or
            ![string]::Equals($resolved, $Expected, [StringComparison]::OrdinalIgnoreCase) -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Unsafe existing output: $full"
        }
        # Reject links anywhere below the output before a recursive deletion.
        $null = @(Get-SafeTree $full)
    }
}

function Copy-Feed([string]$Destination) {
    $null = [IO.Directory]::CreateDirectory($Destination)
    foreach ($item in @(Get-SafeTree $feedPath)) {
        $target = Join-Path $Destination $item.FullName.Substring($feedPath.Length + 1)
        if ($item.PSIsContainer) { $null = [IO.Directory]::CreateDirectory($target) }
        else {
            # Hold a read-only, read-sharing handle while copying a source file.
            $readLock = [IO.File]::Open($item.FullName, [IO.FileMode]::Open,
                [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try { [IO.File]::Copy($item.FullName, $target, $false) }
            finally { $readLock.Dispose() }
        }
    }
}

function Update-JsonAssets($Node, [string]$Label, $Hashes, $Changes) {
    if ($null -eq $Node) { return }
    if ($Node -is [System.Management.Automation.PSCustomObject]) {
        $fileName = $Node.PSObject.Properties['FileName']
        if ($null -ne $fileName -and $fileName.Value -ceq $packageName) {
            foreach ($field in @('SHA1', 'SHA256', 'Size')) {
                $property = $Node.PSObject.Properties[$field]
                if ($null -ne $property) {
                    $property.Value = $Hashes[$field]
                    $null = $Changes.Add($field)
                    Write-Host "Updated $Label : $packageName : $field"
                }
            }
        }
        foreach ($property in @($Node.PSObject.Properties)) {
            Update-JsonAssets $property.Value $Label $Hashes $Changes
        }
    }
    elseif ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [string]) {
        foreach ($value in $Node) { Update-JsonAssets $value $Label $Hashes $Changes }
    }
}

$feedItem = Get-Item -LiteralPath $Feed -Force
if (!$feedItem.PSIsContainer) { throw 'Feed must be a folder.' }
Assert-NoReparseAncestors $feedItem.FullName
$feedPath = (Resolve-Path -LiteralPath $Feed).ProviderPath.TrimEnd('\', '/')
if ($feedPath -eq [IO.Path]::GetPathRoot($feedPath).TrimEnd('\', '/')) {
    throw 'A filesystem root cannot be used as a feed.'
}
$updateOutput = $feedPath + '-tampered-updateexe'
$sigOutput = $feedPath + '-tampered-sig'
# Snapshot every source file before inspecting packages or changing outputs.
$before = Get-SourceSnapshot $feedPath
try {
    $packages = @(Get-ChildItem -LiteralPath $feedPath -File -Force |
        Where-Object { $_.Name -like '*-full.nupkg' })
    if ($packages.Count -ne 1) { throw 'Feed must contain exactly one *-full.nupkg.' }
    $packageName = $packages[0].Name
    $sigName = $packageName.Substring(0, $packageName.Length - '-full.nupkg'.Length) + '.sig'
    $sourceSig = Join-Path $feedPath $sigName
    if (![IO.File]::Exists($sourceSig)) { throw "Missing full-file signature: $sigName" }
    $originalPackageHash = Get-ReadOnlyHash $packages[0].FullName
    $originalSigHash = Get-ReadOnlyHash $sourceSig

    # Validate BOTH destinations before deleting either of them.
    foreach ($output in @($updateOutput, $sigOutput)) {
        Assert-SafeOutput $output $output
        if ((Test-Path -LiteralPath $output) -and !$Force) {
            throw "Output already exists; use -Force to replace: $output"
        }
    }
    foreach ($output in @($updateOutput, $sigOutput)) {
        if (Test-Path -LiteralPath $output) {
            Assert-SafeOutput $output $output
            Remove-Item -LiteralPath $output -Recurse -Force
        }
        Copy-Feed $output
    }

    $tamperedPackage = Join-Path $updateOutput $packageName
    $zip = [IO.Compression.ZipFile]::Open($tamperedPackage, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entries = @($zip.Entries | Where-Object {
            ($_.FullName -split '[/\\]')[-1] -ieq 'Update.exe'
        })
        if ($entries.Count -ne 1) { throw 'Package must contain exactly one Update.exe entry.' }
        $entry = $entries[0]
        $stream = $entry.Open()
        try {
            $null = $stream.Seek(0, [IO.SeekOrigin]::End)
            $suffix = [byte[]]@(255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255)
            $stream.Write($suffix, 0, $suffix.Length)
        }
        finally { $stream.Dispose() }
    }
    finally { $zip.Dispose() }

    # Without -FixFeedHash, Velopack may reject stale SHA/size metadata before
    # our signature verification, so that test might never reach our check.
    if ($FixFeedHash) {
        $hashes = @{
            SHA1 = Get-ReadOnlyHash $tamperedPackage 'SHA1'
            SHA256 = Get-ReadOnlyHash $tamperedPackage
            Size = (Get-Item -LiteralPath $tamperedPackage).Length
        }
        $changedFiles = 0
        foreach ($file in Get-ChildItem -LiteralPath $updateOutput -File -Force) {
            if ($file.Name -like 'releases.*.json' -or $file.Name -like 'assets.*.json') {
                $json = [IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json
                $changes = New-Object 'System.Collections.Generic.List[string]'
                Update-JsonAssets $json $file.Name $hashes $changes
                if ($changes.Count -gt 0) {
                    # PS 5.1 supports depth 100; preserve all other fields/values.
                    $text = ConvertTo-Json -InputObject $json -Depth 100
                    [IO.File]::WriteAllText($file.FullName, $text, $utf8)
                    $changedFiles++
                }
            }
            elseif ($file.Name -like 'RELEASES*' -and $file.Extension -ne '.json') {
                $text = [IO.File]::ReadAllText($file.FullName)
                $pattern = '(?m)^(?<lead>[ \t]*)\S+(?<sep>[ \t]+)' +
                    [regex]::Escape($packageName) + '(?<sep2>[ \t]+)\d+(?<tail>[ \t]*\r?)$'
                if ([regex]::IsMatch($text, $pattern)) {
                    $text = [regex]::Replace($text, $pattern, [Text.RegularExpressions.MatchEvaluator]{
                        param($match)
                        $match.Groups['lead'].Value + $hashes.SHA1 +
                            $match.Groups['sep'].Value + $packageName +
                            $match.Groups['sep2'].Value + $hashes.Size + $match.Groups['tail'].Value
                    })
                    [IO.File]::WriteAllText($file.FullName, $text, $utf8)
                    Write-Host "Updated $($file.Name) : $packageName : SHA1, Size"
                    $changedFiles++
                }
            }
        }
        if ($changedFiles -eq 0) { Write-Warning '-FixFeedHash found no matching fields or RELEASES lines to update.' }
    }

    $tamperedSig = Join-Path $sigOutput $sigName
    $bytes = [IO.File]::ReadAllBytes($tamperedSig)
    if ($bytes.Length -eq 0) { throw "Cannot flip a byte in an empty signature: $sigName" }
    $middle = [int][Math]::Floor($bytes.Length / 2)
    $bytes[$middle] = [byte]($bytes[$middle] -bxor 0x01)
    [IO.File]::WriteAllBytes($tamperedSig, $bytes)

    Write-Host "Update.exe feed: $updateOutput"
    Write-Host "Signature feed:  $sigOutput"
    Write-Host "Package SHA256 original: $originalPackageHash"
    Write-Host "Package SHA256 tampered: $(Get-ReadOnlyHash $tamperedPackage)"
    Write-Host "Signature SHA256 original: $originalSigHash"
    Write-Host "Signature SHA256 tampered: $(Get-ReadOnlyHash $tamperedSig)"
}
finally {
    $after = Get-SourceSnapshot $feedPath
    $unchanged = $before.Count -eq $after.Count
    foreach ($name in $before.Keys) {
        if (!$after.ContainsKey($name) -or $before[$name] -cne $after[$name]) { $unchanged = $false }
    }
    if (!$unchanged) { throw 'Source unchanged check: FAILED (file names or SHA256 hashes differ).' }
    Write-Host "Source unchanged check: PASS ($($before.Count) files)."
}
