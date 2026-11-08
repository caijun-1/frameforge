$ErrorActionPreference = 'Stop'

# Portable single-file app: download the exe into the tools folder only.
# No registry entries and no shortcuts. Chocolatey auto-generates a
# "FrameForge" shim for the exe that lives in tools.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
  packageName   = $env:ChocolateyPackageName
  fileFullPath  = Join-Path $toolsDir 'FrameForge.exe'
  url           = 'https://github.com/caijun-1/frameforge/releases/download/v1.3.9/FrameForge.exe'
  checksum      = '5581218A9439005AEF6562648FF65BB081EB7009B6CAE4E3711CCB0FDCDCC807'
  checksumType  = 'sha256'
}

Get-ChocolateyWebFile @packageArgs