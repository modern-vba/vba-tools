namespace VbaTools.Semantics;

/// <summary>
/// Identifies a reference definition's explicit global root exposure.
/// </summary>
public enum ReferenceDefinitionGlobalExposure
{
    /// <summary>
    /// The definition has no global-value exposure. Public root types remain addressable as types.
    /// </summary>
    None,

    /// <summary>
    /// The definition is a public library global whenever its owning reference is active.
    /// </summary>
    LibraryGlobal,

    /// <summary>
    /// The definition is a host global only when its owning reference is the active main reference.
    /// </summary>
    MainHostGlobal
}
