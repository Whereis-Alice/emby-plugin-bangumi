<#
.SYNOPSIS
    Builds Emby.Plugins.Bangumi.

.DESCRIPTION
    Prefers compiling against the exact assemblies of a local Emby install so the
    produced DLL is ABI-identical to the server that will load it. Falls back to
    the NuGet packages (MediaBrowser.Server.Core / MediaBrowser.Common 4.9.1.90)
    when no Emby system directory is given or found.

.PARAMETER EmbySystemDir
    The Emby "system" folder, i.e. the one containing EmbyServer.exe and
    MediaBrowser.Controller.dll. Auto-detected from common portable layouts and
    from the EMBY_SYSTEM_DIR environment variable.

.PARAMETER Configuration
    Release (default) or Debug.

.EXAMPLE
    .\scripts\build.ps1
.EXAMPLE
    .\scripts\build.ps1 -EmbySystemDir 'E:\Emby-Server\system'
#>
[CmdletBinding()]
param(
    [string]$EmbySystemDir,
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\Emby.Plugins.Bangumi\Emby.Plugins.Bangumi.csproj'

if (-not (Test-Path -LiteralPath $project)) {
    throw "Project not found: $project"
}

function Test-EmbySystemDir([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $false }
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    return Test-Path -LiteralPath (Join-Path $path 'MediaBrowser.Controller.dll')
}

if (-not (Test-EmbySystemDir $EmbySystemDir)) {
    if ($EmbySystemDir) {
        Write-Warning "MediaBrowser.Controller.dll not found under '$EmbySystemDir'; ignoring it."
    }
    $EmbySystemDir = $null

    $candidates = @(
        $env:EMBY_SYSTEM_DIR,
        'E:\Emby-Server\system',
        'D:\Emby-Server\system',
        'C:\Emby-Server\system',
        "$env:APPDATA\Emby-Server\system",
        '/opt/emby-server/system',
        '/usr/lib/emby-server/system'
    )
    foreach ($candidate in $candidates) {
        if (Test-EmbySystemDir $candidate) { $EmbySystemDir = $candidate; break }
    }
}

$dotnet = 'dotnet'
if (-not (Get-Command $dotnet -ErrorAction SilentlyContinue)) {
    $fallback = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $fallback) { $dotnet = $fallback }
    else { throw 'dotnet SDK 8.0 not found on PATH.' }
}

$arguments = @('build', $project, '-c', $Configuration)
if ($EmbySystemDir) {
    Write-Host "Reference source : local Emby install -> $EmbySystemDir" -ForegroundColor Cyan
    $arguments += "-p:EmbySystemDir=$EmbySystemDir"
}
else {
    Write-Host 'Reference source : NuGet fallback (MediaBrowser.* 4.9.1.90)' -ForegroundColor Cyan
    Write-Host 'Pass -EmbySystemDir to build against your own server assemblies.' -ForegroundColor DarkGray
}

& $dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

$output = Join-Path $repoRoot "src\Emby.Plugins.Bangumi\bin\$Configuration\Emby.Plugins.Bangumi.dll"
if (-not (Test-Path -LiteralPath $output)) { throw "Build reported success but $output is missing." }

$info = Get-Item -LiteralPath $output
Write-Host ''
Write-Host "Built: $($info.FullName)" -ForegroundColor Green
Write-Host ("Size : {0:N0} bytes   Modified: {1:yyyy-MM-dd HH:mm:ss}" -f $info.Length, $info.LastWriteTime)