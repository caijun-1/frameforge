$ErrorActionPreference = 'Stop'

# Portable single-file app: download the exe into the tools folder only.
# No registry entries and no shortcuts. Chocolatey auto-generates a
# "FrameForge" shim for the exe that lives in tools.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
  packageName   = $env:ChocolateyPackageName
  fileFullPath  = Join-Path $toolsDir 'FrameForge.exe'
  url           = 'https://github.com/caijun-1/frameforge/releases/download/v1.4.3.6/FrameForge.exe'
  checksum      = '6D720E7DD295AF1FECA17E08ACF27B0F012F8C23CC144D9E83E951CFA4360E4B'
  checksumType  = 'sha256'
}

Get-ChocolateyWebFile @packageArgs