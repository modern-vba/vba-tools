using System.Text.RegularExpressions;
using VbaDev.App.Build;
using VbaDev.App.Cli;
using VbaDev.App.FileSystem;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Testing;

/// <summary>Runs tests on the exact selected source workbook without tool-initiated Save.</summary>
public sealed class TestCommand
{
    private readonly SourceWorkbookTestCommand? sourceWorkbookTestCommand;

    /// <summary>Creates a source Test command through the original composition signature.</summary>
    /// <remarks>The path runner is retained for source compatibility, never used to reopen a workbook.</remarks>
    public TestCommand(
        BuildCommand buildCommand,
        IWorkbookTestRunner workbookTestRunner,
        TestResultOutputFormatter outputFormatter,
        TestProcedureSourceLocator sourceLocator,
        IFileSystemPathIdentityResolver pathIdentityResolver,
        IExactFileSystemObjectOwnershipFactory ownershipFactory)
        : this(buildCommand, workbookTestRunner, outputFormatter, sourceLocator,
            new SnapshotTestExecutionWorkspaceFactory(ownershipFactory, pathIdentityResolver))
    {
    }

    internal TestCommand(
        BuildCommand buildCommand,
        IWorkbookTestRunner workbookTestRunner,
        TestResultOutputFormatter outputFormatter,
        TestProcedureSourceLocator sourceLocator,
        SnapshotTestExecutionWorkspaceFactory snapshotWorkspaceFactory,
        SourceWorkbookTestCommand? sourceWorkbookTestCommand = null)
    {
        this.sourceWorkbookTestCommand = (sourceWorkbookTestCommand
            ?? buildCommand.SourceWorkbookTestCommand)?.WithSnapshotWorkspaceFactory(snapshotWorkspaceFactory);
    }

    /// <summary>Imports admitted sources when requested, then executes bound source-workbook tests.</summary>
    public CommandResult Run(ResolvedProjectContext context, TestCommandRequest request)
        => RunAsync(context, request, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Runs source-workbook tests with cooperative cancellation.</summary>
    public Task<CommandResult> RunAsync(
        ResolvedProjectContext context,
        TestCommandRequest request,
        CancellationToken cancellationToken)
        => RunAsync(context, request, null, cancellationToken);

    /// <summary>Runs source tests with explicit unsaved-workbook confirmation.</summary>
    public Task<CommandResult> RunAsync(
        ResolvedProjectContext context,
        TestCommandRequest request,
        Func<string, CancellationToken, Task<bool>>? confirmUnsavedChanges,
        CancellationToken cancellationToken)
        => sourceWorkbookTestCommand is null
            ? Task.FromResult(CommandResult.UsageError(
                "Source workbook Test automation was not configured; no bin fallback or path reopening was attempted."))
            : sourceWorkbookTestCommand.RunAsync(context, request, confirmUnsavedChanges, cancellationToken);

    internal static string SanitizeSnapshotOperationText(
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
        sanitized = ReplacePrivateGuidRoot(
            sanitized,
            Path.Combine(Path.GetTempPath(), "vba-dev-vbe-import"),
            "<vbe-import-staging>");
        return ReplacePrivateGuidRoot(
            sanitized,
            Path.Combine(Path.GetTempPath(), "vba-dev-vbe-import", "source-workbook-build"),
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
