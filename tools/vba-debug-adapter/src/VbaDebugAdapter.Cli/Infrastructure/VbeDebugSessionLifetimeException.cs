namespace VbaDebugAdapter.Infrastructure;

internal sealed class VbeDebugSessionLifetimeException(
    string terminalCause, Exception? primaryFailure, IReadOnlyList<Exception> cleanupFailures)
    : InvalidOperationException(
        $"The native Excel/VBE session ended because of {terminalCause}, with additional cleanup failures.",
        new AggregateException(primaryFailure is null ? cleanupFailures : cleanupFailures.Prepend(primaryFailure)))
{
    public string TerminalCause { get; } = terminalCause;
    public Exception? PrimaryFailure { get; } = primaryFailure;
    public IReadOnlyList<Exception> CleanupFailures { get; } = cleanupFailures;
}
