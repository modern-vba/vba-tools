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

/// <summary>
/// Defines lookup equivalence for human-visible VBA project reference names.
/// </summary>
public static class VbaProjectReferenceName
{
    /// <summary>
    /// Gets the human-visible name of the always-active VBA standard library.
    /// </summary>
    public const string StandardLibrary = "Visual Basic For Applications";

    /// <summary>
    /// Gets the diagnostic used when manifest selection attempts to include the standard library.
    /// </summary>
    public const string StandardLibrarySelectionError =
        "Visual Basic For Applications is always active and cannot be added to or removed from project reference selection.";

    private static readonly ReferenceNameComparer ComparerInstance = new();

    /// <summary>
    /// Gets the trimmed, case-insensitive equality comparer used for reference lookup keys.
    /// </summary>
    public static IEqualityComparer<string> Comparer => ComparerInstance;

    /// <summary>
    /// Gets the deterministic trimmed, case-insensitive ordering comparer for reference lookup keys.
    /// </summary>
    public static IComparer<string> OrderingComparer => ComparerInstance;

    /// <summary>
    /// Determines whether two stored spellings identify the same reference name.
    /// </summary>
    public static bool AreEquivalent(string left, string right)
        => ComparerInstance.Equals(left, right);

    /// <summary>
    /// Determines whether a requested name is the always-active VBA standard library.
    /// </summary>
    /// <param name="name">The normalized or raw human-visible reference name.</param>
    /// <returns>True when the name identifies the standard library.</returns>
    public static bool IsStandardLibrary(string name)
        => string.Equals(
            name.Trim(),
            StandardLibrary,
            StringComparison.OrdinalIgnoreCase);

    private sealed class ReferenceNameComparer : IEqualityComparer<string>, IComparer<string>
    {
        public bool Equals(string? left, string? right)
            => string.Equals(
                left?.Trim(),
                right?.Trim(),
                StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(string value)
            => StringComparer.OrdinalIgnoreCase.GetHashCode(value.Trim());

        public int Compare(string? left, string? right)
            => StringComparer.OrdinalIgnoreCase.Compare(left?.Trim(), right?.Trim());
    }
}
