namespace Ostrasort;

public enum Relation
{
    SupersetOk,      // later pool contains everything the earlier adds - order correct
    SubsetViolation, // later pool is a strict subset - the earlier mod's items vanish; swap suggested
    Equal,           // same item set - only quantities differ, last-loaded wins
    Partial,         // each side has items the other lacks - no order fixes it; --patch merges it
    NonLoot,         // whole-object replacement, no item-level analysis - last-loaded wins
}

public sealed record PairRelation(
    ModEntry Earlier, ModEntry Later, Relation Rel,
    string[] LostFromEarlier,   // items the earlier claimant stocks that the later one drops
    string[] AddedByLater);

public sealed class Collision
{
    public required string Type { get; init; }
    public required string ObjName { get; init; }
    public required List<ModEntry> Claimants { get; init; }   // in effective load order
    public List<PairRelation> Pairs { get; } = new();
    public List<string> FieldNotes { get; } = new();          // non-loot: which fields each claimant changes vs core
    public bool ObjectMergeable { get; set; }                 // non-loot: a 3-way field merge would preserve more than last-wins
    public bool ResolvedByPatch { get; set; }                 // a fresh OstrasortPatch covers this pool
    public bool FfuMergedAtLoad { get; set; }                 // the FFU loader merges this at load - no action needed
    public bool NothingLost { get; set; }                     // non-loot: identical overrides, or the last mod includes every change - lossless, no action
    public bool AdditiveAtLoad { get; set; }                  // flat-packed simple-container type: the game merges its records key-by-key at load, never whole-object replace
    public string? FriendlyName { get; set; }                 // loot pools: a readable description of the internal id (e.g. "OKLG embassy kiosk inventory")
    public string Key => $"{Type}/{ObjName}";
}

public sealed record Change(string Action, string Entry, string Reason);

public sealed class Analysis
{
    public required List<ModEntry> Registered { get; init; }     // current aLoadOrder, parsed
    public List<ModEntry> UnregisteredLocal { get; } = new();
    public List<ModEntry> UnregisteredWorkshop { get; } = new();
    public List<Collision> Collisions { get; } = new();
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Install-level FFU state, or null on a plain Workshop install. FFU is a
    /// supported framework (ordering rules + merge semantics applied); only
    /// <see cref="FfuContext.AutoloaderActive"/> gates writes. See <see cref="FfuContext"/>.
    /// </summary>
    public FfuContext? Ffu { get; set; }

    /// <summary>loading_order.json's global aIgnorePatterns (sanitized) - see <see cref="LoadOrderFile.IgnorePatterns"/>.</summary>
    public List<string> IgnorePatterns { get; init; } = new();

    public List<string> SuggestedOrder { get; private set; } = new();
    public List<Change> Changes { get; } = new();

    public IEnumerable<ModEntry> AllMods =>
        Registered.Concat(UnregisteredLocal).Concat(UnregisteredWorkshop);

    public bool OrderChanged { get; private set; }

    // ---------------------------------------------------------- collisions ---

    public void FindCollisions()
    {
        // effective order: current registration order, then would-be additions.
        // The Ostrasort-generated patch never counts as a claimant - it IS the
        // resolution of a collision, not a party to one. A disabled entry does
        // not load, so it cannot collide. A duplicated aLoadOrder entry (the
        // game can re-append a subscription) must not collide with itself -
        // only the first occurrence claims.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mods = Registered.Where(m => m.Kind != EntryKind.Core && m.Dir is not null)
            .Concat(UnregisteredLocal).Concat(UnregisteredWorkshop)
            .Where(m => !m.IsPatch && !m.Disabled && seen.Add(IdentityOf(m))).ToList();

        var byKey = new Dictionary<(string, string), List<ModEntry>>();
        foreach (var m in mods)
            foreach (var claim in m.Claims.Keys)
            {
                if (!byKey.TryGetValue(claim, out var list)) byKey[claim] = list = new();
                list.Add(m);
            }

        foreach (var ((type, name), claimants) in byKey.Where(kv => kv.Value.Count > 1).OrderBy(kv => kv.Key))
        {
            var col = new Collision { Type = type, ObjName = name, Claimants = claimants };
            // ALL pairs, not just adjacent ones - with 3+ claimants a partial
            // overlap between non-adjacent pools would otherwise slip through.
            // Order keeps the last adjacent pair last (the display anchor).
            for (var i = 0; i < claimants.Count - 1; i++)
                for (var j = i + 1; j < claimants.Count; j++)
                    col.Pairs.Add(Relate(type, name, claimants[i], claimants[j]));
            Collisions.Add(col);
        }
    }

    internal static PairRelation Relate(string type, string name, ModEntry earlier, ModEntry later)
    {
        var a = earlier.Claims[(type, name)];
        var b = later.Claims[(type, name)];
        if (a is null || b is null)
            return new PairRelation(earlier, later, Relation.NonLoot, [], []);

        // aLoots entries are "Item=weightxcount" - compare at item granularity
        static HashSet<string> Items(string[] loots) =>
            loots.Select(l => l.Split('=')[0]).ToHashSet(StringComparer.Ordinal);

        HashSet<string> ia = Items(a), ib = Items(b);
        var lost = ia.Where(x => !ib.Contains(x)).OrderBy(x => x).ToArray();
        var added = ib.Where(x => !ia.Contains(x)).OrderBy(x => x).ToArray();

        var rel = (lost.Length, added.Length) switch
        {
            (0, 0) => Relation.Equal,
            (0, _) => Relation.SupersetOk,
            (_, 0) => Relation.SubsetViolation,
            _ => Relation.Partial,
        };
        return new PairRelation(earlier, later, rel, lost, added);
    }

    /// <summary>Partial-overlap loot conflicts not (freshly) covered by the generated patch.</summary>
    public bool HasUnresolvedConflicts =>
        Collisions.Any(c => !c.ResolvedByPatch && c.Pairs.Any(p => p.Rel == Relation.Partial));

    // ------------------------------------------------------------- sorting ---

    /// <summary>
    /// Minimal-churn suggestion: start from the current order, then apply only
    /// the moves a rule demands. Everything else keeps its position.
    /// With <paramref name="tidy"/>, additionally group the list for
    /// readability: core, infrastructure, code, shells, additive data,
    /// overriding data, patch - stable within each group (opt-in cosmetics;
    /// position rarely matters for non-colliding mods).
    /// </summary>
    public void BuildSuggestion(bool tidy = false)
    {
        var work = new List<ModEntry>();
        var seenIdentity = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in Registered)
        {
            var identity = IdentityOf(m);
            if (!seenIdentity.Add(identity))
            {
                Changes.Add(new Change("remove", m.Raw, "duplicate entry"));
                Warnings.Add($"duplicate aLoadOrder entry '{m.Raw}' - keeping the first occurrence");
                continue;
            }
            if (m.Kind != EntryKind.Core && m.Dir is null)
            {
                Changes.Add(new Change("remove", m.Raw,
                    m.Kind == EntryKind.Workshop ? "dead path (item unsubscribed?)" : "local folder missing"));
                Warnings.Add($"'{m.Raw}' points at nothing - suggested for removal");
                continue;
            }
            work.Add(m);
        }

        // rule 1: core first (the game enforces CORE_MOD_NAME anyway; just verify)
        var coreIdx = work.FindIndex(m => m.Kind == EntryKind.Core);
        if (coreIdx > 0)
        {
            var core = work[coreIdx];
            work.RemoveAt(coreIdx);
            work.Insert(0, core);
            Changes.Add(new Change("move", "core", "core must load first"));
        }

        // rule 2: infrastructure (BepInEx\patchers) immediately after core
        var insertAt = 1;
        foreach (var infra in work.Where(m => m.Class == ModClass.Infrastructure).ToList())
        {
            var idx = work.IndexOf(infra);
            if (idx > insertAt)
            {
                work.RemoveAt(idx);
                work.Insert(insertAt, infra);
                Changes.Add(new Change("move", infra.Raw, "mod-loader infrastructure pins right after core"));
            }
            if (work.IndexOf(infra) == insertAt) insertAt++;
        }

        // optional tidy grouping (before the hard rules so they still win)
        if (tidy)
        {
            var grouped = work.OrderBy(m => m.Kind == EntryKind.Core ? 0
                : m.Class == ModClass.Infrastructure ? 1
                : m.IsPatch ? 6
                : m.Class switch
                {
                    ModClass.Code => 2,
                    ModClass.Shell => 3,
                    ModClass.DataAdditive => 4,
                    _ => 5,
                }).ToList();   // OrderBy is stable: relative order inside each group is kept
            if (!grouped.SequenceEqual(work))
            {
                work = grouped;
                Changes.Add(new Change("tidy", "(whole list)",
                    "grouped: core, infrastructure, code, shells, additive data, overriding data, patch"));
            }
        }

        // rule 3: clean subset/superset loot collisions load subset-first.
        // A SubsetViolation means the pair is reversed: the earlier claimant is
        // the superset. Move it to just after the later (subset) claimant.
        foreach (var col in Collisions)
            foreach (var pair in col.Pairs.Where(p => p.Rel == Relation.SubsetViolation))
            {
                int from = work.IndexOf(pair.Earlier), to = work.IndexOf(pair.Later);
                if (from < 0 || to < 0 || from > to) continue;
                work.RemoveAt(from);
                work.Insert(work.IndexOf(pair.Later) + 1, pair.Earlier);
                Changes.Add(new Change("move", pair.Earlier.Raw,
                    $"its {col.Key} pool is a superset of {pair.Later.Label}'s and must load after it"));
            }

        // rule 4: register what exists but is invisible to the MODS screen
        // (unless the user chose to leave it unregistered - the ignore list)
        foreach (var m in UnregisteredLocal.Where(m => !m.Ignored))
        {
            work.Add(m);
            Changes.Add(new Change("add", SuggestedRaw(m),
                m.IsPatch
                    ? "the generated Ostrasort Patch exists on disk but is unregistered"
                    : m.Kind == EntryKind.PluginDir
                        ? "data mod under BepInEx\\plugins present but unregistered - the game loads only what aLoadOrder lists"
                        : "local mod folder present but unregistered - invisible to the MODS screen"));
        }
        foreach (var m in UnregisteredWorkshop)
        {
            work.Add(m);
            Changes.Add(new Change("add", m.Dir!,
                "subscribed Workshop item not in aLoadOrder (the game will add it on next launch anyway)"));
        }

        // rule 5 (FFU): FFU-dependent mods form a block AFTER every non-FFU mod:
        // the FFU core tier (Minor Fixes Plus) first, then FFU mods dependency-
        // sorted per their Autoload.Meta.toml, patch-style mods pinned to their
        // targets. Non-FFU mods keep their relative order in front.
        if (work.Any(m => m.IsFfu))
        {
            var lastNonFfu = work.FindLastIndex(m => !m.IsFfu);
            foreach (var m in work.Where(m => m.IsFfu && work.IndexOf(m) < lastNonFfu))
                Changes.Add(new Change("move", SuggestedRaw(m),
                    $"FFU-dependent mods load after all non-FFU mods ({m.FfuSignals.FirstOrDefault() ?? "FFU mod"})"));

            var ffuBlock = OrderFfuBlock(work.Where(m => m.IsFfu).ToList());
            work = work.Where(m => !m.IsFfu).Concat(ffuBlock).ToList();
        }

        // rule 6: the generated patch merges other mods' pools - it must load
        // after everything it merges: last, or (with FFU mods present) closing
        // the non-FFU block, since FFU mods field-merge on top of it at load
        var patch = work.FirstOrDefault(m => m.IsPatch);
        if (patch is not null)
        {
            var oldIdx = work.IndexOf(patch);
            work.RemoveAt(oldIdx);
            var firstFfu = work.FindIndex(m => m.IsFfu);
            if (firstFfu < 0) work.Add(patch);
            else work.Insert(firstFfu, patch);
            if (work.IndexOf(patch) != oldIdx)
                Changes.Add(new Change("move", SuggestedRaw(patch),
                    firstFfu < 0
                        ? "the generated Ostrasort Patch must load last"
                        : "the generated Ostrasort Patch closes the non-FFU block (FFU mods merge on top of it)"));
        }

        SuggestedOrder = work.Select(SuggestedRaw).ToList();
        OrderChanged = !SuggestedOrder.SequenceEqual(Registered.Select(m => m.Raw));
    }

    /// <summary>
    /// Orders the FFU block: FFUCore tier first, then AfterFFU (stable within
    /// each tier), dependents bubbled after their Autoload.Meta.toml
    /// dependencies, and FFU "Patch" mods pinned immediately after their
    /// targets. Emits change notes only when something actually moves.
    /// </summary>
    private List<ModEntry> OrderFfuBlock(List<ModEntry> ffu)
    {
        var ordered = ffu.OrderBy(m => m.FfuGroup == FfuLoadGroup.FFUCore ? 0 : 1).ToList();   // stable

        ModEntry? Resolve(string strName) =>
            ordered.FirstOrDefault(m => string.Equals(m.DisplayName, strName, StringComparison.OrdinalIgnoreCase))
            ?? ordered.FirstOrDefault(m => string.Equals(m.Name, strName, StringComparison.OrdinalIgnoreCase));

        // bubble dependents after their dependencies (bounded - a cycle can't loop forever)
        for (var pass = 0; pass <= ffu.Count; pass++)
        {
            var moved = false;
            foreach (var m in ordered.Where(m => m.Meta is { Dependencies.Count: > 0 }).ToList())
            {
                var deps = m.Meta!.Dependencies.Keys.Select(Resolve).OfType<ModEntry>().Where(d => d != m).ToList();
                if (deps.Count == 0) continue;
                var maxDep = deps.Max(d => ordered.IndexOf(d));
                var idx = ordered.IndexOf(m);
                if (idx >= maxDep) continue;
                ordered.RemoveAt(idx);
                ordered.Insert(maxDep, m);   // maxDep shifted down by the removal - lands right after the dep
                moved = true;
            }
            if (!moved) break;
        }

        // FFU patch mods: immediately after the mod they patch (when it is in this block)
        foreach (var p in ordered.Where(m => m is { IsFfuPatch: true, FfuPatchTarget: not null }).ToList())
        {
            var ti = ordered.IndexOf(p.FfuPatchTarget!);
            if (ti < 0 || ordered.IndexOf(p) == ti + 1) continue;
            ordered.Remove(p);
            ordered.Insert(ordered.IndexOf(p.FfuPatchTarget!) + 1, p);
            Changes.Add(new Change("move", SuggestedRaw(p),
                $"FFU patch mods load immediately after their target ({p.FfuPatchTarget!.DisplayName ?? p.FfuPatchTarget.Name}) - and should be removed after one game launch"));
        }

        if (!ordered.SequenceEqual(ffu))
            Changes.Add(new Change("move", "(FFU block)",
                "FFU block ordered: Minor Fixes Plus tier first, then dependency order per Autoload.Meta.toml"));
        return ordered;
    }

    private static string IdentityOf(ModEntry m) =>
        m.Kind == EntryKind.Local ? $"local:{m.Name}"
        : m.Raw.Length > 0 ? m.Raw
        : m.Dir ?? m.Name;   // unregistered entries all share Raw "" - identify by folder

    /// <summary>
    /// The exact aLoadOrder string this mod registers as: local mods keep their
    /// |edit marker, Workshop/plugins-dir mods register by absolute path, the
    /// generated patch registers plain. Used by the suggestion and by the
    /// single-mod Register action.
    /// </summary>
    public static string SuggestedRaw(ModEntry m) =>
        m.Registered ? m.Raw
        : m.Kind is EntryKind.Workshop or EntryKind.PluginDir ? m.Dir!   // absolute-path registrations
        : m.IsPatch ? m.Name                 // the patch registers plain, nothing to upload
        : $"{m.Name}|edit";

    // ------------------------------------------------------ manual ordering ---

    /// <summary>
    /// Validates a manually arranged order (GUI drag-and-drop) against the
    /// rules. "BLOCK:" prefixed items must stop the write; the rest are
    /// warnings the user may consciously accept.
    /// </summary>
    public static List<string> ValidateOrder(List<ModEntry> order)
    {
        var issues = new List<string>();
        if (order.Count == 0 || order[0].Kind != EntryKind.Core)
            issues.Add("BLOCK: core must be first - the game loads it before everything.");

        var firstNonInfra = order.FindIndex(m => m.Kind != EntryKind.Core && m.Class != ModClass.Infrastructure);
        foreach (var infra in order.Where(m => m.Class == ModClass.Infrastructure))
            if (firstNonInfra >= 0 && order.IndexOf(infra) > firstNonInfra)
                issues.Add($"{infra.DisplayName ?? infra.Name}: mod-loader infrastructure should sit right after core.");

        // recompute loot pool relations for the NEW order (all pairs, like FindCollisions)
        var byKey = new Dictionary<(string, string), List<ModEntry>>();
        foreach (var m in order.Where(m => !m.IsPatch && !m.Disabled))
            foreach (var claim in m.Claims.Keys)
            {
                if (!byKey.TryGetValue(claim, out var list)) byKey[claim] = list = new();
                list.Add(m);
            }
        foreach (var ((type, name), claimants) in byKey.Where(kv => kv.Value.Count > 1))
            for (var i = 0; i < claimants.Count - 1; i++)
                for (var j = i + 1; j < claimants.Count; j++)
                {
                    var rel = Relate(type, name, claimants[i], claimants[j]);
                    if (rel.Rel == Relation.SubsetViolation)
                        issues.Add($"{type}/{name}: {claimants[j].DisplayName ?? claimants[j].Name} would drop " +
                                   $"{rel.LostFromEarlier.Length} item(s) that {claimants[i].DisplayName ?? claimants[i].Name} stocks " +
                                   "- the superset should load last.");
                }

        // FFU rules: the FFU block sits after every non-FFU mod, core tier first,
        // dependencies before dependents
        var firstFfu = order.FindIndex(m => m.IsFfu);
        if (firstFfu >= 0)
        {
            foreach (var m in order.Skip(firstFfu + 1).Where(m => !m.IsFfu && m.Kind != EntryKind.Core && !m.IsPatch && !m.Disabled))
                issues.Add($"{m.DisplayName ?? m.Name}: non-FFU mods should load before the FFU block " +
                           "(FFU-dependent mods come after all non-FFU mods).");
            var firstAfter = order.FindIndex(m => m.IsFfu && m.FfuGroup != FfuLoadGroup.FFUCore);
            foreach (var m in order.Where(m => m.IsFfu && m.FfuGroup == FfuLoadGroup.FFUCore))
                if (firstAfter >= 0 && order.IndexOf(m) > firstAfter)
                    issues.Add($"{m.DisplayName ?? m.Name}: the FFU core tier (Minor Fixes Plus) must be the first FFU mod loaded.");
        }
        foreach (var m in order.Where(m => m.Meta is { Dependencies.Count: > 0 }))
            foreach (var dep in m.Meta!.Dependencies.Keys)
            {
                var t = order.FirstOrDefault(o => string.Equals(o.DisplayName, dep, StringComparison.OrdinalIgnoreCase)
                                               || string.Equals(o.Name, dep, StringComparison.OrdinalIgnoreCase));
                if (t is not null && t != m && order.IndexOf(t) > order.IndexOf(m))
                    issues.Add($"{m.DisplayName ?? m.Name}: loads before its declared dependency '{dep}' (Autoload.Meta.toml).");
            }

        var patchIdx = order.FindIndex(m => m.IsPatch);
        if (patchIdx >= 0 && order.Skip(patchIdx + 1).Any(m => !m.IsFfu))
            issues.Add(firstFfu >= 0
                ? "the generated Ostrasort Patch must close the non-FFU block (only FFU mods after it) or the pools it merges get overridden again."
                : "the generated Ostrasort Patch must load last or the pools it merges get overridden again.");

        return issues;
    }
}
