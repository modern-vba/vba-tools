using System.Collections.Concurrent;
using VbaTools.Syntax;

namespace VbaLanguageServer.SourceModel;

internal enum VbaEffectiveDeclaredTypeState
{
    Known,
    ExplicitlyUnresolved,
    InsufficientEvidence,
    NoReturnType
}

internal sealed record VbaEffectiveDeclaredType(
    VbaEffectiveDeclaredTypeState State,
    VbaTypeReference? Reference = null,
    object? Identity = null,
    string? DisplayName = null,
    string? ReferenceQualifiedDisplayName = null,
    VbaResolvedNameTarget? Target = null);

/// <summary>
/// Owns declaration and parameter-slot type evidence for one immutable inventory.
/// </summary>
internal sealed class VbaEffectiveDeclaredTypes(VbaNameResolutionService names)
{
    private readonly ConcurrentDictionary<(VbaDefinitionIdentity Identity, int Ordinal),
        Lazy<VbaEffectiveDeclaredType>> results = new();

    public VbaEffectiveDeclaredType Get(VbaSourceDefinition definition, int parameterOrdinal = -1)
        => results.GetOrAdd((definition.Identity, parameterOrdinal), _ =>
            new Lazy<VbaEffectiveDeclaredType>(() => Resolve(definition, parameterOrdinal))).Value;

    public VbaSourceDefinition Project(VbaSourceDefinition definition)
    {
        if (definition.Identity.Origin != VbaDefinitionOrigin.Source)
        {
            return definition;
        }
        var type = Get(definition);
        var signature = definition.Signature;
        if (signature is not null)
        {
            var parameters = signature.Parameters.Select((parameter, ordinal) =>
            {
                var effective = Get(definition, ordinal);
                var label = parameter.Label;
                if (parameter.TypeReference is null && effective.Reference is { } reference)
                {
                    label = AppendType(label, reference.Name);
                }
                return parameter with { TypeReference = effective.Reference, DisplayLabel = label };
            }).ToArray();
            var opening = signature.Label.IndexOf('(');
            var closing = signature.Label.LastIndexOf(')');
            var label = opening < 0 || closing < opening ? signature.Label
                : signature.Label[..(opening + 1)] + string.Join(", ", parameters.Select(parameter => parameter.Label))
                    + signature.Label[closing..];
            if (definition.TypeReference is null && type.Reference is { } result)
            {
                label += $" As {result.Name}";
            }
            signature = signature with { Label = label, Parameters = parameters };
        }
        var declarationLabel = definition.DeclarationLabel;
        if (signature is null && definition.TypeReference is null && type.Reference is { } declaredType
            && definition.Kind is VbaSourceDefinitionKind.Variable or VbaSourceDefinitionKind.Parameter or VbaSourceDefinitionKind.TypeMember)
        {
            declarationLabel = AppendType(declarationLabel
                ?? $"{definition.Name}{(definition.IsArray ? "()" : "")}", declaredType.Name);
        }
        return definition with { TypeReference = type.Reference, Signature = signature, DeclarationLabel = declarationLabel };
    }

    private static string AppendType(string label, string typeName)
        => label.EndsWith(']')
            ? label[..^1] + $" As {typeName}]"
            : label + $" As {typeName}";

    private VbaEffectiveDeclaredType Resolve(VbaSourceDefinition definition, int parameterOrdinal)
    {
        var document = names.FindDocument(definition.Uri);
        definition = document?.Definitions.FirstOrDefault(candidate => candidate.Identity == definition.Identity)
            ?? definition;
        var parameter = parameterOrdinal >= 0 && definition.Signature is { } signature
            && parameterOrdinal < signature.Parameters.Count ? signature.Parameters[parameterOrdinal] : null;
        if (parameterOrdinal >= 0 && parameter is null
            && definition.Identity.Origin == VbaDefinitionOrigin.Source
            && document?.SyntaxTree is { } sourceTree)
        {
            var syntax = FindDeclaration(sourceTree, definition)?.Signature?.Parameters.ElementAtOrDefault(parameterOrdinal);
            if (syntax is not null)
            {
                parameter = new VbaCallableParameter(syntax.Name,
                    TypeReference: syntax.TypeReference is null ? null
                        : new VbaTypeReference(syntax.TypeReference.Name, syntax.TypeReference.Qualifier));
            }
        }
        if (parameterOrdinal >= 0 && parameter is null)
        {
            return new(VbaEffectiveDeclaredTypeState.InsufficientEvidence);
        }
        if (parameterOrdinal < 0 && ((definition.CallableKind ?? definition.Signature?.CallableKind)
                is VbaCallableKind.Sub or VbaCallableKind.Event
            || definition.PropertyAccessorKind is VbaPropertyAccessorKind.Let or VbaPropertyAccessorKind.Set))
        {
            return new(VbaEffectiveDeclaredTypeState.NoReturnType);
        }
        var written = parameter is null ? definition.TypeReference : parameter.TypeReference;
        var reference = written;
        if (reference is null)
        {
            if (definition.Identity.Origin != VbaDefinitionOrigin.Source || document?.SyntaxTree is not { } tree
                || definition.ConditionalCompilationPath is not { } path
                || parameter is null && definition.Kind is not (VbaSourceDefinitionKind.Variable
                    or VbaSourceDefinitionKind.Parameter or VbaSourceDefinitionKind.Procedure or VbaSourceDefinitionKind.Property))
            {
                return new(VbaEffectiveDeclaredTypeState.InsufficientEvidence);
            }
            if (HasIncompleteTypeClause(tree, definition, parameterOrdinal))
            {
                return new(VbaEffectiveDeclaredTypeState.InsufficientEvidence);
            }
            var name = parameter?.Name ?? definition.Name;
            var initial = char.ToUpperInvariant(name[0]);
            string? effectiveName = "Variant";
            foreach (var directive in tree.Module.DefTypeDirectives.Where(candidate =>
                (candidate.Range.Start.Line, candidate.Range.Start.Character).CompareTo(
                    (definition.Range.Start.Line, definition.Range.Start.Character)) < 0
                && candidate.LetterRanges.Any(range => range.Start <= initial && initial <= range.End)))
            {
                if (!VbaConditionalCompilationBranchFacts.TryGetPath(tree, directive.Range,
                    requireCompleteStructure: true, out var directivePath))
                {
                    effectiveName = null;
                }
                else if (directivePath.IsPrefixOf(path))
                {
                    effectiveName = directive.TypeName;
                }
                else if (VbaConditionalCompilationBranchFacts.CanCoexist(directivePath, path))
                {
                    effectiveName = null;
                }
            }
            if (effectiveName is null)
            {
                return new(VbaEffectiveDeclaredTypeState.InsufficientEvidence);
            }
            reference = new VbaTypeReference(effectiveName);
        }
        if (reference.Qualifier is null
            && VbaLanguageVocabulary.TryGetCanonicalTypeName(reference.Name, out var intrinsic))
        {
            return new(VbaEffectiveDeclaredTypeState.Known, new VbaTypeReference(intrinsic), intrinsic, intrinsic);
        }
        VbaResolvedNameTarget? target;
        if (definition.Identity.Origin == VbaDefinitionOrigin.ProjectReference)
        {
            target = names.ResolveProjectReferenceTypeDefinition(
                definition.Identity.ReferenceName ?? definition.ModuleName, reference) is { } foreignType
                    ? new VbaDefinitionNameTarget(foreignType) : null;
        }
        else
        {
            var outcome = document is null ? null : names.ResolveTypeDefinitionOutcome(document, reference);
            target = outcome?.Kind == VbaNameResolutionKind.Resolved ? outcome.Target : null;
        }
        if (target is null)
        {
            return new(written is null ? VbaEffectiveDeclaredTypeState.InsufficientEvidence
                : VbaEffectiveDeclaredTypeState.ExplicitlyUnresolved, reference,
                DisplayName: reference.Qualifier is null ? reference.Name : $"{reference.Qualifier}.{reference.Name}");
        }
        var selected = target.SelectedDefinition;
        var qualifier = reference.Qualifier is null ? null
            : names.GetCanonicalQualifierName(selected, reference.Qualifier) ?? reference.Qualifier;
        var preferredQualifier = names.GetPreferredReferenceQualifierName(selected);
        if (definition.Identity.Origin == VbaDefinitionOrigin.ProjectReference
            && reference.Qualifier is not null && names.IsReferenceQualifierAmbiguous(reference.Qualifier)
            && !string.IsNullOrEmpty(preferredQualifier))
        {
            qualifier = preferredQualifier;
        }
        return new(VbaEffectiveDeclaredTypeState.Known, reference, target.Identity,
            qualifier is null ? target.CanonicalName : $"{qualifier}.{target.CanonicalName}",
            string.IsNullOrEmpty(preferredQualifier) ? null : $"{preferredQualifier}.{target.CanonicalName}", target);
    }

    private static bool HasIncompleteTypeClause(VbaSyntaxTree tree, VbaSourceDefinition definition, int ordinal)
    {
        var declaration = FindDeclaration(tree, definition);
        var range = ordinal < 0 ? declaration?.Range
            : declaration?.Signature?.Parameters.ElementAtOrDefault(ordinal)?.Range;
        if (range is null)
        {
            return true;
        }
        var depth = 0;
        var continued = false;
        foreach (var token in tree.TokenStream.Tokens.Where(token =>
            (token.Range.Start.Line, token.Range.Start.Character).CompareTo(
                (range.End.Line, range.End.Character)) >= 0))
        {
            if (token.Kind == VbaTokenKind.Whitespace)
            {
                continue;
            }
            if (token.Kind == VbaTokenKind.LineContinuation)
            {
                continued = true;
                continue;
            }
            if (token.Kind == VbaTokenKind.NewLine)
            {
                if (!continued)
                {
                    break;
                }
                continued = false;
                continue;
            }
            if (token.Kind == VbaTokenKind.Comment || depth == 0 && token.Text is "," or ":" or "=")
            {
                break;
            }
            if (token.Text == "(")
            {
                depth++;
            }
            else if (token.Text == ")")
            {
                if (depth == 0)
                {
                    break;
                }
                depth--;
            }
            else if (depth == 0 && token.Text.Equals("As", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static VbaDeclarationSyntax? FindDeclaration(VbaSyntaxTree tree, VbaSourceDefinition definition)
        => tree.Module.Declarations.FirstOrDefault(candidate =>
            candidate.Range.Start.Line == definition.Range.Start.Line
            && candidate.Range.Start.Character == definition.Range.Start.Character
            && candidate.Name.Equals(definition.Name, StringComparison.OrdinalIgnoreCase));
}
