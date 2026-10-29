using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

/// <summary>
/// Compiles a fake game (two builds), a fake Harmony and a plugin built against
/// the first game build, then checks the plugin against the second.
/// </summary>
public class CodeModCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OstraCode_" + Guid.NewGuid().ToString("N"));

    private const string Harmony = """
        namespace HarmonyLib {
            public enum MethodType { Normal, Getter, Setter, Constructor, StaticConstructor }
            [System.AttributeUsage(System.AttributeTargets.All, AllowMultiple = true)]
            public class HarmonyPatch : System.Attribute {
                public HarmonyPatch() {}
                public HarmonyPatch(System.Type t) {}
                public HarmonyPatch(string m) {}
                public HarmonyPatch(System.Type t, string m) {}
                public HarmonyPatch(System.Type t, string m, MethodType k) {}
            }
        }
        """;

    private const string GameV1 = """
        public class Base { public int baseField; public void BaseCall() {} }
        public class DataHandler : Base {
            public static int GetCondTrigger(string name) => 0;
            public int count;
            public int Size { get; set; }
            public void Tick() {}
            public class Nested { public void Run() {} }
        }
        public class Removed { public void Go() {} }
        public class Box<T> { public T Get() => default!; }
        """;

    private const string Plugin = """
        using HarmonyLib;
        public class Uses {
            public void Run(DataHandler d, Removed r, Box<int> b) {
                DataHandler.GetCondTrigger("x");
                d.count++;
                d.baseField++;
                d.BaseCall();
                d.Tick();
                new DataHandler.Nested().Run();
                b.Get();
                r.Go();
            }
        }
        [HarmonyPatch(typeof(DataHandler), "Tick")] public class PatchTick { static void Prefix() {} }
        [HarmonyPatch(typeof(DataHandler), "Size", MethodType.Getter)] public class PatchSize { static void Prefix() {} }
        [HarmonyPatch(typeof(DataHandler))]
        public class PatchByMethod {
            [HarmonyPatch("Tick")] static void Prefix() {}
            [HarmonyPatch("BaseCall")] static void Postfix() {}
        }
        """;

    public CodeModCheckTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Compile(string name, string source, string outName, params string[] refs)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is "System.Runtime.dll" or "System.Private.CoreLib.dll" or "netstandard.dll")
            .Concat(refs)
            .Select(p => MetadataReference.CreateFromFile(p));
        var comp = CSharpCompilation.Create(name, new[] { CSharpSyntaxTree.ParseText(source) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var path = Path.Combine(_dir, outName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var result = comp.Emit(path);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return path;
    }

    private (string Plugin, string Harmony) BuildPlugin()
    {
        var v1 = Compile("Assembly-CSharp", GameV1, "v1/Assembly-CSharp.dll");
        var harmony = Compile("0Harmony", Harmony, "0Harmony.dll");
        return (Compile("MyPlugin", Plugin, "MyPlugin.dll", v1, harmony), harmony);
    }

    [Fact]
    public void SameBuild_ReportsNothing()
    {
        var (plugin, _) = BuildPlugin();
        Assert.Empty(CodeModCheck.Check(Path.Combine(_dir, "v1", "Assembly-CSharp.dll"), plugin));
    }

    [Fact]
    public void ChangedBuild_ReportsWhatIsGone()
    {
        var (plugin, _) = BuildPlugin();
        // GetCondTrigger re-signed, a field renamed, a type and a property removed,
        // Tick removed (a call and two Harmony targets), base members moved away
        var v2 = Compile("Assembly-CSharp", """
            public class Base { }
            public class DataHandler : Base {
                public static int GetCondTrigger(string name, bool strict) => 0;
                public int total;
                public class Nested { public void Run() {} }
            }
            public class Box<T> { public T Get() => default!; }
            """, "v2/Assembly-CSharp.dll");

        var missing = CodeModCheck.Check(v2, plugin);

        Assert.Contains("method DataHandler.GetCondTrigger", missing);
        Assert.Contains("field DataHandler.count", missing);
        Assert.Contains("field Base.baseField", missing);
        Assert.Contains("method Base.BaseCall", missing);
        Assert.Contains("method DataHandler.Tick", missing);
        Assert.Contains("type Removed", missing);
        Assert.Contains("patch target DataHandler.Tick", missing);
        Assert.Contains("patch target DataHandler.Size", missing);
        Assert.Contains("patch target DataHandler.BaseCall", missing);
        Assert.DoesNotContain(missing, m => m.Contains("Nested") || m.Contains("Box"));
    }

    [Fact]
    public void MemberInheritedFromAUnityBase_IsFollowedIntoItsAssembly()
    {
        // a game type deriving from a library type in the same Managed folder
        var lib1 = Compile("UnityEngine", "public class MonoBehaviour { public void Invoke(string m) {} }", "v1/UnityEngine.dll");
        var v1 = Compile("Assembly-CSharp", "public class Ship : MonoBehaviour { public void Fly() {} public void Old() {} }", "v1/Assembly-CSharp.dll", lib1);
        var plugin = Compile("P", "public class U { public void Run(Ship s) { s.Fly(); s.Old(); } }", "P.dll", v1, lib1);
        var lib2 = Compile("UnityEngine", "public class MonoBehaviour { public void Invoke(string m) {} public void Old() {} }", "v2/UnityEngine.dll");
        var v2 = Compile("Assembly-CSharp", "public class Ship : MonoBehaviour { }", "v2/Assembly-CSharp.dll", lib2);

        // Old moved up into the Unity base, so the call still binds; Fly is gone
        Assert.Equal(new[] { "method Ship.Fly" }, CodeModCheck.Check(v2, plugin));
    }

    [Fact]
    public void BaseAssemblyUnreadable_GivesUpInsteadOfFlagging()
    {
        var lib = Compile("UnityEngine", "public class MonoBehaviour { }", "lib/UnityEngine.dll");
        var v1 = Compile("Assembly-CSharp", "public class Ship : MonoBehaviour { public void Fly() {} }", "v1/Assembly-CSharp.dll", lib);
        var plugin = Compile("P", "public class U { public void Run(Ship s) { s.Fly(); } }", "P.dll", v1, lib);
        var v2 = Compile("Assembly-CSharp", "public class Ship : MonoBehaviour { }", "v2/Assembly-CSharp.dll", lib);   // no UnityEngine beside it

        Assert.Empty(CodeModCheck.Check(v2, plugin));
    }
    [Fact]
    public void PluginThatUsesNoGameCode_IsSkipped()
    {
        var v1 = Compile("Assembly-CSharp", GameV1, "v1/Assembly-CSharp.dll");
        var lib = Compile("Helper", "public class H { public void X() {} }", "Helper.dll");
        Assert.Empty(CodeModCheck.Check(v1, lib));
    }

    [Theory]
    [InlineData(@"Workshop\123\Mod\Mod.dll", "Workshop 123")]
    [InlineData(@"ShipsWater\ShipsWater.dll", "ShipsWater")]
    [InlineData(@"Loose.dll", "Loose")]
    public void OwnerOf_NamesTheFolderThePlayerKnows(string rel, string owner)
    {
        Assert.Equal(owner, CodeModCheck.OwnerOf(@"C:\g\BepInEx\plugins", Path.Combine(@"C:\g\BepInEx\plugins", rel)));
    }
}
