using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Rename;

// One evidence ordering for ordinary results and retained confirmed plans.
// Acquisition, leases and pending ownership remain with their existing owners.
internal sealed class RenameEvidencePolicy(
    VbaRenameProjectSnapshotCapture capture,
    bool requiresTemplate,
    bool supportsRenameFile)
{
    internal VbaRenameFailure? ValidateEvidence(VbaRenamePathDecision? paths = null)
        => capture.GetParticipatingSourceChangeFailure()
            ?? capture.GetConfirmationAuthorityChangeFailure()
            ?? (requiresTemplate ? capture.GetSourceTemplateChangeFailure() : null)
            ?? paths?.GetChangeFailure();

    internal VbaRenamePathDecisionResult PreparePaths(VbaRenamePlan plan)
    {
        if (ValidateEvidence() is { } before) return new(null, before);
        var prepared = capture.PrepareRenamePathDecision(plan);
        // Path preparation can itself inspect files. Stale proof wins even if
        // that inspection reports a domain failure.
        return ValidateEvidence(prepared.Decision) is { } after
            ? new(null, after) : prepared;
    }

    internal VbaRenameFilePreflightResult ValidatePlan(
        VbaRenamePlan plan, VbaRenamePathDecision? paths, bool retainCompletePlan = false)
    {
        if (ValidateEvidence(paths) is { } before) return new(plan, before);
        var preflight = capture.PreflightFileRenames(plan);
        // Do not short-circuit on a preflight failure: a newer invalid source,
        // authority, template or path decision has higher precedence.
        if (ValidateEvidence(paths) is { } after) return new(plan, after);
        if (preflight.Failure is not null) return new(plan, preflight.Failure);
        if (retainCompletePlan && !SameCompletePlan(plan, preflight.Plan))
            return new(plan, new VbaRenameFailure("resourceOperationConflict",
                "The retained complete Rename plan no longer matches file preflight.",
                Condition: "renamePlanChanged", Guidance: "Start a fresh Rename operation."));
        if (preflight.Plan.FileRenames.Count > 0 && !supportsRenameFile)
            return new(plan, new VbaRenameFailure("clientCapabilityMissing",
                "File-following module Rename requires ordered documentChanges and the rename resource operation."));
        return new(retainCompletePlan ? plan : preflight.Plan, null);
    }

    private static bool SameCompletePlan(VbaRenamePlan expected, VbaRenamePlan actual)
        => expected.TargetRange == actual.TargetRange
            && ReferenceEquals(expected.Changes, actual.Changes)
            && ReferenceEquals(expected.TextChanges, actual.TextChanges)
            && ReferenceEquals(expected.TargetCorrespondence, actual.TargetCorrespondence)
            && expected.FormSourceUnits.SequenceEqual(actual.FormSourceUnits)
            && expected.FileRenames.SequenceEqual(actual.FileRenames);
}
