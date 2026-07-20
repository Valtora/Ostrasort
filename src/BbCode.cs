using System.Net;
using System.Text.RegularExpressions;

namespace Ostrasort;

/// <summary>
/// Turns a Steam Workshop / mod_info description (BBCode, the markup Steam pages
/// use) into readable plain text for the mod table's Description column and its
/// hover tooltip. Best-effort: tags we do not special-case are simply dropped,
/// so an unknown tag degrades to its inner text rather than showing raw markup.
/// </summary>
public static partial class BbCode
{
    // [img]http://...[/img] carries a URL as its content, not prose - drop the lot.
    [GeneratedRegex(@"\[img\].*?\[/img\]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ImgTag();

    // list items become bullet lines
    [GeneratedRegex(@"\[\*\]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();

    // any remaining [tag], [tag=value] or [/tag]
    [GeneratedRegex(@"\[/?[a-z0-9]+(=[^\]]*)?\]", RegexOptions.IgnoreCase)]
    private static partial Regex AnyTag();

    // runs of inline spaces/tabs (e.g. left behind where a tag was removed) -> one
    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex TrailingSpace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyNewlines();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Cleaned, multi-line text (paragraph breaks kept) for the tooltip.</summary>
    public static string ToPlainText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Replace("\r\n", "\n").Replace("\r", "\n");
        s = ImgTag().Replace(s, "");
        s = ListItem().Replace(s, "\n• ");   // "• "
        s = AnyTag().Replace(s, "");
        s = WebUtility.HtmlDecode(s);              // &amp; &lt; &quot; ...
        s = MultiSpace().Replace(s, " ");
        s = TrailingSpace().Replace(s, "\n");
        s = ManyNewlines().Replace(s, "\n\n");
        return s.Trim();
    }

    /// <summary>The same text collapsed to a single line, for the (trimmed) table cell.</summary>
    public static string Flatten(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "" : Whitespace().Replace(text, " ").Trim();
}
