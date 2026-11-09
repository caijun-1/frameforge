$ErrorActionPreference = 'Stop'

# Portable single-file app: download the exe into the tools folder only.
# No registry entries and no shortcuts. Chocolatey auto-generates a
# "FrameForge" shim for the exe that lives in tools.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
  packageName   = $env:ChocolateyPackageName
  fileFullPath  = Join-Path $toolsDir 'FrameForge.exe'
  url           = 'https://github.com/caijun-1/frameforge/releases/download/v1.4.2/FrameForge.exe'
  checksum      = 'BEABC212896868F03F50831501A0D3836E14DD1DAF95071BEE43226D3C4AF53B'
  checksumType  = 'sha256'
}

Get-ChocolateyWebFile @packageArgs