using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ostrasort;

/// <summary>
/// Reads JSON the way the GAME reads it, not the way the RFC defines it.
/// <para>
/// Ostranauts parses every data file with LitJson (<c>DataHandler</c> calls
/// <c>JsonMapper.ToObject</c>), whose lexer copies EVERY character inside a
/// string except <c>"</c> and <c>\</c>, accepts single-quoted strings, and
/// accepts a <c>\'</c> escape. System.Text.Json follows RFC 8259 and rejects
/// all three, with no option to allow them. So a mod whose <c>strNotes</c>
/// carries a pasted line break loads perfectly in game while Ostrasort reads
/// nothing from the file: no mod name, plus a JSON warning about a file that is
/// not the player's to fix and that the game never complains about.
/// </para>
/// <para>
/// <see cref="TryRelax"/> rewrites exactly those three constructs into their
/// strict equivalents and changes nothing else. It is a REPAIR PASS, run only
/// after a parse has already failed, so what it cannot fix stays broken and is
/// still reported. Trailing commas are deliberately NOT repaired: LitJson
/// errors on those too, so they are a real fault the game hits at load and the
/// hygiene report must keep flagging them.
/// </para>
/// </summary>
public static class GameJson
{
    private static readonly char[] LineEnds = ['\n', '\r'];

    /// <summary>
    /// Rewrites game-legal-but-RFC-invalid JSON into strict JSON. Returns false
    /// (and the text unchanged) when it holds none of those constructs, which
    /// means a re-parse would fail exactly as the first one did.
    /// </summary>
    public static bool TryRelax(string text, out string relaxed)
    {
        var sb = new StringBuilder(text.Length + 16);
        var changed = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            // Comments are stepped over whole rather than scanned, so that an
            // apostrophe in one is never read as the start of a string. They
            // are copied through: the game accepts them and ships them in its
            // own core data, and System.Text.Json skips them on request.
            if (c == '/' && i + 1 < text.Length && (text[i + 1] == '/' || text[i + 1] == '*'))
            {
                int end;
                if (text[i + 1] == '/')
                {
                    end = text.IndexOfAny(LineEnds, i + 2);
                }
                else
                {
                    var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = close < 0 ? -1 : close + 2;
                }
                if (end < 0) end = text.Length;   // unterminated - let the parser say so
                sb.Append(text, i, end - i);
                i = end - 1;
                continue;
            }

            if (c is not ('"' or '\'')) { sb.Append(c); continue; }

            // A string. LitJson ends it at the matching quote and takes every
            // other character verbatim, so re-emit it as a strict double-quoted
            // string with the same content.
            var quote = c;
            if (quote == '\'') changed = true;
            sb.Append('"');
            var closed = false;
            for (i++; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == quote) { closed = true; break; }

                if (ch == '\\' && i + 1 < text.Length)
                {
                    var esc = text[++i];
                    // \' is a LitJson escape strict JSON rejects; the game reads
                    // it as a bare apostrophe, so that is what gets written.
                    if (esc == '\'') { sb.Append('\''); changed = true; }
                    else if (esc == '"' && quote == '\'') sb.Append("\\\"");
                    else sb.Append('\\').Append(esc);
                    continue;
                }
                if (ch == '"')            // only reachable inside a single-quoted string
                {
                    sb.Append("\\\"");
                    changed = true;
                    continue;
                }
                if (ch < ' ')             // an unescaped control character: the common case
                {
                    sb.Append(Escape(ch));
                    changed = true;
                    continue;
                }
                sb.Append(ch);
            }
            // An unterminated string is left unterminated: it is a real fault,
            // and closing it here would turn a broken file into a parseable one.
            if (closed) sb.Append('"');
        }

        relaxed = changed ? sb.ToString() : text;
        return changed;
    }

    /// <summary>
    /// Parses as the game's loader would: strictly first, then again over the
    /// repaired text. A failure on the repaired text is a fault the game hits
    /// too, so that exception is the one callers see - never one about a
    /// construct the game accepts. Its reported line and byte position refer to
    /// the repaired copy, which differs from the file wherever a repair
    /// happened.
    /// </summary>
    public static JsonDocument Parse(string text, JsonDocumentOptions options)
    {
        try
        {
            return JsonDocument.Parse(text, options);
        }
        catch (JsonException)
        {
            if (!TryRelax(text, out var relaxed)) throw;
            return JsonDocument.Parse(relaxed, options);
        }
    }

    /// <summary>
    /// <see cref="Parse"/> for the mutable node model (the patcher's world).
    /// </summary>
    public static JsonNode? ParseNode(string text, JsonNodeOptions? nodeOptions, JsonDocumentOptions options)
    {
        try
        {
            return JsonNode.Parse(text, nodeOptions, options);
        }
        catch (JsonException)
        {
            if (!TryRelax(text, out var relaxed)) throw;
            return JsonNode.Parse(relaxed, nodeOptions, options);
        }
    }

    private static string Escape(char c) => c switch
    {
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        '\b' => "\\b",
        '\f' => "\\f",
        _ => "\\u" + ((int)c).ToString("x4"),
    };
}
