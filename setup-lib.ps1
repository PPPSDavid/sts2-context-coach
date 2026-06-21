#Requires -Version 5.0
<#
  Copies sts2.dll, GodotSharp.dll, 0Harmony.dll from the game data folder and BaseLib.dll from mods\BaseLib into .\lib\
  Usage:
    .\setup-lib.ps1
    .\setup-lib.ps1 -GamePath "D:\Games\Slay the Spire 2"
#>
param(
  [string] $GamePath = "M:\SteamLibrary\steamapps\common\Slay the Spire 2"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$lib = Join-Path $root "lib"
$data = Join-Path $GamePath "data_sts2_windows_x86_64"
$baseLib = Join-Path $GamePath "mods\BaseLib\BaseLib.dll"

if (-not (Test-Path $data)) {
  Write-Error "Game data folder not found: $data`nSet -GamePath to your Slay the Spire 2 install."
}

if (-not (Test-Path $baseLib)) {
  Write-Warning "BaseLib.dll not found at: $baseLib`nInstall [BaseLib-StS2](https://github.com/Alchyr/BaseLib-StS2) into mods\BaseLib first."
}

New-Item -ItemType Directory -Force -Path $lib | Out-Null

$files = @(
  @{ Src = Join-Path $data "sts2.dll"; Name = "sts2.dll" },
  @{ Src = Join-Path $data "GodotSharp.dll"; Name = "GodotSharp.dll" },
  @{ Src = Join-Path $data "0Harmony.dll"; Name = "0Harmony.dll" },
  @{ Src = $baseLib; Name = "BaseLib.dll" }
)

foreach ($f in $files) {
  if (-not (Test-Path $f.Src)) {
    Write-Error "Missing: $($f.Src)"
  }
  Copy-Item -LiteralPath $f.Src -Destination (Join-Path $lib $f.Name) -Force
  Write-Host "Copied $($f.Name)"
}

Write-Host "Done. You can run: dotnet build -c Release"
