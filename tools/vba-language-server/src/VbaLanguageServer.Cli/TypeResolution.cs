using System.Text.RegularExpressions;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

internal sealed record VbaResolvedType(string Name, string? ReferenceName);

/// <summary>
/// Resolves VBA type references and member chains across source documents and active reference catalogs.
/// </summary>
internal sealed class VbaTypeResolution
{
    private readonly IReadOnlyList<VbaSourceDocument> documents;
    private readonly VbaProjectReferenceSelection? referenceSelection;
    private readonly VbaProjectReferenceCatalogSet referenceCatalogs;
    private readonly IReadOnlyList<VbaSourceDefinition> activeReferenceDefinitions;
    private readonly VbaNameResolutionService nameResolution;
    private readonly VbaResolutionPolicy resolutionPolicy;

    public VbaTypeResolution(
        IReadOnlyList<VbaSourceDocument> documents,
        VbaProjectReferenceSelection? referenceSelection,
        VbaProjectReferenceCatalogSet referenceCatalogs,
        IReadOnlyList<VbaSourceDefinition> activeReferenceDefinitions,
        VbaNameResolutionService nameResolution,
        VbaResolutionPolicy? resolutionPolicy = null)
    {
        this.documents = documents;
        this.referenceSelection = referenceSelection;
        this.referenceCatalogs = referenceCatalogs;
        this.activeReferenceDefinitions = activeReferenceDefinitions;
        this.nameResolution = nameResolution;
        this.resolutionPolicy = resolutionPolicy ?? new VbaResolutionPolicy();
    }

    public bool TryResolveExpressionType(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        string expression,
        out VbaResolvedType resolvedType,
        IReadOnlyList<VbaResolvedType>? withReceivers = null)
    {
        resolvedType = default!;
        var normalized = VbaMemberExpressionSyntax.NormalizeMemberExpression(expression);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (normalized.StartsWith(".", StringComparison.Ordinal))
        {
            if (withReceivers is { Count: > 0 })
            {
                resolvedType = withReceivers[^1];
            }
            else if (!TryGetWithReceiverType(currentDocument, line, character, out resolvedType))
            {
                return false;
            }

            foreach (var memberName in parts)
            {
                var member = ResolveMember(resolvedType, memberName);
                if (member?.TypeReference is null || !TryResolveTypeReference(currentDocument, member.TypeReference, out resolvedType))
                {
                    return false;
                }
            }

            return true;
        }

        if (parts.Length == 0)
        {
            return false;
        }

        if (parts.Length >= 2 && TryResolveTypeReference(currentDocument, new VbaTypeReference(parts[1], parts[0]), out resolvedType))
        {
            for (var index = 2; index < parts.Length; index++)
            {
                var member = ResolveMember(resolvedType, parts[index]);
                if (member?.TypeReference is null || !TryResolveTypeReference(currentDocument, member.TypeReference, out resolvedType))
                {
                    return false;
                }
            }

            return true;
        }

        var firstDefinition = nameResolution.Resolve(
            currentDocument.Uri,
            new VbaPosition(line, character),
            qualifier: null,
            parts[0]);
        if (firstDefinition?.TypeReference is not null)
        {
            if (!TryResolveTypeReference(currentDocument, firstDefinition.TypeReference, out resolvedType))
            {
                return false;
            }
        }
        else if (firstDefinition is not null && resolutionPolicy.IsTypeDefinition(firstDefinition))
        {
            resolvedType = ToResolvedType(firstDefinition);
        }
        else
        {
            return false;
        }

        for (var index = 1; index < parts.Length; index++)
        {
            var member = ResolveMember(resolvedType, parts[index]);
            if (member?.TypeReference is null || !TryResolveTypeReference(currentDocument, member.TypeReference, out resolvedType))
            {
                return false;
            }
        }

        return true;
    }

    public bool TryResolveTypeReference(
        VbaSourceDocument currentDocument,
        VbaTypeReference typeReference,
        out VbaResolvedType resolvedType)
    {
        resolvedType = default!;
        if (!TryResolveTypeReferenceDefinition(currentDocument, typeReference, out var definition)
            || definition is null)
        {
            return false;
        }

        resolvedType = ToResolvedType(definition);
        return true;
    }

    public bool TryResolveTypeReferenceDefinition(
        VbaSourceDocument currentDocument,
        string[] lines,
        int line,
        VbaIdentifierOccurrence identifier,
        out VbaSourceDefinition? definition)
    {
        definition = null;
        if (VbaLanguageVocabulary.IsKeyword(identifier.Name) ||
            IsFollowedByDot(lines[line], identifier.End))
        {
            return false;
        }

        var logicalPrefix = VbaSourceText.GetLogicalPrefix(lines, line, identifier.End);
        if (!TryGetTypeReferencePrefix(logicalPrefix, out var typeReference))
        {
            return false;
        }

        TryResolveTypeReferenceDefinition(currentDocument, typeReference, out definition);
        return true;
    }

    public bool TryResolveTypeReferenceDefinition(
        VbaSourceDocument currentDocument,
        VbaTypeReference typeReference,
        out VbaSourceDefinition? definition)
    {
        definition = null;
        if (!string.IsNullOrWhiteSpace(typeReference.Qualifier))
        {
            definition = ResolveReferenceTypeDefinition(typeReference.Qualifier, typeReference.Name);
            if (definition is not null)
            {
                return true;
            }

            definition = ResolveSourceTypeDefinition(typeReference.Name, typeReference.Qualifier);
            return definition is not null;
        }

        definition = ResolveSourceTypeDefinition(typeReference.Name, qualifier: null);
        if (definition is not null)
        {
            return true;
        }

        var referenceCandidates = activeReferenceDefinitions
            .Where(definition => SameName(definition.Name, typeReference.Name))
            .Where(resolutionPolicy.IsTypeDefinition)
            .Where(definition => definition.ParentTypeName is null)
            .ToArray();
        var referenceTypeDefinition = ResolveReferenceCandidates(referenceCandidates);
        if (referenceTypeDefinition is null)
        {
            return false;
        }

        definition = referenceTypeDefinition;
        return true;
    }

    public IReadOnlyList<VbaSourceDefinition> GetMembersOfType(VbaResolvedType resolvedType)
        => GetMemberCandidates(resolvedType)
            .GroupBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public VbaSourceDefinition? ResolveMember(VbaResolvedType resolvedType, string memberName)
    {
        var candidates = GetMemberCandidates(resolvedType)
            .Where(definition => SameName(definition.Name, memberName))
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    public VbaSourceDefinition? ResolveEvent(VbaResolvedType resolvedType, string eventName)
    {
        var candidates = GetMemberCandidates(resolvedType)
            .Where(definition => definition.Kind == VbaSourceDefinitionKind.Event)
            .Where(definition => SameName(definition.Name, eventName))
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    public IEnumerable<VbaSourceDefinition> GetVisibleTypeDefinitions(VbaSourceDocument currentDocument)
    {
        foreach (var definition in currentDocument.Definitions.Where(resolutionPolicy.IsTypeDefinition))
        {
            yield return definition;
        }

        foreach (var definition in documents
            .Where(document => !SameUri(document.Uri, currentDocument.Uri))
            .SelectMany(document => document.Definitions)
            .Where(resolutionPolicy.IsTypeDefinition)
            .Where(definition => definition.Visibility == VbaSourceDefinitionVisibility.Public))
        {
            yield return definition;
        }

        foreach (var definition in activeReferenceDefinitions
            .Where(resolutionPolicy.IsTypeDefinition)
            .Where(definition => definition.ParentTypeName is null))
        {
            yield return definition;
        }
    }

    public VbaSourceDefinition? ResolveSourceTypeCompletionGroup(IReadOnlyList<VbaSourceDefinition> candidates)
    {
        var sourceCandidates = candidates
            .Where(definition => !VbaProjectReferenceCatalogSet.IsExternalDefinition(definition))
            .ToArray();
        if (sourceCandidates.Length == 1)
        {
            return sourceCandidates[0];
        }

        if (sourceCandidates.Length > 1)
        {
            return null;
        }

        return ResolveReferenceCandidates(candidates);
    }

    private bool TryGetWithReceiverType(
        VbaSourceDocument currentDocument,
        int targetLine,
        int character,
        out VbaResolvedType resolvedType)
    {
        resolvedType = default!;
        var lines = VbaSourceText.SplitLines(currentDocument.Text);
        var stack = new List<VbaResolvedType>();
        for (var lineIndex = 0; lineIndex < targetLine && lineIndex < lines.Length; lineIndex++)
        {
            var statement = VbaSourceText.GetLogicalStatement(lines, lineIndex, out var endLine);
            if (endLine >= targetLine)
            {
                break;
            }

            var trimmed = statement.Trim();
            if (Regex.IsMatch(trimmed, "^End\\s+With\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                lineIndex = endLine;
                continue;
            }

            var withMatch = Regex.Match(
                trimmed,
                "^With\\s+(?<expression>.+)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (withMatch.Success
                && TryResolveExpressionType(
                    currentDocument,
                    endLine,
                    lines[Math.Min(endLine, lines.Length - 1)].Length,
                    withMatch.Groups["expression"].Value,
                    out var withType,
                    stack))
            {
                stack.Add(withType);
            }

            lineIndex = endLine;
        }

        if (stack.Count == 0)
        {
            return false;
        }

        resolvedType = stack[^1];
        return true;
    }

    private IEnumerable<VbaSourceDefinition> GetMemberCandidates(VbaResolvedType resolvedType)
    {
        if (resolvedType.ReferenceName is not null)
        {
            return activeReferenceDefinitions
                .Where(definition => SameName(definition.ModuleName, resolvedType.ReferenceName))
                .Where(definition => definition.ParentTypeName is not null)
                .Where(definition => SameName(definition.ParentTypeName!, resolvedType.Name));
        }

        return documents
            .Where(document => SameName(document.ModuleName, resolvedType.Name))
            .SelectMany(document => document.Definitions)
            .Where(resolutionPolicy.IsReferenceTarget);
    }

    private VbaSourceDefinition? ResolveReferenceTypeDefinition(string qualifier, string typeName)
    {
        if (referenceSelection is null)
        {
            return null;
        }

        var candidates = referenceCatalogs
            .GetQualifiedDefinitions(referenceSelection, qualifier, typeName)
            .Where(resolutionPolicy.IsTypeDefinition)
            .Where(definition => definition.ParentTypeName is null)
            .ToArray();
        return ResolveReferenceCandidates(candidates);
    }

    private VbaSourceDefinition? ResolveSourceTypeDefinition(string typeName, string? qualifier)
    {
        var candidates = documents
            .SelectMany(document => document.Definitions)
            .Where(resolutionPolicy.IsTypeDefinition)
            .Where(definition => SameName(definition.Name, typeName))
            .Where(definition => qualifier is null || SameName(definition.ModuleName, qualifier))
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private VbaSourceDefinition? ResolveReferenceCandidates(IEnumerable<VbaSourceDefinition> candidates)
        => resolutionPolicy.ResolveReferenceCandidates(candidates, referenceSelection);

    private static bool TryGetTypeReferencePrefix(string logicalPrefix, out VbaTypeReference typeReference)
    {
        typeReference = default!;
        var match = Regex.Match(
            logicalPrefix,
            "\\bAs\\s+(?:New\\s+)?(?:(?<qualifier>[A-Za-z_][A-Za-z0-9_]*)\\s*\\.\\s*)?(?<type>[A-Za-z_][A-Za-z0-9_]*)\\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            match = Regex.Match(
                logicalPrefix,
                "\\bNew\\s+(?:(?<qualifier>[A-Za-z_][A-Za-z0-9_]*)\\s*\\.\\s*)?(?<type>[A-Za-z_][A-Za-z0-9_]*)\\s*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (!match.Success)
        {
            return false;
        }

        var qualifier = match.Groups["qualifier"].Success
            ? match.Groups["qualifier"].Value
            : null;
        typeReference = new VbaTypeReference(match.Groups["type"].Value, qualifier);
        return true;
    }

    private static bool IsFollowedByDot(string line, int position)
    {
        while (position < line.Length && char.IsWhiteSpace(line[position]))
        {
            position++;
        }

        return position < line.Length && line[position] == '.';
    }

    private static VbaResolvedType ToResolvedType(VbaSourceDefinition definition)
        => new(
            definition.Name,
            VbaProjectReferenceCatalogSet.IsExternalDefinition(definition)
                ? definition.ModuleName
                : null);

    private static bool SameUri(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool SameName(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
