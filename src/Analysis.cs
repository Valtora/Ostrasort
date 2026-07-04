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
    public bool ResolvedByPatch { get; set; }                 // a fresh OstrasortPatch covers this pool
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
        // resolution of a collision, not a party to one.
        var mods = Registered.Where(m => m.Kind != EntryKind.Core && m.Dir is not null)
            .Concat(UnregisteredLocal).Concat(UnregisteredWorkshop)
            .Where(m => !m.IsPatch).ToList();

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
            for (var i = 0; i < claimants.Count - 1; i++)
                col.Pairs.Add(Relate(type, name, claimants[i], claimants[i + 1]));
            Collisions.Add(col);
        }
    }

    private static PairRelation Relate(string type, string name, ModEntry earlier, ModEntry later)
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
    /// </summary>
    public void BuildSuggestion()
    {
        var work = new List<ModEntry>();
        var seenIdentity = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in Registered)
        {
            var identity = m.Kind == EntryKind.Local ? $"local:{m.Name}" : m.Raw;
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
        foreach (var m in UnregisteredLocal)
        {
            work.Add(m);
            Changes.Add(new Change("add", SuggestedRaw(m),
                m.IsPatch
                    ? "the generated Ostrasort Patch exists on disk but is unregistered"
                    : "local mod folder present but unregistered - invisible to the MODS screen"));
        }
        foreach (var m in UnregisteredWorkshop)
        {
            work.Add(m);
            Changes.Add(new Change("add", m.Dir!,
                "subscribed Workshop item not in aLoadOrder (the game will add it on next launch anyway)"));
        }

        // rule 5: the generated patch merges other mods' pools - it must load
        // after everything it merges, i.e. last
        var patchIdx = work.FindIndex(m => m.IsPatch);
        if (patchIdx >= 0 && patchIdx != work.Count - 1)
        {
            var patch = work[patchIdx];
            work.RemoveAt(patchIdx);
            work.Add(patch);
            Changes.Add(new Change("move", SuggestedRaw(patch), "the generated Ostrasort Patch must load last"));
        }

        SuggestedOrder = work.Select(SuggestedRaw).ToList();
        OrderChanged = !SuggestedOrder.SequenceEqual(Registered.Select(m => m.Raw));
    }

    private static string SuggestedRaw(ModEntry m) =>
        m.Registered ? m.Raw
        : m.Kind == EntryKind.Workshop ? m.Dir!
        : m.IsPatch ? m.Name                 // the patch registers plain, nothing to upload
        : $"{m.Name}|edit";
}
