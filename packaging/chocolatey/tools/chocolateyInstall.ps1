$ErrorActionPreference = 'Stop'

# Portable single-file app: download the exe into the tools folder only.
# No registry entries and no shortcuts. Chocolatey auto-generates a
# "FrameForge" shim for the exe that lives in tools.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
  packageName   = $env:ChocolateyPackageName
  fileFullPath  = Join-Path $toolsDir 'FrameForge.exe'
  url           = 'https://github.com/caijun-1/frameforge/releases/download/v1.4.3.5/FrameForge.exe'
  checksum      = '361CB3D072F961281999D86AA79EC9F9B5FDFED73DD6ACD34E38BD42CD1CFDFE'
  checksumType  = 'sha256'
}

Get-ChocolateyWebFile @packageArgs