using System.Diagnostics.CodeAnalysis;

namespace VbaLanguageServer.SourceModel;

internal static class VbaSemanticReferenceSelectionAdapter
{
    [return: NotNullIfNotNull(nameof(selection))]
    internal static VbaReferenceSelection? ToSemanticSelection(this VbaProjectReferenceSelection? selection)
        => selection is null ? null : VbaReferenceSelection.Capture(
            selection.References.Select(reference => reference.Name), selection.MainVbaProjectReference?.Name);
}
