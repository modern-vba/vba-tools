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

    private VbaLexicalFacts(string text, string[] lines)
    {
        Text = text;
        this.lines = lines;
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
    /// Creates lexical facts from complete source text.
    /// </summary>
    /// <param name="source">The source text.</param>
    /// <returns>The lexical fact query module.</returns>
    public static VbaLexicalFacts FromText(string source)
        => new(source, VbaSourceText.SplitLines(source));

    /// <summary>
    /// Creates lexical facts from a parsed syntax tree.
    /// </summary>
    /// <param name="syntaxTree">The parsed syntax tree.</param>
    /// <returns>The lexical fact query module.</returns>
    public static VbaLexicalFacts FromSyntaxTree(VbaSyntaxTree syntaxTree)
        => FromText(syntaxTree.Text);

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
        if (!TryGetLine(line, out var text)
            || !VbaSourceText.IsCodePosition(text, character))
        {
            return false;
        }

        var found = VbaSourceText.GetIdentifierAt(text, character);
        if (found is null)
        {
            return false;
        }

        identifier = found;
        return true;
    }

    /// <summary>
    /// Finds identifier occurrences in the code portion of a physical line.
    /// </summary>
    /// <param name="line">The physical line to inspect.</param>
    /// <returns>The identifier occurrences in source order.</returns>
    public static IEnumerable<VbaIdentifierOccurrence> FindCodeIdentifierOccurrences(string line)
        => VbaSourceText.FindIdentifierOccurrences(line);

    /// <summary>
    /// Splits one physical line into code and apostrophe-comment portions.
    /// </summary>
    /// <param name="line">The physical line text.</param>
    /// <returns>The code/comment split.</returns>
    public static VbaCodeLineParts SplitCodeAndComment(string line)
    {
        var commentStart = VbaSourceText.FindApostropheCommentStart(line);
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
}
