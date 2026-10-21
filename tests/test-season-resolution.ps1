param([string]$EmbySystemDir = 'E:\Emby-Server\system')
$ErrorActionPreference = 'Stop'
foreach ($name in @('MediaBrowser.Common','MediaBrowser.Model','Emby.Web.GenericEdit','Emby.Media.Model','MediaBrowser.Controller')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $EmbySystemDir "$name.dll")) | Out-Null
}
$dll = Join-Path (Split-Path $PSScriptRoot) 'src\Emby.Plugins.Bangumi\bin\Release\Emby.Plugins.Bangumi.dll'
$assembly = [Reflection.Assembly]::LoadFrom($dll)
$resolver = $assembly.GetType('Emby.Plugins.Bangumi.Providers.BangumiSeasonResolver')
$entryType = $assembly.GetType('Emby.Plugins.Bangumi.Providers.SubjectChainEntry')
$flags = [Reflection.BindingFlags]'Static,Public,NonPublic'
$represents = $resolver.GetMethod('RepresentsIdentifiedSeason', $flags)
$search = $resolver.GetMethod('IsStrongSeasonSearchMatch', $flags)
$resolve = $resolver.GetMethod('ResolveFromChain', $flags)
$marker = $resolver.GetMethod('SeasonMarkerOf', $flags)
$script:passed = 0
function Check($name, $actual, $expected) {
    if ($actual -cne $expected) { throw "$name expected=$expected actual=$actual" }
    $script:passed++
    Write-Output "PASS $name"
}
function Entry($id, $name, $cn='') {
    $entry = [Activator]::CreateInstance($entryType)
    $entry.Id=$id; $entry.Name=$name; $entry.NameCn=$cn; $entry.Type=2
    $entry.SeasonMarker=$marker.Invoke($null, [object[]]@($name,$cn))
    return $entry
}
function Subject($id, $name, $cn='', $type=2) {
    $s=[Emby.Plugins.Bangumi.Api.BangumiSubject]::new()
    $s.Id=$id; $s.Name=$name; $s.NameCn=$cn; $s.Type=$type
    return $s
}
function SearchCheck($name, $root, $hit, $n, $expected) {
    Check $name ($search.Invoke($null, [object[]]@($root,$hit,[int]$n))) $expected
}
function ResolveCheck($name, $entries, $n, $expected) {
    $listType=[Collections.Generic.List``1].MakeGenericType($entryType)
    $list=[Activator]::CreateInstance($listType)
    foreach($entry in $entries) { $list.Add($entry) }
    $result=$resolve.Invoke($null,[object[]]@($list,[int]$n))
    $ids=if($null -eq $result) { '' } else { $result.SubjectIds -join ',' }
    Check $name $ids $expected
}

$root=Entry 309311 'Cyberpunk: Edgerunners' '赛博朋克：边缘行者'
SearchCheck 'season number does not make Transformers part of Cyberpunk' $root (Subject 339619 'Transformers: War for Cybertron Season 2' '变形金刚：赛博坦之战 第二季') 2 $false
SearchCheck 'missing relations permit matching sequel title and numeric suffix' $root (Subject 513878 'Cyberpunk: Edgerunners 2' '赛博朋克：边缘行者 2') 2 $true
SearchCheck 'Chinese title can identify a matching sequel independently' (Entry 1 '' '测试作品') (Subject 2 'Unrelated Latin Spelling Season 2' '测试作品 第二季') 2 $true
SearchCheck 'original title can identify a matching sequel independently' (Entry 1 'Test Work' '译名甲') (Subject 2 'Test Work Season 2' '译名乙 第二季') 2 $true
SearchCheck 'punctuation does not change title identity' (Entry 1 'Test: Work' '') (Subject 2 'Test Work Season 2') 2 $true
SearchCheck 'two-character Chinese titles remain valid franchise identities' (Entry 1 '' '仙逆') (Subject 2 '' '仙逆 第二季') 2 $true
SearchCheck 'same season but merely similar title is rejected' (Entry 1 '' '赛博朋克：边缘行者') (Subject 2 'Cyberpunk Season 2' '赛博朋克：边境行者 第二季') 2 $false
SearchCheck 'wrong season cannot pass a matching base title' $root (Subject 3 'Cyberpunk: Edgerunners 3') 2 $false
SearchCheck 'root result is not a sequel search match' $root (Subject 309311 'Cyberpunk: Edgerunners Season 2') 2 $false
SearchCheck 'wrong media type is rejected' $root (Subject 4 'Cyberpunk: Edgerunners 2' '' 1) 2 $false
SearchCheck 'blank root cannot match by season number alone' (Entry 1 '' '') (Subject 2 'Season 2') 2 $false
SearchCheck 'title numbers are not erased while comparing season 2' (Entry 1 'Persona 5') (Subject 2 'Persona Season 2') 2 $false
SearchCheck 'title numbers survive a real sequel marker' (Entry 1 'Persona 5') (Subject 2 'Persona 5 Season 2') 2 $true
SearchCheck 'one generic shared word is insufficient' (Entry 1 'X') (Subject 2 'X Season 2') 2 $false

Check 'identified Arabic suffix matches local S02' ($represents.Invoke($null,[object[]]@('Cyberpunk: Edgerunners 2','赛博朋克：边缘行者 2',2))) $true
Check 'identified suffix does not match a different season' ($represents.Invoke($null,[object[]]@('Cyberpunk: Edgerunners 2','赛博朋克：边缘行者 2',3))) $false
Check 'explicit season wins over other trailing numbers' ($represents.Invoke($null,[object[]]@('Work Season 3 Part 2','',2))) $false
Check 'Roman season remains supported' ($represents.Invoke($null,[object[]]@('Clevatess II','',2))) $true
Check 'unmarked root is not explicitly season 2' ($represents.Invoke($null,[object[]]@('Cyberpunk: Edgerunners','',2))) $false
Check 'general parsing still preserves Persona 5' ($marker.Invoke($null,[object[]]@('Persona 5',''))) $null
Check 'general parsing still preserves Steins Gate 0' ($marker.Invoke($null,[object[]]@('Steins;Gate 0',''))) $null

ResolveCheck 'standalone identified season 2 needs no relation graph' @((Entry 513878 'Cyberpunk: Edgerunners 2' '赛博朋克：边缘行者 2')) 2 '513878'
ResolveCheck 'standalone identified season 2 is not season 3' @((Entry 513878 'Cyberpunk: Edgerunners 2')) 3 ''
ResolveCheck 'first season alone does not resolve an unrelated second season' @($root) 2 ''
ResolveCheck 'split cour subjects remain grouped' @((Entry 140001 'Re:Zero'),(Entry 278826 'Re:Zero 第二季'),(Entry 316247 'Re:Zero 第二季 後半'),(Entry 425998 'Re:Zero 第三季')) 2 '278826,316247'
ResolveCheck 'next season does not select the second cour' @((Entry 140001 'Re:Zero'),(Entry 278826 'Re:Zero 第二季'),(Entry 316247 'Re:Zero 第二季 後半'),(Entry 425998 'Re:Zero 第三季')) 3 '425998'

$match=[Emby.Plugins.Bangumi.Providers.BangumiEpisodeProvider].GetMethod('Match',$flags)
foreach($strict in @($false,$true)) {
    $episodes=[Collections.Generic.List[Emby.Plugins.Bangumi.Api.BangumiEpisode]]::new()
    $ep=[Emby.Plugins.Bangumi.Api.BangumiEpisode]::new()
    $ep.Id=1532450; $ep.Ep=1; $ep.Sort=1; $ep.Airdate='2026-10-20'
    $episodes.Add($ep)
    $info=[MediaBrowser.Controller.Providers.EpisodeInfo]::new()
    $info.IndexNumber=1; $info.ParentIndexNumber=2
    $info.ProviderIds['BangumiEpisode']='960324'
    $options=[Emby.Plugins.Bangumi.PluginOptions]::new()
    $args=[object[]]@($episodes,$info,$options,0,'',$strict)
    $hit=$match.Invoke($null,$args)
    Check "old first-season episode pin cannot override new season candidates (strict=$strict)" $hit.Id 1532450
}
Write-Output "$script:passed season resolution tests passed"
