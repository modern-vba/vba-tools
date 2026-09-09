namespace VbaTools.Semantics;

/// <summary>
/// Identifies where the active catalog for a reference came from.
/// </summary>
public enum VbaProjectReferenceCatalogSource
{
    /// <summary>
    /// No editor metadata catalog is available for the reference.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The catalog came from the bundled minimal metadata shipped with the language server.
    /// </summary>
    Bundled,

    /// <summary>
    /// The catalog came from a current persisted generated cache entry.
    /// </summary>
    Persisted,

    /// <summary>
    /// The catalog came from a stale persisted generated cache entry.
    /// </summary>
    StalePersisted,

    /// <summary>
    /// The catalog was generated from TypeLib metadata in the current session.
    /// </summary>
    Generated
}
