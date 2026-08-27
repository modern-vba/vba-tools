namespace VbaLanguageServer.Syntax;

/// <summary>
/// Represents one physical line split into code and comment portions.
/// </summary>
/// <param name="CodePart">The line portion before an apostrophe or Rem comment.</param>
/// <param name="CommentPart">The apostrophe or Rem comment portion, or an empty string.</param>
public sealed record VbaCodeLineParts(string CodePart, string CommentPart);

/// <summary>
/// Provides shared token-based lexical operations over physical source lines.
/// Position-dependent editor queries belong to <see cref="VbaSyntaxTree.GetPositionSyntax"/>.
/// </summary>
public static class VbaLexicalFacts
{
    /// <summary>
    /// Determines whether one physical line contains only whitespace or a VBA comment.
    /// </summary>
    public static bool IsBlankOrCommentOnlyLine(string line)
    {
        if (line.All(VbaIdentifier.IsWhitespace))
        {
            return true;
        }

        var tokens = VbaTokenStream.FromText(line).Tokens;
        for (var tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
        {
            var token = tokens[tokenIndex];
            if (token.Kind is VbaTokenKind.Whitespace or VbaTokenKind.NewLine)
            {
                continue;
            }

            return token.Kind == VbaTokenKind.Comment
                || IsRemCommentStart(tokens, tokenIndex, line);
        }

        return true;
    }

    /// <summary>
    /// Determines whether a character position is within an apostrophe or Rem comment.
    /// </summary>
    public static bool IsPositionInComment(string line, int character)
    {
        var commentStart = FindCommentStart(line);
        return commentStart >= 0 && character >= commentStart;
    }

    /// <summary>
    /// Finds identifier occurrences in the code portion of a physical line.
    /// </summary>
    public static IEnumerable<VbaIdentifierOccurrence> FindCodeIdentifierOccurrences(string line)
    {
        var tokens = VbaTokenStream.FromText(line).Tokens;
        for (var tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
        {
            var token = tokens[tokenIndex];
            if (token.Kind == VbaTokenKind.Comment
                || IsRemCommentStart(tokens, tokenIndex, line))
            {
                yield break;
            }

            if (token.Kind is VbaTokenKind.Identifier or VbaTokenKind.Keyword)
            {
                yield return new VbaIdentifierOccurrence(
                    token.Text,
                    token.Range.Start.Character,
                    token.Range.End.Character);
            }
        }
    }

    /// <summary>
    /// Splits one physical line into code and apostrophe or Rem comment portions.
    /// </summary>
    public static VbaCodeLineParts SplitCodeAndComment(string line)
    {
        var commentStart = FindCommentStart(line);
        return commentStart < 0
            ? new VbaCodeLineParts(line, "")
            : new VbaCodeLineParts(line[..commentStart], line[commentStart..]);
    }

    private static int FindCommentStart(string line)
    {
        var tokens = VbaTokenStream.FromText(line).Tokens;
        var isStatementStart = true;
        var isFirstSignificantToken = true;
        foreach (var token in tokens)
        {
            if (token.Kind is VbaTokenKind.Whitespace or VbaTokenKind.NewLine)
            {
                continue;
            }

            if (token.Kind == VbaTokenKind.Comment
                || (isStatementStart && IsRemCommentStart(token, line)))
            {
                return token.Range.Start.Character;
            }

            if (isFirstSignificantToken && IsNumericLineLabel(token, line))
            {
                isFirstSignificantToken = false;
                continue;
            }

            isStatementStart = token.Kind == VbaTokenKind.Punctuation
                && token.Text == ":";
            isFirstSignificantToken = false;
        }

        return -1;
    }

    private static bool IsNumericLineLabel(VbaToken token, string line)
    {
        var tokenEnd = token.Range.End.Character;
        return token.Kind == VbaTokenKind.NumericLiteral
            && token.Text.All(character => character is >= '0' and <= '9')
            && tokenEnd < line.Length
            && VbaIdentifier.IsWhitespace(line[tokenEnd]);
    }

    private static bool IsRemCommentStart(VbaToken token, string line)
    {
        if (!token.Text.Equals("Rem", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var tokenEnd = token.Range.End.Character;
        return tokenEnd == line.Length
            || (tokenEnd < line.Length && VbaIdentifier.IsWhitespace(line[tokenEnd]));
    }

    private static bool IsRemCommentStart(
        IReadOnlyList<VbaToken> tokens,
        int tokenIndex,
        string line)
    {
        var token = tokens[tokenIndex];
        if (!token.Text.Equals("Rem", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (tokens
            .Take(tokenIndex)
            .Any(previous => previous.Kind != VbaTokenKind.Whitespace))
        {
            return false;
        }

        var tokenEnd = token.Range.End.Character;
        return tokenEnd == line.Length
            || (tokenEnd < line.Length && VbaIdentifier.IsWhitespace(line[tokenEnd]));
    }
}
