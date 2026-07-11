using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Build;

public sealed class WorkbookReferenceNormalizer
{
    private readonly VbaProjectReferencePlanner referencePlanner;

    public WorkbookReferenceNormalizer(VbaProjectReferencePlanner referencePlanner)
    {
        this.referencePlanner = referencePlanner;
    }

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
}
