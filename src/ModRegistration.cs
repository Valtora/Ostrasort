namespace Ostrasort;

/// <summary>
/// Registers a discovered-but-unregistered mod into loading_order.json by
/// adding its aLoadOrder entry - the same string the suggestion would use
/// (local mods keep their |edit marker; Workshop/plugins-dir mods register by
/// absolute path). The entry lands in its "smart slot": immediately before the
/// FFU block and the generated Ostrasort Patch (whichever comes first), which
/// is exactly where "Apply Suggested Fixes" would place a normal mod, so no
/// order-change is flagged afterwards; with neither present it is appended.
/// The mod's own files are never touched. Guarded write (.bak + rolling
/// backup); a no-op if the mod is already listed under any marker variant.
/// This is the counterpart to <see cref="ModRemoval"/>.
/// </summary>
public static class ModRegistration
{
    public sealed record Result(string Entry, int Position, bool AlreadyRegistered);

    public static Result Register(GameEnv env, ModEntry m, Analysis a)
    {
        if (m.Registered)
            throw new InvalidOperationException("this mod is already registered.");
        if (m.Kind == EntryKind.Core)
            throw new InvalidOperationException("core is the base game data, not a registerable mod.");
        if (m.IsPatch)
            throw new InvalidOperationException(
                "the Ostrasort Patch registers itself when generated - use the Patch controls, not Register.");

        var entry = Analysis.SuggestedRaw(m);
        var path0 = entry.Split('|')[0];
        var lo = LoadOrderFile.Read(env.LoadingOrderPath);

        // defensive: if it is somehow already listed (any marker variant), leave
        // the file untouched - a rescan would then show it as registered anyway
        var existing = lo.Order.FindIndex(e =>
            string.Equals(e.Split('|')[0], path0, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) return new Result(lo.Order[existing], existing, AlreadyRegistered: true);

        // smart slot: before the first registered FFU mod or the generated patch
        // (the patch closes the non-FFU block and FFU mods trail it), so a normal
        // mod slots into the non-FFU block just like the suggestion places it;
        // with neither anchor present, append at the end
        var anchors = a.Registered
            .Where(r => (r.IsFfu || r.IsPatch) && r.Raw.Length > 0)
            .Select(r => r.Raw.Split('|')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var insertAt = anchors.Count == 0 ? -1
            : lo.Order.FindIndex(e => anchors.Contains(e.Split('|')[0]));

        var target = lo.Order.ToList();
        if (insertAt < 0) { target.Add(entry); insertAt = target.Count - 1; }
        else target.Insert(insertAt, entry);
        lo.Write(target);
        return new Result(entry, insertAt, AlreadyRegistered: false);
    }
}
