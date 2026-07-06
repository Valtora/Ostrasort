using System.IO;

namespace Ostrasort;

/// <summary>
/// A small persistent record of the operations Ostrasort performs (writes
/// especially), shown in the GUI's Logs tab so the user can see exactly what
/// the tool did - and correlate it with the game's own logs. Persisted to
/// %LOCALAPPDATA%\Ostrasort\ostrasort.log so history survives restarts.
/// </summary>
public static class OpLog
{
    private static readonly object Lock = new();
    private static readonly List<string> Mem = new();
    private static bool _loaded;

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ostrasort");
    private static string LogPath => Path.Combine(Dir, "ostrasort.log");

    /// <summary>Records one operation with a timestamp (in-memory + on disk).</summary>
    public static void Add(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        lock (Lock)
        {
            EnsureLoaded();
            Mem.Add(line);
            try
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch { /* logging must never throw into an operation */ }
        }
    }

    /// <summary>Most-recent-last operation lines (loads persisted history on first use).</summary>
    public static IReadOnlyList<string> Recent(int max = 500)
    {
        lock (Lock)
        {
            EnsureLoaded();
            return Mem.Count <= max ? Mem.ToArray() : Mem.Skip(Mem.Count - max).ToArray();
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(LogPath))
                Mem.InsertRange(0, File.ReadLines(LogPath).TakeLast(500));
        }
        catch { /* unreadable log is not fatal */ }
    }

    /// <summary>Clears the operations log - wipes the in-memory history and empties the file on disk.</summary>
    public static void Clear()
    {
        lock (Lock)
        {
            _loaded = true;   // nothing left to lazily load
            Mem.Clear();
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(LogPath, string.Empty);
            }
            catch { /* clearing must never throw */ }
        }
    }

    /// <summary>Empties a game log file on disk (Player.log / BepInEx). Returns false if it could not be written.</summary>
    public static bool ClearFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.WriteAllText(path, string.Empty);
            return true;
        }
        catch { return false; }
    }

    public static string FilePath => LogPath;

    /// <summary>Last <paramref name="lines"/> lines of a game log file (Player.log / BepInEx), or a note if absent.</summary>
    public static List<string> Tail(string path, int lines)
    {
        try
        {
            if (!File.Exists(path)) return new List<string> { $"(not found: {path})" };
            return File.ReadLines(path).TakeLast(lines).ToList();
        }
        catch (Exception e) { return new List<string> { $"(could not read {path}: {e.Message})" }; }
    }
}
