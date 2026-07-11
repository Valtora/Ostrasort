using System.IO;
using System.Text.Json.Nodes;

namespace Ostrasort;

/// <summary>
/// Builds the side-by-side field table behind the Collisions drill-down: the
/// base game's version (when there is one) and each claimant's version of the
/// object, loaded from disk, laid out one column per source with every field
/// marked as changed-vs-vanilla and/or contested. UI-agnostic so the wording
/// and the change/contest logic are testable without a window; the dialog just
/// renders the model. Reuses <see cref="FieldDiff.LoadObject"/> so it reads the
/// exact same object the merge does.
/// </summary>
public static class CollisionDetail
{
    /// <summary>One source's value for one field. <see cref="Present"/> false = the field is absent from that source.</summary>
    public sealed record Cell(JsonNode? Node, bool Present, bool Changed);

    /// <summary>One field across every source. <see cref="Contested"/> = the claimants disagree among themselves.</summary>
    public sealed record Row(string Field, bool Contested, bool AnyChanged, IReadOnlyList<Cell> Cells);

    public sealed record Model(
        string ObjName, string TypeLabel, string? FriendlyName,
        IReadOnlyList<string> Columns, bool HasVanilla,
        IReadOnlyList<Row> Rows, IReadOnlyList<string> MissingClaimants);

    public static Model Build(GameEnv env, Collision c, IReadOnlyList<string>? ignore = null)
    {
        var coreObj = FieldDiff.LoadObject(Path.Combine(env.CoreDataDir, c.Type), c.ObjName, ignore) as JsonObject;

        var claimants = new List<(string Label, JsonObject? Obj)>();
        var missing = new List<string>();
        foreach (var m in c.Claimants)
        {
            var obj = m.Dir is null ? null
                : FieldDiff.LoadObject(Path.Combine(m.Dir, "data", c.Type), c.ObjName, ignore) as JsonObject;
            var label = m.DisplayName ?? m.Name;
            if (obj is null) missing.Add(label);
            claimants.Add((label, obj));
        }

        var columns = new List<string>();
        if (coreObj is not null) columns.Add("Vanilla");
        columns.AddRange(claimants.Select(x => x.Label));

        // union of field keys across every source (identity excluded), stable
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void AddKeys(JsonObject? o)
        {
            if (o is null) return;
            foreach (var (k, _) in o)
                if (k != "strName" && seen.Add(k)) keys.Add(k);
        }
        AddKeys(coreObj);
        foreach (var x in claimants) AddKeys(x.Obj);

        var rows = new List<Row>();
        foreach (var key in keys)
        {
            var vanillaVal = coreObj?[key];
            var vanillaHas = coreObj?.ContainsKey(key) == true;

            var cells = new List<Cell>();
            if (coreObj is not null) cells.Add(new Cell(vanillaVal, vanillaHas, Changed: false));   // baseline

            var claimSignatures = new List<string>();
            var anyChanged = false;
            foreach (var (_, obj) in claimants)
            {
                var has = obj?.ContainsKey(key) == true;
                var val = obj?[key];
                // "changed" = differs from vanilla; with no vanilla field, merely being present is the change
                var changed = vanillaHas ? !NodeEquals(val, vanillaVal) : has;
                anyChanged |= changed;
                cells.Add(new Cell(val, has, changed));
                claimSignatures.Add(has ? val?.ToJsonString() ?? "null" : "\0absent");
            }

            var contested = claimSignatures.Distinct(StringComparer.Ordinal).Count() > 1;
            rows.Add(new Row(key, contested, anyChanged, cells));
        }

        // most interesting first: contested, then changed, then untouched (stable)
        rows = rows.OrderBy(r => r.Contested ? 0 : r.AnyChanged ? 1 : 2).ToList();

        return new Model(c.ObjName, CollisionView.TypeLabel(c.Type), c.FriendlyName,
            columns, coreObj is not null, rows, missing);
    }

    private static bool NodeEquals(JsonNode? a, JsonNode? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return JsonNode.DeepEquals(a, b);
    }
}
