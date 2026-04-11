"""One-shot repair: install pluggy wheel into Miniconda base site-packages.

Run (from any shell that can invoke base Python):
  D:\\miniconda\\python.exe tools/restore_pluggy_miniconda.py
"""
from __future__ import annotations

import shutil
import sys
import tempfile
import urllib.request
import zipfile
from pathlib import Path

MINICONDA = Path(r"D:\miniconda")
SITE = MINICONDA / "Lib" / "site-packages"
WHEEL_URL = (
    "https://files.pythonhosted.org/packages/88/5f/"
    "e351af9a41f866ac3f1fac4ca0613908d9a41741cfcf2228f4ad853b697d/"
    "pluggy-1.5.0-py3-none-any.whl"
)


def main() -> int:
    if not MINICONDA.is_dir():
        print(f"Expected Miniconda at {MINICONDA}; adjust script if your path differs.")
        return 2
    SITE.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory() as td:
        whl = Path(td) / "pluggy-1.5.0-py3-none-any.whl"
        print("Downloading", WHEEL_URL)
        urllib.request.urlretrieve(WHEEL_URL, whl)
        with zipfile.ZipFile(whl, "r") as zf:
            names = zf.namelist()
            pkg_dirs = sorted({n.split("/")[0] for n in names if "/" in n})
            print("Wheel contains:", ", ".join(pkg_dirs))
            for name in names:
                if name.endswith("/"):
                    continue
                dest = SITE / name.replace("/", "\\")
                dest.parent.mkdir(parents=True, exist_ok=True)
                with zf.open(name) as src, open(dest, "wb") as out:
                    shutil.copyfileobj(src, out)
                print("Wrote", dest)

    try:
        import pluggy  # noqa: WPS433

        print("import pluggy OK, version", pluggy.__version__)
    except Exception as e:  # noqa: BLE001
        print("import failed:", e)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
