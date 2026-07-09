using System.IO;

namespace Ostrasort;

/// <summary>
/// Shared filesystem primitives for the mod-management operations. Folders that
/// come out of a downloaded .zip commonly carry the ReadOnly attribute, which a
/// plain Directory.Delete(recursive) cannot remove - it deletes the files, then
/// throws "access denied" on the first ReadOnly directory, leaving a
/// half-deleted mod. These helpers clear ReadOnly and delete robustly, and are
/// reused by both <see cref="ModRemoval"/> (removing a mod) and
/// <see cref="ModInstall"/> (replacing a mod on overwrite).
/// </summary>
public static class FileSystemUtil
{
    /// <summary>
    /// Delete a folder even when it (or a child) carries the ReadOnly attribute.
    /// We clear ReadOnly, then rename-then-delete so the removal is atomic: if
    /// the rename is refused (a file is genuinely locked, e.g. the game is open)
    /// nothing is deleted and we say why; once renamed, the folder is gone from
    /// its real path even if emptying the husk is interrupted.
    /// </summary>
    public static void RobustDeleteDirectory(string dir)
    {
        ClearReadOnly(dir);
        var husk = dir + ".deleting-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Directory.Move(dir, husk);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"Couldn't remove '{Path.GetFileName(dir)}'. A file in it may be open (close the game " +
                "and any Explorer window showing the folder), or it is write-protected. Nothing was changed.", e);
        }
        try { Directory.Delete(husk, recursive: true); }
        catch { /* the folder is already gone from its real path; an inert husk is harmless */ }
    }

    /// <summary>Clear the ReadOnly attribute on a folder and everything under it, so it can be deleted.</summary>
    public static void ClearReadOnly(string dir)
    {
        var root = new DirectoryInfo(dir);
        if (root.Attributes.HasFlag(FileAttributes.ReadOnly))
            root.Attributes &= ~FileAttributes.ReadOnly;
        foreach (var info in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            if (info.Attributes.HasFlag(FileAttributes.ReadOnly))
                info.Attributes &= ~FileAttributes.ReadOnly;
    }
}
