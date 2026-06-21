from __future__ import annotations

import json
import zipfile
from collections.abc import Iterator
from pathlib import Path
from typing import Any


def _read_json_object(path: Path) -> dict[str, Any]:
    if not path.is_file():
        return {}
    try:
        # utf-8-sig: tolerate UTF-8 BOM from Windows editors / PowerShell Set-Content
        raw = path.read_text(encoding="utf-8-sig")
    except OSError:
        return {}
    try:
        data = json.loads(raw)
    except json.JSONDecodeError:
        return {}
    return data if isinstance(data, dict) else {}


def resolve_run_directory_for_sidecars(input_path: Path) -> Path | None:
    """
    If ``input_path`` is a run folder (contains events.jsonl) or a path to
    events.jsonl, return the directory that may hold metadata.json / summary.json.
    """
    p = input_path.expanduser().resolve()
    if p.is_dir():
        if (p / "events.jsonl").is_file():
            return p
        return None
    if p.is_file() and p.name.lower() == "events.jsonl":
        return p.parent
    return None


def build_run_context_from_filesystem(input_path: Path) -> dict[str, Any] | None:
    """
    Load optional telemetry sidecars next to events.jsonl.

    Returns ``None`` when no run directory can be resolved or both JSON files are
    missing / empty.
    """
    run_dir = resolve_run_directory_for_sidecars(input_path)
    if run_dir is None:
        return None
    metadata = _read_json_object(run_dir / "metadata.json")
    summary = _read_json_object(run_dir / "summary.json")
    if not metadata and not summary:
        return None
    rid = str(metadata.get("run_id") or summary.get("run_id") or "").strip()
    return {
        "run_id": rid or None,
        "run_directory": str(run_dir),
        "metadata": metadata,
        "summary": summary,
    }


def collect_run_sidecars_from_zip(path: Path) -> list[dict[str, Any]]:
    """
    For export ZIPs that include ``<runId>/metadata.json`` (and optionally
    ``summary.json``), return one entry per run prefix found on disk.

    Entries are sorted by ``run_id`` for stable output. Omitted when the archive
    has no such files.
    """
    path = path.expanduser().resolve()
    if not path.is_file() or path.suffix.lower() != ".zip":
        return []
    prefixes: set[str] = set()
    try:
        with zipfile.ZipFile(path, "r") as z:
            for name in z.namelist():
                if name.startswith("__MACOSX"):
                    continue
                parts = name.split("/")
                if len(parts) == 2 and parts[1] == "metadata.json" and parts[0]:
                    prefixes.add(parts[0])
            if not prefixes:
                return []
            out: list[dict[str, Any]] = []
            for prefix in sorted(prefixes):
                try:
                    meta_raw = z.read(f"{prefix}/metadata.json").decode("utf-8", errors="replace")
                    meta = json.loads(meta_raw) if meta_raw.strip() else {}
                except (KeyError, json.JSONDecodeError):
                    meta = {}
                if not isinstance(meta, dict):
                    meta = {}
                summ: dict[str, Any] = {}
                try:
                    summ_raw = z.read(f"{prefix}/summary.json").decode("utf-8", errors="replace")
                    summ = json.loads(summ_raw) if summ_raw.strip() else {}
                except (KeyError, json.JSONDecodeError):
                    summ = {}
                if not isinstance(summ, dict):
                    summ = {}
                if not meta and not summ:
                    continue
                rid = str(meta.get("run_id") or summ.get("run_id") or prefix).strip()
                out.append(
                    {
                        "run_id": rid or prefix,
                        "archive_prefix": prefix,
                        "metadata": meta,
                        "summary": summ,
                    }
                )
            return out
    except (OSError, zipfile.BadZipFile):
        return []


def iter_jsonl_lines(path: Path) -> Iterator[str]:
    with path.open(encoding="utf-8-sig") as f:
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
