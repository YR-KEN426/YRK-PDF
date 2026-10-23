# Shows how often each release of YRK PDF was downloaded, from GitHub's own counters (the app sends
# nothing). Needs no token: it reads the public releases repository.
#
#   powershell -ExecutionPolicy Bypass -File build\download-stats.ps1
#
# Setup and Portable are new installs. Updates are the update packages (full or delta) of that version,
# fetched by installed copies that update themselves, so they show roughly how many copies are in use.
param(
    [string]$GitHubRepo = $(if ($env:YRK_GITHUB_REPO) { $env:YRK_GITHUB_REPO } else { "YR-KEN426/yrk-easy-pdf-release" })
)
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# Assigned directly, not wrapped in @(): Windows PowerShell 5 returns a JSON array as one object.
$releases = Invoke-RestMethod "https://api.github.com/repos/$GitHubRepo/releases?per_page=100" -Headers @{ "User-Agent" = "YRK-download-stats" }

$rows = foreach ($release in $releases) {
    if ($release.draft) { continue }
    $setup = 0; $portable = 0; $updates = 0
    foreach ($asset in @($release.assets)) {
        if ($asset.name -like "*Setup.exe") { $setup += $asset.download_count }
        elseif ($asset.name -like "*Portable.zip") { $portable += $asset.download_count }
        elseif ($asset.name -like "*.nupkg") { $updates += $asset.download_count }
    }
    [pscustomobject]@{
        Version   = $release.tag_name
        Published = ([datetime]$release.published_at).ToLocalTime().ToString("yyyy-MM-dd")
        Setup     = $setup
        Portable  = $portable
        Updates   = $updates
    }
}

$rows | Format-Table -AutoSize
$installs = ($rows | Measure-Object Setup -Sum).Sum + ($rows | Measure-Object Portable -Sum).Sum
$updated = ($rows | Measure-Object Updates -Sum).Sum
Write-Host "New installs (Setup + Portable): $installs"
Write-Host "Updates downloaded:              $updated"

$repo = Invoke-RestMethod "https://api.github.com/repos/$GitHubRepo" -Headers @{ "User-Agent" = "YRK-download-stats" }
Write-Host "Stars: $($repo.stargazers_count)   Watching: $($repo.subscribers_count)"
Write-Host "Page visits (last 14 days, signed in as owner): https://github.com/$GitHubRepo/graphs/traffic"
