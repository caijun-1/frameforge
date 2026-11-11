$ErrorActionPreference = 'Stop'

# Portable single-file app: download the exe into the tools folder only.
# No registry entries and no shortcuts. Chocolatey auto-generates a
# "FrameForge" shim for the exe that lives in tools.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
  packageName   = $env:ChocolateyPackageName
  fileFullPath  = Join-Path $toolsDir 'FrameForge.exe'
  url           = 'https://github.com/caijun-1/frameforge/releases/download/v1.4.4.2/FrameForge.exe'
  checksum      = '4DD90D4E31969FC1ACB86002E99772B5C3959BEBCFBFD81335F90ADAD8A0C5D4'
  checksumType  = 'sha256'
}

Get-ChocolateyWebFile @packageArgs