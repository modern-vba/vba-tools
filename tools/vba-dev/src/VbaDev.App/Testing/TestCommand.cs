using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using VbaDev.App.Build;
using VbaDev.App.Cli;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Testing;

/// <summary>
/// Runs workbook-backed VBA tests and formats the resulting command output.
/// </summary>
public sealed class TestCommand
{
    private const string NoBuildSourceLocationWarning =
        "Warning: Source locations were omitted because --no-build runs an existing workbook without a proved source capture.";

    private readonly BuildCommand buildCommand;
    private readonly IWorkbookTestRunner workbookTestRunner;
    private readonly TestResultOutputFormatter outputFormatter;
    private readonly TestProcedureSourceLocator sourceLocator;
    private readonly SnapshotTestExecutionWorkspaceFactory snapshotWorkspaceFactory;

    /// <summary>
    /// Creates the test command.
    /// </summary>
    /// <param name="buildCommand">The build command used when the test request builds first.</param>
    /// <param name="workbookTestRunner">The workbook automation port used to execute tests.</param>
    /// <param name="outputFormatter">The formatter for text and machine-readable test output.</param>
    /// <param name="sourceLocator">The exported-source procedure locator.</param>
    public TestCommand(
        BuildCommand buildCommand,
        IWorkbookTestRunner workbookTestRunner,
        TestResultOutputFormatter outputFormatter,
        TestProcedureSourceLocator sourceLocator,
        IFileSystemPathIdentityResolver pathIdentityResolver)
        : this(
            buildCommand,
            workbookTestRunner,
            outputFormatter,
            sourceLocator,
            new SnapshotTestExecutionWorkspaceFactory(pathIdentityResolver))
    {
    }

    internal TestCommand(
        BuildCommand buildCommand,
        IWorkbookTestRunner workbookTestRunner,
        TestResultOutputFormatter outputFormatter,
        TestProcedureSourceLocator sourceLocator,
        SnapshotTestExecutionWorkspaceFactory snapshotWorkspaceFactory)
    {
        this.buildCommand = buildCommand;
        this.workbookTestRunner = workbookTestRunner;
        this.outputFormatter = outputFormatter;
        this.sourceLocator = sourceLocator;
        this.snapshotWorkspaceFactory = snapshotWorkspaceFactory;
    }

    /// <summary>
    /// Optionally builds the selected document, runs workbook tests, and formats the results.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <param name="request">The test command input.</param>
    /// <returns>A successful result when all tests pass, otherwise a failing command result with test output.</returns>
    public CommandResult Run(ResolvedProjectContext context, TestCommandRequest request)
        => RunAsync(context, request, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Optionally builds the selected document, runs workbook tests, and formats the results.
    /// </summary>
    public async Task<CommandResult> RunAsync(
        ResolvedProjectContext context,
        TestCommandRequest request,
        CancellationToken cancellationToken)
    {
        SnapshotTestExecutionWorkspace? snapshotWorkspace = null;
        ExecutedSourceIndex? executedSourceIndex = null;
        var hasCompletedTestRunOutput = false;
        var successfulBuildStandardError = string.Empty;
        CommandResult result;
        try
        {
            result = await RunCoreAsync().ConfigureAwait(false);
        }
        catch (SnapshotTestWorkspacePreparationException ex)
        {
            var preparationResult = CreatePreparationFailureResult(
                ex.PreparationError,
                cancellationToken);
            var sanitizedResult = SanitizeSnapshotOperationResult(
                preparationResult,
                ex.WorkspacePath,
                redactKnownTemporaryRoots: true);
            result = sanitizedResult with
            {
                StandardError = sanitizedResult.StandardError + ex.CleanupWarning
            };
        }
        catch (WorkbookAutomationCanceledException ex)
        {
            result = PreserveReleaseProof(ex, CommandResult.Cancelled(ex.Message));
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            result = PreserveReleaseProof(
                ex,
                CommandResult.Cancelled(
                    "Workbook automation was cancelled during the active test stage."));
        }
        catch (WorkbookAutomationTimeoutException ex)
        {
            result = PreserveReleaseProof(ex, CommandResult.UsageError(ex.Message));
        }
        catch (WorkbookAutomationProcessLostException ex)
        {
            result = PreserveReleaseProof(ex, CommandResult.UsageError(ex.Message));
        }
        catch (WorkbookAutomationCleanupException ex)
        {
            result = PreserveReleaseProof(ex, CommandResult.UsageError(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            result = PreserveReleaseProof(ex, CommandResult.UsageError(ex.Message));
        }
        catch (IOException ex)
        {
            result = PreserveReleaseProof(ex, CommandResult.UsageError(ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            result = PreserveReleaseProof(ex, CommandResult.UsageError(ex.Message));
        }
        catch (COMException ex)
        {
            result = PreserveReleaseProof(
                ex,
                CommandResult.UsageError(
                    CommandErrorMessages.ExcelComAutomationFailed("test", ex)));
        }
        catch (Exception ex)
        {
            result = PreserveReleaseProof(ex, CommandResult.UsageError(ex.Message));
        }

        if (successfulBuildStandardError.Length > 0)
        {
            result = result with
            {
                StandardError = successfulBuildStandardError + result.StandardError
            };
        }

        if (snapshotWorkspace is null)
        {
            return result;
        }

        result = SanitizeSnapshotOperationResult(
            result,
            snapshotWorkspace,
            redactKnownTemporaryRoots: !hasCompletedTestRunOutput);
        if (result.OwnedProcessReleaseProof == OwnedProcessReleaseProof.Unproven)
        {
            return result with
            {
                StandardError = result.StandardError +
                    $"The snapshot test workspace was retained because owned Excel process release could not be proved: {snapshotWorkspace.WorkspacePath}{Environment.NewLine}"
            };
        }

        var cleanup = snapshotWorkspace.Cleanup();
        return cleanup.Warning is null
            ? result
            : result with { StandardError = result.StandardError + cleanup.Warning };

        async Task<CommandResult> RunCoreAsync()
        {
            var workbookPath = context.BinDocumentPath;
            if (request.SourceSnapshotPath is not null)
            {
                snapshotWorkspace = snapshotWorkspaceFactory.Create(
                    context,
                    request.SourceSnapshotPath,
                    Path.GetFileName(context.BinDocumentPath),
                    cancellationToken);
                var buildResult = await buildCommand.RunSnapshotIntentAsync(
                        context,
                        snapshotWorkspace.TakeSourceCapture(),
                        snapshotWorkspace.WorkbookPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (buildResult.CommandResult.ExitCode != 0)
                {
                    return buildResult.CommandResult;
                }

                successfulBuildStandardError = buildResult.CommandResult.StandardError;

                workbookPath = buildResult.CommittedArtifactPath
                    ?? throw new InvalidOperationException(
                        "Snapshot materialization succeeded without a committed workbook path.");
                executedSourceIndex = sourceLocator.CreateIndex(
                    buildResult.SourceAdmission
                        ?? throw new InvalidOperationException(
                            "Snapshot materialization succeeded without its admitted source capture."),
                    snapshotWorkspace.SourceRootPath,
                    context.DocumentSourceSetPath);
            }
            else if (request.BuildFirst)
            {
                var buildResult = await buildCommand.RunTestBuildIntentAsync(
                        context,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (buildResult.CommandResult.ExitCode != 0)
                {
                    return buildResult.CommandResult;
                }

                successfulBuildStandardError = buildResult.CommandResult.StandardError;
                workbookPath = buildResult.CommittedArtifactPath
                    ?? throw new InvalidOperationException(
                        "Test materialization succeeded without a committed workbook path.");
                executedSourceIndex = sourceLocator.CreateIndex(
                    buildResult.SourceAdmission
                        ?? throw new InvalidOperationException(
                            "Test materialization succeeded without its admitted source capture."),
                    context.DocumentSourceSetPath,
                    context.DocumentSourceSetPath);
            }

            if (!File.Exists(workbookPath))
            {
                return CommandResult.UsageError($"Bin workbook was not found: {workbookPath}");
            }

            var resultRows = await workbookTestRunner.RunTestsAsync(
                    workbookPath,
                    request.Selector,
                    request.ExecutionTimeout,
                    WorkbookAutomationTimeouts.Default with
                    {
                        WorkbookOpen = CommandDefaultResolver.ResolveWorkbookOpenTimeout(
                            context.Manifest),
                        WorkbookSave = CommandDefaultResolver.ResolveWorkbookSaveTimeout(
                            context.Manifest)
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var results = resultRows
                .Select(row => TestResultRecord.FromWorkbookRow(context.DocumentName, row))
                .Select(result => snapshotWorkspace is null
                    ? result
                    : SanitizeSnapshotTestResult(result, snapshotWorkspace))
                .ToArray();
            var locatedResults = executedSourceIndex is null
                ? results
                : sourceLocator.Locate(executedSourceIndex, results);
            var testRun = TestRun.FromResults(
                context.Manifest.ProjectName,
                context.DocumentName,
                locatedResults);
            var output = outputFormatter.Format(request.Format, testRun);
            var locationWarnings = executedSourceIndex is null
                ? $"{NoBuildSourceLocationWarning}{Environment.NewLine}"
                : RenderSourceLocationWarnings(locatedResults);

            var commandResult = testRun.HasFailures
                ? CommandResult.Failure(output)
                : CommandResult.Success(output);
            hasCompletedTestRunOutput = true;
            return commandResult with { StandardError = locationWarnings };
        }
    }

    private static CommandResult PreserveReleaseProof(Exception error, CommandResult result)
        => WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(error)
            ? result.MarkOwnedProcessReleaseUnproven()
            : result;

    private static CommandResult CreatePreparationFailureResult(
        Exception error,
        CancellationToken cancellationToken)
    {
        CommandResult result;
        if (cancellationToken.IsCancellationRequested
            && ContainsCancellation(error))
        {
            result = CommandResult.Cancelled(
                "Workbook automation was cancelled during snapshot test preparation.");
        }
        else
        {
            result = error switch
            {
                COMException comError => CommandResult.UsageError(
                    CommandErrorMessages.ExcelComAutomationFailed("test", comError)),
                _ => CommandResult.UsageError(error.Message)
            };
        }

        return PreserveReleaseProof(error, result);
    }

    private static bool ContainsCancellation(Exception error)
    {
        if (error is OperationCanceledException)
        {
            return true;
        }

        if (error is AggregateException aggregate
            && aggregate.InnerExceptions.Any(ContainsCancellation))
        {
            return true;
        }

        return error.InnerException is not null
            && ContainsCancellation(error.InnerException);
    }

    private static string RenderSourceLocationWarnings(
        IReadOnlyList<TestResultRecord> results)
        => string.Concat(
            results
                .Where(result => result.Location is null)
                .Select(result => $"{result.Category}.{result.TestName}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(identity =>
                    $"Warning: Source location for '{identity}' was omitted because it could not be mapped safely or unambiguously from the executed source capture to the persistent source set.{Environment.NewLine}"));

    private static TestResultRecord SanitizeSnapshotTestResult(
        TestResultRecord result,
        SnapshotTestExecutionWorkspace workspace)
        => result with
        {
            Category = SanitizeSnapshotOperationText(
                result.Category,
                workspace.WorkspacePath,
                redactKnownTemporaryRoots: false),
            TestName = SanitizeSnapshotOperationText(
                result.TestName,
                workspace.WorkspacePath,
                redactKnownTemporaryRoots: false),
            Message = SanitizeSnapshotOperationText(
                result.Message,
                workspace.WorkspacePath,
                redactKnownTemporaryRoots: false)
        };

    private static CommandResult SanitizeSnapshotOperationResult(
        CommandResult result,
        SnapshotTestExecutionWorkspace workspace,
        bool redactKnownTemporaryRoots)
        => SanitizeSnapshotOperationResult(
            result,
            workspace.WorkspacePath,
            redactKnownTemporaryRoots);

    private static CommandResult SanitizeSnapshotOperationResult(
        CommandResult result,
        string workspacePath,
        bool redactKnownTemporaryRoots)
        => result with
        {
            StandardOutput = SanitizeSnapshotOperationText(
                result.StandardOutput,
                workspacePath,
                redactKnownTemporaryRoots),
            StandardError = SanitizeSnapshotOperationText(
                result.StandardError,
                workspacePath,
                redactKnownTemporaryRoots)
        };

    private static string SanitizeSnapshotOperationText(
        string text,
        string workspacePath,
        bool redactKnownTemporaryRoots)
    {
        var normalizedWorkspacePath = Path.GetFullPath(workspacePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sanitized = ReplacePrivatePathForms(
            text,
            normalizedWorkspacePath,
            "<snapshot-test-workspace>");
        if (!redactKnownTemporaryRoots)
        {
            return sanitized;
        }

        sanitized = ReplacePrivateGuidRoot(
            sanitized,
            Path.Combine(Path.GetTempPath(), "vba-dev-build-source-snapshot"),
            "<build-source-snapshot>");
        return ReplacePrivateGuidRoot(
            sanitized,
            Path.Combine(Path.GetTempPath(), "vba-dev-vbe-import"),
            "<vbe-import-staging>");
    }

    private static string ReplacePrivatePathForms(
        string text,
        string normalizedPath,
        string replacement)
    {
        var sanitized = text.Replace(
            normalizedPath,
            replacement,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
        return sanitized.Replace(
            new Uri(normalizedPath).AbsoluteUri.TrimEnd('/'),
            replacement,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ReplacePrivateGuidRoot(
        string text,
        string privateRoot,
        string replacement)
    {
        var normalizedRoot = Path.GetFullPath(privateRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sanitized = ReplacePrivateGuidRootForm(
            text,
            normalizedRoot,
            $"[{Regex.Escape(string.Concat(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar))}]",
            replacement);
        return ReplacePrivateGuidRootForm(
            sanitized,
            new Uri(normalizedRoot).AbsoluteUri.TrimEnd('/'),
            "/",
            replacement);
    }

    private static string ReplacePrivateGuidRootForm(
        string text,
        string privateRoot,
        string separatorPattern,
        string replacement)
    {
        var pattern = Regex.Escape(privateRoot)
            + separatorPattern
            + "[0-9a-f]{32}";
        return Regex.Replace(
            text,
            pattern,
            replacement,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
