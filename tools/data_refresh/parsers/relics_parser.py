"""Parse wiki HTML into RawRelicRecord list."""

from __future__ import annotations

import re
from typing import Any

from bs4 import BeautifulSoup

from models import RawRelicRecord


def _guess_internal_name(display: str) -> str:
    s = re.sub(r"[^a-zA-Z0-9]+", "", display.strip())
    return s or "Unknown"


def _norm_header(h: str) -> str:
    return re.sub(r"\s+", " ", h.strip().lower())


def _cell_text(cell: Any) -> str:
    return cell.get_text(" ", strip=True)


def parse_relics_from_wiki_html(html: str, source_url: str, fetched_at: str) -> list[RawRelicRecord]:
    soup = BeautifulSoup(html, "html.parser")
    records: list[RawRelicRecord] = []
    seen: set[str] = set()

    box_records = _parse_relic_boxes(soup, source_url, fetched_at, seen)
    if box_records:
        return box_records

    for table in soup.find_all("table", class_=re.compile(r"wikitable", re.I)):
        rows = table.find_all("tr")
        if not rows:
            continue
        header_cells = rows[0].find_all(["th", "td"])
        headers = [_norm_header(_cell_text(c)) for c in header_cells]
        if not headers:
            continue

        col_name = _pick_col(headers, ["name", "relic", "title"])
        if col_name is None:
            col_name = 0

        for row in rows[1:]:
            cells = row.find_all(["td", "th"])
            if not cells:
                continue
            texts = [_cell_text(c) for c in cells]
            if col_name >= len(texts):
                continue
            name = texts[col_name].strip()
            if not name or len(name) < 2:
                continue
            internal = _guess_internal_name(name)
            key = internal.lower()
            if key in seen:
                continue
            seen.add(key)

            records.append(
                RawRelicRecord(
                    name=name,
                    internal_name=internal,
                    raw_description="",
                    source_url=source_url,
                    source_fetched_at=fetched_at,
                )
            )

    if not records:
        content = soup.find(class_="mw-parser-output") or soup
        for a in content.find_all("a", href=True):
            text = a.get_text(strip=True)
            if not text or len(text) < 2:
                continue
            if "/wiki/" not in a["href"]:
                continue
            internal = _guess_internal_name(text)
            key = internal.lower()
            if key in seen:
                continue
            seen.add(key)
            records.append(
                RawRelicRecord(
                    name=text,
                    internal_name=internal,
                    raw_description="",
                    source_url=source_url,
                    source_fetched_at=fetched_at,
                )
            )

    return records


def _parse_relic_boxes(
    soup: BeautifulSoup,
    source_url: str,
    fetched_at: str,
    seen: set[str],
) -> list[RawRelicRecord]:
    out: list[RawRelicRecord] = []
    for box in soup.select("div.relic-box"):
        title_link = box.select_one(".relic-title a") or box.select_one(".img-base a")
        if title_link is None:
            continue
        name = title_link.get_text(" ", strip=True)
        if not name:
            continue
        internal = _internal_from_link_or_name(title_link.get("href"), name)
        key = internal.lower()
        if key in seen:
            continue
        seen.add(key)

        desc_node = box.select_one(".relic-desc")
        desc = desc_node.get_text(" ", strip=True) if desc_node else ""
        desc = re.sub(r"\s+", " ", desc).strip()

        out.append(
            RawRelicRecord(
                name=name,
                internal_name=internal,
                character=_clean(box.get("data-character")),
                raw_description=desc,
                source_url=source_url,
                source_fetched_at=fetched_at,
            )
        )
    return out


def _internal_from_link_or_name(href: str | None, name: str) -> str:
    if href and "/wiki/" in href:
        slug = href.split("/wiki/", 1)[1]
        slug = slug.split(":", 1)[-1]
        slug = slug.split("?", 1)[0]
        slug = slug.replace("_", " ")
        return _guess_internal_name(slug)
    return _guess_internal_name(name)


def _clean(v: Any) -> str | None:
    if v is None:
        return None
    s = str(v).strip()
    return s or None


def _pick_col(headers: list[str], keywords: list[str]) -> int | None:
    for i, h in enumerate(headers):
        for kw in keywords:
            if kw in h:
                return i
    return None
