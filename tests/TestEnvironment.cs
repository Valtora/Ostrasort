using System.IO;
using System.Runtime.CompilerServices;

namespace Ostrasort.Tests;

/// <summary>
/// Points Ostrasort's data root at a throwaway folder before any test runs, so
/// the suite never writes core-index caches, profiles or backups into the
/// player's real %APPDATA%\Ostrasort (where its caches would also push out the
/// real game's).
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Init() =>
        Environment.SetEnvironmentVariable("OSTRASORT_DATA_DIR",
            Path.Combine(Path.GetTempPath(), "OstrasortTests_" + Environment.ProcessId));
}