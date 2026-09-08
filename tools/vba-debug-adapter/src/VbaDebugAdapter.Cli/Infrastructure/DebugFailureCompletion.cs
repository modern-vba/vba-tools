using System.Runtime.ExceptionServices;

namespace VbaDebugAdapter.Infrastructure;

internal enum DebugResourceKind
{
    Process,
    Com,
    Handle,
    FileSystem,
    Observation
}

internal sealed record DebugResourceEvidence(
    string Stage, string Resource, DebugResourceKind Kind, bool Released, string Reason,
    int? ProcessId = null, string? RetainedPath = null);

internal sealed record DebugCleanupFailure(
    string Stage, string Resource, DebugResourceKind Kind, Exception Exception,
    int? ProcessId = null, string? RetainedPath = null);

internal interface IDebugFailureEvidence
{
    DebugFailureOutcome FailureOutcome { get; }
}

internal interface IDebugResourceOwnerEvidence
{
    DebugFailureOutcome? CleanupOutcome { get; }
}

/// <summary>Retains observations; resource owners still perform and sequence all cleanup.</summary>
internal sealed class DebugFailureCompletion
{
    private readonly object gate = new();
    private readonly ExceptionDispatchInfo? primary;
    private readonly List<DebugCleanupFailure> failures = [];
    private readonly List<DebugResourceEvidence> evidence = [];
    private readonly HashSet<Exception> observed = new(ReferenceEqualityComparer.Instance);
    private DebugFailureOutcome? completed;

    public DebugFailureCompletion(Exception? primaryFailure = null)
    {
        if (primaryFailure is IDebugFailureEvidence retained)
        {
            primary = retained.FailureOutcome.PrimaryDispatchInfo;
            if (primary is not null) { observed.Add(primary.SourceException); }
            Import(retained.FailureOutcome, includePrimary: false);
        }
        else if (primaryFailure is not null)
        {
            primary = ExceptionDispatchInfo.Capture(primaryFailure);
            observed.Add(primaryFailure);
        }
    }

    public void AddFailure(string stage, string resource, DebugResourceKind kind,
        Exception exception, int? processId = null, string? retainedPath = null)
    {
        lock (gate)
        {
            EnsureOpen();
            if (exception is IDebugFailureEvidence retained)
            {
                Import(retained.FailureOutcome, includePrimary: true, stage, resource, kind, processId, retainedPath);
            }
            else if (exception is AggregateException aggregate)
            {
                foreach (var failure in aggregate.InnerExceptions)
                {
                    AddFailure(stage, resource, kind, failure, processId, retainedPath);
                }
            }
            else if (observed.Add(exception))
            {
                failures.Add(new(stage, resource, kind, exception, processId, retainedPath));
            }
        }
    }

    public void AddEvidence(DebugResourceEvidence observation)
    {
        lock (gate)
        {
            EnsureOpen();
            // The same owner may replace its initial unproved state with its terminal observation.
            var index = evidence.FindIndex(existing => existing.Stage == observation.Stage
                && existing.Resource == observation.Resource && existing.Kind == observation.Kind
                && existing.ProcessId == observation.ProcessId && existing.RetainedPath == observation.RetainedPath);
            if (index < 0) { evidence.Add(observation); }
            else { evidence[index] = observation; }
        }
    }

    public void Merge(DebugFailureOutcome outcome)
    {
        lock (gate)
        {
            EnsureOpen();
            Import(outcome, includePrimary: true);
        }
    }

    public DebugFailureOutcome Complete()
    {
        lock (gate)
        {
            return completed ??= new(primary, Array.AsReadOnly(failures.ToArray()),
                Array.AsReadOnly(evidence.ToArray()));
        }
    }

    private void Import(DebugFailureOutcome outcome, bool includePrimary,
        string stage = "cleanup", string resource = "owned resource",
        DebugResourceKind kind = DebugResourceKind.Handle, int? processId = null, string? retainedPath = null)
    {
        if (includePrimary && outcome.PrimaryFailure is { } failure)
        {
            // A primary cause is already unwrapped. Some owners expose their own
            // exception as the causal evidence carrier, so reopening it would cycle.
            if (observed.Add(failure))
            {
                failures.Add(new(stage, resource, kind, failure, processId, retainedPath));
            }
        }
        foreach (var cleanup in outcome.CleanupFailures)
        {
            AddFailure(cleanup.Stage, cleanup.Resource, cleanup.Kind, cleanup.Exception,
                cleanup.ProcessId, cleanup.RetainedPath);
        }
        foreach (var observation in outcome.Evidence) { AddEvidence(observation); }
    }

    private void EnsureOpen()
    {
        if (completed is not null) { throw new InvalidOperationException("Debug failure completion is already retained."); }
    }
}

internal sealed class DebugFailureOutcome(
    ExceptionDispatchInfo? primaryDispatchInfo,
    IReadOnlyList<DebugCleanupFailure> cleanupFailures,
    IReadOnlyList<DebugResourceEvidence> evidence)
{
    public ExceptionDispatchInfo? PrimaryDispatchInfo { get; } = primaryDispatchInfo;
    public Exception? PrimaryFailure => PrimaryDispatchInfo?.SourceException;
    public IReadOnlyList<DebugCleanupFailure> CleanupFailures { get; } = cleanupFailures;
    public IReadOnlyList<DebugResourceEvidence> Evidence { get; } = evidence;
    public bool HasCleanupFailure => CleanupFailures.Count != 0 || Evidence.Any(item => !item.Released);
    public bool HasUnprovedRelease => Evidence.Any(item => item.Kind != DebugResourceKind.FileSystem && !item.Released)
        || CleanupFailures.Any(failure => failure.Kind != DebugResourceKind.FileSystem
            && !Evidence.Any(item => item.Stage == failure.Stage && item.Resource == failure.Resource
                && item.Kind == failure.Kind && item.ProcessId == failure.ProcessId
                && item.RetainedPath == failure.RetainedPath && item.Released));
    public bool OnlyFileDeletionFailed => HasCleanupFailure && !HasUnprovedRelease
        && Evidence.Any(item => item.Kind != DebugResourceKind.FileSystem && item.Released)
        && Evidence.Any(item => item.Kind == DebugResourceKind.FileSystem && !item.Released)
        && CleanupFailures.All(item => item.Kind == DebugResourceKind.FileSystem);

    public string Describe()
    {
        var primary = PrimaryFailure is OperationCanceledException
            ? $"Cancelled: {PrimaryFailure.Message}" : PrimaryFailure?.Message ?? "Debug cleanup failed.";
        var details = CleanupFailures.Select(item =>
            $"{item.Stage}: {item.Resource} ({item.Kind}{Identity(item.ProcessId, item.RetainedPath)}): {item.Exception.Message}")
            .Concat(Evidence.Where(item => !item.Released).Select(item =>
                $"{item.Stage}: {item.Resource} ({item.Kind}{Identity(item.ProcessId, item.RetainedPath)}): release unproved: {item.Reason}"));
        return string.Join(Environment.NewLine, new[] { primary }.Concat(details));
    }

    public void Throw()
    {
        if (HasCleanupFailure) { throw new DebugFailureException(this); }
        PrimaryDispatchInfo?.Throw();
    }

    public void ThrowWithEvidence()
    {
        if (!HasCleanupFailure && PrimaryFailure is OperationCanceledException)
        {
            throw new DebugFailureCanceledException(this);
        }
        if (HasCleanupFailure || PrimaryFailure is not null)
        {
            throw new DebugFailureException(this);
        }
    }

    private static string Identity(int? processId, string? path)
        => (processId is null ? "" : $", PID {processId}") + (path is null ? "" : $", path {path}");
}

internal sealed class DebugFailureException(DebugFailureOutcome outcome)
    : InvalidOperationException(outcome.Describe(), new AggregateException(
        (outcome.PrimaryFailure is null ? [] : new[] { outcome.PrimaryFailure })
            .Concat(outcome.CleanupFailures.Select(item => item.Exception)))), IDebugFailureEvidence
{
    public DebugFailureOutcome FailureOutcome { get; } = outcome;
}

internal class DebugFailureCanceledException : OperationCanceledException, IDebugFailureEvidence
{
    public DebugFailureCanceledException(DebugFailureOutcome outcome)
        : this(outcome, RequireProvedCancellation(outcome)) { }

    private DebugFailureCanceledException(DebugFailureOutcome outcome, OperationCanceledException cancellation)
        : base(cancellation.Message, cancellation, cancellation.CancellationToken)
    {
        FailureOutcome = outcome;
    }

    public DebugFailureOutcome FailureOutcome { get; }

    private static OperationCanceledException RequireProvedCancellation(DebugFailureOutcome outcome)
    {
        if (!outcome.HasCleanupFailure && outcome.PrimaryFailure is OperationCanceledException cancellation)
        {
            return cancellation;
        }

        throw new ArgumentException("Ordinary debug cancellation requires proved cleanup and a cancellation cause.",
            nameof(outcome));
    }
}
