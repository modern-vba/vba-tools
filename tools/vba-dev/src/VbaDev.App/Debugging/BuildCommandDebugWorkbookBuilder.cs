using VbaDev.App.Build;
using VbaDev.App.Cli;
using VbaDev.App.Projects;

namespace VbaDev.App.Debugging;

/// <summary>
/// Adapts the normal manifest-aware workbook build command to debug launch orchestration.
/// </summary>
public sealed class BuildCommandDebugWorkbookBuilder : IDebugWorkbookBuilder
{
    private readonly Func<ResolvedProjectContext, CommandResult> runBuild;

    /// <summary>
    /// Creates a debug workbook builder over the normal build command.
    /// </summary>
    public BuildCommandDebugWorkbookBuilder(BuildCommand buildCommand)
        : this(buildCommand.Run)
    {
    }

    internal BuildCommandDebugWorkbookBuilder(
        Func<ResolvedProjectContext, CommandResult> runBuild)
    {
        this.runBuild = runBuild;
    }

    /// <inheritdoc />
    public Task<DebugWorkbookBuildResult> BuildAsync(
        ResolvedProjectContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = runBuild(context);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;
            throw new DebugSetupException(
                string.IsNullOrWhiteSpace(detail)
                    ? "Workbook build failed before the debug Excel process could start."
                    : detail.Trim());
        }

        var output = result.StandardOutput
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return Task.FromResult(new DebugWorkbookBuildResult(output));
    }
}
