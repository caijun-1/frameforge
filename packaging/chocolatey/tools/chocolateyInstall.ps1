$ErrorActionPreference = 'Stop'

# Portable single-file app: download the exe into the tools folder only.
# No registry entries and no shortcuts. Chocolatey auto-generates a
# "FrameForge" shim for the exe that lives in tools.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
  packageName   = $env:ChocolateyPackageName
  fileFullPath  = Join-Path $toolsDir 'FrameForge.exe'
  url           = 'https://github.com/caijun-1/frameforge/releases/download/v1.4.4/FrameForge.exe'
  checksum      = '33A94A001CD5D3B3455B51B81E8B1F9BEC191E788AA4608DACFF3823549818E9'
  checksumType  = 'sha256'
}

Get-ChocolateyWebFile @packageArgs