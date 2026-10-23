# Authenticode-signs files with a .pfx, using PowerShell only (no Windows SDK). Called by vpk through
# build\release.ps1, which passes the certificate in YRK_SIGN_PFX / YRK_SIGN_PFX_PASSWORD and the
# timestamp server in YRK_SIGN_TIMESTAMP (empty = no timestamp).
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Files)
$ErrorActionPreference = "Stop"

$certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2($env:YRK_SIGN_PFX, $env:YRK_SIGN_PFX_PASSWORD)
if (-not $certificate.HasPrivateKey) { throw "The certificate has no private key." }

foreach ($file in $Files) {
    $path = $file.Trim('"')
    $arguments = @{ FilePath = $path; Certificate = $certificate; HashAlgorithm = "SHA256" }
    if ($env:YRK_SIGN_TIMESTAMP) { $arguments.TimestampServer = $env:YRK_SIGN_TIMESTAMP }
    $result = Set-AuthenticodeSignature @arguments

    # "UnknownError" means signed but the certificate is not trusted on this computer (a test certificate).
    if ($result.Status -notin @("Valid", "UnknownError")) {
        throw "Signing $path failed: $($result.Status) $($result.StatusMessage)"
    }

    Write-Host "Signed $(Split-Path $path -Leaf) ($($result.Status))"
}
