param(
    [string]$IndexPath = "E:\Emby-Server\system\dashboard-ui\index.html",
    [switch]$Remove,
    [switch]$NoBackup
)

$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------
# inject-ui.ps1
#
# 往 Emby 的 dashboard-ui\index.html 注入一行 script 标签, 让浏览器加载插件
# 内置的 Bangumi UI (bangumi-ui.js)。CSS 由 js 自己以 link 标签注入, 所以这里
# 只需要一行。
#
#   .\inject-ui.ps1              注入 (幂等, 重复跑不会重复插入)
#   .\inject-ui.ps1 -Remove      移除注入
#
# 注意: Emby 升级会覆盖 index.html, 升级后需要重新跑一次。
# ---------------------------------------------------------------------------

$marker = "bangumi-ui-inject"
$tag = '<script src="/emby/Bangumi/Ui/bangumi-ui.js" data-bangumi-ui-inject="1"></' + 'script>'
$anchor = "</head>"

if (-not (Test-Path -LiteralPath $IndexPath)) {
    throw "index.html not found: $IndexPath"
}

$raw = [System.IO.File]::ReadAllText($IndexPath)
$hasMarker = $raw.Contains($marker)

function Backup-Index {
    param([string]$Path)

    if ($NoBackup) { return $null }

    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $dest = "$Path.bak-$stamp"
    Copy-Item -LiteralPath $Path -Destination $dest
    Write-Host "backup  -> $dest"
    return $dest
}

if ($Remove) {
    if (-not $hasMarker) {
        Write-Host "nothing to remove (marker absent)"
        exit 0
    }

    Backup-Index -Path $IndexPath | Out-Null

    $lines = $raw -split "(?<=\n)"
    $kept = @()
    $dropped = 0
    foreach ($line in $lines) {
        if ($line.Contains($marker)) {
            $dropped++
            continue
        }
        $kept += $line
    }

    [System.IO.File]::WriteAllText($IndexPath, ($kept -join ""), (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "removed $dropped line(s) from $IndexPath"
    exit 0
}

if ($hasMarker) {
    Write-Host "already injected, nothing to do"
    exit 0
}

$hits = ([regex]::Matches($raw, [regex]::Escape($anchor))).Count
if ($hits -ne 1) {
    throw "expected exactly one $anchor in $IndexPath but found $hits"
}

Backup-Index -Path $IndexPath | Out-Null

$replacement = $tag + $anchor
$patched = $raw.Replace($anchor, $replacement)

if ($patched -eq $raw) {
    throw "patch produced no change"
}

[System.IO.File]::WriteAllText($IndexPath, $patched, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "injected -> $IndexPath"
Write-Host "tag      -> $tag"
Write-Host ""
Write-Host "刷新浏览器时请强制刷新 (Ctrl+F5) 以避开 index.html 缓存。"
