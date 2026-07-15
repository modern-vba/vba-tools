namespace VbaLanguageServer.Syntax;

/// <summary>
/// Identifies the lexical region that owns an editor position.
/// </summary>
public enum VbaPositionRegion
{
    Outside,
    Code,
    String,
    Comment,
    Preprocessor,
    Designer
}

/// <summary>
/// Identifies the completion vocabulary selected by syntax at an editor position.
/// </summary>
public enum VbaCompletionSyntaxKind
{
    None,
    General,
    Member,
    TypeName
}

/// <summary>
/// Identifies the VBA call form surrounding an editor position.
/// </summary>
public enum VbaCallSyntaxForm
{
    Parenthesized,
    Statement
}

/// <summary>
/// Represents an identifier token at an editor position.
/// </summary>
public sealed record VbaPositionIdentifierSyntax(
    string Name,
    VbaSyntaxRange Range,
    bool IsKeyword);

/// <summary>
/// Represents a token-derived dotted name chain.
/// </summary>
public sealed record VbaMemberAccessSyntax(
    IReadOnlyList<VbaPositionIdentifierSyntax> Segments,
    int TargetSegmentIndex,
    bool IsLeadingDot,
    bool IsIncomplete,
    bool HasTrailingWhitespace,
    VbaSyntaxRange Range)
{
    /// <summary>
    /// Gets the target segment, or null when the chain ends in a dot.
    /// </summary>
    public VbaPositionIdentifierSyntax? Target
        => TargetSegmentIndex >= 0 && TargetSegmentIndex < Segments.Count
            ? Segments[TargetSegmentIndex]
            : null;

    /// <summary>
    /// Gets the receiver segments before the target.
    /// </summary>
    public IReadOnlyList<VbaPositionIdentifierSyntax> ReceiverSegments
        => Segments.Take(Math.Clamp(TargetSegmentIndex, 0, Segments.Count)).ToArray();
}

/// <summary>
/// Represents an incomplete or complete type reference around an editor position.
/// </summary>
public sealed record VbaPositionTypeReferenceSyntax(
    VbaPositionIdentifierSyntax? Qualifier,
    VbaPositionIdentifierSyntax? Name,
    bool IsNew,
    bool IsIncomplete,
    VbaSyntaxRange Range);

/// <summary>
/// Represents one token-derived call argument.
/// </summary>
public sealed record VbaCallArgumentSyntax(
    int Index,
    string? Name,
    bool IsOmitted,
    VbaSyntaxRange Range);

/// <summary>
/// Represents the call site surrounding an editor position.
/// </summary>
public sealed record VbaCallSiteSyntax(
    VbaCallSyntaxForm Form,
    VbaMemberAccessSyntax Callee,
    IReadOnlyList<VbaCallArgumentSyntax> Arguments,
    int ActiveArgumentIndex,
    string? ActiveNamedArgument,
    bool IsIncomplete);

/// <summary>
/// Represents one enclosing With scope. A null receiver preserves an invalid nested scope as a fail-closed barrier.
/// </summary>
public sealed record VbaWithScopeSyntax(VbaMemberAccessSyntax? Receiver);

/// <summary>
/// Contains the syntax facts needed by editor features at one position.
/// </summary>
public sealed record VbaPositionSyntax(
    VbaPositionRegion Region,
    VbaPositionIdentifierSyntax? Identifier,
    VbaCompletionSyntaxKind CompletionKind,
    VbaMemberAccessSyntax? MemberAccess,
    VbaPositionTypeReferenceSyntax? TypeReference,
    VbaCallSiteSyntax? CallSite,
    IReadOnlyList<VbaWithScopeSyntax> EnclosingWithScopes)
{
    /// <summary>
    /// Gets the result returned for a position outside the source document.
    /// </summary>
    public static VbaPositionSyntax Outside { get; } = new(
        VbaPositionRegion.Outside,
        null,
        VbaCompletionSyntaxKind.None,
        null,
        null,
        null,
        []);
}

/// <summary>
/// Owns token-derived position queries for one immutable syntax tree.
/// </summary>
internal sealed class VbaPositionSyntaxIndex
{
    private readonly VbaSourceText sourceText;
    private readonly IReadOnlyList<VbaToken> tokens;
    private readonly IReadOnlyList<IReadOnlyList<VbaToken>> nonWhitespaceTokensByLine;
    private readonly IReadOnlyList<StatementSpan> statements;
    private readonly IReadOnlyList<WithScopeSpan> withScopes;
    private readonly IReadOnlyList<int> withScopePrefixMaximumEnds;
    private readonly VbaSyntaxRange? designerRange;

    public VbaPositionSyntaxIndex(VbaSyntaxTree tree)
    {
        sourceText = tree.SourceText;
        tokens = tree.TokenStream.Tokens;
        nonWhitespaceTokensByLine = BuildNonWhitespaceTokensByLine(sourceText.Lines.Count, tokens);
        statements = BuildStatements(tree.Text.Length, tokens);
        withScopes = BuildWithScopes(tree.Text.Length, statements)
            .OrderBy(scope => scope.StartOffset)
            .ThenBy(scope => scope.Depth)
            .ToArray();
        withScopePrefixMaximumEnds = BuildPrefixMaximumEnds(withScopes);
        designerRange = tree.Module.FormDesignerBlock?.Range;
    }

    public VbaPositionSyntax GetPositionSyntax(int line, int character)
    {
        if (!TryGetPosition(line, character, out var position))
        {
            return VbaPositionSyntax.Outside;
        }

        var statement = FindStatement(position.Offset);
        var region = GetRegion(position, statement);
        if (region != VbaPositionRegion.Code)
        {
            return new VbaPositionSyntax(
                region,
                null,
                VbaCompletionSyntaxKind.None,
                null,
                null,
                null,
                []);
        }

        var identifier = FindIdentifier(statement, position);
        var memberAccess = TryGetMemberAccess(statement, position);
        var typeReference = TryGetTypeReference(statement, position);
        var callSite = TryGetCallSite(statement, position);
        var enclosingWithScopes = GetEnclosingWithScopes(position.Offset);
        var completionKind = GetCompletionKind(
            position,
            memberAccess,
            typeReference);
        return new VbaPositionSyntax(
            region,
            identifier,
            completionKind,
            memberAccess,
            typeReference,
            callSite,
            enclosingWithScopes);
    }

    private bool TryGetPosition(int line, int character, out VbaSyntaxPosition position)
    {
        position = default!;
        if (line < 0 || line >= sourceText.Lines.Count)
        {
            return false;
        }

        var sourceLine = sourceText.Lines[line];
        if (character < 0 || character > sourceLine.Text.Length)
        {
            return false;
        }

        position = new VbaSyntaxPosition(line, character, sourceLine.StartOffset + character);
        return true;
    }

    private VbaPositionRegion GetRegion(VbaSyntaxPosition position, StatementSpan statement)
    {
        if (designerRange is not null
            && position.Offset >= designerRange.Start.Offset
            && position.Offset < designerRange.End.Offset)
        {
            return VbaPositionRegion.Designer;
        }

        var tokenIndex = FindTokenIndex(position.Offset);
        var token = tokenIndex >= 0 ? tokens[tokenIndex] : null;
        if (token is not null
            && token.Kind == VbaTokenKind.NewLine
            && tokenIndex > 0
            && tokens[tokenIndex - 1].Range.End.Offset == position.Offset
            && tokens[tokenIndex - 1].Kind is VbaTokenKind.Comment or VbaTokenKind.PreprocessorDirective)
        {
            token = tokens[tokenIndex - 1];
        }

        if (token is not null
            && position.Line == token.Range.Start.Line
            && position.Offset <= token.Range.End.Offset)
        {
            if (token.Kind == VbaTokenKind.StringLiteral)
            {
                return VbaPositionRegion.String;
            }

            if (token.Kind == VbaTokenKind.Comment)
            {
                return VbaPositionRegion.Comment;
            }

            if (token.Kind == VbaTokenKind.PreprocessorDirective)
            {
                return VbaPositionRegion.Preprocessor;
            }
        }

        var first = statement.SignificantTokens.FirstOrDefault();
        if (first is not null
            && first.Text.Equals("Rem", StringComparison.OrdinalIgnoreCase)
            && position.Offset >= first.Range.Start.Offset)
        {
            return VbaPositionRegion.Comment;
        }

        return VbaPositionRegion.Code;
    }

    private VbaPositionIdentifierSyntax? FindIdentifier(
        StatementSpan statement,
        VbaSyntaxPosition position)
        => statement.SignificantTokens
            .Where(IsNameToken)
            .Where(token => token.Range.Start.Line == position.Line)
            .Where(token => token.Range.Start.Character <= position.Character)
            .Where(token => position.Character <= token.Range.End.Character)
            .OrderByDescending(token => token.Range.Start.Offset)
            .Select(ToIdentifier)
            .FirstOrDefault();

    private VbaMemberAccessSyntax? TryGetMemberAccess(
        StatementSpan statement,
        VbaSyntaxPosition position)
    {
        var significant = statement.SignificantTokens;
        var nameIndex = -1;
        for (var index = 0; index < significant.Count; index++)
        {
            var token = significant[index];
            if (IsNameToken(token)
                && token.Range.Start.Line == position.Line
                && token.Range.Start.Character <= position.Character
                && position.Character <= token.Range.End.Character)
            {
                nameIndex = index;
            }
        }

        if (nameIndex < 0)
        {
            var previousIndex = -1;
            for (var index = 0; index < significant.Count; index++)
            {
                if (significant[index].Range.End.Offset <= position.Offset)
                {
                    previousIndex = index;
                }
            }

            if (previousIndex >= 0 && IsNameToken(significant[previousIndex]))
            {
                nameIndex = previousIndex;
            }
        }

        if (nameIndex >= 0)
        {
            var start = nameIndex;
            while (start >= 2 && IsDot(significant[start - 1]) && IsNameToken(significant[start - 2]))
            {
                start -= 2;
            }

            if (start > 0 && IsDot(significant[start - 1]))
            {
                start--;
            }

            var end = nameIndex;
            while (end + 2 < significant.Count && IsDot(significant[end + 1]) && IsNameToken(significant[end + 2]))
            {
                end += 2;
            }

            var hasTrailingDot = end + 1 < significant.Count && IsDot(significant[end + 1]);
            if (hasTrailingDot)
            {
                end++;
            }

            if (!significant.Skip(start).Take(end - start + 1).Any(IsDot))
            {
                return null;
            }

            var segments = significant
                .Skip(start)
                .Take(end - start + 1)
                .Where(IsNameToken)
                .Select(ToIdentifier)
                .ToArray();
            var targetIndex = significant
                .Skip(start)
                .Take(nameIndex - start)
                .Count(IsNameToken);
            return new VbaMemberAccessSyntax(
                segments,
                targetIndex,
                IsDot(significant[start]),
                hasTrailingDot,
                position.Offset > significant[nameIndex].Range.End.Offset,
                new VbaSyntaxRange(significant[start].Range.Start, significant[end].Range.End));
        }

        var anchorIndex = -1;
        for (var index = 0; index < significant.Count; index++)
        {
            if (significant[index].Range.End.Offset <= position.Offset)
            {
                anchorIndex = index;
            }
        }

        if (anchorIndex < 0 || !IsDot(significant[anchorIndex]))
        {
            return null;
        }

        var chainStart = anchorIndex;
        if (anchorIndex > 0 && IsNameToken(significant[anchorIndex - 1]))
        {
            chainStart = anchorIndex - 1;
            while (chainStart >= 2
                && IsDot(significant[chainStart - 1])
                && IsNameToken(significant[chainStart - 2]))
            {
                chainStart -= 2;
            }

            if (chainStart > 0 && IsDot(significant[chainStart - 1]))
            {
                chainStart--;
            }
        }

        var chainSegments = significant
            .Skip(chainStart)
            .Take(anchorIndex - chainStart + 1)
            .Where(IsNameToken)
            .Select(ToIdentifier)
            .ToArray();
        return new VbaMemberAccessSyntax(
            chainSegments,
            chainSegments.Length,
            IsDot(significant[chainStart]),
            true,
            position.Offset > significant[anchorIndex].Range.End.Offset,
            new VbaSyntaxRange(significant[chainStart].Range.Start, significant[anchorIndex].Range.End));
    }

    private static VbaPositionTypeReferenceSyntax? TryGetTypeReference(
        StatementSpan statement,
        VbaSyntaxPosition position)
    {
        var significant = statement.SignificantTokens;
        var markerIndex = -1;
        for (var index = 0; index < significant.Count; index++)
        {
            if (significant[index].Range.Start.Offset <= position.Offset
                && (IsKeyword(significant[index], "As") || IsKeyword(significant[index], "New")))
            {
                markerIndex = index;
            }
        }

        if (markerIndex < 0)
        {
            return null;
        }

        var marker = significant[markerIndex];
        var isNew = IsKeyword(marker, "New");
        var valueStart = markerIndex + 1;
        if (!isNew
            && valueStart < significant.Count
            && IsKeyword(significant[valueStart], "New"))
        {
            isNew = true;
            valueStart++;
        }

        VbaPositionIdentifierSyntax? qualifier = null;
        VbaPositionIdentifierSyntax? name = null;
        var consumedEnd = valueStart - 1;
        if (valueStart < significant.Count && IsNameToken(significant[valueStart]))
        {
            consumedEnd = valueStart;
            if (valueStart + 1 < significant.Count && IsDot(significant[valueStart + 1]))
            {
                qualifier = ToIdentifier(significant[valueStart]);
                consumedEnd = valueStart + 1;
                if (valueStart + 2 < significant.Count && IsNameToken(significant[valueStart + 2]))
                {
                    name = ToIdentifier(significant[valueStart + 2]);
                    consumedEnd = valueStart + 2;
                }
            }
            else
            {
                name = ToIdentifier(significant[valueStart]);
            }
        }

        if (significant
            .Skip(consumedEnd + 1)
            .Any(token => token.Range.Start.Offset < position.Offset))
        {
            return null;
        }

        var end = consumedEnd >= valueStart
            ? significant[consumedEnd].Range.End
            : marker.Range.End;
        return new VbaPositionTypeReferenceSyntax(
            qualifier,
            name,
            isNew,
            name is null,
            new VbaSyntaxRange(marker.Range.Start, end));
    }

    private VbaCallSiteSyntax? TryGetCallSite(
        StatementSpan statement,
        VbaSyntaxPosition position)
    {
        var significant = statement.SignificantTokens;
        var parsed = VbaCallSyntaxParser.TryParsePositionCall(sourceText, significant, position);
        if (parsed is null)
        {
            return null;
        }

        var callee = CreateChain(
            significant,
            parsed.CalleeStartIndex,
            parsed.CalleeEndIndex,
            parsed.CalleeEndIndex);
        if (callee is null)
        {
            return null;
        }

        return new VbaCallSiteSyntax(
            parsed.Form,
            callee,
            parsed.Arguments,
            parsed.ActiveArgumentIndex,
            parsed.ActiveNamedArgument,
            parsed.IsIncomplete);
    }

    private VbaCompletionSyntaxKind GetCompletionKind(
        VbaSyntaxPosition position,
        VbaMemberAccessSyntax? memberAccess,
        VbaPositionTypeReferenceSyntax? typeReference)
    {
        if (IsAfterLineContinuation(position))
        {
            return VbaCompletionSyntaxKind.None;
        }

        if (typeReference is not null)
        {
            return VbaCompletionSyntaxKind.TypeName;
        }

        if (memberAccess is not null
            && (memberAccess.TargetSegmentIndex > 0
                || memberAccess.IsLeadingDot
                || memberAccess.IsIncomplete))
        {
            return memberAccess.HasTrailingWhitespace
                ? VbaCompletionSyntaxKind.None
                : VbaCompletionSyntaxKind.Member;
        }

        return VbaCompletionSyntaxKind.General;
    }

    private bool IsAfterLineContinuation(VbaSyntaxPosition position)
    {
        var lineTokens = nonWhitespaceTokensByLine[position.Line];
        var low = 0;
        var high = lineTokens.Count - 1;
        var previousIndex = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (lineTokens[middle].Range.Start.Offset < position.Offset)
            {
                previousIndex = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return previousIndex >= 0
            && lineTokens[previousIndex].Kind == VbaTokenKind.LineContinuation;
    }

    private IReadOnlyList<VbaWithScopeSyntax> GetEnclosingWithScopes(int offset)
    {
        var low = 0;
        var high = withScopes.Count - 1;
        var candidate = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (withScopes[middle].StartOffset <= offset)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        var enclosing = new List<WithScopeSpan>();
        for (var index = candidate;
             index >= 0 && withScopePrefixMaximumEnds[index] > offset;
             index--)
        {
            if (offset < withScopes[index].EndOffset)
            {
                enclosing.Add(withScopes[index]);
            }
        }

        return enclosing
            .OrderBy(scope => scope.Depth)
            .Select(scope => new VbaWithScopeSyntax(scope.Receiver))
            .ToArray();
    }

    private static IReadOnlyList<int> BuildPrefixMaximumEnds(
        IReadOnlyList<WithScopeSpan> scopes)
    {
        var maximumEnds = new int[scopes.Count];
        var maximum = 0;
        for (var index = 0; index < scopes.Count; index++)
        {
            maximum = Math.Max(maximum, scopes[index].EndOffset);
            maximumEnds[index] = maximum;
        }

        return maximumEnds;
    }

    private int FindTokenIndex(int offset)
    {
        var low = 0;
        var high = tokens.Count - 1;
        var candidate = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (tokens[middle].Range.Start.Offset <= offset)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return candidate >= 0 && offset <= tokens[candidate].Range.End.Offset
            ? candidate
            : -1;
    }

    private StatementSpan FindStatement(int offset)
    {
        var low = 0;
        var high = statements.Count - 1;
        var candidate = 0;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (statements[middle].StartOffset <= offset)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return statements[candidate];
    }

    private static IReadOnlyList<StatementSpan> BuildStatements(
        int textLength,
        IReadOnlyList<VbaToken> tokens)
    {
        var statements = new List<StatementSpan>();
        var significant = new List<VbaToken>();
        var startOffset = 0;
        var continued = false;
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

                statements.Add(new StatementSpan(
                    startOffset,
                    token.Range.Start.Offset,
                    token.Range.End.Offset,
                    significant.ToArray()));
                significant.Clear();
                startOffset = token.Range.End.Offset;
                continue;
            }

            if (token.Kind == VbaTokenKind.Punctuation && token.Text == ":")
            {
                if (significant.Count > 0
                    && significant[0].Text.Equals("Rem", StringComparison.OrdinalIgnoreCase))
                {
                    significant.Add(token);
                    continue;
                }

                statements.Add(new StatementSpan(
                    startOffset,
                    token.Range.Start.Offset,
                    token.Range.End.Offset,
                    significant.ToArray()));
                significant.Clear();
                startOffset = token.Range.End.Offset;
                continued = false;
                continue;
            }

            if (token.Kind is not VbaTokenKind.Whitespace and not VbaTokenKind.Comment)
            {
                significant.Add(token);
            }
        }

        statements.Add(new StatementSpan(startOffset, textLength, textLength, significant.ToArray()));
        return statements;
    }

    private static IReadOnlyList<IReadOnlyList<VbaToken>> BuildNonWhitespaceTokensByLine(
        int lineCount,
        IReadOnlyList<VbaToken> tokens)
    {
        var lineTokens = Enumerable.Range(0, lineCount)
            .Select(_ => new List<VbaToken>())
            .ToArray();
        foreach (var token in tokens)
        {
            if (token.Range.Start.Line >= 0
                && token.Range.Start.Line < lineTokens.Length
                && token.Kind is not VbaTokenKind.Whitespace and not VbaTokenKind.NewLine)
            {
                lineTokens[token.Range.Start.Line].Add(token);
            }
        }

        return lineTokens
            .Select(tokensOnLine => (IReadOnlyList<VbaToken>)tokensOnLine.ToArray())
            .ToArray();
    }

    private static IReadOnlyList<WithScopeSpan> BuildWithScopes(
        int textLength,
        IReadOnlyList<StatementSpan> statements)
    {
        var completed = new List<WithScopeSpan>();
        var stack = new Stack<OpenWithScope>();
        foreach (var statement in statements)
        {
            var significant = statement.SignificantTokens;
            if (significant.Count >= 2
                && IsKeyword(significant[0], "End")
                && IsKeyword(significant[1], "With"))
            {
                if (stack.Count > 0)
                {
                    var scope = stack.Pop();
                    completed.Add(new WithScopeSpan(
                        scope.StartOffset,
                        statement.StartOffset,
                        scope.Depth,
                        scope.Receiver));
                }

                continue;
            }

            if (significant.Count > 0 && IsKeyword(significant[0], "With"))
            {
                var receiver = significant.Count > 1
                    ? CreateChain(significant, 1, significant.Count - 1, significant.Count - 1)
                    : null;
                stack.Push(new OpenWithScope(
                    statement.NextOffset,
                    stack.Count,
                    receiver));
            }
        }

        while (stack.Count > 0)
        {
            var scope = stack.Pop();
            completed.Add(new WithScopeSpan(
                scope.StartOffset,
                textLength + 1,
                scope.Depth,
                scope.Receiver));
        }

        return completed;
    }

    private static VbaMemberAccessSyntax? CreateChain(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end,
        int targetTokenIndex)
    {
        if (start < 0 || end < start || end >= tokens.Count)
        {
            return null;
        }

        var index = start;
        var leadingDot = IsDot(tokens[index]);
        if (leadingDot)
        {
            index++;
        }

        var segments = new List<VbaPositionIdentifierSyntax>();
        var targetSegmentIndex = -1;
        var expectName = true;
        for (; index <= end; index++)
        {
            var token = tokens[index];
            if (expectName)
            {
                if (!IsNameToken(token))
                {
                    return null;
                }

                if (index == targetTokenIndex)
                {
                    targetSegmentIndex = segments.Count;
                }

                segments.Add(ToIdentifier(token));
            }
            else if (!IsDot(token))
            {
                return null;
            }

            expectName = !expectName;
        }

        if (segments.Count == 0 || expectName || targetSegmentIndex < 0)
        {
            return null;
        }

        return new VbaMemberAccessSyntax(
            segments,
            targetSegmentIndex,
            leadingDot,
            false,
            false,
            new VbaSyntaxRange(tokens[start].Range.Start, tokens[end].Range.End));
    }

    private static bool IsNameToken(VbaToken token)
        => token.Kind is VbaTokenKind.Identifier or VbaTokenKind.Keyword;

    private static bool IsDot(VbaToken token)
        => IsPunctuation(token, ".");

    private static bool IsPunctuation(VbaToken token, string text)
        => token.Kind == VbaTokenKind.Punctuation && token.Text == text;

    private static bool IsKeyword(VbaToken token, string text)
        => token.Kind == VbaTokenKind.Keyword
            && token.Text.Equals(text, StringComparison.OrdinalIgnoreCase);

    private static VbaPositionIdentifierSyntax ToIdentifier(VbaToken token)
        => new(token.Text, token.Range, token.Kind == VbaTokenKind.Keyword);

    private sealed record StatementSpan(
        int StartOffset,
        int EndOffset,
        int NextOffset,
        IReadOnlyList<VbaToken> SignificantTokens);

    private sealed record OpenWithScope(
        int StartOffset,
        int Depth,
        VbaMemberAccessSyntax? Receiver);

    private sealed record WithScopeSpan(
        int StartOffset,
        int EndOffset,
        int Depth,
        VbaMemberAccessSyntax? Receiver);
}
