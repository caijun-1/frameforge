# 通过 GitHub REST API 推送本地提交（绕过 git:// 与 https 克隆被墙/不可达）
# 令牌来源：Windows 凭据管理器里 git:https://github.com 的凭据，脚本只读取，绝不打印。
# 用法：powershell -ExecutionPolicy Bypass -File .\tools\github-api-push.ps1
param(
    [string]$Repo = "caijun-1/frameforge",
    [string]$Branch = "main",
    [string]$Root = (Get-Location).Path
)

$ErrorActionPreference = "Stop"
Set-Location $Root

$cred = "protocol=https`nhost=github.com`n`n" | git credential fill
$tok = ($cred | Where-Object { $_ -like "password=*" }) -replace '^password=', ''
if (-not $tok) { throw "no stored github credential; run: git credential fill and log in once" }
$H = @{
    Authorization = "token $tok"
    "User-Agent"  = "FrameForge-Release"
    Accept        = "application/vnd.github+json"
}
function Api($method, $uri, $body) {
    if ($null -eq $body) { return Invoke-RestMethod -Method $method -Uri $uri -Headers $H -TimeoutSec 120 }
    return Invoke-RestMethod -Method $method -Uri $uri -Headers $H -Body ($body | ConvertTo-Json -Depth 10 -Compress) -ContentType "application/json" -TimeoutSec 120
}

$localSha = (git rev-parse HEAD).Trim()
$parentSha = (git rev-parse HEAD~1).Trim()
Write-Host "local=$localSha parent=$parentSha"

$ref = Api GET "https://api.github.com/repos/$Repo/git/ref/heads/$Branch" $null
$remoteSha = $ref.object.sha
Write-Host "remote=$remoteSha"
if ($remoteSha -eq $localSha) { Write-Host "already pushed"; return }
if ($remoteSha -ne $parentSha) { throw "remote head is not the parent of the local commit; refusing to rewrite history" }

$parentTree = (Api GET "https://api.github.com/repos/$Repo/git/commits/$parentSha" $null).tree.sha
$files = @(git diff --name-only $parentSha $localSha)
Write-Host ("changed files: " + $files.Count)

# 原样读取 index 里的 blob 字节，避免 CRLF/BOM 被 PowerShell 改写。
function Read-Blob([string]$sha) {
    $p = New-Object System.Diagnostics.Process
    $p.StartInfo.FileName = "git"
    $p.StartInfo.Arguments = "cat-file blob $sha"
    $p.StartInfo.RedirectStandardOutput = $true
    $p.StartInfo.UseShellExecute = $false
    $p.StartInfo.CreateNoWindow = $true
    [void]$p.Start()
    $ms = New-Object System.IO.MemoryStream
    $p.StandardOutput.BaseStream.CopyTo($ms)
    $p.WaitForExit()
    return $ms.ToArray()
}

$entries = @()
foreach ($f in $files) {
    $line = (git ls-tree HEAD -- $f) -split "`t"
    $meta = $line[0] -split " "
    $mode = $meta[0]
    $sha = $meta[2]
    $b64 = [Convert]::ToBase64String((Read-Blob $sha))
    $blob = Api POST "https://api.github.com/repos/$Repo/git/blobs" @{ content = $b64; encoding = "base64" }
    $entries += @{ path = $f; mode = $mode; type = "blob"; sha = $blob.sha }
    Write-Host ("blob " + $f + " " + $blob.sha)
}

$tree = Api POST "https://api.github.com/repos/$Repo/git/trees" @{ base_tree = $parentTree; tree = $entries }
$an = (git log -1 --format=%an).Trim()
$ae = (git log -1 --format=%ae).Trim()
$cn = (git log -1 --format=%cn).Trim()
$ce = (git log -1 --format=%ce).Trim()
$ad = (git log -1 --format=%aI).Trim()
$cd = (git log -1 --format=%cI).Trim()
$msg = ((git log -1 --format=%B) -join "`n").TrimEnd()

$commitBody = @{
    message   = $msg
    tree      = $tree.sha
    parents   = @($parentSha)
    author    = @{ name = $an; email = $ae; date = $ad }
    committer = @{ name = $cn; email = $ce; date = $cd }
}
$newCommit = Api POST "https://api.github.com/repos/$Repo/git/commits" $commitBody
Write-Host ("new commit " + $newCommit.sha + " (local " + $localSha + ")")
if ($newCommit.sha -ne $localSha) { Write-Warning "commit hash differs from local; remote history is still consistent" }

[void](Api PATCH "https://api.github.com/repos/$Repo/git/refs/heads/$Branch" @{ sha = $newCommit.sha; force = $false })
Write-Host "ref updated"
