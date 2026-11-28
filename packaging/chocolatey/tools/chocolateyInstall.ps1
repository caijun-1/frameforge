$ErrorActionPreference = 'Stop'

# Portable single-file app: download the exe into the tools folder only.
# No registry entries and no shortcuts. Chocolatey auto-generates a
# "FrameForge" shim for the exe that lives in tools.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
  packageName   = $env:ChocolateyPackageName
  fileFullPath  = Join-Path $toolsDir 'FrameForge.exe'
  url           = 'https://github.com/caijun-1/frameforge/releases/download/v1.7.0/FrameForge.exe'
  checksum      = 'E22FA7696E5A0AA19E787BE38C4E1B2EA9891A32EEC6CFE129C286B70B5C89AF'
  checksumType  = 'sha256'
}

Get-ChocolateyWebFile @packageArgs