param([string]$EmbySystemDir = $env:EMBY_SYSTEM_DIR)
$ErrorActionPreference = 'Stop'
if (-not $EmbySystemDir) { $EmbySystemDir = 'E:\Emby-Server\system' }
$repo = Split-Path $PSScriptRoot
$dll = Join-Path $repo 'src\Emby.Plugins.Bangumi\bin\Release\Emby.Plugins.Bangumi.dll'
# Load from the server for ABI parity. These assemblies never initialize an Emby server here.
foreach ($name in @('MediaBrowser.Common','MediaBrowser.Model','Emby.Web.GenericEdit','Emby.Media.Model','MediaBrowser.Controller')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $EmbySystemDir "$name.dll")) | Out-Null
}
[Reflection.Assembly]::LoadFrom($dll) | Out-Null
$method = [Emby.Plugins.Bangumi.Providers.BangumiEpisodeProvider].GetMethod('Match', [Reflection.BindingFlags]'Static,NonPublic')
$script:passed = 0
function Episode([int]$id, [Nullable[double]]$ep, [double]$sort, [string]$date='2000-01-01') {
    $e = [Emby.Plugins.Bangumi.Api.BangumiEpisode]::new()
    $e.Id=$id; $e.Ep=$ep; $e.Sort=$sort; $e.Airdate=$date
    return $e
}
function Check($name, $episodes, $number, $expected, $mode='Auto', $preceding=0, $strict=$true, $pinned=0) {
    $list = [Collections.Generic.List[Emby.Plugins.Bangumi.Api.BangumiEpisode]]::new()
    foreach ($e in $episodes) { $list.Add($e) }
    $info = [MediaBrowser.Controller.Providers.EpisodeInfo]::new()
    $info.IndexNumber=$number
    if ($pinned) { $info.ProviderIds['BangumiEpisode'] = [string]$pinned }
    $options = [Emby.Plugins.Bangumi.PluginOptions]::new()
    $options.EpisodeNumberMode=[Emby.Plugins.Bangumi.EpisodeNumberMode]::$mode
    $args = [object[]]@($list, $info, $options, [int]$preceding, '', [bool]$strict)
    $result = $method.Invoke($null, $args)
    $actual = if ($null -eq $result) { 0 } else { $result.Id }
    if ($actual -ne $expected) { throw "$name expected=$expected actual=$actual matchedBy=$($args[4])" }
    Write-Output "PASS $name ($($args[4]))"
    $script:passed++
}
Check 'exact ep/sort same target' @((Episode 101 1 1)) 1 101
Check 'absolute sort target' @((Episode 102 1 129)) 129 102
Check 'auto numbering ambiguity rejected' @((Episode 103 1 13),(Episode 104 13 25)) 13 0
Check 'explicit ep numbering honored' @((Episode 103 1 13),(Episode 104 13 25)) 13 104 EpisodeNumber
Check 'explicit sort numbering honored' @((Episode 103 1 13),(Episode 104 13 25)) 13 103 SortNumber
Check 'metadata legacy priority unchanged' @((Episode 103 1 13),(Episode 104 13 25)) 13 104 Auto 0 $false
Check 'unaired tie discarded' @((Episode 103 1 13),(Episode 104 13 25 '2999-01-01')) 13 103
Check 'only unaired match rejected' @((Episode 104 13 25 '2999-01-01')) 13 0
Check 'duplicate ep IDs rejected' @((Episode 105 1 1),(Episode 106 1 2)) 1 0
Check 'split cour offset' @((Episode 107 1 39)) 14 107 Auto 13
Check 'no ordinal guess for watch history' @((Episode 108 $null 0)) 1 0
Check 'legacy ordinal still supported' @((Episode 108 $null 0)) 1 108 Auto 0 $false
Check 'missing index rejected' @((Episode 101 1 1)) $null 0
Check 'existing valid pinned ID honored' @((Episode 103 1 13),(Episode 104 13 25)) 13 103 Auto 0 $true 103
Check 'unresolved shifted ambiguity rejected' @((Episode 109 1 13),(Episode 110 13 25)) 26 0 Auto 13
Write-Output "$script:passed matching tests passed"
