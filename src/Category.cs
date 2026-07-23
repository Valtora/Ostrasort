namespace Ostrasort;

/// <summary>
/// The game system a mod predominantly affects, inferred from the data types it
/// ships and the object namespaces it touches. Descriptive - it is shown in the
/// report and the mod detail so "category" is a visible concept - while the
/// ordering weight lives in <see cref="LoadTier"/>. Most mods are
/// <see cref="General"/> and keep their place (minimal churn). Only
/// <see cref="CharacterGeneration"/> carries a non-neutral tier by default: it
/// owns the new-game / start flow (the deterministic-vs-dice-roll choices a
/// player makes before the game begins) and must have the final say, so it loads
/// late. See <see cref="CategoryAnalysis"/> for detection.
/// </summary>
public enum ModCategory
{
    General,                 // no single strong system signal - neutral
    CharacterGeneration,     // new-game / start flow (lifeevents + the CGEnc namespace) - loads late
    ShipsAndStations,        // ships, shipspecs, rooms, star systems
    ItemsAndEconomy,         // items, blueprints, installables, shop/kiosk loot, market, jobs
    CharactersAndBehaviour,  // condowners, personspecs, AI training, careers, homeworlds, wounds
    InteractionsAndRules,    // interactions, conditions, triggers, tokens
    Narrative,               // plots, plot beats, headlines, tickers
    Cosmetic,                // colours, lights, strings, music, tips, images
}

/// <summary>
/// A mod's load-order priority band. Later tiers load later, so their overrides
/// win when two mods touch the same object. Enforcement is conservative:
/// automatic detection only ever assigns <see cref="Late"/> (to the
/// character-generation category); <see cref="Early"/> is a manual-only choice.
/// Everything else stays <see cref="Normal"/>, so the suggestion keeps its
/// minimal-churn character and only the deliberately-prioritised mods move.
/// </summary>
public enum LoadTier { Early, Normal, Late }

/// <summary>
/// Buckets each mod into a <see cref="ModCategory"/> from its data, and resolves
/// its effective <see cref="LoadTier"/> (auto-detected, then overridden by any
/// per-install manual pin). Runs after the scanner has populated every mod's
/// <see cref="ModEntry.Claims"/>, alongside <see cref="FfuAnalysis.Classify"/>.
/// </summary>
public static class CategoryAnalysis
{
    /// <summary>Human label for a category, matching the game's own wording where it has one.</summary>
    public static string Label(ModCategory c) => c switch
    {
        ModCategory.CharacterGeneration => "new game / start",
        ModCategory.ShipsAndStations => "ships & stations",
        ModCategory.ItemsAndEconomy => "items & economy",
        ModCategory.CharactersAndBehaviour => "characters & behaviour",
        ModCategory.InteractionsAndRules => "interactions & rules",
        ModCategory.Narrative => "narrative",
        ModCategory.Cosmetic => "cosmetic",
        _ => "general",
    };

    /// <summary>Human label for a load tier (the ordering effect, not the system).</summary>
    public static string Label(LoadTier t) => t switch
    {
        LoadTier.Late => "late (final say)",
        LoadTier.Early => "early (yields to others)",
        _ => "normal",
    };

    /// <summary>Parses a tier word from the CLI / a stored override; null when unrecognised.</summary>
    public static LoadTier? ParseTier(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "late" => LoadTier.Late,
        "early" => LoadTier.Early,
        "normal" => LoadTier.Normal,
        _ => null,
    };

    /// <summary>The tier a category loads at with no manual override.</summary>
    public static LoadTier DefaultTier(ModCategory c) =>
        c == ModCategory.CharacterGeneration ? LoadTier.Late : LoadTier.Normal;

    // data type (folder name, lower-cased) -> the game system it belongs to.
    // Types with no strong ordering signal (cosmetic, world dressing) still map,
    // so a mod's dominant category is informative even when its tier is Normal.
    private static readonly IReadOnlyDictionary<string, ModCategory> ByType =
        new Dictionary<string, ModCategory>(StringComparer.OrdinalIgnoreCase)
        {
            // new-game / start flow. lifeevents is 100% character-generation
            // content in the base game (every object is a CGEnc* encounter).
            ["lifeevents"] = ModCategory.CharacterGeneration,

            ["ships"] = ModCategory.ShipsAndStations,
            ["shipspecs"] = ModCategory.ShipsAndStations,
            ["rooms"] = ModCategory.ShipsAndStations,
            ["star_systems"] = ModCategory.ShipsAndStations,
            ["transit"] = ModCategory.ShipsAndStations,
            ["powerinfos"] = ModCategory.ShipsAndStations,

            ["items"] = ModCategory.ItemsAndEconomy,
            ["jobitems"] = ModCategory.ItemsAndEconomy,
            ["blueprints"] = ModCategory.ItemsAndEconomy,
            ["installables"] = ModCategory.ItemsAndEconomy,
            ["slots"] = ModCategory.ItemsAndEconomy,
            ["slot_effects"] = ModCategory.ItemsAndEconomy,
            ["market"] = ModCategory.ItemsAndEconomy,
            ["ledgerdefs"] = ModCategory.ItemsAndEconomy,
            ["loot"] = ModCategory.ItemsAndEconomy,
            ["attackmodes"] = ModCategory.ItemsAndEconomy,
            ["jobs"] = ModCategory.ItemsAndEconomy,

            ["condowners"] = ModCategory.CharactersAndBehaviour,
            ["personspecs"] = ModCategory.CharactersAndBehaviour,
            ["ai_training"] = ModCategory.CharactersAndBehaviour,
            ["careers"] = ModCategory.CharactersAndBehaviour,
            ["homeworlds"] = ModCategory.CharactersAndBehaviour,
            ["crewskins"] = ModCategory.CharactersAndBehaviour,
            ["wounds"] = ModCategory.CharactersAndBehaviour,
            ["gasrespires"] = ModCategory.CharactersAndBehaviour,
            ["chargeprofiles"] = ModCategory.CharactersAndBehaviour,
            ["traitscores"] = ModCategory.CharactersAndBehaviour,

            ["interactions"] = ModCategory.InteractionsAndRules,
            ["interaction_overrides"] = ModCategory.InteractionsAndRules,
            ["conditions"] = ModCategory.InteractionsAndRules,
            ["conditions_simple"] = ModCategory.InteractionsAndRules,
            ["condrules"] = ModCategory.InteractionsAndRules,
            ["condtrigs"] = ModCategory.InteractionsAndRules,
            ["tokens"] = ModCategory.InteractionsAndRules,
            ["context"] = ModCategory.InteractionsAndRules,
            ["zone_triggers"] = ModCategory.InteractionsAndRules,

            ["plots"] = ModCategory.Narrative,
            ["plot_beats"] = ModCategory.Narrative,
            ["plot_beat_overrides"] = ModCategory.Narrative,
            ["plot_manager"] = ModCategory.Narrative,
            ["headlines"] = ModCategory.Narrative,
            ["tickers"] = ModCategory.Narrative,
            ["lifeevents_meta"] = ModCategory.Narrative,

            ["colors"] = ModCategory.Cosmetic,
            ["lights"] = ModCategory.Cosmetic,
            ["parallax"] = ModCategory.Cosmetic,
            ["music"] = ModCategory.Cosmetic,
            ["music_stations"] = ModCategory.Cosmetic,
            ["strings"] = ModCategory.Cosmetic,
            ["tips"] = ModCategory.Cosmetic,
            ["manpages"] = ModCategory.Cosmetic,
            ["guipropmaps"] = ModCategory.Cosmetic,
            ["pda_apps"] = ModCategory.Cosmetic,
            ["explosions"] = ModCategory.Cosmetic,
            ["audioemitters"] = ModCategory.Cosmetic,
        };

    /// <summary>
    /// True when a claim belongs to the character-generation namespace: any
    /// <c>lifeevents</c> object, or any object whose name carries the game's
    /// <c>CGEnc</c> (Character Generation Encounter) token - including
    /// author-namespaced clones like <c>HLVpCGEnc*</c>. This is what makes a mod
    /// that curates the starting-ship / intro flow (e.g. Vanilla Plus Character
    /// Generation) sort last, so its deterministic choices win over the vanilla
    /// dice roll and over ship mods that merely add a starter ship to the pool.
    /// </summary>
    public static bool IsCharacterGenClaim(string type, string name) =>
        type.Equals("lifeevents", StringComparison.OrdinalIgnoreCase)
        || name.Contains("CGEnc", StringComparison.Ordinal);

    /// <summary>
    /// Detects a mod's descriptive category and the evidence for it, from the set
    /// of objects it claims. Character generation wins whenever any CG claim is
    /// present (it is order-sensitive and specific); otherwise the dominant
    /// data-type system wins, ties broken toward the more distinctive category.
    /// </summary>
    public static (ModCategory Category, List<string> Signals) Detect(ModEntry m)
    {
        var signals = new List<string>();
        if (m.Claims.Count == 0)
            return (ModCategory.General, signals);

        var cgClaims = m.Claims.Keys.Where(k => IsCharacterGenClaim(k.Type, k.Name)).ToList();
        if (cgClaims.Count > 0)
        {
            var lifeevents = m.Claims.Keys.Count(k => k.Type.Equals("lifeevents", StringComparison.OrdinalIgnoreCase));
            signals.Add(lifeevents > 0
                ? $"ships {lifeevents} life-event object(s) - the new-game / start flow"
                : $"touches {cgClaims.Count} character-generation object(s) (CGEnc* namespace)");
            return (ModCategory.CharacterGeneration, signals);
        }

        // no CG signal: dominant system by claim count, tie broken by enum order
        // (skipping General, which is the fallback, not a positive match)
        var counts = new Dictionary<ModCategory, int>();
        foreach (var (type, _) in m.Claims.Keys)
            if (ByType.TryGetValue(type, out var cat))
                counts[cat] = counts.GetValueOrDefault(cat) + 1;
        if (counts.Count == 0)
            return (ModCategory.General, signals);

        var best = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => (int)kv.Key).First().Key;
        signals.Add($"mostly {Label(best)} content ({counts[best]} object(s))");
        return (best, signals);
    }

    /// <summary>
    /// Assigns every non-core mod its category and effective load tier. The tier
    /// is the category's default unless the user pinned it in
    /// <paramref name="overrides"/>, which always wins (Late / Normal / Early).
    /// </summary>
    public static void Classify(GameEnv env, Analysis a, CategoryOverrideList? overrides = null)
    {
        foreach (var m in a.AllMods.Where(m => m.Kind != EntryKind.Core && m.Dir is not null))
        {
            var (cat, signals) = Detect(m);
            m.Category = cat;
            m.CategorySignals.AddRange(signals);
            m.Tier = DefaultTier(cat);

            if (overrides is not null && overrides.TryGet(env, m, out var forced))
            {
                m.Tier = forced;
                m.CategoryManual = true;
                m.CategorySignals.Insert(0, $"you pinned its load priority to {Label(forced)}");
            }
        }
    }
}
