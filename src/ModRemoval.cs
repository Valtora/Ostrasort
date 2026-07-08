using System.IO;

namespace Ostrasort;

/// <summary>
/// Removes a local (or BepInEx\plugins) mod from the install: the folder is
/// parked as *.disabled (reversible - rename it back) or deleted outright,
/// and every aLoadOrder entry that resolves to it is dropped (guarded write,
/// .bak + rolling backup kept). Workshop subscriptions are Steam's to manage
/// - the GUI sends those to the item's Steam page instead - and the
/// generated patch has its own Remove action.
/// </summary>
public static class ModRemoval
{
    public sealed record Result(List<string> Affected, List<string> Unregistered, bool Deleted);

    public static Result RemoveLocal(GameEnv env, ModEntry m, bool delete)
    {
        if (m.Kind is not (EntryKind.Local or EntryKind.PluginDir))
            throw new InvalidOperationException("only local and plugins-dir mods can be removed - Workshop items are managed by Steam subscriptions.");
        if (m.IsPatch)
            throw new InvalidOperationException("the Ostrasort Patch has its own Remove action (--unpatch / the Remove patch button).");

        var affected = new List<string>();
        if (m.Dir is not null && Directory.Exists(m.Dir))
        {
            var dir = Path.TrimEndingDirectorySeparator(m.Dir);
            if (delete)
            {
                RobustDelete(dir);
                affected.Add(dir);
            }
            else
            {
                var target = dir + ".disabled";
                if (Directory.Exists(target)) RobustDelete(target);   // clear a stale park first
                Directory.Move(dir, target);                          // rename works even on ReadOnly folders
                affected.Add(target);
            }
        }

        // drop every entry variant (plain / |edit / |disabled) that resolves to this mod
        var unregistered = new List<string>();
        if (File.Exists(env.LoadingOrderPath))
        {
            bool Matches(string raw)
            {
                var path = raw.Split('|')[0];
                return m.Kind == EntryKind.Local
                    ? string.Equals(path, m.Name, StringComparison.OrdinalIgnoreCase)
                    : m.Dir is not null && string.Equals(
                        Path.TrimEndingDirectorySeparator(path),
                        Path.TrimEndingDirectorySeparator(m.Dir), StringComparison.OrdinalIgnoreCase);
            }

            var lo = LoadOrderFile.Read(env.LoadingOrderPath);
            var target = lo.Order.Where(e => !Matches(e)).ToList();
            if (target.Count != lo.Order.Count)
            {
                unregistered.AddRange(lo.Order.Where(Matches));
                lo.Write(target);
            }
        }
        return new Result(affected, unregistered, delete);
    }

    /// <summary>
    /// Delete a mod folder even when it (or a child) carries the ReadOnly
    /// attribute - common on folders extracted from a downloaded .zip, which
    /// plain Directory.Delete(recursive) cannot remove: it deletes the files,
    /// then throws "access denied" on the first ReadOnly directory, leaving a
    /// half-deleted mod. We clear ReadOnly, then rename-then-delete so the
    /// removal is atomic: if the rename is refused (a file is genuinely locked,
    /// e.g. the game is open) nothing is deleted and we say why; once renamed,
    /// the mod is gone from its real path even if emptying the husk is interrupted.
    /// </summary>
    private static void RobustDelete(string dir)
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
        catch { /* the mod is already gone from its real path; an inert husk is harmless */ }
    }

    /// <summary>Clear the ReadOnly attribute on a folder and everything under it, so it can be deleted.</summary>
    private static void ClearReadOnly(string dir)
    {
        var root = new DirectoryInfo(dir);
        if (root.Attributes.HasFlag(FileAttributes.ReadOnly))
            root.Attributes &= ~FileAttributes.ReadOnly;
        foreach (var info in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            if (info.Attributes.HasFlag(FileAttributes.ReadOnly))
                info.Attributes &= ~FileAttributes.ReadOnly;
    }
}
