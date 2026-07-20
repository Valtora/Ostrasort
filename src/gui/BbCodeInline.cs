using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Ostrasort.Gui;

/// <summary>
/// Attaches BBCode (the markup Steam Workshop pages and mod_info descriptions use)
/// to a <see cref="TextBlock"/>, rendered as real WPF inlines: bold/italic/underline/
/// strike, sized headers, bullet lists, and underlined link text. Unknown tags
/// degrade to their inner text. Set <c>gui:BbCodeInline.Source</c> to the raw
/// string; the tooltip in the mod table uses this for its rich description.
/// </summary>
public static partial class BbCodeInline
{
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.RegisterAttached("Source", typeof(string), typeof(BbCodeInline),
            new PropertyMetadata(null, OnSourceChanged));

    public static string? GetSource(DependencyObject o) => (string?)o.GetValue(SourceProperty);
    public static void SetSource(DependencyObject o, string? v) => o.SetValue(SourceProperty, v);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        var raw = e.NewValue as string;
        if (string.IsNullOrWhiteSpace(raw)) return;
        var baseSize = tb.FontSize > 0 ? tb.FontSize : 12.0;
        foreach (var inline in Render(raw!, baseSize))
            tb.Inlines.Add(inline);
    }

    [GeneratedRegex(@"\[img\].*?\[/img\]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ImgTag();

    // pull the surrounding whitespace into the [*] token so a bullet is exactly one line
    [GeneratedRegex(@"\s*\[\*\]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyNewlines();

    // split on any tag while keeping the tags as tokens
    [GeneratedRegex(@"(\[/?[a-z0-9]+(?:=[^\]]*)?\])", RegexOptions.IgnoreCase)]
    private static partial Regex Tokens();

    private const string Bullet = "";   // internal marker a normalised [*] becomes

    /// <summary>Parse BBCode into a flat list of WPF inlines.</summary>
    private static List<Inline> Render(string raw, double baseSize)
    {
        var s = raw.Replace("\r\n", "\n").Replace("\r", "\n");
        s = ImgTag().Replace(s, "");
        s = ListItem().Replace(s, Bullet);
        s = ManyNewlines().Replace(s, "\n\n");
        s = s.Trim();

        // nesting counters, so overlapping/duplicate tags cannot go negative
        int bold = 0, italic = 0, under = 0, strike = 0, link = 0;
        var headers = new Stack<double>();   // active header size multipliers

        var inlines = new List<Inline>();

        void AddText(string text)
        {
            if (text.Length == 0) return;
            var run = new Run(WebUtility.HtmlDecode(text));
            if (bold > 0 || headers.Count > 0) run.FontWeight = FontWeights.Bold;
            if (italic > 0) run.FontStyle = FontStyles.Italic;
            var deco = new TextDecorationCollection();
            if (under > 0 || link > 0) deco.Add(TextDecorations.Underline);
            if (strike > 0) deco.Add(TextDecorations.Strikethrough);
            if (deco.Count > 0) run.TextDecorations = deco;
            if (headers.Count > 0) run.FontSize = baseSize * headers.Peek();
            inlines.Add(run);
        }

        // text may carry newlines and bullet markers; emit LineBreaks for both
        void AddChunk(string chunk)
        {
            var first = true;
            foreach (var line in chunk.Split('\n'))
            {
                if (!first) inlines.Add(new LineBreak());
                first = false;
                var parts = line.Split(Bullet[0]);
                for (var i = 0; i < parts.Length; i++)
                {
                    if (i > 0) { inlines.Add(new LineBreak()); AddText("• "); }
                    AddText(parts[i]);
                }
            }
        }

        foreach (var tok in Tokens().Split(s))
        {
            if (tok.Length == 0) continue;
            if (tok[0] != '[') { AddChunk(tok); continue; }

            var closing = tok.StartsWith("[/", StringComparison.Ordinal);
            var body = tok[(closing ? 2 : 1)..^1];
            var name = body.Split('=', 2)[0].ToLowerInvariant();

            switch (name)
            {
                case "b": bold += closing ? -1 : 1; break;
                case "i": italic += closing ? -1 : 1; break;
                case "u": under += closing ? -1 : 1; break;
                case "strike" or "s": strike += closing ? -1 : 1; break;
                case "url": link += closing ? -1 : 1; break;
                case "h1": HeaderTag(1.5); break;
                case "h2": HeaderTag(1.3); break;
                case "h3": HeaderTag(1.15); break;
                // structural / unstyled: swallow the tag, keep the inner text
                default: break;
            }

            void HeaderTag(double mult)
            {
                if (closing) { if (headers.Count > 0) headers.Pop(); }
                else headers.Push(mult);
            }
        }

        // guard against a stray leading/trailing LineBreak from normalisation
        while (inlines.Count > 0 && inlines[0] is LineBreak) inlines.RemoveAt(0);
        while (inlines.Count > 0 && inlines[^1] is LineBreak) inlines.RemoveAt(inlines.Count - 1);
        return inlines;
    }
}
