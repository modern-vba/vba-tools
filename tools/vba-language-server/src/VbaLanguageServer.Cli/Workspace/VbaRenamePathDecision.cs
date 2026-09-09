using VbaLanguageServer.SourceModel;

namespace VbaLanguageServer.Workspace;

internal sealed class VbaRenamePathDecision(
    bool retainOriginalPaths,
    IReadOnlyList<VbaRenameConflict> conflicts,
    IReadOnlyList<string> retainedPaths,
    Func<VbaRenameFailure?> getChangeFailure)
{
    public bool RetainOriginalPaths { get; } = retainOriginalPaths;

    public IReadOnlyList<VbaRenameConflict> Conflicts { get; } =
        Array.AsReadOnly(conflicts.ToArray());

    public IReadOnlyList<string> RetainedPaths { get; } =
        Array.AsReadOnly(retainedPaths.ToArray());

    public VbaRenameFailure? GetChangeFailure() => getChangeFailure();

    internal static VbaRenamePathDecision FollowingWithoutFileEvidence()
        => new(false, [], [], static () => null);
}

internal sealed record VbaRenamePathDecisionResult(
    VbaRenamePathDecision? Decision,
    VbaRenameFailure? Failure);
