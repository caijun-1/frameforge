$ErrorActionPreference = 'Stop'

# Portable single-file app: download the exe into the tools folder only.
# No registry entries and no shortcuts. Chocolatey auto-generates a
# "FrameForge" shim for the exe that lives in tools.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
  packageName   = $env:ChocolateyPackageName
  fileFullPath  = Join-Path $toolsDir 'FrameForge.exe'
  url           = 'https://github.com/caijun-1/frameforge/releases/download/v1.4.3.1/FrameForge.exe'
  checksum      = '77926367F2339AE97461F18FD652A760BA47B0276F74D0E59A016D268E411680'
  checksumType  = 'sha256'
}

Get-ChocolateyWebFile @packageArgs