namespace VbaTools.Semantics;

/// <summary>
/// Identifies the operations supported by a VBA property definition.
/// </summary>
[Flags]
public enum VbaPropertyAccess
{
    /// <summary>
    /// The property access metadata is unavailable.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The property can be read as a value.
    /// </summary>
    Readable = 1 << 0,

    /// <summary>
    /// The property can be assigned a value or object reference.
    /// </summary>
    Writable = 1 << 1
}
