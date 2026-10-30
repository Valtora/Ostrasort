using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

public class CulpritHuntTests
{
    private static ModEntry Mod(string name, ModClass cls = ModClass.DataAdditive, string[]? requires = null, bool disabled = false)
    {
        var m = new ModEntry
        {
            Raw = name + (disabled ? "|disabled" : "|edit"), Kind = EntryKind.Local, Name = name, Dir = name,
            DisplayName = name, Class = cls, Disabled = disabled,
        };
        foreach (var r in requires ?? Array.Empty<string>()) m.RequiredMods.Add(r);
        return m;
    }

    private static Analysis With(params ModEntry[] mods) => new()
    {
        Registered = new[] { new ModEntry { Raw = "core", Kind = EntryKind.Core, Name = "core" } }.Concat(mods).ToList(),
    };

    /// <summary>Plays a whole hunt with a fixed culprit, answering as the game would.</summary>
    private static CulpritHunt Play(Analysis a, string culprit, out int launches)
    {
        var hunt = CulpritHunt.Start(a, "lo", DateTime.UtcNow)!;
        launches = 0;
        while (!hunt.Finished)
        {
            var order = hunt.NextRound(a);
            if (hunt.Finished) break;
            launches++;
            var culpritOn = order.Contains(culprit + "|edit");
            hunt.Answer(stillBroken: culpritOn);
        }
        return hunt;
    }

    [Fact]
    public void Halving_FindsTheCulprit_InLogLaunches()
    {
        var mods = Enumerable.Range(1, 16).Select(i => Mod("M" + i)).ToArray();
        var hunt = Play(With(mods), "M11", out var launches);

        Assert.Equal(new[] { "M11" }, hunt.Suspects);
        Assert.Equal(4, launches);
    }

    [Fact]
    public void TheModLoader_AndTheDisabled_AreNeverSuspects()
    {
        var a = With(Mod("Loader", ModClass.Infrastructure), Mod("A"), Mod("B"), Mod("Off", disabled: true));
        var hunt = CulpritHunt.Start(a, "lo", DateTime.UtcNow)!;
        Assert.Equal(new[] { "A", "B" }, hunt.Suspects);

        var order = hunt.NextRound(a);
        Assert.Contains("Loader|edit", order);
    }

    [Fact]
    public void ADependent_GoesOffWithItsDependency()
    {
        var a = With(Mod("A"), Mod("B"), Mod("Base"), Mod("Addon", requires: new[] { "Base" }));
        var hunt = CulpritHunt.Start(a, "lo", DateTime.UtcNow)!;

        var order = hunt.NextRound(a);   // the later half: Base and Addon

        Assert.Contains("Base|disabled", order);
        Assert.Contains("Addon|disabled", order);
        Assert.Contains("A|edit", order);
    }

    [Fact]
    public void AHubEveryoneNeeds_IsSplitAroundNotThrough()
    {
        // turning off Hub would turn off everything; the hunt picks a split that leaves it on
        var a = With(Mod("Hub"), Mod("X", requires: new[] { "Hub" }), Mod("Y", requires: new[] { "Hub" }), Mod("Z", requires: new[] { "Hub" }));
        var hunt = Play(a, "Y", out _);
        Assert.Equal(new[] { "Y" }, hunt.Suspects);
    }

    [Fact]
    public void ThePatch_StaysOffDuringTheHunt_AndComesBackAfter()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OstraHunt_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, Patcher.MarkerFile), "{}");
        var patch = new ModEntry { Raw = Patcher.FolderName, Kind = EntryKind.Local, Name = Patcher.FolderName, Dir = dir };
        var a = With(Mod("A"), Mod("B"), patch);
        var hunt = CulpritHunt.Start(a, "lo", DateTime.UtcNow)!;

        Assert.Contains(Patcher.FolderName + "|disabled", hunt.NextRound(a));
        hunt.Answer(stillBroken: false);
        Assert.Contains(Patcher.FolderName, hunt.FinalOrder(keepCulpritOff: true));
        Assert.Contains(hunt.Suspects[0] + "|disabled", hunt.FinalOrder(keepCulpritOff: true));
        Assert.Equal(a.Registered.Select(m => m.Raw), hunt.FinalOrder(keepCulpritOff: false));
    }

    [Fact]
    public void ProblemWithEverySuspectOff_LeavesNoSuspects()
    {
        var a = With(Mod("A"), Mod("B"));
        var hunt = CulpritHunt.Start(a, "lo", DateTime.UtcNow)!;
        hunt.NextRound(a);
        hunt.Answer(stillBroken: true);    // B off, still broken: A
        Assert.Equal(new[] { "A" }, hunt.Suspects);

        var b = With(Mod("A"), Mod("B"), Mod("C"), Mod("D"));
        var h2 = CulpritHunt.Start(b, "lo", DateTime.UtcNow)!;
        h2.NextRound(b); h2.Answer(stillBroken: true);   // C, D cleared
        h2.NextRound(b); h2.Answer(stillBroken: true);   // B cleared
        Assert.Equal(new[] { "A" }, h2.Suspects);
    }

    [Fact]
    public void LaterRounds_PlanFromTheOrderBeforeTheHunt()
    {
        var names = new[] { "A", "B", "C", "D" };
        var hunt = CulpritHunt.Start(With(names.Select(n => Mod(n)).ToArray()), "lo", DateTime.UtcNow)!;
        var order = hunt.NextRound(With(names.Select(n => Mod(n)).ToArray()));   // C, D off

        // the rescan after writing sees the hunt's own mods as disabled
        var rescanned = With(names.Select(n => Mod(n, disabled: order.Contains(n + "|disabled"))).ToArray());
        hunt.Answer(stillBroken: false);                                          // culprit in C, D
        var next = hunt.NextRound(rescanned);

        Assert.Equal(new[] { "C", "D" }, hunt.Suspects);
        Assert.Single(hunt.Off);
        Assert.Contains("A|edit", next);
    }
}