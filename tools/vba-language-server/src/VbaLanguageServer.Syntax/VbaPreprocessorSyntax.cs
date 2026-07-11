namespace VbaLanguageServer.Syntax;

/// <summary>
/// Identifies a parsed VBA preprocessor directive.
/// </summary>
public enum VbaPreprocessorDirectiveKind
{
    /// <summary>
    /// A #Const directive.
    /// </summary>
    Const,

    /// <summary>
    /// A #If directive.
    /// </summary>
    If,

    /// <summary>
    /// A #ElseIf directive.
    /// </summary>
    ElseIf,

    /// <summary>
    /// A #Else directive.
    /// </summary>
    Else,

    /// <summary>
    /// A #End If directive.
    /// </summary>
    EndIf
}

/// <summary>
/// Represents one parsed preprocessor directive line.
/// </summary>
/// <param name="Kind">The directive kind.</param>
/// <param name="Text">The directive source text.</param>
/// <param name="Range">The directive source range.</param>
public sealed record VbaPreprocessorDirectiveSyntax(
    VbaPreprocessorDirectiveKind Kind,
    string Text,
    VbaSyntaxRange Range);

/// <summary>
/// Represents one branch inside a #If preprocessor block.
/// </summary>
/// <param name="Directive">The branch directive that starts this branch.</param>
/// <param name="BodyText">The source text between this branch directive and the next branch or end directive.</param>
/// <param name="Range">The branch source range.</param>
/// <param name="NestedBlocks">Nested preprocessor blocks parsed inside the branch.</param>
public sealed record VbaPreprocessorBranchSyntax(
    VbaPreprocessorDirectiveSyntax Directive,
    string BodyText,
    VbaSyntaxRange Range,
    IReadOnlyList<VbaPreprocessorBlockSyntax> NestedBlocks);

/// <summary>
/// Represents a full #If preprocessor block with branches and an optional ending directive.
/// </summary>
/// <param name="IfDirective">The opening #If directive.</param>
/// <param name="Branches">The parsed branches in source order.</param>
/// <param name="EndDirective">The closing #End If directive, when present.</param>
/// <param name="Range">The source range covered by the block.</param>
public sealed record VbaPreprocessorBlockSyntax(
    VbaPreprocessorDirectiveSyntax IfDirective,
    IReadOnlyList<VbaPreprocessorBranchSyntax> Branches,
    VbaPreprocessorDirectiveSyntax? EndDirective,
    VbaSyntaxRange Range);
