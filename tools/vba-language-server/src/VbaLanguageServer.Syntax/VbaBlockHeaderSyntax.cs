namespace VbaLanguageServer.Syntax;

/// <summary>
/// Identifies a strict body-owning VBA block header form.
/// </summary>
public enum VbaBlockHeaderKind
{
    /// <summary>
    /// A non-external Sub declaration.
    /// </summary>
    Sub,

    /// <summary>
    /// A block-form If statement inside a callable body.
    /// </summary>
    If
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
    internal static VbaCallableHeaderShape? FindCompleteCallableAncestor(
        VbaSyntaxTree tree,
        VbaSyntaxRange openerRange)
    {
        var statement = FindCompleteAncestorStatement(tree, openerRange);
        if (statement is null)
        {
            return null;
        }

        var tokens = statement.SignificantTokens;
        var firstPhysicalLine = tokens[0].Range.Start.Line;
        if (!TryGetCompleteCallableShape(tokens, tree.Module.Kind, out var shape)
            || HasDisqualifyingHeaderDiagnostic(tree, statement, shape.ExpectedTerminator))
        {
            return null;
        }

        var declarations = tree.Module.CallableDeclarations
            .Where(declaration =>
                declaration.LineIndex == firstPhysicalLine
                && !declaration.IsExternal
                && declaration.Name.Equals(shape.Name, StringComparison.OrdinalIgnoreCase)
                && declaration.DeclarationKeyword?.Equals(
                    shape.DeclarationKeyword,
                    StringComparison.OrdinalIgnoreCase) == true
                && declaration.PropertyAccessorKind == shape.PropertyAccessorKind)
            .Take(2)
            .ToArray();
        return declarations.Length == 1 ? shape : null;
    }

    internal static bool IsCompleteIfAncestor(
        VbaSyntaxTree tree,
        VbaSyntaxRange openerRange)
    {
        var statement = FindCompleteAncestorStatement(tree, openerRange);
        if (statement is null)
        {
            return false;
        }

        var tokens = statement.SignificantTokens;
        return HasCompleteBlockIfTokenShape(
                tokens,
                tree.Module.Kind,
                allowLeadingMemberAccess: false)
            && !HasDisqualifyingHeaderDiagnostic(tree, statement, CanonicalEndIf);
    }

    private static VbaLogicalStatementSpan? FindCompleteAncestorStatement(
        VbaSyntaxTree tree,
        VbaSyntaxRange openerRange)
    {
        if (HasConditionalCompilationDirective(tree.TokenStream))
        {
            return null;
        }

        var statements = VbaLogicalStatementSpan
            .Build(tree.SourceText.Text.Length, tree.TokenStream.Tokens)
            .Where(candidate => candidate.Range.Equals(openerRange))
            .Take(2)
            .ToArray();
        if (statements.Length != 1 || statements[0].EndsWithColon)
        {
            return null;
        }

        var statement = statements[0];
        var tokens = statement.SignificantTokens;
        var firstPhysicalLine = tokens[0].Range.Start.Line;
        var finalPhysicalLine = tokens[^1].Range.End.Line;
        return HasOnlyLeadingWhitespace(tree.SourceText.Lines[firstPhysicalLine], tokens[0])
            && HasOnlyTrailingSpacesOrApostropheComment(
                tree.SourceText.Lines[finalPhysicalLine],
                tokens[^1])
            && !tree.TokenStream.Tokens.Any(token =>
                token.Kind == VbaTokenKind.LineContinuation
                && token.Range.Start.Line == finalPhysicalLine)
                ? statement
                : null;
    }

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
        if (keywordIndex >= 0)
        {
            if (IsIllegalForModuleKind(tree.Module.Kind, tokens)
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
            return declaration is null
                ? null
                : CreateHeader(
                    source,
                    tokens,
                    line,
                    character,
                    VbaBlockHeaderKind.Sub,
                    CanonicalEndSub);
        }

        if (!HasCompleteBlockIfTokenShape(
                tokens,
                tree.Module.Kind,
                VbaBlockSyntaxFacts.HasEnclosingBlock(
                    tree,
                    VbaBlockKind.With,
                    statement.StartOffset,
                    statement.EndOffset))
            || tokens[^1].Range.End.Line != line
            || !HasOnlyLeadingWhitespace(source.Lines[tokens[0].Range.Start.Line], tokens[0])
            || !HasOnlyTrailingSpacesOrApostropheComment(source.Lines[line], tokens[^1])
            || tree.TokenStream.Tokens.Any(token =>
                token.Kind == VbaTokenKind.LineContinuation
                && token.Range.Start.Line == line)
            || !HasCallableOwner(tree, statement)
            || HasDisqualifyingHeaderDiagnostic(tree, statement, CanonicalEndIf))
        {
            return null;
        }

        return CreateHeader(
            source,
            tokens,
            line,
            character,
            VbaBlockHeaderKind.If,
            CanonicalEndIf);
    }

    private static VbaBlockHeaderSyntax CreateHeader(
        VbaSourceText source,
        IReadOnlyList<VbaToken> tokens,
        int finalPhysicalLine,
        int finalCharacter,
        VbaBlockHeaderKind kind,
        string expectedTerminator)
    {
        var firstPhysicalLine = tokens[0].Range.Start.Line;
        var firstLine = source.Lines[firstPhysicalLine];
        var leadingWhitespaceLength = firstLine.Text
            .TakeWhile(value => value is ' ' or '\t')
            .Count();
        var range = new VbaSyntaxRange(
            new VbaSyntaxPosition(firstPhysicalLine, 0, firstLine.StartOffset),
            new VbaSyntaxPosition(
                finalPhysicalLine,
                finalCharacter,
                source.Lines[finalPhysicalLine].StartOffset + finalCharacter));
        return new VbaBlockHeaderSyntax(
            kind,
            range,
            firstPhysicalLine,
            finalPhysicalLine,
            firstLine.Text[..leadingWhitespaceLength],
            expectedTerminator);
    }

    private static string CanonicalEndSub
        => $"{VbaLanguageVocabulary.CanonicalKeywords["end"]} "
            + VbaLanguageVocabulary.CanonicalKeywords["sub"];

    private static string CanonicalEndIf
        => $"{VbaLanguageVocabulary.CanonicalKeywords["end"]} "
            + VbaLanguageVocabulary.CanonicalKeywords["if"];

    private static string CanonicalEndFunction
        => $"{VbaLanguageVocabulary.CanonicalKeywords["end"]} "
            + VbaLanguageVocabulary.CanonicalKeywords["function"];

    private static string CanonicalEndProperty
        => $"{VbaLanguageVocabulary.CanonicalKeywords["end"]} "
            + VbaLanguageVocabulary.CanonicalKeywords["property"];

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

    private static bool TryGetCompleteCallableShape(
        IReadOnlyList<VbaToken> tokens,
        VbaModuleKind moduleKind,
        out VbaCallableHeaderShape shape)
    {
        shape = default!;
        if (tokens.Count == 0 || IsIllegalForModuleKind(moduleKind, tokens))
        {
            return false;
        }

        var keywordIndex = GetSubKeywordIndex(tokens);
        if (keywordIndex >= 0 && HasCompleteSubTokenShape(tokens, keywordIndex))
        {
            shape = new VbaCallableHeaderShape(
                tokens[keywordIndex + 1].Text,
                "Sub",
                null,
                CanonicalEndSub);
            return true;
        }

        keywordIndex = GetCallableKeywordIndex(tokens, "Function");
        if (keywordIndex >= 0)
        {
            if (!HasCompleteValueCallableTokenShape(
                    tokens,
                    keywordIndex + 1,
                    allowReturnType: true,
                    propertyAccessor: null))
            {
                return false;
            }

            shape = new VbaCallableHeaderShape(
                tokens[keywordIndex + 1].Text,
                "Function",
                null,
                CanonicalEndFunction);
            return true;
        }

        keywordIndex = GetCallableKeywordIndex(tokens, "Property");
        var accessorIndex = keywordIndex + 1;
        var nameIndex = accessorIndex + 1;
        if (keywordIndex < 0
            || accessorIndex >= tokens.Count
            || nameIndex >= tokens.Count
            || !TryGetPropertyAccessor(tokens[accessorIndex], out var accessor)
            || !HasCompleteValueCallableTokenShape(
                tokens,
                nameIndex,
                allowReturnType: accessor == VbaPropertyAccessorKind.Get,
                propertyAccessor: accessor))
        {
            return false;
        }

        shape = new VbaCallableHeaderShape(
            tokens[nameIndex].Text,
            "Property",
            accessor,
            CanonicalEndProperty);
        return true;

        static bool TryGetPropertyAccessor(
            VbaToken token,
            out VbaPropertyAccessorKind accessor)
        {
            if (Matches(token, "Get"))
            {
                accessor = VbaPropertyAccessorKind.Get;
                return true;
            }

            if (Matches(token, "Let"))
            {
                accessor = VbaPropertyAccessorKind.Let;
                return true;
            }

            if (Matches(token, "Set"))
            {
                accessor = VbaPropertyAccessorKind.Set;
                return true;
            }

            accessor = default;
            return false;
        }
    }

    private static int GetCallableKeywordIndex(
        IReadOnlyList<VbaToken> tokens,
        string keyword)
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

        return index < tokens.Count && Matches(tokens[index], keyword)
            ? index
            : -1;
    }

    private static bool HasCompleteValueCallableTokenShape(
        IReadOnlyList<VbaToken> tokens,
        int nameIndex,
        bool allowReturnType,
        VbaPropertyAccessorKind? propertyAccessor)
    {
        if (nameIndex >= tokens.Count
            || !VbaIdentifierSyntaxFacts.IsValidDeclaredName(tokens[nameIndex]))
        {
            return false;
        }

        var shapeEnd = tokens.Count;
        if (shapeEnd > nameIndex + 1 && Matches(tokens[shapeEnd - 1], "Static"))
        {
            if (tokens.Take(nameIndex).Any(token => Matches(token, "Static")))
            {
                return false;
            }

            shapeEnd--;
        }

        var index = nameIndex + 1;
        var hasTypeCharacter = index < shapeEnd
            && IsAdjacentTypeCharacter(tokens[nameIndex], tokens[index]);
        if (hasTypeCharacter)
        {
            index++;
        }

        var requireParameter = propertyAccessor is
            VbaPropertyAccessorKind.Let or VbaPropertyAccessorKind.Set;
        var hasParameter = false;
        if (index < shapeEnd && Matches(tokens[index], "("))
        {
            var argumentEnd = FindMatchingCloseParenthesis(tokens, index);
            if (argumentEnd < 0)
            {
                return false;
            }

            var parametersComplete = requireParameter
                ? TryReadCompletePropertyParameterList(
                    tokens,
                    index + 1,
                    argumentEnd,
                    propertyAccessor!.Value)
                : TryReadCompleteParameterList(
                    tokens,
                    index + 1,
                    argumentEnd,
                    out _,
                    tokens[nameIndex].Text);
            if (!parametersComplete)
            {
                return false;
            }

            hasParameter = argumentEnd > index + 1;
            index = argumentEnd + 1;
        }

        if (requireParameter && !hasParameter)
        {
            return false;
        }

        if (index == shapeEnd)
        {
            return true;
        }

        if (!allowReturnType || hasTypeCharacter || !Matches(tokens[index], "As"))
        {
            return false;
        }

        index++;
        if (!SkipCompleteTypeName(tokens, ref index, shapeEnd, out _))
        {
            return false;
        }

        if (index + 1 < shapeEnd
            && Matches(tokens[index], "(")
            && Matches(tokens[index + 1], ")"))
        {
            index += 2;
        }

        return index == shapeEnd;
    }

    private static bool TryReadCompletePropertyParameterList(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end,
        VbaPropertyAccessorKind accessor)
    {
        var separator = -1;
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
            else if (depth == 0 && Matches(tokens[index], ","))
            {
                separator = index;
            }
        }

        var valueStart = separator < 0 ? start : separator + 1;
        if (separator == start
            || depth != 0
            || !TryReadCompleteParameter(tokens, valueStart, end, out var valueParameter)
            || !IsCompletePropertyValueParameter(valueParameter, accessor))
        {
            return false;
        }

        return separator < 0
            || TryReadCompleteParameterList(
                tokens,
                start,
                separator,
                out _,
                valueParameter.Name);
    }

    private static bool IsCompletePropertyValueParameter(
        VbaParameterHeaderShape parameter,
        VbaPropertyAccessorKind accessor)
    {
        if (parameter.IsOptional
            || parameter.IsParamArray
            || accessor == VbaPropertyAccessorKind.Set && parameter.IsArray)
        {
            return false;
        }

        return accessor switch
        {
            VbaPropertyAccessorKind.Let => parameter.TypeCategory is
                VbaParameterTypeCategory.ImplicitVariant
                or VbaParameterTypeCategory.Variant
                or VbaParameterTypeCategory.IntrinsicValue
                or VbaParameterTypeCategory.Named,
            VbaPropertyAccessorKind.Set => parameter.TypeCategory is
                VbaParameterTypeCategory.ImplicitVariant
                or VbaParameterTypeCategory.Variant
                or VbaParameterTypeCategory.Object
                or VbaParameterTypeCategory.Named,
            _ => true
        };
    }

    private static int FindMatchingCloseParenthesis(
        IReadOnlyList<VbaToken> tokens,
        int openIndex)
    {
        var depth = 0;
        for (var index = openIndex; index < tokens.Count; index++)
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
                    return index;
                }
            }
        }

        return -1;
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

    private static bool HasCompleteBlockIfTokenShape(
        IReadOnlyList<VbaToken> tokens,
        VbaModuleKind moduleKind,
        bool allowLeadingMemberAccess)
        => tokens.Count >= 3
            && Matches(tokens[0], "If")
            && Matches(tokens[^1], "Then")
            && VbaExecutableExpressionSyntax.IsComplete(
                tokens,
                1,
                tokens.Count - 1,
                moduleKind,
                allowLeadingMemberAccess);

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

    private static bool HasCallableOwner(
        VbaSyntaxTree tree,
        VbaLogicalStatementSpan statement)
    {
        var owners = tree.Module.CallableDeclarations
            .Where(declaration =>
                !declaration.IsExternal
                && declaration.BlockRange.Start.Offset < statement.StartOffset
                && statement.EndOffset <= declaration.BlockRange.End.Offset)
            .Take(2)
            .ToArray();
        var enclosingProcedures = tree.Module.Blocks
            .Where(block =>
                block.Kind == VbaBlockKind.Procedure
                && block.OpenerRange.Start.Offset < statement.StartOffset
                && statement.EndOffset <= block.Range.End.Offset)
            .Take(2)
            .ToArray();
        return owners.Length == 1
            && enclosingProcedures.Length == 1
            && !tree.Module.Blocks.Any(block =>
                block.IsMalformedBarrier
                && block.Range.Start.Offset < statement.StartOffset
                && statement.EndOffset <= block.Range.End.Offset);
    }

    private static bool HasCompleteParameterList(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end)
        => TryReadCompleteParameterList(tokens, start, end, out _);

    private static bool TryReadCompleteParameterList(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end,
        out VbaParameterHeaderShape finalParameter,
        string? forbiddenParameterName = null)
    {
        finalParameter = default;
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
                    || parameter.Name.Equals(
                        forbiddenParameterName,
                        StringComparison.OrdinalIgnoreCase)
                    || parameter.IsParamArray
                    || optionalSeen && !parameter.IsOptional)
                {
                    return false;
                }

                optionalSeen |= parameter.IsOptional;
                segmentStart = index + 1;
            }
        }

        if (depth != 0
            || !TryReadCompleteParameter(tokens, segmentStart, end, out finalParameter))
        {
            return false;
        }

        return parameterNames.Add(finalParameter.Name)
            && !finalParameter.Name.Equals(
                forbiddenParameterName,
                StringComparison.OrdinalIgnoreCase)
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

        var typeCategory = hasTypeCharacter
            ? VbaParameterTypeCategory.IntrinsicValue
            : hasExplicitType
                ? parameterType.TypeCategory
                : VbaParameterTypeCategory.ImplicitVariant;
        shape = new VbaParameterHeaderShape(
            name.Text,
            isOptional,
            isParamArray,
            hasArrayMarker,
            typeCategory);

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
                IsUnqualifiedObject: Matches(intrinsic, "Object"),
                TypeCategory: GetParameterTypeCategory(intrinsic));
            return true;
        }

        if (IsIntrinsicTypeKeyword(first))
        {
            index++;
            shape = new VbaParameterTypeShape(
                IsUnqualifiedVariant: Matches(first, "Variant"),
                IsUnqualifiedObject: Matches(first, "Object"),
                TypeCategory: GetParameterTypeCategory(first));
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

        shape = new VbaParameterTypeShape(
            IsUnqualifiedVariant: false,
            IsUnqualifiedObject: false,
            TypeCategory: VbaParameterTypeCategory.Named);
        return true;
    }

    private static VbaParameterTypeCategory GetParameterTypeCategory(VbaToken token)
        => Matches(token, "Variant")
            ? VbaParameterTypeCategory.Variant
            : Matches(token, "Object")
                ? VbaParameterTypeCategory.Object
                : VbaParameterTypeCategory.IntrinsicValue;

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
        bool IsParamArray,
        bool IsArray,
        VbaParameterTypeCategory TypeCategory);

    private readonly record struct VbaParameterTypeShape(
        bool IsUnqualifiedVariant,
        bool IsUnqualifiedObject,
        VbaParameterTypeCategory TypeCategory);

    private enum VbaParameterTypeCategory
    {
        ImplicitVariant,
        Variant,
        Object,
        IntrinsicValue,
        Named
    }

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

internal sealed record VbaCallableHeaderShape(
    string Name,
    string DeclarationKeyword,
    VbaPropertyAccessorKind? PropertyAccessorKind,
    string ExpectedTerminator);
