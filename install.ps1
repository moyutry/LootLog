# Builds LootLog, packs the installer + auto-updates (Velopack), installs it on this PC, and optionally publishes it.
#
#   dist\releases\LootLog-win-Setup.exe      one-click installer (what players download)
#   dist\releases\LootLog-win-Portable.zip   no-install version
#   dist\releases\*.nupkg, releases.win.json the update feed the app reads from GitHub Releases
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File install.ps1              build + pack + install here
#   powershell -ExecutionPolicy Bypass -File install.ps1 -NoInstall   build + pack only
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Publish     ...and upload to GitHub Releases (uses the gh login)
#
# Code signing (removes the "unknown publisher" part of the warnings once you have a certificate):
#   $env:LOOTLOG_SIGN_PARAMS = '/f cert.pfx /p <password> /fd sha256 /tr http://timestamp.digicert.com /td sha256'
#   or, for a cloud certificate, a signtool-style command with {{file}}:  $env:LOOTLOG_SIGN_TEMPLATE = '...'
#
# Your data (settings, chest counts, match history) lives in %APPDATA%\FortniteHelper and is never touched.
param([switch]$NoInstall, [switch]$Publish, [switch]$SkipBuild)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$repo = "https://github.com/moyutry/LootLog"
$dist = Join-Path $root "dist\LootLog"
$releases = Join-Path $root "dist\releases"

# Use whichever dotnet has a .NET 10 SDK (the one on PATH may only have older SDKs).
$candidates = @((Get-Command dotnet -ErrorAction SilentlyContinue).Source, (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe")) |
    Where-Object { $_ -and (Test-Path $_) }
$dotnetPath = $candidates | Where-Object { (& $_ --list-sdks 2>$null) -match '^10\.' } | Select-Object -First 1
if (-not $dotnetPath) { throw "The .NET 10 SDK is needed to build. Get it from https://dot.net" }
$env:DOTNET_ROOT = Split-Path $dotnetPath
$env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) { & $dotnetPath tool install -g vpk | Out-Host }

if (-not ($SkipBuild -and (Test-Path (Join-Path $dist "LootLog.exe")))) {
    Write-Host "Building..." -ForegroundColor Cyan
    if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
    & $dotnetPath publish (Join-Path $root "src\FortniteHelper\FortniteHelper.csproj") -c Release -r win-x64 --self-contained true `
        -p:PublishReadyToRun=true -p:DebugType=none -p:DebugSymbols=false -o $dist
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
}
$exe = Join-Path $dist "LootLog.exe"
$version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''

# Installer splash, drawn by the app itself
$splash = Join-Path $root "dist\splash.png"
(Start-Process $exe -ArgumentList "--render-splash", "`"$splash`"" -PassThru).WaitForExit()

# Previous release from GitHub, so updates can be small deltas (fine if there is none yet)
New-Item -ItemType Directory -Force $releases | Out-Null
try { vpk download github --repoUrl $repo --outputDir $releases 2>&1 | Out-Host } catch { }

Write-Host "Packing LootLog $version..." -ForegroundColor Cyan
$notes = Join-Path $root "release-notes.md"
$pack = @("pack", "--packId", "LootLog", "--packVersion", $version, "--packDir", $dist, "--mainExe", "LootLog.exe",
    "--runtime", "win-x64", "--packTitle", "LootLog", "--packAuthors", "LootLog", "--icon", (Join-Path $root "src\FortniteHelper\Assets\AppIcon.ico"),
    "--splashImage", $splash, "--splashProgressColor", "#FFD23F", "--outputDir", $releases)
if (Test-Path $notes) { $pack += @("--releaseNotes", $notes) }
if ($env:LOOTLOG_SIGN_PARAMS) { $pack += @("--signParams", $env:LOOTLOG_SIGN_PARAMS) }
elseif ($env:LOOTLOG_SIGN_TEMPLATE) { $pack += @("--signTemplate", $env:LOOTLOG_SIGN_TEMPLATE) }
& vpk @pack
if ($LASTEXITCODE -ne 0) { throw "Packing failed" }
Write-Host "Installer: $releases\LootLog-win-Setup.exe" -ForegroundColor Green

if ($Publish) {
    Write-Host "Publishing v$version to $repo ..." -ForegroundColor Cyan
    $token = (gh auth token).Trim()
    vpk upload github --repoUrl $repo --token $token --outputDir $releases --publish --releaseName "LootLog $version" --tag "v$version" --merge
    if ($LASTEXITCODE -ne 0) { throw "Upload failed" }
}

if (-not $NoInstall) {
    Write-Host "Installing on this PC..." -ForegroundColor Cyan
    Get-Process LootLog, FortniteHelper -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    # Remove copies installed by the older script (before the installer existed)
    foreach ($old in @((Join-Path $env:LOCALAPPDATA "Programs\LootLog"), (Join-Path $env:LOCALAPPDATA "Programs\Fortnite Helper"))) {
        if (Test-Path $old) { Remove-Item $old -Recurse -Force -ErrorAction SilentlyContinue }
    }
    foreach ($dir in @([Environment]::GetFolderPath("Programs"), [Environment]::GetFolderPath("Desktop"))) {
        foreach ($name in @("Fortnite Helper.lnk", "LootLog.lnk")) {
            $lnk = Join-Path $dir $name
            if ((Test-Path $lnk) -and ((New-Object -ComObject WScript.Shell).CreateShortcut($lnk).TargetPath -like "*\Programs\*")) { Remove-Item $lnk -Force }
        }
    }
    (Start-Process (Join-Path $releases "LootLog-win-Setup.exe") -PassThru).WaitForExit()
    Write-Host "Done! LootLog is installed in $env:LOCALAPPDATA\LootLog" -ForegroundColor Green
}
