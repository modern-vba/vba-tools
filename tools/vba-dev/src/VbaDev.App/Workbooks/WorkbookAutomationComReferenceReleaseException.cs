using System.Collections.Immutable;

namespace VbaDev.App.Workbooks;

/// <summary>Identifies one acquired COM reference whose release was not proved.</summary>
internal sealed record WorkbookAutomationComReferenceReleaseFailure(
    string ReferenceName,
    Exception Error);

/// <summary>
/// Preserves the operation failure and independent acquired-reference release
/// uncertainty without asserting Excel process or STA lifetime ownership.
/// </summary>
internal sealed class WorkbookAutomationComReferenceReleaseException : Exception
{
    internal WorkbookAutomationComReferenceReleaseException(
        Exception? operationError,
        IReadOnlyList<WorkbookAutomationComReferenceReleaseFailure> releaseFailures)
        : base(CreateMessage(operationError, releaseFailures), CreateCause(operationError, releaseFailures))
    {
        OperationError = operationError;
        ReleaseFailures = releaseFailures.ToImmutableArray();
    }

    internal Exception? OperationError { get; }

    internal ImmutableArray<WorkbookAutomationComReferenceReleaseFailure> ReleaseFailures { get; }

    private static string CreateMessage(
        Exception? operationError,
        IReadOnlyList<WorkbookAutomationComReferenceReleaseFailure> releaseFailures)
    {
        ArgumentNullException.ThrowIfNull(releaseFailures);
        if (releaseFailures.Count == 0)
            throw new ArgumentException("At least one unproved COM reference release is required.", nameof(releaseFailures));
        var releaseMessage = "COM reference release could not be proved for "
            + string.Join(", ", releaseFailures.Select(failure => $"'{failure.ReferenceName}'")) + ".";
        return operationError is null ? releaseMessage : $"{operationError.Message} {releaseMessage}";
    }

    private static AggregateException CreateCause(
        Exception? operationError,
        IReadOnlyList<WorkbookAutomationComReferenceReleaseFailure> releaseFailures)
        => new(operationError is null
            ? releaseFailures.Select(failure => failure.Error)
            : new[] { operationError }.Concat(releaseFailures.Select(failure => failure.Error)));
}
