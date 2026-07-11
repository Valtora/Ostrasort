namespace Ostrasort;

public enum LineSev { Normal, Dim, Good, Warn, Bad }

/// <summary>A presentation line - UI-agnostic so both the GUI and tests use it.</summary>
public sealed record ViewLine(string Text, LineSev Sev, int Indent, bool Bold = false);

/// <summary>
/// Human-readable rendering of the collision list, grouped by the set of mods
/// involved. Shared by the WPF Collisions tab and console verification, so the
/// wording is testable without a window.
/// </summary>
public static class CollisionView
{
    private static readonly Dictionary<string, (string One, string Many)> TypeNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["loot"] = ("shop/kiosk inventory", "shop/kiosk inventories"),
            ["condowners"] = ("object", "objects"),
            ["conditions"] = ("condition", "conditions"),
            ["conditions_simple"] = ("condition", "conditions"),
            ["interactions"] = ("interaction", "interactions"),
            ["installables"] = ("installable", "installables"),
            ["items"] = ("item sprite", "item sprites"),
            ["condtrigs"] = ("trigger", "triggers"),
            ["condrules"] = ("condition rule", "condition rules"),
            ["cooverlays"] = ("overlay", "overlays"),
            ["ships"] = ("ship layout", "ship layouts"),
        };

    private static (string One, string Many) HumanType(string t) =>
        TypeNames.TryGetValue(t, out var v) ? v : (t, t);

    /// <summary>Singular human label for a data type (e.g. "condowners" -&gt; "object"). Falls back to the raw type.</summary>
    public static string TypeLabel(string t) => HumanType(t).One;

    private static string Short(ModEntry m) => m.DisplayName ?? m.Name;

    private static string Sample(string[] items, int n = 3) =>
        items.Length <= n ? string.Join(", ", items)
        : string.Join(", ", items.Take(n)) + $", +{items.Length - n} more";

    public static List<ViewLine> Build(Analysis a) =>
        Render(a.Collisions, "No two mods claim the same object — no conflicts.");

    /// <summary>
    /// A collision still needs the user's eyes: the load order / patch / FFU do
    /// NOT already handle it losslessly. Benign cases return false - patch-merged,
    /// FFU- or additively-merged at load, or same-set loot where only quantities
    /// differ (nothing lost). Drives BOTH the Collisions tab count and the
    /// "no action needed" split, so the number and the sections always agree.
    /// </summary>
    public static bool NeedsAttention(Collision c)
    {
        if (c.ResolvedByPatch || c.FfuMergedAtLoad || c.AdditiveAtLoad) return false;
        if (c.ObjectMergeable) return true;                             // a field merge would preserve more than last-wins
        return c.Pairs.Any(p => p.Rel is Relation.Partial              // loot: each side loses items (patch merges)
                                       or Relation.SubsetViolation      // loot: order drops items (reorder fixes)
                                       or Relation.NonLoot);            // object: last-wins drops a version, nothing to merge
    }

    /// <summary>Stable key for the set of mods in a collision, used to group the display.</summary>
    private static string GroupKey(Collision c) =>
        string.Join("", c.Claimants.Select(m => m.Kind == EntryKind.Workshop ? m.Name : "local:" + m.Name));

    /// <summary>
    /// The Collisions tab: ONLY collisions that need action. Benign ones (handled
    /// losslessly by the load order / FFU / the game, or already patch-merged)
    /// are not shown here at all - they live on the Resolved / handled tab - so
    /// this tab reads clean when there is nothing to do.
    /// </summary>
    public static List<ViewLine> BuildActive(Analysis a)
    {
        var attention = a.Collisions.Where(NeedsAttention).ToList();
        if (attention.Count > 0)
        {
            var lines = new List<ViewLine>();
            RenderGroups(attention, lines);
            return lines;
        }

        var handled = a.Collisions.Count - attention.Count;
        return new List<ViewLine>
        {
            new(handled > 0
                ? $"No conflicts need action — {handled} detected, all handled (see the Resolved / handled tab)."
                : "No two mods claim the same object — no conflicts.", LineSev.Good, 0),
        };
    }

    /// <summary>
    /// The Resolved / handled tab: every collision that needs no action - merged
    /// by the Ostrasort Patch, merged field-/entry-by-entry at load by FFU or the
    /// game, or handled losslessly by the load order (e.g. same-set shop pools).
    /// </summary>
    public static List<ViewLine> BuildResolved(Analysis a) =>
        Render(a.Collisions.Where(c => !NeedsAttention(c)).ToList(),
               a.Collisions.Count == 0
                   ? "No two mods claim the same object — no conflicts."
                   : "Nothing handled automatically — every collision needs action (see the Collisions tab).");

    private static List<ViewLine> Render(IReadOnlyList<Collision> collisions, string emptyMessage)
    {
        var lines = new List<ViewLine>();
        if (collisions.Count == 0)
            lines.Add(new ViewLine(emptyMessage, LineSev.Good, 0));
        else
            RenderGroups(collisions, lines);
        return lines;
    }

    private static void RenderGroups(IReadOnlyList<Collision> collisions, List<ViewLine> lines)
    {
        void Add(string text, LineSev sev, int indent, bool bold = false) =>
            lines.Add(new ViewLine(text, sev, indent, bold));

        // one group per distinct set of conflicting mods (in load order)
        foreach (var group in collisions
            .GroupBy(GroupKey)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
        {
            var cols = group.ToList();
            var mods = cols[0].Claimants.Select(Short).ToList();

            var anyLootConflict = cols.Any(c => !c.ResolvedByPatch && c.Pairs.Any(p => p.Rel == Relation.Partial));
            var anyMergeableObj = cols.Any(c => !c.ResolvedByPatch && c.ObjectMergeable);
            var anyWrongOrder = cols.Any(c => !c.ResolvedByPatch && c.Pairs.Any(p => p.Rel == Relation.SubsetViolation));
            var anyResolved = cols.Any(c => c.ResolvedByPatch);
            var anyFfuMerged = cols.Any(c => !c.ResolvedByPatch && c.FfuMergedAtLoad);
            var anyAdditive = cols.Any(c => !c.ResolvedByPatch && c.AdditiveAtLoad);
            var anyDeadReplace = cols.Any(c => !c.ResolvedByPatch && !c.ObjectMergeable && !c.FfuMergedAtLoad &&
                                               !c.AdditiveAtLoad && c.Pairs.Any(p => p.Rel == Relation.NonLoot));
            var fixableByPatch = anyLootConflict || anyMergeableObj;

            Add(string.Join("   +   ", mods), LineSev.Normal, 0, bold: true);

            // "3 shop/kiosk inventories" or "5 objects (3 shop pools, 2 conditions)"
            var byType = cols.GroupBy(c => c.Type).OrderByDescending(g => g.Count()).ToList();
            string Count(IGrouping<string, Collision> g) =>
                $"{g.Count()} {(g.Count() == 1 ? HumanType(g.Key).One : HumanType(g.Key).Many)}";
            var what = byType.Count == 1 ? Count(byType[0])
                : string.Join(" + ", byType.Select(Count));

            if (fixableByPatch)
            {
                Add($"Both edit {what}. The game would keep only the last-loaded version and drop", LineSev.Bad, 1);
                Add("the other mod's changes — but Ostrasort can merge them so nothing is lost.", LineSev.Bad, 1);
                Add("→ Fixable — use “Resolve conflicts & generate patch” (or --patch) to merge them.", LineSev.Warn, 1);
            }
            else if (anyWrongOrder)
            {
                Add($"Both stock {what}, and the current load order is dropping items.", LineSev.Bad, 1);
                Add("→ Fixable — use “Apply Suggested Fixes” (or --apply) to reorder them.", LineSev.Warn, 1);
            }
            else if (anyResolved)
            {
                Add($"Both edit {what} — merged by the Ostrasort Patch, nothing lost.", LineSev.Good, 1);
            }
            else if (anyFfuMerged)
            {
                Add($"Both edit {what} — FFU merges them field-by-field at load, nothing lost", LineSev.Good, 1);
                Add("(a field changed by several mods goes to the last-loaded one; see the detail below).", LineSev.Good, 1);
            }
            else if (anyAdditive)
            {
                Add($"Both add to {what} — the game merges these entry-by-entry at load, nothing lost", LineSev.Good, 1);
                Add("(only an entry defined by both mods goes to the last-loaded one; see the detail below).", LineSev.Good, 1);
            }
            else if (anyDeadReplace)
            {
                Add($"Both edit {what}. The game keeps only the last-loaded version and there is", LineSev.Warn, 1);
                Add("nothing to merge (see the detail below).", LineSev.Warn, 1);
            }
            else
            {
                Add($"Both edit {what} — the load order handles it, nothing lost.", LineSev.Good, 1);
            }

            foreach (var c in cols)
            {
                var p = c.Pairs.LastOrDefault();
                string outcome =
                    c.ResolvedByPatch ? "merged into the patch"
                    : c.FfuMergedAtLoad ? "merged by FFU at load"
                    : c.AdditiveAtLoad ? "merged entry-by-entry at load"
                    : p is null ? "claimed by multiple mods"
                    : p.Rel == Relation.NonLoot
                        ? (c.ObjectMergeable ? "mergeable — see the field notes below" : $"only {Short(p.Later)}'s version applies")
                        : p.Rel switch
                        {
                            Relation.Partial => $"{Short(p.Later)} would drop {p.LostFromEarlier.Length} of {Short(p.Earlier)}'s items ({Sample(p.LostFromEarlier)})",
                            Relation.SubsetViolation => $"{Short(p.Later)} drops {p.LostFromEarlier.Length} item(s) {Short(p.Earlier)} stocks",
                            Relation.SupersetOk => $"{Short(p.Later)} adds {p.AddedByLater.Length} item(s) — fine",
                            Relation.Equal => "same items, different quantities",
                            _ => $"only {Short(p.Later)}'s version applies",
                        };
                Add($"• {c.ObjName}  ({HumanType(c.Type).One})", LineSev.Normal, 2);
                if (c.FriendlyName is { Length: > 0 } friendly)
                    Add($"“{friendly}”", LineSev.Dim, 3);
                Add(outcome, LineSev.Dim, 3);
                foreach (var n in c.FieldNotes)
                    Add(n, n.StartsWith("conflict") ? LineSev.Bad : n.StartsWith("auto-merge") ? LineSev.Good : LineSev.Dim, 3);
            }
            Add("", LineSev.Normal, 0);
        }
    }
}
