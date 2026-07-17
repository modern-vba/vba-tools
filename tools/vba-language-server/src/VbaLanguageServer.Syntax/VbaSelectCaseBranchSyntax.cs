namespace VbaLanguageServer.Syntax;

/// <summary>
/// Validates the strict token shape of a Select Case branch header.
/// </summary>
internal static class VbaSelectCaseBranchSyntax
{
    public static bool TryGetCompleteKind(
        IReadOnlyList<VbaToken> tokens,
        VbaModuleKind moduleKind,
        bool allowLeadingMemberAccess,
        out VbaBlockBranchKind? branchKind)
    {
        branchKind = null;
        if (tokens.Count < 2 || !Matches(tokens[0], "Case"))
        {
            return false;
        }

        if (Matches(tokens[1], "Else"))
        {
            if (tokens.Count != 2)
            {
                return false;
            }

            branchKind = VbaBlockBranchKind.CaseElse;
            return true;
        }

        if (!HasCompleteExpressionList(
            tokens,
            start: 1,
            tokens.Count,
            moduleKind,
            allowLeadingMemberAccess))
        {
            return false;
        }

        branchKind = VbaBlockBranchKind.Case;
        return true;
    }

    private static bool HasCompleteExpressionList(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end,
        VbaModuleKind moduleKind,
        bool allowLeadingMemberAccess)
    {
        var clauseStart = start;
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
                if (parenthesisDepth < 0)
                {
                    return false;
                }

                continue;
            }

            if (parenthesisDepth == 0 && Matches(tokens[index], ","))
            {
                if (!IsCompleteClause(
                    tokens,
                    clauseStart,
                    index,
                    moduleKind,
                    allowLeadingMemberAccess))
                {
                    return false;
                }

                clauseStart = index + 1;
            }
        }

        return parenthesisDepth == 0
            && IsCompleteClause(
                tokens,
                clauseStart,
                end,
                moduleKind,
                allowLeadingMemberAccess);
    }

    private static bool IsCompleteClause(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end,
        VbaModuleKind moduleKind,
        bool allowLeadingMemberAccess)
    {
        if (start >= end)
        {
            return false;
        }

        if (TryGetComparisonExpressionStart(tokens, start, end, out var expressionStart))
        {
            return VbaExecutableExpressionSyntax.IsComplete(
                tokens,
                expressionStart,
                end,
                moduleKind,
                allowLeadingMemberAccess);
        }

        var rangeMatches = 0;
        foreach (var toIndex in FindTopLevelRangeSeparators(tokens, start, end))
        {
            if (VbaExecutableExpressionSyntax.IsComplete(
                    tokens,
                    start,
                    toIndex,
                    moduleKind,
                    allowLeadingMemberAccess)
                && VbaExecutableExpressionSyntax.IsComplete(
                    tokens,
                    toIndex + 1,
                    end,
                    moduleKind,
                    allowLeadingMemberAccess))
            {
                rangeMatches++;
            }
        }

        if (rangeMatches > 0)
        {
            return rangeMatches == 1;
        }

        return VbaExecutableExpressionSyntax.IsComplete(
            tokens,
            start,
            end,
            moduleKind,
            allowLeadingMemberAccess);
    }

    private static bool TryGetComparisonExpressionStart(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end,
        out int expressionStart)
    {
        var operatorStart = Matches(tokens[start], "Is") ? start + 1 : start;
        expressionStart = operatorStart;
        if (operatorStart >= end)
        {
            return false;
        }

        if (operatorStart + 1 < end
            && VbaExecutableExpressionSyntax.IsTwoTokenComparisonOperator(
                tokens[operatorStart],
                tokens[operatorStart + 1]))
        {
            expressionStart = operatorStart + 2;
            return true;
        }

        if (!IsComparisonOperator(tokens[operatorStart]))
        {
            return false;
        }

        expressionStart = operatorStart + 1;
        return true;
    }

    private static IReadOnlyList<int> FindTopLevelRangeSeparators(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end)
    {
        var indexes = new List<int>();
        var parenthesisDepth = 0;
        for (var index = start; index < end; index++)
        {
            if (Matches(tokens[index], "("))
            {
                parenthesisDepth++;
            }
            else if (Matches(tokens[index], ")"))
            {
                parenthesisDepth--;
                if (parenthesisDepth < 0)
                {
                    return [];
                }
            }
            else if (parenthesisDepth == 0
                && Matches(tokens[index], "To")
                && IsStructuralRangeSeparator(
                    tokens,
                    start,
                    end,
                    index))
            {
                indexes.Add(index);
            }
        }

        return parenthesisDepth == 0 ? indexes : [];
    }

    private static bool IsStructuralRangeSeparator(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end,
        int index)
    {
        if (index == start)
        {
            return true;
        }

        var previousIndex = index - 1;
        if (Matches(tokens[previousIndex], "."))
        {
            return VbaExecutableExpressionSyntax.HasTrailingDecimalPointTokenShape(
                tokens,
                previousIndex);
        }

        return !Matches(tokens[previousIndex], "!")
            || VbaExecutableExpressionSyntax.HasAdjacentNumericBangTypeCharacterTokenShape(
                tokens,
                previousIndex)
            || !VbaExecutableExpressionSyntax.HasDictionaryAccessTokenShape(
                tokens,
                previousIndex,
                end);
    }

    private static bool IsComparisonOperator(VbaToken token)
        => token.Text is "=" or "<>" or "<" or ">" or "<=" or ">=";

    private static bool Matches(VbaToken token, string text)
        => token.Text.Equals(text, StringComparison.OrdinalIgnoreCase);
}
