using System.Text.RegularExpressions;

namespace Ostrasort;

/// <summary>
/// Turns an internal loot-pool id (e.g. <c>CNDOLKioskEmbassyOKLG</c>) into a
/// player-readable description. The strong signal is the reverse index: the
/// friendly name of the core object(s) that reference the pool via
/// <c>strLoot</c> / <c>strCondLoot</c> (a kiosk item's <c>strNameFriendly</c>
/// like "Embassy Services: K-Leg, 1036-Ganymed"). When no core object names the
/// pool, a light pattern decode of the id itself is offered for shop-like pools
/// ("OKLG embassy kiosk inventory"). Built from the cached core index, so the
/// (expensive) core parse only happens on a game update.
/// </summary>
public sealed class LootPoolNames
{
    private readonly Dictionary<string, List<string>> _byPool = new(StringComparer.Ordinal);

    public LootPoolNames(IEnumerable<CoreIndexCache.LootRef> refs)
    {
        foreach (var r in refs)
        {
            if (string.IsNullOrEmpty(r.Pool) || string.IsNullOrEmpty(r.Friendly)) continue;
            if (!_byPool.TryGetValue(r.Pool, out var list)) _byPool[r.Pool] = list = new();
            if (!list.Contains(r.Friendly, StringComparer.Ordinal)) list.Add(r.Friendly);
        }
    }

    public static LootPoolNames Empty { get; } = new(Array.Empty<CoreIndexCache.LootRef>());

    /// <summary>
    /// A friendly one-line description for a loot-pool id, or null if none is
    /// derivable (leave the raw id). Prefers a referencing object's friendly
    /// name; falls back to a pattern decode for recognisably shop-like ids.
    /// </summary>
    public string? Describe(string poolName)
    {
        if (_byPool.TryGetValue(poolName, out var names) && names.Count > 0)
            return names.Count == 1 ? names[0] : $"{names[0]} (+{names.Count - 1} more)";
        return Pattern(poolName);
    }

    // shop-like keywords that mark a pool as a store/kiosk inventory
    private static readonly string[] ShopKeywords =
        { "Kiosk", "Vending", "Vendor", "Shop", "Store", "Market", "Merchant" };

    // known station / faction codes seen in ids (avoids matching prefix runs
    // like "CNDOL" as a code); first hit wins
    private static readonly string[] FactionCodes =
        { "OKLG", "VNCA", "VENC", "BCER", "BCRS", "CCRE", "VORB", "JATL" };

    // recognisable inventory categories -> display word (longer keys first so
    // "Medical" wins over "Med")
    private static readonly (string Key, string Word)[] Categories =
    {
        ("Furnishings", "furnishings"), ("Medical", "medical"), ("Housing", "housing"),
        ("Embassy", "embassy"), ("Cargo", "cargo"), ("Supply", "supply"), ("Food", "food"),
        ("Scrap", "scrap"), ("Broker", "broker"), ("Faction", "faction"), ("Govt", "government"),
        ("Ship", "ship"), ("Med", "medical"),
    };

    /// <summary>Best-effort decode of a shop-like id into "&lt;code&gt; &lt;category&gt; &lt;keyword&gt; inventory".</summary>
    internal static string? Pattern(string id)
    {
        var kw = ShopKeywords.FirstOrDefault(k => id.Contains(k, StringComparison.Ordinal));
        if (kw is null) return null;

        var parts = new List<string>();
        var code = FactionCodes.FirstOrDefault(c => id.Contains(c, StringComparison.Ordinal));
        if (code is not null) parts.Add(code);
        var cat = Categories.FirstOrDefault(c => id.Contains(c.Key, StringComparison.Ordinal)).Word;
        if (cat is not null) parts.Add(cat);
        parts.Add(kw.ToLowerInvariant());
        parts.Add("inventory");
        return string.Join(" ", parts);
    }
}
