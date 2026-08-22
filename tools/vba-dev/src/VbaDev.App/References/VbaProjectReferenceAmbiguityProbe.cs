using VbaDev.App.Workbooks;

namespace VbaDev.App.References;

/// <summary>
/// Resolves registry ambiguity through one VBE-equivalent probe lifecycle.
/// </summary>
public sealed class VbaProjectReferenceAmbiguityProbe(
    IVbaProjectReferenceProbeAutomation automation,
    WorkbookAutomationTimeouts? timeouts = null)
    : IVbaProjectReferenceAmbiguityProbe
{
    private readonly WorkbookAutomationTimeouts timeouts =
        timeouts ?? WorkbookAutomationTimeouts.Default;

    /// <inheritdoc />
    public async Task<VbaProjectReferenceResolutionBatch> ResolveAsync(
        VbaProjectReferenceProbeBaseline baseline,
        VbaProjectReferenceResolutionBatch registryResolution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(registryResolution);

        try
        {
            return await automation.RunAsync(
                    baseline,
                    timeouts,
                    (session, operationCancellationToken) => ResolveAsync(
                        session,
                        registryResolution,
                        operationCancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (VbaProjectReferenceProbeBaselineException exception)
        {
            return AbortProbeDependentReferences(
                registryResolution,
                "probeBaselineUnavailable",
                exception.Message);
        }
        catch (VbaProjectReferenceProbeAttemptException exception)
        {
            if (exception.PartialResult is
                VbaProjectReferenceResolutionBatch partialResult)
            {
                return ApplyFinalLifecycleFailure(
                    registryResolution,
                    partialResult,
                    exception.ReasonCode,
                    exception.Message,
                    exception.ProcessTrusted);
            }

            return AbortAfterLifecycleFailure(
                registryResolution,
                exception.ReasonCode,
                exception.Message,
                exception.ProcessTrusted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CancelProbeDependentReferences(registryResolution);
        }
    }

    /// <summary>
    /// Resolves ambiguity against fresh copies of an explicit source-template baseline.
    /// </summary>
    public Task<VbaProjectReferenceResolutionBatch> ResolveAsync(
        string baselineWorkbookPath,
        VbaProjectReferenceResolutionBatch registryResolution,
        CancellationToken cancellationToken)
        => ResolveAsync(
            VbaProjectReferenceProbeBaseline.SourceTemplate(baselineWorkbookPath),
            registryResolution,
            cancellationToken);

    private static async Task<VbaProjectReferenceResolutionBatch> ResolveAsync(
        IVbaProjectReferenceProbeSession session,
        VbaProjectReferenceResolutionBatch registryResolution,
        CancellationToken cancellationToken)
    {
        var references = new List<VbaProjectReferenceNameResolution>(
            registryResolution.References.Count);
        var processTrusted = true;
        for (var index = 0; index < registryResolution.References.Count; index++)
        {
            var reference = registryResolution.References[index];
            if (reference.Matches.Count <= 1)
            {
                references.Add(reference);
                continue;
            }

            if (!processTrusted)
            {
                references.Add(CreateUnverified(
                    reference,
                    "probeAborted",
                    "The reference was not probed because the shared VBE process became untrusted."));
                continue;
            }

            ReferenceProbeResult probeResult;
            try
            {
                probeResult = await ResolveAsync(
                        session,
                        reference,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                references.Add(CreateUnverified(
                    reference,
                    "cancelled",
                    "Reference probing was cancelled before this entry became conclusive."));
                for (var remainingIndex = index + 1;
                     remainingIndex < registryResolution.References.Count;
                     remainingIndex++)
                {
                    var remaining = registryResolution.References[remainingIndex];
                    references.Add(remaining.Matches.Count > 1
                        ? CreateUnverified(
                            remaining,
                            "cancelled",
                            "Reference probing was cancelled before this entry was attempted.")
                        : remaining);
                }

                var cancellationDiagnostics = registryResolution.AdditionalDiagnostics?.ToList()
                                              ?? [];
                cancellationDiagnostics.Add(
                    new VbaTools.TypeLibRegistry.TypeLibRegistryCatalogDiagnostic(
                        "operationCancelled",
                        "Reference probing was cancelled."));
                return registryResolution with
                {
                    Complete = false,
                    References = references,
                    AdditionalDiagnostics = cancellationDiagnostics
                };
            }

            references.Add(probeResult.Resolution);
            processTrusted = probeResult.ProcessTrusted;
        }

        var additionalDiagnostics = registryResolution.AdditionalDiagnostics?.ToList()
                                    ?? [];
        if (!processTrusted)
        {
            additionalDiagnostics.Add(new VbaTools.TypeLibRegistry.TypeLibRegistryCatalogDiagnostic(
                "probeProcessUntrusted",
                "The owned reference probe process became untrusted; later VBE work was stopped."));
        }

        return registryResolution with
        {
            Complete = registryResolution.Complete &&
                       references.All(reference => reference.UnverifiedReasonCode is null),
            References = references,
            AdditionalDiagnostics = additionalDiagnostics
        };
    }

    private static async Task<ReferenceProbeResult> ResolveAsync(
        IVbaProjectReferenceProbeSession session,
        VbaProjectReferenceNameResolution registryResolution,
        CancellationToken cancellationToken)
    {
        var usableIdentities = new List<ResolvedVbaProjectReference>();
        string? unverifiedReasonCode = null;
        string? unverifiedMessage = null;
        foreach (var lineage in registryResolution.CandidateLineages)
        {
            foreach (var candidate in lineage.Versions
                         .OrderByDescending(version => version.Major)
                         .ThenByDescending(version => version.Minor))
            {
                VbaProjectReferenceProbeAttemptResult attempt;
                try
                {
                    attempt = await session.TryResolveAsync(
                            registryResolution.RegisteredName ?? registryResolution.RequestedName,
                            candidate,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (VbaProjectReferenceProbeAttemptException exception)
                    when (exception.ProcessTrusted)
                {
                    unverifiedReasonCode ??= exception.ReasonCode;
                    unverifiedMessage ??= exception.Message;
                    break;
                }
                catch (VbaProjectReferenceProbeAttemptException exception)
                {
                    return new ReferenceProbeResult(
                        CreateUnverified(
                            registryResolution,
                            exception.ReasonCode,
                            exception.Message),
                        ProcessTrusted: false);
                }

                if (attempt.Outcome == VbaProjectReferenceProbeAttemptOutcome.Rejected)
                {
                    continue;
                }

                var returned = attempt.Reference
                    ?? throw new InvalidOperationException(
                        "An accepted reference probe attempt did not return an identity.");
                if (!Guid.TryParse(returned.Guid, out var guid) ||
                    returned.Major is < ushort.MinValue or > ushort.MaxValue ||
                    returned.Minor is < ushort.MinValue or > ushort.MaxValue)
                {
                    unverifiedReasonCode ??= "identityReadFailure";
                    unverifiedMessage ??=
                        "The returned reference identity was missing or invalid.";
                    break;
                }

                usableIdentities.Add(new ResolvedVbaProjectReference(
                    registryResolution.RegisteredName ?? registryResolution.RequestedName,
                    guid.ToString("D").ToLowerInvariant(),
                    returned.Major,
                    returned.Minor));
                break;
            }
        }

        var matches = usableIdentities
            .DistinctBy(identity => (
                identity.Guid.ToLowerInvariant(),
                identity.Major,
                identity.Minor))
            .OrderBy(identity => identity.Guid, StringComparer.Ordinal)
            .ThenBy(identity => identity.Major)
            .ThenBy(identity => identity.Minor)
            .ToArray();
        return new ReferenceProbeResult(
            registryResolution with
            {
                Matches = unverifiedReasonCode is null ? matches : [],
                Candidates = unverifiedReasonCode is null && matches.Length > 0
                    ? matches
                    : registryResolution.Candidates,
                UnverifiedReasonCode = unverifiedReasonCode,
                Message = unverifiedMessage
            },
            ProcessTrusted: true);
    }

    private static VbaProjectReferenceNameResolution CreateUnverified(
        VbaProjectReferenceNameResolution registryResolution,
        string reasonCode,
        string message)
        => registryResolution with
        {
            Matches = [],
            Candidates = registryResolution.Candidates,
            UnverifiedReasonCode = reasonCode,
            Message = message
        };

    private static VbaProjectReferenceResolutionBatch AbortProbeDependentReferences(
        VbaProjectReferenceResolutionBatch registryResolution,
        string diagnosticCode,
        string diagnosticMessage)
    {
        var references = registryResolution.References
            .Select(reference => reference.Matches.Count > 1
                ? CreateUnverified(
                    reference,
                    "probeAborted",
                    "The reference could not be probed because its selected workbook baseline was unavailable.")
                : reference)
            .ToArray();
        var diagnostics = registryResolution.AdditionalDiagnostics?.ToList()
                          ?? [];
        diagnostics.Add(new VbaTools.TypeLibRegistry.TypeLibRegistryCatalogDiagnostic(
            diagnosticCode,
            diagnosticMessage));
        return registryResolution with
        {
            Complete = false,
            References = references,
            AdditionalDiagnostics = diagnostics
        };
    }

    private static VbaProjectReferenceResolutionBatch AbortAfterLifecycleFailure(
        VbaProjectReferenceResolutionBatch registryResolution,
        string reasonCode,
        string reasonMessage,
        bool processTrusted)
    {
        var currentFailureAssigned = false;
        var references = registryResolution.References
            .Select(reference =>
            {
                if (reference.Matches.Count <= 1)
                {
                    return reference;
                }

                if (!currentFailureAssigned)
                {
                    currentFailureAssigned = true;
                    return CreateUnverified(reference, reasonCode, reasonMessage);
                }

                return CreateUnverified(
                    reference,
                    "probeAborted",
                    "The reference was not probed because the shared VBE lifecycle ended before it was attempted.");
            })
            .ToArray();
        var diagnostics = registryResolution.AdditionalDiagnostics?.ToList()
                          ?? [];
        if (!processTrusted)
        {
            diagnostics.Add(new VbaTools.TypeLibRegistry.TypeLibRegistryCatalogDiagnostic(
                "probeProcessUntrusted",
                "The owned reference probe process became untrusted; later VBE work was stopped."));
        }

        return registryResolution with
        {
            Complete = false,
            References = references,
            AdditionalDiagnostics = diagnostics
        };
    }

    private static VbaProjectReferenceResolutionBatch CancelProbeDependentReferences(
        VbaProjectReferenceResolutionBatch registryResolution)
    {
        var references = registryResolution.References
            .Select(reference => reference.Matches.Count > 1
                ? CreateUnverified(
                    reference,
                    "cancelled",
                    "Reference probing was cancelled before this entry was attempted.")
                : reference)
            .ToArray();
        var diagnostics = registryResolution.AdditionalDiagnostics?.ToList()
                          ?? [];
        diagnostics.Add(new VbaTools.TypeLibRegistry.TypeLibRegistryCatalogDiagnostic(
            "operationCancelled",
            "Reference probing was cancelled."));
        return registryResolution with
        {
            Complete = false,
            References = references,
            AdditionalDiagnostics = diagnostics
        };
    }

    private static VbaProjectReferenceResolutionBatch ApplyFinalLifecycleFailure(
        VbaProjectReferenceResolutionBatch registryResolution,
        VbaProjectReferenceResolutionBatch partialResult,
        string reasonCode,
        string reasonMessage,
        bool processTrusted)
    {
        if (partialResult.References.Count != registryResolution.References.Count)
        {
            return AbortAfterLifecycleFailure(
                registryResolution,
                reasonCode,
                reasonMessage,
                processTrusted);
        }

        var affectedIndex = Enumerable.Range(0, registryResolution.References.Count)
            .LastOrDefault(
                index => registryResolution.References[index].Matches.Count > 1,
                -1);
        if (affectedIndex < 0)
        {
            return partialResult;
        }

        var references = partialResult.References.ToArray();
        references[affectedIndex] = references[affectedIndex] with
        {
            Matches = [],
            Candidates = registryResolution.References[affectedIndex].Candidates,
            UnverifiedReasonCode = reasonCode,
            Message = reasonMessage
        };
        var diagnostics = partialResult.AdditionalDiagnostics?.ToList()
                          ?? [];
        if (!processTrusted)
        {
            diagnostics.Add(new VbaTools.TypeLibRegistry.TypeLibRegistryCatalogDiagnostic(
                "probeProcessUntrusted",
                "The owned reference probe process became untrusted during final cleanup."));
        }

        return partialResult with
        {
            Complete = false,
            References = references,
            AdditionalDiagnostics = diagnostics
        };
    }

    private sealed record ReferenceProbeResult(
        VbaProjectReferenceNameResolution Resolution,
        bool ProcessTrusted);
}
