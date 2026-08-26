using System.Text;
using System.Text.RegularExpressions;

namespace VbaLanguageServer.Syntax;

/// <summary>
/// Parses exported VBA module source text into the reusable syntax model.
/// </summary>
internal static class VbaSyntaxTreeParser
{
    private static readonly Regex AttributePattern = new(
        "^" + VbaIdentifier.RegexWhitespace + "*"
        + "Attribute" + VbaIdentifier.RegexWhitespace + "+"
        + "(?<name>(?>" + VbaIdentifier.RegexIdentifierCandidate + "))"
        + VbaIdentifier.RegexWhitespace + "*=" + VbaIdentifier.RegexWhitespace + "*"
        + "(?<value>.+?)" + VbaIdentifier.RegexWhitespace + "*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex OptionPattern = new(
        "^" + VbaIdentifier.RegexWhitespace + "*Option"
        + "(?:" + VbaIdentifier.RegexWhitespace + "+.*)?"
        + VbaIdentifier.RegexWhitespace + "*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ProcedurePattern = new(
        "^" + VbaIdentifier.RegexWhitespace + "*"
        + "(?:(?<visibility>Public|Private|Friend|Global)" + VbaIdentifier.RegexWhitespace + "+)?"
        + "(?:(?<static>Static)" + VbaIdentifier.RegexWhitespace + "+)?"
        + "(?:(?<kind>Sub|Function)|Property" + VbaIdentifier.RegexWhitespace + "+(?<propertyKind>Get|Let|Set))"
        + VbaIdentifier.RegexWhitespace + "+"
        + "(?<name>(?>" + VbaIdentifier.RegexIdentifierCandidate + "))"
        + VbaIdentifier.RegexWhitespace + "*(?:\\((?<parameters>.*)\\))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DeclarePattern = new(
        "^" + VbaIdentifier.RegexWhitespace + "*"
        + "(?:(?<visibility>Public|Private)" + VbaIdentifier.RegexWhitespace + "+)?"
        + "Declare" + VbaIdentifier.RegexWhitespace + "+"
        + "(?:PtrSafe" + VbaIdentifier.RegexWhitespace + "+)?"
        + "(?<kind>Sub|Function)" + VbaIdentifier.RegexWhitespace + "+"
        + "(?<name>(?>" + VbaIdentifier.RegexIdentifierCandidate + "))"
        + VbaIdentifier.RegexWhitespace + "+Lib" + VbaIdentifier.RegexWhitespace + "+\"[^\"]+\""
        + "(?:" + VbaIdentifier.RegexWhitespace + "+Alias" + VbaIdentifier.RegexWhitespace + "+\"[^\"]+\")?"
        + VbaIdentifier.RegexWhitespace + "*(?:\\((?<parameters>.*)\\))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EventPattern = new(
        "^" + VbaIdentifier.RegexWhitespace + "*"
        + "(?:(?<visibility>Public|Private|Friend)" + VbaIdentifier.RegexWhitespace + "+)?"
        + "Event" + VbaIdentifier.RegexWhitespace + "+"
        + "(?<name>(?>" + VbaIdentifier.RegexIdentifierCandidate + "))"
        + VbaIdentifier.RegexWhitespace + "*(?:\\((?<parameters>.*)\\))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EnumPattern = new(
        "^" + VbaIdentifier.RegexWhitespace + "*"
        + "(?:(?<visibility>Public|Private|Friend)" + VbaIdentifier.RegexWhitespace + "+)?"
        + "Enum" + VbaIdentifier.RegexWhitespace + "+"
        + "(?<name>(?>" + VbaIdentifier.RegexIdentifierCandidate + "))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex TypePattern = new(
        "^" + VbaIdentifier.RegexWhitespace + "*"
        + "(?:(?<visibility>Public|Private|Friend)" + VbaIdentifier.RegexWhitespace + "+)?"
        + "Type" + VbaIdentifier.RegexWhitespace + "+"
        + "(?<name>(?>" + VbaIdentifier.RegexIdentifierCandidate + "))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ConstPattern = new(
        "^" + VbaIdentifier.RegexWhitespace + "*"
        + "(?:(?<visibility>Public|Private|Friend|Global)" + VbaIdentifier.RegexWhitespace + "+)?"
        + "Const" + VbaIdentifier.RegexWhitespace + "+(?<declarations>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ModuleVariablePattern = new(
        "^" + VbaIdentifier.RegexWhitespace + "*"
        + "(?<visibility>Public|Private|Friend|Global|Dim)"
        + VbaIdentifier.RegexWhitespace + "+(?<declarations>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LocalVariablePattern = new(
        "^" + VbaIdentifier.RegexWhitespace + "*"
        + "(?:Dim|(?<static>Static))"
        + VbaIdentifier.RegexWhitespace + "+(?<declarations>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses one module source document.
    /// </summary>
    /// <param name="uri">The document URI used for module kind and fallback identity inference.</param>
    /// <param name="source">The complete source text to parse.</param>
    /// <returns>The parsed syntax tree.</returns>
    public static VbaSyntaxTree ParseModule(string uri, string source)
    {
        var sourceText = VbaSourceText.From(source);
        var tokenStream = VbaTokenStream.FromSourceText(sourceText);
        var physicalAnalysisSourceText = MaskPreprocessorDirectives(
            sourceText,
            tokenStream,
            out var hasPreprocessorDirectives);
        var kind = GetModuleKind(uri);
        var diagnostics = new List<VbaSyntaxDiagnostic>();
        var codeStartLine = 0;
        VbaFormDesignerBlock? designerBlock = null;

        if (kind == VbaModuleKind.FormModule)
        {
            var boundaryLine = FindAttributeNameLine(sourceText);
            if (boundaryLine is null)
            {
                designerBlock = new VbaFormDesignerBlock(source, sourceText.FullRange);
                diagnostics.Add(new VbaSyntaxDiagnostic(
                    "syntax.formCodeSectionBoundaryMissing",
                    "Form module is missing an Attribute VB_Name code-section boundary.",
                    sourceText.FullRange));
                codeStartLine = sourceText.Lines.Count;
            }
            else
            {
                codeStartLine = boundaryLine.LineNumber;
                var boundaryStart = sourceText.PositionAt(boundaryLine.StartOffset);
                designerBlock = new VbaFormDesignerBlock(
                    source[..boundaryLine.StartOffset],
                    new VbaSyntaxRange(sourceText.StartPosition, boundaryStart));
            }
        }

        var attributes = ParseAttributes(physicalAnalysisSourceText, codeStartLine);
        var options = ParseOptions(physicalAnalysisSourceText, codeStartLine);
        var identity = CreateIdentity(uri, sourceText, kind, attributes);
        var parsedPreprocessor = hasPreprocessorDirectives
            ? VbaPreprocessorParser.Parse(
                sourceText,
                tokenStream,
                codeStartLine)
            : ParsedPreprocessor.Empty;
        var parsedMembers = ParseMembersAndDeclarations(
            physicalAnalysisSourceText,
            codeStartLine,
            parsedPreprocessor.Blocks);
        var parsedStatements = ParseStatementsAndDiagnostics(
            physicalAnalysisSourceText,
            codeStartLine);
        var parsedExpressions = ParseExpressions(sourceText, tokenStream, codeStartLine);
        var completionFacts = VbaCompletionSyntaxFactsParser.Parse(
            sourceText,
            tokenStream,
            parsedMembers.CallableDeclarations,
            parsedPreprocessor.Blocks,
            codeStartLine);
        diagnostics.AddRange(parsedStatements.Diagnostics);
        diagnostics.AddRange(parsedPreprocessor.Diagnostics);
        var module = new VbaModuleSyntax(
            kind,
            identity,
            attributes,
            options,
            parsedMembers.Members,
            parsedMembers.Declarations,
            parsedMembers.CallableDeclarations,
            parsedStatements.Statements,
            parsedExpressions.Expressions,
            parsedExpressions.ArgumentLists,
            completionFacts.Blocks,
            completionFacts.LineLabels,
            parsedPreprocessor.Directives,
            parsedPreprocessor.Blocks,
            designerBlock,
            codeStartLine,
            sourceText.FullRange);
        return new VbaSyntaxTree(uri, sourceText, tokenStream, module, diagnostics);
    }

    private static VbaSourceText MaskPreprocessorDirectives(
        VbaSourceText sourceText,
        VbaTokenStream tokenStream,
        out bool hasPreprocessorDirectives)
    {
        hasPreprocessorDirectives = tokenStream.Tokens.Any(
            token => token.Kind == VbaTokenKind.PreprocessorDirective);
        if (!hasPreprocessorDirectives)
        {
            return sourceText;
        }

        var characters = sourceText.Text.ToCharArray();
        foreach (var directive in tokenStream.Tokens)
        {
            if (directive.Kind != VbaTokenKind.PreprocessorDirective)
            {
                continue;
            }

            for (var offset = directive.Range.Start.Offset;
                offset < directive.Range.End.Offset;
                offset++)
            {
                if (characters[offset] is not '\r' and not '\n')
                {
                    characters[offset] = ' ';
                }
            }
        }

        return VbaSourceText.From(new string(characters));
    }

    private static IReadOnlyList<VbaModuleAttributeSyntax> ParseAttributes(VbaSourceText sourceText, int startLine)
    {
        var attributes = new List<VbaModuleAttributeSyntax>();
        for (var index = startLine; index < sourceText.Lines.Count; index++)
        {
            if (sourceText.IsBlankLine(index))
            {
                continue;
            }

            var line = sourceText.Lines[index];
            if (!VbaIdentifier.TrimStartWhitespace(line.Text).StartsWith(
                "Attribute",
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = MatchLexIdentifier(AttributePattern, line.Text);
            if (!match.Success)
            {
                continue;
            }

            var nameGroup = match.Groups["name"];
            var valueGroup = match.Groups["value"];
            var rawValue = VbaIdentifier.TrimWhitespace(valueGroup.Value);
            var value = UnquoteAttributeValue(rawValue);
            var valueOffsetInGroup = valueGroup.Value.IndexOf(value, StringComparison.Ordinal);
            var valueStartCharacter = valueGroup.Index + Math.Max(0, valueOffsetInGroup);
            attributes.Add(new VbaModuleAttributeSyntax(
                nameGroup.Value,
                value,
                sourceText.RangeForLine(line, match.Index, match.Index + match.Length),
                sourceText.RangeForLine(line, nameGroup.Index, nameGroup.Index + nameGroup.Length),
                sourceText.RangeForLine(line, valueStartCharacter, valueStartCharacter + value.Length)));
        }

        return attributes;
    }

    private static IReadOnlyList<VbaModuleOptionSyntax> ParseOptions(VbaSourceText sourceText, int startLine)
    {
        var options = new List<VbaModuleOptionSyntax>();
        for (var index = startLine; index < sourceText.Lines.Count; index++)
        {
            if (sourceText.IsBlankLine(index))
            {
                continue;
            }

            var line = sourceText.Lines[index];
            if (!VbaIdentifier.TrimStartWhitespace(line.Text).StartsWith(
                "Option",
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = OptionPattern.Match(line.Text);
            if (!match.Success)
            {
                continue;
            }

            var text = VbaIdentifier.TrimWhitespace(match.Value);
            var startCharacter = line.Text.IndexOf(text, StringComparison.Ordinal);
            options.Add(new VbaModuleOptionSyntax(
                text,
                sourceText.RangeForLine(line, startCharacter, startCharacter + text.Length)));
        }

        return options;
    }

    private static ParsedExpressions ParseExpressions(
        VbaSourceText sourceText,
        VbaTokenStream tokenStream,
        int codeStartLine)
    {
        var expressions = new List<VbaExpressionSyntax>();
        var argumentLists = VbaCallSyntaxParser.ParseCompleteArgumentLists(
            sourceText,
            tokenStream,
            codeStartLine);
        foreach (var statement in CreateLogicalStatements(sourceText, codeStartLine))
        {
            var trimmed = VbaIdentifier.TrimStartWhitespace(statement.Text);
            if (VbaIdentifier.IsWhitespaceOnly(trimmed)
                || MatchLexIdentifier(AttributePattern, trimmed).Success
                || OptionPattern.IsMatch(trimmed)
                || trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            const string withKeyword = "With";
            if (trimmed.Length > withKeyword.Length
                && trimmed.StartsWith(withKeyword, StringComparison.OrdinalIgnoreCase)
                && VbaIdentifier.IsWhitespace(trimmed[withKeyword.Length]))
            {
                expressions.Add(new VbaExpressionSyntax(
                    VbaExpressionKind.WithReceiver,
                    VbaIdentifier.TrimWhitespace(trimmed[withKeyword.Length..]),
                    statement.Range,
                    statement.IsContinued));
            }

            if (statement.Text.Contains('.', StringComparison.Ordinal))
            {
                expressions.Add(new VbaExpressionSyntax(
                    VbaExpressionKind.MemberAccess,
                    statement.Text,
                    statement.Range,
                    statement.IsContinued));
            }

            if (statement.Text.Contains('=', StringComparison.Ordinal))
            {
                expressions.Add(new VbaExpressionSyntax(
                    VbaExpressionKind.AssignmentExpression,
                    statement.Text,
                    statement.Range,
                    statement.IsContinued));
            }

            foreach (var argumentList in argumentLists.Where(argumentList =>
                argumentList.Range.Start.Offset >= statement.Range.Start.Offset
                && argumentList.Range.End.Offset <= statement.Range.End.Offset))
            {
                expressions.Add(new VbaExpressionSyntax(
                    VbaExpressionKind.ArgumentList,
                    statement.Text,
                    argumentList.Range,
                    argumentList.IsContinued));
            }
        }

        return new ParsedExpressions(expressions, argumentLists);
    }

    private static IReadOnlyList<LogicalStatement> CreateLogicalStatements(VbaSourceText sourceText, int codeStartLine)
    {
        var statements = new List<LogicalStatement>();
        for (var lineIndex = codeStartLine; lineIndex < sourceText.Lines.Count; lineIndex++)
        {
            if (sourceText.IsBlankLine(lineIndex))
            {
                continue;
            }

            var statement = CreateLogicalStatement(sourceText, lineIndex);
            statements.Add(statement);
            lineIndex = statement.Range.End.Line;
        }

        return statements;
    }

    private static LogicalStatement CreateLogicalStatement(VbaSourceText sourceText, int startLineIndex)
    {
        var startLine = sourceText.Lines[startLineIndex];
        var logicalText = new List<char>();
        var sourcePositions = new List<VbaSyntaxPosition?>();
        var endLine = startLine;
        var isContinued = false;

        for (var lineIndex = startLineIndex; lineIndex < sourceText.Lines.Count; lineIndex++)
        {
            var line = sourceText.Lines[lineIndex];
            endLine = line;
            var codeText = VbaSourceText.StripApostropheComment(line.Text);
            var hasContinuation = VbaSourceText.HasLineContinuation(codeText);
            var part = hasContinuation ? VbaSourceText.RemoveLineContinuation(codeText) : codeText;
            for (var character = 0; character < part.Length; character++)
            {
                logicalText.Add(part[character]);
                sourcePositions.Add(new VbaSyntaxPosition(line.LineNumber, character, line.StartOffset + character));
            }

            if (!hasContinuation)
            {
                break;
            }

            isContinued = true;
            logicalText.Add(' ');
            sourcePositions.Add(null);
        }

        return new LogicalStatement(
            new string(logicalText.ToArray()),
            sourcePositions,
            new VbaSyntaxRange(
                new VbaSyntaxPosition(startLine.LineNumber, 0, startLine.StartOffset),
                new VbaSyntaxPosition(endLine.LineNumber, endLine.Text.Length, endLine.EndOffset)),
            isContinued);
    }

    private static VbaSyntaxRange RangeFromLogicalSpan(LogicalStatement statement, int startIndex, int endIndex)
    {
        var startPosition = FindMappedPosition(statement, startIndex, searchForward: true)
            ?? statement.Range.Start;
        var endPosition = FindMappedPosition(statement, Math.Max(startIndex, endIndex - 1), searchForward: false);
        if (endPosition is null)
        {
            return new VbaSyntaxRange(startPosition, startPosition);
        }

        return new VbaSyntaxRange(
            startPosition,
            new VbaSyntaxPosition(endPosition.Line, endPosition.Character + 1, endPosition.Offset + 1));
    }

    private static VbaSyntaxPosition? FindMappedPosition(
        LogicalStatement statement,
        int index,
        bool searchForward)
    {
        if (statement.SourcePositions.Count == 0)
        {
            return null;
        }

        var current = Math.Clamp(index, 0, statement.SourcePositions.Count - 1);
        while (current >= 0 && current < statement.SourcePositions.Count)
        {
            var position = statement.SourcePositions[current];
            if (position is not null)
            {
                return position;
            }

            current += searchForward ? 1 : -1;
        }

        return null;
    }

    private static ParsedMembers ParseMembersAndDeclarations(
        VbaSourceText sourceText,
        int codeStartLine,
        IReadOnlyList<VbaPreprocessorBlockSyntax> preprocessorBlocks)
    {
        var members = new List<VbaModuleMemberSyntax>();
        var declarations = new List<VbaDeclarationSyntax>();
        var callableDeclarations = new List<VbaCallableDeclarationSyntax>();

        for (var lineIndex = codeStartLine; lineIndex < sourceText.Lines.Count; lineIndex++)
        {
            var line = sourceText.Lines[lineIndex];
            if (sourceText.IsBlankLine(lineIndex))
            {
                continue;
            }

            var codeLine = VbaSourceText.StripApostropheComment(line.Text);
            if (VbaIdentifier.IsWhitespaceOnly(codeLine))
            {
                continue;
            }

            var declareMatch = MatchIdentifier(DeclarePattern, codeLine);
            if (declareMatch.Success)
            {
                var declaration = CreateCallableDeclaration(
                    sourceText,
                    declareMatch,
                    line,
                    lineIndex,
                    preprocessorBlocks,
                    isExternal: true);
                members.Add(new VbaModuleMemberSyntax(
                    declaration.Name,
                    declaration.Kind,
                    declaration.BlockRange,
                    IsExternal: true));
                callableDeclarations.Add(declaration);
                declarations.Add(CreateCallableSourceDeclaration(declaration));
                foreach (var parameter in declaration.Parameters)
                {
                    declarations.Add(CreateParameterDeclaration(parameter, parameter.Range.Start.Line));
                }

                continue;
            }

            var eventMatch = MatchIdentifier(EventPattern, codeLine);
            if (eventMatch.Success)
            {
                var documentation = ParseDocumentationComment(sourceText.Lines, lineIndex);
                var name = eventMatch.Groups["name"].Value;
                var parameters = ParseParameterSyntax(sourceText, eventMatch, line, documentation);
                members.Add(CreateSingleLineMember(
                    sourceText,
                    eventMatch,
                    "name",
                    VbaDeclarationKind.Event,
                    line));
                declarations.Add(CreateDeclaration(
                    sourceText,
                    eventMatch,
                    "name",
                    VbaDeclarationKind.Event,
                    GetVisibility(eventMatch.Groups["visibility"].Value, defaultPublic: true),
                    line,
                    documentation: documentation?.HoverText,
                    signature: CreateSignature(name, parameters, null, documentation),
                    declarationLabel: CreateDeclarationLabel("Event", name, parameters),
                    callableKind: "Event"));
                foreach (var parameter in parameters)
                {
                    declarations.Add(CreateParameterDeclaration(parameter, parameter.Range.Start.Line));
                }

                continue;
            }

            var enumMatch = MatchIdentifier(EnumPattern, codeLine);
            if (enumMatch.Success)
            {
                var visibility = GetVisibility(enumMatch.Groups["visibility"].Value, defaultPublic: true);
                declarations.Add(CreateDeclaration(
                    sourceText,
                    enumMatch,
                    "name",
                    VbaDeclarationKind.Enum,
                    visibility,
                    line,
                    declarationLabel: CreateDeclarationLabel("Enum", enumMatch.Groups["name"].Value)));
                var endLine = FindBlockEndLine(
                    sourceText,
                    lineIndex,
                    lineIndex + 1,
                    "Enum",
                    preprocessorBlocks);
                AddMemberDeclarations(
                    sourceText,
                    declarations,
                    lineIndex + 1,
                    endLine,
                    VbaDeclarationKind.EnumMember,
                    visibility,
                    enumMatch.Groups["name"].Value);
                members.Add(new VbaModuleMemberSyntax(
                    enumMatch.Groups["name"].Value,
                    VbaDeclarationKind.Enum,
                    CreateBlockRange(sourceText.Lines, lineIndex, endLine)));
                lineIndex = endLine;
                continue;
            }

            var typeMatch = MatchIdentifier(TypePattern, codeLine);
            if (typeMatch.Success)
            {
                var visibility = GetVisibility(typeMatch.Groups["visibility"].Value, defaultPublic: true);
                declarations.Add(CreateDeclaration(
                    sourceText,
                    typeMatch,
                    "name",
                    VbaDeclarationKind.Type,
                    visibility,
                    line,
                    declarationLabel: CreateDeclarationLabel("Type", typeMatch.Groups["name"].Value)));
                var endLine = FindBlockEndLine(
                    sourceText,
                    lineIndex,
                    lineIndex + 1,
                    "Type",
                    preprocessorBlocks);
                AddMemberDeclarations(
                    sourceText,
                    declarations,
                    lineIndex + 1,
                    endLine,
                    VbaDeclarationKind.TypeMember,
                    visibility,
                    typeMatch.Groups["name"].Value);
                members.Add(new VbaModuleMemberSyntax(
                    typeMatch.Groups["name"].Value,
                    VbaDeclarationKind.Type,
                    CreateBlockRange(sourceText.Lines, lineIndex, endLine)));
                lineIndex = endLine;
                continue;
            }

            var constMatch = ConstPattern.Match(codeLine);
            if (constMatch.Success)
            {
                var documentation = ParseDocumentationComment(sourceText.Lines, lineIndex);
                var visibility = GetVisibility(constMatch.Groups["visibility"].Value, defaultPublic: true);
                foreach (var declaration in ParseVariableLikeDeclarations(
                    sourceText,
                    constMatch.Groups["declarations"],
                    line,
                    VbaDeclarationKind.Constant,
                    visibility,
                    documentation?.HoverText))
                {
                    members.Add(new VbaModuleMemberSyntax(declaration.Name, declaration.Kind, CreateLineRange(line)));
                    declarations.Add(declaration);
                }

                continue;
            }

            var procedureMatch = MatchIdentifier(ProcedurePattern, codeLine);
            if (procedureMatch.Success)
            {
                var procedureStatement = CreateLogicalStatement(sourceText, lineIndex);
                procedureMatch = MatchIdentifier(ProcedurePattern, procedureStatement.Text);
                var declaration = CreateCallableDeclaration(
                    sourceText,
                    procedureMatch,
                    procedureStatement,
                    lineIndex,
                    preprocessorBlocks,
                    isStatic: procedureMatch.Groups["static"].Success);
                members.Add(new VbaModuleMemberSyntax(
                    declaration.Name,
                    declaration.Kind,
                    declaration.BlockRange,
                    IsStatic: declaration.IsStatic));
                callableDeclarations.Add(declaration);
                declarations.Add(CreateCallableSourceDeclaration(declaration));
                foreach (var parameter in declaration.Parameters)
                {
                    declarations.Add(CreateParameterDeclaration(
                        parameter,
                        parameter.Range.Start.Line,
                        declaration.Name,
                        declaration.BlockRange));
                }

                AddLocalVariableDeclarations(
                    sourceText,
                    declarations,
                    declaration.LineIndex + 1,
                    declaration.BlockRange.End.Line,
                    declaration.Name,
                    declaration.BlockRange);
                lineIndex = declaration.BlockRange.End.Line;
                continue;
            }

            var variableMatch = ModuleVariablePattern.Match(codeLine);
            if (variableMatch.Success && IsModuleVariableDeclaration(codeLine))
            {
                var visibility = GetVisibility(variableMatch.Groups["visibility"].Value, defaultPublic: false);
                foreach (var declaration in ParseVariableLikeDeclarations(
                    sourceText,
                    variableMatch.Groups["declarations"],
                    line,
                    VbaDeclarationKind.Variable,
                    visibility,
                    isWithEventsDefault: IsWithEventsVariableDeclaration(codeLine)))
                {
                    members.Add(new VbaModuleMemberSyntax(declaration.Name, declaration.Kind, CreateLineRange(line)));
                    declarations.Add(declaration);
                }
            }
        }

        return new ParsedMembers(members, declarations, callableDeclarations);
    }

    private static ParsedStatements ParseStatementsAndDiagnostics(VbaSourceText sourceText, int codeStartLine)
    {
        var statements = new List<VbaStatementSyntax>();
        var diagnostics = new List<VbaSyntaxDiagnostic>();
        var blockStack = new Stack<BlockFrame>();
        var inLogicalContinuation = false;

        for (var lineIndex = codeStartLine; lineIndex < sourceText.Lines.Count; lineIndex++)
        {
            var line = sourceText.Lines[lineIndex];
            if (sourceText.IsBlankLine(lineIndex))
            {
                inLogicalContinuation = false;
                continue;
            }

            var lineContinuationDiagnostics = CollectLineContinuationDiagnostics(line).ToArray();
            diagnostics.AddRange(lineContinuationDiagnostics);
            diagnostics.AddRange(CollectStringDiagnostics(line));
            diagnostics.AddRange(CollectRaiseEventDiagnostics(line));

            var codeLine = VbaSourceText.StripApostropheComment(line.Text);
            var hasValidLineContinuation = VbaSourceText.HasLineContinuation(codeLine)
                && !lineContinuationDiagnostics.Any(diagnostic =>
                    diagnostic.Code == "syntax.invalidTrailingCommentContinuation");
            if (inLogicalContinuation)
            {
                inLogicalContinuation = hasValidLineContinuation;
                continue;
            }

            if (VbaIdentifier.IsWhitespaceOnly(codeLine)
                || MatchLexIdentifier(AttributePattern, codeLine).Success
                || OptionPattern.IsMatch(codeLine)
                || VbaIdentifier.TrimStartWhitespace(codeLine).StartsWith("#", StringComparison.Ordinal))
            {
                inLogicalContinuation = hasValidLineContinuation;
                continue;
            }

            var statementText = line.Text;
            var statementRange = CreateLineRange(line);
            var trimmed = VbaIdentifier.TrimStartWhitespace(codeLine);
            if (hasValidLineContinuation)
            {
                var logicalStatement = CreateLogicalStatement(sourceText, lineIndex);
                statementText = logicalStatement.Text;
                statementRange = logicalStatement.Range;
                trimmed = VbaIdentifier.TrimStartWhitespace(logicalStatement.Text);
            }

            if (IsMalformedDeclarationHeader(trimmed))
            {
                diagnostics.Add(new VbaSyntaxDiagnostic(
                    "syntax.malformedDeclarationHeader",
                    "Declaration header is malformed.",
                    statementRange));
                statements.Add(new VbaStatementSyntax(VbaStatementKind.Malformed, statementText, statementRange, IsMalformed: true));
                inLogicalContinuation = hasValidLineContinuation;
                continue;
            }

            if (TryCloseBlock(trimmed, blockStack, out var unexpectedClose))
            {
                if (unexpectedClose is not null)
                {
                    diagnostics.Add(new VbaSyntaxDiagnostic(
                        "syntax.unexpectedStatementBoundaryToken",
                        $"Unexpected statement-boundary token '{unexpectedClose}'.",
                        statementRange));
                    statements.Add(new VbaStatementSyntax(VbaStatementKind.Malformed, statementText, statementRange, IsMalformed: true));
                }

                inLogicalContinuation = hasValidLineContinuation;
                continue;
            }

            var statementKind = VbaBlockSyntaxFacts.ClassifyStatement(
                trimmed,
                MatchIdentifier(ProcedurePattern, trimmed).Success);
            statements.Add(new VbaStatementSyntax(
                statementKind,
                statementText,
                statementRange,
                IsMalformed: statementKind == VbaStatementKind.Malformed));

            if (statementKind == VbaStatementKind.Malformed)
            {
                diagnostics.Add(new VbaSyntaxDiagnostic(
                    "syntax.unexpectedStatementBoundaryToken",
                    "Unexpected token at statement boundary.",
                    statementRange));
                inLogicalContinuation = hasValidLineContinuation;
                continue;
            }

            var expectedTerminator = VbaBlockSyntaxFacts.GetExpectedStatementTerminator(trimmed, statementKind);
            if (expectedTerminator is not null)
            {
                blockStack.Push(new BlockFrame(statementKind, expectedTerminator, statementRange));
            }

            inLogicalContinuation = hasValidLineContinuation;
        }

        foreach (var block in blockStack)
        {
            diagnostics.Add(new VbaSyntaxDiagnostic(
                "syntax.missingBlockTerminator",
                $"Block is missing '{block.ExpectedTerminator}'.",
                block.Range));
        }

        return new ParsedStatements(statements, diagnostics);
    }

    private static IEnumerable<VbaSyntaxDiagnostic> CollectRaiseEventDiagnostics(VbaSourceLine line)
    {
        var codeLine = VbaSourceText.StripApostropheComment(line.Text);
        var index = SkipWhitespace(codeLine, 0);
        const string keyword = "RaiseEvent";
        if (!StartsWithKeyword(codeLine, index, keyword))
        {
            yield break;
        }

        index += keyword.Length;
        var afterKeyword = SkipWhitespace(codeLine, index);
        if (afterKeyword == index)
        {
            yield break;
        }

        var eventNameEnd = ReadIdentifierEnd(codeLine, afterKeyword);
        if (eventNameEnd == afterKeyword)
        {
            yield break;
        }

        var argumentStart = SkipWhitespace(codeLine, eventNameEnd);
        if (argumentStart >= codeLine.Length || codeLine[argumentStart] == '(')
        {
            yield break;
        }

        yield return new VbaSyntaxDiagnostic(
            "syntax.raiseEventArgumentListRequiresParentheses",
            "RaiseEvent arguments must be enclosed in parentheses.",
            new VbaSyntaxRange(
                new VbaSyntaxPosition(line.LineNumber, argumentStart, line.StartOffset + argumentStart),
                new VbaSyntaxPosition(line.LineNumber, codeLine.Length, line.StartOffset + codeLine.Length)));
    }

    private static IEnumerable<VbaSyntaxDiagnostic> CollectLineContinuationDiagnostics(VbaSourceLine line)
    {
        var commentStart = VbaSourceText.FindApostropheCommentStart(line.Text);
        if (commentStart < 0)
        {
            yield break;
        }

        var codePart = line.Text[..commentStart];
        var underscoreIndex = codePart.LastIndexOf('_');
        if (underscoreIndex >= 0
            && VbaIdentifier.TrimEndWhitespace(codePart).EndsWith('_'))
        {
            yield return new VbaSyntaxDiagnostic(
                "syntax.invalidTrailingCommentContinuation",
                "Code line-continuation marker cannot be followed by a comment.",
                new VbaSyntaxRange(
                    new VbaSyntaxPosition(line.LineNumber, underscoreIndex, line.StartOffset + underscoreIndex),
                    new VbaSyntaxPosition(line.LineNumber, line.Text.Length, line.EndOffset)));
        }
    }

    private static IEnumerable<VbaSyntaxDiagnostic> CollectStringDiagnostics(VbaSourceLine line)
    {
        if (IsRemCommentLine(line.Text))
        {
            yield break;
        }

        var inString = false;
        var stringStart = -1;
        for (var index = 0; index < line.Text.Length; index++)
        {
            var current = line.Text[index];
            if (!inString && current == '\'')
            {
                break;
            }

            if (current != '"')
            {
                continue;
            }

            if (inString && index + 1 < line.Text.Length && line.Text[index + 1] == '"')
            {
                index++;
                continue;
            }

            inString = !inString;
            if (inString)
            {
                stringStart = index;
            }
        }

        if (inString)
        {
            yield return new VbaSyntaxDiagnostic(
                "syntax.unterminatedStringLiteral",
                "String literal is missing a closing double quote.",
                new VbaSyntaxRange(
                    new VbaSyntaxPosition(line.LineNumber, stringStart, line.StartOffset + stringStart),
                    new VbaSyntaxPosition(line.LineNumber, line.Text.Length, line.EndOffset)));
        }
    }

    private static bool TryCloseBlock(string trimmedLine, Stack<BlockFrame> blockStack, out string? unexpectedClose)
    {
        unexpectedClose = null;
        var closeTerminator = VbaBlockSyntaxFacts.GetStatementCloseTerminator(trimmedLine);
        if (closeTerminator is null)
        {
            return false;
        }

        if (blockStack.Count == 0)
        {
            unexpectedClose = closeTerminator;
            return true;
        }

        if (!blockStack.Peek().ExpectedTerminator.Equals(closeTerminator, StringComparison.OrdinalIgnoreCase))
        {
            unexpectedClose = closeTerminator;
            return true;
        }

        blockStack.Pop();
        return true;
    }

    private static bool IsMalformedDeclarationHeader(string trimmedLine)
    {
        var tokens = VbaTokenStream.FromText(trimmedLine).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.LineContinuation
                and not VbaTokenKind.Comment)
            .ToArray();
        var index = 0;
        if (index < tokens.Length
            && tokens[index].Kind == VbaTokenKind.Keyword
            && (tokens[index].Text.Equals("Public", StringComparison.OrdinalIgnoreCase)
                || tokens[index].Text.Equals("Private", StringComparison.OrdinalIgnoreCase)
                || tokens[index].Text.Equals("Friend", StringComparison.OrdinalIgnoreCase)
                || tokens[index].Text.Equals("Global", StringComparison.OrdinalIgnoreCase)))
        {
            index++;
        }

        if (index < tokens.Length
            && tokens[index].Kind == VbaTokenKind.Keyword
            && tokens[index].Text.Equals("Static", StringComparison.OrdinalIgnoreCase))
        {
            index++;
        }

        if (index >= tokens.Length
            || tokens[index].Kind != VbaTokenKind.Keyword
            || !tokens[index].Text.Equals("Sub", StringComparison.OrdinalIgnoreCase)
                && !tokens[index].Text.Equals("Function", StringComparison.OrdinalIgnoreCase)
                && !tokens[index].Text.Equals("Property", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !MatchIdentifier(ProcedurePattern, trimmedLine).Success;
    }

    private static VbaCallableDeclarationSyntax CreateCallableDeclaration(
        VbaSourceText sourceText,
        Match match,
        VbaSourceLine line,
        int lineIndex,
        IReadOnlyList<VbaPreprocessorBlockSyntax> preprocessorBlocks,
        bool isExternal = false,
        bool isStatic = false)
    {
        var name = match.Groups["name"].Value;
        var documentation = ParseDocumentationComment(sourceText.Lines, lineIndex);
        var parameters = ParseParameterSyntax(
            sourceText,
            match,
            line,
            documentation,
            allowAnyTypeReference: isExternal);
        var typeReference = ParseReturnTypeReference(match, line.Text);
        var signature = CreateSignature(name, parameters, typeReference, documentation);
        var kind = match.Groups["kind"].Success && !match.Groups["propertyKind"].Success
            ? VbaDeclarationKind.Procedure
            : VbaDeclarationKind.Property;
        var endKeyword = isExternal
            ? null
            : kind == VbaDeclarationKind.Property
                ? "Property"
                : match.Groups["kind"].Value;
        var endLine = endKeyword is null
            ? lineIndex
            : FindBlockEndLine(
                sourceText,
                lineIndex,
                lineIndex + 1,
                endKeyword,
                preprocessorBlocks);

        return new VbaCallableDeclarationSyntax(
            name,
            kind,
            GetVisibility(match.Groups["visibility"].Value, defaultPublic: true),
            CreateRange(sourceText, match, "name", line),
            CreateBlockRange(sourceText.Lines, lineIndex, endLine),
            parameters,
            documentation?.HoverText,
            signature,
            typeReference,
            lineIndex,
            line.Text,
            IsExternal: isExternal,
            IsStatic: isStatic,
            DeclarationKeyword: GetDeclarationKeyword(match),
            PropertyAccessorKind: GetPropertyAccessorKind(match),
            VisibilityKeyword: match.Groups["visibility"].Value);
    }

    private static VbaCallableDeclarationSyntax CreateCallableDeclaration(
        VbaSourceText sourceText,
        Match match,
        LogicalStatement statement,
        int lineIndex,
        IReadOnlyList<VbaPreprocessorBlockSyntax> preprocessorBlocks,
        bool isStatic = false)
    {
        var name = match.Groups["name"].Value;
        var documentation = ParseDocumentationComment(sourceText.Lines, lineIndex);
        var parameters = ParseParameterSyntax(match, statement, documentation);
        var typeReference = ParseReturnTypeReference(match, statement.Text);
        var signature = CreateSignature(name, parameters, typeReference, documentation);
        var kind = match.Groups["kind"].Success && !match.Groups["propertyKind"].Success
            ? VbaDeclarationKind.Procedure
            : VbaDeclarationKind.Property;
        var endKeyword = kind == VbaDeclarationKind.Property
            ? "Property"
            : match.Groups["kind"].Value;
        var endLine = FindBlockEndLine(
            sourceText,
            lineIndex,
            statement.Range.End.Line + 1,
            endKeyword,
            preprocessorBlocks);

        return new VbaCallableDeclarationSyntax(
            name,
            kind,
            GetVisibility(match.Groups["visibility"].Value, defaultPublic: true),
            RangeFromLogicalSpan(statement, match.Groups["name"].Index, match.Groups["name"].Index + name.Length),
            CreateBlockRange(sourceText.Lines, lineIndex, endLine),
            parameters,
            documentation?.HoverText,
            signature,
            typeReference,
            lineIndex,
            statement.Text,
            IsStatic: isStatic,
            DeclarationKeyword: GetDeclarationKeyword(match),
            PropertyAccessorKind: GetPropertyAccessorKind(match),
            VisibilityKeyword: match.Groups["visibility"].Value);
    }

    private static VbaDeclarationSyntax CreateCallableSourceDeclaration(VbaCallableDeclarationSyntax declaration)
        => new(
            declaration.Name,
            declaration.Kind,
            declaration.Visibility,
            declaration.Range,
            declaration.LineIndex,
            Documentation: declaration.Documentation,
            Signature: declaration.Signature,
            TypeReference: declaration.TypeReference,
            IsExternal: declaration.IsExternal,
            IsStatic: declaration.IsStatic,
            DeclarationLabel: CreateDeclarationLabel(declaration),
            CallableKind: declaration.DeclarationKeyword,
            PropertyAccessorKind: declaration.PropertyAccessorKind);

    private static VbaDeclarationSyntax CreateParameterDeclaration(
        VbaCallableParameterSyntax parameter,
        int lineIndex,
        string? parentProcedureName = null,
        VbaSyntaxRange? parentProcedureRange = null)
        => new(
            parameter.Name,
            VbaDeclarationKind.Parameter,
            VbaDeclarationVisibility.Local,
            parameter.Range,
            lineIndex,
            Documentation: parameter.Documentation,
            ParentProcedureName: parentProcedureName,
            ParentProcedureRange: parentProcedureRange,
            TypeReference: parameter.TypeReference,
            DeclarationLabel: CreateParameterDeclarationLabel(parameter),
            IsArray: parameter.IsArray);

    private static VbaDeclarationSyntax CreateDeclaration(
        VbaSourceText sourceText,
        Match match,
        string groupName,
        VbaDeclarationKind kind,
        VbaDeclarationVisibility visibility,
        VbaSourceLine line,
        string? documentation = null,
        VbaCallableSignatureSyntax? signature = null,
        string? parentProcedureName = null,
        VbaSyntaxRange? parentProcedureRange = null,
        string? parentTypeName = null,
        VbaTypeReferenceSyntax? typeReference = null,
        bool isWithEvents = false,
        bool isExternal = false,
        bool isStatic = false,
        string? declarationLabel = null,
        string? callableKind = null)
    {
        var name = match.Groups[groupName].Value;
        return new VbaDeclarationSyntax(
            name,
            kind,
            visibility,
            CreateRange(sourceText, match, groupName, line),
            line.LineNumber,
            Documentation: documentation,
            Signature: signature,
            ParentProcedureName: parentProcedureName,
            ParentProcedureRange: parentProcedureRange,
            ParentTypeName: parentTypeName,
            TypeReference: typeReference,
            IsWithEvents: isWithEvents,
            IsExternal: isExternal,
            IsStatic: isStatic,
            DeclarationLabel: declarationLabel,
            CallableKind: callableKind);
    }

    private static VbaModuleMemberSyntax CreateSingleLineMember(
        VbaSourceText sourceText,
        Match match,
        string groupName,
        VbaDeclarationKind kind,
        VbaSourceLine line)
        => new(
            match.Groups[groupName].Value,
            kind,
            CreateLineRange(line));

    private static void AddMemberDeclarations(
        VbaSourceText sourceText,
        ICollection<VbaDeclarationSyntax> declarations,
        int startLine,
        int endLine,
        VbaDeclarationKind kind,
        VbaDeclarationVisibility visibility,
        string parentTypeName)
    {
        for (var lineIndex = startLine; lineIndex < endLine; lineIndex++)
        {
            var line = sourceText.Lines[lineIndex];
            var codeLine = VbaSourceText.StripApostropheComment(line.Text);
            var nameToken = VbaTokenStream.FromText(codeLine).Tokens.FirstOrDefault(
                token => token.Kind is not VbaTokenKind.Whitespace
                    and not VbaTokenKind.LineContinuation);
            if (nameToken is null || !VbaIdentifierSyntaxFacts.IsValidDeclaredName(nameToken))
            {
                continue;
            }

            var typeReference = ParseTypeReference(line.Text);
            var isArray = IsArrayParameter(codeLine, nameToken.Text);
            declarations.Add(new VbaDeclarationSyntax(
                nameToken.Text,
                kind,
                visibility,
                sourceText.RangeForLine(
                    line,
                    nameToken.Range.Start.Offset,
                    nameToken.Range.End.Offset),
                lineIndex,
                TypeReference: typeReference,
                DeclarationLabel: CreateValueDeclarationLabel(
                    kind,
                    nameToken.Text,
                    typeReference,
                    isArray: isArray),
                ParentTypeName: parentTypeName,
                IsArray: isArray));
        }
    }

    private static void AddLocalVariableDeclarations(
        VbaSourceText sourceText,
        ICollection<VbaDeclarationSyntax> declarations,
        int startLine,
        int endLine,
        string parentProcedureName,
        VbaSyntaxRange parentProcedureRange)
    {
        for (var lineIndex = startLine; lineIndex < endLine; lineIndex++)
        {
            var line = sourceText.Lines[lineIndex];
            var codeLine = VbaSourceText.StripApostropheComment(line.Text);
            var match = LocalVariablePattern.Match(codeLine);
            if (!match.Success)
            {
                continue;
            }

            foreach (var declaration in ParseVariableLikeDeclarations(
                sourceText,
                match.Groups["declarations"],
                line,
                VbaDeclarationKind.Variable,
                VbaDeclarationVisibility.Local,
                parentProcedureName: parentProcedureName,
                parentProcedureRange: parentProcedureRange,
                isStaticDefault: match.Groups["static"].Success))
            {
                declarations.Add(declaration);
            }
        }
    }

    private static IReadOnlyList<VbaDeclarationSyntax> ParseVariableLikeDeclarations(
        VbaSourceText sourceText,
        Group declarationsGroup,
        VbaSourceLine line,
        VbaDeclarationKind kind,
        VbaDeclarationVisibility visibility,
        string? documentation = null,
        string? parentProcedureName = null,
        VbaSyntaxRange? parentProcedureRange = null,
        bool isWithEventsDefault = false,
        bool isStaticDefault = false)
    {
        var declarations = new List<VbaDeclarationSyntax>();
        foreach (var segment in SplitDeclarationSegments(declarationsGroup.Value))
        {
            var segmentStart = declarationsGroup.Index + segment.Start;
            if (!TryReadDeclaredName(segment.Text, out var nameToken, out var hasWithEventsModifier))
            {
                continue;
            }

            var name = nameToken.Text;
            var nameStart = segmentStart + nameToken.Range.Start.Offset;
            var isWithEvents = isWithEventsDefault || hasWithEventsModifier;
            var isArray = IsArrayParameter(segment.Text, name);
            var typeReference = ParseTypeReference(segment.Text);
            declarations.Add(new VbaDeclarationSyntax(
                name,
                kind,
                visibility,
                sourceText.RangeForLine(line, nameStart, nameStart + name.Length),
                line.LineNumber,
                Documentation: documentation,
                DeclarationLabel: CreateValueDeclarationLabel(
                    kind,
                    name,
                    typeReference,
                    isWithEvents,
                    isStaticDefault,
                    isArray),
                ParentProcedureName: parentProcedureName,
                ParentProcedureRange: parentProcedureRange,
                TypeReference: typeReference,
                IsWithEvents: isWithEvents,
                IsStatic: isStaticDefault,
                IsArray: isArray));
        }

        return declarations;
    }

    private static bool TryReadDeclaredName(
        string text,
        out VbaToken nameToken,
        out bool hasWithEventsModifier)
    {
        hasWithEventsModifier = false;
        foreach (var token in VbaTokenStream.FromText(text).Tokens)
        {
            if (token.Kind == VbaTokenKind.Whitespace)
            {
                continue;
            }

            if (!hasWithEventsModifier
                && token.Kind == VbaTokenKind.Keyword
                && token.Text.Equals("WithEvents", StringComparison.OrdinalIgnoreCase))
            {
                hasWithEventsModifier = true;
                continue;
            }

            if (VbaIdentifierSyntaxFacts.IsValidDeclaredName(token))
            {
                nameToken = token;
                return true;
            }

            break;
        }

        nameToken = null!;
        return false;
    }

    private static IReadOnlyList<VbaCallableParameterSyntax> ParseParameterSyntax(
        VbaSourceText sourceText,
        Match match,
        VbaSourceLine line,
        DocumentationComment? documentation,
        bool allowAnyTypeReference = false)
    {
        var parametersGroup = match.Groups["parameters"];
        if (!parametersGroup.Success
            || VbaIdentifier.IsWhitespaceOnly(parametersGroup.Value))
        {
            return [];
        }

        var parameters = new List<VbaCallableParameterSyntax>();
        foreach (var segment in SplitDeclarationSegments(parametersGroup.Value))
        {
            var name = ParseParameterName(segment.Text);
            if (name is null)
            {
                continue;
            }

            var nameOffset = segment.Text.IndexOf(name, StringComparison.Ordinal);
            var start = parametersGroup.Index + segment.Start + nameOffset;
            parameters.Add(new VbaCallableParameterSyntax(
                name,
                sourceText.RangeForLine(line, start, start + name.Length),
                documentation?.ParameterDocs.TryGetValue(name, out var parameterDocumentation) == true
                    ? parameterDocumentation
                    : null,
                ParseTypeReference(segment.Text, allowAnyTypeReference),
                IsOptionalParameter(segment.Text),
                IsByRefParameter(segment.Text),
                IsParamArrayParameter(segment.Text),
                IsArrayParameter(segment.Text, name)));
        }

        return parameters;
    }

    private static IReadOnlyList<VbaCallableParameterSyntax> ParseParameterSyntax(
        Match match,
        LogicalStatement statement,
        DocumentationComment? documentation)
    {
        var parametersGroup = match.Groups["parameters"];
        if (!parametersGroup.Success
            || VbaIdentifier.IsWhitespaceOnly(parametersGroup.Value))
        {
            return [];
        }

        var parameters = new List<VbaCallableParameterSyntax>();
        foreach (var segment in SplitDeclarationSegments(parametersGroup.Value))
        {
            var name = ParseParameterName(segment.Text);
            if (name is null)
            {
                continue;
            }

            var nameOffset = segment.Text.IndexOf(name, StringComparison.Ordinal);
            var start = parametersGroup.Index + segment.Start + nameOffset;
            parameters.Add(new VbaCallableParameterSyntax(
                name,
                RangeFromLogicalSpan(statement, start, start + name.Length),
                documentation?.ParameterDocs.TryGetValue(name, out var parameterDocumentation) == true
                    ? parameterDocumentation
                    : null,
                ParseTypeReference(segment.Text),
                IsOptionalParameter(segment.Text),
                IsByRefParameter(segment.Text),
                IsParamArrayParameter(segment.Text),
                IsArrayParameter(segment.Text, name)));
        }

        return parameters;
    }

    private static VbaCallableSignatureSyntax CreateSignature(
        string name,
        IReadOnlyList<VbaCallableParameterSyntax> parameters,
        VbaTypeReferenceSyntax? returnTypeReference,
        DocumentationComment? documentation)
    {
        var returnTypeName = returnTypeReference?.Name;
        var label = $"{name}({string.Join(", ", parameters.Select(CreateSignatureParameterLabel))})";
        if (!string.IsNullOrEmpty(returnTypeName))
        {
            label = $"{label} As {returnTypeName}";
        }

        var documentationLines = new List<string>();
        if (!string.IsNullOrWhiteSpace(documentation?.Summary))
        {
            documentationLines.Add(documentation.Summary);
        }

        if (!string.IsNullOrWhiteSpace(documentation?.ReturnDocumentation))
        {
            if (documentationLines.Count > 0)
            {
                documentationLines.Add("");
            }

            documentationLines.Add($"@return {documentation.ReturnDocumentation}");
        }

        return new VbaCallableSignatureSyntax(
            label,
            parameters
                .Select(parameter => new VbaCallableParameterInfoSyntax(
                    parameter.Name,
                    parameter.Documentation,
                    parameter.IsOptional,
                    parameter.TypeReference,
                    parameter.IsByRef,
                    parameter.IsParamArray,
                    parameter.IsArray))
                .ToArray(),
            documentationLines.Count == 0 ? null : string.Join('\n', documentationLines));
    }

    private static string CreateSignatureParameterLabel(VbaCallableParameterSyntax parameter)
        => parameter.IsOptional ? $"[{parameter.Name}]" : parameter.Name;

    private static string CreateDeclarationLabel(VbaCallableDeclarationSyntax declaration)
    {
        var keyword = declaration.DeclarationKeyword ?? GetCallableKind(declaration.Kind, declaration.TypeReference);
        var declarePrefix = declaration.IsExternal ? "Declare " : "";
        var staticPrefix = declaration.IsStatic ? "Static " : "";
        return $"{staticPrefix}{declarePrefix}{keyword} {declaration.Signature.Label}";
    }

    private static string CreateDeclarationLabel(
        string keyword,
        string name,
        IReadOnlyList<VbaCallableParameterSyntax> parameters)
        => $"{keyword} {name}({string.Join(", ", parameters.Select(CreateSignatureParameterLabel))})";

    private static string CreateDeclarationLabel(string keyword, string name)
        => $"{keyword} {name}";

    private static string CreateValueDeclarationLabel(
        VbaDeclarationKind kind,
        string name,
        VbaTypeReferenceSyntax? typeReference,
        bool isWithEvents = false,
        bool isStatic = false,
        bool isArray = false)
    {
        var parts = new List<string>();
        if (isStatic)
        {
            parts.Add("Static");
        }

        if (isWithEvents)
        {
            parts.Add("WithEvents");
        }

        if (kind == VbaDeclarationKind.Constant)
        {
            parts.Add("Const");
        }

        parts.Add(isArray ? $"{name}()" : name);
        var label = string.Join(" ", parts);
        return typeReference is null ? label : $"{label} As {typeReference.Name}";
    }

    private static string CreateParameterDeclarationLabel(VbaCallableParameterSyntax parameter)
    {
        var parts = new List<string>();
        if (parameter.IsParamArray)
        {
            parts.Add("ParamArray");
        }
        else if (parameter.IsByRef)
        {
            parts.Add("ByRef");
        }

        parts.Add(parameter.IsArray ? $"{parameter.Name}()" : parameter.Name);
        if (parameter.TypeReference is not null)
        {
            parts.Add($"As {parameter.TypeReference.Name}");
        }

        return string.Join(" ", parts);
    }

    private static string GetCallableKind(
        VbaDeclarationKind kind,
        VbaTypeReferenceSyntax? typeReference)
        => kind == VbaDeclarationKind.Property
            ? "Property"
            : typeReference is null ? "Sub" : "Function";

    private static string GetDeclarationKeyword(Match match)
        => match.Groups["propertyKind"].Success
            ? "Property"
            : match.Groups["kind"].Value;

    private static VbaPropertyAccessorKind? GetPropertyAccessorKind(Match match)
        => match.Groups["propertyKind"].Value.ToUpperInvariant() switch
        {
            "GET" => VbaPropertyAccessorKind.Get,
            "LET" => VbaPropertyAccessorKind.Let,
            "SET" => VbaPropertyAccessorKind.Set,
            _ => null
        };

    private static bool IsOptionalParameter(string text)
        => HasParameterModifier(text, "Optional");

    private static bool IsByRefParameter(string text)
        => !HasParameterModifier(text, "ByVal");

    private static bool IsParamArrayParameter(string text)
        => HasParameterModifier(text, "ParamArray");

    private static bool HasParameterModifier(string text, string modifier)
    {
        foreach (var token in VbaTokenStream.FromText(text).Tokens)
        {
            if (token.Kind == VbaTokenKind.Whitespace)
            {
                continue;
            }

            if (token.Kind == VbaTokenKind.Keyword
                && (token.Text.Equals("ByVal", StringComparison.OrdinalIgnoreCase)
                    || token.Text.Equals("ByRef", StringComparison.OrdinalIgnoreCase)
                    || token.Text.Equals("Optional", StringComparison.OrdinalIgnoreCase)
                    || token.Text.Equals("ParamArray", StringComparison.OrdinalIgnoreCase)))
            {
                if (token.Text.Equals(modifier, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            break;
        }

        return false;
    }

    private static bool IsArrayParameter(string text, string name)
    {
        var tokens = VbaTokenStream.FromText(text).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.Comment)
            .ToArray();
        for (var index = 0; index + 1 < tokens.Length; index++)
        {
            if (VbaIdentifierSyntaxFacts.IsValidDeclaredName(tokens[index])
                && tokens[index].Text.Equals(name, StringComparison.Ordinal)
                && tokens[index + 1].Kind == VbaTokenKind.Punctuation
                && tokens[index + 1].Text == "(")
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<DeclarationSegment> SplitDeclarationSegments(string text)
    {
        var segments = new List<DeclarationSegment>();
        var start = 0;
        var inString = false;
        var parenthesesDepth = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (current == '"' && inString && index + 1 < text.Length && text[index + 1] == '"')
            {
                index++;
                continue;
            }

            if (current == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (current == '(')
            {
                parenthesesDepth++;
                continue;
            }

            if (current == ')' && parenthesesDepth > 0)
            {
                parenthesesDepth--;
                continue;
            }

            if (current != ',' || parenthesesDepth != 0)
            {
                continue;
            }

            segments.Add(new DeclarationSegment(start, text[start..index]));
            start = index + 1;
        }

        segments.Add(new DeclarationSegment(start, text[start..]));
        return segments;
    }

    internal static int FindDocumentationCommentStartLine(IReadOnlyList<VbaSourceLine> lines, int declarationLine)
    {
        var startLine = declarationLine;
        for (var lineIndex = declarationLine - 1; lineIndex >= 0; lineIndex--)
        {
            var trimmed = VbaIdentifier.TrimStartWhitespace(lines[lineIndex].Text);
            if (!trimmed.StartsWith("'*", StringComparison.Ordinal))
            {
                break;
            }

            startLine = lineIndex;
        }

        return startLine;
    }

    private static DocumentationComment? ParseDocumentationComment(IReadOnlyList<VbaSourceLine> lines, int declarationLine)
    {
        var rawLines = new Stack<string>();
        var documentationStartLine = FindDocumentationCommentStartLine(lines, declarationLine);
        for (var lineIndex = declarationLine - 1; lineIndex >= documentationStartLine; lineIndex--)
        {
            var trimmed = VbaIdentifier.TrimStartWhitespace(lines[lineIndex].Text);
            rawLines.Push(trimmed[2..].TrimStart());
        }

        if (rawLines.Count == 0)
        {
            return null;
        }

        var summaryLines = new List<string>();
        var detailsLines = new List<string>();
        var currentBodyLines = summaryLines;
        var parameterDocs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parameterDirectionQualifiers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? returnDocumentation = null;
        foreach (var rawLine in rawLines)
        {
            if (rawLine.StartsWith("@brief ", StringComparison.OrdinalIgnoreCase))
            {
                summaryLines.Add(rawLine["@brief ".Length..].Trim());
                continue;
            }

            if (TryParseDocumentationCommand(rawLine, "@details", out var details))
            {
                currentBodyLines = detailsLines;
                if (details.Length != 0)
                {
                    detailsLines.Add(details);
                }

                continue;
            }

            if (TryParseParameterDocumentationCommand(
                rawLine,
                out var parameterName,
                out var parameterDocumentation,
                out var directionQualifier))
            {
                if (parameterName is not null && parameterDocumentation is not null)
                {
                    parameterDocs[parameterName] = parameterDocumentation;
                    parameterDirectionQualifiers[parameterName] = directionQualifier;
                }

                continue;
            }

            if (TryParseDocumentationCommand(rawLine, "@return", out var returnText)
                || TryParseDocumentationCommand(rawLine, "@returns", out returnText))
            {
                returnDocumentation = returnText;
                continue;
            }

            currentBodyLines.Add(rawLine.Trim());
        }

        var bodyLines = new List<string>();
        AddDocumentationSection(bodyLines, summaryLines);
        AddDocumentationSection(bodyLines, detailsLines);
        var hoverLines = new List<string>(bodyLines);
        foreach (var parameter in parameterDocs)
        {
            if (hoverLines.Count > 0 && hoverLines[^1].Length != 0)
            {
                hoverLines.Add("");
            }

            parameterDirectionQualifiers.TryGetValue(parameter.Key, out var directionQualifier);
            hoverLines.Add($"@param{directionQualifier} {parameter.Key} {parameter.Value}");
        }

        if (!string.IsNullOrWhiteSpace(returnDocumentation))
        {
            if (hoverLines.Count > 0 && hoverLines[^1].Length != 0)
            {
                hoverLines.Add("");
            }

            hoverLines.Add($"@return {returnDocumentation}");
        }

        return new DocumentationComment(
            string.Join('\n', hoverLines).TrimEnd(),
            bodyLines.Count == 0 ? null : string.Join('\n', bodyLines).TrimEnd(),
            parameterDocs,
            returnDocumentation);
    }

    private static bool TryParseParameterDocumentationCommand(
        string rawLine,
        out string? parameterName,
        out string? documentation,
        out string? directionQualifier)
    {
        const string command = "@param";
        parameterName = null;
        documentation = null;
        directionQualifier = null;
        if (!rawLine.StartsWith(command, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var content = rawLine[command.Length..];
        if (content.StartsWith("[", StringComparison.Ordinal))
        {
            var closingBracket = content.IndexOf(']');
            if (closingBracket < 0)
            {
                return false;
            }

            var direction = content[1..closingBracket];
            if (!direction.Equals("in", StringComparison.OrdinalIgnoreCase)
                && !direction.Equals("out", StringComparison.OrdinalIgnoreCase)
                && !direction.Equals("in,out", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            directionQualifier = $"[{direction.ToLowerInvariant()}]";
            content = content[(closingBracket + 1)..];
        }

        if (content.Length == 0 || !char.IsWhiteSpace(content[0]))
        {
            return false;
        }

        var parts = content.Trim().Split(
            [' ', '\t'],
            2,
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
        {
            parameterName = parts[0];
            documentation = parts[1].Trim();
        }

        return true;
    }

    private static bool TryParseDocumentationCommand(
        string rawLine,
        string command,
        out string content)
    {
        content = "";
        if (!rawLine.StartsWith(command, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (rawLine.Length == command.Length)
        {
            return true;
        }

        if (!char.IsWhiteSpace(rawLine[command.Length]))
        {
            return false;
        }

        content = rawLine[command.Length..].Trim();
        return true;
    }

    private static void AddDocumentationSection(
        ICollection<string> bodyLines,
        IReadOnlyList<string> sectionLines)
    {
        var firstContentLine = 0;
        while (firstContentLine < sectionLines.Count && sectionLines[firstContentLine].Length == 0)
        {
            firstContentLine++;
        }

        var lastContentLine = sectionLines.Count - 1;
        while (lastContentLine >= firstContentLine && sectionLines[lastContentLine].Length == 0)
        {
            lastContentLine--;
        }

        if (lastContentLine < firstContentLine)
        {
            return;
        }

        if (bodyLines.Count > 0)
        {
            bodyLines.Add("");
        }

        for (var index = firstContentLine; index <= lastContentLine; index++)
        {
            bodyLines.Add(sectionLines[index]);
        }
    }

    private static string? ParseParameterName(string parameter)
    {
        foreach (var token in VbaTokenStream.FromText(parameter).Tokens)
        {
            if (token.Kind == VbaTokenKind.Whitespace)
            {
                continue;
            }

            if (token.Kind == VbaTokenKind.Keyword
                && (token.Text.Equals("ByVal", StringComparison.OrdinalIgnoreCase)
                    || token.Text.Equals("ByRef", StringComparison.OrdinalIgnoreCase)
                    || token.Text.Equals("Optional", StringComparison.OrdinalIgnoreCase)
                    || token.Text.Equals("ParamArray", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return VbaIdentifierSyntaxFacts.IsValidDeclaredName(token) ? token.Text : null;
        }

        return null;
    }

    private static VbaTypeReferenceSyntax? ParseReturnTypeReference(Match match, string line)
    {
        var parametersGroup = match.Groups["parameters"];
        if (parametersGroup.Success)
        {
            return ParseReturnTypeReference(line[(parametersGroup.Index + parametersGroup.Length)..]);
        }

        return ParseReturnTypeReference(line);
    }

    private static VbaTypeReferenceSyntax? ParseReturnTypeReference(string text)
        => ParseTypeReferenceAfterAs(text);

    private static VbaTypeReferenceSyntax? ParseTypeReference(
        string text,
        bool allowAnyTypeReference = false)
        => ParseTypeReferenceAfterAs(text, allowAnyTypeReference);

    private static VbaTypeReferenceSyntax? ParseTypeReferenceAfterAs(
        string text,
        bool allowAnyTypeReference = false)
    {
        var tokens = VbaTokenStream.FromText(text).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.LineContinuation
                and not VbaTokenKind.Comment)
            .ToArray();
        var asIndex = Array.FindIndex(tokens, token =>
            token.Text.Equals("As", StringComparison.OrdinalIgnoreCase));
        if (asIndex < 0 || asIndex + 1 >= tokens.Length)
        {
            return null;
        }

        var index = asIndex + 1;
        var isNew = tokens[index].Text.Equals("New", StringComparison.OrdinalIgnoreCase);
        if (isNew && ++index >= tokens.Length)
        {
            return null;
        }

        if (!IsTypeReferenceName(tokens[index], allowAnyTypeReference))
        {
            return null;
        }

        var name = tokens[index].Text;
        string? qualifier = null;
        if (index + 2 < tokens.Length
            && tokens[index + 1].Text == "."
            && VbaIdentifier.IsIdentifier(name)
            && IsTypeReferenceName(tokens[index + 2], allowAnyTypeReference: false))
        {
            qualifier = name;
            name = tokens[index + 2].Text;
        }

        return new VbaTypeReferenceSyntax(
            name,
            qualifier,
            isNew);
    }

    private static bool IsTypeReferenceName(
        VbaToken token,
        bool allowAnyTypeReference)
        => token.Kind is VbaTokenKind.Identifier or VbaTokenKind.Keyword
            && (VbaIdentifier.IsIdentifier(token.Text)
                || VbaLanguageVocabulary.TypeNames.Contains(
                    token.Text,
                    StringComparer.OrdinalIgnoreCase)
                || (allowAnyTypeReference
                    && token.Text.Equals("Any", StringComparison.OrdinalIgnoreCase)));

    private static int FindBlockEndLine(
        VbaSourceText sourceText,
        int headerLine,
        int startLine,
        string keyword,
        IReadOnlyList<VbaPreprocessorBlockSyntax> preprocessorBlocks)
    {
        var lines = sourceText.Lines;
        var pattern = new Regex(
            "^" + VbaIdentifier.RegexWhitespace + "*"
            + "End" + VbaIdentifier.RegexWhitespace + "+"
            + Regex.Escape(keyword)
            + "(?=$|" + VbaIdentifier.RegexWhitespace + "|:)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!VbaConditionalCompilationBranchFacts.TryGetStructuralPath(
                preprocessorBlocks,
                CreateLineRange(lines[headerLine]),
                out var headerPath)
            || !VbaConditionalCompilationBranchFacts.TryGetStructuralClosingDirective(
                preprocessorBlocks,
                headerPath,
                out var closingDirective))
        {
            return lines.Count - 1;
        }

        var searchEndLine = closingDirective is null
            ? lines.Count - 1
            : Math.Max(headerLine, closingDirective.Range.Start.Line - 1);
        for (var lineIndex = startLine; lineIndex <= searchEndLine; lineIndex++)
        {
            if (pattern.IsMatch(VbaSourceText.StripApostropheComment(lines[lineIndex].Text))
                && VbaConditionalCompilationBranchFacts.TryGetStructuralPath(
                    preprocessorBlocks,
                    CreateLineRange(lines[lineIndex]),
                    out var closerPath)
                && closerPath.Equals(headerPath))
            {
                return lineIndex;
            }
        }

        return searchEndLine;
    }

    private static VbaDeclarationVisibility GetVisibility(string visibility, bool defaultPublic)
    {
        if (visibility.Equals("Private", StringComparison.OrdinalIgnoreCase)
            || visibility.Equals("Dim", StringComparison.OrdinalIgnoreCase))
        {
            return VbaDeclarationVisibility.Private;
        }

        if (visibility.Equals("Global", StringComparison.OrdinalIgnoreCase))
        {
            return VbaDeclarationVisibility.Public;
        }

        if (visibility.Equals("Friend", StringComparison.OrdinalIgnoreCase))
        {
            return VbaDeclarationVisibility.Friend;
        }

        if (visibility.Equals("Public", StringComparison.OrdinalIgnoreCase))
        {
            return VbaDeclarationVisibility.Public;
        }

        return defaultPublic
            ? VbaDeclarationVisibility.Public
            : VbaDeclarationVisibility.Private;
    }

    private static bool IsModuleVariableDeclaration(string codeLine)
    {
        var tokens = VbaTokenStream.FromText(codeLine).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.LineContinuation
                and not VbaTokenKind.Comment)
            .ToArray();
        if (tokens.Length < 2)
        {
            return false;
        }

        var index = 1;
        if (tokens[index].Text.Equals("Static", StringComparison.OrdinalIgnoreCase))
        {
            index++;
        }

        if (index >= tokens.Length)
        {
            return true;
        }

        return !tokens[index].Text.Equals("Sub", StringComparison.OrdinalIgnoreCase)
            && !tokens[index].Text.Equals("Function", StringComparison.OrdinalIgnoreCase)
            && !tokens[index].Text.Equals("Property", StringComparison.OrdinalIgnoreCase)
            && !tokens[index].Text.Equals("Declare", StringComparison.OrdinalIgnoreCase)
            && !tokens[index].Text.Equals("Const", StringComparison.OrdinalIgnoreCase)
            && !tokens[index].Text.Equals("Event", StringComparison.OrdinalIgnoreCase)
            && !tokens[index].Text.Equals("Enum", StringComparison.OrdinalIgnoreCase)
            && !tokens[index].Text.Equals("Type", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWithEventsVariableDeclaration(string codeLine)
        => VbaTokenStream.FromText(codeLine).Tokens.Any(token =>
            token.Text.Equals("WithEvents", StringComparison.OrdinalIgnoreCase));

    private static int SkipWhitespace(string text, int startIndex)
    {
        var index = startIndex;
        while (index < text.Length && VbaIdentifier.IsWhitespace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static int ReadIdentifierEnd(string text, int startIndex)
    {
        if (startIndex >= text.Length)
        {
            return startIndex;
        }

        var candidateLength = VbaIdentifier.ReadCandidateLength(
            text.AsSpan(startIndex),
            out _);
        return candidateLength > 0
            && VbaIdentifier.IsIdentifier(text.Substring(startIndex, candidateLength))
                ? startIndex + candidateLength
                : startIndex;
    }

    private static Match MatchIdentifier(Regex pattern, string text, string groupName = "name")
    {
        var match = pattern.Match(text);
        return match.Success
            && VbaIdentifierSyntaxFacts.IsValidDeclaredName(match.Groups[groupName].Value)
            && HasDeclaredNameBoundary(text, match.Groups[groupName])
            ? match
            : Match.Empty;
    }

    private static bool HasDeclaredNameBoundary(string text, Group group)
    {
        var boundary = group.Index + group.Length;
        if (boundary >= text.Length)
        {
            return true;
        }

        if (IsDeclaredNameTailBoundary(text[boundary]))
        {
            return true;
        }

        if (text[boundary] is not ('$' or '%' or '&' or '^' or '!' or '#' or '@'))
        {
            return false;
        }

        boundary++;
        return boundary >= text.Length || IsDeclaredNameTailBoundary(text[boundary]);
    }

    private static bool IsDeclaredNameTailBoundary(char value)
        => VbaIdentifier.IsWhitespace(value) || value is '(' or ':';

    private static Match MatchLexIdentifier(Regex pattern, string text, string groupName = "name")
    {
        var match = pattern.Match(text);
        return match.Success
            && VbaIdentifier.IsLexIdentifier(match.Groups[groupName].Value)
            ? match
            : Match.Empty;
    }

    private static bool StartsWithKeyword(string text, int startIndex, string keyword)
    {
        if (!text.AsSpan(startIndex).StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var beforeIsBoundary = startIndex == 0 || !VbaSourceText.IsIdentifierCharacter(text[startIndex - 1]);
        var afterIndex = startIndex + keyword.Length;
        var afterIsBoundary = afterIndex >= text.Length || !VbaSourceText.IsIdentifierCharacter(text[afterIndex]);
        return beforeIsBoundary && afterIsBoundary;
    }

    private static VbaSyntaxRange CreateRange(VbaSourceText sourceText, Match match, string groupName, VbaSourceLine line)
    {
        var group = match.Groups[groupName];
        return sourceText.RangeForLine(line, group.Index, group.Index + group.Length);
    }

    private static VbaSyntaxRange CreateLineRange(VbaSourceLine line)
        => new(
            new VbaSyntaxPosition(line.LineNumber, 0, line.StartOffset),
            new VbaSyntaxPosition(line.LineNumber, line.Text.Length, line.EndOffset));

    private static VbaSyntaxRange CreateBlockRange(IReadOnlyList<VbaSourceLine> lines, int startLine, int endLine)
        => new(
            new VbaSyntaxPosition(startLine, 0, lines[startLine].StartOffset),
            new VbaSyntaxPosition(endLine, lines[endLine].Text.Length, lines[endLine].EndOffset));

    private static bool IsRemCommentLine(string line)
    {
        var trimmed = VbaIdentifier.TrimStartWhitespace(line);
        return trimmed.StartsWith("Rem", StringComparison.OrdinalIgnoreCase)
            && (trimmed.Length == "Rem".Length
                || VbaIdentifier.IsWhitespace(trimmed["Rem".Length]));
    }

    private static VbaModuleIdentitySyntax CreateIdentity(
        string uri,
        VbaSourceText sourceText,
        VbaModuleKind kind,
        IReadOnlyList<VbaModuleAttributeSyntax> attributes)
    {
        var nameAttribute = attributes.FirstOrDefault(attribute =>
            attribute.Name.Equals("VB_Name", StringComparison.OrdinalIgnoreCase));
        if (nameAttribute is not null && IsValidModuleIdentity(nameAttribute.Value))
        {
            return new VbaModuleIdentitySyntax(nameAttribute.Value, nameAttribute.ValueRange);
        }

        var fallbackName = GetFileBaseName(uri);
        return new VbaModuleIdentitySyntax(
            fallbackName,
            new VbaSyntaxRange(sourceText.StartPosition, sourceText.StartPosition));
    }

    private static bool IsValidModuleIdentity(string value)
        => VbaIdentifier.IsIdentifier(value)
            && value.EnumerateRunes().Take(32).Count() <= 31;

    private static VbaSourceLine? FindAttributeNameLine(VbaSourceText sourceText)
        => sourceText.Lines.FirstOrDefault(line =>
            MatchLexIdentifier(AttributePattern, line.Text) is { Success: true } match
            && match.Groups["name"].Value.Equals("VB_Name", StringComparison.OrdinalIgnoreCase));

    private static string UnquoteAttributeValue(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        }

        return value;
    }

    private static VbaModuleKind GetModuleKind(string uri)
    {
        if (uri.EndsWith(".cls", StringComparison.OrdinalIgnoreCase))
        {
            return VbaModuleKind.ClassModule;
        }

        if (uri.EndsWith(".frm", StringComparison.OrdinalIgnoreCase))
        {
            return VbaModuleKind.FormModule;
        }

        return VbaModuleKind.StandardModule;
    }

    private static string GetFileBaseName(string uri)
    {
        try
        {
            return Path.GetFileNameWithoutExtension(new Uri(uri).LocalPath);
        }
        catch (UriFormatException)
        {
            var separator = Math.Max(uri.LastIndexOf('/'), uri.LastIndexOf('\\'));
            var fileName = separator < 0 ? uri : uri[(separator + 1)..];
            var extension = fileName.LastIndexOf('.');
            return extension <= 0 ? fileName : fileName[..extension];
        }
    }

}

/// <summary>
/// Contains module members and declarations parsed from a module body.
/// </summary>
/// <param name="Members">The top-level module member blocks.</param>
/// <param name="Declarations">The parsed definitions.</param>
/// <param name="CallableDeclarations">The parsed callable definitions.</param>
internal sealed record ParsedMembers(
    IReadOnlyList<VbaModuleMemberSyntax> Members,
    IReadOnlyList<VbaDeclarationSyntax> Declarations,
    IReadOnlyList<VbaCallableDeclarationSyntax> CallableDeclarations);

/// <summary>
/// Contains parsed statement syntax and statement-level diagnostics.
/// </summary>
/// <param name="Statements">The parsed statement and block nodes.</param>
/// <param name="Diagnostics">The diagnostics produced while parsing statements.</param>
internal sealed record ParsedStatements(
    IReadOnlyList<VbaStatementSyntax> Statements,
    IReadOnlyList<VbaSyntaxDiagnostic> Diagnostics);

/// <summary>
/// Contains parsed expressions and argument lists.
/// </summary>
/// <param name="Expressions">The parsed expression fragments.</param>
/// <param name="ArgumentLists">The parsed call argument lists.</param>
internal sealed record ParsedExpressions(
    IReadOnlyList<VbaExpressionSyntax> Expressions,
    IReadOnlyList<VbaArgumentListSyntax> ArgumentLists);


/// <summary>
/// Represents a logical VBA statement assembled from one or more physical lines.
/// </summary>
/// <param name="Text">The logical statement text.</param>
/// <param name="SourcePositions">The source position for each character in the logical text, when available.</param>
/// <param name="Range">The source range covered by the logical statement.</param>
/// <param name="IsContinued">Whether the statement spans physical lines using continuation markers.</param>
internal sealed record LogicalStatement(
    string Text,
    IReadOnlyList<VbaSyntaxPosition?> SourcePositions,
    VbaSyntaxRange Range,
    bool IsContinued);

/// <summary>
/// Tracks an open statement block while parsing nested block structure.
/// </summary>
/// <param name="Kind">The block statement kind.</param>
/// <param name="ExpectedTerminator">The terminator text expected for this block.</param>
/// <param name="Range">The source range of the block opener.</param>
internal sealed record BlockFrame(
    VbaStatementKind Kind,
    string ExpectedTerminator,
    VbaSyntaxRange Range);

/// <summary>
/// Represents one declaration segment split from a multi-declaration line.
/// </summary>
/// <param name="Start">The segment start character in the source line.</param>
/// <param name="Text">The segment text.</param>
internal sealed record DeclarationSegment(int Start, string Text);

/// <summary>
/// Represents parsed Doxygen-style documentation comment content attached to a declaration.
/// </summary>
/// <param name="HoverText">The rendered documentation text for hover display.</param>
/// <param name="Summary">The summary text, when present.</param>
/// <param name="ParameterDocs">The parameter documentation keyed by parameter name.</param>
/// <param name="ReturnDocumentation">The return value documentation, when present.</param>
internal sealed record DocumentationComment(
    string HoverText,
    string? Summary,
    IReadOnlyDictionary<string, string> ParameterDocs,
    string? ReturnDocumentation);
