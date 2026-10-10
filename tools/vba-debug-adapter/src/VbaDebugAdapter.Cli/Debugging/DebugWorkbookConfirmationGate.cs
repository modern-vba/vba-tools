using System.Text.Json;
using VbaDebugAdapter.Infrastructure;

namespace VbaDebugAdapter.Debugging;

internal sealed record DebugWorkbookConfirmationRequest(
    string SchemaVersion, string RequestId, string SessionId, int GenerationId,
    string WorkbookPath, string Message);

internal interface IDebugWorkbookConfirmationSink
{
    Task<bool> ConfirmReplacementAsync(DebugGenerationId generation, string workbookPath,
        string message, CancellationToken cancellationToken);
}

/// <summary>Correlates a single live-workbook warning to its admitted DAP generation.</summary>
internal sealed class DebugWorkbookConfirmationGate(DebugSessionId sessionId) : IDisposable
{
    private readonly object gate = new();
    private PendingConfirmation? pending;
    private bool disposed;

    internal async Task<bool> RequestAsync(DebugGenerationId generation, string workbookPath,
        string message, Func<DebugWorkbookConfirmationRequest, CancellationToken, Task> publish,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(workbookPath))
            throw new ArgumentException("The confirmation workbook path must be absolute.", nameof(workbookPath));
        var request = new DebugWorkbookConfirmationRequest("1.0", Guid.NewGuid().ToString("N"),
            sessionId.Value, generation.Value, Path.GetFullPath(workbookPath), message);
        var current = new PendingConfirmation(request);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (pending is not null)
                throw new InvalidOperationException("A workbook confirmation is already pending.");
            pending = current;
        }
        using var cancellation = cancellationToken.Register(
            () => current.Completion.TrySetCanceled(cancellationToken));
        try
        {
            await publish(request, cancellationToken).ConfigureAwait(false);
            return await current.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(pending, current)) pending = null;
            }
        }
    }

    internal bool TryComplete(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!Cli.DebugRequestAdmission.TryReadPropertyName(property, out var name)
                || name is not ("requestId" or "sessionId" or "generationId" or "accepted")
                || !names.Add(name)) return false;
        }
        if (names.Count != 4
            || !Cli.DebugRequestAdmission.TryReadString(arguments.GetProperty("requestId"), out var requestId)
            || !Cli.DebugRequestAdmission.TryReadString(arguments.GetProperty("sessionId"), out var suppliedSession)
            || arguments.GetProperty("generationId").ValueKind != JsonValueKind.Number
            || !arguments.GetProperty("generationId").TryGetInt32(out var generation)
            || arguments.GetProperty("accepted").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        lock (gate)
        {
            if (disposed || pending is not { } current
                || !current.Request.RequestId.Equals(requestId, StringComparison.Ordinal)
                || !current.Request.SessionId.Equals(suppliedSession, StringComparison.Ordinal)
                || current.Request.GenerationId != generation
                || current.Completion.Task.IsCompleted) return false;
            return current.Completion.TrySetResult(arguments.GetProperty("accepted").GetBoolean());
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            pending?.Completion.TrySetResult(false);
            pending = null;
        }
    }

    private sealed class PendingConfirmation(DebugWorkbookConfirmationRequest request)
    {
        internal DebugWorkbookConfirmationRequest Request { get; } = request;
        internal TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
