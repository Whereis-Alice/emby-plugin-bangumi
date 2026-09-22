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

$compact = [Emby.Plugins.Bangumi.Providers.BangumiEpisodeProvider].GetMethod('GetCompactAbsoluteNumber', [Reflection.BindingFlags]'Static,NonPublic')
$absolute = [Emby.Plugins.Bangumi.Providers.BangumiEpisodeProvider].GetMethod('MatchCompactAbsolute', [Reflection.BindingFlags]'Static,NonPublic')
function CompactCheck($name, $path, $season, $index, $expected, $mode='Auto', $end=$null, $offset=0) {
    $info = [MediaBrowser.Controller.Providers.EpisodeInfo]::new()
    $info.Path=$path; $info.ParentIndexNumber=$season; $info.IndexNumber=$index; $info.IndexNumberEnd=$end
    $info.ProviderIds['BangumiEpisode']='1181536'
    $options = [Emby.Plugins.Bangumi.PluginOptions]::new()
    $options.EpisodeNumberMode=[Emby.Plugins.Bangumi.EpisodeNumberMode]::$mode
    $options.EpisodeIndexOffset=$offset
    $actual = $compact.Invoke($null, [object[]]@($info,$options))
    if ($actual -ne $expected) { throw "$name expected=$expected actual=$actual" }
    Write-Output "PASS $name"
    $script:passed++
}
CompactCheck '242 4K is not S02E42' 'D:\media\吞噬星空 宇宙篇\242 4K.mp4' 2 42 242
CompactCheck 'bare 242 filename' 'D:\media\Show\242.mkv' 2 42 242
CompactCheck 'encode labels accepted' 'D:\media\Show\243.1080p.HEVC.mkv' 2 43 243
CompactCheck 'corrected numbering stable on refresh' 'D:\media\Show\242 4K.mp4' 1 242 242
CompactCheck 'explicit S02E42 preserved' 'D:\media\Show\S02E42 4K.mp4' 2 42 $null
CompactCheck 'explicit 2x42 preserved' 'D:\media\Show\2x42.mp4' 2 42 $null
CompactCheck 'explicit season folder preserved' 'D:\media\Show\Season 2\242 4K.mp4' 2 42 $null
CompactCheck 'explicit S02 folder preserved' 'D:\media\Show\S02\242 4K.mp4' 2 42 $null
CompactCheck 'manual unrelated numbering preserved' 'D:\media\Show\242 4K.mp4' 1 157 $null
CompactCheck 'explicit subject ep mode preserved' 'D:\media\Show\242 4K.mp4' 2 42 $null EpisodeNumber
CompactCheck 'manual offset preserved' 'D:\media\Show\242 4K.mp4' 2 42 $null Auto $null 5
CompactCheck 'merged episode untouched' 'D:\media\Show\242 4K.mp4' 2 42 $null Auto 43
CompactCheck 'year not an episode' 'D:\media\Show\2026.mp4' 20 26 $null
CompactCheck 'resolution-only name not an episode' 'D:\media\Show\720.mp4' 7 20 $null
CompactCheck 'unrecognized title suffix not guessed' 'D:\media\Show\242 Documentary.mp4' 2 42 $null
CompactCheck 'range not partially matched' 'D:\media\Show\242-243.mp4' 2 42 $null
function AbsoluteCheck($name, $episodes, $expected) {
    $list = [Collections.Generic.List[Emby.Plugins.Bangumi.Api.BangumiEpisode]]::new()
    foreach ($e in $episodes) { $list.Add($e) }
    $args = [object[]]@($list, 242, '')
    $hit = $absolute.Invoke($null, $args)
    $actual = if ($hit) { $hit.Id } else { 0 }
    if ($actual -ne $expected) { throw "$name expected=$expected actual=$actual" }
    Write-Output "PASS $name ($($args[2]))"
    $script:passed++
}
AbsoluteCheck '242 maps to subject ep157, never old ep42 or ep242' @((Episode 1181536 42 127),(Episode 1632123 157 242),(Episode 900 242 327)) 1632123
AbsoluteCheck 'missing 242 does not fall back to 42' @((Episode 1181536 42 127)) 0
AbsoluteCheck 'duplicate sort rejected' @((Episode 1632123 157 242),(Episode 901 242 242)) 0
AbsoluteCheck 'future 242 rejected' @((Episode 1632123 157 242 '2999-01-01')) 0
Write-Output "$script:passed matching tests passed"
