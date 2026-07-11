namespace VbaDev.App.Workbooks;

/// <summary>
/// Resolves human-visible VBA project reference names to concrete reference identities.
/// </summary>
public interface IVbaProjectReferenceResolver
{
    /// <summary>
    /// Finds reference identities that match a manifest reference name.
    /// </summary>
    /// <param name="referenceName">The human-visible reference description.</param>
    /// <returns>The matching reference identities, possibly empty or ambiguous.</returns>
    IReadOnlyList<ResolvedVbaProjectReference> Resolve(string referenceName);
}
