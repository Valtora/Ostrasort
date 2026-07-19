using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Ostrasort;

/// <summary>
/// Rolling history for loading_order.json. The sibling .bak only survives
/// until the next write, so every overwrite also snapshots the previous text
/// into %LOCALAPPDATA%\Ostrasort\backups\&lt;key&gt;\ - keeping the newest
/// <see cref="Keep"/> - which gives "apply, notice a problem tomorrow" a way
/// back. The key embeds the file's path, so installs and fixtures never mix.
/// </summary>
public static class Backups
{
    public const int Keep = 3;

    private static string DirFor(string loPath) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ostrasort", "backups",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(loPath.ToLowerInvariant())))[..12]);

    /// <summary>Stores the text being replaced; prunes to the newest <see cref="Keep"/>.</summary>
    public static void Snapshot(string loPath, string previousText)
    {
        try
        {
            var dir = DirFor(loPath);
            Directory.CreateDirectory(dir);
            // UTC so a DST fall-back / clock change can never make a newer
            // snapshot sort as older (prune would then delete the freshest
            // restore point); the trailing sequence keeps same-millisecond
            // snapshots in write order under the ordinal filename sort
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            string path;
            var n = 0;
            do { path = Path.Combine(dir, $"loading_order-{stamp}-{n++:D2}.json"); }
            while (File.Exists(path));
            File.WriteAllText(path, previousText);

            foreach (var old in Directory.GetFiles(dir, "loading_order-*.json")
                         .OrderByDescending(f => f, StringComparer.Ordinal).Skip(Keep))
                try { File.Delete(old); } catch { }
        }
        catch { /* a failed backup must never block the write itself */ }
    }

    /// <summary>Available snapshots, newest first.</summary>
    public static List<(string Path, DateTime Written)> List(string loPath)
    {
        try
        {
            var dir = DirFor(loPath);
            if (!Directory.Exists(dir)) return new();
            return Directory.GetFiles(dir, "loading_order-*.json")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .Select(f => (f, File.GetLastWriteTime(f)))
                .ToList();
        }
        catch { return new(); }
    }
}
