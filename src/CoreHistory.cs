using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ostrasort;

/// <summary>
/// Per-field hashes of every core object, for the current game version and the
/// one before it. Core data is far too large to keep a copy of (over 200 MB),
/// but a hash per top-level field is enough to tell which fields a game update
/// changed, and whether a mod's copy of an object still carries the old values.
///
/// Kept in %APPDATA%\Ostrasort\core-history\&lt;install&gt;\ as current.json.gz
/// and previous.json.gz. A new snapshot replaces current, and the old current
/// becomes previous, only when the content differs: a Steam "verify files" that
/// rewrites identical files changes the folder fingerprint but not the hashes,
/// and must not push the real previous version out.
/// </summary>
public static class CoreHistory
{
    private const int FormatVersion = 1;

    /// <summary>One core version: "type|name" -> field -> hash.</summary>
    public sealed class Snapshot
    {
        public int V { get; set; } = FormatVersion;
        public string? Version { get; set; }
        public string Fingerprint { get; set; } = "";
        public string Digest { get; set; } = "";
        public Dictionary<string, Dictionary<string, string>> Objects { get; set; } = new(StringComparer.Ordinal);
    }

    public static string Key(string type, string name) => type + "|" + name;

    private static string DirFor(string coreDataDir) =>
        Path.Combine(AppPaths.DataDir, "core-history",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(coreDataDir.ToLowerInvariant())))[..12]);

    public static string FingerprintText((long Count, long Bytes, long MaxTicks) f) => $"{f.Count}:{f.Bytes}:{f.MaxTicks}";

    /// <summary>
    /// A stable hash of one JSON value. Re-serialised compactly first, so the
    /// same value laid out differently (indentation, line breaks) hashes the
    /// same. FNV-1a, 32 bits: the comparison is one value against one value.
    /// </summary>
    public static string HashValue(JsonElement value)
    {
        var buf = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buf)) value.WriteTo(w);
        var h = 2166136261u;
        foreach (var b in buf.WrittenSpan) { h ^= b; h *= 16777619u; }
        return h.ToString("x8");
    }

    public static Dictionary<string, string> HashFields(JsonElement obj)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in obj.EnumerateObject()) fields[p.Name] = HashValue(p.Value);
        return fields;
    }

    /// <summary>True when the current snapshot was not taken from this exact core folder state.</summary>
    public static bool NeedsRecord(string coreDataDir, string fingerprint) =>
        Read(Path.Combine(DirFor(coreDataDir), "current.json.gz"))?.Fingerprint != fingerprint;

    /// <summary>Stores a fresh snapshot, rotating the old current to previous when the content changed.</summary>
    public static void Record(string coreDataDir, Snapshot snap)
    {
        try
        {
            snap.Digest = DigestOf(snap.Objects);
            var dir = DirFor(coreDataDir);
            Directory.CreateDirectory(dir);
            var current = Path.Combine(dir, "current.json.gz");
            var previous = Path.Combine(dir, "previous.json.gz");
            var old = Read(current);
            if (old is not null && old.Digest != snap.Digest)
                File.Copy(current, previous, overwrite: true);
            Write(current, snap);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the audit is a convenience; a failed write only costs the comparison
        }
    }

    public static Snapshot? Current(string coreDataDir) => Read(Path.Combine(DirFor(coreDataDir), "current.json.gz"));
    public static Snapshot? Previous(string coreDataDir) => Read(Path.Combine(DirFor(coreDataDir), "previous.json.gz"));

    private static string DigestOf(Dictionary<string, Dictionary<string, string>> objects)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (key, fields) in objects.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(key));
            foreach (var (f, h) in fields.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                sha.AppendData(Encoding.UTF8.GetBytes($"\n{f}={h}"));
            sha.AppendData("\n\n"u8);
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static void Write(string path, Snapshot snap)
    {
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            JsonSerializer.Serialize(gz, snap);
        File.Move(tmp, path, overwrite: true);
        Cache.Remove(path);
    }

    // a rescan reads the same snapshots again; keep them until the file changes
    private static readonly Dictionary<string, (DateTime Stamp, Snapshot Snap)> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static Snapshot? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var stamp = File.GetLastWriteTimeUtc(path);
            lock (Cache)
                if (Cache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Snap;
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            var snap = JsonSerializer.Deserialize<Snapshot>(gz);
            if (snap is null || snap.V != FormatVersion) return null;
            lock (Cache) Cache[path] = (stamp, snap);
            return snap;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;   // a damaged snapshot is a missing one
        }
    }

}
