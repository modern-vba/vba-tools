namespace VbaDev.App.Workbooks;

/// <summary>
/// Applies the selected Excel/VBE environment to registry-ambiguous reference names.
/// </summary>
public interface IVbaProjectReferenceAmbiguityProbe
{
    /// <summary>
    /// Resolves ambiguous entries against fresh copies of one source-template baseline.
    /// </summary>
    /// <param name="baselineWorkbookPath">The selected document source-template path.</param>
    /// <param name="registryResolution">The ordered registry resolution batch.</param>
    /// <param name="cancellationToken">The cooperative command cancellation token.</param>
    /// <returns>The ordered batch after VBE-equivalent ambiguity resolution.</returns>
    Task<VbaProjectReferenceResolutionBatch> ResolveAsync(
        string baselineWorkbookPath,
        VbaProjectReferenceResolutionBatch registryResolution,
        CancellationToken cancellationToken);
}
