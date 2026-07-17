namespace VbaLanguageServer.Syntax;

internal enum VbaBoundVariableRole
{
    NumericCounter,
    CollectionElement
}

/// <summary>
/// Validates the strict token shape of a syntactic VBA bound-variable expression.
/// Semantic classification remains the responsibility of later binding stages.
/// </summary>
internal static class VbaBoundVariableExpressionSyntax
{
    public static bool IsComplete(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end,
        VbaModuleKind moduleKind,
        bool allowLeadingMemberAccess,
        VbaBoundVariableRole role)
    {
        if (start < 0
            || end > tokens.Count
            || start >= end
            || !CanStartBoundVariable(
                tokens[start],
                moduleKind,
                allowLeadingMemberAccess)
            || IsBareMe(tokens, start, end)
            || HasEmptyInvocation(tokens, start, end)
            || HasTopLevelValueOperator(tokens, start, end)
            || HasDisallowedExplicitTypeCharacter(tokens, start, end, role))
        {
            return false;
        }

        return VbaExecutableExpressionSyntax.IsComplete(
            tokens,
            start,
            end,
            moduleKind,
            allowLeadingMemberAccess);
    }

    private static bool CanStartBoundVariable(
        VbaToken token,
        VbaModuleKind moduleKind,
        bool allowLeadingMemberAccess)
        => VbaIdentifierSyntaxFacts.IsValidDeclaredName(token)
            || Matches(token, "Me")
                && moduleKind is VbaModuleKind.ClassModule or VbaModuleKind.FormModule
            || allowLeadingMemberAccess
                && (Matches(token, ".") || Matches(token, "!"));

    private static bool IsBareMe(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end)
        => end == start + 1 && Matches(tokens[start], "Me");

    private static bool HasEmptyInvocation(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end)
    {
        var parenthesisDepth = 0;
        for (var index = start; index + 1 < end; index++)
        {
            if (Matches(tokens[index], "("))
            {
                if (parenthesisDepth == 0 && Matches(tokens[index + 1], ")"))
                {
                    return true;
                }

                parenthesisDepth++;
            }
            else if (Matches(tokens[index], ")"))
            {
                parenthesisDepth--;
            }
        }

        return false;
    }

    private static bool HasDisallowedExplicitTypeCharacter(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end,
        VbaBoundVariableRole role)
    {
        var parenthesisDepth = 0;
        for (var index = start; index < end; index++)
        {
            if (Matches(tokens[index], "("))
            {
                parenthesisDepth++;
                continue;
            }

            if (Matches(tokens[index], ")"))
            {
                parenthesisDepth--;
                continue;
            }

            if (parenthesisDepth == 0
                && IsIdentifierTypeCharacter(tokens[index])
                && index > start
                && tokens[index - 1].Range.End.Offset == tokens[index].Range.Start.Offset
                && !VbaExecutableExpressionSyntax.HasDictionaryAccessTokenShape(
                    tokens,
                    index,
                    end)
                && (role == VbaBoundVariableRole.CollectionElement
                    || Matches(tokens[index], "$")))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasTopLevelValueOperator(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end)
    {
        var parenthesisDepth = 0;
        for (var index = start; index < end; index++)
        {
            var token = tokens[index];
            if (Matches(token, "("))
            {
                parenthesisDepth++;
                continue;
            }

            if (Matches(token, ")"))
            {
                parenthesisDepth--;
                if (parenthesisDepth < 0)
                {
                    return true;
                }

                continue;
            }

            if (parenthesisDepth == 0
                && IsValueOperator(token)
                && !IsMemberNameAfterAccess(tokens, start, index)
                && !IsAdjacentScalarTypeCharacter(tokens, start, end, index))
            {
                return true;
            }
        }

        return parenthesisDepth != 0;
    }

    private static bool IsValueOperator(VbaToken token)
        => token.Text.ToUpperInvariant() is "="
            or "<>"
            or "<"
            or ">"
            or "<="
            or ">="
            or "LIKE"
            or "IS"
            or "&"
            or "+"
            or "-"
            or "MOD"
            or "\\"
            or "*"
            or "/"
            or "^"
            or "AND"
            or "OR"
            or "XOR"
            or "EQV"
            or "IMP";

    private static bool IsIdentifierTypeCharacter(VbaToken token)
        => token.Text is "$" or "%" or "&" or "^" or "!" or "#" or "@";

    private static bool IsMemberNameAfterAccess(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int index)
        => index > start
            && (Matches(tokens[index - 1], ".") || Matches(tokens[index - 1], "!"));

    private static bool IsAdjacentScalarTypeCharacter(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end,
        int index)
        => (Matches(tokens[index], "&") || Matches(tokens[index], "^"))
            && index > start
            && tokens[index - 1].Range.End.Offset == tokens[index].Range.Start.Offset
            && (index == end - 1 || Matches(tokens[index + 1], "("));

    private static bool Matches(VbaToken token, string text)
        => token.Text.Equals(text, StringComparison.OrdinalIgnoreCase);
}
