using Timeback.Domain.Assets;

namespace Timeback.Infrastructure.MarketData;

/// <summary>How one game asset maps to an external provider symbol and how its quote is normalised to TRY.</summary>
public sealed record AssetSpec(
    string Symbol,
    string DisplayName,
    AssetClass Class,
    string ProviderSymbol,
    QuoteKind Quote);

public enum QuoteKind
{
    /// <summary>Provider close is already in TRY (e.g. XU100.IS). Used as-is.</summary>
    AlreadyTry,
    /// <summary>Provider close is USD; TRY value = close * USD/TRY.</summary>
    UsdConvertToTry,
    /// <summary>Provider close is USD per troy ounce; TRY value = close / 31.1034768 * USD/TRY (gram gold).</summary>
    UsdPerOunceToGramTry,
    /// <summary>The USD/TRY rate itself - holding USD, value tracks the rate.</summary>
    FxRate
}

/// <summary>
/// The seeded asset catalog. Adding an asset = adding a row here (plus the provider being able to
/// serve its symbol). The game engine has no per-asset code.
/// </summary>
public static class AssetCatalog
{
    public const decimal TroyOunceGrams = 31.1034768m;

    // USD/TRY ("TRY=X") is deliberately not a row here: it is not a player-selectable asset, only the
    // FX backbone MarketDataIngestionService uses to convert every USD-quoted asset to TRY (see its
    // direct fetch of "TRY=X" there, independent of this catalog).
    public static readonly IReadOnlyList<AssetSpec> All =
    [
        new("GOLD",    "Altın",     AssetClass.Commodity,   "GC=F",     QuoteKind.UsdPerOunceToGramTry),
        new("BIST100", "BIST 100",  AssetClass.EquityIndex, "XU100.IS", QuoteKind.AlreadyTry),
        new("BTC",     "Bitcoin",   AssetClass.Crypto,      "BTC-USD",  QuoteKind.UsdConvertToTry),
        new("SP500",   "S&P 500",   AssetClass.EquityIndex, "_GSPC",    QuoteKind.UsdConvertToTry),
    ];

    public static decimal ToTry(QuoteKind kind, decimal providerClose, decimal usdTry) => kind switch
    {
        QuoteKind.AlreadyTry => providerClose,
        QuoteKind.FxRate => providerClose,
        QuoteKind.UsdConvertToTry => providerClose * usdTry,
        QuoteKind.UsdPerOunceToGramTry => providerClose / TroyOunceGrams * usdTry,
        _ => providerClose
    };
}
