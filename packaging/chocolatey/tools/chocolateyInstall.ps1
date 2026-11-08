$ErrorActionPreference = 'Stop'

# Portable single-file app: download the exe into the tools folder only.
# No registry entries and no shortcuts. Chocolatey auto-generates a
# "FrameForge" shim for the exe that lives in tools.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
  packageName   = $env:ChocolateyPackageName
  fileFullPath  = Join-Path $toolsDir 'FrameForge.exe'
  url           = 'https://github.com/caijun-1/frameforge/releases/download/v1.4.1/FrameForge.exe'
  checksum      = 'BDCE9DDC813C6F777A8C69D8B2ACBCA7BCE3404D4C28FD595CED2471698C0D8C'
  checksumType  = 'sha256'
}

Get-ChocolateyWebFile @packageArgs