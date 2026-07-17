using System.Text;

namespace VbaLanguageServer.Syntax;

/// <summary>
/// Parses VBA preprocessor directives and nested #If blocks from module source lines.
/// </summary>
internal static class VbaPreprocessorParser
{
    /// <summary>
    /// Parses preprocessor directives beginning at the module code section.
    /// </summary>
    /// <param name="sourceText">The indexed source snapshot.</param>
    /// <param name="tokenStream">The atomic token stream for the same snapshot.</param>
    /// <param name="codeStartLine">The zero-based line where code parsing begins.</param>
    /// <returns>The parsed directives, blocks, and preprocessor diagnostics.</returns>
    public static ParsedPreprocessor Parse(
        VbaSourceText sourceText,
        VbaTokenStream tokenStream,
        int codeStartLine)
    {
        var lines = sourceText.Lines;
        var directiveTokens = tokenStream.Tokens
            .Where(token =>
                token.Kind == VbaTokenKind.PreprocessorDirective
                && token.Range.Start.Line >= codeStartLine)
            .ToDictionary(token => token.Range.Start.Line);
        var directives = new List<VbaPreprocessorDirectiveSyntax>();
        var blocks = new List<VbaPreprocessorBlockSyntax>();
        var diagnostics = new List<VbaSyntaxDiagnostic>();
        var stack = new Stack<PreprocessorBlockBuilder>();

        for (var lineIndex = codeStartLine; lineIndex < lines.Count; lineIndex++)
        {
            if (!directiveTokens.TryGetValue(lineIndex, out var directiveToken))
            {
                if (stack.Count > 0)
                {
                    AddBodyLines(stack.Peek().CurrentBranch, lines, lineIndex, lineIndex);
                }

                continue;
            }

            var firstDirectiveLine = lineIndex;
            var finalDirectiveLine = Math.Min(
                directiveToken.Range.End.Line,
                lines.Count - 1);
            lineIndex = finalDirectiveLine;
            var hasDirective = TryCreatePreprocessorDirective(
                directiveToken,
                out var directive,
                out var malformedDirectiveDiagnostic);
            if (malformedDirectiveDiagnostic is not null)
            {
                diagnostics.Add(malformedDirectiveDiagnostic);
            }

            if (!hasDirective)
            {
                if (stack.Count > 0)
                {
                    AddBodyLines(
                        stack.Peek().CurrentBranch,
                        lines,
                        firstDirectiveLine,
                        finalDirectiveLine);
                }

                continue;
            }

            directives.Add(directive);
            if (malformedDirectiveDiagnostic is not null
                && directive.Kind != VbaPreprocessorDirectiveKind.If)
            {
                if (stack.Count > 0)
                {
                    AddBodyLines(
                        stack.Peek().CurrentBranch,
                        lines,
                        firstDirectiveLine,
                        finalDirectiveLine);
                }

                continue;
            }

            switch (directive.Kind)
            {
                case VbaPreprocessorDirectiveKind.Const:
                    if (stack.Count > 0)
                    {
                        AddBodyLines(
                            stack.Peek().CurrentBranch,
                            lines,
                            firstDirectiveLine,
                            finalDirectiveLine);
                    }

                    break;

                case VbaPreprocessorDirectiveKind.If:
                    stack.Push(new PreprocessorBlockBuilder(directive));
                    break;

                case VbaPreprocessorDirectiveKind.ElseIf:
                case VbaPreprocessorDirectiveKind.Else:
                    if (stack.Count == 0)
                    {
                        diagnostics.Add(CreateMalformedPreprocessorDiagnostic(
                            directive,
                            $"Preprocessor directive '{directive.Text}' has no matching '#If'."));
                        break;
                    }

                    var branchError = stack.Peek().StartBranch(directive);
                    if (branchError is not null)
                    {
                        diagnostics.Add(CreateMalformedPreprocessorDiagnostic(
                            directive,
                            branchError));
                    }

                    break;

                case VbaPreprocessorDirectiveKind.EndIf:
                    if (stack.Count == 0)
                    {
                        diagnostics.Add(CreateMalformedPreprocessorDiagnostic(
                            directive,
                            "Preprocessor directive '#End If' has no matching '#If'."));
                        break;
                    }

                    var finished = stack.Pop().Build(directive);
                    if (stack.Count == 0)
                    {
                        blocks.Add(finished);
                    }
                    else
                    {
                        stack.Peek().CurrentBranch.NestedBlocks.Add(finished);
                    }

                    break;
            }
        }

        while (stack.Count > 0)
        {
            var unfinished = stack.Pop().Build(endDirective: null);
            diagnostics.Add(new VbaSyntaxDiagnostic(
                "syntax.malformedPreprocessorNesting",
                "Preprocessor block is missing '#End If'.",
                unfinished.IfDirective.Range));
            if (stack.Count == 0)
            {
                blocks.Add(unfinished);
            }
            else
            {
                stack.Peek().CurrentBranch.NestedBlocks.Add(unfinished);
            }
        }

        return new ParsedPreprocessor(directives, blocks, diagnostics);
    }

    private static void AddBodyLines(
        PreprocessorBranchBuilder branch,
        IReadOnlyList<VbaSourceLine> lines,
        int firstLine,
        int finalLine)
    {
        for (var lineIndex = firstLine; lineIndex <= finalLine; lineIndex++)
        {
            branch.BodyLines.Add(lines[lineIndex].Text);
        }

        branch.EndLine = lines[finalLine];
    }

    private static bool TryCreatePreprocessorDirective(
        VbaToken token,
        out VbaPreprocessorDirectiveSyntax directive,
        out VbaSyntaxDiagnostic? malformedDirectiveDiagnostic)
    {
        directive = default!;
        malformedDirectiveDiagnostic = null;
        var trimmed = token.Text.TrimStart();
        if (!trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            return false;
        }

        var rawDirectiveBody = GetDirectiveBody(trimmed);
        var hasInvalidCommentContinuation =
            HasCommentAfterLineContinuation(rawDirectiveBody);
        var directiveBody = NormalizeDirectiveBody(rawDirectiveBody);
        VbaPreprocessorDirectiveKind? kind =
            StartsWithDirectiveWord(directiveBody, "Const")
                ? VbaPreprocessorDirectiveKind.Const
                : StartsWithDirectiveWord(directiveBody, "ElseIf")
                    ? VbaPreprocessorDirectiveKind.ElseIf
                    : StartsWithDirectiveWord(directiveBody, "Else")
                        ? VbaPreprocessorDirectiveKind.Else
                        : IsEndIfDirective(directiveBody)
                            ? VbaPreprocessorDirectiveKind.EndIf
                            : StartsWithDirectiveWord(directiveBody, "If")
                                ? VbaPreprocessorDirectiveKind.If
                                : null;
        if (kind is null)
        {
            if (!TryGetMalformedConditionalDirectiveKind(
                directiveBody,
                out var recoveredKind))
            {
                if (LooksLikeMalformedConditionalDirective(directiveBody))
                {
                    malformedDirectiveDiagnostic = CreateMalformedDirectiveDiagnostic(
                        token.Range,
                        trimmed);
                }

                return false;
            }

            kind = recoveredKind;
            malformedDirectiveDiagnostic = CreateMalformedDirectiveDiagnostic(
                token.Range,
                trimmed);
        }

        if (kind != VbaPreprocessorDirectiveKind.Const
            && !HasCompleteConditionalDirectiveShape(kind.Value, directiveBody))
        {
            malformedDirectiveDiagnostic ??= CreateMalformedDirectiveDiagnostic(
                token.Range,
                trimmed);
        }

        if (hasInvalidCommentContinuation)
        {
            malformedDirectiveDiagnostic ??= CreateMalformedDirectiveDiagnostic(
                token.Range,
                trimmed);
        }

        directive = new VbaPreprocessorDirectiveSyntax(
            kind.Value,
            trimmed,
            token.Range);
        return true;
    }

    private static string GetDirectiveBody(string text)
    {
        var index = 1;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return text[index..];
    }

    private static string NormalizeDirectiveBody(string text)
    {
        var result = new StringBuilder(text.Length);
        var skipContinuationNewLine = false;
        foreach (var token in VbaTokenStream.FromText(text).Tokens)
        {
            if (token.Kind == VbaTokenKind.LineContinuation)
            {
                result.Append(' ');
                skipContinuationNewLine = true;
                continue;
            }

            if (token.Kind == VbaTokenKind.NewLine && skipContinuationNewLine)
            {
                skipContinuationNewLine = false;
                continue;
            }

            result.Append(token.Text);
        }

        return result.ToString();
    }

    private static bool HasCommentAfterLineContinuation(string text)
    {
        var sourceText = VbaSourceText.From(text);
        foreach (var line in sourceText.Lines)
        {
            var commentStart = VbaSourceText.FindApostropheCommentStart(line.Text);
            if (commentStart < 0)
            {
                continue;
            }

            var code = line.Text[..commentStart];
            if (code.TrimEnd().EndsWith("_", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetMalformedConditionalDirectiveKind(
        string text,
        out VbaPreprocessorDirectiveKind kind)
    {
        if (text.StartsWith("ElseIf", StringComparison.OrdinalIgnoreCase))
        {
            kind = VbaPreprocessorDirectiveKind.ElseIf;
            return true;
        }

        if (text.StartsWith("Else", StringComparison.OrdinalIgnoreCase))
        {
            kind = VbaPreprocessorDirectiveKind.Else;
            return true;
        }

        if (text.StartsWith("EndIf", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("End", StringComparison.OrdinalIgnoreCase)
                && text["End".Length..]
                    .TrimStart()
                    .StartsWith("If", StringComparison.OrdinalIgnoreCase))
        {
            kind = VbaPreprocessorDirectiveKind.EndIf;
            return true;
        }

        if (text.StartsWith("If", StringComparison.OrdinalIgnoreCase))
        {
            kind = VbaPreprocessorDirectiveKind.If;
            return true;
        }

        kind = default;
        return false;
    }

    private static VbaSyntaxDiagnostic CreateMalformedDirectiveDiagnostic(
        VbaSyntaxRange range,
        string text)
        => new(
            "syntax.malformedPreprocessorDirective",
            $"Malformed conditional-compilation directive '{text}'.",
            range);

    private static bool StartsWithDirectiveWord(string text, string word)
        => text.StartsWith(word, StringComparison.OrdinalIgnoreCase)
            && (text.Length == word.Length
                || !IsIdentifierCharacter(text[word.Length]));

    private static bool IsEndIfDirective(string text)
    {
        var core = RemoveTrailingComment(text).TrimEnd();
        if (StartsWithDirectiveWord(core, "EndIf"))
        {
            return true;
        }

        if (!StartsWithDirectiveWord(core, "End"))
        {
            return false;
        }

        var remainder = core["End".Length..].TrimStart();
        return StartsWithDirectiveWord(remainder, "If");
    }

    private static bool HasCompleteConditionalDirectiveShape(
        VbaPreprocessorDirectiveKind kind,
        string text)
    {
        var core = RemoveTrailingComment(text).TrimEnd();
        return kind switch
        {
            VbaPreprocessorDirectiveKind.If =>
                HasConditionalExpressionAndThen(core, "If"),
            VbaPreprocessorDirectiveKind.ElseIf =>
                HasConditionalExpressionAndThen(core, "ElseIf"),
            VbaPreprocessorDirectiveKind.Else =>
                core.Equals("Else", StringComparison.OrdinalIgnoreCase),
            VbaPreprocessorDirectiveKind.EndIf =>
                core.Equals("EndIf", StringComparison.OrdinalIgnoreCase)
                || StartsWithDirectiveWord(core, "End")
                    && core["End".Length..].Trim()
                        .Equals("If", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool HasConditionalExpressionAndThen(string text, string directive)
    {
        if (!StartsWithDirectiveWord(text, directive))
        {
            return false;
        }

        var remainder = text[directive.Length..];
        var tokens = VbaTokenStream.FromText($"({remainder})")
            .Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.LineContinuation
                and not VbaTokenKind.Comment)
            .ToArray();
        if (tokens.Length < 4
            || !tokens[0].Text.Equals("(", StringComparison.Ordinal)
            || !tokens[^1].Text.Equals(")", StringComparison.Ordinal)
            || tokens[^2].Kind != VbaTokenKind.Keyword
            || !tokens[^2].Text.Equals("Then", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return VbaConstantExpressionSyntax
            .IsConditionalCompilationExpressionComplete(
                tokens,
                start: 1,
                end: tokens.Length - 2);
    }

    private static string RemoveTrailingComment(string text)
    {
        var inString = false;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '"')
            {
                if (inString
                    && index + 1 < text.Length
                    && text[index + 1] == '"')
                {
                    index++;
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (text[index] == '\'' && !inString)
            {
                return text[..index];
            }
        }

        return text;
    }

    private static bool LooksLikeMalformedConditionalDirective(string text)
        => text.StartsWith("If", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Else", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("End", StringComparison.OrdinalIgnoreCase);

    private static bool IsIdentifierCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value == '_';

    private static VbaSyntaxDiagnostic CreateMalformedPreprocessorDiagnostic(
        VbaPreprocessorDirectiveSyntax directive,
        string message)
        => new(
            "syntax.malformedPreprocessorNesting",
            message,
            directive.Range);
}

/// <summary>
/// Contains parsed preprocessor syntax and diagnostics.
/// </summary>
/// <param name="Directives">The parsed directive lines in source order.</param>
/// <param name="Blocks">The top-level preprocessor blocks.</param>
/// <param name="Diagnostics">The diagnostics produced while parsing preprocessor nesting.</param>
internal sealed record ParsedPreprocessor(
    IReadOnlyList<VbaPreprocessorDirectiveSyntax> Directives,
    IReadOnlyList<VbaPreprocessorBlockSyntax> Blocks,
    IReadOnlyList<VbaSyntaxDiagnostic> Diagnostics);

/// <summary>
/// Builds a preprocessor #If block while scanning directive lines.
/// </summary>
internal sealed class PreprocessorBlockBuilder
{
    private readonly List<PreprocessorBranchBuilder> branches = [];

    /// <summary>
    /// Creates a block builder for an opening #If directive.
    /// </summary>
    /// <param name="ifDirective">The opening #If directive.</param>
    public PreprocessorBlockBuilder(VbaPreprocessorDirectiveSyntax ifDirective)
    {
        IfDirective = ifDirective;
        CurrentBranch = new PreprocessorBranchBuilder(ifDirective);
        branches.Add(CurrentBranch);
    }

    /// <summary>
    /// Gets the opening #If directive for the block.
    /// </summary>
    public VbaPreprocessorDirectiveSyntax IfDirective { get; }

    /// <summary>
    /// Gets the branch currently receiving body lines and nested blocks.
    /// </summary>
    public PreprocessorBranchBuilder CurrentBranch { get; private set; }

    /// <summary>
    /// Starts a new #ElseIf or #Else branch.
    /// </summary>
    /// <param name="directive">The directive that starts the new branch.</param>
    public string? StartBranch(VbaPreprocessorDirectiveSyntax directive)
    {
        var hasElse = branches.Any(branch =>
            branch.Directive.Kind == VbaPreprocessorDirectiveKind.Else);
        var error = directive.Kind switch
        {
            VbaPreprocessorDirectiveKind.Else when hasElse =>
                "Conditional compilation block contains a duplicate '#Else' directive.",
            VbaPreprocessorDirectiveKind.ElseIf when hasElse =>
                "Conditional compilation '#ElseIf' cannot follow '#Else'.",
            _ => null
        };
        CurrentBranch = new PreprocessorBranchBuilder(directive);
        branches.Add(CurrentBranch);
        return error;
    }

    /// <summary>
    /// Builds the completed preprocessor block syntax.
    /// </summary>
    /// <param name="endDirective">The closing #End If directive, or null for an unterminated block.</param>
    /// <returns>The preprocessor block syntax.</returns>
    public VbaPreprocessorBlockSyntax Build(VbaPreprocessorDirectiveSyntax? endDirective)
    {
        var builtBranches = branches.Select(branch => branch.Build()).ToArray();
        var rangeEnd = endDirective?.Range.End
            ?? builtBranches.LastOrDefault()?.Range.End
            ?? IfDirective.Range.End;
        return new VbaPreprocessorBlockSyntax(
            IfDirective,
            builtBranches,
            endDirective,
            new VbaSyntaxRange(IfDirective.Range.Start, rangeEnd));
    }
}

/// <summary>
/// Builds one branch inside a preprocessor #If block.
/// </summary>
internal sealed class PreprocessorBranchBuilder
{
    /// <summary>
    /// Creates a branch builder for a branch directive.
    /// </summary>
    /// <param name="directive">The directive that starts the branch.</param>
    public PreprocessorBranchBuilder(VbaPreprocessorDirectiveSyntax directive)
    {
        Directive = directive;
        EndLine = null;
    }

    /// <summary>
    /// Gets the directive that starts the branch.
    /// </summary>
    public VbaPreprocessorDirectiveSyntax Directive { get; }

    /// <summary>
    /// Gets the raw body lines collected for the branch.
    /// </summary>
    public List<string> BodyLines { get; } = [];

    /// <summary>
    /// Gets nested preprocessor blocks collected inside the branch.
    /// </summary>
    public List<VbaPreprocessorBlockSyntax> NestedBlocks { get; } = [];

    /// <summary>
    /// Gets or sets the last source line included in the branch body.
    /// </summary>
    public VbaSourceLine? EndLine { get; set; }

    /// <summary>
    /// Builds the branch syntax from collected lines and nested blocks.
    /// </summary>
    /// <returns>The preprocessor branch syntax.</returns>
    public VbaPreprocessorBranchSyntax Build()
    {
        var end = EndLine is null
            ? Directive.Range.End
            : new VbaSyntaxPosition(EndLine.LineNumber, EndLine.Text.Length, EndLine.EndOffset);
        foreach (var nestedBlock in NestedBlocks)
        {
            if (nestedBlock.Range.End.Offset > end.Offset)
            {
                end = nestedBlock.Range.End;
            }
        }

        return new VbaPreprocessorBranchSyntax(
            Directive,
            string.Join('\n', BodyLines),
            new VbaSyntaxRange(Directive.Range.Start, end),
            NestedBlocks);
    }
}
