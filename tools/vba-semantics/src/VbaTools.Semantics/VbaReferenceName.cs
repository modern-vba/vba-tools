namespace VbaTools.Semantics;

/// <summary>
/// Defines lookup equivalence for human-visible VBA project reference names.
/// </summary>
public static class VbaReferenceName
{
    /// <summary>
    /// Gets the human-visible name of the always-active VBA standard library.
    /// </summary>
    public const string StandardLibrary = "Visual Basic For Applications";

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
