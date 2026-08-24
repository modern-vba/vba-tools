using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Diagnostics;

/// <summary>
/// Adds manifest-defined VBA project reference diagnostics.
/// </summary>
public sealed class VbaProjectReferenceDiagnosticProvider
    : IActiveDoctorProjectDiagnosticProvider
{
    private readonly VbaProjectReferencePlanner referencePlanner;

    /// <summary>
    /// Creates a VBA project reference diagnostic provider.
    /// </summary>
    /// <param name="referencePlanner">The planner used to resolve and diagnose references.</param>
    public VbaProjectReferenceDiagnosticProvider(
        VbaProjectReferencePlanner referencePlanner)
    {
        this.referencePlanner = referencePlanner;
    }

    /// <inheritdoc />
    public async Task AddDiagnosticsAsync(
        List<DiagnosticResult> results,
        ResolvedProject project,
        CancellationToken cancellationToken)
    {
        foreach (var (documentName, document) in project.Manifest.Documents.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var consistencyDiagnostic = referencePlanner.CreateManifestReferenceConsistencyDiagnostic(documentName, document);
            if (consistencyDiagnostic is not null)
            {
                results.Add(consistencyDiagnostic);
            }

            results.AddRange(referencePlanner.CreateReferenceCatalogAvailabilityDiagnostics(documentName, document));
            var referencesToResolve = document.References
                .GroupBy(reference => reference.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            VbaProjectReferenceResolutionBatch? resolutionBatch = null;
            if (referencesToResolve.Length > 0)
            {
                resolutionBatch = await referencePlanner.ResolveReferencesAsync(
                        project.ResolvePath(document.TemplatePath),
                        referencesToResolve.Select(reference => reference.Name).ToArray(),
                        cancellationToken)
                    .ConfigureAwait(false);
                AddResolutionBatchDiagnostics(results, documentName, resolutionBatch);
            }

            var resolutionIndex = 0;
            foreach (var reference in referencesToResolve)
            {
                if (resolutionBatch is null ||
                    resolutionIndex >= resolutionBatch.References.Count)
                {
                    results.Add(DiagnosticResult.Fail(
                        ReferenceCheckId(documentName, reference.Name, "resolution"),
                        $"VbaProjectReferences ({documentName}/{reference.Name})",
                        "Reference resolver returned an incomplete ordered result batch."));
                    continue;
                }

                var resolution = resolutionBatch.References[resolutionIndex++];
                if (!resolutionBatch.Complete && resolution.UnverifiedReasonCode is null)
                {
                    results.Add(DiagnosticResult.Fail(
                        ReferenceCheckId(documentName, reference.Name, "resolution"),
                        $"VbaProjectReferences ({documentName}/{reference.Name})",
                        "Reference verification did not complete because the shared resolver batch was incomplete."));
                    continue;
                }

                results.Add(referencePlanner.CreateReferenceResolutionDiagnostic(
                    documentName,
                    reference,
                    resolution));
            }
        }
    }

    private static void AddResolutionBatchDiagnostics(
        List<DiagnosticResult> results,
        string documentName,
        VbaProjectReferenceResolutionBatch resolutionBatch)
    {
        if (resolutionBatch.Complete)
        {
            return;
        }

        if (resolutionBatch.Diagnostics.Count == 0)
        {
            results.Add(DiagnosticResult.Fail(
                ReferenceCheckId(documentName, null, "resolutionBatch"),
                $"VbaProjectReferences ({documentName})",
                "referenceResolutionIncomplete: Reference resolution did not complete."));
            return;
        }

        for (var index = 0; index < resolutionBatch.Diagnostics.Count; index++)
        {
            var diagnostic = resolutionBatch.Diagnostics[index];
            results.Add(DiagnosticResult.Fail(
                ReferenceCheckId(
                    documentName,
                    null,
                    $"resolutionBatch.{Uri.EscapeDataString(diagnostic.Code)}.{index}"),
                $"VbaProjectReferences ({documentName})",
                $"{diagnostic.Code}: {diagnostic.Message}"));
        }
    }

    private static string ReferenceCheckId(
        string documentName,
        string? referenceName,
        string finding)
        => referenceName is null
            ? $"project.references.{Uri.EscapeDataString(documentName)}.{finding}"
            : $"project.references.{Uri.EscapeDataString(documentName)}." +
              $"{Uri.EscapeDataString(referenceName)}.{finding}";

}
