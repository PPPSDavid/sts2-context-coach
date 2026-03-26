"""JSON helpers, atomic writes, backup paths."""

from __future__ import annotations

import json
import shutil
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def read_json(path: Path) -> Any:
    if not path.exists():
        return None
    return json.loads(path.read_text(encoding="utf-8"))


def write_json(path: Path, data: Any, indent: int = 2) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(
        json.dumps(data, ensure_ascii=False, indent=indent) + "\n",
        encoding="utf-8",
    )
    tmp.replace(path)


def ensure_dirs(*paths: Path) -> None:
    for p in paths:
        p.mkdir(parents=True, exist_ok=True)


def backup_timestamp() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")


def create_backup(
    cards_src: Path,
    relics_src: Path,
    backups_root: Path,
) -> Path:
    """Copy production JSON into backups/<ts>/. Returns backup directory."""

    ts = backup_timestamp()
    dest = backups_root / ts
    dest.mkdir(parents=True, exist_ok=True)
    if cards_src.exists():
        shutil.copy2(cards_src, dest / "cards.json")
    if relics_src.exists():
        shutil.copy2(relics_src, dest / "relics.json")
    write_json(dest / "manifest.json", {"created_at": utc_iso(), "files": ["cards.json", "relics.json"]})
    return dest


def utc_iso() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def list_backups(backups_root: Path) -> list[tuple[str, Path]]:
    if not backups_root.exists():
        return []
    out: list[tuple[str, Path]] = []
    for child in sorted(backups_root.iterdir(), reverse=True):
        if child.is_dir() and (child / "manifest.json").exists():
            out.append((child.name, child))
    return out
