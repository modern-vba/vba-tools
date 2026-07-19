namespace VbaLanguageServer.Syntax;

/// <summary>
/// Identifies a structured VBA block that can contribute a contextual completion.
/// </summary>
public enum VbaBlockKind
{
    Procedure,
    If,
    Select,
    For,
    Do,
    While,
    With,
    Enum,
    Type,
    Malformed
}

/// <summary>
/// Identifies the active branch within a structured VBA block.
/// </summary>
public enum VbaBlockBranchKind
{
    Body,
    Then,
    ElseIf,
    Else,
    Case,
    CaseElse
}

/// <summary>
/// Represents one branch header and the body range that it owns.
/// </summary>
/// <param name="Kind">The branch kind.</param>
/// <param name="HeaderRange">The branch header range.</param>
/// <param name="Range">The range owned by the branch body.</param>
public sealed record VbaBlockBranchSyntax(
    VbaBlockBranchKind Kind,
    VbaSyntaxRange HeaderRange,
    VbaSyntaxRange Range);

/// <summary>
/// Represents one grammar-derived structured VBA block.
/// </summary>
/// <param name="Kind">The block kind.</param>
/// <param name="OpenerRange">The opener statement range.</param>
/// <param name="CloserRange">The closer statement range, when present.</param>
/// <param name="ExpectedTerminator">The canonical terminator text.</param>
/// <param name="Branches">The block branches in source order.</param>
/// <param name="Range">The full range owned by the block.</param>
/// <param name="IsMalformedBarrier">Whether the block prevents completion after mismatched syntax.</param>
/// <param name="MalformedBarrierOwnerRange">The unmatched opener that caused a malformed barrier.</param>
public sealed record VbaBlockSyntax(
    VbaBlockKind Kind,
    VbaSyntaxRange OpenerRange,
    VbaSyntaxRange? CloserRange,
    string ExpectedTerminator,
    IReadOnlyList<VbaBlockBranchSyntax> Branches,
    VbaSyntaxRange Range,
    bool IsMalformedBarrier = false,
    VbaSyntaxRange? MalformedBarrierOwnerRange = null);

/// <summary>
/// Represents a block enclosing an editor position and its active branch.
/// </summary>
public sealed record VbaEnclosingBlockSyntax(
    VbaBlockSyntax Block,
    VbaBlockBranchKind ActiveBranch);

/// <summary>
/// Represents an identifier or numeric line label owned by a callable body.
/// </summary>
public sealed record VbaLineLabelSyntax(
    string Name,
    bool IsNumeric,
    VbaSyntaxRange Range,
    string ProcedureName,
    VbaSyntaxRange ProcedureRange);

/// <summary>
/// Identifies the statement form that owns an active line-label destination.
/// </summary>
public enum VbaLabelReferenceKind
{
    GoTo,
    GoSub,
    Resume,
    OnGoTo,
    OnGoSub,
    OnErrorGoTo,
    OnErrorResume
}

/// <summary>
/// Represents the active line-label destination at an editor position.
/// </summary>
public sealed record VbaLabelReferenceSyntax(
    VbaLabelReferenceKind Kind,
    int DestinationIndex,
    bool IsIncomplete,
    bool AllowsProcedureLabels,
    IReadOnlyList<string> SyntaxCandidates,
    string ProcedureName,
    VbaSyntaxRange ProcedureRange,
    VbaSyntaxRange ReplacementRange);

internal sealed record VbaCompletionSyntaxFacts(
    IReadOnlyList<VbaBlockSyntax> Blocks,
    IReadOnlyList<VbaLineLabelSyntax> LineLabels);

/// <summary>
/// Builds completion-specific block and label facts from the lexical statement stream.
/// </summary>
internal static class VbaCompletionSyntaxFactsParser
{
    public static VbaCompletionSyntaxFacts Parse(
        VbaSourceText sourceText,
        VbaTokenStream tokenStream,
        IReadOnlyList<VbaCallableDeclarationSyntax> callableDeclarations,
        IReadOnlyList<VbaPreprocessorBlockSyntax> preprocessorBlocks,
        int codeStartLine)
    {
        var statements = VbaLogicalStatementSpan.Build(
                sourceText.Text.Length,
                tokenStream.Tokens,
                includeEmptyStatements: false)
            .Where(statement => statement.Range.Start.Line >= codeStartLine)
            .ToArray();
        var blocks = ParseBlocks(sourceText, statements, callableDeclarations);
        var labels = ParseLineLabels(sourceText, statements, callableDeclarations, preprocessorBlocks);
        return new VbaCompletionSyntaxFacts(blocks, labels);
    }

    private static IReadOnlyList<VbaBlockSyntax> ParseBlocks(
        VbaSourceText sourceText,
        IReadOnlyList<VbaLogicalStatementSpan> statements,
        IReadOnlyList<VbaCallableDeclarationSyntax> callableDeclarations)
    {
        var completed = new List<VbaBlockSyntax>();
        var stack = new Stack<OpenBlock>();
        foreach (var statement in statements)
        {
            var tokens = statement.SignificantTokens;
            if (tokens.Count == 0 || tokens[0].Kind == VbaTokenKind.PreprocessorDirective)
            {
                continue;
            }

            if (TryGetCloser(tokens, out var closeKind, out var closeText))
            {
                if (stack.Count == 0
                    || stack.Peek().Kind != closeKind
                    || !stack.Peek().ExpectedTerminator.Equals(closeText, StringComparison.OrdinalIgnoreCase))
                {
                    completed.Add(CreateMalformedBarrier(
                        sourceText,
                        statement.Range,
                        stack.TryPeek(out var open) ? open.ExpectedTerminator : closeText,
                        callableDeclarations,
                        FindBlockingOwnerForCloser(stack, closeKind, closeText)));
                    continue;
                }

                completed.Add(Close(stack.Pop(), statement.Range));
                continue;
            }

            if (TryGetBranch(tokens, out var branchKind))
            {
                if (stack.Count == 0 || !CanAcceptBranch(stack.Peek(), branchKind))
                {
                    completed.Add(CreateMalformedBarrier(
                        sourceText,
                        statement.Range,
                        stack.TryPeek(out var open) ? open.ExpectedTerminator : string.Empty,
                        callableDeclarations,
                        FindBlockingOwnerForBranch(stack, branchKind)));
                    continue;
                }

                stack.Peek().BranchHeaders.Add(new BranchHeader(branchKind, statement.Range));
                continue;
            }

            if (TryGetOpener(tokens, out var kind, out var expectedTerminator, out var initialBranch))
            {
                if (kind == VbaBlockKind.If && statement.EndsWithColon)
                {
                    continue;
                }

                var branches = initialBranch is null
                    ? []
                    : new List<BranchHeader> { new(initialBranch.Value, statement.Range) };
                stack.Push(new OpenBlock(kind, expectedTerminator, statement.Range, branches));
            }
        }

        while (stack.Count > 0)
        {
            var open = stack.Pop();
            completed.Add(Close(open, null, sourceText.FullRange.End));
        }

        return completed
            .OrderBy(block => block.OpenerRange.Start.Offset)
            .ThenByDescending(block => block.Range.End.Offset)
            .ToArray();
    }

    private static IReadOnlyList<VbaLineLabelSyntax> ParseLineLabels(
        VbaSourceText sourceText,
        IReadOnlyList<VbaLogicalStatementSpan> statements,
        IReadOnlyList<VbaCallableDeclarationSyntax> callableDeclarations,
        IReadOnlyList<VbaPreprocessorBlockSyntax> preprocessorBlocks)
    {
        var labels = new List<VbaLineLabelSyntax>();
        foreach (var statement in statements)
        {
            var tokens = statement.SignificantTokens;
            if (tokens.Count == 0)
            {
                continue;
            }

            var labelToken = tokens[0];
            var isIdentifierLabel = statement.EndsWithColon
                && tokens.Count == 1
                && labelToken.Kind == VbaTokenKind.Identifier;
            var isNumericLabel = labelToken.Kind == VbaTokenKind.NumericLiteral
                && labelToken.Text.All(char.IsDigit)
                && IsFirstCodeTokenOnPhysicalLine(sourceText, labelToken);
            if (!isIdentifierLabel && !isNumericLabel)
            {
                continue;
            }

            if (preprocessorBlocks.Any(block =>
                block.Range.Start.Offset <= labelToken.Range.Start.Offset
                && labelToken.Range.End.Offset <= block.Range.End.Offset))
            {
                continue;
            }

            var owner = FindCallableOwner(callableDeclarations, labelToken.Range.Start.Offset);
            if (owner is null
                || labelToken.Range.Start.Line <= owner.LineIndex
                || labelToken.Range.Start.Line >= owner.BlockRange.End.Line)
            {
                continue;
            }

            labels.Add(new VbaLineLabelSyntax(
                labelToken.Text,
                isNumericLabel,
                labelToken.Range,
                owner.Name,
                owner.BlockRange));
        }

        return labels;
    }

    private static bool TryGetOpener(
        IReadOnlyList<VbaToken> tokens,
        out VbaBlockKind kind,
        out string expectedTerminator,
        out VbaBlockBranchKind? initialBranch)
    {
        kind = default;
        expectedTerminator = string.Empty;
        initialBranch = null;
        var index = SkipDeclarationModifiers(tokens, 0);
        if (Matches(tokens, index, "Sub"))
        {
            kind = VbaBlockKind.Procedure;
            expectedTerminator = "End Sub";
            return true;
        }

        if (Matches(tokens, index, "Function"))
        {
            kind = VbaBlockKind.Procedure;
            expectedTerminator = "End Function";
            return true;
        }

        if (Matches(tokens, index, "Property")
            && (Matches(tokens, index + 1, "Get")
                || Matches(tokens, index + 1, "Let")
                || Matches(tokens, index + 1, "Set")))
        {
            kind = VbaBlockKind.Procedure;
            expectedTerminator = "End Property";
            return true;
        }

        if (Matches(tokens, index, "Enum"))
        {
            kind = VbaBlockKind.Enum;
            expectedTerminator = "End Enum";
            return true;
        }

        if (Matches(tokens, index, "Type"))
        {
            kind = VbaBlockKind.Type;
            expectedTerminator = "End Type";
            return true;
        }

        if (Matches(tokens, 0, "If")
            && tokens.Count > 1
            && Matches(tokens, tokens.Count - 1, "Then"))
        {
            kind = VbaBlockKind.If;
            expectedTerminator = "End If";
            initialBranch = VbaBlockBranchKind.Then;
            return true;
        }

        if (Matches(tokens, 0, "Select") && Matches(tokens, 1, "Case"))
        {
            kind = VbaBlockKind.Select;
            expectedTerminator = "End Select";
            return true;
        }

        if (Matches(tokens, 0, "For"))
        {
            kind = VbaBlockKind.For;
            expectedTerminator = "Next";
            return true;
        }

        if (Matches(tokens, 0, "Do"))
        {
            kind = VbaBlockKind.Do;
            expectedTerminator = "Loop";
            return true;
        }

        if (Matches(tokens, 0, "While"))
        {
            kind = VbaBlockKind.While;
            expectedTerminator = "Wend";
            return true;
        }

        if (Matches(tokens, 0, "With"))
        {
            kind = VbaBlockKind.With;
            expectedTerminator = "End With";
            return true;
        }

        return false;
    }

    private static bool TryGetCloser(
        IReadOnlyList<VbaToken> tokens,
        out VbaBlockKind kind,
        out string text)
    {
        kind = default;
        text = string.Empty;
        if (Matches(tokens, 0, "Next"))
        {
            kind = VbaBlockKind.For;
            text = "Next";
            return true;
        }

        if (Matches(tokens, 0, "Loop"))
        {
            kind = VbaBlockKind.Do;
            text = "Loop";
            return true;
        }

        if (Matches(tokens, 0, "Wend"))
        {
            kind = VbaBlockKind.While;
            text = "Wend";
            return true;
        }

        if (!Matches(tokens, 0, "End") || tokens.Count < 2)
        {
            return false;
        }

        var suffix = tokens[1].Text;
        (kind, text) = suffix.ToUpperInvariant() switch
        {
            "SUB" => (VbaBlockKind.Procedure, "End Sub"),
            "FUNCTION" => (VbaBlockKind.Procedure, "End Function"),
            "PROPERTY" => (VbaBlockKind.Procedure, "End Property"),
            "IF" => (VbaBlockKind.If, "End If"),
            "SELECT" => (VbaBlockKind.Select, "End Select"),
            "WITH" => (VbaBlockKind.With, "End With"),
            "ENUM" => (VbaBlockKind.Enum, "End Enum"),
            "TYPE" => (VbaBlockKind.Type, "End Type"),
            _ => (default, string.Empty)
        };
        return text.Length > 0;
    }

    private static bool TryGetBranch(
        IReadOnlyList<VbaToken> tokens,
        out VbaBlockBranchKind kind)
    {
        if (Matches(tokens, 0, "ElseIf"))
        {
            kind = VbaBlockBranchKind.ElseIf;
            return true;
        }

        if (Matches(tokens, 0, "Else"))
        {
            kind = VbaBlockBranchKind.Else;
            return true;
        }

        if (Matches(tokens, 0, "Case") && Matches(tokens, 1, "Else"))
        {
            kind = VbaBlockBranchKind.CaseElse;
            return true;
        }

        if (Matches(tokens, 0, "Case"))
        {
            kind = VbaBlockBranchKind.Case;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool CanAcceptBranch(OpenBlock block, VbaBlockBranchKind branch)
    {
        if (branch is VbaBlockBranchKind.Case or VbaBlockBranchKind.CaseElse)
        {
            return block.Kind == VbaBlockKind.Select
                && !block.BranchHeaders.Any(header =>
                    header.Kind == VbaBlockBranchKind.CaseElse);
        }

        if (block.Kind != VbaBlockKind.If)
        {
            return false;
        }

        var hasElse = block.BranchHeaders.Any(header => header.Kind == VbaBlockBranchKind.Else);
        return !hasElse;
    }

    private static VbaSyntaxRange? FindBlockingOwnerForCloser(
        Stack<OpenBlock> stack,
        VbaBlockKind closeKind,
        string closeText)
    {
        var openBlocks = stack.ToArray();
        return openBlocks.Length > 1
            && openBlocks.Skip(1).Any(open =>
                open.Kind == closeKind
                && open.ExpectedTerminator.Equals(
                    closeText,
                    StringComparison.OrdinalIgnoreCase))
                ? openBlocks[0].OpenerRange
                : null;
    }

    private static VbaSyntaxRange? FindBlockingOwnerForBranch(
        Stack<OpenBlock> stack,
        VbaBlockBranchKind branchKind)
    {
        var openBlocks = stack.ToArray();
        return openBlocks.Length > 1
            && openBlocks.Skip(1).Any(open => CanAcceptBranch(open, branchKind))
                ? openBlocks[0].OpenerRange
                : null;
    }

    private static VbaBlockSyntax Close(
        OpenBlock open,
        VbaSyntaxRange? closerRange,
        VbaSyntaxPosition? fallbackEnd = null)
    {
        var end = closerRange?.End ?? fallbackEnd ?? open.OpenerRange.End;
        var bodyEnd = closerRange?.Start ?? end;
        var branches = new List<VbaBlockBranchSyntax>();
        for (var index = 0; index < open.BranchHeaders.Count; index++)
        {
            var header = open.BranchHeaders[index];
            var branchEnd = index + 1 < open.BranchHeaders.Count
                ? open.BranchHeaders[index + 1].Range.Start
                : bodyEnd;
            branches.Add(new VbaBlockBranchSyntax(
                header.Kind,
                header.Range,
                new VbaSyntaxRange(header.Range.End, branchEnd)));
        }

        return new VbaBlockSyntax(
            open.Kind,
            open.OpenerRange,
            closerRange,
            open.ExpectedTerminator,
            branches,
            new VbaSyntaxRange(open.OpenerRange.Start, end));
    }

    private static VbaBlockSyntax CreateMalformedBarrier(
        VbaSourceText sourceText,
        VbaSyntaxRange offendingRange,
        string expectedTerminator,
        IReadOnlyList<VbaCallableDeclarationSyntax> callableDeclarations,
        VbaSyntaxRange? ownerRange)
    {
        var owner = FindCallableOwner(callableDeclarations, offendingRange.Start.Offset);
        var end = owner?.BlockRange.End ?? sourceText.FullRange.End;
        return new VbaBlockSyntax(
            VbaBlockKind.Malformed,
            offendingRange,
            null,
            expectedTerminator,
            [],
            new VbaSyntaxRange(offendingRange.Start, end),
            IsMalformedBarrier: true,
            MalformedBarrierOwnerRange: ownerRange);
    }

    private static VbaCallableDeclarationSyntax? FindCallableOwner(
        IReadOnlyList<VbaCallableDeclarationSyntax> callableDeclarations,
        int offset)
        => callableDeclarations
            .Where(declaration => !declaration.IsExternal)
            .Where(declaration => declaration.BlockRange.Start.Offset <= offset
                && offset <= declaration.BlockRange.End.Offset)
            .OrderBy(declaration => declaration.BlockRange.End.Offset - declaration.BlockRange.Start.Offset)
            .FirstOrDefault();

    private static int SkipDeclarationModifiers(IReadOnlyList<VbaToken> tokens, int index)
    {
        if (Matches(tokens, index, "Public")
            || Matches(tokens, index, "Private")
            || Matches(tokens, index, "Friend")
            || Matches(tokens, index, "Global"))
        {
            index++;
        }

        return Matches(tokens, index, "Static") ? index + 1 : index;
    }

    private static bool IsFirstCodeTokenOnPhysicalLine(VbaSourceText sourceText, VbaToken token)
    {
        var line = sourceText.Lines[token.Range.Start.Line];
        var before = line.Text.AsSpan(0, token.Range.Start.Character);
        return before.Trim().Length == 0;
    }

    private static bool Matches(IReadOnlyList<VbaToken> tokens, int index, string text)
        => index >= 0
            && index < tokens.Count
            && IsNameToken(tokens[index])
            && tokens[index].Text.Equals(text, StringComparison.OrdinalIgnoreCase);

    private static bool IsNameToken(VbaToken token)
        => token.Kind is VbaTokenKind.Identifier or VbaTokenKind.Keyword;

    private sealed record BranchHeader(VbaBlockBranchKind Kind, VbaSyntaxRange Range);

    private sealed record OpenBlock(
        VbaBlockKind Kind,
        string ExpectedTerminator,
        VbaSyntaxRange OpenerRange,
        List<BranchHeader> BranchHeaders);
}

/// <summary>
/// Represents one colon- or newline-delimited logical statement span.
/// </summary>
internal sealed record VbaLogicalStatementSpan(
    int StartOffset,
    int EndOffset,
    int NextOffset,
    IReadOnlyList<VbaToken> SignificantTokens,
    bool EndsWithColon)
{
    public VbaSyntaxRange Range
        => SignificantTokens.Count > 0
            ? new VbaSyntaxRange(SignificantTokens[0].Range.Start, SignificantTokens[^1].Range.End)
            : new VbaSyntaxRange(
                new VbaSyntaxPosition(0, 0, StartOffset),
                new VbaSyntaxPosition(0, 0, EndOffset));

    public static IReadOnlyList<VbaLogicalStatementSpan> Build(
        int textLength,
        IReadOnlyList<VbaToken> tokens,
        bool includeEmptyStatements = true)
    {
        var statements = new List<VbaLogicalStatementSpan>();
        var significant = new List<VbaToken>();
        var startOffset = 0;
        var continued = false;
        var parenthesisDepth = 0;
        foreach (var token in tokens)
        {
            if (token.Kind == VbaTokenKind.LineContinuation)
            {
                continued = true;
                continue;
            }

            if (token.Kind == VbaTokenKind.NewLine)
            {
                if (continued)
                {
                    continued = false;
                    continue;
                }

                AddStatement(
                    statements,
                    startOffset,
                    token.Range.Start.Offset,
                    token.Range.End.Offset,
                    significant,
                    endsWithColon: false,
                    includeEmptyStatements);
                significant.Clear();
                startOffset = token.Range.End.Offset;
                parenthesisDepth = 0;
                continue;
            }

            if (token.Kind == VbaTokenKind.Punctuation)
            {
                if (token.Text == "(")
                {
                    parenthesisDepth++;
                }
                else if (token.Text == ")")
                {
                    parenthesisDepth = Math.Max(0, parenthesisDepth - 1);
                }
                else if (token.Text == ":"
                    && parenthesisDepth == 0
                    && !(significant.Count > 0
                        && significant[0].Text.Equals("Rem", StringComparison.OrdinalIgnoreCase)))
                {
                    AddStatement(
                        statements,
                        startOffset,
                        token.Range.Start.Offset,
                        token.Range.End.Offset,
                        significant,
                        endsWithColon: true,
                        includeEmptyStatements);
                    significant.Clear();
                    startOffset = token.Range.End.Offset;
                    continued = false;
                    continue;
                }
            }

            if (token.Kind is not VbaTokenKind.Whitespace and not VbaTokenKind.Comment)
            {
                significant.Add(token);
            }
        }

        AddStatement(
            statements,
            startOffset,
            textLength,
            textLength,
            significant,
            endsWithColon: false,
            includeEmptyStatements);
        return statements;
    }

    private static void AddStatement(
        ICollection<VbaLogicalStatementSpan> statements,
        int startOffset,
        int endOffset,
        int nextOffset,
        IReadOnlyList<VbaToken> significant,
        bool endsWithColon,
        bool includeEmptyStatements)
    {
        if (!includeEmptyStatements && significant.Count == 0)
        {
            return;
        }

        statements.Add(new VbaLogicalStatementSpan(
            startOffset,
            endOffset,
            nextOffset,
            significant.ToArray(),
            endsWithColon));
    }
}
