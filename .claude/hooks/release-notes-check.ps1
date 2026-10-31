# Stop hook: after a release, Claude can't end a turn quietly until PROJECT_NOTES.md has a section for that
# version, and then keeps asking Ken to approve the commit until that section is in HEAD.
# build\release.ps1 writes the version to .claude\released-version.txt when it publishes.
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$marker = Join-Path $root ".claude\released-version.txt"
if (-not (Test-Path $marker)) { exit 0 }

$version = (Get-Content $marker -TotalCount 1).Trim()
if (-not $version) { exit 0 }

# Blocked once per turn at most, so a stop that was already blocked goes through instead of looping.
$hookInput = [Console]::In.ReadToEnd() | ConvertFrom-Json
if ($hookInput.stop_hook_active) { exit 0 }

$heading = "(?m)^### $([regex]::Escape($version))\s*$"

function Block([string]$Reason) {
    @{ decision = "block"; reason = $Reason } | ConvertTo-Json -Compress
    exit 0
}

$notes = Get-Content (Join-Path $root "PROJECT_NOTES.md") -Raw -Encoding UTF8
if ($notes -notmatch $heading) {
    Block ("YRK PDF $version has been released but PROJECT_NOTES.md has no '### $version' section. " +
        "Read the commits of this version with git log, then add '### $version' under the section of lessons from each release: " +
        "what was learned (bugs, decisions, release problems), and 'none' if there was nothing. Set the latest released version " +
        "and the next version in the overview, and tick or move the finished pending items. The file is public: no personal data, " +
        "keys or file names. Do not commit: show Ken what you wrote and ask whether to commit it.")
}

$committed = (git -C $root show HEAD:PROJECT_NOTES.md) -join "`n"
if ($committed -notmatch $heading) {
    Block "PROJECT_NOTES.md has the '### $version' section but it is not committed. Ask Ken whether to commit it. Commit only after Ken says yes."
}
