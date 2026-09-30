using System.Diagnostics;
using Xunit;

namespace VbaTools.Integration.Tests;

public sealed class LspChildCrashDumpTests
{
    private const string RunId = "run-20260930T120000000Z-0123456789abcdef";

    [Fact]
    public void Prebuilt_LSP_child_is_armed_only_for_a_valid_local_diagnostic_run()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = TempDirectory.Create();
        var runRoot = Directory.CreateDirectory(Path.Combine(temp.Path, RunId)).FullName;
        var startInfo = NewStartInfo();

        using (var lease = LspChildCrashDump.TryConfigure(
                   startInfo, runRoot, RunId, 11L * 1024 * 1024 * 1024))
        {
            Assert.NotNull(lease);
            Assert.True(lease.IsArmed);
            Assert.Equal("1", startInfo.Environment["DOTNET_DbgEnableMiniDump"]);
            Assert.Equal("4", startInfo.Environment["DOTNET_DbgMiniDumpType"]);
            Assert.Contains("%p", startInfo.Environment["DOTNET_DbgMiniDumpName"]);
            Assert.True(startInfo.RedirectStandardInput);
            Assert.True(startInfo.RedirectStandardOutput);
            Assert.True(startInfo.RedirectStandardError);
        }

        Assert.Null(LspChildCrashDump.TryConfigure(
            NewStartInfo(), runRoot, "wrong-run-id", 11L * 1024 * 1024 * 1024));
    }

    private static ProcessStartInfo NewStartInfo() => new("vba-language-server.exe")
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
}
