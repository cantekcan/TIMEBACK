using Timeback.Domain.Common;

namespace Timeback.Domain.Assets;

public enum AssetClass
{
    Commodity = 1,
    EquityIndex = 2,
    Crypto = 3,
    Currency = 4
}

/// <summary>
/// A tradable instrument the player can allocate capital to. This is a small reference aggregate,
/// seeded once. New asset types are added by inserting a row - no code change in the game engine.
/// </summary>
public sealed class Asset : AggregateRoot
{
    public string Symbol { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
    public AssetClass Class { get; private set; }
    public string QuoteCurrency { get; private set; } = "TRY";
    public bool IsActive { get; private set; }

    private Asset() { } // EF

    public Asset(string symbol, string displayName, AssetClass @class, string quoteCurrency)
    {
        if (string.IsNullOrWhiteSpace(symbol)) throw new DomainException("Asset symbol is required.");
        if (string.IsNullOrWhiteSpace(displayName)) throw new DomainException("Asset display name is required.");
        Symbol = symbol.ToUpperInvariant();
        DisplayName = displayName;
        Class = @class;
        QuoteCurrency = string.IsNullOrWhiteSpace(quoteCurrency) ? "TRY" : quoteCurrency.ToUpperInvariant();
        IsActive = true;
    }

    public void Deactivate() => IsActive = false;

    /// <summary>Reconciles this asset's catalog-owned fields (display name, class) and reactivates it -
    /// used when re-seeding finds a catalog entry that was previously removed and has now come back,
    /// or whose display name changed (e.g. a labelling fix).</summary>
    public void SyncFromCatalog(string displayName, AssetClass @class)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new DomainException("Asset display name is required.");
        DisplayName = displayName;
        Class = @class;
        IsActive = true;
    }
}
