[CmdletBinding()]
param([switch]$SkipInstaller, [switch]$SkipTests, [switch]$NoSelfContained)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'scripts\release.ps1') @PSBoundParameters
