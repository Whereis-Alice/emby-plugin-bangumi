param(
    [string]$IndexPath,
    [switch]$Remove,
    [switch]$NoBackup
)

$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------
# inject-ui.ps1
#
# 手动版的前端注入。插件本身已经会在每次服务器启动时做同样的事
# (src/Emby.Plugins.Bangumi/Web/BangumiUiInjector.cs, 对应选项「自动注入前端脚本」),
# 所以这个脚本只在两种情况下用得上:
#
#   * 想立刻注入 / 撤掉, 不想为此重启服务器;
#   * 关掉了自动注入, 打算自己管理 index.html。
#
#   .\inject-ui.ps1                     自动探测 index.html 并注入 (幂等)
#   .\inject-ui.ps1 -IndexPath <path>   指定 index.html
#   .\inject-ui.ps1 -Remove             撤掉注入
#
# 注入的路径是相对的 (../emby/...): index.html 由 <base>/web/ 提供,
# 相对路径在反向代理挂了 base url 的部署下同样成立, 绝对路径只对根路径有效。
# ---------------------------------------------------------------------------

$marker = "data-bangumi-ui-inject"
$tag = '<script src="../emby/Bangumi/Ui/bangumi-ui.js" data-bangumi-ui-inject="1"></script>'
$anchor = "</head>"

# 只吃掉注入的那一个 script 标签。按行删是不行的: 注入点是 </head> 之前,
# 而主题 (例如 emby-fluent) 往往把自己的 script 标签写在同一行上。
$pattern = '[ \t]*<script\b[^>]*\bdata-bangumi-ui-inject\b[^>]*>\s*</script>[ \t]*(\r?\n)?'

if (-not $IndexPath) {
    $candidates = @()
    if ($env:EMBY_SYSTEM_DIR) { $candidates += (Join-Path $env:EMBY_SYSTEM_DIR "dashboard-ui\index.html") }
    $candidates += @(
        "C:\Program Files\Emby-Server\system\dashboard-ui\index.html",
        "C:\Emby-Server\system\dashboard-ui\index.html",
        "D:\Emby-Server\system\dashboard-ui\index.html",
        "E:\Emby-Server\system\dashboard-ui\index.html",
        "/system/dashboard-ui/index.html",
        "/opt/emby-server/system/dashboard-ui/index.html"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { $IndexPath = $candidate; break }
    }

    if (-not $IndexPath) {
        throw "找不到 dashboard-ui\index.html, 请用 -IndexPath 指定。试过: " + ($candidates -join " | ")
    }

    Write-Host "index    -> $IndexPath (自动探测)"
}

if (-not (Test-Path -LiteralPath $IndexPath)) {
    throw "index.html not found: $IndexPath"
}

$raw = [System.IO.File]::ReadAllText($IndexPath)
$hasMarker = $raw.Contains($marker)

function Backup-Index {
    param([string]$Path)

    if ($NoBackup) { return }

    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $dest = "$Path.bangumi-bak-$stamp"
    Copy-Item -LiteralPath $Path -Destination $dest
    Write-Host "backup   -> $dest"
}

function Write-Index {
    param([string]$Path, [string]$Content)

    [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding($false)))
}

if ($Remove) {
    if (-not $hasMarker) {
        Write-Host "nothing to remove (marker absent)"
        exit 0
    }

    $stripped = [regex]::Replace($raw, $pattern, "")
    if ($stripped.Contains($marker)) {
        throw "index.html 里的注入标签形状不认识, 没有改动: $IndexPath"
    }

    Backup-Index -Path $IndexPath
    Write-Index -Path $IndexPath -Content $stripped
    Write-Host "removed  -> $IndexPath ($($raw.Length - $stripped.Length) 字节)"
    Write-Host ""
    Write-Host "提醒: 插件下次启动会自动注入回来, 想彻底撤掉请先关掉选项「自动注入前端脚本」。"
    exit 0
}

if ($raw.Contains($tag)) {
    Write-Host "already injected, nothing to do"
    exit 0
}

# 旧版本 (或别的形状) 的注入行先撤掉, 再插新的, 避免出现两行。
$base = if ($hasMarker) { [regex]::Replace($raw, $pattern, "") } else { $raw }

$hits = ([regex]::Matches($base, [regex]::Escape($anchor))).Count
if ($hits -ne 1) {
    throw "expected exactly one $anchor in $IndexPath but found $hits"
}

Backup-Index -Path $IndexPath

$patched = $base.Replace($anchor, ($tag + $anchor))
if ($patched -eq $raw) {
    throw "patch produced no change"
}

Write-Index -Path $IndexPath -Content $patched

Write-Host "injected -> $IndexPath"
Write-Host "tag      -> $tag"
Write-Host ""
Write-Host "刷新浏览器时请强制刷新 (Ctrl+F5) 以避开 index.html 缓存。"
