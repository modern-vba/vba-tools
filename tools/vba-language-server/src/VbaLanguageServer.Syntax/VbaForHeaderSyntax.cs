namespace VbaLanguageServer.Syntax;

/// <summary>
/// Validates complete strict For and For Each header token shapes.
/// Name binding, declared-type compatibility, and callable arity remain outside
/// this syntax-only planner path.
/// </summary>
internal static class VbaForHeaderSyntax
{
    public static bool TryGetCompleteKind(
        VbaSyntaxTree tree,
        VbaLogicalStatementSpan statement,
        bool allowLeadingMemberAccess,
        out VbaBlockHeaderKind kind)
    {
        var tokens = statement.SignificantTokens;
        kind = default;
        if (tokens.Count < 5 || !Matches(tokens[0], "For"))
        {
            return false;
        }

        if (Matches(tokens[1], "Each"))
        {
            if (FindCompleteForEachSplits(
                tokens,
                tree.Module.Kind,
                allowLeadingMemberAccess) != 1)
            {
                return false;
            }

            kind = VbaBlockHeaderKind.ForEach;
            return true;
        }

        if (FindCompleteForSplits(
            tokens,
            tree.Module.Kind,
            allowLeadingMemberAccess) != 1)
        {
            return false;
        }

        kind = VbaBlockHeaderKind.For;
        return true;
    }

    private static int FindCompleteForEachSplits(
        IReadOnlyList<VbaToken> tokens,
        VbaModuleKind moduleKind,
        bool allowLeadingMemberAccess)
    {
        var matches = 0;
        foreach (var inIndex in FindTopLevelSeparators(
            tokens,
            2,
            tokens.Count,
            "In"))
        {
            if (VbaBoundVariableExpressionSyntax.IsComplete(
                    tokens,
                    2,
                    inIndex,
                    moduleKind,
                    allowLeadingMemberAccess,
                    VbaBoundVariableRole.CollectionElement)
                && VbaPotentialReferenceExpressionSyntax.IsCompleteForEachGroup(
                    tokens,
                    inIndex + 1,
                    tokens.Count,
                    moduleKind,
                    allowLeadingMemberAccess))
            {
                matches++;
            }
        }

        return matches;
    }

    private static int FindCompleteForSplits(
        IReadOnlyList<VbaToken> tokens,
        VbaModuleKind moduleKind,
        bool allowLeadingMemberAccess)
    {
        var matches = 0;
        foreach (var assignmentIndex in FindTopLevelSeparators(
            tokens,
            1,
            tokens.Count,
            "="))
        {
            if (!VbaBoundVariableExpressionSyntax.IsComplete(
                tokens,
                1,
                assignmentIndex,
                moduleKind,
                allowLeadingMemberAccess,
                VbaBoundVariableRole.NumericCounter))
            {
                continue;
            }

            foreach (var toIndex in FindTopLevelSeparators(
                tokens,
                assignmentIndex + 1,
                tokens.Count,
                "To"))
            {
                if (!VbaExecutableExpressionSyntax.IsComplete(
                    tokens,
                    assignmentIndex + 1,
                    toIndex,
                    moduleKind,
                    allowLeadingMemberAccess))
                {
                    continue;
                }

                var stepIndexes = FindTopLevelSeparators(
                    tokens,
                    toIndex + 1,
                    tokens.Count,
                    "Step");
                if (stepIndexes.Count == 0)
                {
                    if (VbaExecutableExpressionSyntax.IsComplete(
                        tokens,
                        toIndex + 1,
                        tokens.Count,
                        moduleKind,
                        allowLeadingMemberAccess))
                    {
                        matches++;
                    }

                    continue;
                }

                foreach (var stepIndex in stepIndexes)
                {
                    if (VbaExecutableExpressionSyntax.IsComplete(
                            tokens,
                            toIndex + 1,
                            stepIndex,
                            moduleKind,
                            allowLeadingMemberAccess)
                        && VbaExecutableExpressionSyntax.IsComplete(
                            tokens,
                            stepIndex + 1,
                            tokens.Count,
                            moduleKind,
                            allowLeadingMemberAccess))
                    {
                        matches++;
                    }
                }
            }
        }

        return matches;
    }

    private static IReadOnlyList<int> FindTopLevelSeparators(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end,
        string text)
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
                && Matches(tokens[index], text)
                && IsStructuralSeparator(tokens, start, end, index))
            {
                indexes.Add(index);
            }
        }

        return parenthesisDepth == 0 ? indexes : [];
    }

    private static bool IsStructuralSeparator(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end,
        int index)
        => index == start
            || (!Matches(tokens[index - 1], ".")
                    || VbaExecutableExpressionSyntax.HasTrailingDecimalPointTokenShape(
                        tokens,
                        index - 1))
                && !(Matches(tokens[index - 1], "!")
                    && VbaExecutableExpressionSyntax.HasDictionaryAccessTokenShape(
                        tokens,
                        index - 1,
                        end));

    private static bool Matches(VbaToken token, string text)
        => token.Text.Equals(text, StringComparison.OrdinalIgnoreCase);
}
