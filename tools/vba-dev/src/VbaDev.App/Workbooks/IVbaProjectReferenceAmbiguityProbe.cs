namespace VbaDev.App.Workbooks;

/// <summary>
/// Applies the selected Excel/VBE environment to registry-ambiguous reference names.
/// </summary>
public interface IVbaProjectReferenceAmbiguityProbe
{
    /// <summary>
    /// Resolves ambiguous entries against fresh instances of the selected baseline.
    /// </summary>
    /// <param name="baseline">The source-template or blank-workbook baseline.</param>
    /// <param name="registryResolution">The ordered registry resolution batch.</param>
    /// <param name="cancellationToken">The cooperative command cancellation token.</param>
    /// <returns>The ordered batch after VBE-equivalent ambiguity resolution.</returns>
    Task<VbaProjectReferenceResolutionBatch> ResolveAsync(
        VbaProjectReferenceProbeBaseline baseline,
        VbaProjectReferenceResolutionBatch registryResolution,
        CancellationToken cancellationToken);
}
