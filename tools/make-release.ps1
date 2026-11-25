# Builds the two release zips for the current version into ..\_release (next to this repo):
#   NO-Aces-High-<version>.zip  - extract into the game folder (BepInEx/plugins/NOAcesHigh/...), plus README, LICENSE, CHANGELOG
#   NOAcesHigh-nomm.zip         - for NOMM / NOMNOM: the plugin folder's contents at the top of the archive
# Only files tracked in git go in, so local extras (music, artwork kept out of the repo) never ship.
# Prints the version and the SHA-256 of the NOMM zip for the NOMNOM manifest.
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$version = (Select-String -Path "Plugin.cs" -Pattern 'Version = "([0-9.]+)"').Matches[0].Groups[1].Value
$projVersion = (Select-String -Path "NOAcesHigh.csproj" -Pattern "<Version>([0-9.]+)</Version>").Matches[0].Groups[1].Value
if ($projVersion -ne $version) { throw "Plugin.cs says $version but NOAcesHigh.csproj says $projVersion; NOMNOM needs the DLL metadata to match" }
dotnet build -c Release | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$out = Join-Path (Split-Path -Parent $repo) "_release"
$stage = Join-Path $out "stage-$version"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
$plugin = Join-Path $stage "plugin"            # the contents of BepInEx/plugins/NOAcesHigh
New-Item -ItemType Directory -Force (Join-Path $plugin "AceSquadrons") | Out-Null
Copy-Item "bin/Release/NOAcesHigh.dll" $plugin
foreach ($f in (git ls-files Squadrons)) {
    $dest = Join-Path $plugin ("AceSquadrons" + $f.Substring("Squadrons".Length))
    New-Item -ItemType Directory -Force (Split-Path -Parent $dest) | Out-Null
    Copy-Item $f $dest
}
Copy-Item README.md, LICENSE, CHANGELOG.md $plugin

# Manual zip: game-folder layout.
$manual = Join-Path $stage "manual"
New-Item -ItemType Directory -Force (Join-Path $manual "BepInEx/plugins") | Out-Null
Copy-Item -Recurse $plugin (Join-Path $manual "BepInEx/plugins/NOAcesHigh")
Copy-Item README.md, LICENSE, CHANGELOG.md $manual
New-Item -ItemType Directory -Force (Join-Path $manual "docs") | Out-Null
Copy-Item "docs/logo.svg" (Join-Path $manual "docs")

$manualZip = Join-Path $out "NO-Aces-High-$version.zip"
$nommZip = Join-Path $out "NOAcesHigh-nomm.zip"
Remove-Item -Force -ErrorAction SilentlyContinue $manualZip, $nommZip
# Windows own tar writes standard zips with forward-slash paths (Compress-Archive writes backslashes, which break on Linux).
$tar = Join-Path $env:SystemRoot "System32\tar.exe"
Push-Location $manual; & $tar -a -c -f $manualZip *; Pop-Location
Push-Location $plugin; & $tar -a -c -f $nommZip *; Pop-Location
if (-not (Test-Path $manualZip) -or -not (Test-Path $nommZip)) { throw "zip failed" }
Remove-Item -Recurse -Force $stage

$hash = (Get-FileHash -Algorithm SHA256 $nommZip).Hash.ToLowerInvariant()
"version $version"
"manual  $manualZip"
"nomm    $nommZip"
"sha256:$hash"
