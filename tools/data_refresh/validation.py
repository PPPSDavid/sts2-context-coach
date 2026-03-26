"""Validate generated JSON and fetch artifacts."""

from __future__ import annotations

from typing import Any

from models import PatchNoteEntry
from tag_vocabulary import (
    SUPPORTED_CARD_ROLE_TAGS,
    SUPPORTED_CARD_SYNERGY_TAGS,
    SUPPORTED_CARD_TAGS,
    SUPPORTED_IMPACT_LEVELS,
    SUPPORTED_RELIC_SYNERGY_TAGS,
    SUPPORTED_RELIC_TAGS,
)


class ValidationIssue:
    def __init__(self, level: str, message: str, detail: str | None = None) -> None:
        self.level = level
        self.message = message
        self.detail = detail


def validate_cards_document(doc: dict[str, Any]) -> list[ValidationIssue]:
    issues: list[ValidationIssue] = []
    cards = doc.get("cards")
    if not isinstance(cards, list):
        return [ValidationIssue("error", "cards must be a list")]
    seen: set[str] = set()
    for i, c in enumerate(cards):
        if not isinstance(c, dict):
            issues.append(ValidationIssue("error", f"card[{i}] not an object"))
            continue
        iid = c.get("internal_name")
        if not isinstance(iid, str) or not iid.strip():
            issues.append(ValidationIssue("error", f"card[{i}] missing internal_name"))
        else:
            if iid in seen:
                issues.append(ValidationIssue("error", f"duplicate internal_name: {iid}"))
            seen.add(iid)
        dn = c.get("display_name")
        if dn is not None and not _valid_display_name(dn):
            issues.append(ValidationIssue("error", f"{iid}: display_name must be string"))
        cost = c.get("cost")
        if cost is not None and not isinstance(cost, int):
            issues.append(ValidationIssue("warning", f"{iid}: cost should be int"))
        meta = c.get("_meta")
        if meta is not None and not isinstance(meta, dict):
            issues.append(ValidationIssue("error", f"{iid}: _meta must be object"))
        _validate_tag_list(issues, iid, "tags", c.get("tags"), set(SUPPORTED_CARD_TAGS))
        _validate_tag_list(issues, iid, "synergy_tags", c.get("synergy_tags"), set(SUPPORTED_CARD_SYNERGY_TAGS))
        _validate_tag_list(issues, iid, "role_tags", c.get("role_tags"), set(SUPPORTED_CARD_ROLE_TAGS))
        il = c.get("impact_level")
        if il is not None and str(il).lower() not in set(SUPPORTED_IMPACT_LEVELS):
            issues.append(ValidationIssue("warning", f"{iid}: unsupported impact_level '{il}'"))
    return issues


def validate_relics_document(doc: dict[str, Any]) -> list[ValidationIssue]:
    issues: list[ValidationIssue] = []
    relics = doc.get("relics")
    if not isinstance(relics, list):
        return [ValidationIssue("error", "relics must be a list")]
    seen: set[str] = set()
    for i, r in enumerate(relics):
        if not isinstance(r, dict):
            issues.append(ValidationIssue("error", f"relic[{i}] not an object"))
            continue
        iid = r.get("internal_name")
        if not isinstance(iid, str) or not iid.strip():
            issues.append(ValidationIssue("error", f"relic[{i}] missing internal_name"))
        else:
            if iid in seen:
                issues.append(ValidationIssue("error", f"duplicate internal_name: {iid}"))
            seen.add(iid)
        dn = r.get("display_name")
        if dn is not None and not _valid_display_name(dn):
            issues.append(ValidationIssue("error", f"{iid}: display_name invalid"))
        _validate_tag_list(issues, iid, "tags", r.get("tags"), set(SUPPORTED_RELIC_TAGS))
        _validate_tag_list(issues, iid, "synergy_tags", r.get("synergy_tags"), set(SUPPORTED_RELIC_SYNERGY_TAGS))
    return issues


def validate_patch_notes(doc: dict[str, Any]) -> list[ValidationIssue]:
    issues: list[ValidationIssue] = []
    patches = doc.get("patches")
    if patches is None:
        return [ValidationIssue("warning", "no patches key")]
    if not isinstance(patches, list):
        return [ValidationIssue("error", "patches must be a list")]
    for p in patches:
        try:
            PatchNoteEntry.model_validate(p)
        except Exception as e:
            issues.append(ValidationIssue("error", "patch entry invalid", str(e)))
    return issues


def _valid_display_name(dn: Any) -> bool:
    return isinstance(dn, str)


def _validate_tag_list(
    issues: list[ValidationIssue],
    internal_name: str | None,
    field: str,
    value: Any,
    allowed: set[str],
) -> None:
    if value is None:
        return
    if not isinstance(value, list):
        issues.append(ValidationIssue("warning", f"{internal_name}: {field} should be list"))
        return
    for t in value:
        if not isinstance(t, str):
            issues.append(ValidationIssue("warning", f"{internal_name}: {field} contains non-string tag"))
            continue
        if t.lower() not in allowed:
            issues.append(ValidationIssue("warning", f"{internal_name}: unsupported {field} tag '{t}'"))
