using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ostrasort;

/// <summary>
/// Zipped copies of the game's whole Saves folder, taken before changes that
/// can hurt a save: a profile switch, a patch change, the first scan after a
/// game update, a culprit hunt. Saves are already zip files, so the copy is
/// stored without recompressing; the newest <see cref="Keep"/> are kept in
/// %APPDATA%\Ostrasort\save-backups. A backup is skipped when no save changed
/// since the last one, so frequent triggers cost nothing.
/// </summary>
public static class SaveBackups
{
    public const int Keep = 3;

    public sealed record Backup(string Path, DateTime TakenUtc, string Reason, long Bytes);

    /// <summary>The game's saves, per Windows user (shared by every install).</summary>
    public static string DefaultSavesDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        @"AppData\LocalLow\Blue Bottle Games\Ostranauts\Saves");

    public static string DefaultBackupDir => AppPaths.File("save-backups");

    /// <summary>Newest first.</summary>
    public static List<Backup> List(string? backupDir = null)
    {
        backupDir ??= DefaultBackupDir;
        if (!Directory.Exists(backupDir)) return new();
        var list = new List<Backup>();
        foreach (var f in Directory.GetFiles(backupDir, "saves-*.zip"))
        {
            // saves-20261029-153012-profile-switch.zip
            var stem = System.IO.Path.GetFileNameWithoutExtension(f);
            var parts = stem.Split('-', 4);
            if (parts.Length < 3 || !DateTime.TryParseExact(parts[1] + parts[2], "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var taken))
                continue;
            var reason = parts.Length == 4 ? parts[3].Replace('-', ' ') : "";
            list.Add(new Backup(f, taken, reason, new FileInfo(f).Length));
        }
        return list.OrderByDescending(b => b.TakenUtc).ToList();
    }

    /// <summary>The newest write time of anything in the Saves folder, or null when there is nothing.</summary>
    public static DateTime? NewestSaveWriteUtc(string savesDir)
    {
        if (!Directory.Exists(savesDir)) return null;
        DateTime? newest = null;
        foreach (var f in Directory.EnumerateFiles(savesDir, "*", SearchOption.AllDirectories))
        {
            var t = File.GetLastWriteTimeUtc(f);
            if (newest is null || t > newest) newest = t;
        }
        return newest;
    }

    /// <summary>
    /// Zips the Saves folder. Returns null when there is nothing to back up or
    /// no save changed since the newest backup (unless <paramref name="force"/>).
    /// <paramref name="protect"/> is never pruned, so a restore cannot delete
    /// the backup it is restoring from.
    /// </summary>
    public static Backup? Create(string reason, string? savesDir = null, string? backupDir = null,
        bool force = false, string? protect = null, DateTime? nowUtc = null)
    {
        savesDir ??= DefaultSavesDir;
        backupDir ??= DefaultBackupDir;
        var newestSave = NewestSaveWriteUtc(savesDir);
        if (newestSave is null) return null;
        var existing = List(backupDir);
        if (!force && existing.Count > 0 && newestSave <= existing[0].TakenUtc) return null;

        Directory.CreateDirectory(backupDir);
        var now = nowUtc ?? DateTime.UtcNow;
        var slug = new string(reason.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        var path = System.IO.Path.Combine(backupDir, $"saves-{now:yyyyMMdd-HHmmss}-{slug}.zip");
        for (var n = 2; File.Exists(path); n++)
            path = System.IO.Path.Combine(backupDir, $"saves-{now:yyyyMMdd-HHmmss}-{slug}-{n}.zip");

        var tmp = path + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
                foreach (var f in Directory.EnumerateFiles(savesDir, "*", SearchOption.AllDirectories))
                {
                    var rel = System.IO.Path.GetRelativePath(savesDir, f).Replace('\\', '/');
                    // shared read: Steam may hold steam_autocloud.vdf open
                    using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var entry = zip.CreateEntry(rel, CompressionLevel.NoCompression);
                    entry.LastWriteTime = File.GetLastWriteTime(f);
                    using var dst = entry.Open();
                    src.CopyTo(dst);
                }
            File.Move(tmp, path);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }

        foreach (var old in List(backupDir).Skip(Keep))
            if (!string.Equals(old.Path, protect, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(old.Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return List(backupDir).FirstOrDefault(b => b.Path == path);
    }

    /// <summary>
    /// Replaces the Saves folder with a backup's contents. The current saves are
    /// backed up first (always, even when unchanged), so a restore can itself be
    /// undone. The swap goes through a sibling folder, so a failed extract leaves
    /// the current saves where they were.
    /// </summary>
    public static Backup? Restore(Backup backup, string? savesDir = null, string? backupDir = null)
    {
        savesDir ??= DefaultSavesDir;
        var before = Create("before restore", savesDir, backupDir, force: true, protect: backup.Path);

        var parent = System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(savesDir))!;
        var incoming = System.IO.Path.Combine(parent, "Saves.ostrasort-restore");
        var outgoing = System.IO.Path.Combine(parent, "Saves.ostrasort-old");
        if (Directory.Exists(incoming)) FileSystemUtil.RobustDeleteDirectory(incoming);
        if (Directory.Exists(outgoing)) FileSystemUtil.RobustDeleteDirectory(outgoing);

        ZipFile.ExtractToDirectory(backup.Path, incoming);
        if (Directory.Exists(savesDir)) Directory.Move(savesDir, outgoing);
        try { Directory.Move(incoming, savesDir); }
        catch
        {
            if (Directory.Exists(outgoing) && !Directory.Exists(savesDir)) Directory.Move(outgoing, savesDir);
            throw;
        }
        if (Directory.Exists(outgoing)) FileSystemUtil.RobustDeleteDirectory(outgoing);
        return before;
    }

    // ------------------------------------------------------ game updates ---

    private static string VersionsPath => AppPaths.File("save-backups", "seen-versions.json");

    /// <summary>
    /// Records the installed game version for this install and reports whether
    /// it changed since the last scan: the first scan after a game update. The
    /// very first sighting of an install is not an update.
    /// </summary>
    public static bool GameUpdatedSinceLastScan(GameEnv env)
    {
        if (env.InstalledVersion is not { } version) return false;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(env.GameRoot.ToLowerInvariant())))[..12];
        Dictionary<string, string> seen;
        try { seen = File.Exists(VersionsPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(VersionsPath)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { seen = new(); }
        var changed = seen.TryGetValue(key, out var last) && last != version;
        if (last != version)
        {
            seen[key] = version;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(VersionsPath)!);
                File.WriteAllText(VersionsPath, JsonSerializer.Serialize(seen));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return changed;
    }
}

/// <summary>
/// Which mods each save was made with. Ostranauts saves do not record their own
/// mod list, so Ostrasort records the active set whenever the game is started
/// from its Launch button and links each save written during that session to
/// it. A save written by a game started straight from Steam stays unlinked.
/// Kept in %APPDATA%\Ostrasort\save-modsets.json.
/// </summary>
public static class SaveModSets
{
    private const int KeepLaunches = 30;

    public sealed class Launch
    {
        public DateTime StartedUtc { get; set; }
        public DateTime? EndedUtc { get; set; }
        public string LoadingOrderPath { get; set; } = "";
        public string? GameVersion { get; set; }
        public List<ProfileEntry> Mods { get; set; } = new();
    }

    public sealed class SaveRecord
    {
        public DateTime WrittenUtc { get; set; }
        public DateTime LaunchedUtc { get; set; }
        public string LoadingOrderPath { get; set; } = "";
        public string? GameVersion { get; set; }
        public List<ProfileEntry> Mods { get; set; } = new();
    }

    public sealed class Store
    {
        public List<Launch> Launches { get; set; } = new();
        public Dictionary<string, SaveRecord> Saves { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static string DefaultPath => AppPaths.File("save-modsets.json");

    public static Store Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Store>(File.ReadAllText(path)) is { } s)
            {
                s.Saves = new Dictionary<string, SaveRecord>(s.Saves, StringComparer.OrdinalIgnoreCase);
                return s;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new Store();
    }

    public static void Save(Store store, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The mods that load, as a profile would restore them (the generated patch left out).</summary>
    public static List<ProfileEntry> ActiveSet(Analysis a) =>
        Profile.Capture(a, "launch", null, null).Entries;

    /// <summary>Records a launch from Ostrasort. Closes any launch still open: only one game runs at a time.</summary>
    public static void RecordLaunch(Store store, Analysis a, GameEnv env, DateTime nowUtc)
    {
        foreach (var open in store.Launches.Where(l => l.EndedUtc is null)) open.EndedUtc = nowUtc;
        store.Launches.Add(new Launch
        {
            StartedUtc = nowUtc, LoadingOrderPath = env.LoadingOrderPath, GameVersion = env.InstalledVersion, Mods = ActiveSet(a),
        });
        if (store.Launches.Count > KeepLaunches) store.Launches.RemoveRange(0, store.Launches.Count - KeepLaunches);
    }

    /// <summary>
    /// Closes an open launch once the game is no longer running. The end is the
    /// last write to Player.log when that happened after the launch (the game
    /// writes it until it exits), otherwise now.
    /// </summary>
    public static void CloseFinished(Store store, bool gameRunning, DateTime? playerLogWriteUtc, DateTime nowUtc)
    {
        if (gameRunning) return;
        foreach (var l in store.Launches.Where(l => l.EndedUtc is null))
            l.EndedUtc = playerLogWriteUtc is { } w && w > l.StartedUtc ? w : nowUtc;
    }

    /// <summary>
    /// Links every save folder written during a recorded launch to that launch's
    /// mod set. A save rewritten in a later launch (autosave slots are reused)
    /// moves to the later one. Returns true when anything changed.
    /// </summary>
    public static bool Link(Store store, string savesDir, DateTime nowUtc)
    {
        if (!Directory.Exists(savesDir) || store.Launches.Count == 0) return false;
        var changed = false;
        foreach (var dir in Directory.EnumerateDirectories(savesDir))
        {
            var written = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
            // a minute of slack after the end covers the last save as the game quits
            var launch = store.Launches.LastOrDefault(l =>
                written >= l.StartedUtc && written <= (l.EndedUtc ?? nowUtc).AddMinutes(1));
            if (launch is null) continue;
            var name = System.IO.Path.GetFileName(dir);
            if (store.Saves.TryGetValue(name, out var rec) && rec.WrittenUtc >= written) continue;
            store.Saves[name] = new SaveRecord
            {
                WrittenUtc = written, LaunchedUtc = launch.StartedUtc, LoadingOrderPath = launch.LoadingOrderPath,
                GameVersion = launch.GameVersion, Mods = launch.Mods,
            };
            changed = true;
        }
        return changed;
    }

    /// <summary>The newest save folder by write time, or null.</summary>
    public static (string Name, DateTime WrittenUtc)? NewestSave(string savesDir)
    {
        if (!Directory.Exists(savesDir)) return null;
        (string, DateTime)? best = null;
        foreach (var dir in Directory.EnumerateDirectories(savesDir))
        {
            var t = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
            if (best is null || t > best.Value.Item2) best = (System.IO.Path.GetFileName(dir), t);
        }
        return best;
    }

    /// <summary>Mods a save was made with that do not load now, by their load-order path.</summary>
    public static List<ProfileEntry> MissingNow(SaveRecord rec, Analysis a)
    {
        static string Key(string raw) => raw.Split('|')[0].TrimEnd('\\', '/');
        var active = a.Registered.Where(m => m.Loads).Select(m => Key(m.Raw)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rec.Mods.Where(e => !e.Raw.EndsWith("|disabled", StringComparison.OrdinalIgnoreCase)
                                   && Key(e.Raw) != "core" && !active.Contains(Key(e.Raw))).ToList();
    }

    /// <summary>
    /// Warns when the newest save on disk was made with mods that do not load
    /// now. Loading a save without the mods that placed its objects can drop
    /// or damage them, and the player can put the set back from the Saves tab.
    /// </summary>
    public static void Annotate(Analysis a, GameEnv env, Store store, string savesDir)
    {
        if (NewestSave(savesDir) is not { } newest) return;
        if (!store.Saves.TryGetValue(newest.Name, out var rec) || rec.WrittenUtc < newest.WrittenUtc.AddSeconds(-2)) return;
        if (!string.Equals(rec.LoadingOrderPath, env.LoadingOrderPath, StringComparison.OrdinalIgnoreCase)) return;
        var missing = MissingNow(rec, a);
        if (missing.Count == 0) return;
        var names = string.Join(", ", missing.Take(3).Select(e => e.DisplayName)) + (missing.Count > 3 ? $", +{missing.Count - 3} more" : "");
        a.Warnings.Add($"your newest save ('{newest.Name}') was made with {missing.Count} mod(s) that do not load now ({names}). " +
                       "Loading it without them can drop or damage what they added - the Saves tab can put that mod set back as a profile");
    }
}
