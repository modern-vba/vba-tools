namespace VbaLanguageServer.Syntax;

public enum VbaStatementKind
{
    ProcedureBody,
    IfBlock,
    WithBlock,
    SelectBlock,
    ForBlock,
    DoLoopBlock,
    Assignment,
    Call,
    Malformed,
    Unknown
}

public sealed record VbaStatementSyntax(
    VbaStatementKind Kind,
    string Text,
    VbaSyntaxRange Range,
    bool IsMalformed = false);
