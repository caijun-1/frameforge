$ErrorActionPreference = 'Stop'

# Portable single-file app: download the exe into the tools folder only.
# No registry entries and no shortcuts. Chocolatey auto-generates a
# "FrameForge" shim for the exe that lives in tools.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
  packageName   = $env:ChocolateyPackageName
  fileFullPath  = Join-Path $toolsDir 'FrameForge.exe'
  url           = 'https://github.com/caijun-1/frameforge/releases/download/v1.4.1.3/FrameForge.exe'
  checksum      = '72D571D2301BD497D876DF961BF4ED47BF48EB43B2A619028E830F7E6D4100CB'
  checksumType  = 'sha256'
}

Get-ChocolateyWebFile @packageArgs