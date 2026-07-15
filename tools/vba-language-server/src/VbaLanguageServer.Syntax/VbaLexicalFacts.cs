namespace VbaLanguageServer.Syntax;

/// <summary>
/// Represents one physical line split into code and apostrophe-comment portions.
/// </summary>
/// <param name="CodePart">The line portion before an apostrophe comment.</param>
/// <param name="CommentPart">The apostrophe comment portion, or an empty string.</param>
public sealed record VbaCodeLineParts(string CodePart, string CommentPart);

/// <summary>
/// Provides token-based bulk lexical operations used by source formatting.
/// Position-dependent editor queries belong to <see cref="VbaSyntaxTree.GetPositionSyntax"/>.
/// </summary>
public static class VbaLexicalFacts
{
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
                || IsRemCommentStart(tokens, tokenIndex))
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
    /// Splits one physical line into code and apostrophe-comment portions.
    /// </summary>
    public static VbaCodeLineParts SplitCodeAndComment(string line)
    {
        var commentStart = VbaTokenStream.FromText(line)
            .Tokens
            .FirstOrDefault(token => token.Kind == VbaTokenKind.Comment)
            ?.Range
            .Start
            .Character
            ?? -1;
        return commentStart < 0
            ? new VbaCodeLineParts(line, "")
            : new VbaCodeLineParts(line[..commentStart], line[commentStart..]);
    }

    private static bool IsRemCommentStart(IReadOnlyList<VbaToken> tokens, int tokenIndex)
    {
        var token = tokens[tokenIndex];
        if (!token.Text.Equals("Rem", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return tokens
            .Take(tokenIndex)
            .All(previous => previous.Kind == VbaTokenKind.Whitespace);
    }
}
