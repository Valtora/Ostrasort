using System.IO;

namespace Ostrasort;

/// <summary>
/// One restorable point-in-time: the exact loading_order.json text plus the
/// full contents of the generated patch folder (null = no patch existed).
/// Together those cover every mutation Ostrasort performs - order changes,
/// patch generation/refresh (including the player's merge decisions, which
/// live in the marker), patch removal, and .bak restores. LoadingOrderPath
/// identifies the install it was taken from, so a snapshot can never be
/// restored into a different install.
/// </summary>
public sealed record OpSnapshot(string Label, string LoadingOrderText, Dictionary<string, byte[]>? PatchFiles,
    string LoadingOrderPath);

public static class UndoOps
{
    public static OpSnapshot Capture(GameEnv env, string label)
    {
        var lo = File.ReadAllText(env.LoadingOrderPath);
        Dictionary<string, byte[]>? patch = null;
        var dir = Path.Combine(env.ModsDir, Patcher.FolderName);
        if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, Patcher.MarkerFile)))
        {
            patch = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                patch[Path.GetRelativePath(dir, f)] = File.ReadAllBytes(f);
        }
        return new OpSnapshot(label, lo, patch, Path.GetFullPath(env.LoadingOrderPath));
    }

    /// <summary>Puts the install back exactly as the snapshot recorded it.</summary>
    public static void Restore(GameEnv env, OpSnapshot snap)
    {
        // EVERY validation runs before ANY write, so a refusal leaves the
        // install untouched instead of half-restored.
        if (!string.Equals(Path.GetFullPath(env.LoadingOrderPath), snap.LoadingOrderPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"This undo step belongs to a different installation ({snap.LoadingOrderPath}) - refusing to restore it here.");
        var parsed = LoadOrderFile.Parse(env.LoadingOrderPath, snap.LoadingOrderText);

        var dir = Path.Combine(env.ModsDir, Patcher.FolderName);
        if (Directory.Exists(dir) && !File.Exists(Path.Combine(dir, Patcher.MarkerFile)))
            throw new InvalidOperationException(
                $"'{dir}' has no {Patcher.MarkerFile} - refusing to touch a folder Ostrasort did not generate.");

        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        if (snap.PatchFiles is { } files)
            foreach (var (rel, bytes) in files)
            {
                var path = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
            }

        // route through Write so a restore gets the same guarantees as any
        // other order change: the cross-process write lock, a .bak + rolling
        // backup of what is on disk right now, path-case canonicalisation and
        // dedupe (an old snapshot may predate a canonicalisation fix), and an
        // atomic replace
        parsed.Write(parsed.Order);
    }
}
