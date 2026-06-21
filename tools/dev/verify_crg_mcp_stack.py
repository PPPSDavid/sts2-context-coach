"""Print interpreter, torch/CUDA, and code-review-graph paths (run with intended MCP Python)."""

from __future__ import annotations

import os
import sys


def main() -> None:
    print("executable:", sys.executable)
    conda_exe = os.environ.get("CONDA_EXE")
    print(
        "CONDA_EXE:",
        conda_exe if conda_exe else "(unset — OK if tools/dev/crg_mcp_serve.ps1 finds conda.exe)",
    )
    try:
        import torch

        print("torch:", torch.__version__)
        print("torch.cuda.is_available:", torch.cuda.is_available())
        if torch.cuda.is_available():
            print("torch.cuda.get_device_name(0):", torch.cuda.get_device_name(0))
    except Exception as e:  # noqa: BLE001
        print("torch import error:", e)
    try:
        import code_review_graph

        print("code_review_graph:", code_review_graph.__file__)
        from importlib.metadata import version

        print("code-review-graph package:", version("code-review-graph"))
    except Exception as e:  # noqa: BLE001
        print("code_review_graph import error:", e)


if __name__ == "__main__":
    main()
