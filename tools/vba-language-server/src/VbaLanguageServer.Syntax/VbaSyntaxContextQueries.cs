using System.Text.RegularExpressions;

namespace VbaLanguageServer.Syntax;

/// <summary>
/// Represents a parsed member-chain context at an editor position.
/// </summary>
public sealed record VbaMemberChainContext(
    string ReceiverExpression,
    string? MemberName,
    string NormalizedExpression,
    IReadOnlyList<string> Segments,
    bool IsWithReceiver);

/// <summary>
/// Represents a parsed call-site context used by signature help.
/// </summary>
public sealed record VbaCallExpressionContext(
    string Callee,
    string Arguments,
    VbaMemberChainContext? MemberChain,
    string? Qualifier,
    string UnqualifiedName);

internal static class VbaSyntaxContextQueries
{
    public static bool TryGetCompletionContext(
        VbaSyntaxTree tree,
        int line,
        int character,
        out VbaMemberChainContext context)
    {
        context = default!;
        if (!TryGetLogicalPrefix(tree, line, character, out var logicalPrefix)
            || !VbaMemberExpressionSyntax.TryGetMemberReceiverExpression(logicalPrefix, out var receiverExpression))
        {
            return false;
        }

        context = CreateContext(receiverExpression, memberName: null);
        return true;
    }

    public static bool IsCompletedMemberAccessWithTrailingWhitespace(
        VbaSyntaxTree tree,
        int line,
        int character)
        => TryGetLogicalPrefix(tree, line, character, out var logicalPrefix)
            && VbaMemberExpressionSyntax.IsCompletedMemberAccessWithTrailingWhitespace(logicalPrefix);

    public static bool TryGetMemberReferenceContext(
        VbaSyntaxTree tree,
        int line,
        int character,
        string memberName,
        out VbaMemberChainContext context)
    {
        context = default!;
        if (!TryGetLogicalPrefix(tree, line, character, out var logicalPrefix)
            || !VbaMemberExpressionSyntax.TryGetMemberReceiverExpression(logicalPrefix, out var receiverExpression))
        {
            return false;
        }

        context = CreateContext(receiverExpression, memberName);
        return true;
    }

    public static bool TryGetPreviousMemberContext(
        string codePart,
        VbaIdentifierOccurrence occurrence,
        out VbaMemberChainContext context)
    {
        context = default!;
        if (!VbaMemberExpressionSyntax.TryGetPreviousMemberReceiverExpression(
            codePart,
            occurrence,
            out var receiverExpression))
        {
            return false;
        }

        context = CreateContext(receiverExpression, occurrence.Name);
        return true;
    }

    public static bool TryGetCallExpressionContext(
        VbaSyntaxTree tree,
        int line,
        int character,
        out VbaCallExpressionContext context)
    {
        context = default!;
        return TryGetLogicalPrefix(tree, line, character, out var logicalPrefix)
            && (TryGetParenthesizedCallContext(logicalPrefix, out context)
                || TryGetStatementFormCallContext(logicalPrefix, out context));
    }

    private static bool TryGetParenthesizedCallContext(
        string logicalPrefix,
        out VbaCallExpressionContext context)
    {
        context = default!;
        var callMatch = Regex.Matches(
                logicalPrefix,
                "(?<callee>(?:\\.|[A-Za-z_][A-Za-z0-9_]*)(?:\\s*\\.\\s*[A-Za-z_][A-Za-z0-9_]*)*)\\s*\\((?<arguments>[^()]*)$",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .LastOrDefault();
        return callMatch is not null && TryCreateCallContext(callMatch, out context);
    }

    private static bool TryGetStatementFormCallContext(
        string logicalPrefix,
        out VbaCallExpressionContext context)
    {
        context = default!;
        var callMatch = Regex.Match(
            logicalPrefix,
            "^\\s*(?<callee>[A-Za-z_][A-Za-z0-9_]*(?:\\s*\\.\\s*[A-Za-z_][A-Za-z0-9_]*)*)\\s+(?<arguments>.*)$",
            RegexOptions.CultureInvariant);
        if (!callMatch.Success)
        {
            return false;
        }

        var trimmedArguments = callMatch.Groups["arguments"].Value.TrimStart();
        return !trimmedArguments.StartsWith("=", StringComparison.Ordinal)
            && !trimmedArguments.StartsWith("(", StringComparison.Ordinal)
            && TryCreateCallContext(callMatch, out context);
    }

    private static bool TryCreateCallContext(Match callMatch, out VbaCallExpressionContext context)
    {
        var arguments = callMatch.Groups["arguments"].Value;
        var callee = VbaMemberExpressionSyntax.NormalizeMemberExpression(callMatch.Groups["callee"].Value);
        var memberChain = VbaMemberExpressionSyntax.TrySplitMemberExpression(
            callee,
            out var receiverExpression,
            out var memberName)
                ? CreateContext(receiverExpression, memberName)
                : null;
        var qualifier = VbaMemberExpressionSyntax.GetQualifierFromCallee(callee);
        var unqualifiedName = qualifier is null ? callee : callee[(qualifier.Length + 1)..];
        context = new VbaCallExpressionContext(callee, arguments, memberChain, qualifier, unqualifiedName);
        return true;
    }

    private static bool TryGetLogicalPrefix(
        VbaSyntaxTree tree,
        int line,
        int character,
        out string logicalPrefix)
    {
        var lines = VbaSourceText.SplitLines(tree.Text);
        logicalPrefix = "";
        if (line < 0 || line >= lines.Length)
        {
            return false;
        }

        logicalPrefix = VbaSourceText.GetLogicalPrefix(lines, line, character);
        return true;
    }

    private static VbaMemberChainContext CreateContext(string receiverExpression, string? memberName)
    {
        var normalized = VbaMemberExpressionSyntax.NormalizeMemberExpression(
            memberName is null ? receiverExpression : $"{receiverExpression}.{memberName}");
        return new VbaMemberChainContext(
            receiverExpression,
            memberName,
            normalized,
            normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            receiverExpression.Trim().Equals(".", StringComparison.Ordinal));
    }
}
