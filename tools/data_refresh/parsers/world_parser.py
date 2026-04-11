"""Parse act / encounter / event / monster metadata from wiki pages."""

from __future__ import annotations

import json
import re
from typing import Any
from urllib.parse import quote_plus

from bs4 import BeautifulSoup

from models import RawActRecord, RawEncounterRecord, RawEventRecord, RawMonsterRecord, RawMonsterSkillRecord


def _guess_internal_name(display: str) -> str:
    s = re.sub(r"[^a-zA-Z0-9]+", "", display.strip())
    return s or "Unknown"


def _absolute_wiki_url(href: str | None) -> str | None:
    if not href:
        return None
    if href.startswith("//"):
        return f"https:{href}"
    if href.startswith("http://") or href.startswith("https://"):
        return href
    if href.startswith("/wiki/"):
        return f"https://slaythespire.wiki.gg{href}"
    return None


def _act_internal_from_url(url: str) -> str:
    slug = url.split(":", 1)[-1].split("#", 1)[0].split("?", 1)[0]
    slug = slug.replace("_", " ")
    return _guess_internal_name(slug)


def _entity_url_from_name(name: str) -> str:
    slug = re.sub(r"\s+", "_", name.strip())
    return f"https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2:{slug}"


def build_wiki_parse_api_url(page_title: str) -> str:
    # Use parse+wikitext endpoint for stable, script-friendly extraction.
    return (
        "https://slaythespire.wiki.gg/api.php"
        f"?action=parse&format=json&prop=wikitext&page={quote_plus(page_title)}"
    )


def extract_wikitext_from_parse_api_response(raw_text: str) -> str:
    try:
        data = json.loads(raw_text)
        return str((data.get("parse") or {}).get("wikitext", {}).get("*") or "")
    except Exception:
        return ""


def _page_title_from_wiki_url(url: str) -> str | None:
    if "/wiki/" not in url:
        return None
    title = url.split("/wiki/", 1)[1]
    title = title.split("?", 1)[0].split("#", 1)[0]
    return title or None


def extract_act_urls_from_acts_page(acts_html: str) -> list[str]:
    soup = BeautifulSoup(acts_html, "html.parser")
    urls: list[str] = []
    seen: set[str] = set()
    content = soup.find(class_="mw-parser-output") or soup
    for a in content.find_all("a", href=True):
        href = _absolute_wiki_url(a.get("href"))
        if not href or "Slay_the_Spire_2:" not in href:
            continue
        title = (a.get("title") or a.get_text(" ", strip=True) or "")
        title_l = title.lower()
        slug = href.split("Slay_the_Spire_2:", 1)[-1].split("#", 1)[0].split("?", 1)[0]
        slug_l = slug.lower()
        if (
            "acts" in title_l
            or "main" in title_l
            or "cards" in title_l
            or "relic" in title_l
            or "events_list" in slug_l
            or "potions_list" in slug_l
            or "ironclad" in slug_l
            or "silent" in slug_l
            or "defect" in slug_l
            or "regent" in slug_l
            or "necrobinder" in slug_l
        ):
            continue
        if href not in seen:
            seen.add(href)
            urls.append(href)
    # Acts page should define a small fixed set; keep stable and bounded.
    urls.sort()
    return urls[:4] if len(urls) >= 4 else urls


def extract_act_urls_from_acts_wikitext(wikitext: str) -> list[str]:
    # Acts page has stable lines: ''See main article: {{2|Overgrowth}}''
    names: list[str] = []
    for m in re.finditer(r"See main article:\s*\{\{2\|([^}|#]+)", wikitext, flags=re.IGNORECASE):
        n = (m.group(1) or "").strip()
        if n:
            names.append(n)

    out: list[str] = []
    seen: set[str] = set()
    for n in names:
        k = n.lower()
        if k in seen:
            continue
        seen.add(k)
        slug = n.replace(" ", "_")
        out.append(f"https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2:{slug}")
    return out[:4] if len(out) >= 4 else out


def parse_act_page(
    html: str,
    source_url: str,
    fetched_at: str,
) -> tuple[RawActRecord, list[RawEventRecord], list[RawEncounterRecord], list[RawMonsterRecord]]:
    soup = BeautifulSoup(html, "html.parser")
    content = soup.find(class_="mw-parser-output") or soup

    slug = source_url.split("Slay_the_Spire_2:", 1)[-1].split("#", 1)[0].split("?", 1)[0]
    act_name = slug.replace("_", " ")
    act = RawActRecord(
        name=act_name,
        internal_name=_act_internal_from_url(source_url),
        source_url=source_url,
        source_fetched_at=fetched_at,
    )

    events: list[RawEventRecord] = []
    encounters: list[RawEncounterRecord] = []
    monsters: list[RawMonsterRecord] = []

    section = ""
    for node in content.find_all(["h2", "h3", "h4", "h5", "a", "li", "td", "th"]):
        if node.name in {"h2", "h3", "h4", "h5"}:
            section = node.get_text(" ", strip=True).lower()
            continue
        if node.name not in {"a", "li", "td", "th"}:
            continue

        link = node if node.name == "a" else node.find("a", href=True)
        if link is None:
            continue
        href = _absolute_wiki_url(link.get("href"))
        name = link.get_text(" ", strip=True)
        if not name:
            name = (link.get("title") or "").split(":", 1)[-1].replace("_", " ").strip()
        if not href or not name or "Slay_the_Spire_2:" not in href:
            continue

        if "event" in section:
            events.append(
                RawEventRecord(
                    name=name,
                    internal_name=_guess_internal_name(name),
                    act_internal_name=act.internal_name,
                    source_url=href,
                    source_fetched_at=fetched_at,
                )
            )
            act.event_names.append(name)
        elif "elite" in section:
            encounters.append(
                RawEncounterRecord(
                    name=name,
                    internal_name=_guess_internal_name(name),
                    act_internal_name=act.internal_name,
                    encounter_type="elite",
                    source_url=href,
                    source_fetched_at=fetched_at,
                )
            )
            act.elite_names.append(name)
        elif "boss" in section:
            encounters.append(
                RawEncounterRecord(
                    name=name,
                    internal_name=_guess_internal_name(name),
                    act_internal_name=act.internal_name,
                    encounter_type="boss",
                    source_url=href,
                    source_fetched_at=fetched_at,
                )
            )
            act.boss_names.append(name)
        elif "encounter" in section:
            encounters.append(
                RawEncounterRecord(
                    name=name,
                    internal_name=_guess_internal_name(name),
                    act_internal_name=act.internal_name,
                    encounter_type="normal",
                    source_url=href,
                    source_fetched_at=fetched_at,
                )
            )
            act.encounter_names.append(name)
        elif "monster" in section or "enemy" in section:
            monsters.append(
                RawMonsterRecord(
                    name=name,
                    internal_name=_guess_internal_name(name),
                    act_internal_name=act.internal_name,
                    source_url=href,
                    source_fetched_at=fetched_at,
                )
            )
            act.monster_names.append(name)

    # Also capture plain-text bullet items under key sections (some pages omit links).
    section = ""
    for node in content.find_all(["h2", "h3", "h4", "h5", "li"]):
        if node.name in {"h2", "h3", "h4", "h5"}:
            section = node.get_text(" ", strip=True).lower()
            continue
        if node.name != "li":
            continue
        text = re.sub(r"\s+", " ", node.get_text(" ", strip=True)).strip()
        if not text:
            continue
        if text.startswith("5 Bosses"):
            continue
        if "event" in section:
            if not any(e.name.lower() == text.lower() for e in events):
                events.append(
                    RawEventRecord(
                        name=text,
                        internal_name=_guess_internal_name(text),
                        act_internal_name=act.internal_name,
                        source_url=_entity_url_from_name(text),
                        source_fetched_at=fetched_at,
                    )
                )
                act.event_names.append(text)
        elif "elite" in section:
            if not any(e.name.lower() == text.lower() and e.encounter_type == "elite" for e in encounters):
                encounters.append(
                    RawEncounterRecord(
                        name=text,
                        internal_name=_guess_internal_name(text),
                        act_internal_name=act.internal_name,
                        encounter_type="elite",
                        source_url=_entity_url_from_name(text),
                        source_fetched_at=fetched_at,
                    )
                )
                act.elite_names.append(text)
        elif "boss" in section:
            if not any(e.name.lower() == text.lower() and e.encounter_type == "boss" for e in encounters):
                encounters.append(
                    RawEncounterRecord(
                        name=text,
                        internal_name=_guess_internal_name(text),
                        act_internal_name=act.internal_name,
                        encounter_type="boss",
                        source_url=_entity_url_from_name(text),
                        source_fetched_at=fetched_at,
                    )
                )
                act.boss_names.append(text)
        elif "monster" in section:
            if not any(m.name.lower() == text.lower() for m in monsters):
                monsters.append(
                    RawMonsterRecord(
                        name=text,
                        internal_name=_guess_internal_name(text),
                        act_internal_name=act.internal_name,
                        source_url=_entity_url_from_name(text),
                        source_fetched_at=fetched_at,
                    )
                )
                act.monster_names.append(text)

    # Fallback pass: classify links by URL/title hints when section-based parsing found nothing.
    if not events and not encounters and not monsters:
        for a in content.find_all("a", href=True):
            href = _absolute_wiki_url(a.get("href"))
            name = a.get_text(" ", strip=True)
            if not name:
                name = (a.get("title") or "").split(":", 1)[-1].replace("_", " ").strip()
            title = (a.get("title") or name or "").lower()
            if not href or not name or "Slay_the_Spire_2:" not in href:
                continue
            if "category:" in href.lower() or "cards_list" in href.lower() or "relics_list" in href.lower():
                continue

            if "monster" in title or "enemy" in title or "boss" in title:
                monsters.append(
                    RawMonsterRecord(
                        name=name,
                        internal_name=_guess_internal_name(name),
                        act_internal_name=act.internal_name,
                        source_url=href,
                        source_fetched_at=fetched_at,
                    )
                )
                act.monster_names.append(name)
            elif "event" in title:
                events.append(
                    RawEventRecord(
                        name=name,
                        internal_name=_guess_internal_name(name),
                        act_internal_name=act.internal_name,
                        source_url=href,
                        source_fetched_at=fetched_at,
                    )
                )
                act.event_names.append(name)
            elif "boss" in title:
                encounters.append(
                    RawEncounterRecord(
                        name=name,
                        internal_name=_guess_internal_name(name),
                        act_internal_name=act.internal_name,
                        encounter_type="boss",
                        source_url=href,
                        source_fetched_at=fetched_at,
                    )
                )
                act.boss_names.append(name)
            elif "elite" in title:
                encounters.append(
                    RawEncounterRecord(
                        name=name,
                        internal_name=_guess_internal_name(name),
                        act_internal_name=act.internal_name,
                        encounter_type="elite",
                        source_url=href,
                        source_fetched_at=fetched_at,
                    )
                )
                act.elite_names.append(name)
            elif "encounter" in title or "fight" in title:
                encounters.append(
                    RawEncounterRecord(
                        name=name,
                        internal_name=_guess_internal_name(name),
                        act_internal_name=act.internal_name,
                        encounter_type="normal",
                        source_url=href,
                        source_fetched_at=fetched_at,
                    )
                )
                act.encounter_names.append(name)

    # Deduplicate list fields while preserving order.
    act.event_names = _dedupe(act.event_names)
    act.encounter_names = _dedupe(act.encounter_names)
    act.elite_names = _dedupe(act.elite_names)
    act.boss_names = _dedupe(act.boss_names)
    act.monster_names = _dedupe(act.monster_names)
    events = _dedupe_records(events)
    encounters = _dedupe_records(encounters)
    monsters = _dedupe_records(monsters)
    return act, events, encounters, monsters


def parse_act_wikitext(
    wikitext: str,
    source_url: str,
    fetched_at: str,
) -> tuple[RawActRecord, list[RawEventRecord], list[RawEncounterRecord], list[RawMonsterRecord]]:
    slug = source_url.split("Slay_the_Spire_2:", 1)[-1].split("#", 1)[0].split("?", 1)[0]
    act_name = slug.replace("_", " ")
    act = RawActRecord(
        name=act_name,
        internal_name=_guess_internal_name(act_name),
        source_url=source_url,
        source_fetched_at=fetched_at,
    )

    events: list[RawEventRecord] = []
    encounters: list[RawEncounterRecord] = []
    monsters: list[RawMonsterRecord] = []

    section = ""
    for raw in wikitext.splitlines():
        line = raw.strip()
        if not line:
            continue
        if line.startswith("==") and line.endswith("=="):
            section = re.sub(r"=+", "", line).strip().lower()
            continue
        if not section:
            continue

        if "event" in section:
            for name, source_url in _extract_entity_refs_from_wikitext_line(line):
                if name.lower() in {"events", "exclusive events", "shared events"}:
                    continue
                events.append(
                    RawEventRecord(
                        name=name,
                        internal_name=_guess_internal_name(name),
                        act_internal_name=act.internal_name,
                        source_url=source_url or _entity_url_from_name(name),
                        source_fetched_at=fetched_at,
                    )
                )
                act.event_names.append(name)
            continue

        if "monster" in section:
            for name, source_url in _extract_entity_refs_from_wikitext_line(line):
                monsters.append(
                    RawMonsterRecord(
                        name=name,
                        internal_name=_guess_internal_name(name),
                        act_internal_name=act.internal_name,
                        source_url=source_url or _entity_url_from_name(name),
                        source_fetched_at=fetched_at,
                    )
                )
                act.monster_names.append(name)
            continue

        if "elite" in section:
            for name, source_url in _extract_entity_refs_from_wikitext_line(line):
                encounters.append(
                    RawEncounterRecord(
                        name=name,
                        internal_name=_guess_internal_name(name),
                        act_internal_name=act.internal_name,
                        encounter_type="elite",
                        source_url=source_url or _entity_url_from_name(name),
                        source_fetched_at=fetched_at,
                    )
                )
                act.elite_names.append(name)
            continue

        if "boss" in section:
            for name, source_url in _extract_entity_refs_from_wikitext_line(line):
                encounters.append(
                    RawEncounterRecord(
                        name=name,
                        internal_name=_guess_internal_name(name),
                        act_internal_name=act.internal_name,
                        encounter_type="boss",
                        source_url=source_url or _entity_url_from_name(name),
                        source_fetched_at=fetched_at,
                    )
                )
                act.boss_names.append(name)
            continue

    act.event_names = _dedupe(act.event_names)
    act.encounter_names = _dedupe(act.encounter_names)
    act.elite_names = _dedupe(act.elite_names)
    act.boss_names = _dedupe(act.boss_names)
    act.monster_names = _dedupe(act.monster_names)
    events = _dedupe_records(events)
    encounters = _dedupe_records(encounters)
    monsters = _dedupe_records(monsters)
    return act, events, encounters, monsters


def enrich_events_with_detail_pages(events: list[RawEventRecord], fetcher: Any, force: bool = False) -> list[RawEventRecord]:
    out: list[RawEventRecord] = []
    seen: set[str] = set()
    for e in events:
        key = e.source_url or e.name
        if key in seen:
            out.append(e)
            continue
        seen.add(key)
        wikitext = _fetch_entity_wikitext(fetcher, e.source_url, force=force)
        if wikitext:
            desc = _extract_summary_from_wikitext(wikitext)
            if desc:
                e.raw_description = desc
        out.append(e)
    return out


def enrich_encounters_with_detail_pages(
    encounters: list[RawEncounterRecord], fetcher: Any, force: bool = False
) -> list[RawEncounterRecord]:
    out: list[RawEncounterRecord] = []
    seen: set[str] = set()
    for e in encounters:
        key = e.source_url or e.name
        if key in seen:
            out.append(e)
            continue
        seen.add(key)
        wikitext = _fetch_entity_wikitext(fetcher, e.source_url, force=force)
        if wikitext:
            desc = _extract_summary_from_wikitext(wikitext)
            if desc:
                e.raw_description = desc
        out.append(e)
    return out


def enrich_monsters_with_detail_pages(monsters: list[RawMonsterRecord], fetcher: Any, force: bool = False) -> list[RawMonsterRecord]:
    out: list[RawMonsterRecord] = []
    seen: set[str] = set()
    for m in monsters:
        if not m.source_url or m.source_url in seen:
            out.append(m)
            continue
        seen.add(m.source_url)
        cc = fetcher.get_text(m.source_url, force=force)
        resolved_url, wikitext = _resolve_monster_wikitext(fetcher, m, force=force)
        if not cc.text and not wikitext:
            out.append(m)
            continue
        skills, desc = _extract_monster_detail(cc.text) if cc.text else ([], "")
        if wikitext:
            wt_desc = _extract_summary_from_wikitext(wikitext)
            if wt_desc:
                desc = wt_desc
            if not skills:
                skills = _extract_skills_from_wikitext(wikitext)
        if desc and not m.raw_description:
            m.raw_description = desc
        if skills:
            m.skills = skills
        if resolved_url and resolved_url != m.source_url:
            m.source_url = resolved_url
        out.append(m)
    return out


def _extract_monster_detail(html: str) -> tuple[list[RawMonsterSkillRecord], str]:
    soup = BeautifulSoup(html, "html.parser")
    content = soup.find(class_="mw-parser-output") or soup
    text = re.sub(r"\s+", " ", content.get_text(" ", strip=True)).strip()
    desc = text[:260]

    skills: list[RawMonsterSkillRecord] = []
    section = ""
    for node in content.find_all(["h2", "h3", "li"]):
        if node.name in {"h2", "h3"}:
            section = node.get_text(" ", strip=True).lower()
            continue
        if node.name == "li" and ("move" in section or "intent" in section or "skill" in section or "ability" in section):
            line = re.sub(r"\s+", " ", node.get_text(" ", strip=True)).strip()
            if not line:
                continue
            name = line.split(":", 1)[0][:80]
            skills.append(RawMonsterSkillRecord(name=name, summary=line[:240]))
    return _dedupe_skill_records(skills), desc


def _extract_skills_from_wikitext(wikitext: str) -> list[RawMonsterSkillRecord]:
    skills: list[RawMonsterSkillRecord] = []
    section = ""
    for raw in wikitext.splitlines():
        line = raw.strip()
        if not line:
            continue
        if line.startswith("==") and line.endswith("=="):
            section = re.sub(r"=+", "", line).strip().lower()
            continue
        if not section:
            continue
        if not any(k in section for k in ("move", "intent", "skill", "ability")):
            continue
        if not line.startswith("*"):
            continue
        cleaned = re.sub(r"^\*\s*", "", line).strip()
        cleaned = re.sub(r"\{\{[^}]+\}\}", "", cleaned)
        cleaned = re.sub(r"\[\[([^]|]+)\|([^]]+)\]\]", r"\2", cleaned)
        cleaned = re.sub(r"\[\[([^]]+)\]\]", r"\1", cleaned)
        cleaned = re.sub(r"\s+", " ", cleaned).strip()
        if not cleaned:
            continue
        name = cleaned.split(":", 1)[0][:80]
        skills.append(RawMonsterSkillRecord(name=name, summary=cleaned[:240]))
    return _dedupe_skill_records(skills)


def _extract_summary_from_wikitext(wikitext: str) -> str:
    for raw in wikitext.splitlines():
        line = raw.strip()
        if not line:
            continue
        if line.startswith(("{{", "}}", "__", "==", "|", "*", ";", ":")):
            continue
        text = re.sub(r"\[\[([^]|]+)\|([^]]+)\]\]", r"\2", line)
        text = re.sub(r"\[\[([^]]+)\]\]", r"\1", text)
        text = re.sub(r"\{\{[^}]+\}\}", "", text)
        text = re.sub(r"<[^>]+>", "", text)
        text = re.sub(r"\s+", " ", text).strip()
        if len(text) < 16:
            continue
        return text[:320]
    return ""


def _fetch_entity_wikitext(fetcher: Any, url: str, force: bool = False) -> str:
    title = _page_title_from_wiki_url(url)
    if not title:
        return ""
    api = build_wiki_parse_api_url(title)
    cc = fetcher.get_text(api, force=force)
    if not cc.text:
        return ""
    return extract_wikitext_from_parse_api_response(cc.text)


def _resolve_monster_wikitext(fetcher: Any, monster: RawMonsterRecord, force: bool = False) -> tuple[str | None, str]:
    # 1) Try direct title first; this is cheapest and most accurate.
    direct_wikitext = _fetch_entity_wikitext(fetcher, monster.source_url, force=force)
    if _extract_summary_from_wikitext(direct_wikitext):
        return monster.source_url, direct_wikitext

    # 2) Fallback: search for likely parent/group pages and pick best candidate.
    best_title = ""
    best_wikitext = ""
    best_score = -1
    for query in _monster_search_queries(monster.name):
        for title in _wiki_search_titles(fetcher, query):
            wikitext = _fetch_wikitext_by_title(fetcher, title, force=False)
            if not wikitext:
                continue
            score = _score_monster_candidate(monster.name, title, wikitext)
            if score > best_score:
                best_score = score
                best_title = title
                best_wikitext = wikitext

    if best_title and best_wikitext and best_score >= 2:
        return f"https://slaythespire.wiki.gg/wiki/{best_title}", best_wikitext
    return monster.source_url, direct_wikitext


def _wiki_search_titles(fetcher: Any, query: str) -> list[str]:
    api = (
        "https://slaythespire.wiki.gg/api.php"
        f"?action=query&format=json&list=search&srlimit=5&srsearch={quote_plus(query)}"
    )
    cc = fetcher.get_text(api, force=False)
    if not cc.text:
        return []
    try:
        data = json.loads(cc.text)
        items = (data.get("query") or {}).get("search") or []
    except Exception:
        return []
    out: list[str] = []
    seen: set[str] = set()
    for item in items:
        title = str((item or {}).get("title") or "").strip()
        if not title or title in seen:
            continue
        seen.add(title)
        out.append(title)
    return out[:5]


def _fetch_wikitext_by_title(fetcher: Any, page_title: str, force: bool = False) -> str:
    cc = fetcher.get_text(build_wiki_parse_api_url(page_title), force=force)
    if not cc.text:
        return ""
    return extract_wikitext_from_parse_api_response(cc.text)


def _monster_search_queries(name: str) -> list[str]:
    base = name.strip()
    out = [base]
    no_paren = re.sub(r"\s*\([^)]*\)", "", base).strip()
    if no_paren and no_paren.lower() != base.lower():
        out.append(no_paren)
    if " " in no_paren:
        out.append(f"Slay the Spire 2 {no_paren.split()[-1]}")
    deduped: list[str] = []
    seen: set[str] = set()
    for q in out:
        key = q.lower()
        if not q or key in seen:
            continue
        seen.add(key)
        deduped.append(q)
    return deduped


def _score_monster_candidate(name: str, title: str, wikitext: str) -> int:
    nn = _norm_text(name)
    nt = _norm_text(title)
    nw = _norm_text(wikitext)
    score = 0
    if nn and nn in nt:
        score += 4
    if nn and nn in nw:
        score += 5
    name_tokens = {t for t in nn.split() if len(t) > 2}
    title_tokens = {t for t in nt.split() if len(t) > 2}
    score += len(name_tokens & title_tokens)
    if _extract_summary_from_wikitext(wikitext):
        score += 1
    return score


def _norm_text(text: str) -> str:
    cleaned = re.sub(r"[^a-z0-9]+", " ", text.lower())
    return re.sub(r"\s+", " ", cleaned).strip()


def _dedupe(items: list[str]) -> list[str]:
    out: list[str] = []
    seen: set[str] = set()
    for item in items:
        k = item.strip().lower()
        if not k or k in seen:
            continue
        seen.add(k)
        out.append(item)
    return out


def _dedupe_records(items: list[Any]) -> list[Any]:
    out: list[Any] = []
    seen: set[str] = set()
    for item in items:
        name = getattr(item, "internal_name", None) or getattr(item, "name", "")
        key = str(name).strip().lower()
        if not key or key in seen:
            continue
        seen.add(key)
        out.append(item)
    return out


def _dedupe_skill_records(items: list[RawMonsterSkillRecord]) -> list[RawMonsterSkillRecord]:
    out: list[RawMonsterSkillRecord] = []
    seen: set[str] = set()
    for item in items:
        key = item.name.strip().lower()
        if not key or key in seen:
            continue
        seen.add(key)
        out.append(item)
    return out


def _extract_names_from_wikitext_line(line: str) -> list[str]:
    return [name for name, _ in _extract_entity_refs_from_wikitext_line(line)]


def _extract_entity_refs_from_wikitext_line(line: str) -> list[tuple[str, str | None]]:
    refs: list[tuple[str, str | None]] = []
    seen: set[str] = set()

    def _add(name: str, url: str | None = None) -> None:
        clean_name = name.strip()
        if not clean_name:
            return
        lower_name = clean_name.lower()
        if (
            "=" in clean_name
            or "class=" in lower_name
            or "link=" in lower_name
            or clean_name.startswith(("File:", "Category:", "Template:"))
        ):
            return
        key = clean_name.lower()
        if key in seen:
            return
        seen.add(key)
        refs.append((clean_name, url))

    def _wiki_url_from_target(target: str) -> str:
        # target may contain spaces, prefix, and/or anchor.
        normalized = target.strip().replace(" ", "_")
        if ":" not in normalized:
            normalized = f"Slay_the_Spire_2:{normalized}"
        return f"https://slaythespire.wiki.gg/wiki/{normalized}"

    # Event template on wiki: {{E|Aroma of Chaos|2}}
    for m in re.finditer(r"\{\{E\|([^}|]+)", line):
        n = (m.group(1) or "").strip()
        if n:
            _add(n, _entity_url_from_name(n))
    # Generic template used heavily: {{2|Name}} or {{2|Page#Anchor|Display}}
    for m in re.finditer(r"\{\{2\|([^}|]+)(?:\|([^}]+))?\}\}", line):
        base = (m.group(1) or "").strip()
        disp = (m.group(2) or "").strip()
        n = disp or base
        if n:
            _add(n, _wiki_url_from_target(base))
    # Explicit links: [[Slay the Spire 2:Name|Display]]
    for m in re.finditer(r"\[\[([^]|]+)(?:\|([^]]+))?\]\]", line):
        base = (m.group(1) or "").strip()
        disp = (m.group(2) or "").strip()
        if not base:
            continue
        n = disp or base.split(":", 1)[-1]
        if n:
            if base.lower().startswith("slay the spire 2:"):
                base = "Slay_the_Spire_2:" + base.split(":", 1)[-1]
            _add(n, _wiki_url_from_target(base))
    # Bullet plain text fallback.
    if line.startswith("*"):
        plain = re.sub(r"^\*\s*", "", line).strip()
        plain = re.sub(r"\{\{[^}]+\}\}", "", plain)
        plain = re.sub(r"\[\[[^]]+\]\]", "", plain)
        plain = re.sub(r"\s+", " ", plain).strip(" -")
        if plain and plain.lower() not in {"5 bosses"}:
            _add(plain)
    return refs
