# Launches code-review-graph MCP via conda run (stdio preserved for Cursor).
# Used by .cursor/mcp.json so the MCP command is never empty when CONDA_EXE is unset in the GUI process.
$ErrorActionPreference = 'Stop'

function Get-CondaExe {
    if ($env:CONDA_EXE -and (Test-Path -LiteralPath $env:CONDA_EXE)) {
        return $env:CONDA_EXE
    }
    $cmd = Get-Command conda.exe -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source) {
        return $cmd.Source
    }
    if ($env:CONDA_ROOT) {
        $p = Join-Path $env:CONDA_ROOT 'Scripts\conda.exe'
        if (Test-Path -LiteralPath $p) { return $p }
    }
    foreach ($base in @(
            "$env:USERPROFILE\miniconda3",
            "$env:USERPROFILE\miniconda",
            "$env:USERPROFILE\anaconda3",
            "$env:USERPROFILE\mambaforge",
            'D:\miniconda',
            'C:\ProgramData\miniconda3'
        )) {
        if (-not $base) { continue }
        $p = Join-Path $base 'Scripts\conda.exe'
        if (Test-Path -LiteralPath $p) { return $p }
    }
    throw @'
conda.exe not found for code-review-graph MCP.
Fix: install Miniconda/Anaconda, add ...\Scripts to your user PATH, or set user env var CONDA_EXE to ...\Scripts\conda.exe, then restart Cursor.
'@
}

$condaExe = Get-CondaExe
$condaArgs = @(
    'run',
    '-n', 'sts2-context-coach',
    '--no-capture-output',
    'python', '-m', 'code_review_graph',
    'serve'
)

& $condaExe @condaArgs
exit $LASTEXITCODE
