namespace Sts2ContextCoach.State;

/// <summary>Optional shop-only context parsed from UI (no network). Used when hovering shop cards.</summary>
public readonly struct ShopEconomyContext
{
    public int? CardPrice { get; init; }
    public bool IsDiscounted { get; init; }
    public int? RemovalServicePrice { get; init; }

    public bool HasCardPrice => CardPrice is > 0;
}
