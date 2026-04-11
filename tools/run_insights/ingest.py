from __future__ import annotations

import json
import zipfile
from pathlib import Path
from typing import Any, Iterator


def iter_jsonl_lines(path: Path) -> Iterator[str]:
    with path.open(encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                yield line


def load_events_from_jsonl(path: Path) -> list[dict[str, Any]]:
    out: list[dict[str, Any]] = []
    for line in iter_jsonl_lines(path):
        try:
            out.append(json.loads(line))
        except json.JSONDecodeError:
            continue
    return out


def _find_events_jsonl_in_zip(z: zipfile.ZipFile) -> list[tuple[str, bytes]]:
    """Return (archive_name, raw_bytes) for each events.jsonl found."""
    found: list[tuple[str, bytes]] = []
    for name in z.namelist():
        if name.endswith("events.jsonl") and not name.startswith("__MACOSX"):
            found.append((name, z.read(name)))
    return found


def load_events_from_zip(path: Path) -> list[dict[str, Any]]:
    out: list[dict[str, Any]] = []
    with zipfile.ZipFile(path, "r") as z:
        entries = _find_events_jsonl_in_zip(z)
        if not entries:
            return out
        for _, raw in entries:
            text = raw.decode("utf-8", errors="replace")
            for line in text.splitlines():
                line = line.strip()
                if not line:
                    continue
                try:
                    out.append(json.loads(line))
                except json.JSONDecodeError:
                    continue
    return out


def load_events(path: Path) -> list[dict[str, Any]]:
    path = path.expanduser().resolve()
    if path.is_file() and path.suffix.lower() == ".zip":
        return load_events_from_zip(path)
    if path.is_file() and path.name == "events.jsonl":
        return load_events_from_jsonl(path)
    if path.is_dir():
        ev = path / "events.jsonl"
        if ev.is_file():
            return load_events_from_jsonl(ev)
    raise FileNotFoundError(f"No events.jsonl or export .zip found at {path}")
