<#
.SYNOPSIS
    Builds the Windows installer for Video Player.

.DESCRIPTION
    Publishes a self-contained win-x64 build and packages it with Velopack,
    producing an installer, a portable zip, and the update feed.

    IMPORTANT: this publishes to a temporary directory rather than in-place.
    The repository lives under a path containing an apostrophe ("Alex's
    Files"), and the .NET SDK's publish target builds its destination list with
    a single-quoted MSBuild transform:

        DestinationFiles="@(_ResolvedFileToPublishPreserveNewest->'$(PublishDir)%(RelativePath)')"

    The apostrophe closes that string early, so all ~830 destinations collapse
    into one and the build dies with MSB3094 ("DestinationFiles refers to 1
    item(s), and SourceFiles refers to 830 item(s)"). Publishing to an
    apostrophe-free path sidesteps it entirely. Do not "simplify" this back to
    an in-tree publish.

.PARAMETER Version
    Package version, e.g. 2.0.1. Velopack uses this to decide what is an update.
#>
param(
    [string]$Version = "2.0.0",
    [string]$OutDir = "$PSScriptRoot\..\releases",
    # Supply to sign the output with certs\vault-movies-signing.pfx.
    # Create that first with scripts\new-signing-cert.ps1.
    [string]$PfxPassword
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\App\VideoPlayer.App.csproj"
$staging = Join-Path $env:TEMP "videoplayer-publish"   # no apostrophe -- see above

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host "Installing the Velopack CLI (vpk)..." -ForegroundColor Cyan
    dotnet tool install -g vpk
    $env:PATH += ";$env:USERPROFILE\.dotnet\tools"
}

Write-Host "Publishing self-contained win-x64 to $staging ..." -ForegroundColor Cyan
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
dotnet publish $project -c Release -r win-x64 --self-contained true -o $staging --nologo
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

New-Item -ItemType Directory -Force $OutDir | Out-Null

Write-Host "Packaging version $Version ..." -ForegroundColor Cyan
vpk pack `
    --packId VideoPlayer `
    --packVersion $Version `
    --packDir $staging `
    --mainExe VideoPlayer.App.exe `
    --packTitle "Vault Movies" `
    --packAuthors "Jump Vault LLC" `
    --icon "$PSScriptRoot\..\src\App\Assets\app.ico" `
    --splashImage "$PSScriptRoot\..\src\App\Assets\install-splash.png" `
    --splashProgressColor "#C8442A" `
    --shortcuts "Desktop,StartMenuRoot" `
    -o $OutDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

if ($PfxPassword) {
    $pfx = Join-Path $PSScriptRoot "..\certs\vault-movies-signing.pfx"
    if (-not (Test-Path $pfx)) { throw "No certificate at $pfx -- run scripts\new-signing-cert.ps1 first." }

    Write-Host "Signing the installer..." -ForegroundColor Cyan
    $secure = ConvertTo-SecureString -String $PfxPassword -Force -AsPlainText
    $cert = Get-PfxCertificate -FilePath $pfx -Password $secure
    $setup = Join-Path $OutDir "VideoPlayer-win-Setup.exe"

    # A timestamp keeps the signature valid after the certificate expires;
    # without one, everything signed becomes untrusted on expiry day.
    $result = Set-AuthenticodeSignature -FilePath $setup -Certificate $cert `
        -HashAlgorithm SHA256 -TimestampServer "http://timestamp.digicert.com"
    Write-Host ("  $($result.Status): $($result.StatusMessage)") -ForegroundColor Gray
    if ($result.Status -notin @('Valid', 'UnknownError')) { throw "signing failed: $($result.Status)" }
}

Write-Host "`nDone. Artifacts in $OutDir :" -ForegroundColor Green
Get-ChildItem $OutDir -File | ForEach-Object {
    "  {0,-40} {1,6} MB" -f $_.Name, [math]::Round($_.Length / 1MB)
}
Write-Host @"

Upload to your site:
  VideoPlayer-win-Setup.exe   what people download and run
  *-full.nupkg + RELEASES     the update feed; keep these together so
                              installed copies can find future versions
"@ -ForegroundColor Gray
