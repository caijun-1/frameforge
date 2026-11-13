# FrameForge build script: compile every source file with the .NET Framework csc.
# The shipped csc only understands C# 5, so never add /langversion:latest.

$ErrorActionPreference = "Stop"

# $PSScriptRoot 在"按文件执行"时一定有值;通过管道/表达式喂进来时为空,
# 这时退回当前目录,免得 Join-Path 拿到 $null 直接失败。
$root = $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }
$csc  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

$out  = Join-Path $root "FrameForge.exe"

if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

# 版本号只以 src/Version.cs 为准。manifest 与关于页文案要跟它一致,
# 不一致就在这里失败,而不是等打完 tag 才发现界面还写着旧版本。
$verFile = Join-Path $root "src\Version.cs"
$verMatch = [regex]::Match((Get-Content $verFile -Raw -Encoding UTF8),
    'AssemblyInformationalVersion\("([0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)"\)')
if (-not $verMatch.Success) { throw "cannot read AssemblyInformationalVersion from src/Version.cs" }
$ver = $verMatch.Groups[1].Value

# manifest 里的版本号固定四段:三段版本补一个 .0,四段版本原样比。
$asmVer = $ver
if (($ver -split '\.').Count -eq 3) { $asmVer = $ver + ".0" }

$manifest = Get-Content (Join-Path $root "app.manifest") -Raw -Encoding UTF8
if ($manifest -notmatch ('version="' + [regex]::Escape($asmVer) + '"')) {
    throw "app.manifest version mismatch: expected $asmVer"
}

$i18n = Get-Content (Join-Path $root "src\I18n.cs") -Raw -Encoding UTF8
if ($i18n -notmatch ('"FrameForge ' + [regex]::Escape($ver) + '"')) {
    throw "src/I18n.cs set.version mismatch: expected FrameForge $ver"
}

Write-Host ("version: " + $ver)

$src = @(Get-ChildItem -Path (Join-Path $root "src") -Filter *.cs -Recurse |
         Sort-Object FullName | ForEach-Object { $_.FullName })

Write-Host ("compiling " + $src.Count + " files ...")

$cscArgs = @(
    "/nologo",
    "/target:winexe",
    "/platform:anycpu",
    "/optimize+",
    "/debug-",
    "/codepage:65001",
    ("/win32manifest:" + (Join-Path $root "app.manifest")),
    ("/win32icon:" + (Join-Path $root "app.ico")),
    ("/resource:" + (Join-Path $root "app.ico") + ",app.ico"),
    ("/r:System.Management.dll"),
    # RTSS 安装流程要解压 zip:csc 默认不引用这两个程序集。
    ("/r:System.IO.Compression.dll"),
    ("/r:System.IO.Compression.FileSystem.dll"),
    ("/out:" + $out)
) + $src

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "build failed with exit code $LASTEXITCODE" }

$f = Get-Item $out
Write-Host ("done: " + $f.FullName)
Write-Host ("size: " + [math]::Round($f.Length / 1KB, 1) + " KB")