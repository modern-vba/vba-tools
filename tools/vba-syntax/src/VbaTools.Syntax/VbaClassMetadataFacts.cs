namespace VbaTools.Syntax;

/// <summary>
/// Recognizes the leading export-only class metadata without consuming VBA code.
/// </summary>
internal static class VbaClassMetadataFacts
{
    /// <summary>Returns the exclusive physical line boundary of the metadata prefix.</summary>
    public static int GetEndLine(VbaSyntaxTree tree)
    {
        if (tree.Module.Kind != VbaModuleKind.ClassModule)
        {
            return 0;
        }

        var foundVersion = false;
        var foundEnd = false;
        foreach (var line in tree.SourceText.Lines)
        {
            var tokens = VbaTokenStream.FromText(line.Text).Tokens
                .Where(token => token.Kind is not VbaTokenKind.Whitespace
                    and not VbaTokenKind.NewLine
                    and not VbaTokenKind.Comment)
                .ToArray();
            if (tokens.Length == 0)
            {
                continue;
            }

            if (!foundVersion)
            {
                if (tokens.Length != 3
                    || !IsWord(tokens[0], "VERSION")
                    || tokens[1].Kind != VbaTokenKind.NumericLiteral
                    || !IsWord(tokens[2], "CLASS"))
                {
                    return 0;
                }

                foundVersion = true;
                continue;
            }

            if (foundEnd)
            {
                return line.LineNumber;
            }

            if (tokens.Length == 1 && IsWord(tokens[0], "END"))
            {
                foundEnd = true;
                continue;
            }

            if (tokens.Length == 1 && IsWord(tokens[0], "BEGIN")
                || IsScalarProperty(tokens))
            {
                continue;
            }

            // A missing END or Attribute must not hide a recognizable VBA body.
            return line.LineNumber;
        }

        return foundVersion ? tree.SourceText.Lines.Count : 0;
    }

    private static bool IsWord(VbaToken token, string word)
        => token.Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    private static bool IsScalarProperty(IReadOnlyList<VbaToken> tokens)
    {
        if (tokens.Count < 2
            || tokens[0].Kind is not (VbaTokenKind.Identifier or VbaTokenKind.Keyword)
            || tokens[1].Text != "=")
        {
            return false;
        }

        // An unfinished value is metadata, but statements and expressions are not.
        return tokens.Count == 2
            || tokens.Count == 3
                && (tokens[2].Kind is VbaTokenKind.NumericLiteral or VbaTokenKind.StringLiteral
                    || IsWord(tokens[2], "True") || IsWord(tokens[2], "False"))
            || tokens.Count == 4
                && tokens[2].Text is "+" or "-"
                && tokens[3].Kind == VbaTokenKind.NumericLiteral;
    }
}
