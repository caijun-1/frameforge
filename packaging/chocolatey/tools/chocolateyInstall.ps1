$ErrorActionPreference = 'Stop'

# Portable single-file app: download the exe into the tools folder only.
# No registry entries and no shortcuts. Chocolatey auto-generates a
# "FrameForge" shim for the exe that lives in tools.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
  packageName   = $env:ChocolateyPackageName
  fileFullPath  = Join-Path $toolsDir 'FrameForge.exe'
  url           = 'https://github.com/caijun-1/frameforge/releases/download/v1.4.3/FrameForge.exe'
  checksum      = '77E6E5A7CAF255AFA0BBC95D6FD931B26E5EC54D8D33809EA3ECBE6B7A7FE02A'
  checksumType  = 'sha256'
}

Get-ChocolateyWebFile @packageArgs