namespace VbaDev.Domain;

public sealed record MainVbaProjectReference(string Name)
{
    public static MainVbaProjectReference? FromDocumentKind(string documentKind)
        => string.Equals(documentKind, ProjectDocument.ExcelKind, StringComparison.OrdinalIgnoreCase)
            ? new MainVbaProjectReference("Microsoft Excel 16.0 Object Library")
            : null;
}

public sealed record VbaProjectReferenceSelection(
    IReadOnlyList<VbaProjectReference> References,
    MainVbaProjectReference? MainVbaProjectReference,
    string? MissingExpectedMainReference)
{
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
