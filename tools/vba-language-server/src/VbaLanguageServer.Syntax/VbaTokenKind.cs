namespace VbaLanguageServer.Syntax;

/// <summary>
/// Identifies a lexical token category in VBA source text.
/// </summary>
public enum VbaTokenKind
{
    /// <summary>
    /// A VBA keyword, intrinsic type, literal keyword, or other vocabulary token.
    /// </summary>
    Keyword,

    /// <summary>
    /// An identifier token.
    /// </summary>
    Identifier,

    /// <summary>
    /// A quoted string literal.
    /// </summary>
    StringLiteral,

    /// <summary>
    /// A #delimited date literal.
    /// </summary>
    DateLiteral,

    /// <summary>
    /// A numeric literal.
    /// </summary>
    NumericLiteral,

    /// <summary>
    /// An operator token.
    /// </summary>
    Operator,

    /// <summary>
    /// A punctuation token.
    /// </summary>
    Punctuation,

    /// <summary>
    /// An apostrophe or Rem comment token.
    /// </summary>
    Comment,

    /// <summary>
    /// Whitespace that is not a newline.
    /// </summary>
    Whitespace,

    /// <summary>
    /// A newline token.
    /// </summary>
    NewLine,

    /// <summary>
    /// A VBA line-continuation token.
    /// </summary>
    LineContinuation,

    /// <summary>
    /// A preprocessor directive token.
    /// </summary>
    PreprocessorDirective
}
