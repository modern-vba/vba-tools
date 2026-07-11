namespace VbaLanguageServer.Syntax;

/// <summary>
/// Identifies the expression shape parsed from a VBA statement.
/// </summary>
public enum VbaExpressionKind
{
    /// <summary>
    /// An assignment expression.
    /// </summary>
    AssignmentExpression,

    /// <summary>
    /// A member access or qualified reference expression.
    /// </summary>
    MemberAccess,

    /// <summary>
    /// A callable argument list expression.
    /// </summary>
    ArgumentList,

    /// <summary>
    /// A call expression.
    /// </summary>
    CallExpression,

    /// <summary>
    /// A With statement receiver expression.
    /// </summary>
    WithReceiver
}

/// <summary>
/// Identifies the syntactic context used when computing completion candidates.
/// </summary>
public enum VbaCompletionContextKind
{
    /// <summary>
    /// Completion is requested at statement level.
    /// </summary>
    Statement,

    /// <summary>
    /// Completion is requested inside a general expression.
    /// </summary>
    Expression,

    /// <summary>
    /// Completion is requested after a member-access separator.
    /// </summary>
    MemberAccess,

    /// <summary>
    /// Completion is requested inside a callable argument list.
    /// </summary>
    ArgumentList,

    /// <summary>
    /// Completion is requested inside a With receiver expression.
    /// </summary>
    WithReceiver
}

/// <summary>
/// Represents a parsed expression fragment with source range information.
/// </summary>
/// <param name="Kind">The expression kind.</param>
/// <param name="Text">The source text for the expression fragment.</param>
/// <param name="Range">The source range covered by the fragment.</param>
/// <param name="IsContinued">Whether the expression spans physical lines with continuation markers.</param>
public sealed record VbaExpressionSyntax(
    VbaExpressionKind Kind,
    string Text,
    VbaSyntaxRange Range,
    bool IsContinued = false);

/// <summary>
/// Represents the syntactic completion context at a requested source position.
/// </summary>
/// <param name="Kind">The completion context kind.</param>
/// <param name="Text">The source text that forms the completion prefix or receiver.</param>
/// <param name="Range">The source range covered by the context.</param>
/// <param name="IsContinued">Whether the context is part of a continued expression.</param>
public sealed record VbaCompletionContextSyntax(
    VbaCompletionContextKind Kind,
    string Text,
    VbaSyntaxRange Range,
    bool IsContinued = false);

/// <summary>
/// Identifies the argument shape at a VBA call site.
/// </summary>
public enum VbaArgumentKind
{
    /// <summary>
    /// An argument matched to a callable parameter by ordinal position.
    /// </summary>
    Positional,

    /// <summary>
    /// An argument that explicitly names the target callable parameter.
    /// </summary>
    Named,

    /// <summary>
    /// An empty positional argument slot.
    /// </summary>
    Omitted
}

/// <summary>
/// Represents one call-site argument.
/// </summary>
/// <param name="Kind">The argument kind.</param>
/// <param name="Text">The full argument source text.</param>
/// <param name="Range">The full argument source range.</param>
/// <param name="Name">The named argument target name, when present.</param>
/// <param name="NameRange">The source range of the named argument target.</param>
/// <param name="ValueText">The argument value text, excluding the named target.</param>
/// <param name="ValueRange">The source range of the argument value.</param>
public sealed record VbaArgumentSyntax(
    VbaArgumentKind Kind,
    string Text,
    VbaSyntaxRange Range,
    string? Name = null,
    VbaSyntaxRange? NameRange = null,
    string? ValueText = null,
    VbaSyntaxRange? ValueRange = null);

/// <summary>
/// Represents a parenthesized or statement-form argument list at a call site.
/// </summary>
/// <param name="Callee">The callable expression text preceding the arguments.</param>
/// <param name="Arguments">The ordered call arguments.</param>
/// <param name="Range">The source range covered by the argument list.</param>
/// <param name="IsContinued">Whether the argument list spans physical lines with continuation markers.</param>
public sealed record VbaArgumentListSyntax(
    string Callee,
    IReadOnlyList<VbaArgumentSyntax> Arguments,
    VbaSyntaxRange Range,
    bool IsContinued = false)
{
    /// <summary>
    /// Finds the active argument index for a source position inside the argument list.
    /// </summary>
    /// <param name="position">The source position to evaluate.</param>
    /// <returns>The zero-based active argument index.</returns>
    public int GetActiveArgumentIndex(VbaSyntaxPosition position)
    {
        if (Arguments.Count == 0)
        {
            return 0;
        }

        for (var index = Arguments.Count - 1; index >= 0; index--)
        {
            if (position.Offset >= Arguments[index].Range.Start.Offset)
            {
                return index;
            }
        }

        return 0;
    }
}
