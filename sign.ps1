# 本地代码签名（Certum / SimplySign 或其他已安装的代码签名证书）
#   .\sign.ps1                          签根目录的 FrameForge.exe
#   .\sign.ps1 -Path .\dist\x.exe       签指定文件
#   .\sign.ps1 -Thumbprint ABCD1234     指定证书
# 时间戳必带：否则证书过期后旧签名会失效。

param(
    [string]$Path = (Join-Path $PSScriptRoot "FrameForge.exe"),
    [string]$Thumbprint,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Path)) { throw "找不到待签名文件: $Path" }

# 1) 定位 signtool.exe
$signtool = $null
$kits = "C:\Program Files (x86)\Windows Kits\10\bin"
if (Test-Path $kits) {
    $signtool = Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 |
        ForEach-Object { $_.FullName }
}
if (-not $signtool) {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { $signtool = $cmd.Source }
}
if (-not $signtool) { throw "找不到 signtool.exe，请安装 Windows SDK" }

# 2) 选择代码签名证书（EKU 1.3.6.1.5.5.7.3.3 = Code Signing）
# 直接读 2.5.29.37 扩展判断，兼容不同 .NET 版本下 EnhancedKeyUsageList 的差异
function Test-CodeSigningCert($c) {
    if (-not $c.HasPrivateKey) { return $false }
    $ext = $c.Extensions | Where-Object { $_.Oid.Value -eq "2.5.29.37" } | Select-Object -First 1
    if (-not $ext) { return $true }
    return ($ext.Format($true) -match "1\.3\.6\.1\.5\.5\.7\.3\.3")
}

$certs = Get-ChildItem Cert:\CurrentUser\My | Where-Object { Test-CodeSigningCert $_ }

if ($Thumbprint) {
    $cert = $certs | Where-Object { $_.Thumbprint -eq $Thumbprint } | Select-Object -First 1
} else {
    $cert = $certs | Sort-Object NotAfter -Descending | Select-Object -First 1
}
if (-not $cert) {
    throw "当前用户证书存储里没有可用的代码签名证书。Certum 需先安装 SimplySign 并登录。"
}

Write-Host ("证书: " + $cert.Subject)
Write-Host ("指纹: " + $cert.Thumbprint)
Write-Host ("有效期至: " + $cert.NotAfter.ToString("yyyy-MM-dd"))

# 3) 签名
& $signtool sign /fd SHA256 /td SHA256 /tr $TimestampUrl /sha1 $cert.Thumbprint $Path
if ($LASTEXITCODE -ne 0) { throw "signtool sign 失败，退出码 $LASTEXITCODE" }

# 4) 校验
& $signtool verify /pa /v $Path
if ($LASTEXITCODE -ne 0) { throw "签名校验失败，退出码 $LASTEXITCODE" }

Write-Host ("签名完成: " + (Resolve-Path $Path).Path)