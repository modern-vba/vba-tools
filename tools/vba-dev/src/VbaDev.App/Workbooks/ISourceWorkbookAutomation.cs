namespace VbaDev.App.Workbooks;

/// <summary>
/// Opens the exact selected source workbook for an in-place Build or project Export.
/// Borrowed user sessions are never closed or terminated by the command.
/// </summary>
public interface ISourceWorkbookAutomation
{
    Task<TResult> RunAsync<TResult>(
        string workbookPath,
        WorkbookAutomationTimeouts timeouts,
        Func<ISourceWorkbookSession, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken);
}

/// <summary>
/// Exposes workbook operations and the save boundary without granting lifecycle authority.
/// </summary>
public interface ISourceWorkbookSession : IWorkbookGenerationSession
{
    bool WasAlreadyOpen { get; }

    Task<bool> IsSavedAsync(CancellationToken cancellationToken);

    SourceWorkbookSaveState SaveState { get; }
}

/// <summary>
/// Reports whether native Workbook.Save was not started, may have run, or returned normally.
/// </summary>
public enum SourceWorkbookSaveState
{
    NotStarted,
    Unknown,
    Saved
}
