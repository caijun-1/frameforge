$ErrorActionPreference = 'Stop'

# Portable single-file app: download the exe into the tools folder only.
# No registry entries and no shortcuts. Chocolatey auto-generates a
# "FrameForge" shim for the exe that lives in tools.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
  packageName   = $env:ChocolateyPackageName
  fileFullPath  = Join-Path $toolsDir 'FrameForge.exe'
  url           = 'https://github.com/caijun-1/frameforge/releases/download/v1.4.4.1/FrameForge.exe'
  checksum      = '241795DD140E06292AFF318B966C5534D61AF1D0F8916218C478230FC1AF8C66'
  checksumType  = 'sha256'
}

Get-ChocolateyWebFile @packageArgs