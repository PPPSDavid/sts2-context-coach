# Recreate sts2-context-coach from scratch (conda repair / TOS / broken env).

# Conda root from `conda info --base`.

# Run: powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\dev\recreate-conda-env.ps1

$ErrorActionPreference = "Stop"



$CondaBase = (& conda info --base).Trim()

$CondaExe = Join-Path $CondaBase "Scripts\conda.exe"

if (-not (Test-Path $CondaExe)) {

    Write-Error "Conda not found at $CondaExe"

}



$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")

$EnvName = "sts2-context-coach"

$EnvYaml = Join-Path $RepoRoot "environment.yml"

$EnvPython = Join-Path $CondaBase "envs\$EnvName\python.exe"

$ReqDev = Join-Path $RepoRoot "requirements-dev.txt"



Set-Location $RepoRoot



Write-Host "Removing existing env $EnvName (if any)..."

$prevEap = $ErrorActionPreference

$ErrorActionPreference = "Continue"

& $CondaExe env remove -n $EnvName -y 2>&1 | Out-Null

$ErrorActionPreference = $prevEap



Write-Host "Creating env from environment.yml (--override-channels)..."

& $CondaExe env create --override-channels -f $EnvYaml



Write-Host "Installing PyTorch CUDA 12.8 wheels..."

& $EnvPython -m pip install --upgrade pip

& $EnvPython -m pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu128



Write-Host "Installing requirements-dev.txt..."

& $EnvPython -m pip install -r $ReqDev



Write-Host "--- Verification ---"

& $EnvPython (Join-Path $RepoRoot "tools\dev\verify_crg_mcp_stack.py")

& $EnvPython -m code_review_graph -v



Write-Host "Installing CRG SentenceTransformer cache patch (.pth + site module)..."
powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoRoot "tools\dev\install_crg_st_cache_patch.ps1")

Write-Host 'Done. Restart Cursor MCP / code-review-graph if it was running.'


