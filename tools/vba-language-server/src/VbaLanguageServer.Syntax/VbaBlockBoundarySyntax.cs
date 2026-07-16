namespace VbaLanguageServer.Syntax;

/// <summary>
/// Identifies whether a strict block boundary is a shared branch or a closer.
/// </summary>
public enum VbaBlockBoundaryRole
{
    Branch,
    Closer
}

/// <summary>
/// Represents one locally strict branch or closer without assigning block ownership.
/// </summary>
public sealed record VbaBlockBoundarySyntax(
    VbaBlockBoundaryRole Role,
    VbaBlockBranchKind? BranchKind,
    VbaBlockKind OwnerBlockKind,
    string ExpectedTerminator,
    VbaSyntaxRange Range,
    VbaSyntaxRange TokenRange,
    int FirstPhysicalLine,
    int FinalPhysicalLine,
    string LeadingWhitespace)
{
    /// <summary>
    /// Finds an exact local boundary beginning on the specified physical line.
    /// Ownership must be proven separately against a block ancestry.
    /// </summary>
    public static VbaBlockBoundarySyntax? FindAtFirstPhysicalLine(
        VbaSyntaxTree tree,
        int firstPhysicalLine,
        VbaBlockKind ownerBlockKind,
        string expectedTerminator)
    {
        var source = tree.SourceText;
        if (firstPhysicalLine < 0 || firstPhysicalLine >= source.Lines.Count)
        {
            return null;
        }

        var matches = VbaLogicalStatementSpan
            .Build(source.Text.Length, tree.TokenStream.Tokens)
            .Where(candidate =>
                candidate.SignificantTokens.Count > 0
                && candidate.SignificantTokens[0].Range.Start.Line == firstPhysicalLine)
            .Take(2)
            .ToArray();
        if (matches.Length != 1 || matches[0].EndsWithColon)
        {
            return null;
        }

        var statement = matches[0];
        var tokens = statement.SignificantTokens;
        var finalPhysicalLine = tokens[^1].Range.End.Line;
        if (!HasOnlyLeadingWhitespace(source.Lines[firstPhysicalLine], tokens[0])
            || !HasOnlyTrailingSpacesOrApostropheComment(
                source.Lines[finalPhysicalLine],
                tokens[^1])
            || tree.TokenStream.Tokens.Any(token =>
                token.Kind == VbaTokenKind.LineContinuation
                && token.Range.Start.Line == finalPhysicalLine))
        {
            return null;
        }

        var role = VbaBlockBoundaryRole.Closer;
        VbaBlockBranchKind? branchKind = null;
        if (!MatchesExactTerminator(tokens, expectedTerminator))
        {
            if (ownerBlockKind != VbaBlockKind.If
                || !TryGetIfBranch(
                    tokens,
                    tree.Module.Kind,
                    VbaBlockSyntaxFacts.HasEnclosingBlock(
                        tree,
                        VbaBlockKind.With,
                        statement.StartOffset,
                        statement.EndOffset),
                    out branchKind))
            {
                return null;
            }

            role = VbaBlockBoundaryRole.Branch;
        }

        var firstLine = source.Lines[firstPhysicalLine];
        var finalLine = source.Lines[finalPhysicalLine];
        var leadingWhitespaceLength = firstLine.Text
            .TakeWhile(value => value is ' ' or '\t')
            .Count();
        return new VbaBlockBoundarySyntax(
            role,
            branchKind,
            ownerBlockKind,
            expectedTerminator,
            new VbaSyntaxRange(
                new VbaSyntaxPosition(firstPhysicalLine, 0, firstLine.StartOffset),
                new VbaSyntaxPosition(
                    finalPhysicalLine,
                    finalLine.Text.Length,
                    finalLine.EndOffset)),
            new VbaSyntaxRange(tokens[0].Range.Start, tokens[^1].Range.End),
            firstPhysicalLine,
            finalPhysicalLine,
            firstLine.Text[..leadingWhitespaceLength]);
    }

    private static bool TryGetIfBranch(
        IReadOnlyList<VbaToken> tokens,
        VbaModuleKind moduleKind,
        bool allowLeadingMemberAccess,
        out VbaBlockBranchKind? branchKind)
    {
        branchKind = null;
        if (tokens.Count == 1 && Matches(tokens[0], "Else"))
        {
            branchKind = VbaBlockBranchKind.Else;
            return true;
        }

        if (tokens.Count < 3
            || !Matches(tokens[0], "ElseIf")
            || !Matches(tokens[^1], "Then")
            || !VbaExecutableExpressionSyntax.IsComplete(
                tokens,
                1,
                tokens.Count - 1,
                moduleKind,
                allowLeadingMemberAccess))
        {
            return false;
        }

        branchKind = VbaBlockBranchKind.ElseIf;
        return true;
    }

    private static bool MatchesExactTerminator(
        IReadOnlyList<VbaToken> tokens,
        string expectedTerminator)
    {
        var words = expectedTerminator.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Count == words.Length
            && tokens.Select((token, index) => Matches(token, words[index])).All(value => value);
    }

    private static bool HasOnlyLeadingWhitespace(VbaSourceLine line, VbaToken firstToken)
        => firstToken.Range.Start.Character <= line.Text.Length
            && line.Text.AsSpan(0, firstToken.Range.Start.Character)
                .IndexOfAnyExcept(' ', '\t') < 0;

    private static bool HasOnlyTrailingSpacesOrApostropheComment(
        VbaSourceLine line,
        VbaToken finalToken)
    {
        var code = VbaSourceText.StripApostropheComment(line.Text);
        return finalToken.Range.End.Character <= code.Length
            && code.AsSpan(finalToken.Range.End.Character).Trim().Length == 0;
    }

    private static bool Matches(VbaToken token, string text)
        => token.Text.Equals(text, StringComparison.OrdinalIgnoreCase);
}
