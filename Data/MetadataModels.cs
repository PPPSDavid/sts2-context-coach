using System.Text.Json.Serialization;

namespace Sts2ContextCoach.Data;

/// <summary>JSON DTOs for local metadata. Designed for future offline wiki/patch ingestion.</summary>
public sealed class CardsFileDto
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("cards")]
    public List<CardMetadataDto> Cards { get; set; } = [];
}

public sealed class RelicsFileDto
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("relics")]
    public List<RelicMetadataDto> Relics { get; set; } = [];
}

public sealed class CardMetadataDto
{
    [JsonPropertyName("internal_name")]
    public string InternalName { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("character")]
    public string? Character { get; set; }

    /// <summary>Energy cost; null if unknown or X-cost.</summary>
    [JsonPropertyName("cost")]
    public int? Cost { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("synergy_tags")]
    public List<string> SynergyTags { get; set; } = [];

    [JsonPropertyName("role_tags")]
    public List<string> RoleTags { get; set; } = [];

    /// <summary>e.g. low, medium, high — future tools may emit consistent enums.</summary>
    [JsonPropertyName("impact_level")]
    public string? ImpactLevel { get; set; }

    /// <summary>Inferred upgraded card text (plus version) for tooling and scoring.</summary>
    [JsonPropertyName("upgraded_description")]
    public string? UpgradedDescription { get; set; }

    /// <summary>One-line summary of what + does (LLM or manual).</summary>
    [JsonPropertyName("upgrade_summary")]
    public string? UpgradeSummary { get; set; }

    /// <summary>Negative means cheaper after upgrade (e.g. -1 if cost 1→0).</summary>
    [JsonPropertyName("upgrade_cost_delta")]
    public int? UpgradeCostDelta { get; set; }

    [JsonPropertyName("upgrade_block_delta")]
    public int? UpgradeBlockDelta { get; set; }

    [JsonPropertyName("upgrade_draw_delta")]
    public int? UpgradeDrawDelta { get; set; }

    [JsonPropertyName("upgrade_damage_delta")]
    public int? UpgradeDamageDelta { get; set; }

    [JsonPropertyName("upgrade_removes_exhaust")]
    public bool? UpgradeRemovesExhaust { get; set; }

    /// <summary>True when upgrade is a large pivot (e.g. to 0-cost, removes exhaust, doubles a key number).</summary>
    [JsonPropertyName("upgrade_major")]
    public bool? UpgradeMajor { get; set; }

    /// <summary>LLM-assessed upgrade quality tier: D/C/B/A/S.</summary>
    [JsonPropertyName("upgrade_tier")]
    public string? UpgradeTier { get; set; }

    /// <summary>Expected value of future enchantment outcomes for this card: D/C/B/A/S (probability-discounted).</summary>
    [JsonPropertyName("enchantment_potential_tier")]
    public string? EnchantmentPotentialTier { get; set; }

    /// <summary>Per-kind realized enchantment value tiers (keys: attack/block/draw/energy/remove_exhaust, values: D/C/B/A/S).</summary>
    [JsonPropertyName("enchantment_tier_by_kind")]
    public Dictionary<string, string> EnchantmentTierByKind { get; set; } = [];
}

public sealed class RelicMetadataDto
{
    [JsonPropertyName("internal_name")]
    public string InternalName { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("synergy_tags")]
    public List<string> SynergyTags { get; set; } = [];

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}
