using System.IO;

namespace Ostrasort;

/// <summary>
/// Resolves a path to the exact case it has on disk. Ostranauts writes workshop
/// entries in loading_order.json using the real filesystem case
/// (C:\Program Files (x86)\Steam\...), but the Steam registry hands out a
/// lowercase root (c:\program files (x86)\steam). If Ostrasort writes the
/// lowercase form, the game does not recognise its own subscription and
/// RE-ADDS it every launch - duplicating the mod. Canonicalising every
/// absolute path we write to its true on-disk case keeps our entries matching
/// the game's, which stops the duplication.
/// </summary>
public static class PathCase
{
    private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Canonical on-disk case for an absolute path; other strings (core, "Name|edit") pass through.</summary>
    public static string CanonicalIfPath(string entry)
    {
        // absolute Windows path? (drive letter + colon)
        if (entry.Length < 3 || entry[1] != ':') return entry;
        try { return Canonical(entry); }
        catch { return entry; }
    }

    public static string Canonical(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return path;
        if (Cache.TryGetValue(path, out var hit)) return hit;

        var result = Directory.Exists(path) || File.Exists(path)
            ? Resolve(new DirectoryInfo(path))
            : path;   // doesn't exist (dead entry) - leave it untouched
        Cache[path] = result;
        return result;
    }

    private static string Resolve(DirectoryInfo di)
    {
        if (di.Parent is null)                       // drive root, e.g. "C:\"
            return di.Name.ToUpperInvariant();
        var parent = Resolve(di.Parent);
        try
        {
            var match = new DirectoryInfo(parent).GetFileSystemInfos(di.Name);
            if (match.Length > 0) return Path.Combine(parent, match[0].Name);   // real-cased segment
        }
        catch { /* access denied etc. - fall through to the given case */ }
        return Path.Combine(parent, di.Name);
    }
}
