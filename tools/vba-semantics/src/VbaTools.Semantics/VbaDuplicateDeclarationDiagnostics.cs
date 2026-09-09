using VbaTools.Syntax;

namespace VbaTools.Semantics;

internal static class VbaDuplicateDeclarationDiagnostics
{
    internal static IReadOnlyList<VbaSourceSemanticDiagnostic> Collect(
        IReadOnlyList<VbaSourceDocument> documents,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new List<VbaSourceSemanticDiagnostic>();
        var definitions = documents
            .SelectMany(document => document.Definitions)
            .Where(definition => definition.Identity.Origin == VbaDefinitionOrigin.Source)
            .OrderBy(definition => definition.Uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(definition => definition.Uri, StringComparer.Ordinal)
            .ThenBy(definition => definition.Range.Start.Line)
            .ThenBy(definition => definition.Range.Start.Character)
            .ToArray();
        var declarationsWithPeers = new HashSet<VbaDefinitionIdentity>();
        for (var leftIndex = 0; leftIndex < definitions.Length; leftIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var left = definitions[leftIndex];
            for (var rightIndex = leftIndex + 1;
                rightIndex < definitions.Length;
                rightIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var right = definitions[rightIndex];
                if (!VbaDeclarationRelationshipPolicy.AreDirectCollisionPeers(
                        left,
                        right)
                    || left.ConditionalCompilationPath is not { } leftPath
                    || right.ConditionalCompilationPath is not { } rightPath
                    || !IsProvenCollision(leftPath, rightPath))
                {
                    continue;
                }

                declarationsWithPeers.Add(left.Identity);
                declarationsWithPeers.Add(right.Identity);
            }
        }

        foreach (var declaration in definitions.Where(definition =>
            declarationsWithPeers.Contains(definition.Identity)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!VbaDocumentIdentityPolicy.TryIdentifyDocument(declaration.Uri, out _))
            {
                continue;
            }
            diagnostics.Add(new VbaSourceSemanticDiagnostic(
                    declaration.Uri,
                    "validation.duplicateDeclaration",
                    $"Declaration '{declaration.Name}' conflicts with another "
                        + "declaration in this scope.",
                    declaration.Range));
        }

        return diagnostics.AsReadOnly();
    }

    private static bool IsProvenCollision(
        VbaConditionalCompilationBranchPath left,
        VbaConditionalCompilationBranchPath right)
        => left.IsEmpty
            || right.IsEmpty
            || left.IsPrefixOf(right)
            || right.IsPrefixOf(left);

}
