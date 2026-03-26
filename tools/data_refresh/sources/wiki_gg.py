"""Fetch wiki.gg HTML pages for Slay the Spire 2."""

from __future__ import annotations

from dataclasses import dataclass

from config import SourceUrls
from sources.base import CachedContent, CachedFetcher


@dataclass
class WikiGgSource:
    fetcher: CachedFetcher
    urls: SourceUrls

    def fetch_all(self, force: bool = False) -> dict[str, CachedContent]:
        """Return url -> cached content for main, cards, relics, and character pages."""

        out: dict[str, CachedContent] = {}
        for label, url in self._all_urls():
            out[url] = self.fetcher.get_text(url, force=force)
        return out

    def _all_urls(self) -> list[tuple[str, str]]:
        pairs: list[tuple[str, str]] = [
            ("main", self.urls.wiki_main),
            ("cards", self.urls.wiki_cards_list),
            ("relics", self.urls.wiki_relics_list),
        ]
        for u in self.urls.wiki_character_pages:
            pairs.append(("character", u))
        return pairs

    def list_urls(self) -> list[str]:
        return [u for _, u in self._all_urls()]
