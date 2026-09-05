namespace VbaTools.Syntax;

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
    /// <param name="indentationStyle">The resolved editor indentation style.</param>
    /// <returns>The line with indentation applied when structurally safe.</returns>
    public string Apply(VbaFormattingLine line, string text, VbaIndentationStyle indentationStyle)
    {
        if (line.IsFormDesigner)
        {
            return text;
        }

        if (line.IsBlankOrComment && VbaIdentifier.TrimStartWhitespace(text).Length == 0)
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

        if (StartsWithLineLabel(line.TrimmedCodeText))
        {
            return VbaIdentifier.TrimStartWhitespace(text);
        }

        return $"{indentationStyle.CreateLeadingWhitespace(line.IndentationDepth)}{VbaIdentifier.TrimStartWhitespace(text)}";
    }

    private static bool StartsWithLineLabel(string trimmedCodeText)
    {
        var tokens = VbaTokenStream.FromText(trimmedCodeText).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.Comment)
            .ToArray();
        if (tokens.Length < 2 || tokens[1].Text != ":")
        {
            return false;
        }

        var label = tokens[0];
        return VbaIdentifier.IsIdentifier(label.Text)
            || (label.Kind == VbaTokenKind.NumericLiteral
                && label.Text.All(char.IsAsciiDigit));
    }
}
