namespace VbaTools.Syntax;

/// <summary>
/// Centralizes statement-boundary and formatting block facts for parsed VBA source.
/// </summary>
internal static class VbaBlockSyntaxFacts
{
    public static bool HasEnclosingBlock(
        VbaSyntaxTree tree,
        VbaBlockKind kind,
        int startOffset,
        int endOffset,
        VbaConditionalCompilationBranchPath positionPath,
        bool requireCompleteConditionalStructure)
        => tree.Module.Blocks.Any(block =>
            block.Kind == kind
            && !block.IsMalformedBarrier
            && block.OpenerRange.Start.Offset < startOffset
            && endOffset <= block.Range.End.Offset
            && VbaConditionalCompilationBranchFacts.TryGetPath(
                tree,
                block.OpenerRange,
                requireCompleteConditionalStructure,
                out var blockPath)
            && blockPath.IsPrefixOf(positionPath));

    public static string? GetFormattingOpenTerminator(string trimmedLine)
        => GetProcedureOpenTerminator(trimmedLine)
            ?? GetStructuredStatementOpenTerminator(trimmedLine)
            ?? GetFormattingDeclarationOpenTerminator(trimmedLine)
            ?? GetWhileOpenTerminator(trimmedLine);

    public static string? GetFormattingCloseTerminator(string trimmedLine)
        => GetStatementCloseTerminator(trimmedLine)
            ?? GetFormattingDeclarationCloseTerminator(trimmedLine)
            ?? GetWhileCloseTerminator(trimmedLine);

    public static string? GetFormattingBranchTerminator(string trimmedLine)
    {
        var tokens = SignificantTokens(trimmedLine);
        if (Matches(tokens, 0, "Else") || Matches(tokens, 0, "ElseIf"))
        {
            return "End If";
        }

        return Matches(tokens, 0, "Case") ? "End Select" : null;
    }

    public static string? GetStatementCloseTerminator(string trimmedLine)
    {
        var tokens = SignificantTokens(trimmedLine);
        return Matches(tokens, 0, "End") && Matches(tokens, 1, "Sub") ? "End Sub"
            : Matches(tokens, 0, "End") && Matches(tokens, 1, "Function") ? "End Function"
            : Matches(tokens, 0, "End") && Matches(tokens, 1, "Property") ? "End Property"
            : Matches(tokens, 0, "End") && Matches(tokens, 1, "If") ? "End If"
            : Matches(tokens, 0, "End") && Matches(tokens, 1, "With") ? "End With"
            : Matches(tokens, 0, "End") && Matches(tokens, 1, "Select") ? "End Select"
            : Matches(tokens, 0, "Next") ? "Next"
            : Matches(tokens, 0, "Loop") ? "Loop"
            : null;
    }

    public static VbaStatementKind ClassifyStatement(string trimmedLine, bool isProcedureHeader)
    {
        if (isProcedureHeader)
        {
            return VbaStatementKind.ProcedureBody;
        }

        var tokens = SignificantTokens(trimmedLine);
        if (Matches(tokens, 0, "If") && Matches(tokens, tokens.Count - 1, "Then"))
        {
            return VbaStatementKind.IfBlock;
        }

        if (Matches(tokens, 0, "With"))
        {
            return VbaStatementKind.WithBlock;
        }

        if (Matches(tokens, 0, "Select") && Matches(tokens, 1, "Case"))
        {
            return VbaStatementKind.SelectBlock;
        }

        if (Matches(tokens, 0, "For"))
        {
            return VbaStatementKind.ForBlock;
        }

        if (Matches(tokens, 0, "Do"))
        {
            return VbaStatementKind.DoLoopBlock;
        }

        if (trimmedLine.StartsWith("@", StringComparison.Ordinal))
        {
            return VbaStatementKind.Malformed;
        }

        if (StartsWithIdentifierAssignment(trimmedLine))
        {
            return VbaStatementKind.Assignment;
        }

        if (StartsWithCallTarget(trimmedLine))
        {
            return VbaStatementKind.Call;
        }

        return VbaStatementKind.Unknown;
    }

    public static string? GetExpectedStatementTerminator(string trimmedLine, VbaStatementKind statementKind)
        => statementKind switch
        {
            VbaStatementKind.ProcedureBody => GetProcedureOpenTerminator(trimmedLine),
            VbaStatementKind.IfBlock => "End If",
            VbaStatementKind.WithBlock => "End With",
            VbaStatementKind.SelectBlock => "End Select",
            VbaStatementKind.ForBlock => "Next",
            VbaStatementKind.DoLoopBlock => "Loop",
            _ => null
        };

    private static string? GetProcedureOpenTerminator(string trimmedLine)
    {
        var tokens = SignificantTokens(trimmedLine);
        var index = 0;
        if (MatchesAny(tokens, index, "Public", "Private", "Friend", "Global"))
        {
            index++;
        }

        if (Matches(tokens, index, "Static"))
        {
            index++;
        }

        return Matches(tokens, index, "Sub") ? "End Sub"
            : Matches(tokens, index, "Function") ? "End Function"
            : Matches(tokens, index, "Property") ? "End Property"
            : null;
    }

    private static string? GetStructuredStatementOpenTerminator(string trimmedLine)
    {
        var tokens = SignificantTokens(trimmedLine);
        return Matches(tokens, 0, "If") && Matches(tokens, tokens.Count - 1, "Then") ? "End If"
            : Matches(tokens, 0, "Select") && Matches(tokens, 1, "Case") ? "End Select"
            : Matches(tokens, 0, "With") ? "End With"
            : Matches(tokens, 0, "For") && !ContainsColonWord(tokens, "Next") ? "Next"
            : Matches(tokens, 0, "Do") && !ContainsColonWord(tokens, "Loop") ? "Loop"
            : null;
    }

    private static string? GetFormattingDeclarationOpenTerminator(string trimmedLine)
    {
        var tokens = SignificantTokens(trimmedLine);
        var index = MatchesAny(tokens, 0, "Public", "Private", "Friend") ? 1 : 0;
        return Matches(tokens, index, "Enum") ? "End Enum"
            : Matches(tokens, index, "Type") ? "End Type"
            : null;
    }

    private static string? GetFormattingDeclarationCloseTerminator(string trimmedLine)
    {
        var tokens = SignificantTokens(trimmedLine);
        return Matches(tokens, 0, "End") && Matches(tokens, 1, "Enum") ? "End Enum"
            : Matches(tokens, 0, "End") && Matches(tokens, 1, "Type") ? "End Type"
            : null;
    }

    private static string? GetWhileOpenTerminator(string trimmedLine)
        => Matches(SignificantTokens(trimmedLine), 0, "While") ? "Wend" : null;

    private static string? GetWhileCloseTerminator(string trimmedLine)
        => Matches(SignificantTokens(trimmedLine), 0, "Wend") ? "Wend" : null;

    private static bool StartsWithIdentifierAssignment(string text)
    {
        var tokens = SignificantTokens(text);
        return tokens.Count >= 2
            && VbaIdentifier.IsIdentifier(tokens[0].Text)
            && tokens[1].Text == "=";
    }

    private static bool StartsWithCallTarget(string text)
    {
        var tokens = SignificantTokens(text);
        if (tokens.Count == 0)
        {
            return false;
        }

        var index = tokens[0].Text.Equals("Call", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (index >= tokens.Count)
        {
            return false;
        }

        if (tokens[index].Text == ".")
        {
            return true;
        }

        var target = tokens[index];
        var isWordToken = target.Kind is VbaTokenKind.Identifier or VbaTokenKind.Keyword;
        var isQualified = index + 1 < tokens.Count && tokens[index + 1].Text == ".";
        var isExplicitQualifiedRoot = target.Text.Equals("Me", StringComparison.OrdinalIgnoreCase)
            || target.Text.Equals("Debug", StringComparison.OrdinalIgnoreCase);
        return isWordToken
            && (VbaLanguageVocabulary.CanBeBareCallTarget(target.Text)
                || isQualified && isExplicitQualifiedRoot);
    }

    private static IReadOnlyList<VbaToken> SignificantTokens(string text)
        => VbaTokenStream.FromText(text).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.Comment
                and not VbaTokenKind.LineContinuation)
            .ToArray();

    private static bool ContainsColonWord(IReadOnlyList<VbaToken> tokens, string word)
    {
        for (var index = 0; index + 1 < tokens.Count; index++)
        {
            if (tokens[index].Text == ":" && Matches(tokens, index + 1, word))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Matches(IReadOnlyList<VbaToken> tokens, int index, string word)
        => index >= 0
            && index < tokens.Count
            && tokens[index].Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesAny(
        IReadOnlyList<VbaToken> tokens,
        int index,
        params string[] words)
        => words.Any(word => Matches(tokens, index, word));
}
