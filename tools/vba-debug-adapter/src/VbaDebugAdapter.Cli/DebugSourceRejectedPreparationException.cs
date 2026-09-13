using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;

namespace VbaDebugAdapter.Cli;

/// <summary>
/// Carries a source admission rejection through the preparation ownership boundary.
/// A rejection raised by another stage does not confer this request-only outcome.
/// </summary>
internal sealed class DebugSourceRejectedPreparationException(DebugFailureOutcome outcome)
    : DebugSetupException(outcome.Describe()), IDebugFailureEvidence
{
    public DebugFailureOutcome FailureOutcome { get; } = outcome;
}
