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
    }

    [Fact]
    public void Read_RejectsNonArrayFile()
    {
        Write("""{"strName":"Mod Loading Order","aLoadOrder":["core"]}""");   // bare object, not an array
        Assert.ThrowsAny<Exception>(() => LoadOrderFile.Read(_path));
    }
}
