"""Optional community sources — stub for future curated URLs."""

from __future__ import annotations

from dataclasses import dataclass, field

from sources.base import CachedContent, CachedFetcher


@dataclass
class CommunityGuidesSource:
    fetcher: CachedFetcher
    guide_urls: list[str] = field(default_factory=list)

    def fetch_all(self, force: bool = False) -> dict[str, CachedContent]:
        return {u: self.fetcher.get_text(u, force=force) for u in self.guide_urls}
