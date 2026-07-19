using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ostrasort;

/// <summary>One saved entry: the raw aLoadOrder string plus a friendly name captured at save time.</summary>
public sealed record ProfileEntry(string Raw, string DisplayName);

/// <summary>
/// A named load-order set the user can save and switch between - the signature
/// feature of every mature mod manager. Captures the live aLoadOrder (raw
/// entries, |edit/|disabled markers intact) MINUS the generated OstrasortPatch
/// entry: the patch is Ostrasort's own overlay, re-derived per setup, never
/// carried by a profile. It deliberately does NOT capture aIgnorePatterns, the
/// patch folder, or the ignore list - those stay global to the install, so a
/// switch only ever rewrites the mod order.
/// </summary>
public sealed class Profile
{
    public const int FormatVersion = 1;

    public required string Name { get; init; }
    public string? SavedAt { get; init; }             // ISO-8601, informational (round-trip display only)
    public string? SavedGameVersion { get; init; }    // the installed game version when saved
    public required List<ProfileEntry> Entries { get; init; }

    /// <summary>The raw aLoadOrder strings this profile would restore, in order.</summary>
    public IEnumerable<string> Raws => Entries.Select(e => e.Raw);

    /// <summary>Mods excluding the always-present core entry (for a friendly count).</summary>
    public int ModCount => Entries.Count(e => e.Raw.Split('|')[0] != "core");

    /// <summary>
    /// Snapshots the current registered order as a profile, dropping the
    /// generated OstrasortPatch entry (re-derived after a switch, never part of
    /// a profile). <paramref name="savedAt"/> is passed in rather than sampled
    /// so the capture stays free of ambient time (and unit-testable).
    /// </summary>
    public static Profile Capture(Analysis a, string name, string? gameVersion, string? savedAt) =>
        new()
        {
            Name = name,
            SavedAt = savedAt,
            SavedGameVersion = gameVersion,
            Entries = a.Registered
                .Where(m => !IsPatchEntry(m))
                .Select(m => new ProfileEntry(m.Raw, m.DisplayName ?? m.Name))
                .ToList(),
        };

    /// <summary>The tool-owned patch entry, identified robustly by marker or folder name.</summary>
    private static bool IsPatchEntry(ModEntry m) =>
        m.IsPatch || string.Equals(m.Raw.Split('|')[0], Patcher.FolderName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Per-install storage for profiles, in %LOCALAPPDATA%\Ostrasort\profiles\
/// keyed by a hash of the loading_order.json path - the same scheme
/// <see cref="Backups"/> and <see cref="CoreIndexCache"/> use, so real installs
/// and --game test fixtures never mix. A profile's identity is its (case-
/// insensitive) name; the filename is just a readable container.
/// </summary>
public static class ProfileStore
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The profiles folder for a given loading_order.json (public so tests can clean up).</summary>
    public static string DirFor(string loPath) => AppPaths.File("profiles",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(loPath.ToLowerInvariant())))[..12]);

    /// <summary>All saved profiles for this install, sorted by name (unreadable files are skipped).</summary>
    public static List<Profile> List(string loPath)
    {
        var result = new List<Profile>();
        try
        {
            var dir = DirFor(loPath);
            if (!Directory.Exists(dir)) return result;
            foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
                if (TryRead(f) is { } p) result.Add(p);
        }
        catch { /* a listing failure is not worth an error */ }
        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static Profile? Load(string loPath, string name) =>
        List(loPath).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static bool Exists(string loPath, string name) => Load(loPath, name) is not null;

    /// <summary>Writes a profile, replacing any existing one with the same (case-insensitive) name.</summary>
    public static void Save(string loPath, Profile profile)
    {
        var dir = DirFor(loPath);
        Directory.CreateDirectory(dir);

        // one file per (case-insensitive) name: reuse the file that already
        // holds this name; otherwise never clobber a file holding a DIFFERENT
        // profile whose name merely sanitizes to the same filename
        // ("a:b" and "a?b" both sanitize to "a_b")
        var target = FindFile(loPath, profile.Name);
        if (target is null)
        {
            target = PathForName(dir, profile.Name);
            var n = 1;
            while (File.Exists(target) &&
                   !string.Equals(TryRead(target)?.Name, profile.Name, StringComparison.OrdinalIgnoreCase))
                target = Path.Combine(dir, SafeName(profile.Name) + "-" + (++n) + ".json");
        }

        var node = new JsonObject
        {
            ["v"] = Profile.FormatVersion,
            ["name"] = profile.Name,
            ["savedAt"] = profile.SavedAt,
            ["savedGameVersion"] = profile.SavedGameVersion,
            ["entries"] = new JsonArray(profile.Entries.Select(e => (JsonNode)new JsonObject
            {
                ["raw"] = e.Raw,
                ["displayName"] = e.DisplayName,
            }).ToArray()),
        };
        // atomic: a crash mid-write must not corrupt the profile (TryRead would
        // then silently drop it from every listing)
        AtomicFile.WriteAllText(target, node.ToJsonString(Indented) + "\n");
    }

    public static void Delete(string loPath, string name)
    {
        if (FindFile(loPath, name) is { } f) File.Delete(f);
    }

    /// <summary>Renames a profile, moving its file to the new name and dropping the old one.</summary>
    public static void Rename(string loPath, string oldName, string newName)
    {
        var existing = Load(loPath, oldName)
            ?? throw new InvalidOperationException($"no saved profile named '{oldName}'.");
        var oldFile = FindFile(loPath, oldName);
        Save(loPath, new Profile
        {
            Name = newName,
            SavedAt = existing.SavedAt,
            SavedGameVersion = existing.SavedGameVersion,
            Entries = existing.Entries,
        });
        var newFile = PathForName(DirFor(loPath), newName);
        if (oldFile is not null && !string.Equals(oldFile, newFile, StringComparison.OrdinalIgnoreCase))
            try { File.Delete(oldFile); } catch { }
    }

    // ------------------------------------------------------------- internals ---

    private static string PathForName(string dir, string name) => Path.Combine(dir, SafeName(name) + ".json");

    /// <summary>Filename-safe rendering of a profile name (identity lives in the file's name field).</summary>
    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return cleaned.Length == 0 ? "profile" : cleaned;
    }

    /// <summary>The file currently holding a given profile name (by its in-file name, not its filename).</summary>
    private static string? FindFile(string loPath, string name)
    {
        var dir = DirFor(loPath);
        if (!Directory.Exists(dir)) return null;
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
            if (TryRead(f)?.Name is { } n && string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                return f;
        return null;
    }

    private static Profile? TryRead(string path)
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path));
            if (root?["name"]?.GetValue<string>() is not { Length: > 0 } name) return null;
            var entries = new List<ProfileEntry>();
            foreach (var e in (root["entries"] as JsonArray) ?? new JsonArray())
            {
                if (e?["raw"]?.GetValue<string>() is not { Length: > 0 } raw) continue;
                entries.Add(new ProfileEntry(raw, e["displayName"]?.GetValue<string>() ?? raw));
            }
            return new Profile
            {
                Name = name,
                SavedAt = root["savedAt"]?.GetValue<string>(),
                SavedGameVersion = root["savedGameVersion"]?.GetValue<string>(),
                Entries = entries,
            };
        }
        catch { return null; }   // unreadable/corrupt profile = skipped, never an error
    }
}
