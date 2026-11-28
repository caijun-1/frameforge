# 通过 GitHub REST API 创建 Release 并上传附件（令牌同样取自 Windows 凭据管理器，不打印）。
# 用法：
#   powershell -ExecutionPolicy Bypass -File .\tools\github-api-release.ps1 -Tag v1.6.3 `
#       -Name "FrameForge v1.6.3" -Notes docs\release-notes\v1.6.3.md,docs\release-notes\v1.6.3.en.md `
#       -Assets FrameForge.exe,FrameForge.exe.sha256
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Name = "",
    [string]$Repo = "caijun-1/frameforge",
    [string]$Target = "",
    [string[]]$Notes = @(),
    [string[]]$Assets = @(),
    [switch]$Draft
)

$ErrorActionPreference = "Stop"

$cred = "protocol=https`nhost=github.com`n`n" | git credential fill
$tok = ($cred | Where-Object { $_ -like "password=*" }) -replace '^password=', ''
if (-not $tok) { throw "no stored github credential; run: git credential fill and log in once" }
$H = @{ Authorization = "token $tok"; "User-Agent" = "FrameForge-Release"; Accept = "application/vnd.github+json" }

if (-not $Target) {
    $ref = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/git/ref/heads/main" -Headers $H -TimeoutSec 60
    $Target = $ref.object.sha
}
if (-not $Name) { $Name = "FrameForge $Tag" }

# 多份说明（如中文 + 英文）按顺序拼接，中间用分隔线隔开。
$parts = @()
foreach ($n in $Notes) {
    if (Test-Path $n) { $parts += (Get-Content $n -Raw -Encoding UTF8).TrimEnd() }
    else { Write-Warning "notes file not found: $n" }
}
$body = ($parts -join "`n`n---`n`n")

$payload = @{
    tag_name         = $Tag
    target_commitish = $Target
    name             = $Name
    body             = $body
    draft            = [bool]$Draft
    prerelease       = $false
} | ConvertTo-Json -Depth 5

# 必须按 UTF-8 字节提交，否则中文会触发 GitHub 的 JSON 解析错误。
$release = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$Repo/releases" `
    -Headers $H -Body ([Text.Encoding]::UTF8.GetBytes($payload)) `
    -ContentType "application/json; charset=utf-8" -TimeoutSec 180
Write-Host ("release: " + $release.html_url + " id=" + $release.id)

foreach ($file in $Assets) {
    if (-not (Test-Path $file)) { Write-Warning "asset not found: $file"; continue }
    $item = Get-Item $file
    try {
        $asset = Invoke-RestMethod -Method Post `
            -Uri ("https://uploads.github.com/repos/$Repo/releases/" + $release.id + "/assets?name=" + $item.Name) `
            -Headers $H -InFile $item.FullName -ContentType "application/octet-stream" -TimeoutSec 900
        Write-Host ("asset: " + $asset.browser_download_url + " size=" + $asset.size)
    }
    catch {
        Write-Warning ("asset upload failed (" + $item.Name + "): " + $_.Exception.Message)
    }
}
