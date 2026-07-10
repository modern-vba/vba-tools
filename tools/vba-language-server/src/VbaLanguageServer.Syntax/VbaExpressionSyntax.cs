namespace VbaLanguageServer.Syntax;

public enum VbaExpressionKind
{
    AssignmentExpression,
    MemberAccess,
    ArgumentList,
    CallExpression,
    WithReceiver
}

public enum VbaCompletionContextKind
{
    Statement,
    Expression,
    MemberAccess,
    ArgumentList,
    WithReceiver
}

public sealed record VbaExpressionSyntax(
    VbaExpressionKind Kind,
    string Text,
    VbaSyntaxRange Range,
    bool IsContinued = false);

public sealed record VbaCompletionContextSyntax(
    VbaCompletionContextKind Kind,
    string Text,
    VbaSyntaxRange Range,
    bool IsContinued = false);

public enum VbaArgumentKind
{
    Positional,
    Named,
    Omitted
}

public sealed record VbaArgumentSyntax(
    VbaArgumentKind Kind,
    string Text,
    VbaSyntaxRange Range,
    string? Name = null,
    VbaSyntaxRange? NameRange = null,
    string? ValueText = null,
    VbaSyntaxRange? ValueRange = null);

public sealed record VbaArgumentListSyntax(
    string Callee,
    IReadOnlyList<VbaArgumentSyntax> Arguments,
    VbaSyntaxRange Range,
    bool IsContinued = false)
{
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
