namespace VbaLanguageServer.Syntax;

internal static class VbaPreprocessorParser
{
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

internal sealed record ParsedPreprocessor(
    IReadOnlyList<VbaPreprocessorDirectiveSyntax> Directives,
    IReadOnlyList<VbaPreprocessorBlockSyntax> Blocks,
    IReadOnlyList<VbaSyntaxDiagnostic> Diagnostics);

internal sealed class PreprocessorBlockBuilder
{
    private readonly List<PreprocessorBranchBuilder> branches = [];

    public PreprocessorBlockBuilder(VbaPreprocessorDirectiveSyntax ifDirective)
    {
        IfDirective = ifDirective;
        CurrentBranch = new PreprocessorBranchBuilder(ifDirective);
        branches.Add(CurrentBranch);
    }

    public VbaPreprocessorDirectiveSyntax IfDirective { get; }

    public PreprocessorBranchBuilder CurrentBranch { get; private set; }

    public void StartBranch(VbaPreprocessorDirectiveSyntax directive)
    {
        CurrentBranch = new PreprocessorBranchBuilder(directive);
        branches.Add(CurrentBranch);
    }

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

internal sealed class PreprocessorBranchBuilder
{
    public PreprocessorBranchBuilder(VbaPreprocessorDirectiveSyntax directive)
    {
        Directive = directive;
        EndLine = null;
    }

    public VbaPreprocessorDirectiveSyntax Directive { get; }

    public List<string> BodyLines { get; } = [];

    public List<VbaPreprocessorBlockSyntax> NestedBlocks { get; } = [];

    public SourceLine? EndLine { get; set; }

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
