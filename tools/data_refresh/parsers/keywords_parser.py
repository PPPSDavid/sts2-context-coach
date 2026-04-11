"""Parse STS2 wiki.gg keyword / status pages into keywords.json rows."""

from __future__ import annotations

import re
from urllib.parse import unquote, urlparse, urlunparse

from bs4 import BeautifulSoup, Tag

# Stop collecting body text when we hit these section headings (h2).
_H2_STOP_IDS = frozenset(
    {
        "sources",
        "interactions",
        "hints",
        "achievements",
        "update_history",
        "see_also",
        "navigation",
        "gallery",
    }
)
_H2_STOP_PREFIXES = ("update history", "see also")

# URLs matching these substrings are not standalone keyword pages (list pages, searches, etc.).
_HREF_SKIP_SUBSTRINGS = (
    "Potions_List",
    "Cards_List",
    "Relics_List",
    "Acts",
    ":Main",
    "search=",
    "action=edit",
    "Special:",
    "File:",
    "Category:",
)


def _absolute_url(base: str, href: str) -> str | None:
    if not href or href.startswith("#"):
        return None
    if href.startswith("//"):
        return "https:" + href
    if href.startswith("/"):
        p = urlparse(base)
        return urlunparse((p.scheme, p.netloc, href, "", "", ""))
    if href.startswith("http://") or href.startswith("https://"):
        return href
    return None


def term_from_sts2_wiki_url(url: str) -> str:
    """Derive display term from .../wiki/Slay_the_Spire_2:Page_Title (strip namespace, ununderscore)."""
    try:
        path = urlparse(url).path
    except Exception:
        path = url
    m = re.search(r"/wiki/Slay_the_Spire_2:(.+)$", path, re.I)
    if not m:
        return "Unknown"
    raw = unquote(m.group(1))
    if "#" in raw:
        raw = raw.split("#", 1)[0]
    return raw.replace("_", " ").strip() or "Unknown"


def _should_skip_href(href: str) -> bool:
    h = href
    return any(s in h for s in _HREF_SKIP_SUBSTRINGS)


def discover_keyword_page_urls(html: str, page_url: str) -> list[str]:
    """
    Collect keyword detail URLs from Debuffs/Buffs-style wikitables: row has a **Name** column
    linking to `Slay_the_Spire_2:*` pages (avoids harvesting card/relic links from other columns).
    """
    soup = BeautifulSoup(html, "html.parser")
    parsed_base = urlparse(page_url)
    base = f"{parsed_base.scheme}://{parsed_base.netloc}"
    seen: set[str] = set()
    out: list[str] = []

    for table in soup.select("table.wikitable"):
        header_row = table.find("tr")
        if not header_row:
            continue
        headers = [
            re.sub(r"\s+", " ", th.get_text(" ", strip=True)).lower() for th in header_row.find_all(["th", "td"])
        ]
        name_idx = None
        for i, h in enumerate(headers):
            if h == "name":
                name_idx = i
                break
        if name_idx is None:
            continue

        for tr in table.find_all("tr")[1:]:
            tds = tr.find_all("td", recursive=False)
            if name_idx >= len(tds):
                continue
            name_cell = tds[name_idx]
            for a in name_cell.select("a[href]"):
                href = str(a.get("href") or "").strip()
                if _should_skip_href(href):
                    continue
                abs_u = _absolute_url(base, href)
                if not abs_u or not re.search(r"/wiki/Slay_the_Spire_2:", abs_u, re.I):
                    continue
                p = urlparse(abs_u)
                clean = urlunparse((p.scheme, p.netloc, p.path, p.params, p.query, ""))
                link_text = a.get_text(" ", strip=True)
                if not link_text:
                    continue
                t_from_url = term_from_sts2_wiki_url(clean)
                if link_text.replace(" ", "_").lower() != t_from_url.replace(" ", "_").lower():
                    continue
                if clean in seen:
                    continue
                seen.add(clean)
                out.append(clean)

    return out


def _heading_key(h2: Tag) -> str:
    span = h2.find("span", class_=re.compile(r"mw-headline", re.I))
    if span and span.get("id"):
        return str(span["id"]).strip().lower().replace(" ", "_")
    return re.sub(r"\s+", "_", h2.get_text(" ", strip=True).lower().strip())


def _heading_title_text(h2: Tag) -> str:
    return h2.get_text(" ", strip=True).lower().strip()


def extract_keyword_definition_html(html: str, page_url: str) -> str:
    """
    Lead definition: text from .mw-parser-output until the first major h2
    (Sources, Interactions, ...).
    """
    soup = BeautifulSoup(html, "html.parser")
    root = soup.select_one(".mw-parser-output") or soup.select_one("#mw-content-text")
    if not root or not isinstance(root, Tag):
        return ""

    chunks: list[str] = []
    for child in root.children:
        if not isinstance(child, Tag):
            continue
        name = child.name.lower()
        if name == "h2":
            key = _heading_key(child)
            title = _heading_title_text(child)
            if key in _H2_STOP_IDS or any(title.startswith(p) for p in _H2_STOP_PREFIXES):
                break
            continue
        if name in ("p", "blockquote"):
            t = child.get_text(" ", strip=True)
            if len(t) > 2:
                chunks.append(t)
        elif name == "ul" and len(chunks) < 2:
            # Rare: some pages use a short ul after the title line
            items = [li.get_text(" ", strip=True) for li in child.find_all("li", recursive=False)]
            items = [x for x in items if x]
            if items:
                chunks.append("; ".join(items[:6]))

    text = " ".join(chunks)
    text = re.sub(r"\s+", " ", text).strip()
    if len(text) > 1200:
        text = text[:1197] + "..."
    return text


KEYWORD_MERGE_MAX_CHARS = 2000


def _truncate_keyword_text(text: str, max_len: int) -> str:
    text = re.sub(r"\s+", " ", text).strip()
    if len(text) <= max_len:
        return text
    return text[: max_len - 3].rstrip() + "..."


def _cell_plain_text(cell: Tag) -> str:
    return re.sub(r"\s+", " ", cell.get_text(" ", strip=True)).strip()


def _is_placeholder_cell(s: str) -> bool:
    t = (s or "").strip()
    return len(t) < 2 or t.upper() == "TBC"


def _mechanics_suffix(notes: str, stacking: str, caps: str) -> str:
    parts: list[str] = []
    n = re.sub(r"\s+", " ", (notes or "").strip())
    st = re.sub(r"\s+", " ", (stacking or "").strip())
    cp = re.sub(r"\s+", " ", (caps or "").strip())
    if not _is_placeholder_cell(n):
        parts.append(f"Notes: {n}")
    if not _is_placeholder_cell(st):
        parts.append(f"Stacking: {st}")
    if not _is_placeholder_cell(cp):
        parts.append(f"Caps: {cp}")
    return " ".join(parts)


def _compose_inline_definition(description: str, notes: str, stacking: str, caps: str) -> str:
    base = re.sub(r"\s+", " ", (description or "").strip())
    suf = _mechanics_suffix(notes, stacking, caps)
    if not suf:
        return base
    return f"{base} {suf}".strip()


def _status_table_column_indices(headers: list[str]) -> tuple[int | None, int | None, int | None, int | None, int | None]:
    name_i = desc_i = notes_i = stacking_i = caps_i = None
    for i, h in enumerate(headers):
        if h == "name":
            name_i = i
        elif h == "description":
            desc_i = i
        elif h in ("notes", "note"):
            notes_i = i
        elif "stacking" in h:
            stacking_i = i
        elif h.startswith("caps") or h.startswith("cap "):
            caps_i = i
    return name_i, desc_i, notes_i, stacking_i, caps_i


def _resolve_name_from_cell(name_cell: Tag, base_url: str) -> str:
    name = ""
    for a in name_cell.select('a[href*="/wiki/Slay_the_Spire_2:"]'):
        href = str(a.get("href") or "")
        if "action=edit" in href or "redlink" in str(a.get("class") or []):
            continue
        abs_u = _absolute_url(base_url, href)
        if not abs_u:
            continue
        p = urlparse(abs_u)
        clean = urlunparse((p.scheme, p.netloc, p.path, p.params, p.query, ""))
        link_text = a.get_text(" ", strip=True)
        if not link_text:
            continue
        t_from_url = term_from_sts2_wiki_url(clean)
        if link_text.replace(" ", "_").lower() != t_from_url.replace(" ", "_").lower():
            continue
        name = link_text
        break
    if not name:
        name = name_cell.get_text(" ", strip=True)
    return re.sub(r"\s+", " ", name).strip()


def iter_buff_debuff_wiki_rows(html: str, *, base_url: str) -> list[dict[str, str]]:
    """
    One row per **Name** from Buffs/Debuffs-style wikitables: description plus optional
    notes / stacking / caps (inline when there is no dedicated detail page).
    """
    soup = BeautifulSoup(html, "html.parser")
    rows: list[dict[str, str]] = []
    seen_keys: set[str] = set()

    for table in soup.select("table.wikitable"):
        header_row = table.find("tr")
        if not header_row:
            continue
        headers = [
            re.sub(r"\s+", " ", th.get_text(" ", strip=True)).lower() for th in header_row.find_all(["th", "td"])
        ]
        name_i, desc_i, notes_i, stacking_i, caps_i = _status_table_column_indices(headers)
        if name_i is None or desc_i is None:
            continue

        for tr in table.find_all("tr")[1:]:
            tds = tr.find_all("td", recursive=False)
            need = max(i for i in (name_i, desc_i, notes_i, stacking_i, caps_i) if i is not None)
            if need >= len(tds):
                continue
            name = _resolve_name_from_cell(tds[name_i], base_url)
            if len(name) < 2:
                continue
            key = name.lower()
            if key in seen_keys:
                continue
            seen_keys.add(key)

            desc = _cell_plain_text(tds[desc_i])
            notes = _cell_plain_text(tds[notes_i]) if notes_i is not None and notes_i < len(tds) else ""
            stacking = _cell_plain_text(tds[stacking_i]) if stacking_i is not None and stacking_i < len(tds) else ""
            caps = _cell_plain_text(tds[caps_i]) if caps_i is not None and caps_i < len(tds) else ""

            rows.append(
                {
                    "term": name,
                    "description": desc,
                    "notes": notes,
                    "stacking": stacking,
                    "caps": caps,
                }
            )

    return rows


def merge_index_tables_into_keywords(
    index_html: list[tuple[str, str]],
    by_term: dict[str, dict[str, str]],
    *,
    max_chars: int = KEYWORD_MERGE_MAX_CHARS,
) -> tuple[int, int]:
    """
    Apply Buffs/Debuffs wikitable rows to ``by_term``:

    - Terms not yet present: full definition = Description + Notes + Stacking + Caps (non-placeholder).
    - Terms already filled from a detail page: append only the mechanics suffix when non-empty,
      if it is not already contained in the existing definition (case-insensitive).

    Returns ``(table_fallback_rows_added, detail_page_supplements_applied)``.
    """
    fallback_added = 0
    supplements = 0

    for page_url, html in index_html:
        parsed_base = urlparse(page_url)
        base = f"{parsed_base.scheme}://{parsed_base.netloc}"

        for row in iter_buff_debuff_wiki_rows(html, base_url=base):
            key = row["term"].strip().lower()
            desc = row["description"]
            extra = _mechanics_suffix(row["notes"], row["stacking"], row["caps"])
            cur = by_term.get(key)

            if cur is None:
                if _is_placeholder_cell(desc) or len(desc) < 4:
                    continue
                full = _compose_inline_definition(desc, row["notes"], row["stacking"], row["caps"])
                full = _truncate_keyword_text(full, max_chars)
                by_term[key] = {"term": row["term"].strip(), "definition": full}
                fallback_added += 1
                continue

            if not extra:
                continue
            existing = cur["definition"].strip()
            if extra.lower() in existing.lower():
                continue
            merged = _truncate_keyword_text(f"{existing} {extra}", max_chars)
            cur["definition"] = merged
            supplements += 1

    return fallback_added, supplements


def parse_keyword_page(
    html: str,
    page_url: str,
    fetched_at: str,
    *,
    term_override: str | None = None,
) -> dict[str, str] | None:
    """One keywords.json entry, or None if definition is empty."""
    _ = fetched_at  # reserved for future provenance fields
    term = (term_override or term_from_sts2_wiki_url(page_url)).strip()
    definition = extract_keyword_definition_html(html, page_url)
    if not definition:
        return None
    return {"term": term, "definition": definition}
