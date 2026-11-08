$ErrorActionPreference = 'Stop'

# Portable single-file app: download the exe into the tools folder only.
# No registry entries and no shortcuts. Chocolatey auto-generates a
# "FrameForge" shim for the exe that lives in tools.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
  packageName   = $env:ChocolateyPackageName
  fileFullPath  = Join-Path $toolsDir 'FrameForge.exe'
  url           = 'https://github.com/caijun-1/frameforge/releases/download/v1.4.1/FrameForge.exe'
  checksum      = '5D5D10D18C46382F7AC4B272A416DE3B38DA3B361D87B4586C94A53E45617663'
  checksumType  = 'sha256'
}

Get-ChocolateyWebFile @packageArgs