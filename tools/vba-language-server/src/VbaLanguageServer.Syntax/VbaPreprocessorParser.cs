namespace VbaLanguageServer.Syntax;

/// <summary>
/// Parses VBA preprocessor directives and nested #If blocks from module source lines.
/// </summary>
internal static class VbaPreprocessorParser
{
    /// <summary>
    /// Parses preprocessor directives beginning at the module code section.
    /// </summary>
    /// <param name="lines">The physical source lines with offsets.</param>
    /// <param name="codeStartLine">The zero-based line where code parsing begins.</param>
    /// <returns>The parsed directives, blocks, and preprocessor diagnostics.</returns>
    public static ParsedPreprocessor Parse(IReadOnlyList<SourceLine> lines, int codeStartLine)
    {
        var directives = new List<VbaPreprocessorDirectiveSyntax>();
        var blocks = new List<VbaPreprocessorBlockSyntax>();
        var diagnostics = new List<VbaSyntaxDiagnostic>();
        var stack = new Stack<PreprocessorBlockBuilder>();

        for (var lineIndex = codeStartLine; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            if (!TryCreatePreprocessorDirective(line, out var directive))
            {
                if (stack.Count > 0)
                {
                    stack.Peek().CurrentBranch.BodyLines.Add(line.Text);
                    stack.Peek().CurrentBranch.EndLine = line;
                }

                continue;
            }

            directives.Add(directive);
            switch (directive.Kind)
            {
                case VbaPreprocessorDirectiveKind.Const:
                    if (stack.Count > 0)
                    {
                        stack.Peek().CurrentBranch.BodyLines.Add(line.Text);
                        stack.Peek().CurrentBranch.EndLine = line;
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

                    stack.Peek().StartBranch(directive);
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

    private static bool TryCreatePreprocessorDirective(
        SourceLine line,
        out VbaPreprocessorDirectiveSyntax directive)
    {
        directive = default!;
        var trimmed = line.Text.TrimStart();
        if (!trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            return false;
        }

        VbaPreprocessorDirectiveKind? kind =
            trimmed.StartsWith("#Const", StringComparison.OrdinalIgnoreCase)
                ? VbaPreprocessorDirectiveKind.Const
                : trimmed.StartsWith("#ElseIf", StringComparison.OrdinalIgnoreCase)
                    ? VbaPreprocessorDirectiveKind.ElseIf
                    : trimmed.StartsWith("#Else", StringComparison.OrdinalIgnoreCase)
                        ? VbaPreprocessorDirectiveKind.Else
                        : trimmed.StartsWith("#End If", StringComparison.OrdinalIgnoreCase)
                            ? VbaPreprocessorDirectiveKind.EndIf
                            : trimmed.StartsWith("#If", StringComparison.OrdinalIgnoreCase)
                                ? VbaPreprocessorDirectiveKind.If
                                : null;
        if (kind is null)
        {
            return false;
        }

        var startCharacter = line.Text.IndexOf(trimmed, StringComparison.Ordinal);
        directive = new VbaPreprocessorDirectiveSyntax(
            kind.Value,
            trimmed,
            new VbaSyntaxRange(
                new VbaSyntaxPosition(line.LineNumber, startCharacter, line.StartOffset + startCharacter),
                new VbaSyntaxPosition(line.LineNumber, line.Text.Length, line.EndOffset)));
        return true;
    }

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
    public void StartBranch(VbaPreprocessorDirectiveSyntax directive)
    {
        CurrentBranch = new PreprocessorBranchBuilder(directive);
        branches.Add(CurrentBranch);
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
    public SourceLine? EndLine { get; set; }

    /// <summary>
    /// Builds the branch syntax from collected lines and nested blocks.
    /// </summary>
    /// <returns>The preprocessor branch syntax.</returns>
    public VbaPreprocessorBranchSyntax Build()
    {
        var end = EndLine is null
            ? Directive.Range.End
            : new VbaSyntaxPosition(EndLine.LineNumber, EndLine.Text.Length, EndLine.EndOffset);
        return new VbaPreprocessorBranchSyntax(
            Directive,
            string.Join('\n', BodyLines),
            new VbaSyntaxRange(Directive.Range.Start, end),
            NestedBlocks);
    }
}
