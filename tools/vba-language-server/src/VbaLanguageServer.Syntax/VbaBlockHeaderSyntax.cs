namespace VbaLanguageServer.Syntax;

/// <summary>
/// Identifies a strict body-owning VBA block header form.
/// </summary>
public enum VbaBlockHeaderKind
{
    /// <summary>
    /// A non-external Sub declaration.
    /// </summary>
    Sub
}

/// <summary>
/// Represents a complete logical VBA header admitted for block skeleton insertion.
/// </summary>
/// <param name="Kind">The strict block-header form.</param>
/// <param name="Range">The physical source span from the first-line prefix through the final line end.</param>
/// <param name="FirstPhysicalLine">The first physical line of the logical header.</param>
/// <param name="FinalPhysicalLine">The final physical line of the logical header.</param>
/// <param name="LeadingWhitespace">The exact leading whitespace of the first physical line.</param>
/// <param name="ExpectedTerminator">The canonical matching terminator.</param>
public sealed record VbaBlockHeaderSyntax(
    VbaBlockHeaderKind Kind,
    VbaSyntaxRange Range,
    int FirstPhysicalLine,
    int FinalPhysicalLine,
    string LeadingWhitespace,
    string ExpectedTerminator)
{
    /// <summary>
    /// Finds a complete strict header ending at an actual physical line end.
    /// </summary>
    /// <param name="tree">The parsed source snapshot.</param>
    /// <param name="line">The zero-based final physical line.</param>
    /// <param name="character">The zero-based physical line-end character.</param>
    /// <returns>The strict header, or null when the position is ineligible.</returns>
    public static VbaBlockHeaderSyntax? FindAtPosition(
        VbaSyntaxTree tree,
        int line,
        int character)
    {
        var source = tree.SourceText;
        if (line < 0
            || line >= source.Lines.Count
            || character < 0
            || character != source.Lines[line].Text.Length
            || HasConditionalCompilationDirective(tree.TokenStream))
        {
            return null;
        }

        var positionOffset = source.Lines[line].StartOffset + character;
        var statement = VbaLogicalStatementSpan
            .Build(source.Text.Length, tree.TokenStream.Tokens)
            .SingleOrDefault(candidate =>
                candidate.EndOffset == positionOffset
                && candidate.SignificantTokens.Count > 0);
        if (statement is null || statement.EndsWithColon)
        {
            return null;
        }

        var tokens = statement.SignificantTokens;
        var keywordIndex = GetSubKeywordIndex(tokens);
        if (keywordIndex < 0
            || IsIllegalForModuleKind(tree.Module.Kind, tokens)
            || !HasCompleteSubTokenShape(tokens, keywordIndex)
            || HasPrecedingOpenBlockBarrier(tree, statement)
            || HasDisqualifyingHeaderDiagnostic(tree, statement, CanonicalEndSub))
        {
            return null;
        }

        var nameToken = tokens[keywordIndex + 1];
        var firstPhysicalLine = tokens[0].Range.Start.Line;
        var declaration = tree.Module.CallableDeclarations.FirstOrDefault(candidate =>
            candidate.LineIndex == firstPhysicalLine
            && candidate.Kind == VbaDeclarationKind.Procedure
            && !candidate.IsExternal
            && candidate.Name.Equals(nameToken.Text, StringComparison.OrdinalIgnoreCase)
            && candidate.DeclarationKeyword?.Equals("Sub", StringComparison.OrdinalIgnoreCase) == true);
        if (declaration is null)
        {
            return null;
        }

        var firstLine = source.Lines[firstPhysicalLine];
        var leadingWhitespaceLength = firstLine.Text
            .TakeWhile(value => value is ' ' or '\t')
            .Count();
        var range = new VbaSyntaxRange(
            new VbaSyntaxPosition(firstPhysicalLine, 0, firstLine.StartOffset),
            new VbaSyntaxPosition(line, character, positionOffset));
        return new VbaBlockHeaderSyntax(
            VbaBlockHeaderKind.Sub,
            range,
            firstPhysicalLine,
            line,
            firstLine.Text[..leadingWhitespaceLength],
            CanonicalEndSub);
    }

    private static string CanonicalEndSub
        => $"{VbaLanguageVocabulary.CanonicalKeywords["end"]} "
            + VbaLanguageVocabulary.CanonicalKeywords["sub"];

    private static int GetSubKeywordIndex(IReadOnlyList<VbaToken> tokens)
    {
        var index = 0;
        if (index < tokens.Count && IsVisibility(tokens[index].Text))
        {
            index++;
        }

        if (index < tokens.Count && Matches(tokens[index], "Static"))
        {
            index++;
        }

        return index < tokens.Count && Matches(tokens[index], "Sub")
            ? index
            : -1;
    }

    private static bool HasCompleteSubTokenShape(
        IReadOnlyList<VbaToken> tokens,
        int keywordIndex)
    {
        var nameIndex = keywordIndex + 1;
        if (nameIndex >= tokens.Count
            || !VbaIdentifierSyntaxFacts.IsValidDeclaredName(tokens[nameIndex]))
        {
            return false;
        }

        var shapeEnd = tokens.Count;
        var hasLeadingStatic = keywordIndex > 0 && Matches(tokens[keywordIndex - 1], "Static");
        if (shapeEnd > nameIndex + 1 && Matches(tokens[shapeEnd - 1], "Static"))
        {
            if (hasLeadingStatic)
            {
                return false;
            }

            shapeEnd--;
        }

        var argumentStart = nameIndex + 1;
        if (argumentStart == shapeEnd)
        {
            return true;
        }

        if (!Matches(tokens[argumentStart], "("))
        {
            return false;
        }

        var depth = 0;
        for (var index = argumentStart; index < shapeEnd; index++)
        {
            if (Matches(tokens[index], "("))
            {
                depth++;
            }
            else if (Matches(tokens[index], ")"))
            {
                depth--;
                if (depth < 0 || depth == 0 && index != shapeEnd - 1)
                {
                    return false;
                }
            }
        }

        return depth == 0
            && Matches(tokens[shapeEnd - 1], ")")
            && HasCompleteParameterList(tokens, argumentStart + 1, shapeEnd - 1);
    }

    private static bool HasCompleteParameterList(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end)
    {
        if (start == end)
        {
            return true;
        }

        var segmentStart = start;
        var optionalSeen = false;
        var parameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            }

            if (depth == 0 && Matches(tokens[index], ","))
            {
                if (!TryReadCompleteParameter(tokens, segmentStart, index, out var parameter)
                    || !parameterNames.Add(parameter.Name)
                    || parameter.IsParamArray
                    || optionalSeen && !parameter.IsOptional)
                {
                    return false;
                }

                optionalSeen |= parameter.IsOptional;
                segmentStart = index + 1;
            }
        }

        return depth == 0
            && TryReadCompleteParameter(tokens, segmentStart, end, out var finalParameter)
            && parameterNames.Add(finalParameter.Name)
            && (!finalParameter.IsParamArray || !optionalSeen)
            && (!optionalSeen || finalParameter.IsOptional);
    }

    private static bool TryReadCompleteParameter(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end,
        out VbaParameterHeaderShape shape)
    {
        shape = default;
        var index = start;
        var isOptional = SkipIfMatches(tokens, ref index, end, "Optional");
        var isByVal = false;
        var hasPassingMode = false;
        if (index < end && (Matches(tokens[index], "ByVal") || Matches(tokens[index], "ByRef")))
        {
            isByVal = Matches(tokens[index], "ByVal");
            hasPassingMode = true;
            index++;
        }

        if (!isOptional)
        {
            isOptional = SkipIfMatches(tokens, ref index, end, "Optional");
        }

        var isParamArray = SkipIfMatches(tokens, ref index, end, "ParamArray");
        shape = default;
        if (isParamArray && (isOptional || hasPassingMode)
            || index >= end
            || !VbaIdentifierSyntaxFacts.IsValidDeclaredName(tokens[index]))
        {
            return false;
        }

        var name = tokens[index++];
        shape = new VbaParameterHeaderShape(name.Text, isOptional, isParamArray);
        var hasTypeCharacter = index < end && IsAdjacentTypeCharacter(name, tokens[index]);
        if (hasTypeCharacter)
        {
            index++;
        }

        var hasArrayMarker = false;
        if (index + 1 < end && Matches(tokens[index], "(") && Matches(tokens[index + 1], ")"))
        {
            hasArrayMarker = true;
            index += 2;
        }

        var parameterType = default(VbaParameterTypeShape);
        var hasExplicitType = SkipIfMatches(tokens, ref index, end, "As");
        if (hasExplicitType)
        {
            if (hasTypeCharacter
                || !SkipCompleteTypeName(tokens, ref index, end, out parameterType)
                || isParamArray && !parameterType.IsUnqualifiedVariant)
            {
                return false;
            }
        }

        if (isParamArray && (!hasArrayMarker || hasTypeCharacter)
            || hasArrayMarker && (isByVal || isOptional))
        {
            return false;
        }

        if (index == end)
        {
            return true;
        }

        var defaultStart = index + 1;
        return isOptional
            && Matches(tokens[index], "=")
            && (!parameterType.IsUnqualifiedObject
                || IsExactlyNothing(tokens, defaultStart, end))
            && HasCompleteDefaultExpression(tokens, defaultStart, end);
    }

    private static bool SkipCompleteTypeName(
        IReadOnlyList<VbaToken> tokens,
        ref int index,
        int end,
        out VbaParameterTypeShape shape)
    {
        shape = default;
        if (index >= end)
        {
            return false;
        }

        var first = tokens[index];
        if (Matches(first, "["))
        {
            if (index + 2 >= end
                || !IsIntrinsicTypeKeyword(tokens[index + 1])
                || !Matches(tokens[index + 2], "]"))
            {
                return false;
            }

            var intrinsic = tokens[index + 1];
            index += 3;
            shape = new VbaParameterTypeShape(
                IsUnqualifiedVariant: Matches(intrinsic, "Variant"),
                IsUnqualifiedObject: Matches(intrinsic, "Object"));
            return true;
        }

        if (IsIntrinsicTypeKeyword(first))
        {
            index++;
            shape = new VbaParameterTypeShape(
                IsUnqualifiedVariant: Matches(first, "Variant"),
                IsUnqualifiedObject: Matches(first, "Object"));
            return true;
        }

        if (!VbaIdentifierSyntaxFacts.IsValidDeclaredName(first)
            || Matches(first, "Any"))
        {
            return false;
        }

        index++;
        while (index + 1 < end && Matches(tokens[index], "."))
        {
            if (!(AreAdjacent(tokens[index - 1], tokens[index])
                    || tokens[index - 1].Range.End.Line < tokens[index].Range.Start.Line)
                || !VbaIdentifierSyntaxFacts.IsValidDeclaredName(tokens[index + 1]))
            {
                return false;
            }

            index += 2;
        }

        return true;
    }

    private static bool HasCompleteDefaultExpression(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end)
        => VbaConstantExpressionSyntax.IsComplete(tokens, start, end);

    private static bool SkipIfMatches(
        IReadOnlyList<VbaToken> tokens,
        ref int index,
        int end,
        string text)
    {
        if (index >= end || !Matches(tokens[index], text))
        {
            return false;
        }

        index++;
        return true;
    }

    private static bool IsAdjacentTypeCharacter(VbaToken name, VbaToken candidate)
        => candidate.Range.Start.Offset == name.Range.End.Offset
            && candidate.Text is "$" or "%" or "&" or "^" or "!" or "#" or "@";

    private static bool IsIntrinsicTypeKeyword(VbaToken token)
        => token.Kind == VbaTokenKind.Keyword
            && (Matches(token, "Boolean")
                || Matches(token, "Byte")
                || Matches(token, "Currency")
                || Matches(token, "Date")
                || Matches(token, "Double")
                || Matches(token, "Integer")
                || Matches(token, "Long")
                || Matches(token, "LongLong")
                || Matches(token, "LongPtr")
                || Matches(token, "Object")
                || Matches(token, "Single")
                || Matches(token, "String")
                || Matches(token, "Variant"));

    private readonly record struct VbaParameterHeaderShape(
        string Name,
        bool IsOptional,
        bool IsParamArray);

    private readonly record struct VbaParameterTypeShape(
        bool IsUnqualifiedVariant,
        bool IsUnqualifiedObject);

    private static bool IsExactlyNothing(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end)
        => end == start + 1 && Matches(tokens[start], "Nothing");

    private static bool HasPrecedingOpenBlockBarrier(
        VbaSyntaxTree tree,
        VbaLogicalStatementSpan statement)
        => tree.Module.Blocks.Any(block =>
            block.CloserRange is null
            && block.OpenerRange.Start.Offset < statement.StartOffset
            && statement.EndOffset <= block.Range.End.Offset);

    private static bool HasDisqualifyingHeaderDiagnostic(
        VbaSyntaxTree tree,
        VbaLogicalStatementSpan statement,
        string expectedTerminator)
        => tree.Diagnostics.Any(diagnostic =>
            diagnostic.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)
            && diagnostic.Range.Start.Offset <= statement.EndOffset
            && statement.StartOffset <= diagnostic.Range.End.Offset
            && !(diagnostic.Code.Equals(
                    "syntax.missingBlockTerminator",
                    StringComparison.Ordinal)
                && diagnostic.Message.Contains(
                    expectedTerminator,
                    StringComparison.OrdinalIgnoreCase)));

    private static bool IsVisibility(string text)
        => text.Equals("Global", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Public", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Private", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Friend", StringComparison.OrdinalIgnoreCase);

    private static bool IsIllegalForModuleKind(
        VbaModuleKind moduleKind,
        IReadOnlyList<VbaToken> tokens)
        => tokens.Count > 0
            && (moduleKind == VbaModuleKind.StandardModule
                ? Matches(tokens[0], "Friend")
                : Matches(tokens[0], "Global"));

    private static bool HasConditionalCompilationDirective(VbaTokenStream stream)
        => stream.Tokens.Any(token =>
            token.Kind == VbaTokenKind.PreprocessorDirective
            && IsConditionalCompilationDirective(token.Text));

    private static bool IsConditionalCompilationDirective(string text)
    {
        var directive = text.TrimStart();
        return StartsWithDirectiveWord(directive, "#If")
            || StartsWithDirectiveWord(directive, "#ElseIf")
            || StartsWithDirectiveWord(directive, "#Else")
            || directive.StartsWith("#End", StringComparison.OrdinalIgnoreCase);
    }

    private static bool StartsWithDirectiveWord(string directive, string word)
        => directive.StartsWith(word, StringComparison.OrdinalIgnoreCase)
            && (directive.Length == word.Length || char.IsWhiteSpace(directive[word.Length]));

    private static bool AreAdjacent(VbaToken left, VbaToken right)
        => left.Range.End.Offset == right.Range.Start.Offset;

    private static bool Matches(VbaToken token, string text)
        => token.Text.Equals(text, StringComparison.OrdinalIgnoreCase);
}
