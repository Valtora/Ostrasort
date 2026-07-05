using System.IO;

namespace Ostrasort;

/// <summary>
/// Crash-safe file overwrite: the text is written to a sibling .tmp first and
/// swapped in with File.Replace, so a crash or power cut mid-write can never
/// leave a truncated file behind. That matters most for loading_order.json -
/// a malformed file makes the game silently regenerate it and drop every
/// local mod.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        if (File.Exists(path)) File.Replace(tmp, path, null, ignoreMetadataErrors: true);
        else File.Move(tmp, path);
    }
}
