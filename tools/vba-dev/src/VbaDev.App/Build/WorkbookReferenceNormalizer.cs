using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Build;

public sealed class WorkbookReferenceNormalizer
{
    private readonly IVbaProjectReferenceResolver referenceResolver;

    public WorkbookReferenceNormalizer(IVbaProjectReferenceResolver referenceResolver)
    {
        this.referenceResolver = referenceResolver;
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
                warnings.Add($"[WARN] VbaProjectReferences ({documentName}/{reference.Name}): Unlisted protected reference remains.");
            }
        }

        var currentNames = currentReferences
            .Select(reference => reference.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in desiredReferences)
        {
            if (!currentNames.Contains(reference.Name))
            {
                session.AddReference(ResolveDesiredReference(documentName, reference.Name));
            }
        }

        return warnings;
    }

    private ResolvedVbaProjectReference ResolveDesiredReference(string documentName, string referenceName)
    {
        var matches = referenceResolver.Resolve(referenceName);
        if (matches.Count == 0)
        {
            throw new BuildCommandException($"VbaProjectReference '{referenceName}' for document '{documentName}' was not found.");
        }

        if (matches.Count > 1)
        {
            var candidates = string.Join(
                ", ",
                matches.Select(match => $"{match.Guid} {match.Major}.{match.Minor}"));
            throw new BuildCommandException($"VbaProjectReference '{referenceName}' for document '{documentName}' is ambiguous: {candidates}.");
        }

        return matches[0];
    }
}
