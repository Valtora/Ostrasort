using System.IO;
using System.Text.Json;

namespace Ostrasort;

/// <summary>
/// One restorable point-in-time: the exact loading_order.json text plus the
/// full contents of the generated patch folder (null = no patch existed).
/// Together those cover every mutation Ostrasort performs - order changes,
/// patch generation/refresh (including the player's merge decisions, which
/// live in the marker), patch removal, and .bak restores.
/// </summary>
public sealed record OpSnapshot(string Label, string LoadingOrderText, Dictionary<string, byte[]>? PatchFiles);

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
        return new OpSnapshot(label, lo, patch);
    }

    /// <summary>Puts the install back exactly as the snapshot recorded it.</summary>
    public static void Restore(GameEnv env, OpSnapshot snap)
    {
        // same paranoia as every other loading_order write
        if (!snap.LoadingOrderText.TrimStart().StartsWith('['))
            throw new InvalidOperationException("refusing to restore: the snapshot is not a top-level JSON array.");
        using (var doc = JsonDocument.Parse(snap.LoadingOrderText)) { }   // strict parse or throw

        File.WriteAllText(env.LoadingOrderPath, snap.LoadingOrderText);

        var dir = Path.Combine(env.ModsDir, Patcher.FolderName);
        if (Directory.Exists(dir))
        {
            if (!File.Exists(Path.Combine(dir, Patcher.MarkerFile)))
                throw new InvalidOperationException(
                    $"'{dir}' has no {Patcher.MarkerFile} - refusing to touch a folder Ostrasort did not generate.");
            Directory.Delete(dir, recursive: true);
        }
        if (snap.PatchFiles is { } files)
            foreach (var (rel, bytes) in files)
            {
                var path = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
            }
    }
}
