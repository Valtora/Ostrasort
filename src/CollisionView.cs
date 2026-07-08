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

    /// <summary>Only collisions still in effect — the Collisions tab. Patch-resolved ones move to <see cref="BuildResolved"/>.</summary>
    public static List<ViewLine> BuildActive(Analysis a)
    {
        var active = a.Collisions.Where(c => !c.ResolvedByPatch).ToList();
        var resolved = a.Collisions.Count - active.Count;
        var empty = resolved > 0
            ? $"No active conflicts — {resolved} resolved by the patch (see the Resolved collisions tab)."
            : "No two mods claim the same object — no conflicts.";
        return Render(active, empty);
    }

    /// <summary>Only collisions the generated patch merges away — the Resolved collisions tab.</summary>
    public static List<ViewLine> BuildResolved(Analysis a) =>
        Render(a.Collisions.Where(c => c.ResolvedByPatch).ToList(),
               "No collisions have been merged into a patch yet.");

    private static List<ViewLine> Render(IReadOnlyList<Collision> collisions, string emptyMessage)
    {
        var lines = new List<ViewLine>();
        if (collisions.Count == 0)
        {
            lines.Add(new ViewLine(emptyMessage, LineSev.Good, 0));
            return lines;
        }

        void Add(string text, LineSev sev, int indent, bool bold = false) =>
            lines.Add(new ViewLine(text, sev, indent, bold));

        // one group per distinct set of conflicting mods (in load order)
        foreach (var group in collisions
            .GroupBy(c => string.Join("", c.Claimants.Select(m =>
                m.Kind == EntryKind.Workshop ? m.Name : "local:" + m.Name)))
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
                Add(outcome, LineSev.Dim, 3);
                foreach (var n in c.FieldNotes)
                    Add(n, n.StartsWith("conflict") ? LineSev.Bad : n.StartsWith("auto-merge") ? LineSev.Good : LineSev.Dim, 3);
            }
            Add("", LineSev.Normal, 0);
        }
        return lines;
    }
}
