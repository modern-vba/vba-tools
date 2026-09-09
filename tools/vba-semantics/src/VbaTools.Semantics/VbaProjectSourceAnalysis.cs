using System.Collections.Immutable;
using VbaTools.Syntax;

namespace VbaTools.Semantics;

/// <summary>A source finding without a product-specific publication owner.</summary>
public sealed record VbaSourceSemanticDiagnostic(
    string SourceUri,
    string Code,
    string Message,
    VbaRange Range,
    string Severity = "error",
    IReadOnlyList<VbaDiagnosticDetail>? Details = null,
    IReadOnlyList<VbaModuleNamespaceConflict>? NamespaceConflicts = null);

/// <summary>Analyzes supplied syntax evidence without discovering or rereading inputs.</summary>
public static class VbaProjectSourceAnalysis
{
    public static bool MayRequireIntrinsicHostEventCatalog(IReadOnlyList<VbaSyntaxTree> syntaxTrees)
        => syntaxTrees.Any(tree => tree.Module.Kind == VbaModuleKind.FormModule);

    public static IReadOnlyList<VbaSourceSemanticDiagnostic> Analyze(
        IReadOnlyList<VbaSyntaxTree> syntaxTrees,
        CancellationToken cancellationToken = default,
        bool sourceInventoryComplete = true)
        => Analyze(syntaxTrees, VbaProjectSemanticInputs.Empty, cancellationToken, sourceInventoryComplete);

    public static IReadOnlyList<VbaSourceSemanticDiagnostic> Analyze(
        IReadOnlyList<VbaSyntaxTree> syntaxTrees,
        VbaProjectSemanticInputs inputs,
        CancellationToken cancellationToken = default,
        bool sourceInventoryComplete = true)
    {
        ArgumentNullException.ThrowIfNull(syntaxTrees);
        ArgumentNullException.ThrowIfNull(inputs);
        cancellationToken.ThrowIfCancellationRequested();
        var documents = syntaxTrees.Select(syntax =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return VbaSourceDocumentProjector.Project(syntax.Uri, syntax);
        }).ToImmutableArray();
        if (!sourceInventoryComplete)
        {
            // Missing sources can shadow or add alternatives to every project binding.
            // Direct declaration collisions remain proved by the captured declarations.
            return VbaDuplicateDeclarationDiagnostics.Collect(documents, cancellationToken);
        }
        var catalogs = inputs.ReferenceCatalogs;
        var candidates = new VbaNameCandidateInventory(documents, inputs.ReferenceSelection, catalogs,
            catalogs.GetActiveDefinitions(inputs.ReferenceSelection), inputs.ReferenceCatalogSources);
        var semantics = new VbaProjectSemanticResolution(candidates,
            referenceCatalogIdentities: inputs.ReferenceCatalogIdentities,
            intrinsicHostEventCatalog: inputs.IntrinsicHostEvents);
        var diagnostics = new VbaSemanticDiagnosticIndex(documents, semantics, cancellationToken);
        return documents.SelectMany(document => diagnostics.GetDiagnostics(document.Uri).Select(diagnostic =>
            new VbaSourceSemanticDiagnostic(document.Uri, diagnostic.Code, diagnostic.Message,
                diagnostic.Range, diagnostic.Severity, diagnostic.Details))
            .Concat(VbaModuleIdentityDiagnostics.Collect(document, inputs.ProjectNamespaces, cancellationToken)))
            .ToImmutableArray();
    }
}
