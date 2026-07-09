using VbaDev.App.Cli;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class CliSurfaceTests
{
    private readonly CommandLineApplication application = ToolingCompositionRoot.CreateCommandLineApplication();

    [Fact]
    public void RootHelpListsSupportedCommands()
    {
        var result = application.Run(["--help"]);

        Assert.Equal(0, result.ExitCode);
        foreach (var commandName in new[] { "new", "common-module", "reference", "build", "test", "publish", "export", "doctor" })
        {
            Assert.Contains(commandName, result.StandardOutput, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("  add ", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("  update ", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectCommandsExposeProjectAndDocumentOptions()
    {
        foreach (var commandName in new[] { "common-module add", "common-module list", "reference add", "reference list", "reference remove", "build", "test", "publish", "export" })
        {
            var result = application.Run([.. commandName.Split(' '), "--help"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("--project", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("--document", result.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProjectLevelCommandsDoNotExposeDocumentOptions()
    {
        foreach (var commandName in new[] { "common-module update", "doctor" })
        {
            var result = application.Run([.. commandName.Split(' '), "--help"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("--project", result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("--document", result.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NewExcelHelpExposesNameAndOutputOptions()
    {
        var result = application.Run(["new", "excel", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--name", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("-n", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--output", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("-o", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ResourceCommandHelpExposesExpectedOptions()
    {
        var commonModuleAdd = application.Run(["common-module", "add", "--help"]);
        Assert.Equal(0, commonModuleAdd.ExitCode);
        Assert.Contains("--force", commonModuleAdd.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--document", commonModuleAdd.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("-d", commonModuleAdd.StandardOutput, StringComparison.Ordinal);

        var commonModuleList = application.Run(["common-module", "list", "--help"]);
        Assert.Equal(0, commonModuleList.ExitCode);
        Assert.Contains("--format <text|json>", commonModuleList.StandardOutput, StringComparison.Ordinal);

        var referenceList = application.Run(["reference", "list", "--help"]);
        Assert.Equal(0, referenceList.ExitCode);
        Assert.Contains("--format <text|json>", referenceList.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void TestHelpExposesFormatAndNoBuildOptions()
    {
        var result = application.Run(["test", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--format <text|ndjson>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("-f", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--no-build", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("--build", result.StandardOutput, StringComparison.Ordinal);
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
    public void CapabilitiesCommandReturnsJsonContract()
    {
        var result = application.Run(["capabilities", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"toolVersion\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"contractVersion\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"commands\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"build\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public void UnknownCommandReturnsUsageError()
    {
        var result = application.Run(["missing"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unknown command 'missing'.", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidTestFormatIsRejected()
    {
        var result = application.Run(["test", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unsupported value 'json' for --format.", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void ObsoleteUnreleasedCommandFormsAreRejected()
    {
        foreach (var args in new[]
        {
            new[] { "add", "Logger" },
            ["update"],
            ["add", "reference", "Microsoft Scripting Runtime"],
            ["remove", "reference", "Microsoft Scripting Runtime"],
            ["test", "--build"],
            ["test", "-?"]
        })
        {
            var result = application.Run(args);

            Assert.NotEqual(0, result.ExitCode);
        }
    }
}
