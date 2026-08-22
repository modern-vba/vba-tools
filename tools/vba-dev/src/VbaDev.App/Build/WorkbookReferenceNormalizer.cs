using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Build;

/// <summary>
/// Reconciles a workbook's current VBA project references with the manifest reference list.
/// </summary>
public sealed class WorkbookReferenceNormalizer
{
    private readonly VbaProjectReferencePlanner referencePlanner;

    /// <summary>
    /// Creates the reference normalizer.
    /// </summary>
    /// <param name="referencePlanner">The planner that resolves manifest reference names to concrete reference identities.</param>
    public WorkbookReferenceNormalizer(VbaProjectReferencePlanner referencePlanner)
    {
        this.referencePlanner = referencePlanner;
    }

    /// <summary>
    /// Removes non-manifest references when possible and adds missing manifest references.
    /// </summary>
    /// <param name="session">The open workbook build session to modify.</param>
    /// <param name="documentName">The document name used in protected-reference warnings.</param>
    /// <param name="desiredReferences">The manifest reference list for the document.</param>
    /// <returns>Warnings for protected references that could not be removed.</returns>
    public IReadOnlyList<string> Normalize(
        IWorkbookBuildSession session,
        string documentName,
        IReadOnlyList<VbaProjectReference> desiredReferences)
    {
        var warnings = new List<string>();
        var desiredNames = desiredReferences
            .Select(reference => reference.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentReferences = session.GetReferences().ToArray();
        foreach (var reference in currentReferences)
        {
            if (desiredNames.Contains(reference.Name))
            {
                continue;
            }

            if (!reference.IsRemovable || !session.RemoveReference(reference.Name))
            {
                warnings.Add(VbaProjectReferencePlanner.FormatProtectedReferenceWarning(documentName, reference.Name));
            }
        }

        var currentNames = currentReferences
            .Select(reference => reference.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in desiredReferences)
        {
            if (!currentNames.Contains(reference.Name))
            {
                session.AddReference(referencePlanner.ResolveDocumentReference(documentName, reference.Name));
            }
        }

        return warnings;
    }

    /// <summary>
    /// Removes and adds references through the bounded owned-generation session.
    /// </summary>
    public async Task<IReadOnlyList<string>> NormalizeAsync(
        IWorkbookGenerationSession session,
        string documentName,
        IReadOnlyList<VbaProjectReference> desiredReferences,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var desiredNames = desiredReferences
            .Select(reference => reference.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentReferences = (await session
                .GetReferencesAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToArray();
        foreach (var reference in currentReferences)
        {
            if (desiredNames.Contains(reference.Name))
            {
                continue;
            }

            if (!reference.IsRemovable || !await session
                    .RemoveReferenceAsync(reference.Name, cancellationToken)
                    .ConfigureAwait(false))
            {
                warnings.Add(VbaProjectReferencePlanner.FormatProtectedReferenceWarning(documentName, reference.Name));
            }
        }

        var currentNames = currentReferences
            .Select(reference => reference.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in desiredReferences)
        {
            if (!currentNames.Contains(reference.Name))
            {
                await session.AddReferenceAsync(
                        referencePlanner.ResolveDocumentReference(documentName, reference.Name),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return warnings;
    }
}
