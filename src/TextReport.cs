using System.Text;

namespace Ostrasort;

/// <summary>Plain-text render of a full analysis - for clipboard export and saved reports.</summary>
public static class TextReport
{
    public static string Build(GameEnv env, EngineState s, string version)
    {
        var a = s.Analysis;
        var sb = new StringBuilder();
        sb.AppendLine($"Ostrasort v{version} report");
        sb.AppendLine($"generated for game version {env.InstalledVersion ?? "unknown"} at {env.GameRoot}");
        sb.AppendLine($"mods folder: {env.ModsDir}");
        sb.AppendLine($"core: {s.Scanner.CoreIndex.Count:N0} data objects across {s.Scanner.CoreTypes} types");
        sb.AppendLine();

        if (a.Ffu is { } ffu)
        {
            sb.AppendLine(ffu.AutoloaderActive
                ? "FFU / OSTRAAUTOLOADER: the autoloader is ACTIVE - Ostrasort is read-only on this install"
                : $"FFU: {ffu.Summary} detected (supported - FFU ordering rules applied)");
            foreach (var e in ffu.Evidence) sb.AppendLine($"  - {e}");
            foreach (var n in FfuAnalysis.Notices(a)) sb.AppendLine($"  {n}");
            sb.AppendLine();
        }

        sb.AppendLine($"MODS ({a.Registered.Count} registered)");
        var rows = a.Registered.Select((m, i) => (Pos: (i + 1).ToString(), Mod: m))
            .Concat(a.UnregisteredLocal.Concat(a.UnregisteredWorkshop).Select(m => (Pos: "-", Mod: m)));
        foreach (var (pos, m) in rows)
        {
            var cls = m.Kind == EntryKind.Core ? "core" : m.IsPatch ? "patch" : m.Class.ToString().ToLowerInvariant();
            var notes = m.NoteLines(env.InstalledVersion);
            var name = m.Kind == EntryKind.Core ? "core" : m.DisplayName ?? m.Name;
            var id = m.WorkshopId is { } w ? $" [{w}]" : "";
            sb.AppendLine($"  {pos,3}  {name}{id}  ({cls}{(m.DataObjects > 0 ? $", {m.DataObjects} objs/{m.CoreOverrides} ovr" : "")})" +
                          (notes.Count > 0 ? $"  !! {string.Join("; ", notes)}" : ""));
        }
        sb.AppendLine();

        sb.AppendLine($"COLLISIONS ({a.Collisions.Count})");
        if (a.Collisions.Count == 0) sb.AppendLine("  none - no two mods claim the same object");
        foreach (var col in a.Collisions)
        {
            sb.AppendLine($"  {col.Key} - claimed by {string.Join(" THEN ", col.Claimants.Select(m => m.DisplayName ?? m.Name))}");
            if (col.FriendlyName is { Length: > 0 } fn) sb.AppendLine($"    ({fn})");
            foreach (var p in col.Pairs)
                sb.AppendLine("    " + PairText(col, p));
            foreach (var n in col.FieldNotes) sb.AppendLine($"    {n}");
        }
        sb.AppendLine();

        sb.AppendLine("PATCH");
        sb.AppendLine("  " + (!s.Patch.Exists
            ? (a.HasUnresolvedConflicts ? "none generated - conflicts above are unmerged" : "none needed")
            : s.Patch.Stale ? $"STALE: {string.Join("; ", s.Patch.StaleReasons)}"
            : s.Patch.Obsolete ? "installed but no longer needed - remove it" +
              (s.Patch.UnneededKeys.Count > 0 ? $" (resolved upstream: {string.Join(", ", s.Patch.UnneededKeys)})" : "")
            : $"fresh (v{s.Patch.ToolVersion ?? "?"}) covering {string.Join(", ", s.Patch.CoveredKeys)}" +
              (s.Patch.ExcludedCount > 0 ? $"; {s.Patch.ExcludedCount} item(s) excluded by you" : "")));
        if (s.Patch.Exists && !s.Patch.Stale && !s.Patch.Obsolete && s.Patch.UnneededKeys.Count > 0)
            sb.AppendLine($"  no longer needed for {string.Join(", ", s.Patch.UnneededKeys)} (resolved upstream)");
        sb.AppendLine();

        var jsonErrMods = a.AllMods.Where(m => m.JsonErrors.Count > 0).ToList();
        var warnTotal = a.Warnings.Count + jsonErrMods.Sum(m => m.JsonErrors.Count);
        sb.AppendLine($"WARNINGS ({warnTotal})");
        if (warnTotal == 0) sb.AppendLine("  none");
        foreach (var w in a.Warnings) sb.AppendLine($"  ! {w}");
        foreach (var m in jsonErrMods)
            foreach (var e in m.JsonErrors) sb.AppendLine($"  ! {m.DisplayName ?? m.Name}: {e}");
        sb.AppendLine();

        if (!a.OrderChanged)
            sb.AppendLine("SUGGESTED ORDER: no changes - the current order satisfies every rule.");
        else
        {
            sb.AppendLine($"SUGGESTED ORDER ({a.Changes.Count} change(s))");
            foreach (var c in a.Changes) sb.AppendLine($"  {c.Action,-7} {c.Entry}   ({c.Reason})");
            sb.AppendLine("  resulting aLoadOrder:");
            for (var i = 0; i < a.SuggestedOrder.Count; i++)
                sb.AppendLine($"    {i + 1,2}. {a.SuggestedOrder[i]}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// One pair's outcome, honouring the collision-level classification the
    /// console/GUI view uses - a benign merged-at-load collision must never
    /// read as "last loaded replaces the object entirely" in an exported report.
    /// </summary>
    internal static string PairText(Collision col, PairRelation p) => p.Rel switch
    {
        Relation.SupersetOk => $"OK: {p.Later.DisplayName ?? p.Later.Name} is a superset (+{p.AddedByLater.Length} items)",
        Relation.Equal => "OK: identical item sets, quantities last-wins",
        Relation.SubsetViolation => $"WRONG ORDER: {p.LostFromEarlier.Length} item(s) dropped - superset must load last",
        Relation.Partial when col.ResolvedByPatch => "RESOLVED by the Ostrasort Patch",
        Relation.Partial => $"CONFLICT: {p.Later.DisplayName ?? p.Later.Name} drops {string.Join(", ", p.LostFromEarlier)}",
        _ when col.ResolvedByPatch => "RESOLVED by the Ostrasort Patch (field merge)",
        _ when col.FfuMergedAtLoad => "merged by FFU at load - nothing lost",
        _ when col.AdditiveAtLoad => "merged entry-by-entry at load - nothing lost",
        _ when col.ObjectMergeable => "mergeable field-by-field - resolvable with the Ostrasort Patch",
        _ when col.NothingLost => "nothing lost (identical versions, or the last-loaded includes every change)",
        _ => "last loaded replaces the object entirely",
    };
}
