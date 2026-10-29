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

    public static Result Register(GameEnv env, ModEntry m, Analysis a) => RegisterMany(env, new[] { m }, a)[0];

    /// <summary>
    /// Registers several mods in ONE guarded write (a multi-mod zip install), so
    /// the rolling backups keep the order from before the whole action rather
    /// than filling up with its intermediate states. Results are in input order.
    /// </summary>
    public static List<Result> RegisterMany(GameEnv env, IReadOnlyList<ModEntry> mods, Analysis a)
    {
        foreach (var m in mods)
        {
            if (m.Registered)
                throw new InvalidOperationException("this mod is already registered.");
            if (m.Kind == EntryKind.Core)
                throw new InvalidOperationException("core is the base game data, not a registerable mod.");
            if (m.IsPatch)
                throw new InvalidOperationException(
                    "the Ostrasort Patch registers itself when generated - use the Patch controls, not Register.");
        }

        var lo = LoadOrderFile.Read(env.LoadingOrderPath);
        var target = lo.Order.ToList();

        // smart slot: before the first registered FFU mod or the generated patch
        // (the patch closes the non-FFU block and FFU mods trail it), so a normal
        // mod slots into the non-FFU block just like the suggestion places it;
        // with neither anchor present, append at the end
        var anchors = a.Registered
            .Where(r => (r.IsFfu || r.IsPatch) && r.Raw.Length > 0)
            .Select(r => r.Raw.Split('|')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var results = new List<Result>();
        var added = false;
        foreach (var m in mods)
        {
            var entry = Analysis.SuggestedRaw(m);
            var path0 = entry.Split('|')[0];

            // defensive: if it is somehow already listed (any marker variant), leave
            // it untouched - a rescan would then show it as registered anyway
            var existing = target.FindIndex(e =>
                string.Equals(e.Split('|')[0], path0, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) { results.Add(new Result(target[existing], existing, AlreadyRegistered: true)); continue; }

            var insertAt = anchors.Count == 0 ? -1
                : target.FindIndex(e => anchors.Contains(e.Split('|')[0]));
            if (insertAt < 0) { target.Add(entry); insertAt = target.Count - 1; }
            else target.Insert(insertAt, entry);
            results.Add(new Result(entry, insertAt, AlreadyRegistered: false));
            added = true;
        }

        if (added) lo.Write(target);
        return results;
    }
}