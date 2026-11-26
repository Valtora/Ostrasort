using System.IO;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

/// <summary>Which mod a flagged plugin belongs to, and where an unowned one lives.</summary>
public class CodeModOwnerTests
{
    private const string Root = @"C:\g";
    private static readonly GameEnv Env = new() { GameRoot = Root, DiscoveredVia = "test",
        CoreDataDir = Path.Combine(Root, "core"), ModsDir = Path.Combine(Root, "Mods") };
    private static readonly string Plugins = Path.Combine(Root, "BepInEx", "plugins");

    private static CodeModIssue Issue(string rel) =>
        new(Path.Combine(Plugins, rel), CodeModCheck.OwnerOf(Plugins, Path.Combine(Plugins, rel)), new[] { "patch target GUITooltip2.SetToolTip" });

    [Fact]
    public void PluginNoModShips_HasNoOwner_AndKeepsItsFolderName()
    {
        var mod = new ModEntry { Raw = "ShipsWater", Kind = EntryKind.Local, Name = "ShipsWater", Dir = "ShipsWater" };
        var a = new Analysis { Registered = new List<ModEntry> { mod } };
        var issue = Issue(@"ToggleTooltips\ToggleTooltips.dll");

        Assert.Null(Engine.CodeModOwner(a, issue));
        Assert.Equal("ToggleTooltips", Engine.CodeModOwnerName(a, issue));
    }

    [Theory]
    [InlineData(@"ToggleTooltips\ToggleTooltips.dll", @"ToggleTooltips")]
    [InlineData(@"ToggleTooltips\sub\ToggleTooltips.dll", @"ToggleTooltips")]
    [InlineData(@"Loose.dll", @"Loose.dll")]
    [InlineData(@"Workshop\3741030124\Thing\Thing.dll", @"Workshop\3741030124")]
    public void UnownedPluginTarget_IsWhatThePlayerRemoves(string rel, string expected) =>
        Assert.Equal(Path.Combine(Plugins, expected), Engine.UnownedPluginTarget(Env, Issue(rel)));

    [Fact]
    public void PluginDisplayPath_IsRelativeToTheGameFolder()
    {
        Assert.Equal(@"BepInEx\plugins\ToggleTooltips\ToggleTooltips.dll",
            Engine.PluginDisplayPath(Env, Path.Combine(Plugins, @"ToggleTooltips\ToggleTooltips.dll")));
        Assert.Equal(@"D:\profile\BepInEx\plugins\X.dll", Engine.PluginDisplayPath(Env, @"D:\profile\BepInEx\plugins\X.dll"));
    }
}
