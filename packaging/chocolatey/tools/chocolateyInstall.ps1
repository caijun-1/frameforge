$ErrorActionPreference = 'Stop'

# Portable single-file app: download the exe into the tools folder only.
# No registry entries and no shortcuts. Chocolatey auto-generates a
# "FrameForge" shim for the exe that lives in tools.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
  packageName   = $env:ChocolateyPackageName
  fileFullPath  = Join-Path $toolsDir 'FrameForge.exe'
  url           = 'https://github.com/caijun-1/frameforge/releases/download/v1.3.5/FrameForge.exe'
  checksum      = '08ECF19FDC7B4709F3109A030A2147BB4C50914937E460CC7953EEC4220D04A9'
  checksumType  = 'sha256'
}

Get-ChocolateyWebFile @packageArgs