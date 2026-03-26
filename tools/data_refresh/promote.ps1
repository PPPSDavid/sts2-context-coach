param(
    [string]$ConfigPath = "",
    [switch]$SkipReleaseCopy,
    [switch]$WhatIf
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path (Join-Path $ScriptDir "..\..")
$MainPy = Join-Path $ScriptDir "main.py"

if (-not (Test-Path $MainPy)) {
    throw "Cannot find main.py at $MainPy"
}

function Run-PythonTool {
    param(
        [Parameter(Mandatory = $true)][string[]]$Args
    )

    $cmd = @("python", $MainPy) + $Args
    Write-Host ">> $($cmd -join ' ')" -ForegroundColor Cyan
    & python $MainPy @Args
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: python main.py $($Args -join ' ')"
    }
}

$baseArgs = @()
if ($ConfigPath -and $ConfigPath.Trim().Length -gt 0) {
    $resolvedConfig = Resolve-Path $ConfigPath
    $baseArgs += @("--config", $resolvedConfig)
}

if ($WhatIf) {
    Write-Host "WhatIf mode: will validate and preview, but not apply changes." -ForegroundColor Yellow
    $validateArgs = @("validate") + $baseArgs
    $reviewListArgs = @("review", "list") + $baseArgs
    Run-PythonTool -Args $validateArgs
    Run-PythonTool -Args $reviewListArgs
    exit 0
}

Write-Host "Step 1/3: Validate generated artifacts" -ForegroundColor Green
$validateArgs = @("validate") + $baseArgs
Run-PythonTool -Args $validateArgs

Write-Host "Step 2/3: Apply approved items to Data/ (auto backup)" -ForegroundColor Green
$applyArgs = @("apply-approved") + $baseArgs
Run-PythonTool -Args $applyArgs

if (-not $SkipReleaseCopy) {
    Write-Host "Step 3/3: Sync Data/ -> bin/Release/net9.0/Data/" -ForegroundColor Green
    $srcData = Join-Path $RepoRoot "Data"
    $dstData = Join-Path $RepoRoot "bin\Release\net9.0\Data"
    if (-not (Test-Path $srcData)) {
        throw "Source data directory not found: $srcData"
    }
    if (-not (Test-Path $dstData)) {
        Write-Host "Release data directory missing. Creating: $dstData" -ForegroundColor Yellow
        New-Item -ItemType Directory -Path $dstData -Force | Out-Null
    }
    Copy-Item (Join-Path $srcData "cards.json") (Join-Path $dstData "cards.json") -Force
    Copy-Item (Join-Path $srcData "relics.json") (Join-Path $dstData "relics.json") -Force
    Write-Host "Release data sync complete." -ForegroundColor Green
}
else {
    Write-Host "Skipped release copy due to -SkipReleaseCopy." -ForegroundColor Yellow
}

Write-Host "Promotion flow complete." -ForegroundColor Green
