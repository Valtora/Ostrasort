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
    public bool ResolvedByOrder { get; set; }                 // a final-say (late-tier) mod curates this object and loads last, so its version wins by design - do NOT patch-merge (that would re-add what it removed)
    /// <summary>
    /// A subset violation no load order can fix: the later claimant holds a late
    /// (final say) slot it did NOT earn over this object, so the sort refuses to
    /// move the superset past it (see <see cref="Analysis.OrderLockedPair"/>).
    /// Nothing is decided by design here, so unlike <see cref="ResolvedByOrder"/>
    /// this is a real loss - the patch is what resolves it.
    /// </summary>
    public bool OrderLocked { get; set; }
    /// <summary>
    /// Subset-violation pairs no reorder can fix (see
    /// <see cref="Analysis.ComputeReorderLocks"/>). The sort leaves them alone and
    /// the report never promises a reorder for them.
    /// </summary>
    public HashSet<PairRelation> ReorderLockedPairs { get; } = new(ReferenceEqualityComparer.Instance);
    /// <summary>
    /// Two or more claimants each have final say over this object (both curate
    /// it), so load order decides between curations rather than resolving one.
    /// A real conflict for the player: never reordered by item count, and never
    /// union-merged, since a union would undo both mods' curation.
    /// </summary>
    public bool CurationClash { get; set; }
    public bool AdditiveAtLoad { get; set; }                  // flat-packed simple-container type: the game merges its records key-by-key at load, never whole-object replace
    public string? FriendlyName { get; set; }                 // loot pools: a readable description of the internal id (e.g. "OKLG embassy kiosk inventory")
    public string Key => $"{Type}/{ObjName}";

    /// <summary>
    /// True when this claimant EDITS the object with FFU precision array commands
    /// (<c>--ADD--</c>, <c>--DEL--</c>, …) instead of shipping a replacement.
    /// </summary>
    public bool IsCommandEdit(ModEntry m) => m.FfuArrayEditClaims.Contains((Type, ObjName));

    /// <summary>
    /// Claimants that ship a plain whole-object version - everyone except the
    /// command editors. Those are what a load order actually decides between, and
    /// what the patch merges. Excluding a command editor is safe in both
    /// directions: using the FFU array API makes a mod FFU-dependent
    /// (<see cref="ModEntry.UsesElasticApi"/> seeds <see cref="ModEntry.IsFfu"/>),
    /// so it always lands in the FFU block AFTER the Ostrasort patch and re-applies
    /// its commands on top of whatever the patch merged.
    /// </summary>
    public List<ModEntry> PlainClaimants => Claimants.Where(m => !IsCommandEdit(m)).ToList();

    /// <summary>Pairs between two plain claimants - the ones load order decides.</summary>
    public IEnumerable<PairRelation> PlainPairs =>
        Pairs.Where(p => !IsCommandEdit(p.Earlier) && !IsCommandEdit(p.Later));

    /// <summary>A pair the FFU loader merges at load because one side edits via precision commands.</summary>
    public bool MergesAtLoad(PairRelation p) => IsCommandEdit(p.Earlier) || IsCommandEdit(p.Later);
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
    /// Warnings read out of the game's own logs from its last launch, kept apart
    /// from <see cref="Warnings"/> so the health card can drop them when the
    /// player dismisses that alarm. Everything that reports the full picture
    /// (every report format, the Warnings tab) uses <see cref="AllWarnings"/>.
    /// </summary>
    public List<string> LogWarnings { get; } = new();

    /// <summary>
    /// Identity of the last-launch log issues, for "dismiss until it happens
    /// again". Covers both the issues themselves and the logs' write stamps: the
    /// game rewrites Player.log on every launch, so the same error occurring
    /// again produces a different fingerprint and the alarm returns. Null when
    /// the logs reported nothing.
    /// </summary>
    public string? LogFingerprint { get; set; }

    /// <summary>Every warning, structural and from the game's logs. What the reports print.</summary>
    public IEnumerable<string> AllWarnings => Warnings.Concat(LogWarnings);

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

    /// <summary>Core object names and data files that load, for log attribution (filled by <see cref="Engine"/>).</summary>
    public HashSet<string> CoreObjectNames { get; } = new(StringComparer.Ordinal);
    public HashSet<string> CoreDataFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Plugins that use game code the installed build no longer has (see <see cref="CodeModCheck"/>).</summary>
    public List<CodeModIssue> CodeModIssues { get; } = new();

    public IEnumerable<ModEntry> AllMods =>
        Registered.Concat(UnregisteredLocal).Concat(UnregisteredWorkshop);

    public bool OrderChanged { get; private set; }

    // ---------------------------------------------------------- collisions ---

    public void FindCollisions()
    {
        // effective order: current registration order, then would-be additions.
        // The Ostrasort-generated patch never counts as a claimant - it IS the
        // resolution of a collision, not a party to one. A disabled entry does
        // not load, and an ignored mod is never registered, so neither can collide. A duplicated aLoadOrder entry (the
        // game can re-append a subscription) must not collide with itself -
        // only the first occurrence claims.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mods = Registered.Where(m => m.Kind != EntryKind.Core && m.Dir is not null)
            .Concat(UnregisteredLocal).Concat(UnregisteredWorkshop)
            .Where(m => !m.IsPatch && m.Loads && seen.Add(IdentityOf(m))).ToList();

        var byKey = new Dictionary<(string, string), List<ModEntry>>();
        foreach (var m in mods)
            foreach (var claim in m.Claims.Keys)
            {
                if (!byKey.TryGetValue(claim, out var list)) byKey[claim] = list = new();
                list.Add(m);
            }

        foreach (var ((type, name), claimants) in byKey.Where(kv => kv.Value.Count > 1)
                     .OrderBy(kv => kv.Key.Item1, StringComparer.Ordinal).ThenBy(kv => kv.Key.Item2, StringComparer.Ordinal))   // ordinal: the same install sorts the same on every machine
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

        // aLoots AND aCOs entries are "Item=weightxcount" - compare at item
        // granularity, unioning both pools. Some pools (character-generation
        // ship/intro routing) carry an empty aLoots and route everything through
        // aCOs, so ignoring aCOs would read those overrides as identical-empty.
        // a pipe-joined row ("A=0.7x1|B=0.3x1") holds several items - compare
        // every one of them, not just the first
        static HashSet<string> Items(string[] entries) =>
            entries.SelectMany(l => l.Split('|')).Where(s => s.Length > 0)
                .Select(l => l.Split('=')[0]).ToHashSet(StringComparer.Ordinal);
        static HashSet<string> PoolItems(ModEntry m, string type, string name)
        {
            var set = Items(m.Claims[(type, name)]!);
            if (m.CoClaims.TryGetValue((type, name), out var cos)) set.UnionWith(Items(cos));
            return set;
        }

        HashSet<string> ia = PoolItems(earlier, type, name), ib = PoolItems(later, type, name);
        var lost = ia.Where(x => !ib.Contains(x)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var added = ib.Where(x => !ia.Contains(x)).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        var rel = (lost.Length, added.Length) switch
        {
            (0, 0) => Relation.Equal,
            (0, _) => Relation.SupersetOk,
            (_, 0) => Relation.SubsetViolation,
            _ => Relation.Partial,
        };
        return new PairRelation(earlier, later, rel, lost, added);
    }

    /// <summary>
    /// Marks collisions a final-say (late-tier) mod resolves just by loading last.
    /// A character-generation mod - or any mod the user pinned late - deliberately
    /// curates the objects it owns (e.g. replacing the vanilla starting-ship dice
    /// roll with a deterministic choice), so when it is the last claimant its
    /// whole-object version is meant to win and the other mods' versions are
    /// intentionally replaced. That is lossless-by-design: it needs no attention
    /// and must NOT be patch-merged, since a per-item union would re-add exactly
    /// the entries the author removed. Run AFTER <see cref="FieldDiff.Annotate"/>
    /// so it overrides a mergeable verdict, and after category classification.
    /// </summary>
    public void MarkOrderResolved()
    {
        ComputeReorderLocks();
        foreach (var c in Collisions)
        {
            if (c.Claimants.Count < 2) continue;
            var last = c.Claimants[^1];   // Claimants are in effective load order
            if (last.Tier != LoadTier.Late) continue;

            // auto-detection claims final say only over character-generation
            // objects; a manual late pin claims it over everything the mod touches
            if (OwnsFinalSay(last, c))
            {
                // only where the order actually resolves a would-be loss
                if (!c.Pairs.Any(p => p.Rel is Relation.Partial or Relation.SubsetViolation or Relation.NonLoot)) continue;

                // another claimant has final say here too: the order picks one
                // curation and drops the other, which is the player's call
                var rival = c.Pairs.FirstOrDefault(p => p.Later == last && OwnsFinalSay(p.Earlier, c)
                    && p.Rel is Relation.Partial or Relation.SubsetViolation or Relation.NonLoot)?.Earlier;
                if (rival is not null)
                {
                    c.CurationClash = true;
                    c.ObjectMergeable = false;   // a merge would undo both curations
                    c.FieldNotes.Add($"{rival.DisplayName ?? rival.Name} and {last.DisplayName ?? last.Name} both have final say " +
                                     $"over this, so only the later one's version is used and {rival.DisplayName ?? rival.Name}'s " +
                                     "choices are lost - choose which mod to keep");
                    continue;
                }

                c.ResolvedByOrder = true;
                c.ObjectMergeable = false;   // final say wins wholesale - do not offer a field merge
                c.FieldNotes.Add($"{last.DisplayName ?? last.Name} owns this and loads last for final say, " +
                                 "so its version wins by design - the other mod's version is intentionally replaced, not merged");
                continue;
            }

            // Final say the mod did NOT earn over this object: a content mod that
            // ships a couple of CGEnc* life events is detected late for those, and
            // its ordinary shop pools come along for the ride. The late slot still
            // blocks the reorder (OrderLockedPair, honoured by BuildSuggestion's
            // rule 4), so promising "press Fix automatically to reorder them" would
            // send the player after a move that never happens. Mark it instead, so
            // the report says what is really going on and the patch picks it up.
            if (!c.PlainPairs.Any(OrderLockedPair)) continue;
            c.OrderLocked = true;
            c.FieldNotes.Add($"{last.DisplayName ?? last.Name} loads late because it owns new-game / start content, " +
                             "so no load order keeps both versions - the Ostrasort Patch merges them instead");
        }

        // every other pair the sort will not reorder: say why, and hand it to the
        // patch where the patch can win (it cannot against a later FFU mod)
        foreach (var c in Collisions.Where(c => !c.ResolvedByOrder && !c.OrderLocked && !c.CurationClash))
        {
            var locked = c.PlainPairs.Where(c.ReorderLockedPairs.Contains).ToList();
            if (locked.Count == 0) continue;
            static string N(ModEntry m) => m.DisplayName ?? m.Name;
            if (locked.FirstOrDefault(p => !FfuLockedPair(p)) is { } p)
            {
                c.OrderLocked = true;
                c.FieldNotes.Add((OrderLockedPair(p)
                    ? Tier(p.Earlier) == LoadTier.Early
                        ? $"{N(p.Earlier)} is pinned to load early, so it cannot move after {N(p.Later)}"
                        : $"{N(p.Later)} loads late (final say), so {N(p.Earlier)} cannot move after it"
                    : $"{N(p.Earlier)} and {N(p.Later)} each stock more than the other on different pools") +
                    " - no load order keeps both versions, so the Ostrasort Patch merges them instead");
            }
            else
            {
                var f = locked[0];
                c.FieldNotes.Add($"{N(f.Later)} is FFU-dependent, so it loads after every non-FFU mod, the Ostrasort Patch " +
                                 $"included - its version wins and {N(f.Earlier)}'s extra items are lost. Neither a reorder " +
                                 "nor the patch can keep both, so choose which mod to keep");
            }
        }

        // a partial overlap with a plain (non-command) FFU claimant: that mod
        // loads after the patch and replaces the whole pool, so the patch cannot
        // merge it (Patcher.IsPatchableLoot) - say so rather than offer a merge
        foreach (var c in Collisions.Where(c => c.Type == "loot" && !c.ResolvedByOrder && !c.CurationClash))
        {
            if (c.PlainClaimants.LastOrDefault(m => m.IsFfu) is not { } ffu) continue;
            if (!c.PlainPairs.Any(p => p.Rel == Relation.Partial)) continue;
            c.FieldNotes.Add($"{ffu.DisplayName ?? ffu.Name} is FFU-dependent and ships a whole copy of this pool, so it loads " +
                             "after the Ostrasort Patch and its version wins - the patch cannot keep both, so choose which mod to keep");
        }
    }

    /// <summary>
    /// Works out which subset-violation pairs rule 4 must NOT reorder, because
    /// the move could never stick: the later claimant holds a later tier band than
    /// the earlier one (<see cref="OrderLockedPair"/>), it is FFU and the earlier
    /// one is not (<see cref="FfuLockedPair"/>), or the same two mods
    /// are each a strict superset of the other on different pools, where fixing
    /// one pool breaks the other and the suggestion would flip on every apply.
    /// Idempotent; run before the sort and before the report reads the flags.
    /// </summary>
    internal void ComputeReorderLocks()
    {
        // (superset mod, subset mod) for every clean subset relation, whichever
        // way round the pair currently loads
        var supersets = new HashSet<(ModEntry Sup, ModEntry Sub)>();
        foreach (var c in Collisions)
            foreach (var p in c.PlainPairs)
            {
                if (p.Rel == Relation.SubsetViolation) supersets.Add((p.Earlier, p.Later));
                else if (p.Rel == Relation.SupersetOk) supersets.Add((p.Later, p.Earlier));
            }

        foreach (var c in Collisions)
        {
            c.ReorderLockedPairs.Clear();
            foreach (var p in c.PlainPairs.Where(p => p.Rel == Relation.SubsetViolation))
                if (OrderLockedPair(p) || FfuLockedPair(p) || supersets.Contains((p.Later, p.Earlier))
                    || (OwnsFinalSay(p.Earlier, c) && OwnsFinalSay(p.Later, c)))   // item count never picks between curations
                    c.ReorderLockedPairs.Add(p);
        }
    }

    /// <summary>
    /// True for a subset violation reordering cannot fix because of the load
    /// tiers: the later claimant sits in a later band than the earlier one (late
    /// final say over normal, or normal over a mod pinned early), so moving the
    /// superset below it would only be undone by the category-band rule on the
    /// same pass. Rule 4 skips these, and <see cref="MarkOrderResolved"/> flags
    /// them for the report and the patcher, so the sorter and the report can
    /// never disagree about whether a reorder is coming.
    /// </summary>
    internal static bool OrderLockedPair(PairRelation p) =>
        p.Rel == Relation.SubsetViolation && !p.Earlier.IsFfu && !p.Later.IsFfu && Tier(p.Earlier) < Tier(p.Later);

    /// <summary>
    /// True for a subset violation where the later (subset) claimant is FFU and
    /// the earlier one is not: rule 5 keeps every FFU mod after every non-FFU
    /// mod, so the superset can never move past it. The patch cannot help either,
    /// since it closes the non-FFU block and the FFU mod loads after it.
    /// </summary>
    /// <summary>
    /// Whether this mod has final say over the collision's object: it loads late,
    /// and either the player pinned it (final say over everything it touches) or
    /// the object is character-generation content (what auto-detection claims).
    /// </summary>
    internal static bool OwnsFinalSay(ModEntry m, Collision c) =>
        Tier(m) == LoadTier.Late && (m.CategoryManual || CategoryAnalysis.IsCharacterGenClaim(c.Type, c.ObjName));

    internal static bool FfuLockedPair(PairRelation p) =>
        p.Rel == Relation.SubsetViolation && !p.Earlier.IsFfu && p.Later.IsFfu;

    /// <summary>
    /// Any collision still needing the user's eyes - the same predicate the GUI
    /// badge uses (<see cref="CollisionView.NeedsAttention"/>), so headless exit
    /// codes and the window can never disagree about whether action is needed.
    /// </summary>
    public bool HasUnresolvedConflicts => Collisions.Any(CollisionView.NeedsAttention);

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

        // of several entries for one mod, keep the first ENABLED one (the copy the
        // game loads, and the one FindCollisions analyses), else the first - so
        // dropping a duplicate never turns the mod off
        var survivor = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in Registered.GroupBy(IdentityOf, StringComparer.OrdinalIgnoreCase))
            survivor[g.Key] = g.FirstOrDefault(m => !m.Disabled) ?? g.First();

        foreach (var m in Registered)
        {
            var identity = IdentityOf(m);
            if (!ReferenceEquals(survivor[identity], m) || !seenIdentity.Add(identity))
            {
                Changes.Add(new Change("remove", m.Raw, "duplicate entry"));
                Warnings.Add($"duplicate aLoadOrder entry '{m.Raw}' - keeping '{survivor[identity].Raw}'");
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

        // rule 3: register what exists but is invisible to the MODS screen
        // (unless the user chose to leave it unregistered - the ignore list).
        // Runs BEFORE the subset-first rule so a collision involving a
        // not-yet-registered mod is order-corrected in the SAME suggestion,
        // not only on the next analyze-apply cycle.
        foreach (var m in UnregisteredLocal.Where(m => !m.Ignored))
        {
            work.Add(m);
            Changes.Add(new Change("add", SuggestedRaw(m),
                m.HoldOnRegister
                    ? (m.Kind == EntryKind.PluginDir
                        ? "data mod under BepInEx\\plugins added turned off - tick Active to load it"
                        : "local mod folder added turned off - tick Active to load it")
                    : m.IsPatch
                        ? "the generated Ostrasort Patch exists on disk but is unregistered"
                        : m.Kind == EntryKind.PluginDir
                            ? "data mod under BepInEx\\plugins present but unregistered - the game loads only what aLoadOrder lists"
                            : "local mod folder present but unregistered - invisible to the MODS screen"));
        }
        foreach (var m in UnregisteredWorkshop)
        {
            work.Add(m);
            Changes.Add(new Change("add", SuggestedRaw(m),
                m.HoldOnRegister
                    ? "subscribed Workshop item added turned off - tick Active to load it"
                    : "subscribed Workshop item not in aLoadOrder (the game will add it on next launch anyway)"));
        }

        // rule 4: clean subset/superset loot collisions load subset-first.
        // A SubsetViolation means the pair is reversed: the earlier claimant is
        // the superset. Move it to just after the later (subset) claimant.
        ComputeReorderLocks();
        foreach (var col in Collisions)
            foreach (var pair in col.Pairs.Where(p => p.Rel == Relation.SubsetViolation))
            {
                // a final-say (late-tier) mod deliberately curates its pool - a
                // smaller set loading last is intended, not a violation. Never
                // move a lower-tier superset below it: that would push the
                // final-say mod toward the top and rule 4.5 would undo the move
                // on this same pass. MarkOrderResolved has already flagged these
                // (ResolvedByOrder when the mod owns the object, OrderLocked when
                // it does not and the patch has to resolve it instead). The same
                // goes for a pair whose relation flips on another pool.
                if (col.ReorderLockedPairs.Contains(pair)) continue;
                int from = work.IndexOf(pair.Earlier), to = work.IndexOf(pair.Later);
                if (from < 0 || to < 0 || from > to) continue;
                work.RemoveAt(from);
                work.Insert(work.IndexOf(pair.Later) + 1, pair.Earlier);
                Changes.Add(new Change("move", pair.Earlier.Raw,
                    $"its {col.Key} pool is a superset of {pair.Later.Label}'s and must load after it"));
            }

        // rule 4.5: category load-priority bands. Detected character-generation
        // mods (late / final say) and any mod the user pinned (early or late)
        // settle into ordered bands within the content block - early, then
        // normal, then late - stable inside each band so nothing else shuffles.
        // Runs AFTER the loot subset rule (which it also guards above), so a
        // final-say mod is never left pushed up by pure item count. FFU mods and
        // the patch keep their slots here; rules 5 and 6 place them.
        var band = Enumerable.Range(insertAt, work.Count - insertAt)
            .Where(i => !work[i].IsFfu && !work[i].IsPatch).ToList();
        var content = band.Select(i => work[i]).ToList();
        var ordered = content.OrderBy(m => (int)Tier(m)).ToList();   // OrderBy is stable
        if (!ordered.SequenceEqual(content))
        {
            for (var k = 0; k < band.Count; k++) work[band[k]] = ordered[k];
            foreach (var m in ordered.Where((m, k) => content.IndexOf(m) != k && Tier(m) != LoadTier.Normal))
                Changes.Add(new Change("move", SuggestedRaw(m),
                    Tier(m) == LoadTier.Late
                        ? (m.CategoryManual
                            ? "you pinned it to load late (final say)"
                            : "owns the new-game / start flow - loads late so its choices win over other mods")
                        : "you pinned it to load early (yields to other mods)"));
        }

        // rule 4.6: a non-FFU mod loads after the mods its requiredMods (or
        // Autoload.Meta.toml) names - FFU drops a mod whose dependency loads
        // after it. FFU dependents are ordered inside the FFU block (rule 5).
        // A move never crosses a load-priority band; when the bands keep a
        // dependent ahead of its dependency, say so instead.
        var plain = work.Where(m => !m.IsFfu && !m.IsPatch && m.Kind != EntryKind.Core).ToList();
        var bandWarned = new HashSet<(ModEntry, ModEntry)>();
        for (var pass = 0; pass <= plain.Count; pass++)
        {
            var moved = false;
            foreach (var m in plain.Where(m => m.HasFfuDeps))
                foreach (var dep in m.FfuDeps.Select(n => ResolveDep(plain, n)).OfType<ModEntry>().Where(d => d != m))
                {
                    int mi = work.IndexOf(m), di = work.IndexOf(dep);
                    if (di < mi) continue;
                    if (Tier(dep) != Tier(m))
                    {
                        if (bandWarned.Add((m, dep)))
                            Warnings.Add($"'{m.DisplayName ?? m.Name}' requires '{dep.DisplayName ?? dep.Name}' but its load " +
                                         "priority keeps it loading first (FFU skips a mod whose dependency loads after it) - change one of their load priorities");
                        continue;
                    }
                    work.RemoveAt(mi);
                    work.Insert(work.IndexOf(dep) + 1, m);
                    Changes.Add(new Change("move", SuggestedRaw(m),
                        $"requires {dep.DisplayName ?? dep.Name}, which must load first"));
                    moved = true;
                }
            if (!moved) break;
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

        // a hand-edited/corrupted file may have lost its core entry entirely -
        // never suggest (and so never write) a coreless order; ValidateOrder
        // BLOCKs the same thing on the manual-arrange path
        if (!SuggestedOrder.Any(e => e.Split('|')[0] == "core"))
        {
            SuggestedOrder.Insert(0, "core");
            Changes.Add(new Change("add", "core", "the core entry was missing - core must load first"));
            Warnings.Add("aLoadOrder has no 'core' entry - the file was hand-edited or damaged; " +
                         "the suggestion restores it (the game loads core first regardless)");
        }

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

        ModEntry? Resolve(string strName) => ResolveDep(ordered, strName);

        // bubble dependents after their dependencies (bounded - a cycle can't loop forever)
        for (var pass = 0; pass <= ffu.Count; pass++)
        {
            var moved = false;
            foreach (var m in ordered.Where(m => m.HasFfuDeps).ToList())
            {
                var deps = m.FfuDeps.Select(Resolve).OfType<ModEntry>().Where(d => d != m).ToList();
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

        // FFU patch mods: right after the mod they patch (when it is in this block),
        // but never ahead of anything else they require - FFU drops a mod that
        // loads before one of its requiredMods, so the pin lands after the LATEST
        // of the target and every dependency
        foreach (var p in ordered.Where(m => m is { IsFfuPatch: true, FfuPatchTarget: not null }).ToList())
        {
            if (!ordered.Contains(p.FfuPatchTarget!)) continue;
            var anchors = p.FfuDeps.Select(Resolve).OfType<ModEntry>()
                .Append(p.FfuPatchTarget!).Where(d => d != p && ordered.Contains(d)).ToList();
            var was = ordered.IndexOf(p);
            ordered.RemoveAt(was);
            var at = anchors.Max(d => ordered.IndexOf(d)) + 1;
            ordered.Insert(at, p);
            if (at == was) continue;   // already in place
            Changes.Add(new Change("move", SuggestedRaw(p),
                $"FFU patch mods load right after their target ({p.FfuPatchTarget!.DisplayName ?? p.FfuPatchTarget.Name}) and anything else they require - and should be removed after one game launch"));
        }

        if (!ordered.SequenceEqual(ffu))
            Changes.Add(new Change("move", "(FFU block)",
                "FFU block ordered: Minor Fixes Plus tier first, then dependency order per Autoload.Meta.toml"));
        return ordered;
    }

    /// <summary>
    /// The mod a dependency name refers to: by strName, then folder name, then
    /// FFU's own id rule (spaces become underscores).
    /// </summary>
    internal static ModEntry? ResolveDep(IReadOnlyList<ModEntry> mods, string strName) =>
        mods.FirstOrDefault(m => string.Equals(m.DisplayName, strName, StringComparison.OrdinalIgnoreCase))
        ?? mods.FirstOrDefault(m => string.Equals(m.Name, strName, StringComparison.OrdinalIgnoreCase))
        ?? mods.FirstOrDefault(m => FfuAnalysis.FfuId(m.DisplayName) == FfuAnalysis.FfuId(strName)
                                 || FfuAnalysis.FfuId(m.Name) == FfuAnalysis.FfuId(strName));

    internal static string IdentityOf(ModEntry m) =>
        m.Kind == EntryKind.Local ? $"local:{m.Name}"
        : m.Raw.Length > 0 ? System.IO.Path.TrimEndingDirectorySeparator(m.Raw.Split('|')[0])   // the path, never its marker
        : m.Dir ?? m.Name;   // unregistered entries all share Raw "" - identify by folder

    /// <summary>
    /// A mod's effective load tier for sorting. A disabled entry does not load,
    /// so it is treated as neutral and never moved by a category/priority rule.
    /// </summary>
    internal static LoadTier Tier(ModEntry m) => m.Disabled ? LoadTier.Normal : m.Tier;

    /// <summary>
    /// The exact aLoadOrder string this mod registers as: local mods keep their
    /// |edit marker, Workshop/plugins-dir mods register by absolute path, the
    /// generated patch registers plain. Used by the suggestion and by the
    /// single-mod Register action.
    /// </summary>
    public static string SuggestedRaw(ModEntry m)
    {
        if (m.Registered) return m.Raw;
        var path = m.Kind is EntryKind.Workshop or EntryKind.PluginDir ? m.Dir!   // absolute-path registrations
            : m.Name;
        // held back by "Add new mods turned off": the game's own |disabled marker,
        // which replaces |edit rather than joining it - the same 2-part entry the
        // in-game MODS screen and the Active checkbox write
        if (m.HoldOnRegister) return path + "|disabled";
        return m.Kind is EntryKind.Workshop or EntryKind.PluginDir || m.IsPatch
            ? path                           // the patch registers plain, nothing to upload
            : path + "|edit";
    }

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
                    // a final-say (late-tier) mod curating a pool it loads last is
                    // intended, not a violation - it deliberately replaces the set
                    if (rel.Rel == Relation.SubsetViolation &&
                        !(Tier(claimants[j]) == LoadTier.Late && Tier(claimants[i]) < LoadTier.Late))
                        issues.Add($"{type}/{name}: {claimants[j].DisplayName ?? claimants[j].Name} would drop " +
                                   $"{rel.LostFromEarlier.Length} item(s) that {claimants[i].DisplayName ?? claimants[i].Name} stocks " +
                                   "- the superset should load last.");
                }

        // category priority: a final-say (late-tier) mod must load after lower-tier
        // non-FFU content, or a later mod's whole-object override beats its curation
        foreach (var late in order.Where(m => !m.IsFfu && !m.IsPatch && m.Kind != EntryKind.Core && !m.Disabled && Tier(m) == LoadTier.Late))
        {
            var after = order.Skip(order.IndexOf(late) + 1)
                .Count(m => !m.IsFfu && !m.IsPatch && m.Kind != EntryKind.Core && !m.Disabled && Tier(m) < LoadTier.Late);
            if (after > 0)
                issues.Add($"{late.DisplayName ?? late.Name}: has final say (new game / start) but {after} lower-priority mod(s) " +
                           "load after it - move it below them so its changes are not overridden.");
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
        foreach (var m in order.Where(m => m.HasFfuDeps))
            foreach (var dep in m.FfuDeps)
            {
                var t = order.FirstOrDefault(o => string.Equals(o.DisplayName, dep, StringComparison.OrdinalIgnoreCase)
                                               || string.Equals(o.Name, dep, StringComparison.OrdinalIgnoreCase))
                     ?? order.FirstOrDefault(o => FfuAnalysis.FfuId(o.DisplayName) == FfuAnalysis.FfuId(dep)
                                               || FfuAnalysis.FfuId(o.Name) == FfuAnalysis.FfuId(dep));
                if (t is not null && t != m && order.IndexOf(t) > order.IndexOf(m))
                    issues.Add($"{m.DisplayName ?? m.Name}: loads before its FFU dependency '{dep}'.");
            }

        var patchIdx = order.FindIndex(m => m.IsPatch);
        if (patchIdx >= 0 && order.Skip(patchIdx + 1).Any(m => !m.IsFfu && !m.Disabled))
            issues.Add(firstFfu >= 0
                ? "the generated Ostrasort Patch must close the non-FFU block (only FFU mods after it) or the pools it merges get overridden again."
                : "the generated Ostrasort Patch must load last or the pools it merges get overridden again.");

        return issues;
    }
}
