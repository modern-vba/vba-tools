using System.Reflection;
using System.Text.Json;
using VbaDev.App.Diagnostics;
using VbaDev.App.Projects;
using VbaDev.Cli;
using VbaDev.Domain;
using Xunit;

namespace VbaDev.Tests;

public sealed class CliSurfaceTests
{
    private readonly VbaDevCommandLine application = CommandLineTestFactory.Create();

    [Fact]
    public async Task NoArgumentsReturnRootHelpAsSuccess()
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await application.InvokeAsync(
            [],
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("build", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Empty(standardError.ToString());
    }

    [Fact]
    public async Task VersionOptionRunsThroughPublicCommandLineBoundary()
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var commandLine = VbaDevCommandLine.CreateDefault();

        var exitCode = await commandLine.InvokeAsync(
            ["--version"],
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal($"vba-dev 0.1.0{Environment.NewLine}", standardOutput.ToString());
        Assert.Empty(standardError.ToString());
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("--unknown")]
    public async Task VersionOptionRejectsAdditionalTokens(string additionalToken)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await application.InvokeAsync(
            ["--version", additionalToken],
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task CapabilitiesRunThroughPublicCommandLineBoundary()
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var commandLine = VbaDevCommandLine.CreateDefault();

        var exitCode = await commandLine.InvokeAsync(
            ["capabilities", "--format", "json"],
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            "{\"toolVersion\":\"0.1.0\",\"contractVersion\":\"1.0\",\"commands\":{\"build\":{\"outputSchemaVersion\":\"1.0\"},\"common-module add\":{\"outputSchemaVersion\":\"1.0\"},\"common-module list\":{\"outputSchemaVersion\":\"1.0\"},\"common-module update\":{\"outputSchemaVersion\":\"1.0\"},\"doctor\":{\"outputSchemaVersion\":\"1.0\"},\"export\":{\"outputSchemaVersion\":\"1.0\"},\"import\":{\"outputSchemaVersion\":\"1.0\"},\"new excel\":{\"outputSchemaVersion\":\"1.0\"},\"publish\":{\"outputSchemaVersion\":\"1.0\"},\"reference add\":{\"outputSchemaVersion\":\"1.0\"},\"reference list\":{\"outputSchemaVersion\":\"1.0\"},\"reference remove\":{\"outputSchemaVersion\":\"1.0\"},\"test\":{\"outputSchemaVersion\":\"1.2\"}},\"debugAdapter\":{\"protocolVersion\":\"1.1\",\"transport\":\"stdio\",\"command\":\"debug-adapter\"}}" + Environment.NewLine,
            standardOutput.ToString());
        Assert.Empty(standardError.ToString());
    }

    [Fact]
    public async Task TestHelpRunsThroughPublicCommandLineBoundaryWithStablePlaceholders()
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var commandLine = VbaDevCommandLine.CreateDefault();

        var exitCode = await commandLine.InvokeAsync(
            ["test", "--help"],
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("--project <path>", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("--document <name>", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("-d", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("--format <text|ndjson>", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("-f", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("--no-build", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("--module <name>", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("--procedure <name>", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("--build", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Empty(standardError.ToString());
    }

    [Fact]
    public async Task EveryPublicCommandHelpComesFromTheRootCommandGraph()
    {
        var commandLine = VbaDevCommandLine.CreateDefault();
        var expectations = new Dictionary<string, string[]>
        {
            ["new excel"] = ["--name <name>", "-n", "--output <dir>", "-o"],
            ["common-module add"] = ["--project <path>", "--document <name>", "-d", "--force"],
            ["common-module list"] = ["--project <path>", "--document <name>", "--format <text|json>", "-f"],
            ["common-module update"] = ["--project <path>"],
            ["reference add"] = ["--project <path>", "--document <name>", "-d"],
            ["reference list"] = ["--project <path>", "--document <name>", "--format <text|json>", "-f"],
            ["reference remove"] = ["--project <path>", "--document <name>", "-d"],
            ["build"] = ["--project <path>", "--document <name>", "-d"],
            ["test"] = ["--project <path>", "--document <name>", "--format <text|ndjson>", "--no-build"],
            ["publish"] = ["--project <path>", "--document <name>", "-d"],
            ["export"] = ["--project <path>", "--document <name>", "--from <path>", "--to <dir>"],
            ["import"] = ["--from <dir>", "--to <path>"],
            ["doctor"] = ["--project <path>"],
            ["capabilities"] = ["--format <json>"]
        };

        foreach (var expectation in expectations)
        {
            using var standardOutput = new StringWriter();
            using var standardError = new StringWriter();
            var exitCode = await commandLine.InvokeAsync(
                [.. expectation.Key.Split(' '), "--help"],
                standardOutput,
                standardError,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            foreach (var fragment in expectation.Value)
            {
                Assert.Contains(fragment, standardOutput.ToString(), StringComparison.Ordinal);
            }

            Assert.Empty(standardError.ToString());
        }

        using var rootOutput = new StringWriter();
        using var rootError = new StringWriter();
        var rootExitCode = await commandLine.InvokeAsync(
            ["--help"],
            rootOutput,
            rootError,
            CancellationToken.None);

        Assert.Equal(0, rootExitCode);
        foreach (var commandName in new[]
                 {
                     "new", "common-module", "reference", "build", "test", "publish", "export", "import", "doctor", "capabilities"
                 })
        {
            Assert.Contains(commandName, rootOutput.ToString(), StringComparison.Ordinal);
        }

        Assert.DoesNotContain("debug-adapter", rootOutput.ToString(), StringComparison.Ordinal);
        Assert.Empty(rootError.ToString());
    }

    [Fact]
    public void RootHelpListsSupportedCommands()
    {
        var result = application.Run(["--help"]);

        Assert.Equal(0, result.ExitCode);
        foreach (var commandName in new[] { "new", "common-module", "reference", "build", "test", "publish", "export", "import", "doctor" })
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
        Assert.Contains("--module <name>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--procedure <name>", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("--build", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportHelpExposesFromAndToOptions()
    {
        var result = application.Run(["export", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--from", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--to", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("current directory", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("selected document source set", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportHelpExposesPathOnlyFlow()
    {
        var result = application.Run(["import", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--from", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--to", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("--project", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("--document", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("path-only", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("build", result.StandardOutput, StringComparison.Ordinal);
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
        Assert.Contains("\"test\":{\"outputSchemaVersion\":\"1.2\"}", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "\"debugAdapter\":{\"protocolVersion\":\"1.1\",\"transport\":\"stdio\",\"command\":\"debug-adapter\"}",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public void VersionOptionReturnsCanonicalCliVersion()
    {
        var result = application.Run(["--version"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"vba-dev 0.1.0{Environment.NewLine}", result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public void CliSurfacesAndAssemblyMetadataShareOneReleaseVersion()
    {
        var versionResult = application.Run(["--version"]);
        var capabilitiesResult = application.Run(["capabilities", "--format", "json"]);
        using var capabilities = JsonDocument.Parse(capabilitiesResult.StandardOutput);
        var informationalVersion = typeof(VbaDevCommandLine).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        Assert.Equal("0.1.0", informationalVersion);
        Assert.Equal($"vba-dev {informationalVersion}{Environment.NewLine}", versionResult.StandardOutput);
        Assert.Equal(
            informationalVersion,
            capabilities.RootElement.GetProperty("toolVersion").GetString());
    }

    [Fact]
    public void HelpAndCapabilitiesDoNotReadProjectState()
    {
        var workbookAutomation = new FakeWorkbookBuildAutomation();
        var referenceResolver = new FakeVbaProjectReferenceResolver();
        var contractOnlyApplication = CommandLineTestFactory.Create(
            Directory.GetCurrentDirectory(),
            environmentDiagnosticPort: new ThrowingEnvironmentDiagnosticPort(),
            workbookBuildAutomation: workbookAutomation,
            vbaProjectReferenceResolver: referenceResolver,
            projectManifestStore: new ThrowingProjectManifestStore());

        var help = contractOnlyApplication.Run(["build", "--help"]);
        var capabilities = contractOnlyApplication.Run(["capabilities", "--format", "json"]);

        Assert.Equal(0, help.ExitCode);
        Assert.Contains("--project <path>", help.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(0, capabilities.ExitCode);
        Assert.Contains("\"test\":{\"outputSchemaVersion\":\"1.2\"}", capabilities.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(workbookAutomation.OpenedWorkbooks);
        Assert.Empty(referenceResolver.RequestedNames);
    }

    [Fact]
    public void StaticCompletionComesFromTheInvokedRootCommandGraph()
    {
        var result = application.Run(["[suggest:1]", "b"]);
        var suggestions = result.StandardOutput.Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("build", suggestions);
        Assert.Contains("publish", suggestions);
        Assert.Contains("capabilities", suggestions);
        Assert.DoesNotContain("debug-adapter", suggestions);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public void ApplicationAndDomainAssembliesRemainIndependentOfSystemCommandLine()
    {
        foreach (var assembly in new[]
                 {
                     typeof(VbaDev.App.Cli.CommandResult).Assembly,
                     typeof(ProjectManifest).Assembly
                 })
        {
            Assert.DoesNotContain(
                assembly.GetReferencedAssemblies(),
                reference => reference.Name == "System.CommandLine");
        }
    }

    [Fact]
    public void UnknownCommandReturnsUsageError()
    {
        var result = application.Run(["missing"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("missing", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidTestFormatIsRejected()
    {
        var result = application.Run(["test", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("json", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("text", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("ndjson", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputFormatValuesRemainCaseInsensitive()
    {
        var result = application.Run(["capabilities", "--format", "JSON"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"contractVersion\"", result.StandardOutput, StringComparison.Ordinal);
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
            ["test", "--build"]
        })
        {
            var result = application.Run(args);

            Assert.NotEqual(0, result.ExitCode);
        }
    }

    private sealed class ThrowingProjectManifestStore : IProjectManifestStore
    {
        public ProjectManifest Load(string manifestPath)
            => throw new InvalidOperationException("Help must not load project state.");

        public void Save(string projectRoot, ProjectManifest manifest)
            => throw new InvalidOperationException("Help must not save project state.");
    }

    private sealed class ThrowingEnvironmentDiagnosticPort : IEnvironmentDiagnosticPort
    {
        public IReadOnlyList<DiagnosticResult> RunEnvironmentDiagnostics()
            => throw new InvalidOperationException("Help must not access Excel or VBIDE diagnostics.");
    }
}
