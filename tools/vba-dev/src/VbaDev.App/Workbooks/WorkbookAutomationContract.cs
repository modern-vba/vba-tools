namespace VbaDev.App.Workbooks;

/// <summary>
/// Defines the independent deadlines used by one hidden Excel automation process.
/// </summary>
public sealed record WorkbookAutomationTimeouts(
    TimeSpan ExcelStartup,
    TimeSpan WorkbookOpen,
    TimeSpan ReferenceAttempt,
    TimeSpan ModuleImport,
    TimeSpan WorkbookSave,
    TimeSpan ProcessCleanup)
{
    /// <summary>
    /// Gets the default workbook automation deadlines.
    /// </summary>
    public static WorkbookAutomationTimeouts Default { get; } = new(
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(300),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(300),
        TimeSpan.FromSeconds(5));
}

/// <summary>
/// Identifies one independently bounded workbook automation stage.
/// </summary>
public enum WorkbookAutomationStageKind
{
    ExcelStartup = 0,
    WorkbookOpen = 1,
    ReferenceAttempt = 2,
    ReferenceIdentityInspection = 3,
    ModuleRemoval = 4,
    ModuleImport = 5,
    Verification = 6,
    WorkbookSave = 7,
    ProcessCleanup = 8,
    OutputCommit = 9,
    TestExecution = 10
}

/// <summary>
/// Identifies the active workbook automation stage and optional item.
/// </summary>
public sealed record WorkbookAutomationStage(
    WorkbookAutomationStageKind Kind,
    string? Item = null)
{
    /// <summary>
    /// Gets a stable human-readable stage description.
    /// </summary>
    public string Description
    {
        get
        {
            var stage = Kind switch
            {
                WorkbookAutomationStageKind.ExcelStartup => "Excel startup",
                WorkbookAutomationStageKind.WorkbookOpen => "workbook open",
                WorkbookAutomationStageKind.ReferenceAttempt => "reference attempt",
                WorkbookAutomationStageKind.ReferenceIdentityInspection => "reference identity inspection",
                WorkbookAutomationStageKind.ModuleRemoval => "module removal",
                WorkbookAutomationStageKind.ModuleImport => "module import",
                WorkbookAutomationStageKind.Verification => "workbook verification",
                WorkbookAutomationStageKind.WorkbookSave => "workbook save",
                WorkbookAutomationStageKind.TestExecution => "test macro execution",
                WorkbookAutomationStageKind.ProcessCleanup => "process cleanup",
                WorkbookAutomationStageKind.OutputCommit => "output commit",
                _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null)
            };

            return string.IsNullOrWhiteSpace(Item)
                ? stage
                : $"{stage} '{Item}'";
        }
    }
}

/// <summary>
/// Reports an independently bounded Excel automation stage timeout.
/// </summary>
public sealed class WorkbookAutomationTimeoutException : TimeoutException
{
    public WorkbookAutomationTimeoutException(
        WorkbookAutomationStage stage,
        TimeSpan timeout,
        Exception? innerException = null)
        : base(
            $"Workbook automation timed out during {stage.Description} after {timeout.TotalSeconds:0.###} seconds.",
            innerException)
    {
        Stage = stage;
        Timeout = timeout;
    }

    public WorkbookAutomationStage Stage { get; }

    public TimeSpan Timeout { get; }
}

/// <summary>
/// Reports cancellation while one Excel automation stage was active.
/// </summary>
public sealed class WorkbookAutomationCanceledException : OperationCanceledException
{
    public WorkbookAutomationCanceledException(
        WorkbookAutomationStage stage,
        CancellationToken cancellationToken,
        Exception? innerException = null)
        : base(
            $"Workbook automation was cancelled during {stage.Description}.",
            innerException,
            cancellationToken)
    {
        Stage = stage;
    }

    public WorkbookAutomationStage Stage { get; }
}

/// <summary>
/// Reports unexpected loss of the exactly owned Excel process.
/// </summary>
public sealed class WorkbookAutomationProcessLostException : Exception
{
    public WorkbookAutomationProcessLostException(
        WorkbookAutomationStage stage,
        Exception? innerException = null)
        : base(
            $"The owned Excel process exited during {stage.Description}.",
            innerException)
    {
        Stage = stage;
    }

    public WorkbookAutomationStage Stage { get; }
}

/// <summary>
/// Reports failure to prove release of an owned Excel process.
/// </summary>
public sealed class WorkbookAutomationCleanupException : Exception
{
    public WorkbookAutomationCleanupException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Reports secondary automation cleanup failure after exact owned-process release was proved.
/// </summary>
public sealed class WorkbookAutomationReleasedProcessCleanupException : Exception
{
    public WorkbookAutomationReleasedProcessCleanupException(
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal static class WorkbookAutomationFailureClassifier
{
    public static bool ContainsCleanupProofFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error is WorkbookAutomationCleanupException)
        {
            return true;
        }

        if (error is AggregateException aggregate
            && aggregate.InnerExceptions.Any(ContainsCleanupProofFailure))
        {
            return true;
        }

        return error.InnerException is not null
            && ContainsCleanupProofFailure(error.InnerException);
    }
}
