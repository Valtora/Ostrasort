using System.IO;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

public class LoadOrderFileTests : IDisposable
{
    private readonly string _path;

    public LoadOrderFileTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "ostrasort-lo-" + Guid.NewGuid().ToString("N") + ".json");
        Write("""[{"strName":"Mod Loading Order","aLoadOrder":["core","A","B"],"CORE_MOD_NAME":"core"}]""");
    }

    public void Dispose()
    {
        File.Delete(_path);
        if (File.Exists(_path + ".bak")) File.Delete(_path + ".bak");
    }

    private void Write(string text) => File.WriteAllText(_path, text);

    [Fact]
    public void Read_ParsesTheOrder()
    {
        var lo = LoadOrderFile.Read(_path);
        Assert.Equal(new[] { "core", "A", "B" }, lo.Order);
    }

    [Fact]
    public void Write_DropsExactDuplicates()
    {
        LoadOrderFile.Read(_path).Write(new[] { "core", "A", "B", "A", "core" });
        Assert.Equal(new[] { "core", "A", "B" }, LoadOrderFile.Read(_path).Order);
    }

    [Fact]
    public void Write_KeepsTopLevelArrayAndBacksUp()
    {
        LoadOrderFile.Read(_path).Write(new[] { "core", "B", "A" });
        Assert.StartsWith("[", File.ReadAllText(_path).TrimStart());
        Assert.True(File.Exists(_path + ".bak"));
        Assert.Equal(new[] { "core", "B", "A" }, LoadOrderFile.Read(_path).Order);
        Assert.False(File.Exists(_path + ".tmp"));   // the atomic-write scratch file must not linger
    }

    [Fact]
    public void Read_ParsesAndSanitizesIgnorePatterns()
    {
        Write("""[{"strName":"Mod Loading Order","aLoadOrder":["core"],"aIgnorePatterns":["LA_","foo\\bar//baz"]}]""");
        var lo = LoadOrderFile.Read(_path);
        // sanitized like the game's PathSanitize: backslashes -> /, doubled / collapsed
        Assert.Equal(new[] { "LA_", "foo/bar/baz" }, lo.IgnorePatterns);
    }

    [Fact]
    public void Write_PreservesIgnorePatterns()
    {
        Write("""[{"strName":"Mod Loading Order","aLoadOrder":["core","A"],"aIgnorePatterns":["LA_"]}]""");
        LoadOrderFile.Read(_path).Write(new[] { "core", "A", "B" });
        Assert.Equal(new[] { "LA_" }, LoadOrderFile.Read(_path).IgnorePatterns);   // untouched by order writes
    }

    [Fact]
    public void Read_RejectsNonArrayFile()
    {
        Write("""{"strName":"Mod Loading Order","aLoadOrder":["core"]}""");   // bare object, not an array
        Assert.ThrowsAny<Exception>(() => LoadOrderFile.Read(_path));
    }

    [Fact]
    public void Write_CanonicalisesAbsolutePathCase()
    {
        // a real folder with known case, referenced by a wrong-case path
        var dir = Path.Combine(Path.GetTempPath(), "OstraCase_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "SubFolder"));
        try
        {
            var misCased = Path.Combine(dir, "subfolder");   // exists on disk as "SubFolder"
            LoadOrderFile.Read(_path).Write(new[] { "core", misCased });

            var written = LoadOrderFile.Read(_path).Order;
            Assert.Contains(written, w => w.EndsWith("SubFolder", StringComparison.Ordinal));   // corrected
            Assert.DoesNotContain(written, w => w.EndsWith("subfolder", StringComparison.Ordinal));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Write_CollapsesCaseVariantsOfSameWorkshopPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OstraDup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Item"));
        try
        {
            var upper = Path.Combine(dir, "Item");
            var lower = Path.Combine(dir, "item");
            LoadOrderFile.Read(_path).Write(new[] { "core", lower, upper });   // same folder, two cases

            var paths = LoadOrderFile.Read(_path).Order.Where(o => o != "core").ToList();
            Assert.Single(paths);   // collapsed to one canonical entry
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
