<#
.SYNOPSIS
    Builds the plugin and copies it into a local Emby server's plugin folder.

.DESCRIPTION
    Emby loads plugins from <programdata>\plugins as plain DLLs. This script
    builds against the target server's own assemblies (so the ABI always
    matches), backs up any previously installed copy, and copies the new DLL in.

    Emby only unloads plugin assemblies on restart, so the file is locked while
    the server runs. Use -StopEmby to have the script stop the server first;
    otherwise it tells you to stop it and exits without touching anything.

.PARAMETER EmbySystemDir
    Emby's "system" folder (contains EmbyServer.exe). Auto-detected.

.PARAMETER EmbyDataDir
    Emby's "programdata" folder (contains plugins, logs, config). Defaults to a
    sibling of EmbySystemDir, which is how the portable build lays things out.

.PARAMETER StopEmby
    Stop EmbyServer before copying. Without it, a running server aborts the copy.

.PARAMETER StartEmby
    Start EmbyServer again after copying (implies -StopEmby).

.EXAMPLE
    .\scripts\install-local.ps1 -EmbySystemDir 'E:\Emby-Server\system' -StopEmby -StartEmby
#>
[CmdletBinding()]
param(
    [string]$EmbySystemDir,
    [string]$EmbyDataDir,
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$StopEmby,
    [switch]$StartEmby
)

$ErrorActionPreference = 'Stop'
if ($StartEmby) { $StopEmby = $true }

$repoRoot = Split-Path -Parent $PSScriptRoot

function Test-EmbySystemDir([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $false }
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    return Test-Path -LiteralPath (Join-Path $path 'MediaBrowser.Controller.dll')
}

if (-not (Test-EmbySystemDir $EmbySystemDir)) {
    $EmbySystemDir = $null
    foreach ($candidate in @($env:EMBY_SYSTEM_DIR, 'E:\Emby-Server\system', 'D:\Emby-Server\system', 'C:\Emby-Server\system')) {
        if (Test-EmbySystemDir $candidate) { $EmbySystemDir = $candidate; break }
    }
}
if (-not $EmbySystemDir) {
    throw 'Could not locate the Emby system directory. Pass -EmbySystemDir explicitly.'
}

if (-not $EmbyDataDir) {
    $EmbyDataDir = Join-Path (Split-Path -Parent $EmbySystemDir) 'programdata'
}
if (-not (Test-Path -LiteralPath $EmbyDataDir)) {
    throw "Emby data directory not found: $EmbyDataDir (pass -EmbyDataDir explicitly)."
}

$pluginDir = Join-Path $EmbyDataDir 'plugins'
if (-not (Test-Path -LiteralPath $pluginDir)) {
    New-Item -ItemType Directory -Path $pluginDir | Out-Null
}

Write-Host "Emby system : $EmbySystemDir"
Write-Host "Emby data   : $EmbyDataDir"
Write-Host "Plugin dir  : $pluginDir"
Write-Host ''

& (Join-Path $PSScriptRoot 'build.ps1') -EmbySystemDir $EmbySystemDir -Configuration $Configuration

$source = Join-Path $repoRoot "src\Emby.Plugins.Bangumi\bin\$Configuration\Emby.Plugins.Bangumi.dll"
$target = Join-Path $pluginDir 'Emby.Plugins.Bangumi.dll'

$running = @(Get-Process -Name 'EmbyServer' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    if (-not $StopEmby) {
        Write-Warning "EmbyServer is running (PID $($running.Id -join ', ')) and holds a lock on the installed plugin."
        Write-Warning 'Stop Emby and re-run, or re-run with -StopEmby -StartEmby. Nothing was copied.'
        exit 2
    }

    Write-Host "Stopping EmbyServer (PID $($running.Id -join ', '))..." -ForegroundColor Yellow
    foreach ($process in $running) {
        $process.CloseMainWindow() | Out-Null
    }
    Wait-Process -Name 'EmbyServer' -Timeout 30 -ErrorAction SilentlyContinue
    $still = @(Get-Process -Name 'EmbyServer' -ErrorAction SilentlyContinue)
    if ($still.Count -gt 0) {
        Write-Warning 'EmbyServer did not exit within 30s. Close it manually and re-run; nothing was copied.'
        exit 2
    }
    Write-Host 'EmbyServer stopped.' -ForegroundColor Green
}

if (Test-Path -LiteralPath $target) {
    $backup = "$target.bak-{0:yyyyMMdd-HHmmss}" -f (Get-Date)
    Copy-Item -LiteralPath $target -Destination $backup -Force
    Write-Host "Backed up existing plugin -> $backup" -ForegroundColor DarkGray
}

Copy-Item -LiteralPath $source -Destination $target -Force
$info = Get-Item -LiteralPath $target
Write-Host ("Installed: {0} ({1:N0} bytes)" -f $info.FullName, $info.Length) -ForegroundColor Green

if ($StartEmby) {
    $exe = Join-Path $EmbySystemDir 'EmbyServer.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "EmbyServer.exe not found at $exe" }
    Write-Host 'Starting EmbyServer...' -ForegroundColor Yellow
    # -programdata matters: without it a portable install falls back to a
    # different data directory and appears to have lost every library.
    Start-Process -FilePath $exe -WorkingDirectory $EmbySystemDir -WindowStyle Hidden `
        -ArgumentList @('-programdata', $EmbyDataDir, '-nolaunchbrowser')
    Write-Host 'Started. Give it ~20s, then check the log below for load errors.' -ForegroundColor Green
}
else {
    Write-Host ''
    Write-Host 'Restart Emby to load the new build.' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Verify with:' -ForegroundColor Cyan
Write-Host ("  Select-String -Path '{0}\logs\embyserver.txt' -Pattern 'Bangumi|TypeLoadException|MissingMethodException'" -f $EmbyDataDir)
Write-Host '  then open Emby -> Dashboard -> Plugins -> Bangumi 番组计划'