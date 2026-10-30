using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ostrasort;

/// <summary>A mod list as shared between players: what to load, in what order, independent of anyone's folders.</summary>
public sealed class SharedList
{
    public int OstrasortList { get; set; } = 1;
    public string Name { get; set; } = "";
    public string? GameVersion { get; set; }
    public List<SharedMod> Mods { get; set; } = new();
}

public sealed class SharedMod
{
    /// <summary>The Workshop item: a subscription, or the id a local mod was published under.</summary>
    public string? WorkshopId { get; set; }
    /// <summary>The folder name of a local mod, or of a data mod under BepInEx\plugins.</summary>
    public string? Folder { get; set; }
    /// <summary>True when <see cref="Folder"/> lives under BepInEx\plugins rather than Mods.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Plugins { get; set; }
    public string Name { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Disabled { get; set; }
}

/// <summary>
/// Exports a profile as a file or a short code, and turns a shared list or a
/// Steam Workshop collection back into a profile for this install. Mods are
/// matched by Workshop id first, then by folder name, since absolute paths
/// differ between machines. What this install lacks is reported, with links
/// for Workshop items; Ostrasort cannot subscribe for the player.
/// </summary>
public static class ModListSharing
{
    public const string CodePrefix = "OSTRASORT1:";
    public const string FileExtension = ".ostrasort-list.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions Compact = new(Json) { WriteIndented = false };

    public static SharedList FromProfile(Profile p, GameEnv env, Analysis a)
    {
        var list = new SharedList { Name = p.Name, GameVersion = p.SavedGameVersion ?? env.InstalledVersion };
        foreach (var e in p.Entries)
        {
            var parts = e.Raw.Split('|');
            var path = parts[0];
            if (path == "core") continue;
            var disabled = (parts.Length == 2 && parts[1] == "disabled") || parts.Length >= 3;
            var mod = new SharedMod { Name = e.DisplayName, Disabled = disabled };
            if (path.Length > 2 && path[1] == ':')
            {
                var folder = Path.GetFileName(path.TrimEnd('\\', '/'));
                var underBep = path.Replace('/', '\\').StartsWith(env.BepInExDir.Replace('/', '\\') + "\\", StringComparison.OrdinalIgnoreCase);
                if (underBep) { mod.Folder = folder; mod.Plugins = true; }
                else mod.WorkshopId = folder;
            }
            else
            {
                mod.Folder = path;
                mod.WorkshopId = a.AllMods.FirstOrDefault(m => m.Kind == EntryKind.Local
                    && string.Equals(m.Name, path, StringComparison.OrdinalIgnoreCase))?.PublishedId;
            }
            list.Mods.Add(mod);
        }
        return list;
    }

    public static SharedList FromCollection(string name, IEnumerable<string> ids, IReadOnlyDictionary<string, WorkshopDetails>? details) =>
        new()
        {
            Name = name,
            Mods = ids.Select(id => new SharedMod
            {
                WorkshopId = id,
                Name = details is not null && details.TryGetValue(id, out var d) && d.Title is { Length: > 0 } t ? t : $"Workshop item {id}",
            }).ToList(),
        };

    public static string ToJson(SharedList list) => JsonSerializer.Serialize(list, Json);

    /// <summary>The list as one line of text for a chat message: compressed JSON, base64url.</summary>
    public static string ToCode(SharedList list)
    {
        var raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(list, Compact));
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.SmallestSize)) z.Write(raw);
        return CodePrefix + Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>A shared list from a file's text or a code. Null when it is neither.</summary>
    public static SharedList? Parse(string text)
    {
        text = text.Trim();
        try
        {
            if (text.StartsWith(CodePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var b64 = text[CodePrefix.Length..].Trim().Replace('-', '+').Replace('_', '/');
                b64 += new string('=', (4 - b64.Length % 4) % 4);
                using var z = new DeflateStream(new MemoryStream(Convert.FromBase64String(b64)), CompressionMode.Decompress);
                using var r = new StreamReader(z, Encoding.UTF8);
                text = r.ReadToEnd();
            }
            var list = JsonSerializer.Deserialize<SharedList>(text, Json);
            return list is { OstrasortList: 1 } ? list : null;
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidDataException or IOException) { return null; }
    }

    /// <summary>
    /// The profile this install can build from a shared list, and the mods it
    /// lacks. Core always comes first; the generated patch is never part of a
    /// list (Ostrasort re-derives it after a switch).
    /// </summary>
    public static (Profile Profile, List<SharedMod> Missing) Resolve(SharedList list, GameEnv env, Analysis a, string profileName)
    {
        var entries = new List<ProfileEntry> { new("core", "core") };
        var missing = new List<SharedMod>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in list.Mods)
        {
            var path = Locate(mod, env, a);
            if (path is null) { missing.Add(mod); continue; }
            if (!seen.Add(path)) continue;
            var local = !(path.Length > 2 && path[1] == ':');
            var raw = mod.Disabled ? path + "|disabled" : local ? path + "|edit" : path;
            entries.Add(new ProfileEntry(raw, mod.Name));
        }
        var profile = new Profile
        {
            Name = profileName, SavedAt = DateTime.Now.ToString("o"), SavedGameVersion = list.GameVersion, Entries = entries,
        };
        return (profile, missing);
    }

    /// <summary>The load-order path for a shared mod on this install, or null when it is not installed.</summary>
    private static string? Locate(SharedMod mod, GameEnv env, Analysis a)
    {
        if (mod.WorkshopId is { Length: > 0 } id)
        {
            if (env.WorkshopContentDir is { } ws && Directory.Exists(Path.Combine(ws, id))) return Path.Combine(ws, id);
            // a local copy published under that id
            if (a.AllMods.FirstOrDefault(m => m.Kind == EntryKind.Local && m.PublishedId == id) is { } copy) return copy.Name;
        }
        if (mod.Folder is { Length: > 0 } folder)
        {
            if (mod.Plugins)
            {
                var plugins = Path.Combine(env.BepInExDir, "plugins");
                if (Directory.Exists(plugins))
                    foreach (var info in Directory.EnumerateFiles(plugins, "mod_info.json", SearchOption.AllDirectories))
                        if (string.Equals(Path.GetFileName(Path.GetDirectoryName(info)), folder, StringComparison.OrdinalIgnoreCase))
                            return Path.GetDirectoryName(info);
            }
            else if (Directory.Exists(Path.Combine(env.ModsDir, folder))) return folder;
        }
        return null;
    }
}
