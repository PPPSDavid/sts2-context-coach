"""HTTP fetch with disk cache and metadata."""

from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import requests

from config import FetchConfig
from models import utc_now_iso


@dataclass
class CachedContent:
    url: str
    text: str
    fetched_at: str
    from_cache: bool
    status: str = "ok"
    error: str | None = None


def _url_slug(url: str) -> str:
    h = hashlib.sha256(url.encode("utf-8")).hexdigest()[:16]
    safe = "".join(c if c.isalnum() else "_" for c in url[:80])
    return f"{safe}_{h}"


class CachedFetcher:
    def __init__(self, cache_dir: Path, fetch: FetchConfig) -> None:
        self.cache_dir = cache_dir
        self.fetch = fetch

    def _meta_path(self, slug: str) -> Path:
        return self.cache_dir / f"{slug}.meta.json"

    def _body_path(self, slug: str) -> Path:
        return self.cache_dir / f"{slug}.html"

    def get_text(self, url: str, force: bool = False) -> CachedContent:
        self.cache_dir.mkdir(parents=True, exist_ok=True)
        slug = _url_slug(url)
        body_path = self._body_path(slug)
        meta_path = self._meta_path(slug)
        now = utc_now_iso()

        if not force and body_path.exists() and meta_path.exists():
            try:
                meta = json.loads(meta_path.read_text(encoding="utf-8"))
                fetched_at = meta.get("fetched_at", "")
                age = _age_seconds(fetched_at, now)
                if age is not None and age < self.fetch.cache_ttl_seconds:
                    return CachedContent(
                        url=url,
                        text=body_path.read_text(encoding="utf-8", errors="replace"),
                        fetched_at=fetched_at,
                        from_cache=True,
                    )
            except (json.JSONDecodeError, OSError):
                pass

        headers = {"User-Agent": self.fetch.user_agent}
        try:
            r = requests.get(
                url,
                headers=headers,
                timeout=self.fetch.request_timeout_seconds,
            )
            r.raise_for_status()
            text = r.text
            fetched_at = now
            body_path.write_text(text, encoding="utf-8", errors="replace")
            meta_path.write_text(
                json.dumps({"url": url, "fetched_at": fetched_at, "status_code": r.status_code}),
                encoding="utf-8",
            )
            return CachedContent(url=url, text=text, fetched_at=fetched_at, from_cache=False)
        except requests.RequestException as e:
            if body_path.exists():
                meta = {}
                if meta_path.exists():
                    try:
                        meta = json.loads(meta_path.read_text(encoding="utf-8"))
                    except json.JSONDecodeError:
                        pass
                return CachedContent(
                    url=url,
                    text=body_path.read_text(encoding="utf-8", errors="replace"),
                    fetched_at=meta.get("fetched_at", now),
                    from_cache=True,
                    status="stale_error",
                    error=str(e),
                )
            return CachedContent(
                url=url,
                text="",
                fetched_at=now,
                from_cache=False,
                status="error",
                error=str(e),
            )


def _age_seconds(fetched_at: str, now_iso: str) -> float | None:
    try:
        from datetime import datetime

        a = datetime.fromisoformat(fetched_at.replace("Z", "+00:00"))
        b = datetime.fromisoformat(now_iso.replace("Z", "+00:00"))
        return abs((b - a).total_seconds())
    except (ValueError, TypeError):
        return None
