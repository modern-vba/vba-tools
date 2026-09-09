using System.Text.Json.Serialization;

namespace VbaDev.Domain;

/// <summary>
/// Names a VBA project library reference as it appears to workbook authors and manifests.
/// </summary>
/// <param name="Name">The human-visible reference description, such as an Office object library name.</param>
/// <param name="Requested">Whether the reference was selected independently of CommonModules.</param>
public sealed record VbaProjectReference
{
    /// <summary>
    /// Creates a manifest reference with explicit direct-intent state.
    /// </summary>
    /// <param name="name">The human-visible reference description.</param>
    /// <param name="requested">Whether the reference was selected independently of CommonModules.</param>
    [JsonConstructor]
    public VbaProjectReference(string name, bool requested)
    {
        Name = name;
        Requested = requested;
    }

    /// <summary>
    /// Creates a directly requested reference from application code.
    /// </summary>
    /// <param name="name">The human-visible reference description.</param>
    public VbaProjectReference(string name)
        : this(name, requested: true)
    {
    }

    /// <summary>
    /// Gets the human-visible reference description.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// Gets whether the reference was selected independently of CommonModules.
    /// </summary>
    public bool Requested { get; init; }
}

/// <summary>Exposes the shared lookup policy to manifest and workbook consumers.</summary>
public static class VbaProjectReferenceName
{
    public const string StandardLibrary = VbaTools.Semantics.VbaReferenceName.StandardLibrary;
    public const string StandardLibrarySelectionError =
        "Visual Basic For Applications is always active and cannot be added to or removed from project reference selection.";

    public static IEqualityComparer<string> Comparer => VbaTools.Semantics.VbaReferenceName.Comparer;
    public static IComparer<string> OrderingComparer => VbaTools.Semantics.VbaReferenceName.OrderingComparer;
    public static bool AreEquivalent(string left, string right)
        => VbaTools.Semantics.VbaReferenceName.AreEquivalent(left, right);
    public static bool IsStandardLibrary(string name) => VbaTools.Semantics.VbaReferenceName.IsStandardLibrary(name);
}
