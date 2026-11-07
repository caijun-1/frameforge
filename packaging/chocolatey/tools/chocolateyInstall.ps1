$ErrorActionPreference = 'Stop'

# Portable single-file app: download the exe into the tools folder only.
# No registry entries and no shortcuts. Chocolatey auto-generates a
# "FrameForge" shim for the exe that lives in tools.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
  packageName   = $env:ChocolateyPackageName
  fileFullPath  = Join-Path $toolsDir 'FrameForge.exe'
  url           = 'https://github.com/caijun-1/frameforge/releases/download/v1.3.2/FrameForge.exe'
  checksum      = '587E7BBCB33DDB5B2EF0752803554E3ACDC4BC18A4E40F385F23029EF0045A51'
  checksumType  = 'sha256'
}

Get-ChocolateyWebFile @packageArgs