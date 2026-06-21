"""Fetch Steam RSS / news pages for patch notes."""

from __future__ import annotations

from dataclasses import dataclass

from config import SourceUrls
from sources.base import CachedContent, CachedFetcher


@dataclass
class SteamPatchSource:
    fetcher: CachedFetcher
    urls: SourceUrls

    def fetch_rss(self, force: bool = False) -> CachedContent:
        return self.fetcher.get_text(self.urls.steam_news_rss, force=force)

    def fetch_html_index(self, force: bool = False) -> CachedContent:
        return self.fetcher.get_text(self.urls.steam_patch_notes_html, force=force)

    def fetch_all(self, force: bool = False) -> dict[str, CachedContent]:
        return {
            self.urls.steam_news_rss: self.fetch_rss(force),
            self.urls.steam_patch_notes_html: self.fetch_html_index(force),
        }
