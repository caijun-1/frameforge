$ErrorActionPreference = 'Stop'

# Portable single-file app: download the exe into the tools folder only.
# No registry entries and no shortcuts. Chocolatey auto-generates a
# "FrameForge" shim for the exe that lives in tools.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
  packageName   = $env:ChocolateyPackageName
  fileFullPath  = Join-Path $toolsDir 'FrameForge.exe'
  url           = 'https://github.com/caijun-1/frameforge/releases/download/v1.4.4/FrameForge.exe'
  checksum      = '6D8555A4835351995AC8D0C9105406D26A8EA89FA9377FB3942DBF7B359B107E'
  checksumType  = 'sha256'
}

Get-ChocolateyWebFile @packageArgs