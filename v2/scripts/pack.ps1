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

# The library server ships inside the app, in its own folder.
#
# Without this the app can only ever join somebody else's server: the code that
# starts a local one looks beside the executable and then up a development tree,
# so a released build failed with "Build VideoPlayer.Rendezvous first" — a
# developer's error message shown to a viewer. A subfolder rather than the same
# directory because the two are separate .NET applications with their own
# dependency sets, and merging them is how one shadows the other's assemblies.
$server = Join-Path $staging "server"
Write-Host "Publishing the library server to $server ..." -ForegroundColor Cyan
$serverProject = Join-Path $PSScriptRoot "..\src\Rendezvous\VideoPlayer.Rendezvous.csproj"
dotnet publish $serverProject -c Release -r win-x64 --self-contained true -o $server --nologo
if ($LASTEXITCODE -ne 0) { throw "server publish failed" }
if (-not (Test-Path (Join-Path $server "VideoPlayer.Rendezvous.exe"))) { throw "server exe missing" }
Write-Host ("  server = {0:N0} MB" -f ((Get-ChildItem $server -Recurse | Measure-Object Length -Sum).Sum / 1MB)) -ForegroundColor Gray

# ffmpeg and ffprobe go in beside the server.
#
# The deployed server gets these from its container image and so never noticed
# they were missing here. On a PC they are not on PATH, and without them the
# hosted library has no runtimes, no posters, and — the one that matters — it
# cannot repackage MKV or HEVC films, which is every rip and everything a
# television actually needs. The symptom arrives much later, as a TV refusing a
# film, with nothing pointing back to a missing binary. So it is checked here.
$tools = Join-Path $PSScriptRoot "..\tools"
foreach ($tool in @("ffmpeg.exe", "ffprobe.exe")) {
    $source = Join-Path $tools $tool
    if (Test-Path $source) {
        Copy-Item $source (Join-Path $server $tool) -Force
        Write-Host ("  {0} -> server" -f $tool) -ForegroundColor Gray
    }
    else {
        # Both are required, and ffprobe is not the softer half it sounds like:
        # it fills in each film's video codec, and HlsRemuxer.CanRemux refuses a
        # film whose codec is unknown. Without it every film on a phone or a
        # television answers "can't be repackaged for a browser" — so shipping
        # without it would be shipping a hosting feature that cannot serve a TV.
        throw "tools\$tool is missing. A hosted library needs both ffmpeg.exe and ffprobe.exe: without them nothing plays on a phone or a television. Put both, from the same build, into tools\."
    }
}

# Pre-build libVLC's plugin cache so shipped copies don't scan ~320 native
# plugin DLLs on first launch (~12 s cold). The app writes plugins.dat with its
# OWN libVLC, so the cache always matches the bundled version.
Write-Host "Generating libVLC plugin cache ..." -ForegroundColor Cyan
$genExe = Join-Path $staging "VideoPlayer.App.exe"
Start-Process $genExe -ArgumentList "--gen-plugin-cache" -Wait
$cache = Join-Path $staging "libvlc\win-x64\plugins\plugins.dat"
if (-not (Test-Path $cache)) { throw "plugin cache was not generated" }
Write-Host ("  plugins.dat = {0:N0} KB" -f ((Get-Item $cache).Length / 1KB)) -ForegroundColor Gray

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
