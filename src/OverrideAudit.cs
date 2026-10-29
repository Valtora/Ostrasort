namespace Ostrasort;

/// <summary>
/// After a game update: which mods still carry the old version of a core
/// object, undoing what the update changed, and which overrides now match the
/// game exactly and do nothing. Ostranauts' answer to LOOT's dirty-edit data,
/// driven by the per-field hashes in <see cref="CoreHistory"/>.
///
/// A field counts as undone when the update changed it and the mod's copy holds
/// the value from before the update. Without the FFU framework the game
/// replaces a whole object with the mod's copy, so a field the update ADDED is
/// also lost when the mod's copy lacks it. With FFU the game merges field by
/// field, so only fields the mod actually sets count. A field the update
/// changed that the mod sets to some third value is the mod's own edit and is
/// left alone.
/// </summary>
public static class OverrideAudit
{
    public static void Annotate(GameEnv env, Analysis a)
    {
        var current = CoreHistory.Current(env.CoreDataDir);
        if (current is null) return;
        var previous = CoreHistory.Previous(env.CoreDataDir);
        Annotate(a, current, previous);
    }

    /// <summary>The comparison without I/O, for tests.</summary>
    public static void Annotate(Analysis a, CoreHistory.Snapshot current, CoreHistory.Snapshot? previous)
    {
        var wholeObject = a.Ffu is not { FrameworkPresent: true };
        foreach (var m in a.AllMods.Where(m => m.Loads && !m.IsPatch && m.CoreOverrideFields.Count > 0))
        {
            foreach (var ((type, name), mine) in m.CoreOverrideFields.OrderBy(kv => kv.Key.Name, StringComparer.Ordinal))
            {
                // containers merged record by record, and FFU command edits, are not object copies
                if (Scanner.SimpleContainerTypes.ContainsKey(type) || m.FfuArrayEditClaims.Contains((type, name))) continue;
                var key = CoreHistory.Key(type, name);
                if (!current.Objects.TryGetValue(key, out var now)) continue;

                if (mine.All(f => now.TryGetValue(f.Key, out var h) && h == f.Value) && (!wholeObject || mine.Count == now.Count))
                    m.RedundantOverrides.Add(name);

                if (previous is null || !previous.Objects.TryGetValue(key, out var before)) continue;
                var undone = new List<string>();
                foreach (var field in now.Keys.Union(before.Keys).OrderBy(f => f, StringComparer.Ordinal))
                {
                    before.TryGetValue(field, out var was);
                    now.TryGetValue(field, out var isNow);
                    if (was == isNow) continue;                       // the update left it alone
                    mine.TryGetValue(field, out var theirs);
                    if (theirs is not null && theirs == was) undone.Add(field);              // keeps the old value
                    else if (wholeObject && theirs is null && was is null) undone.Add(field); // a new field the copy drops
                }
                if (undone.Count > 0) m.RevertedChanges.Add($"{name}: {string.Join(", ", undone)}");
            }

            if (m.RevertedChanges.Count > 0)
            {
                var sample = string.Join("; ", m.RevertedChanges.Take(2)) + (m.RevertedChanges.Count > 2 ? $"; +{m.RevertedChanges.Count - 2} more" : "");
                a.Warnings.Add($"'{m.DisplayName ?? m.Name}' still carries values from before the game update" +
                               $"{Between(previous!, current)} in {m.RevertedChanges.Count} object(s), so it undoes that update's changes " +
                               $"to them ({sample}). Check for an update to the mod, or turn it off if the update matters to you");
            }
        }
    }

    private static string Between(CoreHistory.Snapshot previous, CoreHistory.Snapshot current) =>
        previous.Version is { } p && current.Version is { } c && p != c ? $" from {p} to {c}" : "";
}
