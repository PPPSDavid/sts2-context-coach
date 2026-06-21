"""Parse Steam RSS and heuristically extract patch-related entities."""

from __future__ import annotations

import re
import xml.etree.ElementTree as ET
from typing import Any

from models import AffectedEntityRef, RawPatchRecord


def _local_tag(tag: str) -> str:
    if "}" in tag:
        return tag.split("}", 1)[1]
    return tag


def parse_steam_rss(rss_text: str, source_url: str, fetched_at: str) -> list[RawPatchRecord]:
    if not rss_text.strip():
        return []
    records: list[RawPatchRecord] = []
    try:
        root = ET.fromstring(rss_text)
    except ET.ParseError:
        return _fallback_regex_patch(rss_text, source_url, fetched_at)

    channel = None
    for child in root:
        if _local_tag(child.tag) == "channel":
            channel = child
            break
    if channel is None:
        return _fallback_regex_patch(rss_text, source_url, fetched_at)

    for item in channel:
        if _local_tag(item.tag) != "item":
            continue
        title = link = body = ""
        date = None
        for el in item:
            tag = _local_tag(el.tag)
            text = (el.text or "").strip()
            if tag == "title":
                title = text
            elif tag == "link":
                link = text
            elif tag == "pubDate":
                date = text or None
            elif tag == "description":
                body = text
        pid = _derive_patch_id(title, date)
        records.append(
            RawPatchRecord(
                patch_id=pid,
                title=title,
                date=date,
                body_text=_strip_html(body),
                source_url=link or source_url,
                source_fetched_at=fetched_at,
            )
        )
    return records


def _strip_html(s: str) -> str:
    return re.sub(r"<[^>]+>", " ", s)


def _derive_patch_id(title: str, date: str | None) -> str:
    slug = re.sub(r"[^a-zA-Z0-9]+", "-", title.strip().lower()).strip("-")
    if date:
        d = re.sub(r"[^0-9]+", "", date[:16])
        return f"{slug}-{d}"[:120] if slug else d
    return slug or "unknown-patch"


def _fallback_regex_patch(text: str, source_url: str, fetched_at: str) -> list[RawPatchRecord]:
    return [
        RawPatchRecord(
            patch_id="unparsed-feed",
            title="Unparsed RSS",
            body_text=text[:20_000],
            source_url=source_url,
            source_fetched_at=fetched_at,
        )
    ]


def extract_patch_entities_heuristic(
    patch: RawPatchRecord,
    known_card_names: set[str],
    known_relic_names: set[str],
) -> list[AffectedEntityRef]:
    """Match known internal names appearing in patch body (very rough MVP)."""

    text = f"{patch.title}\n{patch.body_text}"
    out: list[AffectedEntityRef] = []
    for name in sorted(known_card_names, key=len, reverse=True):
        if _wordish_match(text, name):
            out.append(
                AffectedEntityRef(
                    type="card",
                    internal_name=name,
                    change_type="balance_change",
                    summary="Mentioned in patch notes (heuristic)",
                )
            )
    for name in sorted(known_relic_names, key=len, reverse=True):
        if _wordish_match(text, name):
            out.append(
                AffectedEntityRef(
                    type="relic",
                    internal_name=name,
                    change_type="balance_change",
                    summary="Mentioned in patch notes (heuristic)",
                )
            )
    return out


def _wordish_match(haystack: str, internal: str) -> bool:
    """Match internal_name or spaced display form with loose word boundaries."""

    if not internal:
        return False
    low = haystack.lower()
    if len(internal) >= 4 and re.search(rf"\b{re.escape(internal.lower())}\b", low):
        return True
    spaced = re.sub(r"(?<!^)(?=[A-Z])", " ", internal).strip()
    if len(spaced) >= 4 and re.search(rf"\b{re.escape(spaced.lower())}\b", low):
        return True
    return False
