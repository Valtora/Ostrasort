using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ostrasort;

/// <summary>
/// Checks each loading mod's own data for mistakes its author can fix: a name
/// it references that nothing loaded defines, and schema errors of a kind the
/// game's own data never has. Most of these silently do nothing in game (a
/// misspelt field is ignored, an item pointing at a missing sprite definition
/// shows wrong), so they are findings for the mod's author, shown in the mod's
/// detail panel with a report to send, never on the health card.
///
/// Only references that always resolve in the base game are checked; fields
/// the game itself leaves dangling (some aInteractions, some aStartingConds)
/// would only produce noise. Schema errors are compared against a baseline of
/// what vanilla data breaks (cached per core version): the game ships data that
/// ignores its own schemas in places, so only an error shape vanilla never has
/// counts as a mod's mistake.
/// </summary>
public static class ModDataCheck
{
    /// <summary>Reference fields that resolve 100% in core data (measured on game 1.0.1.8).</summary>
    private static readonly (string Type, string Field, string Target)[] References =
    {
        ("condowners", "strItemDef", "items"),
        ("condowners", "strLoot", "loot"),
        ("condowners", "aTickers", "tickers"),
        ("condowners", "jsonPI", "powerinfos"),
        ("condowners", "strContainerCT", "condtrigs"),
        ("cooverlays", "strCOBase", "condowners"),
        ("cooverlays", "strCondLoot", "loot"),
        ("items", "aLights", "lights"),
    };

    private static readonly string[] SchemaTypes = { "conditions", "condowners", "interactions", "items", "plots", "plot_beats" };

    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static void Annotate(GameEnv env, Analysis a, Scanner scanner)
    {
        var mods = a.AllMods.Where(m => m.Loads && m.Dir is not null && !m.IsPatch && m.Kind != EntryKind.Core).ToList();
        if (mods.Count == 0) return;
        var defined = new HashSet<(string, string)>(scanner.CoreIndex);
        foreach (var m in mods) defined.UnionWith(m.Claims.Keys);
        var validator = new SchemaValidator(env);
        Lazy<Dictionary<string, HashSet<string>>> baseline = new(() => Baseline(env, validator));
        var partialEdits = a.Ffu is { FrameworkPresent: true };
        foreach (var m in mods)
            Check(m, Path.Combine(m.Dir!, "data"), defined, validator, baseline, partialEdits || m.IsFfu, a.IgnorePatterns);
    }

    /// <summary>The check for one mod's data folder, without the install around it (for tests).</summary>
    public static void Check(ModEntry m, string dataDir, HashSet<(string, string)> defined, SchemaValidator validator,
        Lazy<Dictionary<string, HashSet<string>>> baseline, bool partialEdits, IReadOnlyList<string>? ignorePatterns = null)
    {
        foreach (var type in References.Select(r => r.Type).Concat(SchemaTypes).Distinct())
        {
            var dir = Path.Combine(dataDir, type);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
            {
                if (Scanner.IsIgnored(file, ignorePatterns, out _)) continue;
                var rel = Path.GetRelativePath(dataDir, file).Replace('\\', '/');
                JsonDocument doc;
                try { doc = GameJson.Parse(File.ReadAllText(file), Lenient); }
                catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { continue; }   // reported as a JSON problem already
                using (doc)
                {
                    var objs = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new() { doc.RootElement };
                    foreach (var obj in objs.Where(o => o.ValueKind == JsonValueKind.Object))
                    {
                        var name = obj.TryGetProperty("strName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "(no strName)";
                        // a clone fills its missing fields from the object it names, so it is never checked alone
                        var clone = obj.TryGetProperty("strReference", out var r) && r.ValueKind == JsonValueKind.String;

                        foreach (var (rt, field, target) in References.Where(x => x.Type == type))
                            foreach (var value in Values(obj, field))
                                if (!defined.Contains((target, value)))
                                    m.DataProblems.Add($"{rel}: {name}: {field} names {target} \"{value}\", which nothing loaded defines");

                        if (clone || !SchemaTypes.Contains(type)) continue;
                        var known = baseline.Value.GetValueOrDefault(type) ?? new HashSet<string>();
                        foreach (var err in validator.Validate(obj, type))
                        {
                            // a partial object (FFU field merge) leaves out what it does not change
                            if (partialEdits && err.Contains("missing required property", StringComparison.Ordinal)) continue;
                            if (known.Contains(Signature(err))) continue;
                            m.DataProblems.Add($"{rel}: {name}: {err}");
                        }
                    }
                }
            }
        }
    }

    private static IEnumerable<string> Values(JsonElement obj, string field)
    {
        if (!obj.TryGetProperty(field, out var v)) yield break;
        if (v.ValueKind == JsonValueKind.String) { if (v.GetString() is { Length: > 0 } s && s != "Blank") yield return s; }
        else if (v.ValueKind == JsonValueKind.Array)
            foreach (var e in v.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s && s != "Blank") yield return s;
    }

    private static readonly Regex Index = new(@"\[\d+\]", RegexOptions.Compiled);

    /// <summary>
    /// The shape of a schema error, without array positions or the offending
    /// value: "aConds[]: expected string, got number", "(root): unknown property
    /// \"strFoo\"". Vanilla having the same shape means the game tolerates it.
    /// </summary>
    public static string Signature(string error)
    {
        var s = Index.Replace(error, "[]");
        var colon = s.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0) return s;
        var path = s[..colon];
        var rest = s[(colon + 2)..];
        if (rest.Contains("is not one of the allowed values", StringComparison.Ordinal)) return path + ": not an allowed value";
        if (rest.Contains("does not match pattern", StringComparison.Ordinal)) return path + ": pattern";
        if (rest.Contains("the minimum", StringComparison.Ordinal) || rest.Contains("the maximum", StringComparison.Ordinal)) return path + ": range";
        return s;
    }

    // ---------------------------------------------------- vanilla baseline ---

    /// <summary>The schema-error shapes core data itself has, per type. Cached per core version.</summary>
    private static Dictionary<string, HashSet<string>> Baseline(GameEnv env, SchemaValidator validator)
    {
        var fingerprint = CoreHistory.FingerprintText(CoreIndexCache.FingerprintOf(env.CoreDataDir));
        var path = AppPaths.File("schema-baseline-" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(env.CoreDataDir.ToLowerInvariant())))[..12] + ".json");
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<BaselineFile>(File.ReadAllText(path)) is { } cached
                && cached.Fingerprint == fingerprint && cached.Parser == GameJson.ParserVersion)
                return cached.Shapes.ToDictionary(kv => kv.Key, kv => kv.Value.ToHashSet(StringComparer.Ordinal));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }

        var shapes = new Dictionary<string, HashSet<string>>();
        foreach (var type in SchemaTypes)
        {
            var set = shapes[type] = new HashSet<string>(StringComparer.Ordinal);
            var dir = Path.Combine(env.CoreDataDir, type);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    using var doc = GameJson.Parse(File.ReadAllText(file), Lenient);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                    foreach (var obj in doc.RootElement.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.Object))
                        foreach (var err in validator.Validate(obj, type)) set.Add(Signature(err));
                }
                catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
            }
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new BaselineFile
            {
                Fingerprint = fingerprint, Parser = GameJson.ParserVersion,
                Shapes = shapes.ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(s => s, StringComparer.Ordinal).ToList()),
            }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return shapes;
    }

    private sealed class BaselineFile
    {
        public string Fingerprint { get; set; } = "";
        public int Parser { get; set; }
        public Dictionary<string, List<string>> Shapes { get; set; } = new();
    }

    /// <summary>A ready-to-send note for the mod's author, listing what was found.</summary>
    public static string AuthorReport(ModEntry m, string? gameVersion)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Hi, Ostrasort found {m.DataProblems.Count} possible problem(s) in the data of {m.DisplayName ?? m.Name}" +
                      (m.ModVersion is { } v ? $" (version {v})" : "") + (gameVersion is { } g ? $" on Ostranauts {g}" : "") + ":");
        sb.AppendLine();
        foreach (var p in m.DataProblems.Take(50)) sb.AppendLine("- " + p);
        if (m.DataProblems.Count > 50) sb.AppendLine($"- and {m.DataProblems.Count - 50} more");
        sb.AppendLine();
        sb.AppendLine("A name that nothing defines is usually a typo or a file left out of the upload. A schema error means a field " +
                      "has a different name or type than the game's schema expects, so the game may ignore it.");
        return sb.ToString();
    }
}
