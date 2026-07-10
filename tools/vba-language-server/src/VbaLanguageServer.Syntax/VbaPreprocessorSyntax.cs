namespace VbaLanguageServer.Syntax;

public enum VbaPreprocessorDirectiveKind
{
    Const,
    If,
    ElseIf,
    Else,
    EndIf
}

public sealed record VbaPreprocessorDirectiveSyntax(
    VbaPreprocessorDirectiveKind Kind,
    string Text,
    VbaSyntaxRange Range);

public sealed record VbaPreprocessorBranchSyntax(
    VbaPreprocessorDirectiveSyntax Directive,
    string BodyText,
    VbaSyntaxRange Range,
    IReadOnlyList<VbaPreprocessorBlockSyntax> NestedBlocks);

public sealed record VbaPreprocessorBlockSyntax(
    VbaPreprocessorDirectiveSyntax IfDirective,
    IReadOnlyList<VbaPreprocessorBranchSyntax> Branches,
    VbaPreprocessorDirectiveSyntax? EndDirective,
    VbaSyntaxRange Range);
