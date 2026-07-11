namespace VbaLanguageServer.Syntax;

/// <summary>
/// Identifies the statement or block shape parsed from VBA source.
/// </summary>
public enum VbaStatementKind
{
    /// <summary>
    /// A callable body block.
    /// </summary>
    ProcedureBody,

    /// <summary>
    /// An If...End If block.
    /// </summary>
    IfBlock,

    /// <summary>
    /// A With...End With block.
    /// </summary>
    WithBlock,

    /// <summary>
    /// A Select...End Select block.
    /// </summary>
    SelectBlock,

    /// <summary>
    /// A For...Next or For Each...Next block.
    /// </summary>
    ForBlock,

    /// <summary>
    /// A Do...Loop block.
    /// </summary>
    DoLoopBlock,

    /// <summary>
    /// An assignment statement.
    /// </summary>
    Assignment,

    /// <summary>
    /// A call statement.
    /// </summary>
    Call,

    /// <summary>
    /// A statement that the parser recognized as malformed.
    /// </summary>
    Malformed,

    /// <summary>
    /// A statement shape outside the parser's current structured model.
    /// </summary>
    Unknown
}

/// <summary>
/// Represents a parsed statement or block with source range information.
/// </summary>
/// <param name="Kind">The statement kind.</param>
/// <param name="Text">The source text for the statement.</param>
/// <param name="Range">The source range covered by the statement.</param>
/// <param name="IsMalformed">Whether parser recovery marked the statement as malformed.</param>
public sealed record VbaStatementSyntax(
    VbaStatementKind Kind,
    string Text,
    VbaSyntaxRange Range,
    bool IsMalformed = false);
