namespace VbaDev.Domain;

/// <summary>
/// Identifies the host object library that should win ties among active external definitions.
/// </summary>
/// <param name="Name">The manifest reference name for the main host object library.</param>
public sealed record MainVbaProjectReference(string Name)
{
    /// <summary>
    /// Resolves the expected main host reference for a manifest document kind.
    /// </summary>
    /// <param name="documentKind">The document kind value from vba-project.json.</param>
    /// <returns>The expected main reference, or null when the document kind has no known main reference.</returns>
    public static MainVbaProjectReference? FromDocumentKind(string documentKind)
        => string.Equals(documentKind, ProjectDocument.ExcelKind, StringComparison.OrdinalIgnoreCase)
            ? new MainVbaProjectReference("Microsoft Excel 16.0 Object Library")
            : null;
}

/// <summary>
/// Captures the VBA project references that are active for one document source set.
/// </summary>
/// <param name="References">The manifest references available to workbook automation and editor intelligence.</param>
/// <param name="MainVbaProjectReference">The active main host reference when the manifest contains it.</param>
/// <param name="MissingExpectedMainReference">The expected main host reference name missing from the manifest.</param>
public sealed record VbaProjectReferenceSelection(
    IReadOnlyList<VbaProjectReference> References,
    MainVbaProjectReference? MainVbaProjectReference,
    string? MissingExpectedMainReference)
{
    /// <summary>
    /// Creates a reference selection and validates whether the expected main reference is present.
    /// </summary>
    /// <param name="documentKind">The manifest document kind that determines the expected main reference.</param>
    /// <param name="references">The references declared by the document manifest entry.</param>
    /// <returns>The resolved reference selection for language-server and diagnostic consumers.</returns>
    public static VbaProjectReferenceSelection Create(
        string documentKind,
        IReadOnlyList<VbaProjectReference> references)
    {
        var expectedMainReference = MainVbaProjectReference.FromDocumentKind(documentKind);
        if (expectedMainReference is null)
        {
            return new VbaProjectReferenceSelection(references, null, null);
        }

        var manifestReference = references.FirstOrDefault(reference =>
            reference.Name.Equals(expectedMainReference.Name, StringComparison.OrdinalIgnoreCase));
        return manifestReference is null
            ? new VbaProjectReferenceSelection(references, null, expectedMainReference.Name)
            : new VbaProjectReferenceSelection(
                references,
                new MainVbaProjectReference(manifestReference.Name),
                null);
    }
}
