using System.Text.RegularExpressions;

namespace VbaLanguageServer.Syntax;

/// <summary>
/// Owns lightweight member-expression syntax queries used by editor features.
/// </summary>
public static class VbaMemberExpressionSyntax
{
    public static bool TryGetMemberReceiverExpression(string logicalPrefix, out string receiverExpression)
    {
        receiverExpression = "";
        var trimmed = logicalPrefix;
        if (string.IsNullOrEmpty(trimmed) || char.IsWhiteSpace(trimmed[^1]))
        {
            return false;
        }

        var partialMatch = Regex.Match(trimmed, "[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
        if (partialMatch.Success)
        {
            trimmed = trimmed[..partialMatch.Index].TrimEnd();
        }

        if (!trimmed.EndsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        var beforeDot = trimmed[..^1].TrimEnd();
        if (string.IsNullOrWhiteSpace(beforeDot))
        {
            receiverExpression = ".";
            return true;
        }

        var match = Regex.Match(
            beforeDot,
            "(?<expression>(?:\\.|[A-Za-z_][A-Za-z0-9_]*)(?:\\s*\\.\\s*[A-Za-z_][A-Za-z0-9_]*)*)$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        receiverExpression = match.Groups["expression"].Value;
        return true;
    }

    public static bool IsCompletedMemberAccessWithTrailingWhitespace(string logicalPrefix)
    {
        if (string.IsNullOrEmpty(logicalPrefix) || !char.IsWhiteSpace(logicalPrefix[^1]))
        {
            return false;
        }

        var trimmed = logicalPrefix.TrimEnd();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        if (trimmed.EndsWith(".", StringComparison.Ordinal))
        {
            return TryGetMemberReceiverExpression(trimmed, out _);
        }

        return Regex.IsMatch(
            trimmed,
            "(?:\\.|[A-Za-z_][A-Za-z0-9_]*)(?:\\s*\\.\\s*[A-Za-z_][A-Za-z0-9_]*)+$",
            RegexOptions.CultureInvariant);
    }

    public static bool TrySplitMemberExpression(
        string expression,
        out string receiverExpression,
        out string memberName)
    {
        receiverExpression = "";
        memberName = "";
        var normalized = NormalizeMemberExpression(expression);
        var lastDot = normalized.LastIndexOf('.');
        if (lastDot < 0)
        {
            return false;
        }

        receiverExpression = lastDot == 0 ? "." : normalized[..lastDot];
        memberName = normalized[(lastDot + 1)..];
        return !string.IsNullOrWhiteSpace(memberName);
    }

    public static bool TryGetPreviousMemberReceiverExpression(
        string codePart,
        VbaIdentifierOccurrence occurrence,
        out string receiverExpression)
    {
        receiverExpression = "";
        var dotIndex = occurrence.Start - 1;
        while (dotIndex >= 0 && char.IsWhiteSpace(codePart[dotIndex]))
        {
            dotIndex--;
        }

        if (dotIndex < 0 || codePart[dotIndex] != '.')
        {
            return false;
        }

        receiverExpression = codePart[..dotIndex].Trim();
        return !string.IsNullOrWhiteSpace(receiverExpression);
    }

    public static bool IsQualifiedIdentifierOccurrence(string codePart, VbaIdentifierOccurrence occurrence)
        => TryGetPreviousQualifier(codePart, occurrence, out _)
            || TryGetNextMember(codePart, occurrence, out _);

    public static bool TryGetPreviousQualifier(
        string codePart,
        VbaIdentifierOccurrence occurrence,
        out VbaIdentifierOccurrence qualifier)
    {
        qualifier = new VbaIdentifierOccurrence("", 0, 0);
        var dotIndex = occurrence.Start - 1;
        while (dotIndex >= 0 && char.IsWhiteSpace(codePart[dotIndex]))
        {
            dotIndex--;
        }

        if (dotIndex < 0 || codePart[dotIndex] != '.')
        {
            return false;
        }

        var qualifierEnd = dotIndex - 1;
        while (qualifierEnd >= 0 && char.IsWhiteSpace(codePart[qualifierEnd]))
        {
            qualifierEnd--;
        }

        if (qualifierEnd < 0 || !VbaSourceText.IsIdentifierCharacter(codePart[qualifierEnd]))
        {
            return false;
        }

        var qualifierStart = qualifierEnd;
        while (qualifierStart > 0 && VbaSourceText.IsIdentifierCharacter(codePart[qualifierStart - 1]))
        {
            qualifierStart--;
        }

        qualifier = new VbaIdentifierOccurrence(
            codePart[qualifierStart..(qualifierEnd + 1)],
            qualifierStart,
            qualifierEnd + 1);
        return true;
    }

    public static bool TryGetNextMember(
        string codePart,
        VbaIdentifierOccurrence occurrence,
        out VbaIdentifierOccurrence member)
    {
        member = new VbaIdentifierOccurrence("", 0, 0);
        var dotIndex = occurrence.End;
        while (dotIndex < codePart.Length && char.IsWhiteSpace(codePart[dotIndex]))
        {
            dotIndex++;
        }

        if (dotIndex >= codePart.Length || codePart[dotIndex] != '.')
        {
            return false;
        }

        var memberStart = dotIndex + 1;
        while (memberStart < codePart.Length && char.IsWhiteSpace(codePart[memberStart]))
        {
            memberStart++;
        }

        if (memberStart >= codePart.Length || !VbaSourceText.IsIdentifierStart(codePart[memberStart]))
        {
            return false;
        }

        var memberEnd = memberStart + 1;
        while (memberEnd < codePart.Length && VbaSourceText.IsIdentifierCharacter(codePart[memberEnd]))
        {
            memberEnd++;
        }

        member = new VbaIdentifierOccurrence(codePart[memberStart..memberEnd], memberStart, memberEnd);
        return true;
    }

    public static string? GetQualifierFromCallee(string callee)
    {
        var normalized = NormalizeMemberExpression(callee);
        var lastDot = normalized.LastIndexOf('.');
        return lastDot < 0 ? null : normalized[..lastDot];
    }

    public static string NormalizeMemberExpression(string expression)
        => Regex.Replace(expression, "\\s+", "", RegexOptions.CultureInvariant);
}
