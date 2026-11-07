$ErrorActionPreference = 'Stop'

# Portable single-file app: download the exe into the tools folder only.
# No registry entries and no shortcuts. Chocolatey auto-generates a
# "FrameForge" shim for the exe that lives in tools.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
  packageName   = $env:ChocolateyPackageName
  fileFullPath  = Join-Path $toolsDir 'FrameForge.exe'
  url           = 'https://github.com/caijun-1/frameforge/releases/download/v1.3.2/FrameForge.exe'
  checksum      = '2032201316E70F2CE3D50128BADE243E94EA55465524D39A52A6469919AA4AA0'
  checksumType  = 'sha256'
}

Get-ChocolateyWebFile @packageArgs