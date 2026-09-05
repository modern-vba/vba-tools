namespace VbaTools.Syntax;

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
        string? declarationBlockKind = null;
        foreach (var statement in BuildStatements(tokenStream.Tokens))
        {
            var significant = statement.SignificantTokens;
            if (significant.Count == 0
                || significant[0].Range.Start.Line < codeStartLine)
            {
                continue;
            }

            if (declarationBlockKind is not null)
            {
                if (IsDeclarationBlockEnd(significant, declarationBlockKind))
                {
                    declarationBlockKind = null;
                }

                continue;
            }

            if (TryGetDeclarationBlockKind(significant, out declarationBlockKind)
                || IsLabelDeclaration(statement))
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
                    || IsExcludedParenthesizedCall(significant, openIndex, closeIndex, calleeStart)
                    || IsWhitespaceSeparatedStatementArgument(
                        sourceText,
                        significant,
                        openIndex,
                        calleeStart,
                        calleeEnd))
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
                    statement.IsContinued,
                    new VbaSyntaxRange(
                        significant[calleeStart].Range.Start,
                        significant[calleeEnd].Range.End),
                    VbaCallSyntaxForm.Parenthesized,
                    IsIncompleteArgumentList(
                        arguments,
                        significant,
                        openIndex + 1,
                        closeIndex)));
            }

            AddCompleteStatementArgumentList(sourceText, significant, statement.IsContinued, argumentLists);
            AddCompleteUnindexedPropertyAssignmentArgumentList(
                significant,
                statement.IsContinued,
                argumentLists);
            AddCompleteBareValueReadArgumentLists(
                sourceText,
                significant,
                statement.IsContinued,
                argumentLists);
        }

        return argumentLists;
    }

    private static void AddCompleteBareValueReadArgumentLists(
        VbaSourceText sourceText,
        IReadOnlyList<VbaToken> significant,
        bool isContinued,
        ICollection<VbaArgumentListSyntax> argumentLists)
    {
        if (IsCallableDeclaration(significant)
            || IsNonExecutableDeclaration(significant))
        {
            return;
        }

        var hasStatementCallee = TryGetStatementCallee(
            significant,
            out var statementCalleeStart,
            out var statementCalleeEnd);
        for (var index = 0; index < significant.Count; index++)
        {
            var calleeStart = index;
            var nameStart = calleeStart;
            if (IsDot(significant[nameStart]))
            {
                nameStart++;
            }

            if (nameStart >= significant.Count
                || !VbaLanguageVocabulary.CanBeBareCallTarget(
                    significant[nameStart].Text))
            {
                continue;
            }

            var calleeEnd = nameStart;
            while (calleeEnd + 2 < significant.Count
                && IsDot(significant[calleeEnd + 1])
                && IsNameToken(significant[calleeEnd + 2]))
            {
                calleeEnd += 2;
            }

            index = calleeEnd;
            if (calleeStart > 0
                    && TextEquals(significant[calleeStart - 1], "AddressOf")
                || hasStatementCallee
                    && calleeStart == statementCalleeStart
                    && calleeEnd == statementCalleeEnd)
            {
                continue;
            }

            for (var candidateEnd = nameStart;
                 candidateEnd <= calleeEnd;
                 candidateEnd += 2)
            {
                if (IsAssignmentTarget(significant, calleeStart, candidateEnd)
                    || IsLabelReference(significant, calleeStart, candidateEnd)
                    || candidateEnd + 1 < significant.Count
                        && significant[candidateEnd + 1].Kind == VbaTokenKind.Operator
                        && significant[candidateEnd + 1].Text == ":="
                    || candidateEnd + 1 < significant.Count
                        && IsPunctuation(significant[candidateEnd + 1], "("))
                {
                    continue;
                }

                var calleeRange = new VbaSyntaxRange(
                    significant[calleeStart].Range.Start,
                    significant[candidateEnd].Range.End);
                if (argumentLists.Any(argumentList => argumentList.CalleeRange == calleeRange))
                {
                    continue;
                }

                argumentLists.Add(new VbaArgumentListSyntax(
                    GetCalleeText(significant, calleeStart, candidateEnd),
                    [],
                    new VbaSyntaxRange(calleeRange.End, calleeRange.End),
                    isContinued,
                    calleeRange,
                    VbaCallSyntaxForm.BareValueRead));
            }
        }
    }

    private static bool IsLabelReference(
        IReadOnlyList<VbaToken> tokens,
        int calleeStart,
        int calleeEnd)
    {
        if (calleeStart != calleeEnd || calleeStart == 0)
        {
            return false;
        }

        if (TextEquals(tokens[calleeStart - 1], "GoTo")
            || TextEquals(tokens[calleeStart - 1], "GoSub")
            || TextEquals(tokens[calleeStart - 1], "Resume"))
        {
            return true;
        }

        if (!TextEquals(tokens[0], "On"))
        {
            return false;
        }

        for (var index = calleeStart - 1; index > 0; index--)
        {
            if (TextEquals(tokens[index], "GoTo")
                || TextEquals(tokens[index], "GoSub"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAssignmentTarget(
        IReadOnlyList<VbaToken> tokens,
        int calleeStart,
        int calleeEnd)
    {
        var depth = 0;
        var targetStart = 0;
        for (var index = 0; index < tokens.Count; index++)
        {
            if (IsPunctuation(tokens[index], "("))
            {
                depth++;
            }
            else if (IsPunctuation(tokens[index], ")") && depth > 0)
            {
                depth--;
            }
            else if (depth == 0 && (TextEquals(tokens[index], "Then")
                || TextEquals(tokens[index], "Else")))
            {
                targetStart = index + 1;
            }
            else if (depth == 0
                && tokens[index].Kind == VbaTokenKind.Operator
                && tokens[index].Text == "=")
            {
                var effectiveTargetStart = targetStart;
                if (effectiveTargetStart < index
                    && (TextEquals(tokens[effectiveTargetStart], "Let")
                        || TextEquals(tokens[effectiveTargetStart], "Set")))
                {
                    effectiveTargetStart++;
                }

                if (effectiveTargetStart == calleeStart && index - 1 == calleeEnd)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void AddCompleteUnindexedPropertyAssignmentArgumentList(
        IReadOnlyList<VbaToken> significant,
        bool isContinued,
        ICollection<VbaArgumentListSyntax> argumentLists)
    {
        if (IsCallableDeclaration(significant)
            || !TryGetUnindexedPropertyAssignmentCallee(
                significant,
                out var calleeStart,
                out var calleeEnd))
        {
            return;
        }

        var calleeRange = new VbaSyntaxRange(
            significant[calleeStart].Range.Start,
            significant[calleeEnd].Range.End);
        argumentLists.Add(new VbaArgumentListSyntax(
            GetCalleeText(significant, calleeStart, calleeEnd),
            [],
            new VbaSyntaxRange(calleeRange.End, calleeRange.End),
            isContinued,
            calleeRange,
            VbaCallSyntaxForm.PropertyAssignment));
    }

    private static bool TryGetUnindexedPropertyAssignmentCallee(
        IReadOnlyList<VbaToken> tokens,
        out int start,
        out int end)
    {
        start = 0;
        end = -1;
        var depth = 0;
        var assignmentIndex = -1;
        for (var index = 0; index < tokens.Count; index++)
        {
            if (IsPunctuation(tokens[index], "("))
            {
                depth++;
            }
            else if (IsPunctuation(tokens[index], ")") && depth > 0)
            {
                depth--;
            }
            else if (depth == 0
                && tokens[index].Kind == VbaTokenKind.Operator
                && tokens[index].Text == "=")
            {
                assignmentIndex = index;
                break;
            }
        }

        if (assignmentIndex <= 0 || assignmentIndex + 1 >= tokens.Count)
        {
            return false;
        }

        if (TextEquals(tokens[start], "Let") || TextEquals(tokens[start], "Set"))
        {
            start++;
        }

        var isLeadingDot = start < assignmentIndex && IsDot(tokens[start]);
        if (isLeadingDot)
        {
            start++;
        }

        if (start >= assignmentIndex
            || !VbaLanguageVocabulary.CanBeBareCallTarget(tokens[start].Text))
        {
            return false;
        }

        end = start;
        while (end + 2 < assignmentIndex
            && IsDot(tokens[end + 1])
            && IsNameToken(tokens[end + 2]))
        {
            end += 2;
        }

        if (end != assignmentIndex - 1)
        {
            return false;
        }

        if (isLeadingDot)
        {
            start--;
        }

        return true;
    }

    private static bool TryGetBareValueReadCallee(
        VbaSourceText sourceText,
        IReadOnlyList<VbaToken> tokens,
        out int start,
        out int end)
    {
        start = -1;
        end = -1;
        var depth = 0;
        var assignmentIndex = -1;
        for (var index = 0; index < tokens.Count; index++)
        {
            if (IsPunctuation(tokens[index], "("))
            {
                depth++;
            }
            else if (IsPunctuation(tokens[index], ")") && depth > 0)
            {
                depth--;
            }
            else if (depth == 0
                && tokens[index].Kind == VbaTokenKind.Operator
                && tokens[index].Text == "=")
            {
                assignmentIndex = index;
            }
        }

        if (assignmentIndex >= 0)
        {
            return TryGetExactDottedCallee(
                tokens,
                assignmentIndex + 1,
                tokens.Count,
                out start,
                out end);
        }

        if (tokens.Count >= 3
            && TextEquals(tokens[0], "If")
            && TextEquals(tokens[^1], "Then"))
        {
            return TryGetExactDottedCallee(
                tokens,
                1,
                tokens.Count - 1,
                out start,
                out end);
        }

        var hasDebugPrintPrefix = tokens.Count >= 3
            && TextEquals(tokens[0], "Debug")
            && IsDot(tokens[1])
            && TextEquals(tokens[2], "Print");
        var statementCalleeEnd = hasDebugPrintPrefix
            ? 2
            : TryGetStatementCallee(tokens, out _, out var parsedStatementCalleeEnd)
                ? parsedStatementCalleeEnd
                : -1;
        if (statementCalleeEnd < 0
            || statementCalleeEnd + 1 >= tokens.Count
            || !HasWhitespaceBetween(
                sourceText.Text,
                tokens[statementCalleeEnd].Range.End.Offset,
                tokens[statementCalleeEnd + 1].Range.Start.Offset))
        {
            return false;
        }

        return TryGetExactDottedCallee(
            tokens,
            statementCalleeEnd + 1,
            tokens.Count,
            out start,
            out end);
    }

    private static bool TryGetExactDottedCallee(
        IReadOnlyList<VbaToken> tokens,
        int startIndex,
        int endIndex,
        out int start,
        out int end)
    {
        start = startIndex;
        end = endIndex - 1;
        if (start >= endIndex)
        {
            return false;
        }

        var isLeadingDot = IsDot(tokens[start]);
        if (isLeadingDot)
        {
            start++;
        }

        if (start >= endIndex
            || !VbaLanguageVocabulary.CanBeBareCallTarget(tokens[start].Text))
        {
            return false;
        }

        var candidateEnd = start;
        while (candidateEnd + 2 < endIndex
            && IsDot(tokens[candidateEnd + 1])
            && IsNameToken(tokens[candidateEnd + 2]))
        {
            candidateEnd += 2;
        }

        if (candidateEnd != endIndex - 1)
        {
            return false;
        }

        end = candidateEnd;
        if (isLeadingDot)
        {
            start--;
        }

        return true;
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

        while (openStack.TryPop(out var openIndex))
        {
            if (!TryGetCalleeBefore(significant, openIndex, out var calleeStart, out var calleeEnd))
            {
                continue;
            }

            var closeIndex = FindMatchingParenthesis(significant, openIndex);
            if (IsExcludedParenthesizedCall(significant, openIndex, closeIndex, calleeStart)
                || IsWhitespaceSeparatedStatementArgument(
                    sourceText,
                    significant,
                    openIndex,
                    calleeStart,
                    calleeEnd))
            {
                continue;
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
                completeArguments,
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
            || IsExcludedStatementFormCall(significant, calleeStart, calleeEnd))
        {
            return;
        }

        var calleeRange = new VbaSyntaxRange(
            significant[calleeStart].Range.Start,
            significant[calleeEnd].Range.End);
        if (calleeEnd + 1 >= significant.Count)
        {
            argumentLists.Add(new VbaArgumentListSyntax(
                GetCalleeText(significant, calleeStart, calleeEnd),
                [],
                new VbaSyntaxRange(
                    significant[calleeEnd].Range.End,
                    significant[calleeEnd].Range.End),
                isContinued,
                calleeRange,
                VbaCallSyntaxForm.Statement));
            return;
        }

        if (significant[calleeEnd + 1].Text == "="
            || significant[calleeEnd + 1].Text == "(" && calleeStart > 0
            || !HasWhitespaceBetween(
                sourceText.Text,
                significant[calleeEnd].Range.End.Offset,
                significant[calleeEnd + 1].Range.Start.Offset))
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
            isContinued,
            calleeRange,
            VbaCallSyntaxForm.Statement,
            IsIncompleteArgumentList(arguments, significant, calleeEnd + 1)));
    }

    private static bool IsIncompleteArgumentList(
        IReadOnlyList<ParsedArgument> arguments,
        IReadOnlyList<VbaToken> tokens,
        int startIndex,
        int? endIndex = null)
    {
        var end = endIndex ?? tokens.Count;
        var depth = 0;
        for (var index = startIndex; index < end; index++)
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

        if (depth > 0
            || arguments.Any(argument => argument.Syntax.Kind == VbaArgumentKind.Named
                && string.IsNullOrWhiteSpace(argument.Syntax.ValueText))
            || arguments.Any(argument => IsIncompleteArgumentExpression(
                argument.Syntax,
                tokens,
                startIndex,
                end)))
        {
            return true;
        }

        return end > startIndex
            && (IsIncompleteExpressionOperator(tokens[end - 1])
                || IsPunctuation(tokens[end - 1], "."));
    }

    private static bool IsIncompleteArgumentExpression(
        VbaArgumentSyntax argument,
        IReadOnlyList<VbaToken> tokens,
        int startIndex,
        int endIndex)
    {
        if (argument.Kind == VbaArgumentKind.Omitted)
        {
            return false;
        }

        var range = argument.ValueRange ?? argument.Range;
        var expressionTokens = tokens
            .Skip(startIndex)
            .Take(endIndex - startIndex)
            .Where(token => range.Start.Offset <= token.Range.Start.Offset
                && token.Range.End.Offset <= range.End.Offset)
            .ToArray();
        if (expressionTokens.Length == 0)
        {
            return false;
        }

        var depth = 0;
        for (var index = 0; index < expressionTokens.Length; index++)
        {
            if (IsPunctuation(expressionTokens[index], "("))
            {
                depth++;
            }
            else if (IsPunctuation(expressionTokens[index], ")"))
            {
                if (index > 0
                    && IsIncompleteArgumentTerminal(expressionTokens[index - 1]))
                {
                    return true;
                }

                if (depth > 0)
                {
                    depth--;
                }
            }
            else if (IsPunctuation(expressionTokens[index], ",")
                && index > 0
                && IsIncompleteArgumentTerminal(expressionTokens[index - 1]))
            {
                return true;
            }
        }

        return depth > 0
            || IsIncompleteArgumentTerminal(expressionTokens[^1]);
    }

    private static bool IsIncompleteArgumentTerminal(VbaToken token)
        => IsIncompleteExpressionOperator(token)
            || IsPunctuation(token, ".")
            || token.Kind == VbaTokenKind.Operator && token.Text == ":=";

    private static bool IsIncompleteExpressionOperator(VbaToken token)
        => token.Kind == VbaTokenKind.Operator
            && token.Text.ToUpperInvariant() is "+" or "-" or "*" or "/" or "\\"
                or "^" or "=" or "<>" or "<" or ">" or "<=" or ">="
                or "AND" or "OR" or "XOR" or "EQV" or "IMP" or "LIKE"
                or "IS" or "MOD" or "NOT";

    private static VbaParsedPositionCall? TryParseStatementPositionCall(
        VbaSourceText sourceText,
        IReadOnlyList<VbaToken> significant,
        VbaSyntaxPosition position)
    {
        if (!TryGetStatementCallee(significant, out var calleeStart, out var calleeEnd))
        {
            return null;
        }

        var hasWhitespaceSeparatedParenthesizedArgument = calleeEnd + 1 < significant.Count
            && significant[calleeEnd + 1].Text == "("
            && IsWhitespaceSeparatedStatementArgument(
                sourceText,
                significant,
                calleeEnd + 1,
                calleeStart,
                calleeEnd);
        if (position.Offset <= significant[calleeEnd].Range.End.Offset
            || calleeStart > 0
            || !HasWhitespaceBetween(
                sourceText.Text,
                significant[calleeEnd].Range.End.Offset,
                position.Offset)
            || calleeEnd + 1 < significant.Count
                && (significant[calleeEnd + 1].Text == "="
                    || significant[calleeEnd + 1].Text == "("
                        && !hasWhitespaceSeparatedParenthesizedArgument)
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
        IReadOnlyList<ParsedArgument> positionArguments = arguments;
        if (hasWhitespaceSeparatedParenthesizedArgument
            && arguments.Count > 0
            && completeArguments.Count >= arguments.Count)
        {
            var activeIndex = arguments.Count - 1;
            positionArguments = arguments
                .Select((argument, index) => index == activeIndex
                    ? new ParsedArgument(
                        completeArguments[index].Syntax,
                        argument.PositionRange)
                    : argument)
                .ToArray();
        }

        return CreatePositionCall(
            VbaCallSyntaxForm.Statement,
            calleeStart,
            calleeEnd,
            positionArguments,
            completeArguments,
            GetActiveNamedArgument(arguments, completeArguments, position),
            isIncomplete: false);
    }

    private static VbaParsedPositionCall CreatePositionCall(
        VbaCallSyntaxForm form,
        int calleeStart,
        int calleeEnd,
        IReadOnlyList<ParsedArgument> arguments,
        IReadOnlyList<ParsedArgument> completeArguments,
        string? activeNamedArgument,
        bool isIncomplete)
    {
        var positionArguments = arguments
            .Select((argument, index) => new VbaCallArgumentSyntax(
                index,
                argument.Syntax.Name,
                argument.Syntax.Kind == VbaArgumentKind.Omitted,
                argument.PositionRange,
                argument.Syntax.ValueText,
                argument.Syntax.ValueRange))
            .ToArray();
        var trailingArguments = completeArguments
            .Skip(arguments.Count)
            .Select((argument, index) => new VbaCallArgumentSyntax(
                arguments.Count + index,
                argument.Syntax.Name,
                argument.Syntax.Kind == VbaArgumentKind.Omitted,
                argument.PositionRange,
                argument.Syntax.ValueText,
                argument.Syntax.ValueRange))
            .ToArray();
        return new VbaParsedPositionCall(
            form,
            calleeStart,
            calleeEnd,
            positionArguments,
            positionArguments[^1].Index,
            activeNamedArgument,
            isIncomplete,
            trailingArguments);
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
            && position.Offset >= completeArguments[activeIndex].PositionRange.Start.Offset
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

                if (significant.Count > 0)
                {
                    statements.Add(new CallStatement(
                        significant.ToArray(),
                        statementWasContinued,
                        IsColonTerminated: false));
                }

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

                if (significant.Count > 0
                    && TextEquals(significant[0], "If")
                    && FindTopLevelKeyword(significant, "Then", startIndex: 1) >= 0)
                {
                    significant.Add(token);
                    continue;
                }

                if (significant.Count > 0)
                {
                    statements.Add(new CallStatement(
                        significant.ToArray(),
                        statementWasContinued,
                        IsColonTerminated: true));
                }

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

        if (significant.Count > 0)
        {
            statements.Add(new CallStatement(
                significant.ToArray(),
                statementWasContinued,
                IsColonTerminated: false));
        }

        return ExpandSingleLineIfStatements(statements);
    }

    private static IReadOnlyList<CallStatement> ExpandSingleLineIfStatements(
        IReadOnlyList<CallStatement> statements)
    {
        var expanded = new List<CallStatement>();
        foreach (var statement in statements)
        {
            var tokens = statement.SignificantTokens;
            if (tokens.Count == 0 || !TextEquals(tokens[0], "If"))
            {
                AddColonSeparatedStatements(expanded, statement);
                continue;
            }

            var thenIndex = FindTopLevelKeyword(tokens, "Then", startIndex: 1);
            if (thenIndex < 0 || thenIndex + 1 >= tokens.Count)
            {
                expanded.Add(statement);
                continue;
            }

            expanded.Add(new CallStatement(
                tokens.Take(thenIndex + 1).ToArray(),
                statement.IsContinued,
                IsColonTerminated: false));

            var elseIndex = FindMatchingSingleLineIfElse(tokens, thenIndex + 1);
            var thenEnd = elseIndex < 0 ? tokens.Count : elseIndex;
            if (thenEnd > thenIndex + 1)
            {
                expanded.AddRange(ExpandSingleLineIfStatements([
                    new CallStatement(
                        tokens.Skip(thenIndex + 1).Take(thenEnd - thenIndex - 1).ToArray(),
                        statement.IsContinued,
                        IsColonTerminated: elseIndex < 0 && statement.IsColonTerminated)
                ]));
            }

            if (elseIndex >= 0 && elseIndex + 1 < tokens.Count)
            {
                expanded.AddRange(ExpandSingleLineIfStatements([
                    new CallStatement(
                        tokens.Skip(elseIndex + 1).ToArray(),
                        statement.IsContinued,
                        statement.IsColonTerminated)
                ]));
            }
        }

        return expanded;
    }

    private static void AddColonSeparatedStatements(
        ICollection<CallStatement> expanded,
        CallStatement statement)
    {
        var tokens = statement.SignificantTokens;
        if (tokens.Count == 0
            || TextEquals(tokens[0], "Rem"))
        {
            expanded.Add(statement);
            return;
        }

        var depth = 0;
        var statementStart = 0;
        var foundSeparator = false;
        for (var index = 0; index < tokens.Count; index++)
        {
            if (IsPunctuation(tokens[index], "("))
            {
                depth++;
                continue;
            }

            if (IsPunctuation(tokens[index], ")") && depth > 0)
            {
                depth--;
                continue;
            }

            if (depth != 0 || !IsPunctuation(tokens[index], ":"))
            {
                continue;
            }

            foundSeparator = true;
            if (index > statementStart)
            {
                expanded.Add(new CallStatement(
                    tokens.Skip(statementStart).Take(index - statementStart).ToArray(),
                    statement.IsContinued,
                    IsColonTerminated: true));
            }

            statementStart = index + 1;
        }

        if (statementStart < tokens.Count)
        {
            expanded.Add(new CallStatement(
                tokens.Skip(statementStart).ToArray(),
                statement.IsContinued,
                statement.IsColonTerminated));
        }
        else if (!foundSeparator)
        {
            expanded.Add(statement);
        }
    }

    private static int FindTopLevelKeyword(
        IReadOnlyList<VbaToken> tokens,
        string keyword,
        int startIndex)
    {
        var depth = 0;
        for (var index = startIndex; index < tokens.Count; index++)
        {
            if (IsPunctuation(tokens[index], "("))
            {
                depth++;
            }
            else if (IsPunctuation(tokens[index], ")") && depth > 0)
            {
                depth--;
            }
            else if (depth == 0 && TextEquals(tokens[index], keyword))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindMatchingSingleLineIfElse(
        IReadOnlyList<VbaToken> tokens,
        int startIndex)
    {
        var depth = 0;
        var unmatchedNestedIfCount = 0;
        for (var index = startIndex; index < tokens.Count; index++)
        {
            if (IsPunctuation(tokens[index], "("))
            {
                depth++;
            }
            else if (IsPunctuation(tokens[index], ")") && depth > 0)
            {
                depth--;
            }
            else if (depth == 0 && TextEquals(tokens[index], "If"))
            {
                unmatchedNestedIfCount++;
            }
            else if (depth == 0 && TextEquals(tokens[index], "Else"))
            {
                if (unmatchedNestedIfCount == 0)
                {
                    return index;
                }

                unmatchedNestedIfCount--;
            }
        }

        return -1;
    }

    private static bool IsLabelDeclaration(CallStatement statement)
        => statement.IsColonTerminated
            && statement.SignificantTokens.Count == 1
            && (VbaIdentifierSyntaxFacts.IsValidDeclaredName(
                    statement.SignificantTokens[0])
                || statement.SignificantTokens[0].Kind == VbaTokenKind.NumericLiteral);

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
        if (end > 0 && IsAdjacentTypeCharacter(tokens[end - 1], tokens[end]))
        {
            end--;
        }

        start = end;
        if (end < 0 || !IsNameToken(tokens[end]))
        {
            return false;
        }

        while (start >= 2
            && IsDot(tokens[start - 1])
            && IsNameToken(tokens[start - 2])
            && (start - 2 != 0
                || !TextEquals(tokens[start - 2], "Call")
                    && !TextEquals(tokens[start - 2], "RaiseEvent")))
        {
            start -= 2;
        }

        if (start == end
            && (start == 0 || !IsDot(tokens[start - 1]))
            && !VbaLanguageVocabulary.CanBeBareCallTarget(tokens[end].Text))
        {
            return false;
        }

        if (start > 0 && IsDot(tokens[start - 1]))
        {
            start--;
        }

        return true;
    }

    private static bool IsAdjacentTypeCharacter(VbaToken name, VbaToken candidate)
        => candidate.Range.Start.Offset == name.Range.End.Offset
            && VbaLanguageVocabulary.TryGetTypeDeclarationCharacterTypeName(
                candidate.Text,
                out _);

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

    private static bool IsWhitespaceSeparatedStatementArgument(
        VbaSourceText sourceText,
        IReadOnlyList<VbaToken> tokens,
        int openIndex,
        int calleeStart,
        int calleeEnd)
        => HasWhitespaceBetween(
                sourceText.Text,
                tokens[calleeEnd].Range.End.Offset,
                tokens[openIndex].Range.Start.Offset)
            && calleeStart == 0
            && TryGetStatementCallee(tokens, out var statementStart, out var statementEnd)
            && statementStart == calleeStart
            && statementEnd == calleeEnd;

    private static bool IsCallableDeclaration(IReadOnlyList<VbaToken> tokens)
    {
        var index = 0;
        while (index < tokens.Count
            && (TextEquals(tokens[index], "Public")
                || TextEquals(tokens[index], "Private")
                || TextEquals(tokens[index], "Friend")
                || TextEquals(tokens[index], "Global")
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

    private static bool TryGetDeclarationBlockKind(
        IReadOnlyList<VbaToken> tokens,
        out string? kind)
    {
        kind = null;
        var index = 0;
        while (index < tokens.Count
            && (TextEquals(tokens[index], "Public")
                || TextEquals(tokens[index], "Private")
                || TextEquals(tokens[index], "Friend")
                || TextEquals(tokens[index], "Global")))
        {
            index++;
        }

        if (index >= tokens.Count
            || !TextEquals(tokens[index], "Type")
                && !TextEquals(tokens[index], "Enum"))
        {
            return false;
        }

        kind = tokens[index].Text;
        return true;
    }

    private static bool IsDeclarationBlockEnd(
        IReadOnlyList<VbaToken> tokens,
        string declarationBlockKind)
        => tokens.Count >= 2
            && TextEquals(tokens[0], "End")
            && tokens[1].Text.Equals(
                declarationBlockKind,
                StringComparison.OrdinalIgnoreCase);

    private static bool IsNonExecutableDeclaration(IReadOnlyList<VbaToken> tokens)
        => tokens.Count > 0
            && (TextEquals(tokens[0], "Public")
                || TextEquals(tokens[0], "Private")
                || TextEquals(tokens[0], "Friend")
                || TextEquals(tokens[0], "Global")
                || TextEquals(tokens[0], "Static")
                || TextEquals(tokens[0], "Dim")
                || TextEquals(tokens[0], "Const")
                || TextEquals(tokens[0], "Type")
                || TextEquals(tokens[0], "Enum")
                || TextEquals(tokens[0], "Option")
                || TextEquals(tokens[0], "Attribute")
                || TextEquals(tokens[0], "Implements")
                || IsDefTypeDeclaration(tokens[0])
                || IsUserDefinedTypeMemberDeclaration(tokens));

    private static bool IsDefTypeDeclaration(VbaToken token)
        => token.Text.ToUpperInvariant() is
            "DEFBOOL"
            or "DEFBYTE"
            or "DEFCUR"
            or "DEFDATE"
            or "DEFDBL"
            or "DEFDEC"
            or "DEFINT"
            or "DEFLNG"
            or "DEFLNGLNG"
            or "DEFLNGPTR"
            or "DEFOBJ"
            or "DEFSNG"
            or "DEFSTR"
            or "DEFVAR";

    private static bool IsUserDefinedTypeMemberDeclaration(IReadOnlyList<VbaToken> tokens)
    {
        if (tokens.Count < 2
            || !VbaIdentifierSyntaxFacts.IsValidDeclaredName(tokens[0]))
        {
            return false;
        }

        var index = 1;
        if (IsPunctuation(tokens[index], "("))
        {
            var depth = 0;
            for (; index < tokens.Count; index++)
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
                        index++;
                        break;
                    }
                }
            }

            if (depth != 0)
            {
                return false;
            }
        }

        return index < tokens.Count && TextEquals(tokens[index], "As");
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

        if (TextEquals(tokens[start], "Call")
            || TextEquals(tokens[start], "RaiseEvent"))
        {
            start++;
        }

        if (start >= tokens.Count)
        {
            return false;
        }

        var isLeadingDot = IsDot(tokens[start]);
        if (isLeadingDot)
        {
            start++;
        }

        if (start >= tokens.Count
            || !VbaLanguageVocabulary.CanBeBareCallTarget(tokens[start].Text))
        {
            return false;
        }

        end = start;
        while (end + 2 < tokens.Count
            && tokens[end].Range.End.Offset == tokens[end + 1].Range.Start.Offset
            && IsDot(tokens[end + 1])
            && tokens[end + 1].Range.End.Offset == tokens[end + 2].Range.Start.Offset
            && IsNameToken(tokens[end + 2]))
        {
            end += 2;
        }

        if (isLeadingDot)
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
            if (VbaIdentifier.IsWhitespace(source[offset]))
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
        bool IsContinued,
        bool IsColonTerminated);
}

internal sealed record VbaParsedPositionCall(
    VbaCallSyntaxForm Form,
    int CalleeStartIndex,
    int CalleeEndIndex,
    IReadOnlyList<VbaCallArgumentSyntax> Arguments,
    int ActiveArgumentIndex,
    string? ActiveNamedArgument,
    bool IsIncomplete,
    IReadOnlyList<VbaCallArgumentSyntax> TrailingArguments);
