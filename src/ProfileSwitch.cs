namespace Ostrasort;

public enum SwitchMode
{
    Replace,   // the profile's order BECOMES the whole aLoadOrder (clean setup switch)
    Merge,     // the profile's order, then current mods it doesn't mention appended at the end
}

/// <summary>
/// The computed effect of switching to a profile, without writing anything.
/// <see cref="NewOrder"/> is what to write as the new aLoadOrder; the other
/// lists are for the report the switch shows afterwards.
/// </summary>
public sealed record SwitchPlan(
    SwitchMode Mode,
    List<string> NewOrder,           // raw entries to write
    List<ProfileEntry> Missing,      // profile entries that no longer resolve on disk (skipped)
    List<string> Appended,           // (merge) current entries appended after the profile's order
    List<string> Dropped);           // (replace) current entries the profile omits, removed from the order

/// <summary>
/// Plans a profile switch (pure, testable). Resolves each profile entry against
/// the current install, drops the ones that no longer exist (reported, never a
/// hard failure), de-duplicates by mod identity, and produces the new
/// aLoadOrder for the chosen mode. The generated OstrasortPatch is never
/// carried across in either mode - it is re-derived by the normal post-switch
/// analysis - and core is guaranteed to stay first.
/// </summary>
public static class ProfileSwitch
{
    public static SwitchPlan Plan(GameEnv env, Analysis current, Profile profile, SwitchMode mode)
    {
        var missing = new List<ProfileEntry>();
        var kept = new List<string>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in profile.Entries)
        {
            if (IsPatchRaw(e.Raw)) continue;                       // defensive: never carry the patch
            var parsed = ModEntry.Parse(e.Raw, env);
            if (parsed.Kind != EntryKind.Core && parsed.Dir is null)
            {
                missing.Add(e);                                    // unsubscribed / deleted / parked
                continue;
            }
            if (keys.Add(KeyOf(e.Raw))) kept.Add(e.Raw);           // de-dup within the profile (marker/case variants)
        }

        // merge-append: keep every currently-registered mod the profile doesn't
        // mention, in its current relative order (never core, never the patch)
        var appended = new List<string>();
        if (mode == SwitchMode.Merge)
            foreach (var m in current.Registered)
            {
                if (m.Kind == EntryKind.Core || IsPatchEntry(m)) continue;
                if (keys.Add(KeyOf(m.Raw))) { kept.Add(m.Raw); appended.Add(m.Raw); }
            }

        // replace: the current mods the profile omits are dropped from the order
        // (their files stay on disk - Ostrasort's next scan flags any unregistered)
        var dropped = mode == SwitchMode.Replace
            ? current.Registered
                .Where(m => m.Kind != EntryKind.Core && !IsPatchEntry(m) && !keys.Contains(KeyOf(m.Raw)))
                .Select(m => m.Raw).ToList()
            : new List<string>();

        // the game loads core before everything else - guarantee it leads, exactly once
        kept.RemoveAll(r => r.Split('|')[0] == "core");
        kept.Insert(0, "core");

        return new SwitchPlan(mode, kept, missing, appended, dropped);
    }

    /// <summary>Normalized mod identity: marker stripped, absolute paths canonically cased, case-insensitive.</summary>
    private static string KeyOf(string raw) =>
        PathCase.CanonicalIfPath(raw.Split('|')[0]).ToLowerInvariant();

    private static bool IsPatchRaw(string raw) =>
        string.Equals(raw.Split('|')[0], Patcher.FolderName, StringComparison.OrdinalIgnoreCase);

    private static bool IsPatchEntry(ModEntry m) =>
        m.IsPatch || IsPatchRaw(m.Raw);
}
