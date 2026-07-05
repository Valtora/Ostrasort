using System.IO;

namespace Ostrasort;

/// <summary>
/// Human-readable rendering of the load-order suggestion: the reasons for each
/// change, then the current order and the suggested order side by side with
/// friendly mod names, so it is obvious what moves where. Shared by the GUI
/// Order-changes tab and the console report (and unit-testable).
/// </summary>
public static class OrderChangeView
{
    private const int NameWidth = 40;

    public static List<ViewLine> Build(Analysis a)
    {
        var lines = new List<ViewLine>();
        if (!a.OrderChanged)
        {
            lines.Add(new ViewLine("The current load order satisfies every rule — nothing to change.", LineSev.Good, 0));
            return lines;
        }

        // why
        lines.Add(new ViewLine($"{a.Changes.Count} change(s):", LineSev.Normal, 0, true));
        foreach (var ch in a.Changes)
        {
            var sev = ch.Action switch { "add" => LineSev.Good, "remove" => LineSev.Bad, _ => LineSev.Warn };
            lines.Add(new ViewLine($"{ch.Action.ToUpperInvariant(),-7} {Friendly(a, ch.Entry)}", sev, 1));
            lines.Add(new ViewLine(ch.Reason, LineSev.Dim, 2));
        }
        lines.Add(new ViewLine("", LineSev.Normal, 0));

        // before vs after, side by side
        var cur = a.Registered.Select(m => m.Raw).ToList();
        var sug = a.SuggestedOrder;
        lines.Add(new ViewLine(Row("#", "current order", "#", "suggested order"), LineSev.Normal, 0, true));
        var n = Math.Max(cur.Count, sug.Count);
        for (var i = 0; i < n; i++)
        {
            var lRaw = i < cur.Count ? cur[i] : null;
            var rRaw = i < sug.Count ? sug[i] : null;
            var line = Row(
                lRaw is null ? "" : (i + 1).ToString(), lRaw is null ? "" : Friendly(a, lRaw),
                rRaw is null ? "" : (i + 1).ToString(), rRaw is null ? "" : Friendly(a, rRaw));
            var sev = string.Equals(lRaw, rRaw, StringComparison.Ordinal) ? LineSev.Dim : LineSev.Warn;
            lines.Add(new ViewLine(line, sev, 0));
        }
        return lines;
    }

    private static string Row(string ln, string left, string rn, string right) =>
        $"{ln,2}  {Trunc(left),-NameWidth}     {rn,2}  {Trunc(right)}";

    private static string Trunc(string s) =>
        s.Length <= NameWidth ? s : s[..(NameWidth - 1)] + "…";

    /// <summary>A friendly name for a raw aLoadOrder entry (workshop id / local folder -> display name).</summary>
    public static string Friendly(Analysis a, string raw)
    {
        if (raw == "core" || raw == "(whole list)") return raw;
        var b = raw.Split('|')[0];   // strip |edit / |disabled markers
        var suffix = raw.Contains("|disabled", StringComparison.Ordinal) ? " (disabled)" : "";

        if (b.Length > 2 && b[1] == ':')   // absolute path -> workshop item
        {
            var id = Path.GetFileName(b.TrimEnd('\\', '/'));
            var m = a.AllMods.FirstOrDefault(x => x.Kind == EntryKind.Workshop && x.Name == id);
            return (m?.DisplayName is { Length: > 0 } d ? d : id) + suffix;
        }
        var lm = a.AllMods.FirstOrDefault(x => x.Kind == EntryKind.Local &&
                                               string.Equals(x.Name, b, StringComparison.OrdinalIgnoreCase));
        return (lm?.DisplayName is { Length: > 0 } dl ? dl : b) + suffix;
    }
}
