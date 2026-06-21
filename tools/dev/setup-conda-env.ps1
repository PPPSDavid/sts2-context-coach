# Idempotent maintainer env: create if missing, install CUDA PyTorch + requirements-dev.txt.

# Prefer recreate-conda-env.ps1 after conda breakage (removes env first).

# Run from repo root: powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\dev\setup-conda-env.ps1

$ErrorActionPreference = "Stop"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")

$CondaBase = (& conda info --base).Trim()

$CondaExe = Join-Path $CondaBase "Scripts\conda.exe"

$EnvName = "sts2-context-coach"

$EnvYaml = Join-Path $RepoRoot "environment.yml"

$EnvPython = Join-Path $CondaBase "envs\$EnvName\python.exe"

$ReqDev = Join-Path $RepoRoot "requirements-dev.txt"



Set-Location $RepoRoot



if (-not (Test-Path $EnvPython)) {

    Write-Host "Creating conda env $EnvName from environment.yml..."

    & $CondaExe env create --override-channels -f $EnvYaml

} else {

    Write-Host "Conda env $EnvName already exists; skipping env create."

}



Write-Host "Installing PyTorch (CUDA 12.8 wheels)..."

& $EnvPython -m pip install --upgrade pip

& $EnvPython -m pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu128



Write-Host "Installing requirements-dev.txt..."

& $EnvPython -m pip install -r $ReqDev



Write-Host "--- Verification ---"

& $EnvPython (Join-Path $RepoRoot "tools\dev\verify_crg_mcp_stack.py")

& $EnvPython -m code_review_graph -v

Write-Host "Installing CRG SentenceTransformer cache patch..."
powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoRoot "tools\dev\install_crg_st_cache_patch.ps1")

Write-Host "Done. conda activate $EnvName"


