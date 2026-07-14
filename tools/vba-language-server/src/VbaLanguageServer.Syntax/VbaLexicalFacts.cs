namespace VbaLanguageServer.Syntax;

/// <summary>
/// Represents one physical line split into code and apostrophe-comment portions.
/// </summary>
/// <param name="CodePart">The line portion before an apostrophe comment.</param>
/// <param name="CommentPart">The apostrophe comment portion, or an empty string.</param>
public sealed record VbaCodeLineParts(string CodePart, string CommentPart);

/// <summary>
/// Provides reusable lexical source facts for semantic and formatting features.
/// </summary>
public sealed class VbaLexicalFacts
{
    private readonly string[] lines;
    private readonly VbaTokenStream tokenStream;

    private VbaLexicalFacts(string text, string[] lines, VbaTokenStream tokenStream)
    {
        Text = text;
        this.lines = lines;
        this.tokenStream = tokenStream;
    }

    /// <summary>
    /// Gets the complete source text.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Gets physical source lines after newline normalization.
    /// </summary>
    public IReadOnlyList<string> Lines => lines;

    /// <summary>
    /// Gets the token stream used by lexical queries.
    /// </summary>
    public VbaTokenStream TokenStream => tokenStream;

    /// <summary>
    /// Creates lexical facts from complete source text.
    /// </summary>
    /// <param name="source">The source text.</param>
    /// <returns>The lexical fact query module.</returns>
    public static VbaLexicalFacts FromText(string source)
        => new(source, VbaSourceText.SplitLines(source), VbaTokenStream.FromText(source));

    /// <summary>
    /// Creates lexical facts from a parsed syntax tree.
    /// </summary>
    /// <param name="syntaxTree">The parsed syntax tree.</param>
    /// <returns>The lexical fact query module.</returns>
    public static VbaLexicalFacts FromSyntaxTree(VbaSyntaxTree syntaxTree)
        => new(syntaxTree.Text, VbaSourceText.SplitLines(syntaxTree.Text), syntaxTree.TokenStream);

    /// <summary>
    /// Gets a source line when the line number is valid.
    /// </summary>
    /// <param name="line">The zero-based line number.</param>
    /// <param name="text">The physical line text.</param>
    /// <returns>True when the line exists.</returns>
    public bool TryGetLine(int line, out string text)
    {
        text = "";
        if (line < 0 || line >= lines.Length)
        {
            return false;
        }

        text = lines[line];
        return true;
    }

    /// <summary>
    /// Gets the logical statement prefix ending at a source position.
    /// </summary>
    /// <param name="line">The zero-based line number.</param>
    /// <param name="character">The zero-based character position.</param>
    /// <param name="logicalPrefix">The logical prefix text.</param>
    /// <returns>True when the line exists.</returns>
    public bool TryGetLogicalPrefix(int line, int character, out string logicalPrefix)
    {
        logicalPrefix = "";
        if (line < 0 || line >= lines.Length)
        {
            return false;
        }

        logicalPrefix = VbaSourceText.GetLogicalPrefix(lines, line, character);
        return true;
    }

    /// <summary>
    /// Gets the code identifier at a source position while excluding strings and apostrophe comments.
    /// </summary>
    /// <param name="line">The zero-based line number.</param>
    /// <param name="character">The zero-based character position.</param>
    /// <param name="identifier">The identifier occurrence.</param>
    /// <returns>True when the position resolves to a code identifier.</returns>
    public bool TryGetCodeIdentifierAt(
        int line,
        int character,
        out VbaIdentifierOccurrence identifier)
    {
        identifier = default!;
        if (!TryGetLine(line, out _)
            || !IsCodePosition(line, character))
        {
            return false;
        }

        var found = FindIdentifierTokenAt(line, character);
        if (found is null)
        {
            return false;
        }

        identifier = found;
        return true;
    }

    /// <summary>
    /// Determines whether a source position is in code rather than inside a string or apostrophe comment.
    /// </summary>
    /// <param name="line">The zero-based line number.</param>
    /// <param name="character">The zero-based character position.</param>
    /// <returns>True when the position is inside code.</returns>
    public bool IsCodePosition(int line, int character)
    {
        if (!TryGetLine(line, out var text)
            || IsRemCommentPosition(text, character))
        {
            return false;
        }

        var token = FindTokenAt(line, character);
        return token is null
            || token.Kind is not VbaTokenKind.StringLiteral and not VbaTokenKind.Comment;
    }

    private static bool IsRemCommentPosition(string line, int character)
    {
        var start = 0;
        while (start < line.Length && char.IsWhiteSpace(line[start]))
        {
            start++;
        }

        if (start >= line.Length
            || line.Length - start < 3
            || !line.AsSpan(start, 3).Equals("Rem", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var end = start + 3;
        if (end < line.Length && VbaSourceText.IsIdentifierCharacter(line[end]))
        {
            return false;
        }

        return character >= start;
    }

    /// <summary>
    /// Finds identifier occurrences in the code portion of a physical line.
    /// </summary>
    /// <param name="line">The physical line to inspect.</param>
    /// <returns>The identifier occurrences in source order.</returns>
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
    /// <param name="line">The physical line text.</param>
    /// <returns>The code/comment split.</returns>
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

    /// <summary>
    /// Gets the underlying normalized lines for legacy parser helpers.
    /// </summary>
    /// <returns>The normalized physical lines.</returns>
    public string[] ToLineArray()
        => lines;

    private VbaIdentifierOccurrence? FindIdentifierTokenAt(int line, int character)
    {
        var token = tokenStream.Tokens
            .FirstOrDefault(token =>
                token.Kind is VbaTokenKind.Identifier or VbaTokenKind.Keyword
                && IsIdentifierPosition(token, line, character));
        return token is null
            ? null
            : new VbaIdentifierOccurrence(token.Text, token.Range.Start.Character, token.Range.End.Character);
    }

    private VbaToken? FindTokenAt(int line, int character)
        => tokenStream.Tokens.FirstOrDefault(token => IsTokenPosition(token, line, character));

    private static bool IsIdentifierPosition(VbaToken token, int line, int character)
        => token.Range.Start.Line == line
            && token.Range.End.Line == line
            && token.Range.Start.Character <= character
            && character <= token.Range.End.Character;

    private static bool IsTokenPosition(VbaToken token, int line, int character)
        => token.Range.Start.Line == line
            && token.Range.End.Line == line
            && token.Range.Start.Character <= character
            && character < token.Range.End.Character;

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
