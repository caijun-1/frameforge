$ErrorActionPreference = 'Stop'

# Portable single-file app: download the exe into the tools folder only.
# No registry entries and no shortcuts. Chocolatey auto-generates a
# "FrameForge" shim for the exe that lives in tools.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
  packageName   = $env:ChocolateyPackageName
  fileFullPath  = Join-Path $toolsDir 'FrameForge.exe'
  url           = 'https://github.com/caijun-1/frameforge/releases/download/v1.4.1.2/FrameForge.exe'
  checksum      = '6F7AFFD5DFDA442417F8A884F4091C803C0B32EFE79633F9931A76209CDA3B91'
  checksumType  = 'sha256'
}

Get-ChocolateyWebFile @packageArgs