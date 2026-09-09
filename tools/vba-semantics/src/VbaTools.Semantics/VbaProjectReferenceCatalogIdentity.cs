namespace VbaTools.Semantics;

/// <summary>
/// Identifies a discovered TypeLib catalog identity for a VBA project reference.
/// </summary>
/// <param name="ReferenceName">The human-visible reference name.</param>
/// <param name="Guid">The TypeLib GUID.</param>
/// <param name="MajorVersion">The TypeLib major version.</param>
/// <param name="MinorVersion">The TypeLib minor version.</param>
/// <param name="Lcid">The TypeLib locale identifier.</param>
/// <param name="Path">The registry-resolved TypeLib path.</param>
public sealed record VbaProjectReferenceCatalogIdentity(
    string ReferenceName,
    string Guid,
    int MajorVersion,
    int MinorVersion,
    int Lcid,
    string Path);
