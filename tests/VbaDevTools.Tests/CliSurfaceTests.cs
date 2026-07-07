using VbaDevTools.App.Cli;
using VbaDevTools.Composition;
using Xunit;

namespace VbaDevTools.Tests;

public sealed class CliSurfaceTests
{
    private readonly CommandLineApplication application = ToolingCompositionRoot.CreateCommandLineApplication();

    [Fact]
    public void RootHelpListsSupportedCommands()
    {
        var result = application.Run(["--help"]);

        Assert.Equal(0, result.ExitCode);
        foreach (var commandName in new[] { "new", "add", "build", "test", "publish", "update", "export", "doctor" })
        {
            Assert.Contains(commandName, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProjectCommandsExposeProjectAndDocumentOptions()
    {
        foreach (var commandName in new[] { "add", "build", "test", "publish", "update", "export", "doctor" })
        {
            var result = application.Run([commandName, "--help"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("--project", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("--document", result.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TestHelpExposesFormatAndBuildOptions()
    {
        var result = application.Run(["test", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--format <ndjson|json|text>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--build", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportHelpExposesFromAndToOptions()
    {
        var result = application.Run(["export", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--from", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--to", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaceholderCommandReturnsNotImplementedResult()
    {
        var result = application.Run(["new", "SampleProject"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("not implemented", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidTestFormatIsRejected()
    {
        var result = application.Run(["test", "--format", "xml"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unsupported value 'xml' for --format.", result.StandardError, StringComparison.Ordinal);
    }
}
