using System.IO;
using System.Runtime.CompilerServices;

namespace Wattkin.Tests;

internal static class TestSetup
{
    /// <summary>Tests deliberately feed broken files; keep their log lines out of the real log.</summary>
    [ModuleInitializer]
    internal static void Init() =>
        Environment.SetEnvironmentVariable("WATTKIN_LOG_DIR", Path.Combine(Path.GetTempPath(), "wattkin-test-logs"));
}
