namespace VbaLanguageServer.Syntax;

/// <summary>
/// Validates a complete expression whose visible root can denote an object or array reference.
/// Top-level operator type propagation is deliberately outside this strict,
/// fail-closed syntax subset.
/// </summary>
internal static class VbaPotentialReferenceExpressionSyntax
{
    private static readonly IReadOnlySet<string> NonValueSpecialMemberNames =
        new HashSet<string>(
            ["Circle", "Print", "PSet", "Scale"],
            StringComparer.OrdinalIgnoreCase);

    public static bool IsComplete(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end,
        VbaModuleKind moduleKind,
        bool allowLeadingMemberAccess)
        => VbaExecutableExpressionSyntax.IsComplete(
                tokens,
                start,
                end,
                moduleKind,
                allowLeadingMemberAccess)
            && HasPotentialReferenceShape(
                tokens,
                start,
                end,
                rejectKnownNonReceiverStandardLibraryPath: true,
                rejectEmptyOrNull: false);

    public static bool IsCompleteForEachGroup(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end,
        VbaModuleKind moduleKind,
        bool allowLeadingMemberAccess)
        => VbaExecutableExpressionSyntax.IsComplete(
                tokens,
                start,
                end,
                moduleKind,
                allowLeadingMemberAccess)
            && HasPotentialReferenceShape(
                tokens,
                start,
                end,
                rejectKnownNonReceiverStandardLibraryPath: false,
                rejectEmptyOrNull: true);

    private static bool HasPotentialReferenceShape(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end,
        bool rejectKnownNonReceiverStandardLibraryPath,
        bool rejectEmptyOrNull)
    {
        while (IsWrappedInParentheses(tokens, start, end))
        {
            start++;
            end--;
        }

        if (start >= end)
        {
            return false;
        }

        var first = tokens[start];
        if (first.Kind is VbaTokenKind.StringLiteral
            or VbaTokenKind.DateLiteral
            or VbaTokenKind.NumericLiteral
            || Matches(first, "&")
            || Matches(first, "TypeOf")
            || Matches(first, "Not")
            || Matches(first, "-")
            || Matches(first, "True")
            || Matches(first, "False")
            || rejectEmptyOrNull
                && (Matches(first, "Empty") || Matches(first, "Null"))
            || NonValueSpecialMemberNames.Contains(first.Text)
            || rejectKnownNonReceiverStandardLibraryPath
                && StartsWithKnownNonReceiverStandardLibraryPath(tokens, start, end)
            || IsLeadingDecimalPoint(tokens, start, end))
        {
            return false;
        }

        var depth = 0;
        for (var index = start; index < end; index++)
        {
            if (Matches(tokens[index], "("))
            {
                depth++;
                continue;
            }

            if (Matches(tokens[index], ")"))
            {
                depth--;
                continue;
            }

            if (depth != 0)
            {
                continue;
            }

            if (index > start
                && IsTopLevelBinaryOperator(tokens, index))
            {
                return false;
            }

            if (index > start
                && IsIdentifierTypeCharacter(tokens[index])
                && !IsDictionaryAccess(tokens, index, end)
                && AreAdjacent(tokens[index - 1], tokens[index]))
            {
                return false;
            }
        }

        return depth == 0;
    }

    private static bool IsWrappedInParentheses(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end)
    {
        if (end - start < 2
            || !Matches(tokens[start], "(")
            || !Matches(tokens[end - 1], ")"))
        {
            return false;
        }

        var depth = 0;
        for (var index = start; index < end; index++)
        {
            if (Matches(tokens[index], "("))
            {
                depth++;
            }
            else if (Matches(tokens[index], ")"))
            {
                depth--;
                if (depth == 0)
                {
                    return index == end - 1;
                }
            }
        }

        return false;
    }

    private static bool IsLeadingDecimalPoint(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end)
        => Matches(tokens[start], ".")
            && start + 1 < end
            && tokens[start + 1].Kind == VbaTokenKind.NumericLiteral
            && AreAdjacent(tokens[start], tokens[start + 1]);

    private static bool IsTopLevelBinaryOperator(
        IReadOnlyList<VbaToken> tokens,
        int index)
    {
        var token = tokens[index];
        if (token.Text is "+" or "-" or "*" or "/" or "\\" or "^" or "&")
        {
            return true;
        }

        if (token.Text is "=" or "<>" or "<" or ">" or "<=" or ">=")
        {
            return true;
        }

        if (!(Matches(token, "Mod")
                || Matches(token, "Like")
                || Matches(token, "Is")
                || Matches(token, "And")
                || Matches(token, "Or")
                || Matches(token, "Xor")
                || Matches(token, "Eqv")
                || Matches(token, "Imp")))
        {
            return false;
        }

        return index == 0
            || !Matches(tokens[index - 1], ".")
                && !Matches(tokens[index - 1], "!");
    }

    private static bool IsIdentifierTypeCharacter(VbaToken token)
        => token.Text is "$" or "%" or "&" or "^" or "!" or "#" or "@";

    private static bool IsDictionaryAccess(
        IReadOnlyList<VbaToken> tokens,
        int index,
        int end)
        => VbaExecutableExpressionSyntax.HasDictionaryAccessTokenShape(tokens, index, end);

    private static bool StartsWithKnownNonReceiverStandardLibraryPath(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end)
    {
        var names = new List<VbaToken>();
        var nameTokenIndexes = new List<int>();
        var index = start;
        while (index < end)
        {
            if (tokens[index].Kind is not (VbaTokenKind.Identifier or VbaTokenKind.Keyword))
            {
                return false;
            }

            names.Add(tokens[index]);
            nameTokenIndexes.Add(index);
            index++;
            if (index >= end
                || Matches(tokens[index], "(")
                || Matches(tokens[index], "!"))
            {
                break;
            }

            if (!Matches(tokens[index], "."))
            {
                return false;
            }

            index++;
        }

        if (StartsWithNonReceiverDefaultMemberInvocation(
                tokens,
                index,
                end,
                names))
        {
            return true;
        }

        if (VbaStandardLibrarySyntaxFacts.IsNonValueTypeOwner(names[0].Text)
            || names.Count >= 2
                && Matches(names[0], "VBA")
                && VbaStandardLibrarySyntaxFacts.IsNonValueTypeOwner(names[1].Text))
        {
            return true;
        }

        if (Matches(names[0], "VBA"))
        {
            return StartsWithInvalidVbaNamespacePath(
                tokens,
                end,
                names,
                nameTokenIndexes);
        }

        if (VbaStandardLibrarySyntaxFacts.IsNamedReceiverObject(names[0].Text))
        {
            return names.Count != 1;
        }

        if (VbaStandardLibrarySyntaxFacts.IsKnownOwner(names[0].Text))
        {
            if (names.Count == 1
                || !VbaStandardLibrarySyntaxFacts.TryGetOwnedPotentialReceiverMember(
                    names[0].Text,
                    names[1].Text,
                    out var member))
            {
                return true;
            }

            return !VbaStandardLibraryInvocationSyntax.IsCompatible(
                tokens,
                nameTokenIndexes[1],
                end,
                member);
        }

        if (VbaStandardLibrarySyntaxFacts.TryGetGlobalPotentialReceiverMember(
                names[0].Text,
                out var globalMember))
        {
            return !VbaStandardLibraryInvocationSyntax.IsCompatible(
                tokens,
                nameTokenIndexes[0],
                end,
                globalMember);
        }

        return VbaStandardLibrarySyntaxFacts.ClassifyGlobalMember(names[0].Text) ==
            VbaStandardLibraryMemberReceiverClassification.NonReceiver;
    }

    private static bool StartsWithInvalidVbaNamespacePath(
        IReadOnlyList<VbaToken> tokens,
        int end,
        IReadOnlyList<VbaToken> names,
        IReadOnlyList<int> nameTokenIndexes)
    {
        if (names.Count == 1)
        {
            return true;
        }

        if (VbaStandardLibrarySyntaxFacts.IsNamedReceiverObject(names[1].Text))
        {
            return names.Count != 2;
        }

        if (VbaStandardLibrarySyntaxFacts.IsKnownOwner(names[1].Text))
        {
            if (names.Count == 2
                || !VbaStandardLibrarySyntaxFacts.TryGetOwnedPotentialReceiverMember(
                    names[1].Text,
                    names[2].Text,
                    out var member))
            {
                return true;
            }

            return !VbaStandardLibraryInvocationSyntax.IsCompatible(
                tokens,
                nameTokenIndexes[2],
                end,
                member);
        }

        return !VbaStandardLibrarySyntaxFacts.TryGetGlobalPotentialReceiverMember(
                names[1].Text,
                out var globalMember)
            || !VbaStandardLibraryInvocationSyntax.IsCompatible(
                tokens,
                nameTokenIndexes[1],
                end,
                globalMember);
    }

    private static bool StartsWithNonReceiverDefaultMemberInvocation(
        IReadOnlyList<VbaToken> tokens,
        int index,
        int end,
        IReadOnlyList<VbaToken> names)
    {
        if (index >= end
            || !(Matches(tokens[index], "(") || Matches(tokens[index], "!")))
        {
            return false;
        }

        var ownerIndex = 0;
        if (names.Count == 2 && Matches(names[0], "VBA"))
        {
            ownerIndex = 1;
        }
        else if (names.Count != 1)
        {
            return false;
        }

        return VbaStandardLibrarySyntaxFacts.HasNonReceiverDefaultMember(
            names[ownerIndex].Text);
    }

    private static bool AreAdjacent(VbaToken left, VbaToken right)
        => left.Range.End.Offset == right.Range.Start.Offset;

    private static bool Matches(VbaToken token, string text)
        => token.Text.Equals(text, StringComparison.OrdinalIgnoreCase);
}
