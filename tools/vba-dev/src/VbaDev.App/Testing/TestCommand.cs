using System.Runtime.InteropServices;
using VbaDev.App.Build;
using VbaDev.App.Cli;
using VbaDev.App.Projects;

namespace VbaDev.App.Testing;

/// <summary>
/// Runs workbook-backed VBA tests and formats the resulting command output.
/// </summary>
public sealed class TestCommand
{
    private readonly BuildCommand buildCommand;
    private readonly IWorkbookTestRunner workbookTestRunner;
    private readonly TestResultOutputFormatter outputFormatter;
    private readonly TestProcedureSourceLocator sourceLocator;

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
        TestProcedureSourceLocator sourceLocator)
    {
        this.buildCommand = buildCommand;
        this.workbookTestRunner = workbookTestRunner;
        this.outputFormatter = outputFormatter;
        this.sourceLocator = sourceLocator;
    }

    /// <summary>
    /// Optionally builds the selected document, runs workbook tests, and formats the results.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <param name="request">The test command input.</param>
    /// <returns>A successful result when all tests pass, otherwise a failing command result with test output.</returns>
    public CommandResult Run(ResolvedProjectContext context, TestCommandRequest request)
    {
        try
        {
            if (request.BuildFirst)
            {
                var buildResult = buildCommand.Run(context);
                if (buildResult.ExitCode != 0)
                {
                    return buildResult;
                }
            }

            if (!File.Exists(context.BinDocumentPath))
            {
                return CommandResult.UsageError($"Bin workbook was not found: {context.BinDocumentPath}");
            }

            var results = workbookTestRunner
                .RunTests(context.BinDocumentPath, request.Selector)
                .Select(row => TestResultRecord.FromWorkbookRow(context.DocumentName, row))
                .ToArray();
            var testRun = TestRun.FromResults(
                context.Manifest.ProjectName,
                context.DocumentName,
                sourceLocator.Locate(context.DocumentSourceSetPath, results));
            var output = outputFormatter.Format(request.Format, testRun);

            return testRun.HasFailures
                ? CommandResult.Failure(output)
                : CommandResult.Success(output);
        }
        catch (InvalidOperationException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
        catch (IOException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
        catch (COMException ex)
        {
            return CommandResult.UsageError(CommandErrorMessages.ExcelComAutomationFailed("test", ex));
        }
    }
}
