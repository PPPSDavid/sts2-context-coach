namespace Sts2ContextCoach.State;

/// <summary>Optional shop-only context parsed from UI (no network). Used when hovering shop cards.</summary>
public readonly struct ShopEconomyContext
{
    public int? CardPrice { get; init; }
    public bool IsDiscounted { get; init; }
    public int? RemovalServicePrice { get; init; }

    /// <summary>Min listed price among shop card slots (same merchant root); set when <see cref="RowListedPriceSlotCount"/> ≥ 2.</summary>
    public int? RowMinListedCardPrice { get; init; }

    public int RowListedPriceSlotCount { get; init; }

    public bool HasCardPrice => CardPrice is > 0;
}
