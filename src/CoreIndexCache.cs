using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ostrasort;

/// <summary>
/// Persistent cache of the core data index. Parsing every core JSON is the
/// slow part of a scan and core only changes when the game updates, so the
/// raw index (with each object's source file, aIgnorePatterns NOT applied -
/// those are re-applied per load) is cached in %LOCALAPPDATA%\Ostrasort,
/// keyed by a cheap fingerprint: the count, total size and newest write time
/// of every *.json under the core data folder. Any game update, verify or
/// hand edit changes the fingerprint and invalidates the cache.
/// </summary>
public static class CoreIndexCache
{
    private const int FormatVersion = 1;
    private const int MaxCacheFiles = 8;   // fixture/--game runs get their own files; prune the rest

    public sealed record Entry(string Type, string Name, string RelPath);
    public sealed record Snapshot(List<Entry> Entries, int ProblemFiles);

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ostrasort");

    private static string PathFor(string coreDataDir)
    {
        var key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(coreDataDir.ToLowerInvariant())))[..12];
        return Path.Combine(Dir, $"coreindex-{key}.json");
    }

    private static (long Count, long Bytes, long MaxTicks) Fingerprint(string coreDataDir)
    {
        long count = 0, bytes = 0, maxTicks = 0;
        foreach (var f in Directory.EnumerateFiles(coreDataDir, "*.json", SearchOption.AllDirectories))
        {
            var fi = new FileInfo(f);
            count++;
            bytes += fi.Length;
            var t = fi.LastWriteTimeUtc.Ticks;
            if (t > maxTicks) maxTicks = t;
        }
        return (count, bytes, maxTicks);
    }

    /// <summary>The cached index, or null when absent/stale/unreadable (rebuild and Save).</summary>
    public static Snapshot? TryLoad(string coreDataDir)
    {
        try
        {
            var path = PathFor(coreDataDir);
            if (!File.Exists(path) || !Directory.Exists(coreDataDir)) return null;

            var root = JsonNode.Parse(File.ReadAllText(path));
            if (root?["v"]?.GetValue<int>() != FormatVersion) return null;
            var (count, bytes, maxTicks) = Fingerprint(coreDataDir);
            if (root["count"]?.GetValue<long>() != count ||
                root["bytes"]?.GetValue<long>() != bytes ||
                root["maxTicks"]?.GetValue<long>() != maxTicks) return null;

            var entries = new List<Entry>();
            foreach (var e in (root["entries"] as JsonArray) ?? new JsonArray())
            {
                if (e is not JsonArray { Count: 3 } triple) return null;   // corrupt - rebuild
                entries.Add(new Entry(
                    triple[0]!.GetValue<string>(), triple[1]!.GetValue<string>(), triple[2]!.GetValue<string>()));
            }
            return new Snapshot(entries, root["problemFiles"]?.GetValue<int>() ?? 0);
        }
        catch { return null; }   // any cache problem = cache miss, never an error
    }

    public static void Save(string coreDataDir, Snapshot snap)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var (count, bytes, maxTicks) = Fingerprint(coreDataDir);
            var entries = new JsonArray();
            foreach (var e in snap.Entries)
                entries.Add(new JsonArray(e.Type, e.Name, e.RelPath));
            var root = new JsonObject
            {
                ["v"] = FormatVersion,
                ["dir"] = coreDataDir,
                ["count"] = count,
                ["bytes"] = bytes,
                ["maxTicks"] = maxTicks,
                ["problemFiles"] = snap.ProblemFiles,
                ["entries"] = entries,
            };
            File.WriteAllText(PathFor(coreDataDir), root.ToJsonString(new JsonSerializerOptions()));
            Prune();
        }
        catch { /* a failed cache write only costs the next scan */ }
    }

    private static void Prune()
    {
        var files = Directory.GetFiles(Dir, "coreindex-*.json");
        if (files.Length <= MaxCacheFiles) return;
        foreach (var f in files.OrderByDescending(File.GetLastWriteTimeUtc).Skip(MaxCacheFiles))
            try { File.Delete(f); } catch { }
    }
}
