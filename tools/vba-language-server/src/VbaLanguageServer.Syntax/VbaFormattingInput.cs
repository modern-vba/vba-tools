using System.Text.RegularExpressions;

namespace VbaLanguageServer.Syntax;

/// <summary>
/// Represents one syntax-owned line fact used by source formatting.
/// </summary>
/// <param name="LineNumber">The zero-based physical source line number.</param>
/// <param name="Text">The physical line text without a newline.</param>
/// <param name="CodeText">The code portion before any apostrophe comment.</param>
/// <param name="TrimmedCodeText">The code portion after leading whitespace is removed.</param>
/// <param name="Range">The source range covered by the physical line.</param>
/// <param name="BlockTransition">The indentation block transition recognized on the line.</param>
/// <param name="IndentationDepth">The indentation depth to apply when indentation is structurally valid.</param>
/// <param name="IsBlankOrComment">Whether the line has no code before comments.</param>
/// <param name="IsFormattingIgnored">Whether the line should not affect indentation state.</param>
/// <param name="IsFormDesigner">Whether the line is inside a form designer block.</param>
/// <param name="IsContinuationLine">Whether the line is formatted as a continuation body line.</param>
public sealed record VbaFormattingLine(
    int LineNumber,
    string Text,
    string CodeText,
    string TrimmedCodeText,
    VbaSyntaxRange Range,
    VbaFormattingBlockTransition BlockTransition,
    int IndentationDepth,
    bool IsBlankOrComment,
    bool IsFormattingIgnored,
    bool IsFormDesigner,
    bool IsContinuationLine);

/// <summary>
/// Describes the block transition recognized for a physical source line.
/// </summary>
/// <param name="OpenTerminator">The terminator pushed by an opening line.</param>
/// <param name="CloseTerminator">The terminator consumed by a closing line.</param>
/// <param name="BranchTerminator">The terminator branched under by Else, ElseIf, or Case.</param>
public sealed record VbaFormattingBlockTransition(
    string? OpenTerminator = null,
    string? CloseTerminator = null,
    string? BranchTerminator = null);

/// <summary>
/// Provides syntax-derived source facts used by formatting adapters.
/// </summary>
/// <param name="Lines">The physical line facts in source order.</param>
/// <param name="CanApplyIndentation">Whether block facts formed a balanced indentation model.</param>
public sealed record VbaFormattingInput(
    IReadOnlyList<VbaFormattingLine> Lines,
    bool CanApplyIndentation)
{
    /// <summary>
    /// Creates formatting input from a parsed syntax tree.
    /// </summary>
    /// <param name="syntaxTree">The parsed syntax tree.</param>
    /// <returns>The syntax-owned formatting input.</returns>
    public static VbaFormattingInput FromSyntaxTree(VbaSyntaxTree syntaxTree)
    {
        var sourceText = VbaSourceText.From(syntaxTree.Text);
        var lines = sourceText.Lines;
        var formDesignerRange = syntaxTree.Module.FormDesignerBlock?.Range;
        var formattingLines = new List<VbaFormattingLine>(lines.Count);
        var blockStack = new Stack<string>();
        var inContinuation = false;
        var continuationDepth = 0;
        var canApplyIndentation = true;

        foreach (var line in lines)
        {
            var range = sourceText.RangeForLine(line, 0, line.Text.Length);
            var isFormDesigner = IsLineInRange(formDesignerRange, line.LineNumber);
            var codeText = VbaSourceText.StripApostropheComment(line.Text);
            var trimmed = codeText.TrimStart();
            var isBlankOrComment = string.IsNullOrWhiteSpace(trimmed);
            var isIgnored = IsFormattingIgnoredCodeLine(trimmed);
            var isContinuationLine = inContinuation && !isFormDesigner && !isBlankOrComment && !isIgnored;
            var depth = inContinuation ? continuationDepth : blockStack.Count;
            var transition = new VbaFormattingBlockTransition();

            if (!isFormDesigner)
            {
                if (isBlankOrComment || isIgnored)
                {
                    inContinuation = VbaSourceText.HasLineContinuation(codeText);
                }
                else if (inContinuation)
                {
                    inContinuation = VbaSourceText.HasLineContinuation(codeText);
                }
                else
                {
                    var closeTerminator = GetIndentationCloseTerminator(trimmed);
                    var branchTerminator = GetIndentationBranchTerminator(trimmed);
                    transition = transition with
                    {
                        CloseTerminator = closeTerminator,
                        BranchTerminator = branchTerminator
                    };

                    if (closeTerminator is not null)
                    {
                        if (blockStack.Count == 0
                            || !blockStack.Peek().Equals(closeTerminator, StringComparison.OrdinalIgnoreCase))
                        {
                            canApplyIndentation = false;
                        }
                        else
                        {
                            blockStack.Pop();
                            depth = blockStack.Count;
                        }
                    }
                    else if (branchTerminator is not null)
                    {
                        if (blockStack.Count == 0
                            || !blockStack.Peek().Equals(branchTerminator, StringComparison.OrdinalIgnoreCase))
                        {
                            canApplyIndentation = false;
                        }
                        else
                        {
                            depth = Math.Max(0, blockStack.Count - 1);
                        }
                    }
                    else
                    {
                        depth = blockStack.Count;
                    }

                    var openTerminator = GetIndentationOpenTerminator(trimmed);
                    transition = transition with { OpenTerminator = openTerminator };
                    if (openTerminator is not null)
                    {
                        blockStack.Push(openTerminator);
                    }

                    if (VbaSourceText.HasLineContinuation(codeText))
                    {
                        inContinuation = true;
                        continuationDepth = depth + 1;
                    }
                }
            }

            formattingLines.Add(new VbaFormattingLine(
                line.LineNumber,
                line.Text,
                codeText,
                trimmed,
                range,
                transition,
                depth,
                isBlankOrComment,
                isIgnored,
                isFormDesigner,
                isContinuationLine));
        }

        return new VbaFormattingInput(
            formattingLines,
            canApplyIndentation && blockStack.Count == 0 && !inContinuation);
    }

    private static bool IsLineInRange(VbaSyntaxRange? range, int line)
        => range is not null
            && line >= range.Start.Line
            && line <= range.End.Line
            && (line != range.End.Line || range.End.Character > 0);

    private static bool IsFormattingIgnoredCodeLine(string trimmedLine)
        => Regex.IsMatch(trimmedLine, "^Attribute\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || Regex.IsMatch(trimmedLine, "^Option\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || trimmedLine.StartsWith("#", StringComparison.Ordinal);

    private static string? GetIndentationOpenTerminator(string trimmedLine)
    {
        if (Regex.IsMatch(
            trimmedLine,
            "^((Public|Private|Friend)\\s+)?(Static\\s+)?Sub\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Sub";
        }

        if (Regex.IsMatch(
            trimmedLine,
            "^((Public|Private|Friend)\\s+)?(Static\\s+)?Function\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Function";
        }

        if (Regex.IsMatch(
            trimmedLine,
            "^((Public|Private|Friend)\\s+)?(Static\\s+)?Property\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Property";
        }

        if (Regex.IsMatch(trimmedLine, "^If\\b.*\\bThen\\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End If";
        }

        if (Regex.IsMatch(trimmedLine, "^Select\\s+Case\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Select";
        }

        if (Regex.IsMatch(trimmedLine, "^With\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End With";
        }

        if (Regex.IsMatch(trimmedLine, "^For\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && !Regex.IsMatch(trimmedLine, ":\\s*Next\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "Next";
        }

        if (Regex.IsMatch(trimmedLine, "^Do\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && !Regex.IsMatch(trimmedLine, ":\\s*Loop\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "Loop";
        }

        if (Regex.IsMatch(trimmedLine, "^While\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "Wend";
        }

        if (Regex.IsMatch(trimmedLine, "^((Public|Private|Friend)\\s+)?Enum\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Enum";
        }

        if (Regex.IsMatch(trimmedLine, "^((Public|Private|Friend)\\s+)?Type\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Type";
        }

        return null;
    }

    private static string? GetIndentationCloseTerminator(string trimmedLine)
    {
        if (Regex.IsMatch(trimmedLine, "^End\\s+Sub\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Sub";
        }

        if (Regex.IsMatch(trimmedLine, "^End\\s+Function\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Function";
        }

        if (Regex.IsMatch(trimmedLine, "^End\\s+Property\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Property";
        }

        if (Regex.IsMatch(trimmedLine, "^End\\s+If\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End If";
        }

        if (Regex.IsMatch(trimmedLine, "^End\\s+Select\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Select";
        }

        if (Regex.IsMatch(trimmedLine, "^End\\s+With\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End With";
        }

        if (Regex.IsMatch(trimmedLine, "^Next\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "Next";
        }

        if (Regex.IsMatch(trimmedLine, "^Loop\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "Loop";
        }

        if (Regex.IsMatch(trimmedLine, "^Wend\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "Wend";
        }

        if (Regex.IsMatch(trimmedLine, "^End\\s+Enum\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Enum";
        }

        if (Regex.IsMatch(trimmedLine, "^End\\s+Type\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Type";
        }

        return null;
    }

    private static string? GetIndentationBranchTerminator(string trimmedLine)
    {
        if (Regex.IsMatch(trimmedLine, "^(Else|ElseIf)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End If";
        }

        if (Regex.IsMatch(trimmedLine, "^Case\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "End Select";
        }

        return null;
    }
}
