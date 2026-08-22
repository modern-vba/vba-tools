namespace VbaDev.App.Workbooks;

/// <summary>
/// Resolves human-visible VBA project reference names to concrete reference identities.
/// </summary>
public interface IVbaProjectReferenceResolver
{
    /// <summary>
    /// Resolves every distinct registered reference description from one catalog snapshot.
    /// </summary>
    /// <returns>The complete available-reference batch, including catalog warnings or failure.</returns>
    VbaProjectReferenceResolutionBatch ResolveAvailable();

    /// <summary>
    /// Resolves reference names from one catalog snapshot.
    /// </summary>
    /// <param name="referenceNames">The ordered human-visible reference descriptions.</param>
    /// <returns>The complete batch result, including catalog warnings or failure.</returns>
    VbaProjectReferenceResolutionBatch Resolve(IReadOnlyList<string> referenceNames);
}
