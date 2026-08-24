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
        using var capabilities = JsonDocument.Parse(standardOutput.ToString());
        var activeCodePageProperty = OperatingSystem.IsWindows()
            ? $"\"activeWindowsCodePage\":{capabilities.RootElement.GetProperty("activeWindowsCodePage").GetInt32()},"
            : string.Empty;
        Assert.Equal(
            "{\"toolVersion\":\"0.1.0\",\"contractVersion\":\"1.0\",\"featureVersions\":{\"build.sourceSnapshot\":\"1.0\",\"test.sourceSnapshot\":\"1.0\",\"sourceSnapshot.activeWindowsCodePage\":\"1.0\"}," +
            activeCodePageProperty +
            "\"commands\":{\"build\":{\"outputSchemaVersion\":\"1.0\"},\"common-module add\":{\"outputSchemaVersion\":\"1.0\"},\"common-module list\":{\"outputSchemaVersion\":\"1.0\"},\"common-module update\":{\"outputSchemaVersion\":\"1.0\"},\"doctor\":{\"outputSchemaVersion\":\"1.0\"},\"export\":{\"outputSchemaVersion\":\"1.0\"},\"import\":{\"outputSchemaVersion\":\"1.0\"},\"new excel\":{\"outputSchemaVersion\":\"1.0\"},\"publish\":{\"outputSchemaVersion\":\"1.0\"},\"reference add\":{\"outputSchemaVersion\":\"1.0\"},\"reference list\":{\"outputSchemaVersion\":\"1.0\"},\"reference remove\":{\"outputSchemaVersion\":\"1.0\"},\"test\":{\"outputSchemaVersion\":\"1.2\"}}}" + Environment.NewLine,
            standardOutput.ToString());
        Assert.Empty(standardError.ToString());
    }

    [Fact]
    public async Task RemovedDebugAdapterCommandIsRejectedThroughPublicCommandLineBoundary()
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var commandLine = VbaDevCommandLine.CreateDefault();

        var exitCode = await commandLine.InvokeAsync(
            ["debug-adapter", "--stdio"],
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("debug-adapter", standardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapabilitiesReportTheActiveWindowsCodePageForSnapshotProducers()
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
        using var document = JsonDocument.Parse(standardOutput.ToString());
        if (OperatingSystem.IsWindows())
        {
            Assert.True(document.RootElement.TryGetProperty("activeWindowsCodePage", out var codePage));
            Assert.True(codePage.GetInt32() > 0);
        }
        else
        {
            Assert.False(document.RootElement.TryGetProperty("activeWindowsCodePage", out _));
        }
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
        Assert.Contains("--source-snapshot <dir>", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("--timeout-seconds <seconds>", standardOutput.ToString(), StringComparison.Ordinal);
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
            ["reference list"] = ["--project <path>", "--document <name>", "--available", "--format <text|json>", "-f"],
            ["reference remove"] = ["--project <path>", "--document <name>", "-d"],
            ["build"] =
            [
                "--project <path>",
                "--document <name>",
                "-d",
                "--source-snapshot <dir>",
                "--output <workbook>"
            ],
            ["test"] =
            [
                "--project <path>",
                "--document <name>",
                "--format <text|ndjson>",
                "--no-build",
                "--source-snapshot <dir>",
                "--timeout-seconds <seconds>"
            ],
            ["publish"] = ["--project <path>", "--document <name>", "-d"],
            ["export"] = ["--project <path>", "--document <name>", "--from <path>", "--to <dir>"],
            ["import"] = ["--from <dir>", "--to <path>"],
            ["check"] = ["--project <path>"],
            ["doctor"] = ["--project <path>", "--scope <project|environment>", "--format <text|json>"],
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
                     "new", "common-module", "reference", "build", "test", "publish", "export", "import", "check", "doctor", "capabilities"
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
        foreach (var commandName in new[] { "new", "common-module", "reference", "build", "test", "publish", "export", "import", "check", "doctor" })
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
        foreach (var commandName in new[] { "common-module update", "check", "doctor" })
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
        Assert.DoesNotContain("\"debugAdapter\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public void CapabilitiesAdvertiseSnapshotBuildFeatureVersion()
    {
        var result = application.Run(["capabilities", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        using var capabilities = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            "1.0",
            capabilities.RootElement
                .GetProperty("featureVersions")
                .GetProperty("build.sourceSnapshot")
                .GetString());
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public void CapabilitiesAdvertiseSnapshotTestFeatureVersion()
    {
        var result = application.Run(["capabilities", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        using var capabilities = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            "1.0",
            capabilities.RootElement
                .GetProperty("featureVersions")
                .GetProperty("test.sourceSnapshot")
                .GetString());
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
    public void ReferenceHelpDoesNotEvaluateDynamicCompletionSources()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, ProjectManifest.ManifestFileName),
            "completion help sentinel");
        var manifestStore = new CountingProjectManifestStore(
            ProjectManifest.CreateDefault("Project", "Book1", temp.Path, null));
        var referenceResolver = new CountingReferenceResolver();
        var commandLine = CommandLineTestFactory.Create(
            temp.Path,
            vbaProjectReferenceResolver: referenceResolver,
            projectManifestStore: manifestStore);

        var addHelp = commandLine.Run(["reference", "add", "--help"]);
        var removeHelp = commandLine.Run(["reference", "remove", "--help"]);

        Assert.Equal(0, addHelp.ExitCode);
        Assert.Equal(0, removeHelp.ExitCode);
        Assert.Contains("<references>...", addHelp.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("<references>...", removeHelp.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(addHelp.StandardError);
        Assert.Empty(removeHelp.StandardError);
        Assert.Equal(0, manifestStore.LoadCount);
        Assert.Equal(0, referenceResolver.ResolveAvailableCount);
        Assert.Equal(0, referenceResolver.ResolveCount);
    }

    [Fact]
    public void StaticCompletionComesFromTheInvokedRootCommandGraph()
    {
        var result = application.Run(["[suggest:1]", "b"]);
        var suggestions = result.StandardOutput.Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);
        var checkResult = application.Run(["[suggest:1]", "c"]);
        var checkSuggestions = checkResult.StandardOutput.Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("build", suggestions);
        Assert.Contains("publish", suggestions);
        Assert.Contains("capabilities", suggestions);
        Assert.Equal(0, checkResult.ExitCode);
        Assert.Contains("check", checkSuggestions);
        Assert.DoesNotContain("debug-adapter", suggestions);
        Assert.Empty(result.StandardError);
        Assert.Empty(checkResult.StandardError);
    }

    [Fact]
    public void StaticOptionCompletionComesFromTheInvokedRootCommandGraph()
    {
        var line = "reference add --";

        var result = application.Run([$"[suggest:{line.Length}]", line]);
        var suggestions = result.StandardOutput.Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--project", suggestions);
        Assert.Contains("--document", suggestions);
        Assert.Contains("--help", suggestions);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public void PowerShellCompletionRegistrationScriptIsWrittenToStandardOutput()
    {
        var result = application.Run(["completions", "script", "pwsh"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Register-ArgumentCompleter", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("$PROFILE", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Set-Content", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Add-Content", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Out-File", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Import-Module", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dotnet-suggest", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".psm1", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Visual Studio Code", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public void PowerShellCompletionScriptEmbedsAndRegistersTheExactGeneratingExecutable()
    {
        const string executablePath = @"C:\Program Files\VBA Tools\owner's 日本語\vba-dev.exe";
        var commandLine = CommandLineTestFactory.Create(
            Directory.GetCurrentDirectory(),
            generatingExecutablePath: executablePath);

        var result = commandLine.Run(["completions", "script", "pwsh"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "$vbaDevExecutable = 'C:\\Program Files\\VBA Tools\\owner''s 日本語\\vba-dev.exe'",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            "-CommandName @('vba-dev', 'vba-dev.exe', $vbaDevExecutable)",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public void PowerShellCompletionScriptInvokesTheStandardSuggestDirective()
    {
        var result = application.Run(["completions", "script", "pwsh"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "$suggestDirective = \"[suggest:$completionCursor]\"",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            "& $vbaDevExecutable $suggestDirective $completionCommandLine",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public void CompletionDoesNotIntroduceAPublicCompleteCommand()
    {
        var complete = application.Run(["complete"]);
        var help = application.Run(["--help"]);

        Assert.Equal(1, complete.ExitCode);
        Assert.Contains("complete", complete.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, help.ExitCode);
        Assert.DoesNotContain(
            Environment.NewLine + "  complete ",
            help.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            Environment.NewLine + "  completions ",
            help.StandardOutput,
            StringComparison.Ordinal);
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

    private sealed class CountingProjectManifestStore(ProjectManifest manifest)
        : IProjectManifestStore
    {
        public int LoadCount { get; private set; }

        public ProjectManifest Load(string manifestPath)
        {
            LoadCount++;
            return manifest;
        }

        public void Save(string projectRoot, ProjectManifest projectManifest)
            => throw new InvalidOperationException("Project manifest writes were not expected.");
    }

    private sealed class CountingReferenceResolver : VbaDev.App.Workbooks.IVbaProjectReferenceResolver
    {
        public int ResolveAvailableCount { get; private set; }

        public int ResolveCount { get; private set; }

        public VbaDev.App.Workbooks.VbaProjectReferenceResolutionBatch ResolveAvailable()
        {
            ResolveAvailableCount++;
            return new VbaDev.App.Workbooks.VbaProjectReferenceResolutionBatch(true, [], null, []);
        }

        public VbaDev.App.Workbooks.VbaProjectReferenceResolutionBatch Resolve(
            IReadOnlyList<string> referenceNames)
        {
            ResolveCount++;
            return new VbaDev.App.Workbooks.VbaProjectReferenceResolutionBatch(true, [], null, []);
        }
    }

    private sealed class ThrowingEnvironmentDiagnosticPort : IEnvironmentDiagnosticPort
    {
        public Task<EnvironmentDiagnosticRun> RunEnvironmentDiagnosticsAsync(
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Help must not access Excel or VBIDE diagnostics.");
    }
}
