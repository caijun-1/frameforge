# FrameForge build script: compile every source file with the .NET Framework csc.
# The shipped csc only understands C# 5, so never add /langversion:latest.

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$out  = Join-Path $root "FrameForge.exe"

if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

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
    ("/out:" + $out)
) + $src

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "build failed with exit code $LASTEXITCODE" }

$f = Get-Item $out
Write-Host ("done: " + $f.FullName)
Write-Host ("size: " + [math]::Round($f.Length / 1KB, 1) + " KB")