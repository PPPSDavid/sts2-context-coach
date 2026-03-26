"""Load settings from environment, optional YAML, and defaults."""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import yaml

GENERATOR_VERSION = "0.1.0"


def _project_root() -> Path:
    # tools/data_refresh/config.py -> project root is parents[2]
    return Path(__file__).resolve().parents[2]


@dataclass
class ToolPaths:
    """Resolved filesystem paths for the data refresh tool."""

    project_root: Path
    tool_root: Path
    data_dir: Path
    output_dir: Path
    cache_dir: Path
    backups_dir: Path
    cards_production: Path
    relics_production: Path


@dataclass
class SourceUrls:
    """Primary remote sources (edit config.yaml or env to match live wiki paths)."""

    wiki_main: str = "https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2:Main"
    wiki_cards_list: str = "https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2:Cards_List"
    wiki_relics_list: str = "https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2:Relics_List"
    wiki_character_pages: list[str] = field(
        default_factory=lambda: [
            "https://slaythespire.wiki.gg/wiki/Slay_the_Spire_2:Ironclad",
        ]
    )
    # Steam news RSS for the game (replace with real AppID when known)
    steam_news_rss: str = "https://steamcommunity.com/games/2868840/rss/"
    steam_patch_notes_html: str = "https://store.steampowered.com/news/?appids=2868840"


@dataclass
class FetchConfig:
    cache_ttl_seconds: int = 6 * 3600
    user_agent: str = "Sts2ContextCoach-DataRefresh/0.1 (+local offline tool)"
    request_timeout_seconds: float = 45.0


@dataclass
class LlmConfig:
    """Pluggable LLM: set provider + api key env name; disabled if no key."""

    enabled: bool = False
    provider: str = "openai_compatible"  # openai_compatible | openrouter | anthropic | none
    api_key_env: str = "OPENAI_API_KEY"
    base_url: str | None = "https://api.openai.com/v1"
    model: str = "gpt-4o-mini"
    max_items_per_run: int = 50
    # Optional direct key from config file (prefer env in team settings).
    api_key: str | None = None
    # Optional HTTP headers (e.g. OpenRouter: HTTP-Referer, X-Title)
    extra_headers: dict[str, str] = field(default_factory=dict)


@dataclass
class AppConfig:
    paths: ToolPaths
    sources: SourceUrls
    fetch: FetchConfig
    llm: LlmConfig
    merge_mode: str = "safe"  # safe | suggest | overwrite


def default_paths(project_root: Path | None = None) -> ToolPaths:
    root = project_root or _project_root()
    tool = root / "tools" / "data_refresh"
    # Project ships production JSON under `Data/` (config.yaml can point elsewhere)
    data = root / "Data"
    return ToolPaths(
        project_root=root,
        tool_root=tool,
        data_dir=data,
        output_dir=tool / "output",
        cache_dir=tool / "cache",
        backups_dir=tool / "backups",
        cards_production=data / "cards.json",
        relics_production=data / "relics.json",
    )


def load_config(config_path: Path | None = None) -> AppConfig:
    """Merge optional YAML file with environment overrides."""

    paths = default_paths()
    sources = SourceUrls()
    fetch = FetchConfig()
    llm = LlmConfig()
    merge_mode = os.environ.get("STS2_REFRESH_MERGE_MODE", "safe")

    if config_path is None:
        candidate = paths.tool_root / "config.yaml"
        if candidate.exists():
            config_path = candidate

    if config_path and config_path.exists():
        raw = yaml.safe_load(config_path.read_text(encoding="utf-8")) or {}
        merge_mode = _apply_yaml(raw, paths, sources, fetch, llm, merge_mode)

    # Env overrides
    if ca := os.environ.get("STS2_DATA_DIR"):
        d = Path(ca)
        if not d.is_absolute():
            d = paths.project_root / d
        paths = ToolPaths(
            project_root=paths.project_root,
            tool_root=paths.tool_root,
            data_dir=d,
            output_dir=paths.output_dir,
            cache_dir=paths.cache_dir,
            backups_dir=paths.backups_dir,
            cards_production=d / "cards.json",
            relics_production=d / "relics.json",
        )
    if key := os.environ.get("STS2_REFRESH_LLM_KEY"):
        llm.enabled = True
        os.environ[llm.api_key_env] = key
    elif llm.api_key:
        llm.enabled = True
        os.environ[llm.api_key_env] = llm.api_key

    mm = os.environ.get("STS2_REFRESH_MERGE_MODE") or merge_mode
    return AppConfig(paths=paths, sources=sources, fetch=fetch, llm=llm, merge_mode=mm)


def _apply_yaml(
    raw: dict[str, Any],
    paths: ToolPaths,
    sources: SourceUrls,
    fetch: FetchConfig,
    llm: LlmConfig,
    merge_mode: str,
) -> str:
    if "data_dir" in raw:
        d = Path(raw["data_dir"])
        if not d.is_absolute():
            d = paths.project_root / d
        paths.data_dir = d
        paths.cards_production = d / "cards.json"
        paths.relics_production = d / "relics.json"
    if "output_dir" in raw:
        o = Path(raw["output_dir"])
        paths.output_dir = o if o.is_absolute() else paths.tool_root / o
    if "cache_dir" in raw:
        c = Path(raw["cache_dir"])
        paths.cache_dir = c if c.is_absolute() else paths.tool_root / c
    if "backups_dir" in raw:
        b = Path(raw["backups_dir"])
        paths.backups_dir = b if b.is_absolute() else paths.tool_root / b

    s = raw.get("sources") or {}
    for k, v in s.items():
        if hasattr(sources, k) and v is not None:
            setattr(sources, k, v)

    f = raw.get("fetch") or {}
    for k, v in f.items():
        if hasattr(fetch, k) and v is not None:
            setattr(fetch, k, v)

    l = raw.get("llm") or {}
    for k, v in l.items():
        if hasattr(llm, k) and v is not None:
            setattr(llm, k, v)

    if "merge_mode" in raw:
        merge_mode = str(raw["merge_mode"])
    return merge_mode
