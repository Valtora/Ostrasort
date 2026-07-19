using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace Ostrasort;

/// <summary>
/// Guarded access to loading_order.json. The file MUST stay a top-level JSON
/// array (the game deserializes JsonModList[]; anything else makes it fall
/// back to a no-mods load and regenerate the file, silently dropping every
/// local mod). Every write: .bak of the original text first, serialized text
/// must start with '[', and a strict re-parse round-trip check.
/// </summary>
public sealed class LoadOrderFile
{
    public required string Path { get; init; }
    public required string RawText { get; init; }
    public required JsonNode Root { get; init; }
    public required List<string> Order { get; init; }
    /// <summary>
    /// The [0].aIgnorePatterns array: global substring patterns the game
    /// matches against every data file's (forward-slash) path - matching
    /// files are skipped in core AND every mod. Sanitized like the game
    /// sanitizes them (DataHandler.PathSanitize).
    /// </summary>
    public required List<string> IgnorePatterns { get; init; }

    public static LoadOrderFile Read(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"loading_order.json not found at '{path}'. Launch the game once so it generates one.", path);
        return Parse(path, File.ReadAllText(path));
    }

    /// <summary>
    /// Parses raw loading_order.json text (the same validation Read applies).
    /// Lets a restore path route snapshot TEXT through Write's full ritual.
    /// </summary>
    public static LoadOrderFile Parse(string path, string raw)
    {
        if (!raw.TrimStart().StartsWith('['))
            throw new InvalidDataException(
                "loading_order.json is NOT a top-level JSON array - the game may already have " +
                "regenerated it after a corruption. Refusing to touch it; inspect it manually.");

        var root = JsonNode.Parse(raw) ?? throw new InvalidDataException("loading_order.json parsed to null.");
        if (root is not JsonArray arr || arr.Count == 0 || arr[0]?["aLoadOrder"] is not JsonArray orderArr)
            throw new InvalidDataException("loading_order.json has no [0].aLoadOrder array.");

        var order = orderArr.Select(n => n is not null && n.GetValueKind() == JsonValueKind.String
            ? n.GetValue<string>()
            : throw new InvalidDataException(
                "aLoadOrder contains a non-string entry - the file was hand-edited or damaged; inspect it manually.")).ToList();

        var patterns = new List<string>();
        if (arr[0]?["aIgnorePatterns"] is JsonArray patArr)
            foreach (var p in patArr)
                if (p?.GetValueKind() == JsonValueKind.String &&
                    p.GetValue<string>() is { Length: > 0 } s)
                    patterns.Add(s.Replace('\\', '/').Replace("//", "/"));

        return new LoadOrderFile { Path = path, RawText = raw, Root = root, Order = order, IgnorePatterns = patterns };
    }

    public void Write(IReadOnlyList<string> newOrder)
    {
        // Serialize the write across processes. The GUI and a headless tap (e.g.
        // Ostraplan registering a ship mod) both funnel through here, so a
        // short-lived, per-file named mutex stops them interleaving writes to the
        // same loading_order.json. Held ONLY for this ritual - deliberately not
        // the GUI single-instance mutex (that one lives for the whole window, and
        // a headless tap must never block on it). AtomicFile stops a torn file;
        // this stops a lost update mid-swap.
        using var _writeLock = AcquireWriteLock(Path);

        // Two self-heals at this single choke point, both to stop the game
        // duplicating mods on launch:
        //   1. Canonicalise every absolute (workshop) path to its real on-disk
        //      case. The game writes C:\Program Files (x86)\Steam\...; if we
        //      write a lowercase c:\program files\... form (as the Steam
        //      registry hands out), the game does not match its own
        //      subscription and re-adds it every launch.
        //   2. Drop exact duplicates (first wins) - the game has also been seen
        //      re-appending an already-registered subscription.
        var deduped = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in newOrder)
        {
            var e = PathCase.CanonicalIfPath(raw);
            if (seen.Add(e)) deduped.Add(e);
        }
        newOrder = deduped;

        Root[0]!["aLoadOrder"] = new JsonArray(newOrder.Select(e => (JsonNode)e).ToArray());
        var json = Root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        if (!json.TrimStart().StartsWith('['))
            throw new InvalidOperationException(
                "Refusing to write loading_order.json: serialized result is not a top-level JSON array.");

        using (var doc = JsonDocument.Parse(json))   // strict round-trip check
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Round-trip check failed: root is not an array.");
            var count = doc.RootElement[0].GetProperty("aLoadOrder").GetArrayLength();
            if (count != newOrder.Count)
                throw new InvalidOperationException(
                    $"Round-trip check failed: {count} entries serialized, expected {newOrder.Count}.");
        }

        // back up what is on disk NOW, not the text captured at Read: if another
        // process wrote between our Read and this Write (Read happens outside
        // the lock), the intermediate version must survive in the backups even
        // though this write replaces it
        var previous = RawText;
        try { if (File.Exists(Path)) previous = File.ReadAllText(Path); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }

        File.WriteAllText(Path + ".bak", previous);  // the text this write replaces
        Backups.Snapshot(Path, previous);            // rolling history (the .bak only survives one write)
        AtomicFile.WriteAllText(Path, json);         // never leave a truncated live file
    }

    /// <summary>
    /// A session-local named mutex keyed to this file's canonical path, so writers
    /// to the same loading_order.json serialize while writers to different installs
    /// don't. Best-effort: if the lock can't be taken (times out or errors) the
    /// write still proceeds - AtomicFile keeps the file intact regardless.
    /// </summary>
    private static WriteLockScope AcquireWriteLock(string path)
    {
        string? name = null;
        try
        {
            var key = System.IO.Path.GetFullPath(path).ToLowerInvariant();   // same file -> same name, case-insensitive
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
            name = "Ostrasort.LoadOrderWrite." + hash;   // mutex names can't contain '\', so hash the path
        }
        catch { /* leave name null = run without the lock */ }
        return new WriteLockScope(name);
    }

    private sealed class WriteLockScope : IDisposable
    {
        private readonly Mutex? _mutex;
        private readonly bool _held;

        public WriteLockScope(string? name)
        {
            if (name is null) return;
            try
            {
                _mutex = new Mutex(false, name);
                try { _held = _mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { _held = true; }   // prior holder died mid-write; it's ours now
            }
            catch { _mutex = null; _held = false; }
        }

        // WaitOne/ReleaseMutex must be same-thread; Write runs synchronously, so
        // this using-scope releases on the same thread that acquired it.
        public void Dispose()
        {
            try { if (_held) _mutex?.ReleaseMutex(); } catch { /* ignore */ }
            _mutex?.Dispose();
        }
    }
}
