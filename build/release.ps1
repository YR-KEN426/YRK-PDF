# Builds a release of YRK PDF: tests, self-contained publish, then a Velopack package with
# Setup.exe (per-user install, no admin), a portable zip, and the update feed files.
#
#   powershell -ExecutionPolicy Bypass -File build\release.ps1 -UpdateUrl https://downloads.example.com/pdfstudio
#
# Upload everything in artifacts\releases\<runtime> to the update URL (keep earlier releases there too:
# vpk makes small delta updates from them). Code signing, when configured, signs the app's own files,
# Update.exe and Setup.exe:
#   -SignPfx <file.pfx> (password in YRK_SIGN_PFX_PASSWORD)   signs with PowerShell (no Windows SDK needed)
#   -AzureTrustedSigningMetadata <metadata.json>              signs with Azure Trusted Signing
#
# GitHub Releases as the download site and update feed (the repository must be public, so people can
# download without an account):
#   -GitHubRepo owner/name            builds with https://github.com/owner/name as the update address and
#                                     fetches the previous release first, so updates are small deltas
#   -Publish                          also uploads and publishes the release (token in GITHUB_TOKEN, with
#                                     "Contents: read and write" on that repository only)
#
# Signed updates: every build with an update address signs its package with YRK's update key, and the app
# installs only updates whose signature matches the public key built into it. Create the key once with
#   dotnet run --project build\UpdateSigner -- new-key --private "$env:USERPROFILE\YRK-keys\update-signing-key.pem" --public src\Yrk.PdfStudio.App\Assets\update-public-key.pem
# -UpdateKey points at the private key (default: %USERPROFILE%\YRK-keys\update-signing-key.pem); its
# password is asked for once at the start (or read from YRK_UPDATE_KEY_PASSWORD). Two signatures are made:
# <id>-<version>.sig for the package file, and <id>-<version>.content.sig for the program files inside it,
# which also matches a package that the updater rebuilt from a delta (apps from 1.0.5 on check that one).
#   -NoDelta                          makes no delta update, so every copy downloads the full package. Used
#                                     once for 1.0.5: older copies check only the file signature, which a
#                                     package rebuilt from a delta never matches.
param(
    [string]$Version,
    [string]$UpdateUrl = $env:YRK_UPDATE_URL,
    [string]$GitHubRepo = $env:YRK_GITHUB_REPO,
    [switch]$Publish,
    [string]$UpdateKey = $(if ($env:YRK_UPDATE_KEY) { $env:YRK_UPDATE_KEY } else { Join-Path $env:USERPROFILE "YRK-keys\update-signing-key.pem" }),
    [string]$Runtime = "win-x64",
    [string]$SignPfx = $env:YRK_SIGN_PFX,
    [string]$AzureTrustedSigningMetadata,
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$NoTimestamp,
    [string]$ReleaseNotes,
    [switch]$SkipTests,
    [switch]$NoDelta,
    # Only the last step of -Publish: add the signature to the draft release already on GitHub and publish it.
    [switch]$FinishDraft
)
$ErrorActionPreference = "Stop"

# Attaches the signatures to the draft made by "vpk upload" and publishes it (signatures first, so nobody
# ever sees the release without them).
function Complete-Draft([string]$Repo, [string]$Version, [string[]]$Signatures) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $headers = @{ Authorization = "Bearer $env:GITHUB_TOKEN"; Accept = "application/vnd.github+json"; "X-GitHub-Api-Version" = "2022-11-28" }
    $releaseName = "YRK PDF $Version"

    # Kept in a variable first: Windows PowerShell 5 passes a JSON array down the pipeline as one object.
    $releases = Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$Repo/releases?per_page=30"
    $draft = @($releases | ForEach-Object { $_ } | Where-Object { $_.draft -and $_.name -eq $releaseName })
    if ($draft.Count -ne 1) { throw "Expected one draft release '$releaseName' on GitHub, found $($draft.Count)." }
    $id = [long]$draft[0].id

    foreach ($signature in $Signatures) {
        $sigName = Split-Path $signature -Leaf
        if (-not (@($draft[0].assets) | Where-Object { $_.name -eq $sigName })) {
            Invoke-RestMethod -Headers $headers -Method Post -ContentType "application/octet-stream" -InFile $signature `
                -Uri "https://uploads.github.com/repos/$Repo/releases/$id/assets?name=$sigName" | Out-Null
        }
    }

    Invoke-RestMethod -Headers $headers -Method Patch -ContentType "application/json" -Body '{"draft":false,"make_latest":"true"}' `
        -Uri "https://api.github.com/repos/$Repo/releases/$id" | Out-Null
}
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $root
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { $env:Path = "C:\Program Files\dotnet;" + $env:Path }

if (-not $Version) {
    $Version = ([xml](Get-Content "Directory.Build.props")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { throw "Version '$Version' must look like 1.2.3 (SemVer)." }

$packId = "YRK.EasyPdf"
$publishDir = Join-Path $root "artifacts\publish\$Runtime"
$releaseDir = Join-Path $root "artifacts\releases\$Runtime"
$publicKey = Join-Path $root "src\Yrk.PdfStudio.App\Assets\update-public-key.pem"
$signerProject = Join-Path $PSScriptRoot "UpdateSigner"
$repoUrl = $null
if ($GitHubRepo) {
    if ($GitHubRepo -notmatch '^[\w.-]+/[\w.-]+$') { throw "-GitHubRepo must look like owner/name." }
    $repoUrl = "https://github.com/$GitHubRepo"
    if (-not $UpdateUrl) { $UpdateUrl = $repoUrl }
}
if ($Publish -and -not $repoUrl) { throw "-Publish needs -GitHubRepo." }
if ($Publish -and -not $env:GITHUB_TOKEN) { throw "Set GITHUB_TOKEN to a token that can write releases to $GitHubRepo." }
Write-Host "== YRK PDF $Version ($Runtime)" -ForegroundColor Cyan

if ($FinishDraft) {
    if (-not $repoUrl -or -not $env:GITHUB_TOKEN) { throw "-FinishDraft needs -GitHubRepo and GITHUB_TOKEN." }
    $signatures = @((Join-Path $releaseDir "$packId-$Version.sig"), (Join-Path $releaseDir "$packId-$Version.content.sig")) | Where-Object { Test-Path $_ }
    if (-not $signatures) { throw "No signatures for $Version in $releaseDir. Run the full release first." }
    Write-Host "== Finish the draft release on $repoUrl" -ForegroundColor Cyan
    Complete-Draft -Repo $GitHubRepo -Version $Version -Signatures $signatures
    Write-Host "Published with its signature. Download page: $repoUrl/releases/latest" -ForegroundColor Green
    return
}

if (-not $UpdateUrl) { Write-Warning "No -UpdateUrl: this build will not check for updates." }

$askedForPassword = $false
try {
    if ($UpdateUrl) {
        # An app that updates itself must be able to check its updates: key first, before the long build.
        if (-not (Test-Path $publicKey)) { throw "No update key yet ($publicKey). Create it once with build\UpdateSigner new-key (see the top of this script)." }
        if (-not (Test-Path $UpdateKey)) { throw "The private update key was not found at $UpdateKey (set -UpdateKey)." }
        if (-not $env:YRK_UPDATE_KEY_PASSWORD) {
            $secure = Read-Host -AsSecureString "Update key password"
            $env:YRK_UPDATE_KEY_PASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
            $askedForPassword = $true
        }

        Write-Host "== Update key check" -ForegroundColor Cyan
        $probe = Join-Path ([IO.Path]::GetTempPath()) "yrk-key-probe.bin"
        [IO.File]::WriteAllBytes($probe, [byte[]](1, 2, 3))
        dotnet run --project $signerProject -c Release -- sign --private $UpdateKey --public $publicKey --package $probe --id $packId --version $Version --out "$probe.sig"
        $failed = $LASTEXITCODE
        Remove-Item $probe, "$probe.sig" -ErrorAction SilentlyContinue
        if ($failed) { throw "The update key could not be used (wrong password, or not the key built into the app)." }
    }

    if (-not $SkipTests) {
        Write-Host "== Tests" -ForegroundColor Cyan
        dotnet test --configuration Release --nologo
        if ($LASTEXITCODE) { throw "Tests failed." }
    }

    Write-Host "== Publish" -ForegroundColor Cyan
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish src\Yrk.PdfStudio.App -c Release -r $Runtime --self-contained true --nologo `
        -p:Version=$Version -p:YrkUpdateUrl=$UpdateUrl -p:PublishReadyToRun=true -o $publishDir
    if ($LASTEXITCODE) { throw "Publish failed." }

    $pack = @(
        "vpk", "pack",
        "--packId", $packId,
        "--packVersion", $Version,
        "--packDir", $publishDir,
        "--mainExe", "YrkEasyPdf.exe",
        "--packTitle", "YRK PDF",
        "--packAuthors", "YRK",
        "--icon", "src\Yrk.PdfStudio.App\Assets\app.ico",
        "--runtime", $Runtime,
        "--outputDir", $releaseDir,
        # Tesseract (OCR) needs the Visual C++ runtime; Setup installs it when it is missing.
        "--framework", "vcredist143-$($Runtime.Split('-')[1])"
    )
    if ($ReleaseNotes) { $pack += @("--releaseNotes", $ReleaseNotes) }
    if ($NoDelta) { $pack += @("--delta", "None") }

    if ($SignPfx) {
        if (-not $env:YRK_SIGN_PFX_PASSWORD) { throw "Set YRK_SIGN_PFX_PASSWORD to the password of $SignPfx." }
        $env:YRK_SIGN_PFX = (Resolve-Path $SignPfx).Path
        $env:YRK_SIGN_TIMESTAMP = if ($NoTimestamp) { "" } else { $TimestampUrl }
        $signer = Join-Path $PSScriptRoot "sign.ps1"
        # Passed through vpk's environment variables: Windows PowerShell mangles quotes in native arguments.
        $env:VPK_SIGN_TEMPLATE = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$signer`" {{file}}"
        $env:VPK_SIGN_PARALLEL = "1"
        Write-Host "Signing with $SignPfx"
    }
    elseif ($AzureTrustedSigningMetadata) {
        $pack += @("--azureTrustedSignFile", (Resolve-Path $AzureTrustedSigningMetadata).Path)
        Write-Host "Signing with Azure Trusted Signing"
    }
    else {
        Write-Warning "Not code signed: Windows SmartScreen will warn people who download Setup.exe."
    }

    if ($repoUrl -and -not $NoDelta) {
        # The latest published release, so vpk can make a delta update from it (none yet on the first release).
        Write-Host "== Previous release from $repoUrl" -ForegroundColor Cyan
        $download = @("vpk", "download", "github", "--repoUrl", $repoUrl, "--outputDir", $releaseDir)
        if ($env:GITHUB_TOKEN) { $download += @("--token", $env:GITHUB_TOKEN) }
        dotnet @download
        if ($LASTEXITCODE) { Write-Warning "No earlier release downloaded: this one will have no delta update." }
    }

    Write-Host "== Package" -ForegroundColor Cyan
    dotnet @pack
    if ($LASTEXITCODE) { throw "Packaging failed." }

    $signatures = @()
    if ($UpdateUrl) {
        Write-Host "== Sign the update" -ForegroundColor Cyan
        $package = Join-Path $releaseDir "$packId-$Version-full.nupkg"
        $signatures = @((Join-Path $releaseDir "$packId-$Version.sig"), (Join-Path $releaseDir "$packId-$Version.content.sig"))
        dotnet run --project $signerProject -c Release -- sign --private $UpdateKey --public $publicKey --package $package --id $packId --version $Version `
            --out $signatures[0] --content-out $signatures[1]
        if ($LASTEXITCODE) { throw "Signing the update failed." }
    }

    if ($Publish) {
        Write-Host "== Publish to $repoUrl" -ForegroundColor Cyan
        # Uploaded as a draft first, so the signature is in place before anyone can see the release.
        $releaseName = "YRK PDF $Version"
        dotnet vpk upload github --repoUrl $repoUrl --token $env:GITHUB_TOKEN --outputDir $releaseDir `
            --releaseName $releaseName --tag "v$Version"
        if ($LASTEXITCODE) { throw "Upload failed." }

        Complete-Draft -Repo $GitHubRepo -Version $Version -Signatures $signatures
        Write-Host "Published with its signature. Download page: $repoUrl/releases/latest"
    }
}
finally {
    if ($askedForPassword) { Remove-Item Env:\YRK_UPDATE_KEY_PASSWORD -ErrorAction SilentlyContinue }
}

Write-Host "== Done" -ForegroundColor Green
Get-ChildItem $releaseDir | Where-Object { $_.Name -like "*$Version*" -or $_.Name -like "releases.*" -or $_.Name -like "RELEASES*" -or $_.Name -like "assets.*" } |
    Select-Object Name, @{ Name = "MB"; Expression = { [Math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
if ($Publish) {
    # The download pages show the version number, so they are rebuilt now from the release just published;
    # their download buttons point at the latest release, so upload them right away to keep the two in step.
    Write-Host "== Website pages for $Version" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot "build-site.ps1")
    Write-Host "Next: upload the website pages to $GitHubRepo (Add file > Upload files):" -ForegroundColor Cyan
    Write-Host "  docs\release-repo\site\index.html    -> docs/"
    Write-Host "  docs\release-repo\site\en\index.html -> docs/en/"
}
