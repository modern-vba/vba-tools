namespace VbaLanguageServer.Syntax;

public enum VbaTokenKind
{
    Keyword,
    Identifier,
    StringLiteral,
    DateLiteral,
    NumericLiteral,
    Operator,
    Punctuation,
    Comment,
    Whitespace,
    NewLine,
    LineContinuation,
    PreprocessorDirective
}
