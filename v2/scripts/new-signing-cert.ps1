<#
.SYNOPSIS
    Creates a self-signed code-signing certificate for Vault Movies.

.DESCRIPTION
    Run this ONCE. It creates a certificate in your personal store and exports a
    .pfx that pack.ps1 uses to sign the installer.

    READ THIS BEFORE EXPECTING TOO MUCH OF IT
    -----------------------------------------
    A self-signed certificate does NOT stop the SmartScreen warning. Windows
    trusts a signature only if it chains to a Certificate Authority it already
    trusts, and a certificate you made yourself does not. On another person's
    machine the installer will still say "Windows protected your PC".

    What it does buy you:

      * Tamper evidence -- the signature breaks if the installer is modified.
      * A named publisher on the file's Digital Signatures tab.
      * Trusted status on machines where you deliberately install the
        certificate as a trusted root (sensible for your own machines, not
        something to ask friends to do casually).

    To actually remove the warning for everyone, you need an OV or EV
    code-signing certificate from a commercial CA (roughly $200-400/year).

.PARAMETER PfxPassword
    Password protecting the exported .pfx. Choose your own and keep it safe --
    anyone with the file and this password can sign software as you.
#>
param(
    [Parameter(Mandatory = $true)][string]$PfxPassword,
    [string]$Subject = "CN=Jump Vault LLC, O=Jump Vault LLC, C=US",
    [int]$YearsValid = 5
)

$ErrorActionPreference = "Stop"
$certDir = Join-Path $PSScriptRoot "..\certs"
New-Item -ItemType Directory -Force $certDir | Out-Null
$pfxPath = Join-Path $certDir "vault-movies-signing.pfx"

if (Test-Path $pfxPath) {
    Write-Host "A signing certificate already exists at $pfxPath" -ForegroundColor Yellow
    Write-Host "Delete it first if you really want to replace it." -ForegroundColor Yellow
    return
}

Write-Host "Creating a self-signed code-signing certificate..." -ForegroundColor Cyan
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -KeyUsage DigitalSignature `
    -KeyLength 3072 `
    -KeyAlgorithm RSA `
    -HashAlgorithm SHA256 `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -NotAfter (Get-Date).AddYears($YearsValid)

$secure = ConvertTo-SecureString -String $PfxPassword -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $secure | Out-Null

# The public half, for anyone who wants to trust this publisher deliberately.
$cerPath = Join-Path $certDir "vault-movies-signing.cer"
Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  thumbprint : $($cert.Thumbprint)"
Write-Host "  private key: $pfxPath   (KEEP THIS SECRET -- it is gitignored)"
Write-Host "  public cert: $cerPath"
Write-Host ""
Write-Host "Sign a build with:" -ForegroundColor Gray
Write-Host "  .\scripts\pack.ps1 -Version 2.3.0 -PfxPassword '<your password>'" -ForegroundColor Gray
Write-Host ""
Write-Host "SmartScreen will still warn on other machines: a self-signed certificate" -ForegroundColor Yellow
Write-Host "is not trusted by Windows. Only a CA-issued certificate removes that." -ForegroundColor Yellow
