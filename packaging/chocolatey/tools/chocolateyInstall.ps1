$ErrorActionPreference = 'Stop'

# Portable single-file app: download the exe into the tools folder only.
# No registry entries and no shortcuts. Chocolatey auto-generates a
# "FrameForge" shim for the exe that lives in tools.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
  packageName   = $env:ChocolateyPackageName
  fileFullPath  = Join-Path $toolsDir 'FrameForge.exe'
  url           = 'https://github.com/caijun-1/frameforge/releases/download/v1.4.1.1/FrameForge.exe'
  checksum      = '8BAB08FC38592957F7FA8B328868C2D1CB0069D6D1642B2CCA455B0FE6A34900'
  checksumType  = 'sha256'
}

Get-ChocolateyWebFile @packageArgs