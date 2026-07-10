using VbaDev.App.Build;
using VbaDev.App.Cli;
using VbaDev.App.Projects;

namespace VbaDev.App.Testing;

public sealed class TestCommand
{
    private readonly BuildCommand buildCommand;
    private readonly IWorkbookTestRunner workbookTestRunner;
    private readonly TestResultOutputFormatter outputFormatter;

    public TestCommand(
        BuildCommand buildCommand,
        IWorkbookTestRunner workbookTestRunner,
        TestResultOutputFormatter outputFormatter)
    {
        this.buildCommand = buildCommand;
        this.workbookTestRunner = workbookTestRunner;
        this.outputFormatter = outputFormatter;
    }

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
            var output = outputFormatter.Format(request.Format, results);

            return results.Any(result => !result.Outcome.Equals(TestOutcome.Passed, StringComparison.OrdinalIgnoreCase))
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
    }
}
