using System.IO;

namespace Ostrasort;

/// <summary>
/// The single home for Ostrasort's user data (settings, installations, profiles,
/// backups, ignore list, core-index cache, operation log). It lives in the
/// roaming profile at %APPDATA%\Ostrasort.
///
/// <para>This is deliberately NOT %LOCALAPPDATA%\Ostrasort any more: that folder
/// is now the Velopack install root (the app binaries live in
/// %LOCALAPPDATA%\Ostrasort\current, replaced wholesale on every update and
/// removed entirely on uninstall). Velopack's own guidance is to keep data that
/// must survive updates and uninstalls in the roaming app-data folder, so that
/// is where it goes.</para>
///
/// <para>Every prior release stored this data directly under
/// %LOCALAPPDATA%\Ostrasort. <see cref="MigrateLegacyData"/> moves it across once,
/// on first run of a Velopack build, and drops a sentinel so it never runs again.</para>
/// </summary>
public static class AppPaths
{
    /// <summary>Roaming data root, %APPDATA%\Ostrasort. Created lazily by the callers that write into it.</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ostrasort");

    /// <summary>Where every release up to 0.22 kept its data: %LOCALAPPDATA%\Ostrasort (now the Velopack install root).</summary>
    public static string LegacyDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ostrasort");

    /// <summary>Resolve a path inside the data root (e.g. <c>File("settings.json")</c>).</summary>
    public static string File(params string[] parts) => Path.Combine(new[] { DataDir }.Concat(parts).ToArray());

    /// <summary>
    /// One-time move of pre-0.23 data from %LOCALAPPDATA%\Ostrasort to the roaming
    /// root. Best-effort: any failure leaves the app on a fresh (empty) data dir,
    /// which is still fully usable. Only the data files/folders Ostrasort itself
    /// owns are touched - the legacy folder is now also the Velopack install root,
    /// so its current\, packages\ and Update.exe are deliberately left alone.
    /// </summary>
    public static void MigrateLegacyData() => Migrate(LegacyDataDir, DataDir);

    /// <summary>The named data files/folders Ostrasort owns, for a precise migration.</summary>
    private static readonly string[] KnownFiles = { "settings.json", "installations.json", "ignored.json", "ostrasort.log" };
    private static readonly string[] KnownDirs = { "profiles", "backups" };

    /// <summary>
    /// Testable core of the migration. Moves the known data items from
    /// <paramref name="legacy"/> to <paramref name="data"/> unless a destination
    /// item already exists (a newer copy always wins), then writes a
    /// <c>.migrated</c> sentinel so it is a no-op forever after. Returns the number
    /// of items moved (files + folders).
    /// </summary>
    public static int Migrate(string legacy, string data)
    {
        var moved = 0;
        try
        {
            var sentinel = Path.Combine(data, ".migrated");
            if (System.IO.File.Exists(sentinel)) return 0;
            Directory.CreateDirectory(data);

            var same = string.Equals(
                Path.GetFullPath(legacy).TrimEnd('\\', '/'),
                Path.GetFullPath(data).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);

            if (!same && Directory.Exists(legacy))
            {
                foreach (var name in KnownFiles)
                    if (MoveFileIfAbsent(Path.Combine(legacy, name), Path.Combine(data, name))) moved++;

                foreach (var src in SafeEnumerateFiles(legacy, "coreindex-*.json"))
                    if (MoveFileIfAbsent(src, Path.Combine(data, Path.GetFileName(src)))) moved++;

                // Merge these child-by-child rather than moving the whole folder:
                // a destination that already exists (even empty, or partially
                // written by an earlier run) must not strand the source's contents.
                // The child keys are unique hashes, so a present child is left as-is.
                foreach (var dir in KnownDirs)
                    moved += MergeDirInto(Path.Combine(legacy, dir), Path.Combine(data, dir));
            }

            System.IO.File.WriteAllText(sentinel,
                $"{DateTime.UtcNow:o}  Ostrasort data root moved to roaming AppData ({moved} item(s) migrated).\n");
        }
        catch { /* best-effort: an empty data dir is still usable */ }
        return moved;
    }

    private static bool MoveFileIfAbsent(string src, string dest)
    {
        if (!System.IO.File.Exists(src) || System.IO.File.Exists(dest)) return false;
        try { System.IO.File.Move(src, dest); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Moves every immediate child (subfolder or file) of <paramref name="src"/>
    /// into <paramref name="dest"/> that is not already there, creating dest as
    /// needed. Returns the number of children moved. A child already present in
    /// dest wins and is left untouched (newer data is never clobbered).
    /// </summary>
    private static int MergeDirInto(string src, string dest)
    {
        if (!Directory.Exists(src)) return 0;
        Directory.CreateDirectory(dest);
        var moved = 0;

        foreach (var childDir in SafeEnumerateDirs(src))
        {
            var target = Path.Combine(dest, Path.GetFileName(childDir));
            if (Directory.Exists(target) || System.IO.File.Exists(target)) continue;
            try { Directory.Move(childDir, target); moved++; }
            catch
            {
                try { CopyTree(childDir, target); moved++; }   // cross-volume / partial lock fallback
                catch { /* leave this child behind */ }
            }
        }

        foreach (var childFile in SafeEnumerateFiles(src, "*"))
            if (MoveFileIfAbsent(childFile, Path.Combine(dest, Path.GetFileName(childFile)))) moved++;

        // Leave no empty husk behind in the legacy (now Velopack) folder once
        // everything has been rescued.
        try { if (!Directory.EnumerateFileSystemEntries(src).Any()) Directory.Delete(src); } catch { /* not empty or locked - leave it */ }

        return moved;
    }

    private static void CopyTree(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            System.IO.File.Copy(f, target, overwrite: false);
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeEnumerateDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch { return Array.Empty<string>(); }
    }
}
