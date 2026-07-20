using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.Tests;

internal static class VbaSemanticInventoryFixture
{
    public static VbaSemanticInventory Create(
        IReadOnlyDictionary<string, string> sourceTexts,
        VbaProjectReferenceSelection? referenceSelection = null,
        VbaProjectReferenceCatalogSet? referenceCatalogs = null)
        => VbaSemanticInventory.Create(
            ProjectSourceDocuments(sourceTexts),
            referenceSelection,
            referenceCatalogs);

    public static VbaSemanticInventory CreateFromSyntaxTrees(
        IReadOnlyDictionary<string, VbaSyntaxTree> syntaxTrees,
        VbaProjectReferenceSelection? referenceSelection = null,
        VbaProjectReferenceCatalogSet? referenceCatalogs = null)
        => VbaSemanticInventory.Create(
            syntaxTrees.ToDictionary(
                pair => pair.Key,
                pair => VbaSourceDocumentProjector.Project(pair.Key, pair.Value),
                StringComparer.OrdinalIgnoreCase),
            referenceSelection,
            referenceCatalogs);

    public static IReadOnlyDictionary<string, VbaSourceDocument> ProjectSourceDocuments(
        IReadOnlyDictionary<string, string> sourceTexts)
        => sourceTexts.ToDictionary(
            pair => pair.Key,
            pair => VbaSourceDocumentProjector.Project(
                pair.Key,
                VbaSyntaxTree.ParseModule(pair.Key, pair.Value)),
            StringComparer.OrdinalIgnoreCase);
}
