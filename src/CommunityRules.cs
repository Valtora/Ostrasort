using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ostrasort;

/// <summary>
/// Shared knowledge about specific mods that no mod states about itself:
/// load-after rules, incompatibilities, builds of the game a mod is broken
/// on, replacements, and FFU or final-say tags for mods that ship no marker.
/// Modelled on RimSort's community rules database.
///
/// The file lives in the Ostrasort repository (rules/community-rules.json) so
/// it can be corrected without a release. A copy ships inside the app, and the
/// GUI fetches the live file at most once a day into %APPDATA%\Ostrasort. The
/// newer of the two (by its "updated" date) wins. Keys are Workshop item ids;
/// a local copy of a Workshop mod matches through the strWorkshopID in its
/// mod_info.json.
///
/// The player's own choices always win: a pinned load priority is never
/// replaced, and an FFU tag can only move a mod later, never earlier.
/// </summary>
public sealed class CommunityRules
{
    public const string RemoteUrl = "https://raw.githubusercontent.com/Valtora/Ostrasort/master/rules/community-rules.json";
    private const string ResourceName = "Ostrasort.community-rules.json";
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(24);

    public int Version { get; set; } = 1;
    public string Updated { get; set; } = "";
    public Dictionary<string, ModRule> Mods { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static readonly CommunityRules Empty = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string CachePath => AppPaths.File("community-rules.json");

    /// <summary>The rule for one mod, by its Workshop id or the id its mod_info.json names.</summary>
    public ModRule? For(ModEntry m)
    {
        if (m.Kind == EntryKind.Workshop && Mods.TryGetValue(m.Name, out var r)) return r;
        return m.PublishedId is { Length: > 0 } id && Mods.TryGetValue(id, out r) ? r : null;
    }

    public static CommunityRules? Parse(string json)
    {
        try
        {
            var rules = JsonSerializer.Deserialize<CommunityRules>(json, Options);
            if (rules is null || rules.Version != 1) return null;
            rules.Mods = new Dictionary<string, ModRule>(rules.Mods, StringComparer.OrdinalIgnoreCase);
            return rules;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The newer of the downloaded copy and the copy built into the app.</summary>
    public static CommunityRules Load()
    {
        var bundled = Bundled();
        CommunityRules? cached = null;
        try { if (File.Exists(CachePath)) cached = Parse(File.ReadAllText(CachePath)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (cached is null) return bundled ?? Empty;
        if (bundled is null) return cached;
        return string.CompareOrdinal(cached.Updated, bundled.Updated) >= 0 ? cached : bundled;
    }

    private static CommunityRules? Bundled()
    {
        using var s = typeof(CommunityRules).Assembly.GetManifestResourceStream(ResourceName);
        if (s is null) return null;
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    /// <summary>
    /// Downloads the live file when the cached copy is over a day old. Returns
    /// true when the stored rules changed, so the caller can rescan. A failed
    /// or invalid download leaves the cached copy alone.
    /// </summary>
    public static async Task<bool> RefreshAsync(HttpClient http, CancellationToken token)
    {
        try
        {
            if (File.Exists(CachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) < RefreshEvery) return false;
            var text = await http.GetStringAsync(RemoteUrl, token).ConfigureAwait(false);
            if (Parse(text) is null) return false;
            var old = File.Exists(CachePath) ? await File.ReadAllTextAsync(CachePath, token).ConfigureAwait(false) : null;
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            AtomicFile.WriteAllText(CachePath, text);
            return old != text;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the installed game version matches a pattern: an exact
    /// version, a prefix ending in ".*" ("1.0.1.*"), or "*".
    /// </summary>
    public static bool VersionMatches(string pattern, string? installed)
    {
        if (installed is null) return false;
        pattern = pattern.Trim();
        if (pattern == "*") return true;
        if (pattern.EndsWith(".*", StringComparison.Ordinal))
            return installed.StartsWith(pattern[..^1], StringComparison.Ordinal);
        return installed == pattern;
    }

    /// <summary>
    /// Applies the rules that need no ordering pass: tiers (after the category
    /// pass, never over a pin), load-after edges, and the warnings for
    /// incompatible pairs, broken builds and replaced mods. FFU tags are applied
    /// inside <see cref="FfuAnalysis.Classify"/>.
    /// </summary>
    public void Apply(GameEnv env, Analysis a)
    {
        if (Mods.Count == 0) return;
        var loading = a.AllMods.Where(m => m.Kind != EntryKind.Core && m.Dir is not null && m.Loads).ToList();
        string Name(ModEntry m) => m.DisplayName ?? m.Name;
        ModEntry? Find(string id) => loading.FirstOrDefault(m =>
            (m.Kind == EntryKind.Workshop && string.Equals(m.Name, id, StringComparison.OrdinalIgnoreCase))
            || string.Equals(m.PublishedId, id, StringComparison.OrdinalIgnoreCase));

        var pairsWarned = new HashSet<(ModEntry, ModEntry)>();
        foreach (var m in loading)
        {
            if (For(m) is not { } rule) continue;
            if (rule.Note is { Length: > 0 } note) m.CommunityNotes.Add(note);

            if (!m.CategoryManual && !m.IsFfu && ParseTier(rule.Tier) is { } tier && tier != m.Tier)
            {
                m.Tier = tier;
                m.CategorySignals.Insert(0, $"the community rules set its load priority to {CategoryAnalysis.Label(tier)}");
            }

            foreach (var id in rule.LoadAfter)
                if (Find(id) is { } dep && dep != m) m.LoadAfter.Add(dep);

            foreach (var x in rule.IncompatibleWith)
                if (Find(x.Id) is { } other && other != m && pairsWarned.Add((other, m)) && pairsWarned.Add((m, other)))
                {
                    var why = x.Reason is { Length: > 0 } r ? $" ({r})" : "";
                    a.Warnings.Add($"'{Name(m)}' and '{Name(other)}' are known not to work together{why} - turn one of them off");
                    m.CommunityNotes.Add($"does not work with {Name(other)}{why}");
                }

            if (rule.BrokenOn.FirstOrDefault(p => VersionMatches(p, env.InstalledVersion)) is not null)
            {
                var why = rule.BrokenNote is { Length: > 0 } r ? $" ({r})" : "";
                a.Warnings.Add($"'{Name(m)}' is known to be broken on game {env.InstalledVersion}{why} - " +
                               "turn it off until the mod is updated");
                m.CommunityNotes.Add($"broken on game {env.InstalledVersion}{why}");
            }

            if (rule.ReplacedBy is { Id.Length: > 0 } rep)
            {
                var what = rep.Name ?? $"Workshop item {rep.Id}";
                var why = rep.Note is { Length: > 0 } r ? $" ({r})" : "";
                a.Warnings.Add($"'{Name(m)}' has been replaced by {what}{why} - consider switching to it");
                m.CommunityNotes.Add($"replaced by {what}{why}");
            }
        }
    }

    private static LoadTier? ParseTier(string? tier) => tier?.Trim().ToLowerInvariant() switch
    {
        "early" => LoadTier.Early,
        "normal" => LoadTier.Normal,
        "late" or "finalsay" or "final-say" => LoadTier.Late,
        _ => null,
    };
}

public sealed class ModRule
{
    /// <summary>The mod's name, for people reading the file. Not used for matching.</summary>
    public string? Name { get; set; }
    /// <summary>Loads in the FFU block even though it ships no marker.</summary>
    public bool Ffu { get; set; }
    /// <summary>"early", "normal" or "late" (final say).</summary>
    public string? Tier { get; set; }
    /// <summary>Workshop ids this mod must load after.</summary>
    public List<string> LoadAfter { get; set; } = new();
    public List<Incompatibility> IncompatibleWith { get; set; } = new();
    /// <summary>Game versions it is broken on: "1.0.1.8", "1.0.1.*" or "*".</summary>
    public List<string> BrokenOn { get; set; } = new();
    public string? BrokenNote { get; set; }
    public Replacement? ReplacedBy { get; set; }
    public string? Note { get; set; }
}

public sealed class Incompatibility
{
    public string Id { get; set; } = "";
    public string? Reason { get; set; }
}

public sealed class Replacement
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Note { get; set; }
}
