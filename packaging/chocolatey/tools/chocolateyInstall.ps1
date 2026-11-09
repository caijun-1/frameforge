$ErrorActionPreference = 'Stop'

# Portable single-file app: download the exe into the tools folder only.
# No registry entries and no shortcuts. Chocolatey auto-generates a
# "FrameForge" shim for the exe that lives in tools.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
  packageName   = $env:ChocolateyPackageName
  fileFullPath  = Join-Path $toolsDir 'FrameForge.exe'
  url           = 'https://github.com/caijun-1/frameforge/releases/download/v1.4.1.1/FrameForge.exe'
  checksum      = '37C67ED83DB26884FBEDB62330B6F5CD1EAF4E1C8F33E9BBEE6CFA4DF7B13A3C'
  checksumType  = 'sha256'
}

Get-ChocolateyWebFile @packageArgs