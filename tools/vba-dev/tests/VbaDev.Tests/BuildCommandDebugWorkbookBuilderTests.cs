using VbaDev.App.Cli;
using VbaDev.App.Debugging;
using Xunit;

namespace VbaDev.Tests;

public sealed class BuildCommandDebugWorkbookBuilderTests
{
    [Fact]
    public async Task SuccessfulBuildOutputIsPreservedForDebugLifecycleReporting()
    {
        var builder = new BuildCommandDebugWorkbookBuilder(_ => CommandResult.Success(
            "Built C:\\project\\bin\\Book.xlsm" + Environment.NewLine +
            "WARN Book/Protected reference remains." + Environment.NewLine));

        var result = await builder.BuildAsync(null!, CancellationToken.None);

        Assert.Equal(
            [
                "Built C:\\project\\bin\\Book.xlsm",
                "WARN Book/Protected reference remains."
            ],
            result.Output);
    }

    [Fact]
    public async Task FailedBuildBecomesADebugSetupErrorBeforeVisibleExcelCanStart()
    {
        var builder = new BuildCommandDebugWorkbookBuilder(_ =>
            CommandResult.UsageError("The workbook could not be built."));

        var error = await Assert.ThrowsAsync<DebugSetupException>(() =>
            builder.BuildAsync(null!, CancellationToken.None));

        Assert.Equal("The workbook could not be built.", error.Message);
    }
}
