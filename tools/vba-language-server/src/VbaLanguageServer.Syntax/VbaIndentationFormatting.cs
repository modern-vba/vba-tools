namespace VbaLanguageServer.Syntax;

/// <summary>
/// Applies syntax-derived indentation decisions to formatted physical lines.
/// </summary>
public sealed class VbaIndentationFormatting
{
    private readonly VbaFormattingInput input;

    private VbaIndentationFormatting(VbaFormattingInput input)
    {
        this.input = input;
    }

    /// <summary>
    /// Creates an indentation formatter from syntax-owned formatting input.
    /// </summary>
    /// <param name="input">The syntax-owned formatting input.</param>
    /// <returns>The indentation formatter.</returns>
    public static VbaIndentationFormatting FromInput(VbaFormattingInput input)
        => new(input);

    /// <summary>
    /// Applies indentation to one already-cased physical line.
    /// </summary>
    /// <param name="line">The syntax-owned line facts.</param>
    /// <param name="text">The already-cased line text.</param>
    /// <param name="tabSize">The number of spaces per indentation level.</param>
    /// <returns>The line with indentation applied when structurally safe.</returns>
    public string Apply(VbaFormattingLine line, string text, int tabSize)
    {
        if (line.IsFormDesigner)
        {
            return text;
        }

        if (line.IsBlankOrComment && string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        if (!input.CanApplyIndentation)
        {
            return text;
        }

        if (line.IsContinuationLine)
        {
            return text;
        }

        return $"{new string(' ', Math.Max(1, tabSize) * line.IndentationDepth)}{text.TrimStart()}";
    }
}
