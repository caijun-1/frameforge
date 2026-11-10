$ErrorActionPreference = 'Stop'

# Portable single-file app: download the exe into the tools folder only.
# No registry entries and no shortcuts. Chocolatey auto-generates a
# "FrameForge" shim for the exe that lives in tools.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
  packageName   = $env:ChocolateyPackageName
  fileFullPath  = Join-Path $toolsDir 'FrameForge.exe'
  url           = 'https://github.com/caijun-1/frameforge/releases/download/v1.4.3.5/FrameForge.exe'
  checksum      = 'B22D2FBE1BD7511315DFDA6B8E36A0F507E9C80D22FA48AAF3C8D201CD5C1A5E'
  checksumType  = 'sha256'
}

Get-ChocolateyWebFile @packageArgs