using System.Text;

namespace Ostrasort;

/// <summary>
/// GitHub-flavoured-markdown render of a full analysis — used for the clipboard
/// export, saved <c>*.md</c> reports, and the body of a "Report a bug" issue.
/// Workshop mods link to their Steam Workshop page so a maintainer can open any
/// listed mod in one click.
/// </summary>
public static class MarkdownReport
{
    public static string WorkshopUrl(string id) =>
        $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}";

    public static string Build(GameEnv env, EngineState s, string version)
    {
        var a = s.Analysis;
        var sb = new StringBuilder();
        sb.AppendLine($"# Ostrasort v{version} report");
        sb.AppendLine();
        sb.AppendLine($"- Game version: {env.InstalledVersion ?? "unknown"}");
        sb.AppendLine($"- Game folder: `{env.GameRoot}`");
        sb.AppendLine($"- Mods folder: `{env.ModsDir}`");
        sb.AppendLine($"- Core: {s.Scanner.CoreIndex.Count:N0} data objects across {s.Scanner.CoreTypes} types");
        sb.AppendLine();

        if (a.Ffu is { } ffu)
        {
            sb.AppendLine("## FFU");
            sb.AppendLine();
            sb.AppendLine(ffu.AutoloaderActive
                ? "**OstraAutoloader is ACTIVE** — Ostrasort is read-only on this install."
                : $"{ffu.Summary} detected (supported — FFU ordering rules applied).");
            sb.AppendLine();
            foreach (var e in ffu.Evidence) sb.AppendLine($"- {Md(e)}");
            foreach (var n in FfuAnalysis.Notices(a)) sb.AppendLine($"- {Md(n)}");
            sb.AppendLine();
        }

        sb.AppendLine($"## Mods ({a.Registered.Count} registered)");
        sb.AppendLine();
        sb.AppendLine("| # | Mod | Version | Source | Class | Data | Notes |");
        sb.AppendLine("|--:|-----|---------|--------|-------|------|-------|");
        var rows = a.Registered.Select((m, i) => (Pos: (i + 1).ToString(), Mod: m))
            .Concat(a.UnregisteredLocal.Concat(a.UnregisteredWorkshop).Select(m => (Pos: "–", Mod: m)));
        foreach (var (pos, m) in rows)
        {
            var cls = m.Kind == EntryKind.Core ? "core" : m.IsPatch ? "patch" : m.Class.ToString().ToLowerInvariant();
            var notes = m.NoteLines(env.InstalledVersion);

            var rawName = m.Kind == EntryKind.Core ? "core" : m.DisplayName ?? m.Name;
            var name = m.Kind == EntryKind.Workshop && m.WorkshopId is { } w
                ? $"[{Md(rawName)}]({WorkshopUrl(w)})"
                : Md(rawName);
            var source = m.Kind switch
            {
                EntryKind.Core => "game",
                EntryKind.Workshop => "Workshop",
                EntryKind.PluginDir => "plugins",
                _ when m.IsPatch => "generated",
                _ => "local",
            };
            var data = m.Kind == EntryKind.Core ? $"{s.Scanner.CoreIndex.Count:N0} objects"
                     : m.DataObjects > 0 ? $"{m.DataObjects} objs / {m.CoreOverrides} ovr" : "–";
            var modVersion = m.Kind == EntryKind.Core ? "–" : m.ModVersion ?? "–";
            sb.AppendLine($"| {pos} | {name} | {Md(modVersion)} | {source} | {cls} | {data} | {Md(string.Join("; ", notes))} |");
        }
        sb.AppendLine();

        sb.AppendLine($"## Collisions ({a.Collisions.Count})");
        sb.AppendLine();
        if (a.Collisions.Count == 0) sb.AppendLine("None — no two mods claim the same object.");
        foreach (var col in a.Collisions)
        {
            sb.AppendLine($"- **{Md(col.Key)}** — claimed by {Md(string.Join(" → ", col.Claimants.Select(m => m.DisplayName ?? m.Name)))}"
                          + (col.FriendlyName is { Length: > 0 } fn ? $" _({Md(fn)})_" : ""));
            foreach (var p in col.Pairs)
                sb.AppendLine("  - " + Md(TextReport.PairText(col, p)));
            foreach (var n in col.FieldNotes) sb.AppendLine($"  - {Md(n)}");
        }
        sb.AppendLine();

        sb.AppendLine("## Patch");
        sb.AppendLine();
        sb.AppendLine(!s.Patch.Exists
            ? (a.HasUnresolvedConflicts ? "None generated — the conflicts above are unmerged." : "None needed.")
            : s.Patch.Stale ? $"**STALE:** {Md(string.Join("; ", s.Patch.StaleReasons))}"
            : s.Patch.Obsolete ? "Installed but no longer needed — remove it." +
              (s.Patch.UnneededKeys.Count > 0 ? $" (Resolved upstream: {Md(string.Join(", ", s.Patch.UnneededKeys))}.)" : "")
            : $"Fresh (v{s.Patch.ToolVersion ?? "?"}) covering {Md(string.Join(", ", s.Patch.CoveredKeys))}."
              + (s.Patch.ExcludedCount > 0 ? $" {s.Patch.ExcludedCount} item(s) excluded by you." : ""));
        if (s.Patch.Exists && !s.Patch.Stale && !s.Patch.Obsolete && s.Patch.UnneededKeys.Count > 0)
            sb.AppendLine($"No longer needed for {Md(string.Join(", ", s.Patch.UnneededKeys))} (resolved upstream).");
        sb.AppendLine();

        var jsonErrMods = a.AllMods.Where(m => m.JsonErrors.Count > 0).ToList();
        sb.AppendLine($"## Warnings ({a.Warnings.Count + jsonErrMods.Sum(m => m.JsonErrors.Count)})");
        sb.AppendLine();
        if (a.Warnings.Count == 0 && jsonErrMods.Count == 0) sb.AppendLine("None.");
        foreach (var w in a.Warnings) sb.AppendLine($"- {Md(w)}");
        foreach (var m in jsonErrMods)
            foreach (var e in m.JsonErrors) sb.AppendLine($"- {Md(m.DisplayName ?? m.Name)}: {Md(e)}");
        sb.AppendLine();

        if (!a.OrderChanged)
        {
            sb.AppendLine("## Suggested order");
            sb.AppendLine();
            sb.AppendLine("No changes — the current order satisfies every rule.");
        }
        else
        {
            sb.AppendLine($"## Suggested order ({a.Changes.Count} change(s))");
            sb.AppendLine();
            foreach (var c in a.Changes) sb.AppendLine($"- `{c.Action}` {Md(c.Entry)} — {Md(c.Reason)}");
            sb.AppendLine();
            sb.AppendLine("Resulting `aLoadOrder`:");
            sb.AppendLine();
            sb.AppendLine("```");
            for (var i = 0; i < a.SuggestedOrder.Count; i++)
                sb.AppendLine($"{i + 1,2}. {a.SuggestedOrder[i]}");
            sb.AppendLine("```");
        }
        return sb.ToString();
    }

    /// <summary>Escape the characters that would break a table cell or inline markdown in free text.</summary>
    private static string Md(string s) => s.Replace("\\", "\\\\").Replace("|", "\\|");
}
