namespace VbaDebugAdapter.Infrastructure;

internal sealed class VbeDebugSessionLifetimeException(string terminalCause, DebugFailureOutcome outcome)
    : InvalidOperationException(
        $"The native Excel/VBE session ended because of {terminalCause}, with additional cleanup failures.{Environment.NewLine}{outcome.Describe()}",
        new AggregateException((outcome.PrimaryFailure is null ? [] : new[] { outcome.PrimaryFailure })
            .Concat(outcome.CleanupFailures.Select(item => item.Exception)))), IDebugFailureEvidence
{
    public string TerminalCause { get; } = terminalCause;
    public Exception? PrimaryFailure => FailureOutcome.PrimaryFailure;
    public IReadOnlyList<Exception> CleanupFailures => FailureOutcome.CleanupFailures.Select(item => item.Exception).ToArray();
    public DebugFailureOutcome FailureOutcome { get; } = outcome;
}
