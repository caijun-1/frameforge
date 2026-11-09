$ErrorActionPreference = 'Stop'

# Portable single-file app: download the exe into the tools folder only.
# No registry entries and no shortcuts. Chocolatey auto-generates a
# "FrameForge" shim for the exe that lives in tools.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
  packageName   = $env:ChocolateyPackageName
  fileFullPath  = Join-Path $toolsDir 'FrameForge.exe'
  url           = 'https://github.com/caijun-1/frameforge/releases/download/v1.4.1.3/FrameForge.exe'
  checksum      = '15BDA28BE3E2CDE43B8C79A50301F9CB9BB7EC46062496070D28827F5D26602B'
  checksumType  = 'sha256'
}

Get-ChocolateyWebFile @packageArgs