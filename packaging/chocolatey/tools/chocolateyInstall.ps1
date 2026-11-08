$ErrorActionPreference = 'Stop'

# Portable single-file app: download the exe into the tools folder only.
# No registry entries and no shortcuts. Chocolatey auto-generates a
# "FrameForge" shim for the exe that lives in tools.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
  packageName   = $env:ChocolateyPackageName
  fileFullPath  = Join-Path $toolsDir 'FrameForge.exe'
  url           = 'https://github.com/caijun-1/frameforge/releases/download/v1.3.9/FrameForge.exe'
  checksum      = '4B0E8466FAB43F66D81544FA2C3B6422A0F7572DB7A5D631EDA89F71034CE570'
  checksumType  = 'sha256'
}

Get-ChocolateyWebFile @packageArgs