namespace VbaLanguageServer.Syntax;

/// <summary>
/// Parses token-derived call sites for complete syntax and editor-position queries.
/// </summary>
internal static class VbaCallSyntaxParser
{
    public static IReadOnlyList<VbaArgumentListSyntax> ParseCompleteArgumentLists(
        VbaSourceText sourceText,
        VbaTokenStream tokenStream,
        int codeStartLine)
    {
        var argumentLists = new List<VbaArgumentListSyntax>();
        foreach (var statement in BuildStatements(tokenStream.Tokens))
        {
            var significant = statement.SignificantTokens;
            if (significant.Count == 0 || significant[0].Range.Start.Line < codeStartLine)
            {
                continue;
            }

            for (var openIndex = 0; openIndex < significant.Count; openIndex++)
            {
                if (!IsPunctuation(significant[openIndex], "(")
                    || !TryGetCalleeBefore(significant, openIndex, out var calleeStart, out var calleeEnd))
                {
                    continue;
                }

                var closeIndex = FindMatchingParenthesis(significant, openIndex);
                if (closeIndex < 0
                    || IsExcludedParenthesizedCall(significant, openIndex, closeIndex, calleeStart))
                {
                    continue;
                }

                var arguments = ParseArguments(
                    sourceText,
                    significant,
                    openIndex + 1,
                    closeIndex,
                    significant[openIndex].Range.End,
                    significant[closeIndex].Range.Start,
                    includeSingleEmptyArgument: false);
                argumentLists.Add(new VbaArgumentListSyntax(
                    GetCalleeText(significant, calleeStart, calleeEnd),
                    arguments.Select(argument => argument.Syntax).ToArray(),
                    new VbaSyntaxRange(
                        significant[openIndex].Range.Start,
                        significant[closeIndex].Range.End),
                    statement.IsContinued));
            }

            AddCompleteStatementArgumentList(sourceText, significant, statement.IsContinued, argumentLists);
        }

        return argumentLists;
    }

    public static VbaParsedPositionCall? TryParsePositionCall(
        VbaSourceText sourceText,
        IReadOnlyList<VbaToken> significant,
        VbaSyntaxPosition position)
    {
        var openStack = new Stack<int>();
        for (var index = 0; index < significant.Count; index++)
        {
            var token = significant[index];
            if (token.Range.Start.Offset >= position.Offset)
            {
                break;
            }

            if (IsPunctuation(token, "("))
            {
                openStack.Push(index);
            }
            else if (IsPunctuation(token, ")") && openStack.Count > 0)
            {
                openStack.Pop();
            }
        }

        if (openStack.Count > 0)
        {
            var openIndex = openStack.Peek();
            if (!TryGetCalleeBefore(significant, openIndex, out var calleeStart, out var calleeEnd))
            {
                return null;
            }

            var closeIndex = FindMatchingParenthesis(significant, openIndex);
            if (IsExcludedParenthesizedCall(significant, openIndex, closeIndex, calleeStart))
            {
                return null;
            }

            var endIndex = FindPositionEndIndex(significant, openIndex + 1, position.Offset);
            var arguments = ParseArguments(
                sourceText,
                significant,
                openIndex + 1,
                endIndex,
                significant[openIndex].Range.End,
                position,
                includeSingleEmptyArgument: true);
            var completeArguments = closeIndex >= 0
                ? ParseArguments(
                    sourceText,
                    significant,
                    openIndex + 1,
                    closeIndex,
                    significant[openIndex].Range.End,
                    significant[closeIndex].Range.Start,
                    includeSingleEmptyArgument: false)
                : arguments;
            return CreatePositionCall(
                VbaCallSyntaxForm.Parenthesized,
                calleeStart,
                calleeEnd,
                arguments,
                GetActiveNamedArgument(arguments, completeArguments, position),
                isIncomplete: true);
        }

        return TryParseStatementPositionCall(sourceText, significant, position);
    }

    private static void AddCompleteStatementArgumentList(
        VbaSourceText sourceText,
        IReadOnlyList<VbaToken> significant,
        bool isContinued,
        ICollection<VbaArgumentListSyntax> argumentLists)
    {
        if (!TryGetStatementCallee(significant, out var calleeStart, out var calleeEnd)
            || calleeEnd + 1 >= significant.Count
            || significant[calleeEnd + 1].Text is "=" or "("
            || IsExcludedStatementFormCall(significant, calleeStart, calleeEnd))
        {
            return;
        }

        var listEnd = significant[^1].Range.End;
        var arguments = ParseArguments(
            sourceText,
            significant,
            calleeEnd + 1,
            significant.Count,
            significant[calleeEnd].Range.End,
            listEnd,
            includeSingleEmptyArgument: false);
        if (arguments.Count == 0)
        {
            return;
        }

        argumentLists.Add(new VbaArgumentListSyntax(
            GetCalleeText(significant, calleeStart, calleeEnd),
            arguments.Select(argument => argument.Syntax).ToArray(),
            new VbaSyntaxRange(significant[calleeEnd].Range.End, listEnd),
            isContinued));
    }

    private static VbaParsedPositionCall? TryParseStatementPositionCall(
        VbaSourceText sourceText,
        IReadOnlyList<VbaToken> significant,
        VbaSyntaxPosition position)
    {
        if (!TryGetStatementCallee(significant, out var calleeStart, out var calleeEnd)
            || position.Offset <= significant[calleeEnd].Range.End.Offset
            || !HasWhitespaceBetween(
                sourceText.Text,
                significant[calleeEnd].Range.End.Offset,
                position.Offset)
            || (calleeEnd + 1 < significant.Count
                && significant[calleeEnd + 1].Text is "=" or "(")
            || IsExcludedStatementFormCall(significant, calleeStart, calleeEnd))
        {
            return null;
        }

        var endIndex = FindPositionEndIndex(significant, calleeEnd + 1, position.Offset);
        var arguments = ParseArguments(
            sourceText,
            significant,
            calleeEnd + 1,
            endIndex,
            significant[calleeEnd].Range.End,
            position,
            includeSingleEmptyArgument: true);
        var completeArguments = ParseArguments(
            sourceText,
            significant,
            calleeEnd + 1,
            significant.Count,
            significant[calleeEnd].Range.End,
            significant[^1].Range.End,
            includeSingleEmptyArgument: false);
        return CreatePositionCall(
            VbaCallSyntaxForm.Statement,
            calleeStart,
            calleeEnd,
            arguments,
            GetActiveNamedArgument(arguments, completeArguments, position),
            isIncomplete: false);
    }

    private static VbaParsedPositionCall CreatePositionCall(
        VbaCallSyntaxForm form,
        int calleeStart,
        int calleeEnd,
        IReadOnlyList<ParsedArgument> arguments,
        string? activeNamedArgument,
        bool isIncomplete)
    {
        var positionArguments = arguments
            .Select((argument, index) => new VbaCallArgumentSyntax(
                index,
                argument.Syntax.Name,
                argument.Syntax.Kind == VbaArgumentKind.Omitted,
                argument.PositionRange))
            .ToArray();
        return new VbaParsedPositionCall(
            form,
            calleeStart,
            calleeEnd,
            positionArguments,
            positionArguments[^1].Index,
            activeNamedArgument,
            isIncomplete);
    }

    private static string? GetActiveNamedArgument(
        IReadOnlyList<ParsedArgument> positionArguments,
        IReadOnlyList<ParsedArgument> completeArguments,
        VbaSyntaxPosition position)
    {
        var activeIndex = positionArguments.Count - 1;
        var parsedName = positionArguments[activeIndex].Syntax.Name;
        if (parsedName is not null || activeIndex >= completeArguments.Count)
        {
            return parsedName;
        }

        var completeArgument = completeArguments[activeIndex].Syntax;
        return completeArgument.NameRange is not null
            && position.Offset >= completeArgument.NameRange.Start.Offset
                ? completeArgument.Name
                : null;
    }

    private static IReadOnlyList<ParsedArgument> ParseArguments(
        VbaSourceText sourceText,
        IReadOnlyList<VbaToken> tokens,
        int startIndex,
        int endIndex,
        VbaSyntaxPosition listStart,
        VbaSyntaxPosition listEnd,
        bool includeSingleEmptyArgument)
    {
        var arguments = new List<ParsedArgument>();
        var segmentStartIndex = startIndex;
        var segmentStart = listStart;
        VbaSyntaxRange? previousSeparatorRange = null;
        var depth = 0;
        for (var index = startIndex; index < endIndex; index++)
        {
            var token = tokens[index];
            if (IsPunctuation(token, "("))
            {
                depth++;
            }
            else if (IsPunctuation(token, ")") && depth > 0)
            {
                depth--;
            }

            if (depth != 0 || !IsPunctuation(token, ","))
            {
                continue;
            }

            arguments.Add(CreateArgument(
                sourceText,
                tokens,
                segmentStartIndex,
                index,
                segmentStart,
                token.Range.Start,
                token.Range));
            segmentStartIndex = index + 1;
            segmentStart = token.Range.End;
            previousSeparatorRange = token.Range;
        }

        if (segmentStartIndex < endIndex
            || previousSeparatorRange is not null
            || includeSingleEmptyArgument)
        {
            arguments.Add(CreateArgument(
                sourceText,
                tokens,
                segmentStartIndex,
                endIndex,
                segmentStart,
                listEnd,
                segmentStartIndex == endIndex ? previousSeparatorRange : null));
        }

        return arguments;
    }

    private static ParsedArgument CreateArgument(
        VbaSourceText sourceText,
        IReadOnlyList<VbaToken> tokens,
        int startIndex,
        int endIndex,
        VbaSyntaxPosition rawStart,
        VbaSyntaxPosition rawEnd,
        VbaSyntaxRange? omittedMarkerRange)
    {
        var positionRange = new VbaSyntaxRange(rawStart, rawEnd);
        if (startIndex >= endIndex)
        {
            return new ParsedArgument(
                new VbaArgumentSyntax(
                    VbaArgumentKind.Omitted,
                    "",
                    omittedMarkerRange ?? positionRange),
                positionRange);
        }

        var range = new VbaSyntaxRange(tokens[startIndex].Range.Start, tokens[endIndex - 1].Range.End);
        var text = GetLogicalSourceText(sourceText, range);
        if (endIndex - startIndex >= 2
            && IsNameToken(tokens[startIndex])
            && tokens[startIndex + 1].Kind == VbaTokenKind.Operator
            && tokens[startIndex + 1].Text == ":=")
        {
            var valueRange = startIndex + 2 < endIndex
                ? new VbaSyntaxRange(tokens[startIndex + 2].Range.Start, tokens[endIndex - 1].Range.End)
                : null;
            return new ParsedArgument(
                new VbaArgumentSyntax(
                    VbaArgumentKind.Named,
                    text,
                    range,
                    tokens[startIndex].Text,
                    tokens[startIndex].Range,
                    valueRange is null ? "" : GetLogicalSourceText(sourceText, valueRange),
                    valueRange),
                positionRange);
        }

        return new ParsedArgument(
            new VbaArgumentSyntax(
                VbaArgumentKind.Positional,
                text,
                range,
                ValueText: text,
                ValueRange: range),
            positionRange);
    }

    private static string GetLogicalSourceText(VbaSourceText sourceText, VbaSyntaxRange range)
    {
        if (range.Start.Line == range.End.Line)
        {
            return sourceText.Text[range.Start.Offset..range.End.Offset];
        }

        var text = new System.Text.StringBuilder();
        for (var lineIndex = range.Start.Line; lineIndex <= range.End.Line; lineIndex++)
        {
            var line = sourceText.Lines[lineIndex];
            var startCharacter = lineIndex == range.Start.Line ? range.Start.Character : 0;
            var endCharacter = lineIndex == range.End.Line ? range.End.Character : line.Text.Length;
            var part = line.Text[startCharacter..endCharacter];
            var codeText = VbaSourceText.StripApostropheComment(part);
            var hasContinuation = VbaSourceText.HasLineContinuation(codeText);
            text.Append(hasContinuation ? VbaSourceText.RemoveLineContinuation(codeText) : codeText);
            if (hasContinuation)
            {
                text.Append(' ');
            }
        }

        return text.ToString();
    }

    private static IReadOnlyList<CallStatement> BuildStatements(IReadOnlyList<VbaToken> tokens)
    {
        var statements = new List<CallStatement>();
        var significant = new List<VbaToken>();
        var continued = false;
        var statementWasContinued = false;
        foreach (var token in tokens)
        {
            if (token.Kind == VbaTokenKind.LineContinuation)
            {
                continued = true;
                statementWasContinued = true;
                continue;
            }

            if (token.Kind == VbaTokenKind.NewLine)
            {
                if (continued)
                {
                    continued = false;
                    continue;
                }

                statements.Add(new CallStatement(significant.ToArray(), statementWasContinued));
                significant.Clear();
                statementWasContinued = false;
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

                statements.Add(new CallStatement(significant.ToArray(), statementWasContinued));
                significant.Clear();
                continued = false;
                statementWasContinued = false;
                continue;
            }

            if (token.Kind is not VbaTokenKind.Whitespace and not VbaTokenKind.Comment)
            {
                significant.Add(token);
            }
        }

        statements.Add(new CallStatement(significant.ToArray(), statementWasContinued));
        return statements;
    }

    private static int FindMatchingParenthesis(IReadOnlyList<VbaToken> tokens, int openIndex)
    {
        var depth = 0;
        for (var index = openIndex; index < tokens.Count; index++)
        {
            if (IsPunctuation(tokens[index], "("))
            {
                depth++;
            }
            else if (IsPunctuation(tokens[index], ")"))
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private static bool TryGetCalleeBefore(
        IReadOnlyList<VbaToken> tokens,
        int openIndex,
        out int start,
        out int end)
    {
        end = openIndex - 1;
        start = end;
        if (end < 0 || !IsNameToken(tokens[end]))
        {
            return false;
        }

        while (start >= 2 && IsDot(tokens[start - 1]) && IsNameToken(tokens[start - 2]))
        {
            start -= 2;
        }

        if (start > 0 && IsDot(tokens[start - 1]))
        {
            start--;
        }

        return true;
    }

    private static bool IsExcludedParenthesizedCall(
        IReadOnlyList<VbaToken> tokens,
        int openIndex,
        int closeIndex,
        int calleeStart)
    {
        if (IsCallableDeclaration(tokens)
            || IsDeclaredArrayBounds(tokens, openIndex)
            || IsRemComment(tokens))
        {
            return true;
        }

        return calleeStart == 0
            && closeIndex >= openIndex
            && closeIndex + 1 < tokens.Count
            && TextEquals(tokens[closeIndex + 1], "As");
    }

    private static bool IsCallableDeclaration(IReadOnlyList<VbaToken> tokens)
    {
        var index = 0;
        while (index < tokens.Count
            && (TextEquals(tokens[index], "Public")
                || TextEquals(tokens[index], "Private")
                || TextEquals(tokens[index], "Friend")
                || TextEquals(tokens[index], "Static")))
        {
            index++;
        }

        if (index >= tokens.Count)
        {
            return false;
        }

        return TextEquals(tokens[index], "Sub")
            || TextEquals(tokens[index], "Function")
            || TextEquals(tokens[index], "Event")
            || TextEquals(tokens[index], "Declare")
            || (TextEquals(tokens[index], "Property")
                && index + 1 < tokens.Count
                && (TextEquals(tokens[index + 1], "Get")
                    || TextEquals(tokens[index + 1], "Let")
                    || TextEquals(tokens[index + 1], "Set")));
    }

    private static bool IsDeclaredArrayStatement(IReadOnlyList<VbaToken> tokens)
        => tokens.Count > 0
            && (TextEquals(tokens[0], "Dim")
                || TextEquals(tokens[0], "Static")
                || TextEquals(tokens[0], "Private")
                || TextEquals(tokens[0], "Public")
                || TextEquals(tokens[0], "Friend")
                || TextEquals(tokens[0], "Global")
                || TextEquals(tokens[0], "ReDim"));

    private static bool IsDeclaredArrayBounds(
        IReadOnlyList<VbaToken> tokens,
        int openIndex)
    {
        if (!IsDeclaredArrayStatement(tokens))
        {
            return false;
        }

        var depth = 0;
        for (var index = 0; index < openIndex; index++)
        {
            if (IsPunctuation(tokens[index], "("))
            {
                depth++;
            }
            else if (IsPunctuation(tokens[index], ")") && depth > 0)
            {
                depth--;
            }
        }

        return depth == 0;
    }

    private static bool IsExcludedStatementFormCall(
        IReadOnlyList<VbaToken> tokens,
        int calleeStart,
        int calleeEnd)
        => (calleeStart == 0
            && calleeEnd + 1 < tokens.Count
            && TextEquals(tokens[calleeEnd + 1], "As"))
            || (calleeStart == 0
                && tokens.Count > 0
                && (TextEquals(tokens[0], "ReDim")
                    || TextEquals(tokens[0], "Preserve")
                    || TextEquals(tokens[0], "Rem")));

    private static bool IsRemComment(IReadOnlyList<VbaToken> tokens)
        => tokens.Count > 0 && TextEquals(tokens[0], "Rem");

    private static bool TryGetStatementCallee(
        IReadOnlyList<VbaToken> tokens,
        out int start,
        out int end)
    {
        start = 0;
        end = -1;
        if (tokens.Count == 0)
        {
            return false;
        }

        if (IsDot(tokens[start]))
        {
            start++;
        }

        if (start >= tokens.Count || tokens[start].Kind != VbaTokenKind.Identifier)
        {
            return false;
        }

        end = start;
        while (end + 2 < tokens.Count && IsDot(tokens[end + 1]) && IsNameToken(tokens[end + 2]))
        {
            end += 2;
        }

        if (start > 0)
        {
            start--;
        }

        return true;
    }

    private static string GetCalleeText(IReadOnlyList<VbaToken> tokens, int start, int end)
        => string.Concat(tokens.Skip(start).Take(end - start + 1).Select(token => token.Text));

    private static int FindPositionEndIndex(
        IReadOnlyList<VbaToken> tokens,
        int startIndex,
        int positionOffset)
    {
        var index = startIndex;
        while (index < tokens.Count && tokens[index].Range.Start.Offset < positionOffset)
        {
            index++;
        }

        return index;
    }

    private static bool HasWhitespaceBetween(string source, int startOffset, int endOffset)
    {
        for (var offset = startOffset; offset < endOffset && offset < source.Length; offset++)
        {
            if (char.IsWhiteSpace(source[offset]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNameToken(VbaToken token)
        => token.Kind is VbaTokenKind.Identifier or VbaTokenKind.Keyword;

    private static bool IsDot(VbaToken token)
        => IsPunctuation(token, ".");

    private static bool IsPunctuation(VbaToken token, string text)
        => token.Kind == VbaTokenKind.Punctuation && token.Text == text;

    private static bool TextEquals(VbaToken token, string text)
        => token.Text.Equals(text, StringComparison.OrdinalIgnoreCase);

    private sealed record ParsedArgument(VbaArgumentSyntax Syntax, VbaSyntaxRange PositionRange);

    private sealed record CallStatement(
        IReadOnlyList<VbaToken> SignificantTokens,
        bool IsContinued);
}

internal sealed record VbaParsedPositionCall(
    VbaCallSyntaxForm Form,
    int CalleeStartIndex,
    int CalleeEndIndex,
    IReadOnlyList<VbaCallArgumentSyntax> Arguments,
    int ActiveArgumentIndex,
    string? ActiveNamedArgument,
    bool IsIncomplete);
