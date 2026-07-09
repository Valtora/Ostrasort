using Ostrasort.Gui;
using Xunit;

namespace Ostrasort.Tests;

/// <summary>
/// The self-adopting updater's version-comparison gate: a freshly downloaded
/// build only replaces the installed copy when it is strictly newer. This is
/// compared by embedded version (never filename), so the parse must tolerate a
/// leading "v", +build / -suffix metadata, and garbage. The IPC, process-kill
/// and exe-replace steps are integration-level and verified manually.
/// </summary>
public class UpdaterTests
{
    [Theory]
    [InlineData("0.21.0", "0.20.0", true)]   // newer patch line wins
    [InlineData("1.0.0", "0.20.0", true)]    // newer major
    [InlineData("0.20.1", "0.20.0", true)]
    [InlineData("0.20.0", "0.20.0", false)]  // same version = not an update
    [InlineData("0.19.9", "0.20.0", false)]  // older = never adopt
    public void ShouldAdopt_ComparesVersions(string running, string installed, bool expected) =>
        Assert.Equal(expected, Updater.ShouldAdopt(running, installed));

    [Theory]
    [InlineData("v0.21.0", "0.20.0", true)]                 // leading v tolerated
    [InlineData("0.21.0", "v0.20.0", true)]
    [InlineData("0.21.0+abc123", "0.20.0", true)]           // +build metadata stripped
    [InlineData("0.21.0-rc1", "0.20.0", true)]              // -suffix stripped
    [InlineData("0.20.0+later", "0.20.0+earlier", false)]   // build metadata is not ordered - equal core
    [InlineData("v0.20.0", "0.20.0.0", false)]              // 3-part vs 4-part, same value
    public void ShouldAdopt_ToleratesTags(string running, string installed, bool expected) =>
        Assert.Equal(expected, Updater.ShouldAdopt(running, installed));

    [Theory]
    [InlineData("garbage", "0.20.0", false)]   // unparseable running -> 0.0, never newer
    [InlineData("1.0.0", "garbage", true)]     // unparseable installed -> 0.0, so a real running build wins
    [InlineData("", "", false)]                // both empty -> 0.0 vs 0.0
    public void ShouldAdopt_UnparseableFallsBackToZero(string running, string installed, bool expected) =>
        Assert.Equal(expected, Updater.ShouldAdopt(running, installed));
}
