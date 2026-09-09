using VbaTools.Semantics;

namespace VbaDev.App.References;

/// <summary>
/// Reads TypeLib metadata from a resolved catalog identity.
/// </summary>
public interface ITypeLibCatalogMetadataReader
{
    /// <summary>
    /// Reads TypeLib metadata for a resolved catalog identity.
    /// </summary>
    /// <param name="identity">The resolved catalog identity.</param>
    /// <returns>The TypeLib metadata.</returns>
    TypeLibCatalogMetadata ReadMetadata(VbaProjectReferenceCatalogIdentity identity);

    /// <summary>Reads the identity and metadata actually stored at an observed library path.</summary>
    AcquiredTypeLibCatalogMetadata ReadMetadataFromPath(string referenceName, string path)
        => throw new NotSupportedException($"TypeLib metadata cannot be read from observed path '{path}'.");
}

public sealed record AcquiredTypeLibCatalogMetadata(
    VbaProjectReferenceCatalogIdentity Identity, TypeLibCatalogMetadata Metadata);
