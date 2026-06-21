"""Parse wiki HTML into RawCardRecord list."""

from __future__ import annotations

import re
from typing import Any

from bs4 import BeautifulSoup

from models import RawCardRecord


def _guess_internal_name(display: str) -> str:
    s = re.sub(r"[^a-zA-Z0-9]+", "", display.strip())
    return s or "Unknown"


def _norm_header(h: str) -> str:
    return re.sub(r"\s+", " ", h.strip().lower())


def _cell_text(cell: Any) -> str:
    return cell.get_text(" ", strip=True)


def _parse_int_maybe(s: str) -> int | None:
    m = re.search(r"-?\d+", s)
    if not m:
        return None
    try:
        return int(m.group())
    except ValueError:
        return None


def parse_cards_from_wiki_html(html: str, source_url: str, fetched_at: str) -> list[RawCardRecord]:
    soup = BeautifulSoup(html, "html.parser")
    records: list[RawCardRecord] = []
    seen: set[str] = set()

    # STS2 wiki.gg uses card-box divs on Cards_List; parse this first.
    box_records = _parse_card_boxes(soup, source_url, fetched_at, seen)
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

        col_name = _pick_col(headers, ["name", "card", "title"])
        col_cost = _pick_col(headers, ["cost", "energy", "mana"])
        col_char = _pick_col(headers, ["character", "class", "color"])
        col_type = _pick_col(headers, ["type"])
        col_rarity = _pick_col(headers, ["rarity"])
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

            cost = _parse_int_maybe(texts[col_cost]) if col_cost is not None and col_cost < len(texts) else None
            character = texts[col_char].strip() if col_char is not None and col_char < len(texts) else None
            ctype = texts[col_type].strip() if col_type is not None and col_type < len(texts) else None
            rarity = texts[col_rarity].strip() if col_rarity is not None and col_rarity < len(texts) else None

            desc = ""
            if row.find("td", class_=re.compile(r"description", re.I)):
                desc = _cell_text(row.find("td", class_=re.compile(r"description", re.I)))

            records.append(
                RawCardRecord(
                    name=name,
                    internal_name=internal,
                    character=character,
                    cost=cost,
                    rarity=rarity or None,
                    type=ctype or None,
                    raw_description=desc,
                    source_url=source_url,
                    source_fetched_at=fetched_at,
                )
            )

    # Fallback: mw-parser-output links in lists (many wikis use bullet lists of card links)
    if not records:
        content = soup.find(class_="mw-parser-output") or soup
        for a in content.find_all("a", href=True):
            title = (a.get("title") or "").strip()
            text = a.get_text(strip=True)
            name = text or title
            if not name or len(name) < 2:
                continue
            if "/wiki/" not in a["href"] and "redlink" not in a.get("class", []):
                continue
            internal = _guess_internal_name(name)
            key = internal.lower()
            if key in seen:
                continue
            seen.add(key)
            records.append(
                RawCardRecord(
                    name=name,
                    internal_name=internal,
                    raw_description="",
                    source_url=source_url,
                    source_fetched_at=fetched_at,
                )
            )

    return records


def _parse_card_boxes(
    soup: BeautifulSoup,
    source_url: str,
    fetched_at: str,
    seen: set[str],
) -> list[RawCardRecord]:
    out: list[RawCardRecord] = []
    for box in soup.select("div.card-box"):
        title_link = box.select_one(".card-title a") or box.select_one(".img-base a")
        if title_link is None:
            continue
        name = title_link.get_text(" ", strip=True)
        if not name:
            continue

        internal = _internal_from_link_or_name(title_link.get("href"), name)
        detail_url = _absolute_wiki_url(title_link.get("href"))
        key = internal.lower()
        if key in seen:
            continue
        seen.add(key)

        rarity = _clean(box.get("data-rarity"))
        ctype = _clean(box.get("data-type"))
        character = _clean(box.get("data-color"))
        # Cards list does not expose a simple data-cost; keep None here and optionally
        # resolve from detail pages in a later enrichment step.
        cost = _extract_cost_from_box(box)

        desc_node = box.select_one(".desc-base") or box.select_one(".relic-desc") or box.select_one(".desc-upg")
        desc = desc_node.get_text(" ", strip=True) if desc_node else ""
        desc = re.sub(r"\s+", " ", desc).strip()
        upg_node = box.select_one(".desc-upg")
        upg_desc = upg_node.get_text(" ", strip=True) if upg_node else ""
        upg_desc = re.sub(r"\s+", " ", upg_desc).strip() or None

        out.append(
            RawCardRecord(
                name=name,
                internal_name=internal,
                character=character,
                cost=cost,
                rarity=rarity,
                type=ctype,
                raw_description=desc,
                upgraded_description=upg_desc,
                source_url=detail_url or source_url,
                source_fetched_at=fetched_at,
            )
        )
    return out


def _extract_cost_from_box(box: Any) -> int | None:
    # Some skins expose textual cost in dedicated cost nodes; parse if present.
    cost_node = box.select_one(".card-cost, .cost")
    if cost_node:
        val = _parse_int_maybe(cost_node.get_text(" ", strip=True))
        if val is not None:
            return val

    # No deterministic cost field found in current Cards_List markup.
    return None


def _internal_from_link_or_name(href: str | None, name: str) -> str:
    if href and "/wiki/" in href:
        slug = href.split("/wiki/", 1)[1]
        slug = slug.split(":", 1)[-1]
        slug = slug.split("?", 1)[0]
        slug = re.sub(r"\([^)]*\)", "", slug)  # remove "(Ironclad)" suffixes
        slug = slug.replace("_", " ")
        return _guess_internal_name(slug)
    return _guess_internal_name(name)


def _clean(v: Any) -> str | None:
    if v is None:
        return None
    s = str(v).strip()
    return s or None


def enrich_cards_with_detail_pages(
    cards: list[RawCardRecord],
    fetcher: Any,
    max_fetch: int = 300,
    force: bool = False,
) -> list[RawCardRecord]:
    """
    Fill missing objective fields (currently cost) from individual card pages.
    Uses cached fetcher to avoid aggressive refetch.
    """

    out: list[RawCardRecord] = []
    fetched = 0
    for card in cards:
        if card.cost is not None or fetched >= max_fetch:
            out.append(card)
            continue
        url = card.source_url if "Slay_the_Spire_2:" in (card.source_url or "") else ""
        if not url:
            slug = _slug_from_internal(card.internal_name or card.name)
            if not slug:
                out.append(card)
                continue
            url = f"https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2:{slug}"
        cc = fetcher.get_text(url, force=force)
        fetched += 1
        if not cc.text:
            out.append(card)
            continue
        cost, upg_cost, upg_desc = _extract_detail_fields_html(cc.text)
        if cost is not None:
            card.cost = cost
        if upg_cost is not None:
            card.upgrade_cost = upg_cost
        if upg_desc:
            card.upgraded_description = upg_desc
            if url not in (card.source_url or ""):
                card.source_url = card.source_url or url
        out.append(card)
    return out


def _slug_from_internal(internal: str) -> str:
    parts = re.findall(r"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|\d+", internal)
    if not parts:
        return internal
    return "_".join(parts)


def _extract_detail_fields_html(html: str) -> tuple[int | None, int | None, str | None]:
    soup = BeautifulSoup(html, "html.parser")
    text = soup.get_text(" ", strip=True)
    cost: int | None = None
    upg_cost: int | None = None
    upg_desc: str | None = None

    # Common sentence on wiki page: "Bash is a 2 cost ... Card."
    m = re.search(r"\bis a\s+([0-9xX]+)\s+cost\b", text)
    if m:
        v = m.group(1).lower()
        if v == "x":
            cost = 99
        else:
            try:
                cost = int(v)
            except ValueError:
                cost = None

    # Try to read upgraded section text if present.
    upg_node = soup.select_one(".desc-upg")
    if upg_node:
        s = re.sub(r"\s+", " ", upg_node.get_text(" ", strip=True)).strip()
        upg_desc = s or None

    # Common phrase in some pages: "Upgraded version costs X" etc.
    mu = re.search(r"\bupgrad(?:ed|e).*?\bcosts?\s+([0-9xX]+)\b", text, flags=re.I)
    if mu:
        v = mu.group(1).lower()
        if v == "x":
            upg_cost = 99
        else:
            try:
                upg_cost = int(v)
            except ValueError:
                upg_cost = None

    return cost, upg_cost, upg_desc


def _absolute_wiki_url(href: str | None) -> str | None:
    if not href:
        return None
    if href.startswith("http://") or href.startswith("https://"):
        return href
    if href.startswith("/wiki/"):
        return f"https://slaythespire.wiki.gg{href}"
    return None


def _pick_col(headers: list[str], keywords: list[str]) -> int | None:
    for i, h in enumerate(headers):
        for kw in keywords:
            if kw in h:
                return i
    return None
