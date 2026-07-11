namespace VbaLanguageServer.Syntax;

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
