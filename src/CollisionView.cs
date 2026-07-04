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

    private static string Short(ModEntry m) => m.DisplayName ?? m.Name;

    private static string Sample(string[] items, int n = 3) =>
        items.Length <= n ? string.Join(", ", items)
        : string.Join(", ", items.Take(n)) + $", +{items.Length - n} more";

    public static List<ViewLine> Build(Analysis a)
    {
        var lines = new List<ViewLine>();
        if (a.Collisions.Count == 0)
        {
            lines.Add(new ViewLine("No two mods claim the same object — no conflicts.", LineSev.Good, 0));
            return lines;
        }

        void Add(string text, LineSev sev, int indent, bool bold = false) =>
            lines.Add(new ViewLine(text, sev, indent, bold));

        // one group per distinct set of conflicting mods (in load order)
        foreach (var group in a.Collisions
            .GroupBy(c => string.Join("", c.Claimants.Select(m =>
                m.Kind == EntryKind.Workshop ? m.Name : "local:" + m.Name)))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
        {
            var cols = group.ToList();
            var mods = cols[0].Claimants.Select(Short).ToList();

            var anyConflict = cols.Any(c => !c.ResolvedByPatch && c.Pairs.Any(p => p.Rel == Relation.Partial));
            var anyWrongOrder = cols.Any(c => !c.ResolvedByPatch && c.Pairs.Any(p => p.Rel == Relation.SubsetViolation));
            var anyResolved = cols.Any(c => c.ResolvedByPatch);
            var anyNonLoot = cols.Any(c => c.Pairs.Any(p => p.Rel == Relation.NonLoot));

            Add(string.Join("   +   ", mods), LineSev.Normal, 0, bold: true);

            // "3 shop/kiosk inventories" or "5 objects (3 shop pools, 2 conditions)"
            var byType = cols.GroupBy(c => c.Type).OrderByDescending(g => g.Count()).ToList();
            string Count(IGrouping<string, Collision> g) =>
                $"{g.Count()} {(g.Count() == 1 ? HumanType(g.Key).One : HumanType(g.Key).Many)}";
            var what = byType.Count == 1 ? Count(byType[0])
                : $"{cols.Count} objects ({string.Join(", ", byType.Select(Count))})";

            if (anyConflict)
            {
                Add($"Both stock {what}. Neither mod's version covers the other, so whichever", LineSev.Bad, 1);
                Add("loads last silently deletes the other's items.", LineSev.Bad, 1);
                Add("→ Fixable — use “Resolve conflicts & generate patch” (or --patch) to merge them.", LineSev.Warn, 1);
            }
            else if (anyWrongOrder)
            {
                Add($"Both stock {what}, and the current load order is dropping items.", LineSev.Bad, 1);
                Add("→ Fixable — use “Apply suggested order” (or --apply) to reorder them.", LineSev.Warn, 1);
            }
            else if (anyResolved)
            {
                Add($"Both edit {what} — merged by the Ostrasort Patch, nothing lost.", LineSev.Good, 1);
            }
            else if (anyNonLoot)
            {
                Add($"Both edit {what}. The game keeps only the last-loaded version and", LineSev.Warn, 1);
                Add("Ostrasort can't merge these automatically — see the detail below.", LineSev.Warn, 1);
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
                    : p is null ? "claimed by multiple mods"
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
                    Add(n, n.StartsWith("BOTH") ? LineSev.Bad : n.StartsWith("disjoint") ? LineSev.Good : LineSev.Dim, 3);
            }
            Add("", LineSev.Normal, 0);
        }
        return lines;
    }
}
