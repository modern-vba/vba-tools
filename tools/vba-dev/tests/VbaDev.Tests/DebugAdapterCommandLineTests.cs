using VbaDev.Cli.Debugging;
using Xunit;

namespace VbaDev.Tests;

public sealed class DebugAdapterCommandLineTests
{
    [Fact]
    public void AdvertisedStdioEntryPointIsRecognizedExactly()
    {
        Assert.True(DebugAdapterCommandLine.IsRequested(["debug-adapter", "--stdio"]));
        Assert.Null(DebugAdapterCommandLine.Validate(["debug-adapter", "--stdio"]));

        Assert.False(DebugAdapterCommandLine.IsRequested(["build"]));
        Assert.Equal(
            "Usage: vba-dev debug-adapter --stdio",
            DebugAdapterCommandLine.Validate(["debug-adapter"]));
    }
}
