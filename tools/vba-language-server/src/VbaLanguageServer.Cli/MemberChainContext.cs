using System.Text.RegularExpressions;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Represents a parsed member-chain expression context at an editor position.
/// </summary>
/// <param name="ReceiverExpression">The receiver expression before the target member or completion dot.</param>
/// <param name="MemberName">The target member name, when the context targets a known member.</param>
/// <param name="NormalizedExpression">The whitespace-free chain expression.</param>
/// <param name="Segments">The normalized expression segments.</param>
/// <param name="IsWithReceiver">Whether the receiver is the implicit leading-dot WithReceiver.</param>
internal sealed record VbaMemberChainContext(
    string ReceiverExpression,
    string? MemberName,
    string NormalizedExpression,
    IReadOnlyList<string> Segments,
    bool IsWithReceiver);

/// <summary>
/// Represents a call expression context used by signature help.
/// </summary>
/// <param name="Callee">The normalized callable expression.</param>
/// <param name="Arguments">The argument text after the opening parenthesis.</param>
/// <param name="MemberChain">The member-chain context when the callable is a member expression.</param>
/// <param name="Qualifier">The optional qualifier for a non-member-chain callee.</param>
/// <param name="UnqualifiedName">The callable name without the qualifier.</param>
internal sealed record VbaCallExpressionContext(
    string Callee,
    string Arguments,
    VbaMemberChainContext? MemberChain,
    string? Qualifier,
    string UnqualifiedName);

/// <summary>
/// Extracts source-text member-chain contexts for semantic features.
/// </summary>
internal sealed class VbaMemberChainContextProvider
{
    public bool TryGetCompletionContext(
        VbaSourceDocument document,
        int line,
        int character,
        out VbaMemberChainContext context)
    {
        context = default!;
        var lines = VbaSourceText.SplitLines(document.Text);
        if (!TryGetLogicalPrefix(lines, line, character, out var logicalPrefix)
            || !VbaMemberExpressionSyntax.TryGetMemberReceiverExpression(logicalPrefix, out var receiverExpression))
        {
            return false;
        }

        context = CreateContext(receiverExpression, memberName: null);
        return true;
    }

    public bool IsCompletedMemberAccessWithTrailingWhitespace(
        VbaSourceDocument document,
        int line,
        int character)
    {
        var lines = VbaSourceText.SplitLines(document.Text);
        return TryGetLogicalPrefix(lines, line, character, out var logicalPrefix)
            && VbaMemberExpressionSyntax.IsCompletedMemberAccessWithTrailingWhitespace(logicalPrefix);
    }

    public bool TryGetMemberReferenceContext(
        VbaSourceDocument document,
        int line,
        int character,
        string memberName,
        out VbaMemberChainContext context)
    {
        context = default!;
        var lines = VbaSourceText.SplitLines(document.Text);
        if (!TryGetLogicalPrefix(lines, line, character, out var logicalPrefix)
            || !VbaMemberExpressionSyntax.TryGetMemberReceiverExpression(logicalPrefix, out var receiverExpression))
        {
            return false;
        }

        context = CreateContext(receiverExpression, memberName);
        return true;
    }

    public bool TryGetPreviousMemberContext(
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

    public bool TryGetCallExpressionContext(string logicalPrefix, out VbaCallExpressionContext context)
    {
        context = default!;
        var callMatch = Regex.Matches(
                logicalPrefix,
                "(?<callee>(?:\\.|[A-Za-z_][A-Za-z0-9_]*)(?:\\s*\\.\\s*[A-Za-z_][A-Za-z0-9_]*)*)\\s*\\((?<arguments>[^()]*)$",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .LastOrDefault();
        if (callMatch is null)
        {
            return false;
        }

        var arguments = callMatch.Groups["arguments"].Value;
        var callee = VbaMemberExpressionSyntax.NormalizeMemberExpression(callMatch.Groups["callee"].Value);
        var memberChain = VbaMemberExpressionSyntax.TrySplitMemberExpression(callee, out var receiverExpression, out var memberName)
            ? CreateContext(receiverExpression, memberName)
            : null;
        var qualifier = VbaMemberExpressionSyntax.GetQualifierFromCallee(callee);
        var unqualifiedName = qualifier is null ? callee : callee[(qualifier.Length + 1)..];
        context = new VbaCallExpressionContext(callee, arguments, memberChain, qualifier, unqualifiedName);
        return true;
    }

    public bool TryGetStatementFormCallContext(string logicalPrefix, out VbaCallExpressionContext context)
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

        var arguments = callMatch.Groups["arguments"].Value;
        var trimmedArguments = arguments.TrimStart();
        if (trimmedArguments.StartsWith("=", StringComparison.Ordinal)
            || trimmedArguments.StartsWith("(", StringComparison.Ordinal))
        {
            return false;
        }

        var callee = VbaMemberExpressionSyntax.NormalizeMemberExpression(callMatch.Groups["callee"].Value);
        var memberChain = VbaMemberExpressionSyntax.TrySplitMemberExpression(callee, out var receiverExpression, out var memberName)
            ? CreateContext(receiverExpression, memberName)
            : null;
        var qualifier = VbaMemberExpressionSyntax.GetQualifierFromCallee(callee);
        var unqualifiedName = qualifier is null ? callee : callee[(qualifier.Length + 1)..];
        context = new VbaCallExpressionContext(callee, arguments, memberChain, qualifier, unqualifiedName);
        return true;
    }

    private static bool TryGetLogicalPrefix(
        string[] lines,
        int line,
        int character,
        out string logicalPrefix)
    {
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
