using System.IO;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

/// <summary>
/// The mods-folder override (the two-install feature): an explicit --mods /
/// Installation mods folder wins and is validated. Uses a throwaway fixture so
/// it never touches the real install or the user's settings.json (an explicit
/// game root disables the strPathMods branch).
/// </summary>
public class GameEnvTests : IDisposable
{
    private readonly string _root;

    public GameEnvTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "OstraEnv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "game", "Ostranauts_Data", "StreamingAssets", "data"));
        Directory.CreateDirectory(Path.Combine(_root, "game", "Ostranauts_Data", "Mods"));
        Directory.CreateDirectory(Path.Combine(_root, "othermods"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string GameRoot => Path.Combine(_root, "game");

    [Fact]
    public void Locate_ModsDirOverride_WinsAndDrivesLoadingOrderPath()
    {
        var otherMods = Path.Combine(_root, "othermods");
        var env = GameEnv.Locate(GameRoot, otherMods);

        Assert.Equal(PathCase.Canonical(otherMods), env.ModsDir);
        Assert.Equal(Path.Combine(env.ModsDir, "loading_order.json"), env.LoadingOrderPath);
        // the game root (hence core data) is unchanged - the two folders can live on different disks
        Assert.Equal(Path.Combine(GameRoot, "Ostranauts_Data", "StreamingAssets", "data"), env.CoreDataDir);
    }

    [Fact]
    public void Locate_NoModsOverride_UsesDefaultUnderTheGame()
    {
        var env = GameEnv.Locate(GameRoot);   // explicit game root => strPathMods ignored, default Mods
        Assert.Equal(PathCase.Canonical(Path.Combine(GameRoot, "Ostranauts_Data", "Mods")), env.ModsDir);
    }

    [Fact]
    public void Locate_MissingModsOverride_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => GameEnv.Locate(GameRoot, Path.Combine(_root, "does-not-exist")));
    }
}
