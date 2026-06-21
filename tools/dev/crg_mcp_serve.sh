#!/usr/bin/env bash
# Launches code-review-graph MCP via conda run (stdio preserved for Cursor).
# Pair with .cursor/mcp.json on Linux/macOS: "command": "bash",
# "args": ["${workspaceFolder}/tools/dev/crg_mcp_serve.sh"]
set -euo pipefail

find_conda() {
  if [[ -n "${CONDA_EXE:-}" && -x "${CONDA_EXE}" ]]; then
    printf '%s' "${CONDA_EXE}"
    return 0
  fi
  if command -v conda >/dev/null 2>&1; then
    command -v conda
    return 0
  fi
  if [[ -n "${CONDA_ROOT:-}" && -x "${CONDA_ROOT}/bin/conda" ]]; then
    printf '%s' "${CONDA_ROOT}/bin/conda"
    return 0
  fi
  local home="${HOME:-}"
  local base p
  for base in \
    "${home}/miniconda3" \
    "${home}/miniconda" \
    "${home}/anaconda3" \
    "${home}/mambaforge" \
    "/opt/conda" \
    "/usr/local/miniconda3"; do
    [[ -z "${base}" ]] && continue
    p="${base}/bin/conda"
    if [[ -x "${p}" ]]; then
      printf '%s' "${p}"
      return 0
    fi
  done
  echo "crg_mcp_serve.sh: conda not found. Install Miniconda/Anaconda, add bin/ to PATH, or set CONDA_EXE / CONDA_ROOT." >&2
  return 1
}

conda_exe="$(find_conda)"
exec "${conda_exe}" run -n sts2-context-coach --no-capture-output python -m code_review_graph serve
