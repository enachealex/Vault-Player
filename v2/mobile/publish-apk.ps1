<#
.SYNOPSIS
  Publish a built Vault Movies APK to the Vault server, so every installed
  copy of the app offers it as an update.

.DESCRIPTION
  Reads versionCode/versionName from app.json, copies the APK and an
  android.json manifest into the server's data volume (app/), and checks the
  file's SHA-256 on both ends. The server works out size and checksum from the
  file itself; the app downloads it, verifies it and installs it over itself.

.EXAMPLE
  ./publish-apk.ps1 -Notes "Captions on phones; the app now updates itself."
#>
param(
  [string]$Apk = "$PSScriptRoot\android\app\build\outputs\apk\release\app-release.apk",
  [string]$Notes = "",
  [string]$Server = "huckleberry@100.112.51.64"
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Apk)) { throw "No APK at $Apk - build it first (gradlew assembleRelease)." }
$app = (Get-Content "$PSScriptRoot\app.json" -Raw | ConvertFrom-Json).expo
$manifest = [ordered]@{
  versionCode = [int]$app.android.versionCode
  versionName = [string]$app.version
  notes       = $Notes
} | ConvertTo-Json -Compress

$tmpJson = Join-Path ([IO.Path]::GetTempPath()) "android.json"
[IO.File]::WriteAllText($tmpJson, $manifest)
$sha = (Get-FileHash $Apk -Algorithm SHA256).Hash.ToLower()
Write-Host "Publishing $($app.version) (versionCode $($app.android.versionCode)), sha256 $sha"

$dest = '/srv/apps/docker/volumes/deploy_vault_data/_data/app'
scp -q $Apk "${Server}:vault-movies.apk.upload"
scp -q $tmpJson "${Server}:android.json.upload"
# APK first, manifest last: the server never offers a version whose file is not in place.
ssh $Server "set -e; sudo install -d -m 755 $dest; sudo install -m 644 vault-movies.apk.upload $dest/vault-movies.apk.part; sudo mv $dest/vault-movies.apk.part $dest/vault-movies.apk; sudo install -m 644 android.json.upload $dest/android.json; rm -f vault-movies.apk.upload android.json.upload; sudo sha256sum $dest/vault-movies.apk"
Remove-Item $tmpJson
Write-Host "Local sha256:  $sha"
