using System.Text.RegularExpressions;

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
    /// <param name="indentationStyle">The resolved editor indentation style.</param>
    /// <returns>The line with indentation applied when structurally safe.</returns>
    public string Apply(VbaFormattingLine line, string text, VbaIndentationStyle indentationStyle)
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

        if (StartsWithLineLabel(line.TrimmedCodeText))
        {
            return text.TrimStart();
        }

        return $"{indentationStyle.CreateLeadingWhitespace(line.IndentationDepth)}{text.TrimStart()}";
    }

    private static bool StartsWithLineLabel(string trimmedCodeText)
        => Regex.IsMatch(
            trimmedCodeText,
            "^(?:[A-Za-z_][A-Za-z0-9_]*|[0-9]+)\\s*:",
            RegexOptions.CultureInvariant);
}
