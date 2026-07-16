namespace VbaLanguageServer.Syntax;

/// <summary>
/// Describes one resolved editor indentation unit.
/// </summary>
public sealed record VbaIndentationStyle
{
    private VbaIndentationStyle(bool insertSpaces, int indentSize)
    {
        InsertSpaces = insertSpaces;
        IndentSize = indentSize;
    }

    /// <summary>
    /// Gets whether indentation uses spaces instead of tabs.
    /// </summary>
    public bool InsertSpaces { get; }

    /// <summary>
    /// Gets the number of spaces in one indentation unit.
    /// </summary>
    public int IndentSize { get; }

    /// <summary>
    /// Creates an indentation style from resolved editor options.
    /// </summary>
    /// <param name="insertSpaces">Whether indentation uses spaces instead of tabs.</param>
    /// <param name="indentSize">The resolved number of spaces in one indentation unit.</param>
    /// <returns>The normalized indentation style.</returns>
    public static VbaIndentationStyle FromEditorOptions(bool insertSpaces, int indentSize)
        => new(insertSpaces, Math.Max(1, indentSize));

    /// <summary>
    /// Creates leading whitespace for a syntax-derived indentation depth.
    /// </summary>
    /// <param name="depth">The non-negative indentation depth.</param>
    /// <returns>The leading whitespace.</returns>
    public string CreateLeadingWhitespace(int depth)
    {
        var normalizedDepth = Math.Max(0, depth);
        return InsertSpaces
            ? new string(' ', IndentSize * normalizedDepth)
            : new string('\t', normalizedDepth);
    }
}
