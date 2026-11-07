# Shows how often each release of YRK PDF was downloaded, from GitHub's own counters (the app sends
# nothing). Needs no token: it reads the public releases repository.
#
#   powershell -ExecutionPolicy Bypass -File build\download-stats.ps1
#
# Setup and Portable are new installs. Updates are the update packages (full or delta) of that version,
# fetched by installed copies that update themselves, so they show roughly how many copies are in use.
#
# Each run adds one row to stats\download-history.csv (kept on this computer only, see .gitignore) and
# shows the change since the previous run and since about a week ago. Run it once a week.
param(
    [string]$GitHubRepo = $(if ($env:YRK_GITHUB_REPO) { $env:YRK_GITHUB_REPO } else { "YR-KEN426/yrk-easy-pdf-release" }),
    [string]$History = (Join-Path $PSScriptRoot "..\stats\download-history.csv")
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

# History: one row per run. LatestUpdates (update packages of the newest release) is the best guess at
# how many installed copies are in use; it grows for a few days after each release.
$latest = $rows | Select-Object -First 1
$now = [pscustomobject]@{
    Date          = (Get-Date).ToString("yyyy-MM-dd HH:mm")
    Installs      = $installs
    Updates       = $updated
    LatestVersion = $latest.Version
    LatestUpdates = $latest.Updates
    Stars         = $repo.stargazers_count
}
# @() outside the if: PowerShell 5 unwraps a one-row result into a single object with no Count.
$old = @(if (Test-Path $History) { Import-Csv $History })
New-Item -ItemType Directory -Force (Split-Path $History) | Out-Null
$now | Export-Csv $History -Append -NoTypeInformation -Encoding UTF8
Write-Host ""
Write-Host "Saved to $((Resolve-Path $History).Path) ($($old.Count + 1) rows)"
function Show-Change([string]$label, $then) {
    if ($null -eq $then) { return }
    Write-Host ("Since {0} ({1}): installs +{2}, updates +{3}, stars +{4}" -f $label, $then.Date,
        ($installs - [int]$then.Installs), ($updated - [int]$then.Updates), ($repo.stargazers_count - [int]$then.Stars))
}
if ($old.Count -gt 0) {
    Show-Change "last run" $old[-1]
    $weekAgo = $old | Where-Object { [datetime]$_.Date -le (Get-Date).AddDays(-6) } | Select-Object -Last 1
    Show-Change "a week ago" $weekAgo
} else {
    Write-Host "First snapshot: run again next week to see the change."
}
Write-Host "Page visits (last 14 days, signed in as owner): https://github.com/$GitHubRepo/graphs/traffic"
