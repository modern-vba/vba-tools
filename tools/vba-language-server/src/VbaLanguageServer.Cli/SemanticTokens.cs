using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Builds semantic tokens and LSP token data from source definitions and resolved references.
/// </summary>
internal static class VbaSemanticTokenBuilder
{
    private static readonly IReadOnlyDictionary<string, int> SemanticTokenTypeIndexes =
        VbaSourceIndex.SemanticTokenTypes
            .Select((tokenType, index) => new { tokenType, index })
            .ToDictionary(item => item.tokenType, item => item.index, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, int> SemanticTokenModifierIndexes =
        VbaSourceIndex.SemanticTokenModifiers
            .Select((modifier, index) => new { modifier, index })
            .ToDictionary(item => item.modifier, item => item.index, StringComparer.Ordinal);

    /// <summary>
    /// Builds semantic tokens for declarations and resolved identifier references in one document.
    /// </summary>
    /// <param name="documents">The indexed source documents.</param>
    /// <param name="uri">The target document URI.</param>
    /// <param name="resolveSourceDefinition">A resolver for identifier references by line and character.</param>
    /// <returns>The semantic tokens sorted in source order.</returns>
    public static IReadOnlyList<VbaSemanticToken> GetSemanticTokens(
        IReadOnlyList<VbaSourceDocument> documents,
        string uri,
        Func<int, int, VbaSourceDefinition?> resolveSourceDefinition)
    {
        var currentDocument = documents.FirstOrDefault(document => SameUri(document.Uri, uri));
        if (currentDocument is null)
        {
            return [];
        }

        var lines = VbaSourceText.SplitLines(currentDocument.Text);
        var tokens = new List<VbaSemanticToken>();
        var declarationRanges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in currentDocument.Definitions)
        {
            if (!TryCreateSemanticToken(lines, definition, isDeclaration: true, out var token))
            {
                continue;
            }

            tokens.Add(token);
            declarationRanges.Add(GetRangeKey(token.Range));
        }

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            foreach (var occurrence in VbaSourceText.FindIdentifierOccurrences(lines[lineIndex]))
            {
                var occurrenceRange = new VbaRange(
                    new VbaPosition(lineIndex, occurrence.Start),
                    new VbaPosition(lineIndex, occurrence.End));
                if (declarationRanges.Contains(GetRangeKey(occurrenceRange)))
                {
                    continue;
                }

                var definition = resolveSourceDefinition(lineIndex, occurrence.Start);
                if (definition is null
                    || !TryCreateSemanticToken(
                        lines,
                        definition,
                        isDeclaration: false,
                        out var referenceToken,
                        occurrenceRange,
                        occurrence.Name))
                {
                    continue;
                }

                tokens.Add(referenceToken);
            }
        }

        return tokens
            .GroupBy(token => $"{GetRangeKey(token.Range)}:{token.TokenType}:{string.Join(",", token.TokenModifiers)}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(token => token.Range.Start.Line)
            .ThenBy(token => token.Range.Start.Character)
            .ToArray();
    }

    /// <summary>
    /// Encodes semantic tokens using the LSP relative integer format.
    /// </summary>
    /// <param name="tokens">The semantic tokens sorted in source order.</param>
    /// <returns>The encoded semantic token data.</returns>
    public static IReadOnlyList<int> GetSemanticTokenData(IReadOnlyList<VbaSemanticToken> tokens)
    {
        var data = new List<int>();
        var previousLine = 0;
        var previousStart = 0;
        foreach (var token in tokens)
        {
            var line = token.Range.Start.Line;
            var start = token.Range.Start.Character;
            var deltaLine = line - previousLine;
            var deltaStart = deltaLine == 0 ? start - previousStart : start;
            data.Add(deltaLine);
            data.Add(deltaStart);
            data.Add(token.Range.End.Character - token.Range.Start.Character);
            data.Add(SemanticTokenTypeIndexes[token.TokenType]);
            data.Add(GetSemanticTokenModifierBits(token.TokenModifiers));
            previousLine = line;
            previousStart = start;
        }

        return data;
    }

    private static bool TryCreateSemanticToken(
        IReadOnlyList<string> lines,
        VbaSourceDefinition definition,
        bool isDeclaration,
        out VbaSemanticToken token,
        VbaRange? rangeOverride = null,
        string? textOverride = null)
    {
        token = default!;
        var range = rangeOverride ?? definition.Range;
        if (range.Start.Line < 0 ||
            range.Start.Line >= lines.Count ||
            range.End.Line != range.Start.Line ||
            range.Start.Character < 0 ||
            range.End.Character > lines[range.Start.Line].Length ||
            range.End.Character <= range.Start.Character)
        {
            return false;
        }

        var text = textOverride ?? lines[range.Start.Line][range.Start.Character..range.End.Character];
        var tokenType = GetSemanticTokenType(definition);
        if (tokenType is null)
        {
            return false;
        }

        token = new VbaSemanticToken(
            range,
            text,
            tokenType,
            GetSemanticTokenModifiers(definition, isDeclaration));
        return true;
    }

    private static string? GetSemanticTokenType(VbaSourceDefinition definition)
        => definition.Kind switch
        {
            VbaSourceDefinitionKind.Class => "class",
            VbaSourceDefinitionKind.Form => "class",
            VbaSourceDefinitionKind.Type => "struct",
            VbaSourceDefinitionKind.Enum => "enum",
            VbaSourceDefinitionKind.EnumMember => "enumMember",
            VbaSourceDefinitionKind.Procedure => definition.ParentTypeName is null ? "function" : "method",
            VbaSourceDefinitionKind.Property => "property",
            VbaSourceDefinitionKind.TypeMember => "field",
            VbaSourceDefinitionKind.Event => "event",
            VbaSourceDefinitionKind.Constant => definition.Visibility == VbaSourceDefinitionVisibility.Local ? "variable" : "field",
            VbaSourceDefinitionKind.Variable => definition.Visibility == VbaSourceDefinitionVisibility.Local ? "variable" : "field",
            VbaSourceDefinitionKind.Parameter => "parameter",
            _ => null
        };

    private static IReadOnlyList<string> GetSemanticTokenModifiers(
        VbaSourceDefinition definition,
        bool isDeclaration)
    {
        var modifiers = new List<string>();
        if (isDeclaration)
        {
            modifiers.Add("declaration");
        }

        if (definition.Kind == VbaSourceDefinitionKind.Constant)
        {
            modifiers.Add("readonly");
        }

        if (VbaProjectReferenceCatalogSet.IsExternalDefinition(definition))
        {
            modifiers.Add("defaultLibrary");
        }

        return modifiers;
    }

    private static int GetSemanticTokenModifierBits(IReadOnlyList<string> modifiers)
    {
        var bits = 0;
        foreach (var modifier in modifiers)
        {
            if (SemanticTokenModifierIndexes.TryGetValue(modifier, out var index))
            {
                bits |= 1 << index;
            }
        }

        return bits;
    }

    private static string GetRangeKey(VbaRange range)
        => $"{range.Start.Line}:{range.Start.Character}:{range.End.Line}:{range.End.Character}";

    private static bool SameUri(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
