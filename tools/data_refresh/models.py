"""Normalized and intermediate record types for the data refresh pipeline."""

from __future__ import annotations

from datetime import datetime, timezone
from typing import Any, Literal

from pydantic import BaseModel, Field

ReviewStatus = Literal["auto", "needs_review", "approved", "rejected"]
ProvenanceSource = Literal["wiki", "steam", "llm", "manual", "merged", "unknown"]


class PatchContext(BaseModel):
    last_seen_patch: str | None = None
    recently_changed: bool = False
    related_patch_ids: list[str] = Field(default_factory=list)
    needs_rereview_due_to_patch: bool = False


class FieldProvenance(BaseModel):
    source: ProvenanceSource = "unknown"
    confidence: float = 1.0
    derived_from: str = ""


class EntityMeta(BaseModel):
    source_urls: list[str] = Field(default_factory=list)
    source_last_seen: str | None = None
    generated_at: str | None = None
    generator_version: str = ""
    review_status: ReviewStatus = "needs_review"
    last_reviewed_at: str | None = None
    review_notes: str | None = None
    reviewed_by: str | None = None
    patch_context: PatchContext = Field(default_factory=PatchContext)
    manual_override_fields: list[str] = Field(default_factory=list)
    field_provenance: dict[str, Any] = Field(default_factory=dict)


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


# --- Raw fetch layer (Stage B) ---


class RawCardRecord(BaseModel):
    name: str
    internal_name: str | None = None
    character: str | None = None
    cost: int | None = None
    rarity: str | None = None
    type: str | None = None
    raw_description: str = ""
    upgraded_description: str | None = None
    upgrade_cost: int | None = None
    source_url: str = ""
    source_fetched_at: str = ""


class RawRelicRecord(BaseModel):
    name: str
    internal_name: str | None = None
    character: str | None = None
    raw_description: str = ""
    source_url: str = ""
    source_fetched_at: str = ""


class RawPatchRecord(BaseModel):
    patch_id: str
    title: str = ""
    date: str | None = None
    body_text: str = ""
    source_url: str = ""
    source_fetched_at: str = ""


class AffectedEntityRef(BaseModel):
    type: Literal["card", "relic"]
    internal_name: str
    change_type: Literal["balance_change", "new_content", "bugfix", "unknown"] = "unknown"
    summary: str = ""


class PatchNoteEntry(BaseModel):
    patch_id: str
    date: str | None = None
    source_url: str = ""
    affected_entities: list[AffectedEntityRef] = Field(default_factory=list)


# --- LLM proposals (inferred, never trusted as objective truth) ---


class LlmCardEnrichment(BaseModel):
    tags: list[str] = Field(default_factory=list)
    synergy_tags: list[str] = Field(default_factory=list)
    role_tags: list[str] = Field(default_factory=list)
    impact_level: str | None = None
    notes: str | None = None
    confidence: float = 0.5
    inferred: bool = True
    upgraded_description: str | None = None
    upgrade_summary: str | None = None
    upgrade_cost_delta: int | None = None
    upgrade_block_delta: int | None = None
    upgrade_draw_delta: int | None = None
    upgrade_damage_delta: int | None = None
    upgrade_removes_exhaust: bool | None = None
    upgrade_major: bool | None = None
    upgrade_tier: str | None = None
    enchantment_potential_tier: str | None = None
    enchantment_tier_by_kind: dict[str, str] = Field(default_factory=dict)


class LlmRelicEnrichment(BaseModel):
    tags: list[str] = Field(default_factory=list)
    synergy_tags: list[str] = Field(default_factory=list)
    notes: str | None = None
    confidence: float = 0.5
    inferred: bool = True


# --- Review queue ---


class ReviewQueueItem(BaseModel):
    entity_type: Literal["card", "relic"]
    internal_name: str
    changed_fields: list[str] = Field(default_factory=list)
    previous: dict[str, Any] = Field(default_factory=dict)
    proposed: dict[str, Any] = Field(default_factory=dict)
    provenance: dict[str, Any] = Field(default_factory=dict)
    confidence: float = 1.0
    reason: str = ""
    review_status: ReviewStatus = "needs_review"


class ReviewQueueFile(BaseModel):
    schema_version: int = 1
    generated_at: str = ""
    items: list[ReviewQueueItem] = Field(default_factory=list)


class FetchArtifact(BaseModel):
    url: str
    path: str
    fetched_at: str
    from_cache: bool = False
    status: str = "ok"
    error: str | None = None
