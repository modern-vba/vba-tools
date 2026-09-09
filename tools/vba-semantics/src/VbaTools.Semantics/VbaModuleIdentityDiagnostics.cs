using System.Collections.Immutable;
using VbaTools.Syntax;

namespace VbaTools.Semantics;

public sealed record VbaModuleNamespaceConflict(string CollisionKind, string Name, string? ReferenceName = null);

/// <summary>Checks explicit module identity against accepted project namespaces.</summary>
public static class VbaModuleIdentityDiagnostics
{
    public static IReadOnlyList<VbaSourceSemanticDiagnostic> Collect(
        VbaSourceDocument document, VbaProjectNamespaceIdentity? namespaces,
        CancellationToken cancellationToken = default)
    {
        if (namespaces is null)
        {
            return [];
        }

        var diagnostics = ImmutableArray.CreateBuilder<VbaSourceSemanticDiagnostic>();
        foreach (var target in document.Definitions.Where(target => IsExplicitModuleIdentity(document, target)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var conflicts = FindConflicts(target.Name, namespaces, cancellationToken);
            if (conflicts.Count != 0)
            {
                diagnostics.Add(new(document.Uri, "validation.moduleIdentityNameConflict",
                    $"Module name '{target.Name}' conflicts with {string.Join(", ", conflicts.Select(Describe))}.",
                    target.Range, NamespaceConflicts: conflicts));
            }
        }

        return diagnostics.ToImmutable();
    }

    public static IReadOnlyList<VbaModuleNamespaceConflict> FindConflicts(
        string moduleName, VbaProjectNamespaceIdentity namespaces,
        CancellationToken cancellationToken = default)
    {
        var conflicts = ImmutableArray.CreateBuilder<VbaModuleNamespaceConflict>();
        if (namespaces.ContainingProjectName is { } projectName
            && projectName.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
        {
            conflicts.Add(new("containingProject", projectName));
        }

        foreach (var reference in namespaces.References)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reference.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add(new("referencedProject", reference.Name, reference.ReferenceName));
            }
        }

        return conflicts.ToImmutable();
    }

    public static string Describe(VbaModuleNamespaceConflict conflict) => conflict.CollisionKind switch
    {
        "containingProject" => $"containing VBA project '{conflict.Name}'",
        "referencedProject" => $"referenced project or object library '{conflict.Name}'",
        _ => throw new ArgumentException("Unknown module namespace conflict kind.", nameof(conflict))
    };

    public static bool IsExplicitModuleIdentity(VbaSourceDocument document, VbaSourceDefinition target)
    {
        if (target.Kind is not (VbaSourceDefinitionKind.Module or VbaSourceDefinitionKind.Class or VbaSourceDefinitionKind.Form))
        {
            return false;
        }

        var syntaxTree = document.SyntaxTree ?? VbaSyntaxTree.ParseModule(document.Uri, document.Text);
        var metadata = syntaxTree.Module.Identity.Metadata
            ?? VbaModuleIdentityMetadataReader.Read(document.Text,
                syntaxTree.Module.Kind == VbaModuleKind.StandardModule
                    ? VbaModuleIdentitySourceKind.StandardModule : VbaModuleIdentitySourceKind.ObjectModule);
        return metadata.IsAuthoritative
            && metadata.Name!.Equals(target.Name, StringComparison.Ordinal)
            && target.Range.Start.Line == syntaxTree.Module.Identity.Range.Start.Line
            && target.Range.Start.Character == syntaxTree.Module.Identity.Range.Start.Character
            && target.Range.End.Line == syntaxTree.Module.Identity.Range.End.Line
            && target.Range.End.Character == syntaxTree.Module.Identity.Range.End.Character;
    }
}
