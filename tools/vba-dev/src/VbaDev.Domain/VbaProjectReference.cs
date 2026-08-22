namespace VbaDev.Domain;

/// <summary>
/// Names a VBA project library reference as it appears to workbook authors and manifests.
/// </summary>
/// <param name="Name">The human-visible reference description, such as an Office object library name.</param>
public sealed record VbaProjectReference(string Name);

/// <summary>
/// Defines lookup equivalence for human-visible VBA project reference names.
/// </summary>
public static class VbaProjectReferenceName
{
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
