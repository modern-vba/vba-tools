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
        + "(?:(?<kind>Sub|Function)|(?<propertyDeclaration>Property"
        + VbaIdentifier.RegexWhitespace + "+(?<propertyKind>Get|Let|Set)))"
        + VbaIdentifier.RegexWhitespace + "+"
        + "(?<name>(?>" + VbaIdentifier.RegexIdentifierCandidate + "))"
        + "(?<typeCharacter>[$%&^!#@])?"
        + VbaIdentifier.RegexWhitespace + "*(?:\\((?<parameters>.*)\\))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DeclarePattern = new(
        "^" + VbaIdentifier.RegexWhitespace + "*"
        + "(?:(?<visibility>Public|Private)" + VbaIdentifier.RegexWhitespace + "+)?"
        + "Declare" + VbaIdentifier.RegexWhitespace + "+"
        + "(?:PtrSafe" + VbaIdentifier.RegexWhitespace + "+)?"
        + "(?<kind>Sub|Function)" + VbaIdentifier.RegexWhitespace + "+"
        + "(?<name>(?>" + VbaIdentifier.RegexIdentifierCandidate + "))"
        + "(?<typeCharacter>[$%&^!#@])?"
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

    private static readonly Regex EventDeclarationPrefixPattern = new(
        "^" + VbaIdentifier.RegexWhitespace + "*"
        + "(?:(?:Public|Private|Friend)" + VbaIdentifier.RegexWhitespace + "+)?"
        + "Event(?=" + VbaIdentifier.RegexWhitespace + "|\\(|$)",
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
        + VbaIdentifier.RegexWhitespace + "+"
        + "(?:(?<static>Static)" + VbaIdentifier.RegexWhitespace + "+)?"
        + "(?<declarations>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex RecoveredModuleWithEventsVariablePattern = new(
        "^" + VbaIdentifier.RegexWhitespace + "*"
        + "(?:(?<static>Static)" + VbaIdentifier.RegexWhitespace + "+)?"
        + "(?<declarations>(?=WithEvents" + VbaIdentifier.RegexWhitespace
        + "+|.+," + VbaIdentifier.RegexWhitespace + "*WithEvents"
        + VbaIdentifier.RegexWhitespace + "+).+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LocalVariablePattern = new(
        "^" + VbaIdentifier.RegexWhitespace + "*"
        + "(?:Dim|(?<static>Static))"
        + VbaIdentifier.RegexWhitespace + "+(?<declarations>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex RecoveredLocalVisibilityWithEventsVariablePattern = new(
        "^" + VbaIdentifier.RegexWhitespace + "*"
        + "(?<introducer>Public|Private|Friend|Global)"
        + VbaIdentifier.RegexWhitespace + "+"
        + "(?:(?<static>Static)" + VbaIdentifier.RegexWhitespace + "+)?"
        + "(?<declarations>(?=WithEvents" + VbaIdentifier.RegexWhitespace
        + "+|.+," + VbaIdentifier.RegexWhitespace + "*WithEvents"
        + VbaIdentifier.RegexWhitespace + "+).+)$",
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
        var moduleIdentityMetadata = VbaModuleIdentityMetadataReader.Read(
            sourceText.Text,
            kind == VbaModuleKind.StandardModule
                ? VbaModuleIdentitySourceKind.StandardModule
                : VbaModuleIdentitySourceKind.ObjectModule);
        var identity = CreateIdentity(uri, sourceText, moduleIdentityMetadata);
        diagnostics.AddRange(CreateModuleIdentityMetadataDiagnostics(
            sourceText,
            moduleIdentityMetadata));
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
        diagnostics.AddRange(CollectEventDeclarationDiagnostics(
            physicalAnalysisSourceText,
            tokenStream,
            kind,
            parsedMembers.CallableDeclarations,
            parsedMembers.Members,
            codeStartLine));
        diagnostics.AddRange(CollectWithEventsDeclarationDiagnostics(
            kind,
            parsedMembers.Declarations));
        diagnostics.AddRange(CollectRaiseEventPlacementDiagnostics(
            physicalAnalysisSourceText,
            tokenStream,
            kind,
            parsedMembers.CallableDeclarations,
            codeStartLine));
        diagnostics.AddRange(parsedStatements.Diagnostics);
        foreach (var diagnostic in CollectRaiseEventArgumentListDiagnostics(
            tokenStream,
            parsedExpressions.ArgumentLists))
        {
            if (!diagnostics.Any(existing => existing.Code == diagnostic.Code
                && existing.Range == diagnostic.Range))
            {
                diagnostics.Add(diagnostic);
            }
        }

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
            sourceText.FullRange)
        {
            ImplementsRelationships = ParseImplementsRelationships(
                tokenStream,
                kind,
                codeStartLine,
                parsedMembers.Members,
                parsedMembers.CallableDeclarations),
            DefTypeDirectives = ParseDefTypeDirectives(
                tokenStream,
                codeStartLine,
                parsedMembers.Members,
                parsedMembers.CallableDeclarations),
            IncompleteEventDeclarationRanges =
                GetIncompleteEventDeclarationRanges(
                    physicalAnalysisSourceText,
                    codeStartLine)
        };
        return new VbaSyntaxTree(uri, sourceText, tokenStream, module, diagnostics);
    }

    private static IReadOnlyList<VbaImplementsRelationshipSyntax>
        ParseImplementsRelationships(
            VbaTokenStream tokenStream,
            VbaModuleKind moduleKind,
            int codeStartLine,
            IReadOnlyList<VbaModuleMemberSyntax> members,
            IReadOnlyList<VbaCallableDeclarationSyntax> callableDeclarations)
    {
        if (moduleKind != VbaModuleKind.ClassModule)
        {
            return [];
        }

        var relationships = new List<VbaImplementsRelationshipSyntax>();
        var statement = new List<VbaToken>();
        var lineContinues = false;
        foreach (var token in tokenStream.Tokens)
        {
            if (token.Range.Start.Line < codeStartLine)
            {
                continue;
            }

            if (token.Kind == VbaTokenKind.Comment)
            {
                continue;
            }

            if (token.Kind == VbaTokenKind.LineContinuation)
            {
                lineContinues = true;
                continue;
            }

            if (token.Kind == VbaTokenKind.NewLine)
            {
                if (lineContinues)
                {
                    lineContinues = false;
                    continue;
                }

                AddImplementsRelationship(
                    statement,
                    relationships,
                    members,
                    callableDeclarations);
                statement.Clear();
                continue;
            }

            if (token.Kind == VbaTokenKind.Whitespace)
            {
                continue;
            }

            if (token.Kind == VbaTokenKind.Punctuation && token.Text == ":")
            {
                AddImplementsRelationship(
                    statement,
                    relationships,
                    members,
                    callableDeclarations);
                statement.Clear();
                continue;
            }

            statement.Add(token);
        }

        AddImplementsRelationship(
            statement,
            relationships,
            members,
            callableDeclarations);
        return relationships;
    }

    private static void AddImplementsRelationship(
        IReadOnlyList<VbaToken> statement,
        ICollection<VbaImplementsRelationshipSyntax> relationships,
        IReadOnlyList<VbaModuleMemberSyntax> members,
        IReadOnlyList<VbaCallableDeclarationSyntax> callableDeclarations)
    {
        if (statement.Count is not (2 or 4)
            || IsInsideNestedDeclaration(
                statement[0].Range.Start,
                members,
                callableDeclarations)
            || !statement[0].Text.Equals(
                "Implements",
                StringComparison.OrdinalIgnoreCase)
            || !IsImplementsTypeName(statement[1]))
        {
            return;
        }

        VbaToken name;
        VbaToken? qualifier;
        if (statement.Count == 2)
        {
            qualifier = null;
            name = statement[1];
        }
        else
        {
            if (statement[2].Text != "." || !IsImplementsTypeName(statement[3]))
            {
                return;
            }

            qualifier = statement[1];
            name = statement[3];
        }

        var typeRange = new VbaSyntaxRange(statement[1].Range.Start, name.Range.End);
        relationships.Add(new VbaImplementsRelationshipSyntax(
            new VbaTypeReferenceSyntax(name.Text, qualifier?.Text),
            typeRange,
            name.Range,
            qualifier?.Range,
            new VbaSyntaxRange(statement[0].Range.Start, name.Range.End)));
    }

    private static bool IsImplementsTypeName(VbaToken token)
        => token.Kind is VbaTokenKind.Identifier or VbaTokenKind.Keyword
            && VbaIdentifier.IsIdentifier(token.Text);

    private static IReadOnlyList<VbaDefTypeDirectiveSyntax>
        ParseDefTypeDirectives(
            VbaTokenStream tokenStream,
            int codeStartLine,
            IReadOnlyList<VbaModuleMemberSyntax> members,
            IReadOnlyList<VbaCallableDeclarationSyntax> callableDeclarations)
    {
        var directives = new List<VbaDefTypeDirectiveSyntax>();
        var statement = new List<VbaToken>();
        var lineContinues = false;
        foreach (var token in tokenStream.Tokens)
        {
            if (token.Range.Start.Line < codeStartLine)
            {
                continue;
            }

            if (token.Kind == VbaTokenKind.Comment)
            {
                continue;
            }

            if (token.Kind == VbaTokenKind.LineContinuation)
            {
                lineContinues = true;
                continue;
            }

            if (token.Kind == VbaTokenKind.NewLine)
            {
                if (lineContinues)
                {
                    lineContinues = false;
                    continue;
                }

                AddDefTypeDirective(
                    statement,
                    directives,
                    members,
                    callableDeclarations);
                statement.Clear();
                continue;
            }

            if (token.Kind == VbaTokenKind.Whitespace)
            {
                continue;
            }

            if (token.Kind == VbaTokenKind.Punctuation && token.Text == ":")
            {
                AddDefTypeDirective(
                    statement,
                    directives,
                    members,
                    callableDeclarations);
                statement.Clear();
                continue;
            }

            statement.Add(token);
        }

        AddDefTypeDirective(
            statement,
            directives,
            members,
            callableDeclarations);
        return directives;
    }

    private static void AddDefTypeDirective(
        IReadOnlyList<VbaToken> statement,
        ICollection<VbaDefTypeDirectiveSyntax> directives,
        IReadOnlyList<VbaModuleMemberSyntax> members,
        IReadOnlyList<VbaCallableDeclarationSyntax> callableDeclarations)
    {
        if (statement.Count < 2
            || IsInsideNestedDeclaration(
                statement[0].Range.Start,
                members,
                callableDeclarations)
            || !TryGetDefTypeName(statement[0].Text, out var typeName))
        {
            return;
        }

        var ranges = new List<VbaDefTypeLetterRangeSyntax>();
        var index = 1;
        while (index < statement.Count)
        {
            if (!TryGetDefTypeLetter(statement[index], out var start))
            {
                return;
            }

            var end = start;
            index++;
            if (index < statement.Count && statement[index].Text == "-")
            {
                index++;
                if (index >= statement.Count
                    || !TryGetDefTypeLetter(statement[index], out end))
                {
                    return;
                }

                index++;
            }

            if (start > end)
            {
                return;
            }

            ranges.Add(new VbaDefTypeLetterRangeSyntax(start, end));
            if (index == statement.Count)
            {
                break;
            }

            if (statement[index].Text != ",")
            {
                return;
            }

            index++;
        }

        if (ranges.Count == 0)
        {
            return;
        }

        directives.Add(new VbaDefTypeDirectiveSyntax(
            typeName,
            ranges.ToArray(),
            new VbaSyntaxRange(statement[0].Range.Start, statement[^1].Range.End)));
    }

    private static bool IsInsideNestedDeclaration(
        VbaSyntaxPosition position,
        IReadOnlyList<VbaModuleMemberSyntax> members,
        IReadOnlyList<VbaCallableDeclarationSyntax> callableDeclarations)
        => members.Any(member => member.Kind is
                VbaDeclarationKind.Type or VbaDeclarationKind.Enum
            && member.BlockRange.Start.Offset < position.Offset
            && position.Offset < member.BlockRange.End.Offset)
            || callableDeclarations.Any(callable =>
        {
            if (callable.IsExternal)
            {
                return false;
            }

            var headerEnd = (callable.SignatureRange ?? callable.Range).End.Offset;
            return headerEnd <= position.Offset
                && position.Offset < callable.BlockRange.End.Offset;
        });

    private static bool TryGetDefTypeName(string keyword, out string typeName)
    {
        typeName = keyword.ToUpperInvariant() switch
        {
            "DEFBOOL" => "Boolean",
            "DEFBYTE" => "Byte",
            "DEFCUR" => "Currency",
            "DEFDATE" => "Date",
            "DEFDBL" => "Double",
            "DEFDEC" => "Decimal",
            "DEFINT" => "Integer",
            "DEFLNG" => "Long",
            "DEFLNGLNG" => "LongLong",
            "DEFLNGPTR" => "LongPtr",
            "DEFOBJ" => "Object",
            "DEFSNG" => "Single",
            "DEFSTR" => "String",
            "DEFVAR" => "Variant",
            _ => ""
        };
        return typeName.Length > 0;
    }

    private static bool TryGetDefTypeLetter(VbaToken token, out char letter)
    {
        letter = '\0';
        if (token.Text.Length != 1
            || !char.IsAsciiLetter(token.Text[0]))
        {
            return false;
        }

        letter = char.ToUpperInvariant(token.Text[0]);
        return true;
    }

    private static IEnumerable<VbaSyntaxDiagnostic> CollectWithEventsDeclarationDiagnostics(
        VbaModuleKind moduleKind,
        IReadOnlyList<VbaDeclarationSyntax> declarations)
    {
        foreach (var declaration in declarations.Where(declaration =>
                     declaration.WithEventsKeywordRange is not null))
        {
            var placementAllowed = moduleKind is VbaModuleKind.ClassModule or VbaModuleKind.FormModule
                && declaration.ParentProcedureName is null
                && !declaration.IsStatic
                && declaration.VariableDeclarationIntroducer is
                    VbaVariableDeclarationIntroducer.Public
                    or VbaVariableDeclarationIntroducer.Private
                    or VbaVariableDeclarationIntroducer.Dim;
            if (!placementAllowed)
            {
                yield return new VbaSyntaxDiagnostic(
                    "syntax.withEventsDeclarationNotAllowedHere",
                    "WithEvents variables are allowed only at module level in a class module.",
                    declaration.WithEventsKeywordRange!);
            }

            if (declaration.WithEventsArrayDesignatorRange is { } arrayRange)
            {
                yield return new VbaSyntaxDiagnostic(
                    "syntax.withEventsArrayNotAllowed",
                    "WithEvents variables cannot be arrays.",
                    arrayRange);
            }

            if (declaration.WithEventsNewKeywordRange is { } newRange)
            {
                yield return new VbaSyntaxDiagnostic(
                    "syntax.withEventsNewNotAllowed",
                    "New cannot be used with WithEvents.",
                    newRange);
            }

            if (declaration.WithEventsTypeDeclarationCharacterRange is { } typeCharacterRange)
            {
                yield return new VbaSyntaxDiagnostic(
                    "syntax.withEventsTypeDeclarationCharacterNotAllowed",
                    "Type-declaration characters cannot be used with WithEvents.",
                    typeCharacterRange);
            }

            if (declaration.WithEventsTypeRequiredRange is { } typeRequiredRange)
            {
                yield return new VbaSyntaxDiagnostic(
                    "syntax.withEventsTypeRequired",
                    "WithEvents variables require an explicit class type in an As clause.",
                    typeRequiredRange);
            }
        }
    }

    private static IEnumerable<VbaSyntaxDiagnostic> CollectEventDeclarationDiagnostics(
        VbaSourceText sourceText,
        VbaTokenStream tokenStream,
        VbaModuleKind moduleKind,
        IReadOnlyList<VbaCallableDeclarationSyntax> callableDeclarations,
        IReadOnlyList<VbaModuleMemberSyntax> members,
        int codeStartLine)
    {
        foreach (var statement in CreateLogicalStatements(sourceText, codeStartLine))
        {
            var eventMatch = MatchIdentifier(EventPattern, statement.Text);
            if (!eventMatch.Success)
            {
                continue;
            }

            var name = eventMatch.Groups["name"];
            var nameRange = RangeFromLogicalSpan(
                statement,
                name.Index,
                name.Index + name.Length);
            var isInsideProcedure = callableDeclarations.Any(callable =>
                callable.BlockRange.Start.Offset < statement.Range.Start.Offset
                && statement.Range.Start.Offset < callable.BlockRange.End.Offset);
            var isInsideTypeDeclaration = members.Any(member =>
                member.Kind is VbaDeclarationKind.Enum or VbaDeclarationKind.Type
                && member.BlockRange.Start.Offset < statement.Range.Start.Offset
                && statement.Range.Start.Offset < member.BlockRange.End.Offset);
            var hasInvalidPlacement = moduleKind is not (
                    VbaModuleKind.ClassModule or VbaModuleKind.FormModule)
                || isInsideProcedure
                || isInsideTypeDeclaration;
            if (hasInvalidPlacement)
            {
                var eventToken = tokenStream.Tokens.LastOrDefault(token =>
                    statement.Range.Start.Offset <= token.Range.Start.Offset
                    && token.Range.End.Offset <= nameRange.Start.Offset
                    && token.Text.Equals("Event", StringComparison.OrdinalIgnoreCase));
                if (eventToken is not null)
                {
                    yield return new VbaSyntaxDiagnostic(
                        "syntax.eventDeclarationNotAllowedInModule",
                        "Event declarations are allowed only at module level in a class module.",
                        eventToken.Range);
                }
            }

            var visibility = eventMatch.Groups["visibility"];
            if (visibility.Success
                && (visibility.Value.Equals("Private", StringComparison.OrdinalIgnoreCase)
                    || visibility.Value.Equals("Friend", StringComparison.OrdinalIgnoreCase)))
            {
                yield return new VbaSyntaxDiagnostic(
                    "syntax.eventVisibilityNotAllowed",
                    "Event declarations can only be Public.",
                    RangeFromLogicalSpan(
                        statement,
                        visibility.Index,
                        visibility.Index + visibility.Length));
            }

            if (name.Value.Contains('_'))
            {
                yield return new VbaSyntaxDiagnostic(
                    "syntax.eventNameCannotContainUnderscore",
                    "Event name cannot contain an underscore.",
                    nameRange);
            }

            var parameters = eventMatch.Groups["parameters"];
            if (!TryGetEventParameterSpan(
                    eventMatch,
                    statement.Text,
                    out var parameterStart,
                    out var parameterEnd))
            {
                continue;
            }

            var parametersRange = RangeFromLogicalSpan(
                statement,
                parameterStart,
                parameterEnd);
            foreach (var token in tokenStream.Tokens.Where(token =>
                         parametersRange.Start.Offset <= token.Range.Start.Offset
                         && token.Range.End.Offset <= parametersRange.End.Offset
                         && token.Kind == VbaTokenKind.Keyword
                         && (token.Text.Equals("Optional", StringComparison.OrdinalIgnoreCase)
                             || token.Text.Equals("ParamArray", StringComparison.OrdinalIgnoreCase))))
            {
                var isOptional = token.Text.Equals(
                    "Optional",
                    StringComparison.OrdinalIgnoreCase);
                yield return new VbaSyntaxDiagnostic(
                    isOptional
                        ? "syntax.eventOptionalParameterNotAllowed"
                        : "syntax.eventParamArrayParameterNotAllowed",
                    isOptional
                        ? "Event parameters cannot be Optional."
                        : "Event parameters cannot be ParamArray.",
                    token.Range);
            }
        }
    }

    private static IEnumerable<VbaSyntaxDiagnostic> CollectRaiseEventPlacementDiagnostics(
        VbaSourceText sourceText,
        VbaTokenStream tokenStream,
        VbaModuleKind moduleKind,
        IReadOnlyList<VbaCallableDeclarationSyntax> callableDeclarations,
        int codeStartLine)
    {
        foreach (var token in tokenStream.Tokens.Where(token =>
                     token.Range.Start.Line >= codeStartLine
                     && token.Kind == VbaTokenKind.Keyword
                     && token.Text.Equals("RaiseEvent", StringComparison.OrdinalIgnoreCase)))
        {
            if (VbaLexicalFacts.IsPositionInComment(
                    sourceText.Lines[token.Range.Start.Line].Text,
                    token.Range.Start.Character))
            {
                continue;
            }

            var isInsideProcedure = callableDeclarations.Any(callable =>
                IsInsideCallableBlock(sourceText, callable, token));
            if (moduleKind is VbaModuleKind.ClassModule or VbaModuleKind.FormModule
                && isInsideProcedure)
            {
                continue;
            }

            yield return new VbaSyntaxDiagnostic(
                "syntax.raiseEventStatementNotAllowedHere",
                "RaiseEvent statements are allowed only inside a procedure in a class module.",
                token.Range);
        }
    }

    private static bool IsInsideCallableBlock(
        VbaSourceText sourceText,
        VbaCallableDeclarationSyntax callable,
        VbaToken token)
    {
        var declarationKeyword = callable.DeclarationKeyword;
        if (callable.IsExternal
            || declarationKeyword is null
            || callable.BlockRange.Start.Offset >= token.Range.Start.Offset
            || token.Range.End.Offset > callable.BlockRange.End.Offset)
        {
            return false;
        }

        if (token.Range.Start.Line != callable.BlockRange.End.Line)
        {
            return true;
        }

        var line = sourceText.Lines[token.Range.Start.Line];
        var prefix = line.Text[..token.Range.Start.Character];
        return !ContainsBlockTerminatorStatement(
            prefix,
            declarationKeyword);
    }

    private static IEnumerable<VbaSyntaxDiagnostic> CollectRaiseEventArgumentListDiagnostics(
        VbaTokenStream tokenStream,
        IReadOnlyList<VbaArgumentListSyntax> argumentLists)
    {
        foreach (var argumentList in argumentLists)
        {
            if (!IsRaiseEventArgumentList(tokenStream, argumentList))
            {
                continue;
            }

            if (argumentList.Form != VbaCallSyntaxForm.Parenthesized)
            {
                if (argumentList.Arguments.Count > 0)
                {
                    yield return new VbaSyntaxDiagnostic(
                        "syntax.raiseEventArgumentListRequiresParentheses",
                        "RaiseEvent arguments must be enclosed in parentheses.",
                        new VbaSyntaxRange(
                            argumentList.Arguments[0].Range.Start,
                            argumentList.Range.End));
                }

                continue;
            }

            if (argumentList.Arguments.Count == 0)
            {
                yield return new VbaSyntaxDiagnostic(
                    "syntax.raiseEventEmptyArgumentListNotAllowed",
                    "RaiseEvent must omit parentheses when no arguments are supplied.",
                    argumentList.Range);
                continue;
            }

            if (argumentList.Arguments.Any(argument =>
                    argument.Kind == VbaArgumentKind.Omitted))
            {
                yield return new VbaSyntaxDiagnostic(
                    "syntax.raiseEventOmittedArgumentNotAllowed",
                    "RaiseEvent arguments cannot be omitted.",
                    argumentList.Range);
            }

            foreach (var argument in argumentList.Arguments.Where(argument =>
                         argument.Kind == VbaArgumentKind.Named
                         && argument.NameRange is not null))
            {
                var nameRange = argument.NameRange!;
                var namedOperator = tokenStream.Tokens.FirstOrDefault(token =>
                    token.Range.Start.Offset >= nameRange.End.Offset
                    && token.Range.End.Offset <= argument.Range.End.Offset
                    && token.Text == ":=");
                yield return new VbaSyntaxDiagnostic(
                    "syntax.raiseEventNamedArgumentNotAllowed",
                    "RaiseEvent arguments cannot use named-argument syntax.",
                    new VbaSyntaxRange(
                        nameRange.Start,
                        namedOperator?.Range.End ?? nameRange.End));
            }
        }
    }

    private static bool IsRaiseEventArgumentList(
        VbaTokenStream tokenStream,
        VbaArgumentListSyntax argumentList)
    {
        if (argumentList.CalleeRange is not { } calleeRange)
        {
            return false;
        }

        var precedingToken = FindPrecedingTokenInLogicalStatement(
            tokenStream.Tokens,
            calleeRange.Start.Offset);
        return precedingToken?.Text.Equals(
            "RaiseEvent",
            StringComparison.OrdinalIgnoreCase) == true;
    }

    private static VbaToken? FindPrecedingTokenInLogicalStatement(
        IReadOnlyList<VbaToken> tokens,
        int offset)
    {
        var lower = 0;
        var upper = tokens.Count;
        while (lower < upper)
        {
            var middle = lower + ((upper - lower) / 2);
            if (tokens[middle].Range.Start.Offset < offset)
            {
                lower = middle + 1;
            }
            else
            {
                upper = middle;
            }
        }

        for (var index = lower - 1; index >= 0; index--)
        {
            var token = tokens[index];
            if (token.Range.End.Offset > offset
                || token.Kind == VbaTokenKind.Whitespace)
            {
                continue;
            }

            if (token.Kind == VbaTokenKind.Comment)
            {
                return null;
            }

            if (token.Kind == VbaTokenKind.NewLine)
            {
                index--;
                while (index >= 0 && tokens[index].Kind == VbaTokenKind.Whitespace)
                {
                    index--;
                }

                if (index >= 0 && tokens[index].Kind == VbaTokenKind.LineContinuation)
                {
                    continue;
                }

                return null;
            }

            if (token.Kind == VbaTokenKind.LineContinuation)
            {
                continue;
            }

            if (token.Kind == VbaTokenKind.Punctuation && token.Text == ":")
            {
                return null;
            }

            return token;
        }

        return null;
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

    private static IEnumerable<VbaSyntaxDiagnostic>
        CreateModuleIdentityMetadataDiagnostics(
            VbaSourceText sourceText,
            VbaModuleIdentityMetadata metadata)
    {
        if (metadata.State != VbaModuleIdentityMetadataState.Invalid)
        {
            yield break;
        }

        var code = metadata.Condition
            == VbaModuleIdentityMetadataCondition.Duplicate
            ? "syntax.moduleIdentityMetadataDuplicate"
            : "syntax.moduleIdentityMetadataMalformed";
        var message = metadata.Condition
            == VbaModuleIdentityMetadataCondition.Duplicate
            ? "Module identity metadata is duplicated; re-export or repair the source before Rename."
            : "Module identity metadata is malformed; re-export or repair the source before Rename.";
        var ranges = metadata.Records
            .Where(record => metadata.Condition
                == VbaModuleIdentityMetadataCondition.Duplicate
                || record.IsMalformedOrMisplaced)
            .Select(record => record.RepairRange)
            .ToArray();
        if (ranges.Length == 0)
        {
            yield return new VbaSyntaxDiagnostic(
                code,
                message,
                sourceText.FullRange);
            yield break;
        }

        foreach (var range in ranges)
        {
            yield return new VbaSyntaxDiagnostic(code, message, range);
        }
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

    private static IReadOnlyList<VbaSyntaxRange> GetIncompleteEventDeclarationRanges(
        VbaSourceText sourceText,
        int codeStartLine)
        => CreateLogicalStatements(sourceText, codeStartLine)
            .Where(statement => EventDeclarationPrefixPattern.IsMatch(statement.Text)
                && !MatchIdentifier(EventPattern, statement.Text).Success)
            .Select(statement => statement.Range)
            .ToArray();

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
            var codeText = VbaLexicalFacts.SplitCodeAndComment(line.Text).CodePart;
            var hasContinuation = VbaSourceText.HasLineContinuation(codeText)
                && !CollectLineContinuationDiagnostics(line).Any(diagnostic =>
                    diagnostic.Code == "syntax.invalidTrailingCommentContinuation");
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

            if (!IsLogicalContinuationTail(sourceText, lineIndex)
                && TryCreateEventSourceDeclaration(
                    sourceText,
                    lineIndex,
                    parentProcedureName: null,
                    parentProcedureRange: null,
                    isInvalidEventPlacement: false,
                    out var eventDeclaration,
                    out var eventParameters,
                    out var eventStatement))
            {
                members.Add(new VbaModuleMemberSyntax(
                    eventDeclaration.Name,
                    VbaDeclarationKind.Event,
                    eventStatement.Range));
                declarations.Add(eventDeclaration);
                foreach (var parameter in eventParameters)
                {
                    declarations.Add(CreateParameterDeclaration(parameter, parameter.Range.Start.Line));
                }

                lineIndex = eventStatement.Range.End.Line;
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
                AddRecoveredEventDeclarations(
                    sourceText,
                    declarations,
                    lineIndex + 1,
                    endLine,
                    parentProcedureName: null,
                    parentProcedureRange: null);
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
                AddRecoveredEventDeclarations(
                    sourceText,
                    declarations,
                    lineIndex + 1,
                    endLine,
                    parentProcedureName: null,
                    parentProcedureRange: null);
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

            var procedureStatement = CreateLogicalStatement(sourceText, lineIndex);
            var procedureMatch = MatchProcedureDeclaration(
                procedureStatement.Text);
            if (procedureMatch.Success)
            {
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
                AddRecoveredEventDeclarations(
                    sourceText,
                    declarations,
                    declaration.LineIndex + 1,
                    declaration.BlockRange.End.Line,
                    declaration.Name,
                    declaration.BlockRange);
                lineIndex = declaration.BlockRange.End.Line;
                continue;
            }

            if (IsLogicalContinuationTail(sourceText, lineIndex))
            {
                continue;
            }

            var variableStatement = CreateLogicalStatement(sourceText, lineIndex);
            var variableMatch = ModuleVariablePattern.Match(variableStatement.Text);
            if (variableMatch.Success
                && IsModuleVariableDeclaration(variableStatement.Text))
            {
                var visibility = GetVisibility(variableMatch.Groups["visibility"].Value, defaultPublic: false);
                foreach (var declaration in ParseVariableLikeDeclarations(
                    sourceText,
                    variableMatch.Groups["declarations"],
                    line,
                    VbaDeclarationKind.Variable,
                    visibility,
                    variableDeclarationIntroducer: GetVariableDeclarationIntroducer(
                        variableMatch.Groups["visibility"].Value),
                    isStaticDefault: variableMatch.Groups["static"].Success,
                    logicalStatement: variableStatement))
                {
                    members.Add(new VbaModuleMemberSyntax(
                        declaration.Name,
                        declaration.Kind,
                        variableStatement.Range));
                    declarations.Add(declaration);
                }

                lineIndex = variableStatement.Range.End.Line;
                continue;
            }

            var recoveredWithEventsMatch =
                RecoveredModuleWithEventsVariablePattern.Match(variableStatement.Text);
            if (recoveredWithEventsMatch.Success)
            {
                foreach (var declaration in ParseVariableLikeDeclarations(
                    sourceText,
                    recoveredWithEventsMatch.Groups["declarations"],
                    line,
                    VbaDeclarationKind.Variable,
                    VbaDeclarationVisibility.Private,
                    variableDeclarationIntroducer:
                        recoveredWithEventsMatch.Groups["static"].Success
                            ? VbaVariableDeclarationIntroducer.Static
                            : null,
                    isStaticDefault:
                        recoveredWithEventsMatch.Groups["static"].Success,
                    logicalStatement: variableStatement))
                {
                    members.Add(new VbaModuleMemberSyntax(
                        declaration.Name,
                        declaration.Kind,
                        variableStatement.Range));
                    declarations.Add(declaration);
                }

                lineIndex = variableStatement.Range.End.Line;
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

            var codeLine = VbaLexicalFacts.SplitCodeAndComment(line.Text).CodePart;
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

            if (TryCloseLeadingBlocks(trimmed, blockStack, out var unexpectedClose))
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
        if (argumentStart >= codeLine.Length)
        {
            yield break;
        }

        if (codeLine[argumentStart] == ':')
        {
            yield break;
        }

        if (codeLine[argumentStart] == '_'
            && VbaSourceText.HasLineContinuation(codeLine))
        {
            yield break;
        }

        if (codeLine[argumentStart] == '(')
        {
            var argumentEnd = FindRaiseEventArgumentListEnd(codeLine, argumentStart);
            if (argumentEnd >= 0
                && string.IsNullOrWhiteSpace(
                    codeLine[(argumentStart + 1)..argumentEnd]))
            {
                yield return new VbaSyntaxDiagnostic(
                    "syntax.raiseEventEmptyArgumentListNotAllowed",
                    "RaiseEvent must omit parentheses when no arguments are supplied.",
                    new VbaSyntaxRange(
                        new VbaSyntaxPosition(
                            line.LineNumber,
                            argumentStart,
                            line.StartOffset + argumentStart),
                        new VbaSyntaxPosition(
                            line.LineNumber,
                            argumentEnd + 1,
                            line.StartOffset + argumentEnd + 1)));
                yield break;
            }

            if (argumentEnd >= 0
                && HasRaiseEventOmittedArgument(
                    codeLine,
                    argumentStart,
                    argumentEnd))
            {
                yield return new VbaSyntaxDiagnostic(
                    "syntax.raiseEventOmittedArgumentNotAllowed",
                    "RaiseEvent arguments cannot be omitted.",
                    new VbaSyntaxRange(
                        new VbaSyntaxPosition(
                            line.LineNumber,
                            argumentStart,
                            line.StartOffset + argumentStart),
                        new VbaSyntaxPosition(
                            line.LineNumber,
                            argumentEnd + 1,
                            line.StartOffset + argumentEnd + 1)));
            }

            var inString = false;
            var parenthesisDepth = 1;
            for (var argumentIndex = argumentStart + 1;
                argumentIndex < codeLine.Length;
                argumentIndex++)
            {
                if (codeLine[argumentIndex] == '"')
                {
                    if (inString
                        && argumentIndex + 1 < codeLine.Length
                        && codeLine[argumentIndex + 1] == '"')
                    {
                        argumentIndex++;
                        continue;
                    }

                    inString = !inString;
                    continue;
                }

                if (inString)
                {
                    continue;
                }

                if (codeLine[argumentIndex] == '(')
                {
                    parenthesisDepth++;
                    continue;
                }

                if (codeLine[argumentIndex] == ')')
                {
                    parenthesisDepth--;
                    if (parenthesisDepth == 0)
                    {
                        break;
                    }

                    continue;
                }

                if (parenthesisDepth != 1)
                {
                    continue;
                }

                var nameEnd = ReadIdentifierEnd(codeLine, argumentIndex);
                if (nameEnd == argumentIndex)
                {
                    continue;
                }

                var operatorStart = SkipWhitespace(codeLine, nameEnd);
                if (operatorStart + 1 < codeLine.Length
                    && codeLine[operatorStart] == ':'
                    && codeLine[operatorStart + 1] == '=')
                {
                    yield return new VbaSyntaxDiagnostic(
                        "syntax.raiseEventNamedArgumentNotAllowed",
                        "RaiseEvent arguments cannot use named-argument syntax.",
                        new VbaSyntaxRange(
                            new VbaSyntaxPosition(
                                line.LineNumber,
                                argumentIndex,
                                line.StartOffset + argumentIndex),
                            new VbaSyntaxPosition(
                                line.LineNumber,
                                operatorStart + 2,
                                line.StartOffset + operatorStart + 2)));
                }

                argumentIndex = nameEnd - 1;
            }

            yield break;
        }

        var statementEnd = FindRaiseEventStatementEnd(codeLine, argumentStart);
        while (statementEnd > argumentStart
            && VbaIdentifier.IsWhitespace(codeLine[statementEnd - 1]))
        {
            statementEnd--;
        }

        yield return new VbaSyntaxDiagnostic(
            "syntax.raiseEventArgumentListRequiresParentheses",
            "RaiseEvent arguments must be enclosed in parentheses.",
            new VbaSyntaxRange(
                new VbaSyntaxPosition(line.LineNumber, argumentStart, line.StartOffset + argumentStart),
                new VbaSyntaxPosition(line.LineNumber, statementEnd, line.StartOffset + statementEnd)));
    }

    private static int FindRaiseEventStatementEnd(string codeLine, int argumentStart)
    {
        var inString = false;
        var parenthesisDepth = 0;
        for (var index = argumentStart; index < codeLine.Length; index++)
        {
            if (codeLine[index] == '"')
            {
                if (inString
                    && index + 1 < codeLine.Length
                    && codeLine[index + 1] == '"')
                {
                    index++;
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (codeLine[index] == '(')
            {
                parenthesisDepth++;
            }
            else if (codeLine[index] == ')' && parenthesisDepth > 0)
            {
                parenthesisDepth--;
            }
            else if (codeLine[index] == ':'
                && parenthesisDepth == 0
                && (index + 1 >= codeLine.Length || codeLine[index + 1] != '='))
            {
                return index;
            }
        }

        return codeLine.Length;
    }

    private static int FindRaiseEventArgumentListEnd(string codeLine, int argumentStart)
    {
        var inString = false;
        var depth = 0;
        for (var index = argumentStart; index < codeLine.Length; index++)
        {
            if (codeLine[index] == '"')
            {
                if (inString
                    && index + 1 < codeLine.Length
                    && codeLine[index + 1] == '"')
                {
                    index++;
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (codeLine[index] == '(')
            {
                depth++;
            }
            else if (codeLine[index] == ')')
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

    private static bool HasRaiseEventOmittedArgument(
        string codeLine,
        int argumentStart,
        int argumentEnd)
    {
        var inString = false;
        var depth = 1;
        var slotHasValue = false;
        for (var index = argumentStart + 1; index < argumentEnd; index++)
        {
            if (codeLine[index] == '"')
            {
                if (inString
                    && index + 1 < argumentEnd
                    && codeLine[index + 1] == '"')
                {
                    index++;
                    slotHasValue = true;
                    continue;
                }

                inString = !inString;
                slotHasValue = true;
                continue;
            }

            if (inString)
            {
                slotHasValue = true;
                continue;
            }

            if (codeLine[index] == '(')
            {
                depth++;
                slotHasValue = true;
                continue;
            }

            if (codeLine[index] == ')')
            {
                if (depth > 1)
                {
                    depth--;
                }

                slotHasValue = true;
                continue;
            }

            if (depth == 1 && codeLine[index] == ',')
            {
                if (!slotHasValue)
                {
                    return true;
                }

                slotHasValue = false;
                continue;
            }

            if (!char.IsWhiteSpace(codeLine[index]))
            {
                slotHasValue = true;
            }
        }

        return !slotHasValue;
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

    private static bool TryCloseLeadingBlocks(
        string codeLine,
        Stack<BlockFrame> blockStack,
        out string? unexpectedClose)
    {
        unexpectedClose = null;
        var closedAny = false;
        foreach (var statement in SplitColonSeparatedStatements(codeLine))
        {
            var trimmedStatement = VbaIdentifier.TrimStartWhitespace(statement);
            if (VbaIdentifier.IsWhitespaceOnly(trimmedStatement))
            {
                continue;
            }

            if (!TryCloseBlock(trimmedStatement, blockStack, out var statementUnexpectedClose))
            {
                break;
            }

            closedAny = true;
            unexpectedClose ??= statementUnexpectedClose;
        }

        return closedAny;
    }

    private static bool ContainsBlockTerminatorStatement(string line, string keyword)
        => SplitColonSeparatedStatements(line).Any(statement =>
            IsBlockTerminatorStatement(statement, keyword));

    private static bool IsBlockTerminatorStatement(string statement, string keyword)
    {
        var tokens = VbaTokenStream.FromText(statement).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.LineContinuation
                and not VbaTokenKind.Comment)
            .ToArray();
        return tokens.Length >= 2
            && tokens[0].Text.Equals("End", StringComparison.OrdinalIgnoreCase)
            && tokens[1].Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SplitColonSeparatedStatements(string line)
    {
        var codeLine = VbaLexicalFacts.SplitCodeAndComment(line).CodePart;
        var tokens = VbaTokenStream.FromText(codeLine).Tokens;
        var statementStart = 0;
        for (var tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
        {
            var token = tokens[tokenIndex];
            if (token.Kind != VbaTokenKind.Punctuation
                || token.Text != ":"
                || IsNamedArgumentColon(tokens, tokenIndex))
            {
                continue;
            }

            yield return codeLine[statementStart..token.Range.Start.Character];
            statementStart = token.Range.End.Character;
        }

        yield return codeLine[statementStart..];
    }

    private static bool IsNamedArgumentColon(
        IReadOnlyList<VbaToken> tokens,
        int colonTokenIndex)
    {
        for (var tokenIndex = colonTokenIndex + 1; tokenIndex < tokens.Count; tokenIndex++)
        {
            if (tokens[tokenIndex].Kind is VbaTokenKind.Whitespace or VbaTokenKind.NewLine)
            {
                continue;
            }

            return tokens[tokenIndex].Text == "=";
        }

        return false;
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
        GetCallableSignatureEnd(
            match,
            line.Text,
            isExternal,
            out var isReturnArray);
        var signature = CreateSignature(
            name,
            parameters,
            typeReference,
            documentation,
            isReturnArray);
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
            VisibilityKeyword: match.Groups["visibility"].Value,
            DeclarationKeywordRange: CreateRange(
                sourceText,
                match,
                match.Groups["kind"].Success
                    ? "kind"
                    : "propertyDeclaration",
                line),
            ParameterListRange: match.Groups["parameters"].Success
                ? sourceText.RangeForLine(
                    line,
                    match.Groups["parameters"].Index - 1,
                    match.Groups["parameters"].Index
                        + match.Groups["parameters"].Length + 1)
                : null)
        {
            SignatureRange = CreateCallableSignatureRange(
                sourceText,
                match,
                line,
                isExternal),
            IsReturnArray = isReturnArray
        };
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
        GetCallableSignatureEnd(
            match,
            statement.Text,
            isExternal: false,
            out var isReturnArray);
        var signature = CreateSignature(
            name,
            parameters,
            typeReference,
            documentation,
            isReturnArray);
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
            VisibilityKeyword: match.Groups["visibility"].Value,
            DeclarationKeywordRange: RangeFromLogicalSpan(
                statement,
                match.Groups["kind"].Success
                    ? match.Groups["kind"].Index
                    : match.Groups["propertyDeclaration"].Index,
                match.Groups["kind"].Success
                    ? match.Groups["kind"].Index + match.Groups["kind"].Length
                    : match.Groups["propertyDeclaration"].Index
                        + match.Groups["propertyDeclaration"].Length),
            ParameterListRange: match.Groups["parameters"].Success
                ? RangeFromLogicalSpan(
                    statement,
                    match.Groups["parameters"].Index - 1,
                    match.Groups["parameters"].Index
                        + match.Groups["parameters"].Length + 1)
                : null)
        {
            SignatureRange = CreateCallableSignatureRange(match, statement),
            IsReturnArray = isReturnArray
        };
    }

    private static VbaSyntaxRange CreateCallableSignatureRange(
        VbaSourceText sourceText,
        Match match,
        VbaSourceLine line,
        bool isExternal)
    {
        var start = match.Groups["name"].Index;
        var end = GetCallableSignatureEnd(
            match,
            line.Text,
            isExternal,
            out _);
        return sourceText.RangeForLine(line, start, end);
    }

    private static VbaSyntaxRange CreateCallableSignatureRange(
        Match match,
        LogicalStatement statement)
    {
        var start = match.Groups["name"].Index;
        var end = GetCallableSignatureEnd(
            match,
            statement.Text,
            isExternal: false,
            out _);
        return RangeFromLogicalSpan(statement, start, end);
    }

    private static int GetCallableSignatureEnd(
        Match match,
        string text,
        bool isExternal,
        out bool isReturnArray)
    {
        isReturnArray = false;
        var name = match.Groups["name"];
        var typeCharacter = match.Groups["typeCharacter"];
        var parameters = match.Groups["parameters"];
        var end = typeCharacter.Success
            ? typeCharacter.Index + typeCharacter.Length
            : name.Index + name.Length;
        if (parameters.Success)
        {
            end = parameters.Index + parameters.Length + 1;
        }
        else if (isExternal)
        {
            end = Math.Max(end, match.Index + match.Length);
        }

        var suffixTokens = VbaTokenStream.FromText(text[end..]).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.LineContinuation
                and not VbaTokenKind.Comment)
            .ToArray();
        var index = 0;
        if (index >= suffixTokens.Length
            || !suffixTokens[index].Text.Equals("As", StringComparison.OrdinalIgnoreCase))
        {
            return end;
        }

        index++;
        if (index < suffixTokens.Length
            && suffixTokens[index].Text.Equals("New", StringComparison.OrdinalIgnoreCase))
        {
            index++;
        }

        if (index >= suffixTokens.Length
            || !IsTypeReferenceName(suffixTokens[index], allowAnyTypeReference: false))
        {
            return end;
        }

        var typeEndIndex = index;
        if (index + 2 < suffixTokens.Length
            && suffixTokens[index + 1].Text == "."
            && IsTypeReferenceName(suffixTokens[index + 2], allowAnyTypeReference: false))
        {
            typeEndIndex = index + 2;
        }

        var arrayOpenIndex = typeEndIndex + 1;
        if (arrayOpenIndex < suffixTokens.Length
            && suffixTokens[arrayOpenIndex].Text == "(")
        {
            var arrayCloseIndex = VbaBlockHeaderSyntax.FindMatchingCloseParenthesis(
                suffixTokens,
                arrayOpenIndex);
            if (arrayCloseIndex >= 0)
            {
                typeEndIndex = arrayCloseIndex;
                isReturnArray = true;
            }
        }

        return end + suffixTokens[typeEndIndex].Range.End.Offset;
    }

    private static Match MatchProcedureDeclaration(string text)
        => MatchIdentifier(
            ProcedurePattern,
            MaskReturnArrayDesignator(GetDeclarationHeaderText(text)));

    private static string GetDeclarationHeaderText(string text)
    {
        var separator = VbaTokenStream.FromText(text).Tokens.FirstOrDefault(token =>
            token.Kind == VbaTokenKind.Punctuation && token.Text == ":");
        return separator is null
            ? text
            : text[..separator.Range.Start.Offset];
    }

    private static string MaskReturnArrayDesignator(string text)
    {
        var tokens = VbaTokenStream.FromText(text).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.LineContinuation
                and not VbaTokenKind.Comment)
            .TakeWhile(token => token.Kind != VbaTokenKind.Punctuation
                || token.Text != ":")
            .ToArray();
        var end = tokens.Length;
        if (end > 0
            && tokens[end - 1].Text.Equals(
                "Static",
                StringComparison.OrdinalIgnoreCase))
        {
            end--;
        }

        if (end < 3
            || tokens[end - 2].Text != "("
            || tokens[end - 1].Text != ")"
            || !tokens.Take(end - 2).Any(token => token.Text.Equals(
                "As",
                StringComparison.OrdinalIgnoreCase)))
        {
            return text;
        }

        var masked = new StringBuilder(text);
        for (var index = end - 2; index < end; index++)
        {
            for (var offset = tokens[index].Range.Start.Offset;
                 offset < tokens[index].Range.End.Offset;
                 offset++)
            {
                masked[offset] = ' ';
            }
        }

        return masked.ToString();
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
            IsArray: declaration.IsReturnArray,
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
        string? callableKind = null,
        bool isInvalidEventPlacement = false)
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
            CallableKind: callableKind,
            IsInvalidEventPlacement: isInvalidEventPlacement);
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

            var typeReference = ParseTypeReference(
                line.Text,
                declaredName: nameToken.Text);
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
            if (IsLogicalContinuationTail(sourceText, lineIndex))
            {
                continue;
            }

            var logicalStatement = CreateLogicalStatement(sourceText, lineIndex);
            var codeLine = logicalStatement.Text;
            var match = LocalVariablePattern.Match(codeLine);
            var hasRecoveredOmittedIntroducer = false;
            var hasRecoveredVisibilityIntroducer = false;
            if (!match.Success)
            {
                match = RecoveredLocalVisibilityWithEventsVariablePattern.Match(codeLine);
                hasRecoveredVisibilityIntroducer = match.Success;
                if (!match.Success)
                {
                    match = RecoveredModuleWithEventsVariablePattern.Match(codeLine);
                    hasRecoveredOmittedIntroducer = match.Success;
                    if (!match.Success)
                    {
                        continue;
                    }
                }
            }

            foreach (var declaration in ParseVariableLikeDeclarations(
                sourceText,
                match.Groups["declarations"],
                line,
                VbaDeclarationKind.Variable,
                VbaDeclarationVisibility.Local,
                parentProcedureName: parentProcedureName,
                parentProcedureRange: parentProcedureRange,
                variableDeclarationIntroducer:
                    match.Groups["static"].Success
                        ? VbaVariableDeclarationIntroducer.Static
                        : hasRecoveredOmittedIntroducer
                            ? null
                            : hasRecoveredVisibilityIntroducer
                                ? GetVariableDeclarationIntroducer(
                                    match.Groups["introducer"].Value)
                                : VbaVariableDeclarationIntroducer.Dim,
                isStaticDefault: match.Groups["static"].Success,
                logicalStatement: logicalStatement))
            {
                declarations.Add(declaration);
            }

            lineIndex = logicalStatement.Range.End.Line;
        }
    }

    private static void AddRecoveredEventDeclarations(
        VbaSourceText sourceText,
        ICollection<VbaDeclarationSyntax> declarations,
        int startLine,
        int endLine,
        string? parentProcedureName,
        VbaSyntaxRange? parentProcedureRange)
    {
        for (var lineIndex = startLine; lineIndex < endLine; lineIndex++)
        {
            if (IsLogicalContinuationTail(sourceText, lineIndex)
                || !TryCreateEventSourceDeclaration(
                    sourceText,
                    lineIndex,
                    parentProcedureName,
                    parentProcedureRange,
                    isInvalidEventPlacement: true,
                    out var declaration,
                    out _,
                    out var statement))
            {
                continue;
            }

            declarations.Add(declaration);
            lineIndex = statement.Range.End.Line;
        }
    }

    private static bool IsLogicalContinuationTail(
        VbaSourceText sourceText,
        int lineIndex)
    {
        if (lineIndex <= 0)
        {
            return false;
        }

        var precedingLine = sourceText.Lines[lineIndex - 1];
        var precedingCode = VbaLexicalFacts.SplitCodeAndComment(precedingLine.Text).CodePart;
        return VbaSourceText.HasLineContinuation(precedingCode)
            && !CollectLineContinuationDiagnostics(precedingLine).Any(diagnostic =>
                diagnostic.Code == "syntax.invalidTrailingCommentContinuation");
    }

    private static bool TryCreateEventSourceDeclaration(
        VbaSourceText sourceText,
        int lineIndex,
        string? parentProcedureName,
        VbaSyntaxRange? parentProcedureRange,
        bool isInvalidEventPlacement,
        out VbaDeclarationSyntax declaration,
        out IReadOnlyList<VbaCallableParameterSyntax> parameters,
        out LogicalStatement statement)
    {
        statement = CreateLogicalStatement(sourceText, lineIndex);
        var eventMatch = MatchIdentifier(EventPattern, statement.Text);
        if (!eventMatch.Success)
        {
            declaration = null!;
            parameters = [];
            return false;
        }

        var documentation = ParseDocumentationComment(sourceText.Lines, lineIndex);
        var name = eventMatch.Groups["name"].Value;
        parameters = ParseParameterSyntax(eventMatch, statement, documentation);
        var nameGroup = eventMatch.Groups["name"];
        declaration = new VbaDeclarationSyntax(
            name,
            VbaDeclarationKind.Event,
            GetVisibility(eventMatch.Groups["visibility"].Value, defaultPublic: true),
            RangeFromLogicalSpan(
                statement,
                nameGroup.Index,
                nameGroup.Index + nameGroup.Length),
            lineIndex,
            Documentation: documentation?.HoverText,
            Signature: CreateSignature(name, parameters, null, documentation),
            ParentProcedureName: parentProcedureName,
            ParentProcedureRange: parentProcedureRange,
            DeclarationLabel: CreateDeclarationLabel("Event", name, parameters),
            CallableKind: "Event",
            IsInvalidEventPlacement: isInvalidEventPlacement,
            HasCompleteEventSignatureShape: HasCompleteEventSignatureShape(
                statement.Text,
                name),
            HasOptionalEventParameter: HasEventParameterModifier(
                eventMatch,
                statement.Text,
                "Optional"),
            HasParamArrayEventParameter: HasEventParameterModifier(
                eventMatch,
                statement.Text,
                "ParamArray"));
        return true;
    }

    private static bool HasEventParameterModifier(
        Match eventMatch,
        string statementText,
        string modifier)
    {
        if (!TryGetEventParameterSpan(
                eventMatch,
                statementText,
                out var parameterStart,
                out var parameterEnd))
        {
            return false;
        }

        return VbaTokenStream.FromText(statementText[parameterStart..parameterEnd]).Tokens.Any(token =>
            token.Kind == VbaTokenKind.Keyword
            && token.Text.Equals(modifier, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetEventParameterSpan(
        Match eventMatch,
        string statementText,
        out int parameterStart,
        out int parameterEnd)
    {
        var parameters = eventMatch.Groups["parameters"];
        if (parameters.Success)
        {
            parameterStart = parameters.Index;
            parameterEnd = parameters.Index + parameters.Length;
            return true;
        }

        var name = eventMatch.Groups["name"];
        var openParenthesis = statementText.IndexOf(
            '(',
            name.Index + name.Length);
        if (openParenthesis >= 0)
        {
            parameterStart = openParenthesis + 1;
            parameterEnd = statementText.Length;
            return true;
        }

        parameterStart = 0;
        parameterEnd = 0;
        return false;
    }

    private static bool HasCompleteEventSignatureShape(
        string statementText,
        string eventName)
    {
        var tokens = VbaTokenStream.FromText(statementText).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.Comment
                and not VbaTokenKind.LineContinuation)
            .ToArray();
        var eventIndex = tokens.Length > 0
                && tokens[0].Text.Equals("Event", StringComparison.OrdinalIgnoreCase)
            ? 0
            : tokens.Length > 1
                && (tokens[0].Text.Equals("Public", StringComparison.OrdinalIgnoreCase)
                    || tokens[0].Text.Equals("Private", StringComparison.OrdinalIgnoreCase)
                    || tokens[0].Text.Equals("Friend", StringComparison.OrdinalIgnoreCase))
                && tokens[1].Text.Equals("Event", StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : -1;
        var nameIndex = eventIndex + 1;
        if (eventIndex < 0
            || nameIndex >= tokens.Length
            || !tokens[nameIndex].Text.Equals(eventName, StringComparison.OrdinalIgnoreCase)
            || nameIndex + 1 >= tokens.Length
            || tokens[nameIndex + 1].Text != "(")
        {
            return false;
        }

        var openParenthesis = nameIndex + 1;
        var closeParenthesis = VbaBlockHeaderSyntax.FindMatchingCloseParenthesis(
            tokens,
            openParenthesis);
        return closeParenthesis == tokens.Length - 1
            && VbaBlockHeaderSyntax.HasCompleteParameterList(
                tokens,
                openParenthesis + 1,
                closeParenthesis,
                forbiddenParameterName: null,
                allowAnyTypeReference: false);
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
        VbaVariableDeclarationIntroducer? variableDeclarationIntroducer = null,
        bool isStaticDefault = false,
        LogicalStatement? logicalStatement = null)
    {
        var declarations = new List<VbaDeclarationSyntax>();
        foreach (var segment in SplitDeclarationSegments(declarationsGroup.Value))
        {
            var segmentStart = declarationsGroup.Index + segment.Start;
            if (!TryReadDeclaredName(segment.Text, out var nameToken, out var withEventsToken))
            {
                continue;
            }

            var name = nameToken.Text;
            var nameStart = segmentStart + nameToken.Range.Start.Offset;
            var isWithEvents = withEventsToken is not null;
            var isArray = IsArrayParameter(segment.Text, name);
            var typeReference = ParseTypeReference(
                segment.Text,
                declaredName: name);
            var isFixedLengthString = IsFixedLengthStringDeclaration(
                segment.Text,
                name);
            var withEventsKeywordRange = withEventsToken is null
                ? null
                : MapVariableDeclarationSpan(
                    sourceText,
                    line,
                    logicalStatement,
                    segmentStart + withEventsToken.Range.Start.Offset,
                    segmentStart + withEventsToken.Range.End.Offset);
            var withEventsArrayDesignatorRange = withEventsToken is null
                ? null
                : GetArrayDesignatorRange(
                    sourceText,
                    line,
                    segmentStart,
                    segment.Text,
                    name,
                    logicalStatement);
            var withEventsNewKeywordRange = withEventsToken is null
                ? null
                : GetAsNewKeywordRange(
                    sourceText,
                    line,
                    segmentStart,
                    segment.Text,
                    name,
                    logicalStatement);
            var withEventsTypeDeclarationCharacterRange = withEventsToken is null
                ? null
                : GetTypeDeclarationCharacterRange(
                    sourceText,
                    line,
                    segmentStart,
                    segment.Text,
                    name,
                    logicalStatement);
            var withEventsTypeRequiredRange = withEventsToken is null
                ? null
                : GetWithEventsTypeRequiredRange(
                    sourceText,
                    line,
                    segmentStart,
                    segment.Text,
                    nameToken,
                    logicalStatement);
            var withEventsTypeReferenceRange = withEventsToken is null
                ? null
                : GetExplicitTypeReferenceRange(
                    sourceText,
                    line,
                    segmentStart,
                    segment.Text,
                    name,
                    logicalStatement);
            declarations.Add(new VbaDeclarationSyntax(
                name,
                kind,
                visibility,
                MapVariableDeclarationSpan(
                    sourceText,
                    line,
                    logicalStatement,
                    nameStart,
                    nameStart + name.Length),
                logicalStatement?.Range.Start.Line ?? line.LineNumber,
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
                IsArray: isArray,
                WithEventsKeywordRange: withEventsKeywordRange,
                VariableDeclarationIntroducer: variableDeclarationIntroducer,
                WithEventsArrayDesignatorRange: withEventsArrayDesignatorRange,
                WithEventsNewKeywordRange: withEventsNewKeywordRange,
                WithEventsTypeDeclarationCharacterRange: withEventsTypeDeclarationCharacterRange,
                WithEventsTypeRequiredRange: withEventsTypeRequiredRange,
                WithEventsTypeReferenceRange: withEventsTypeReferenceRange,
                HasRecognizableWithEventsDeclaratorShape: withEventsToken is null
                    || HasRecognizableWithEventsDeclaratorShape(
                        segment.Text,
                        nameToken))
            {
                IsFixedLengthString = isFixedLengthString
            });
        }

        return declarations;
    }

    private static bool IsFixedLengthStringDeclaration(
        string text,
        string declaredName)
    {
        var tokens = VbaTokenStream.FromText(text).Tokens
            .Where(token => token.Kind is not (
                VbaTokenKind.Whitespace
                or VbaTokenKind.NewLine
                or VbaTokenKind.LineContinuation
                or VbaTokenKind.Comment))
            .ToArray();
        var asIndex = FindTypeClauseAsIndex(tokens, declaredName);
        if (asIndex < 0 || asIndex + 2 >= tokens.Length)
        {
            return false;
        }

        var typeIndex = asIndex + 1;
        if (tokens[typeIndex].Text.Equals("New", StringComparison.OrdinalIgnoreCase))
        {
            typeIndex++;
        }

        return typeIndex + 1 < tokens.Length
            && tokens[typeIndex].Text.Equals(
                "String",
                StringComparison.OrdinalIgnoreCase)
            && tokens[typeIndex + 1].Text == "*";
    }

    private static bool TryReadDeclaredName(
        string text,
        out VbaToken nameToken,
        out VbaToken? withEventsToken)
    {
        withEventsToken = null;
        foreach (var token in VbaTokenStream.FromText(text).Tokens)
        {
            if (token.Kind == VbaTokenKind.Whitespace)
            {
                continue;
            }

            if (withEventsToken is null
                && token.Kind == VbaTokenKind.Keyword
                && token.Text.Equals("WithEvents", StringComparison.OrdinalIgnoreCase))
            {
                withEventsToken = token;
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
            var nameToken = ParseParameterNameToken(segment.Text);
            if (nameToken is null)
            {
                continue;
            }

            var name = nameToken.Text;
            var nameOffset = nameToken.Range.Start.Character;
            var start = parametersGroup.Index + segment.Start + nameOffset;
            parameters.Add(new VbaCallableParameterSyntax(
                name,
                sourceText.RangeForLine(line, start, start + name.Length),
                documentation?.ParameterDocs.TryGetValue(name, out var parameterDocumentation) == true
                    ? parameterDocumentation
                    : null,
                ParseTypeReference(
                    segment.Text,
                    allowAnyTypeReference,
                    name),
                IsOptionalParameter(segment.Text),
                IsByRefParameter(segment.Text),
                IsParamArrayParameter(segment.Text),
                IsArrayParameter(segment.Text, name))
            {
                DefaultExpression = GetParameterDefaultExpression(segment.Text)
            });
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
            var nameToken = ParseParameterNameToken(segment.Text);
            if (nameToken is null)
            {
                continue;
            }

            var name = nameToken.Text;
            var nameOffset = nameToken.Range.Start.Character;
            var start = parametersGroup.Index + segment.Start + nameOffset;
            parameters.Add(new VbaCallableParameterSyntax(
                name,
                RangeFromLogicalSpan(statement, start, start + name.Length),
                documentation?.ParameterDocs.TryGetValue(name, out var parameterDocumentation) == true
                    ? parameterDocumentation
                    : null,
                ParseTypeReference(segment.Text, declaredName: name),
                IsOptionalParameter(segment.Text),
                IsByRefParameter(segment.Text),
                IsParamArrayParameter(segment.Text),
                IsArrayParameter(segment.Text, name))
            {
                DefaultExpression = GetParameterDefaultExpression(segment.Text)
            });
        }

        return parameters;
    }

    private static VbaCallableSignatureSyntax CreateSignature(
        string name,
        IReadOnlyList<VbaCallableParameterSyntax> parameters,
        VbaTypeReferenceSyntax? returnTypeReference,
        DocumentationComment? documentation,
        bool isReturnArray = false)
    {
        var returnTypeName = returnTypeReference?.Name;
        var label = $"{name}({string.Join(", ", parameters.Select(CreateSignatureParameterLabel))})";
        if (!string.IsNullOrEmpty(returnTypeName))
        {
            label = $"{label} As {returnTypeName}{(isReturnArray ? "()" : "")}";
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
                    parameter.IsArray,
                    parameter.Range)
                {
                    DefaultExpression = parameter.DefaultExpression
                })
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

    private static string? GetParameterDefaultExpression(string text)
    {
        var tokens = VbaTokenStream.FromText(text).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.Comment
                and not VbaTokenKind.LineContinuation)
            .ToArray();
        var parenthesesDepth = 0;
        for (var index = 0; index < tokens.Length; index++)
        {
            if (tokens[index].Text == "(")
            {
                parenthesesDepth++;
                continue;
            }

            if (tokens[index].Text == ")")
            {
                parenthesesDepth--;
                continue;
            }

            if (tokens[index].Text != "=" || parenthesesDepth != 0)
            {
                continue;
            }

            return string.Concat(tokens[(index + 1)..].Select(token => token.Text));
        }

        return null;
    }

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
        for (var index = 0; index < tokens.Length; index++)
        {
            if (VbaIdentifierSyntaxFacts.IsValidDeclaredName(tokens[index])
                && tokens[index].Text.Equals(name, StringComparison.Ordinal))
            {
                var arrayMarkerIndex = index + 1;
                if (arrayMarkerIndex < tokens.Length
                    && tokens[index].Range.End.Offset
                        == tokens[arrayMarkerIndex].Range.Start.Offset
                    && VbaLanguageVocabulary.TryGetTypeDeclarationCharacterTypeName(
                        tokens[arrayMarkerIndex].Text,
                        out _))
                {
                    arrayMarkerIndex++;
                }

                return arrayMarkerIndex < tokens.Length
                    && tokens[arrayMarkerIndex].Kind == VbaTokenKind.Punctuation
                    && tokens[arrayMarkerIndex].Text == "(";
            }
        }

        return false;
    }

    private static VbaSyntaxRange MapVariableDeclarationSpan(
        VbaSourceText sourceText,
        VbaSourceLine line,
        LogicalStatement? logicalStatement,
        int start,
        int end)
        => logicalStatement is null
            ? sourceText.RangeForLine(line, start, end)
            : RangeFromLogicalSpan(logicalStatement, start, end);

    private static VbaSyntaxRange? GetArrayDesignatorRange(
        VbaSourceText sourceText,
        VbaSourceLine line,
        int segmentStart,
        string text,
        string name,
        LogicalStatement? logicalStatement)
    {
        var tokens = VbaTokenStream.FromText(text).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.LineContinuation
                and not VbaTokenKind.Comment)
            .ToArray();
        for (var index = 0; index < tokens.Length; index++)
        {
            if (!VbaIdentifierSyntaxFacts.IsValidDeclaredName(tokens[index])
                || !tokens[index].Text.Equals(name, StringComparison.Ordinal))
            {
                continue;
            }

            var openIndex = index + 1;
            if (openIndex < tokens.Length
                && tokens[index].Range.End.Offset == tokens[openIndex].Range.Start.Offset
                && VbaLanguageVocabulary.TryGetTypeDeclarationCharacterTypeName(
                    tokens[openIndex].Text,
                    out _))
            {
                openIndex++;
            }

            if (openIndex >= tokens.Length || tokens[openIndex].Text != "(")
            {
                return null;
            }

            var matchingCloseIndex = VbaBlockHeaderSyntax.FindMatchingCloseParenthesis(
                tokens,
                openIndex);
            if (matchingCloseIndex >= 0)
            {
                return MapVariableDeclarationSpan(
                    sourceText,
                    line,
                    logicalStatement,
                    segmentStart + tokens[openIndex].Range.Start.Offset,
                    segmentStart + tokens[matchingCloseIndex].Range.End.Offset);
            }

            var depth = 0;
            var recoveredEndOffset = tokens[openIndex].Range.End.Offset;
            for (var closeIndex = openIndex; closeIndex < tokens.Length; closeIndex++)
            {
                if (tokens[closeIndex].Text == "(")
                {
                    depth++;
                }
                else if (tokens[closeIndex].Text == ")" && --depth == 0)
                {
                    return MapVariableDeclarationSpan(
                        sourceText,
                        line,
                        logicalStatement,
                        segmentStart + tokens[openIndex].Range.Start.Offset,
                        segmentStart + tokens[closeIndex].Range.End.Offset);
                }

                if (closeIndex > openIndex
                    && depth == 1
                    && tokens[closeIndex].Kind == VbaTokenKind.Keyword
                    && tokens[closeIndex].Text.Equals(
                        "As",
                        StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                recoveredEndOffset = tokens[closeIndex].Range.End.Offset;
            }

            return MapVariableDeclarationSpan(
                sourceText,
                line,
                logicalStatement,
                segmentStart + tokens[openIndex].Range.Start.Offset,
                segmentStart + recoveredEndOffset);
        }

        return null;
    }

    private static VbaSyntaxRange? GetAsNewKeywordRange(
        VbaSourceText sourceText,
        VbaSourceLine line,
        int segmentStart,
        string text,
        string declaredName,
        LogicalStatement? logicalStatement)
    {
        var tokens = VbaTokenStream.FromText(text).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.LineContinuation
                and not VbaTokenKind.Comment)
            .ToArray();
        var asIndex = FindTypeClauseAsIndex(tokens, declaredName);
        if (asIndex < 0
            || asIndex + 1 >= tokens.Length
            || !tokens[asIndex + 1].Text.Equals(
                "New",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var newToken = tokens[asIndex + 1];
        return MapVariableDeclarationSpan(
            sourceText,
            line,
            logicalStatement,
            segmentStart + newToken.Range.Start.Offset,
            segmentStart + newToken.Range.End.Offset);
    }

    private static VbaSyntaxRange? GetTypeDeclarationCharacterRange(
        VbaSourceText sourceText,
        VbaSourceLine line,
        int segmentStart,
        string text,
        string name,
        LogicalStatement? logicalStatement)
    {
        var tokens = VbaTokenStream.FromText(text).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.LineContinuation
                and not VbaTokenKind.Comment)
            .ToArray();
        for (var index = 0; index + 1 < tokens.Length; index++)
        {
            var nameToken = tokens[index];
            var typeCharacter = tokens[index + 1];
            if (!nameToken.Text.Equals(name, StringComparison.Ordinal)
                || nameToken.Range.End.Offset != typeCharacter.Range.Start.Offset
                || !VbaLanguageVocabulary.TryGetTypeDeclarationCharacterTypeName(
                    typeCharacter.Text,
                    out _))
            {
                continue;
            }

            return MapVariableDeclarationSpan(
                sourceText,
                line,
                logicalStatement,
                segmentStart + typeCharacter.Range.Start.Offset,
                segmentStart + typeCharacter.Range.End.Offset);
        }

        return null;
    }

    private static VbaSyntaxRange? GetWithEventsTypeRequiredRange(
        VbaSourceText sourceText,
        VbaSourceLine line,
        int segmentStart,
        string text,
        VbaToken nameToken,
        LogicalStatement? logicalStatement)
    {
        var tokens = VbaTokenStream.FromText(text).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.LineContinuation
                and not VbaTokenKind.Comment)
            .ToArray();
        var asIndex = FindTypeClauseAsIndex(tokens, nameToken.Text);
        if (asIndex >= 0
            && ParseTypeReferenceAfterAs(
                text,
                declaredName: nameToken.Text) is not null)
        {
            return null;
        }

        var selectedRange = asIndex >= 0
            ? tokens[asIndex].Range
            : nameToken.Range;
        return MapVariableDeclarationSpan(
            sourceText,
            line,
            logicalStatement,
            segmentStart + selectedRange.Start.Offset,
            segmentStart + selectedRange.End.Offset);
    }

    private static bool HasRecognizableWithEventsDeclaratorShape(
        string text,
        VbaToken nameToken)
    {
        var tokens = VbaTokenStream.FromText(text).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.LineContinuation
                and not VbaTokenKind.Comment)
            .ToArray();
        if (tokens.Length < 2
            || !tokens[0].Text.Equals(
                "WithEvents",
                StringComparison.OrdinalIgnoreCase)
            || tokens[1].Range != nameToken.Range)
        {
            return false;
        }

        var index = 2;
        if (index < tokens.Length
            && tokens[index].Range.Start.Offset == nameToken.Range.End.Offset
            && VbaLanguageVocabulary.TryGetTypeDeclarationCharacterTypeName(
                tokens[index].Text,
                out _))
        {
            index++;
        }

        if (index < tokens.Length && tokens[index].Text == "(")
        {
            var closeIndex = VbaBlockHeaderSyntax.FindMatchingCloseParenthesis(
                tokens,
                index);
            if (closeIndex >= 0)
            {
                index = closeIndex + 1;
            }
            else
            {
                var asIndex = Array.FindIndex(
                    tokens,
                    index + 1,
                    token => token.Kind == VbaTokenKind.Keyword
                        && token.Text.Equals(
                            "As",
                            StringComparison.OrdinalIgnoreCase));
                if (asIndex < 0)
                {
                    return true;
                }

                index = asIndex;
            }
        }

        if (index == tokens.Length)
        {
            return true;
        }

        if (!tokens[index].Text.Equals("As", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        index++;
        if (index < tokens.Length
            && tokens[index].Text.Equals("New", StringComparison.OrdinalIgnoreCase))
        {
            index++;
        }

        if (index == tokens.Length)
        {
            return true;
        }

        if (!IsTypeReferenceName(tokens[index], allowAnyTypeReference: false))
        {
            return index == tokens.Length - 1;
        }

        index++;
        if (index + 1 < tokens.Length
            && tokens[index].Text == "."
            && IsTypeReferenceName(tokens[index + 1], allowAnyTypeReference: false))
        {
            index += 2;
        }

        return index == tokens.Length;
    }

    private static VbaSyntaxRange? GetExplicitTypeReferenceRange(
        VbaSourceText sourceText,
        VbaSourceLine line,
        int segmentStart,
        string text,
        string declaredName,
        LogicalStatement? logicalStatement)
    {
        var tokens = VbaTokenStream.FromText(text).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.LineContinuation
                and not VbaTokenKind.Comment)
            .ToArray();
        var asIndex = FindTypeClauseAsIndex(tokens, declaredName);
        if (asIndex < 0 || asIndex + 1 >= tokens.Length)
        {
            return null;
        }

        var typeIndex = asIndex + 1;
        if (tokens[typeIndex].Text.Equals("New", StringComparison.OrdinalIgnoreCase)
            && ++typeIndex >= tokens.Length)
        {
            return null;
        }

        if (!IsTypeReferenceName(tokens[typeIndex], allowAnyTypeReference: false))
        {
            return null;
        }

        var endIndex = typeIndex;
        if (typeIndex + 2 < tokens.Length
            && tokens[typeIndex + 1].Text == "."
            && IsTypeReferenceName(tokens[typeIndex + 2], allowAnyTypeReference: false))
        {
            endIndex = typeIndex + 2;
        }

        return MapVariableDeclarationSpan(
            sourceText,
            line,
            logicalStatement,
            segmentStart + tokens[typeIndex].Range.Start.Offset,
            segmentStart + tokens[endIndex].Range.End.Offset);
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

    private static VbaToken? ParseParameterNameToken(string parameter)
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

            return VbaIdentifierSyntaxFacts.IsValidDeclaredName(token) ? token : null;
        }

        return null;
    }

    private static VbaTypeReferenceSyntax? ParseReturnTypeReference(Match match, string line)
    {
        var parametersGroup = match.Groups["parameters"];
        var explicitType = parametersGroup.Success
            ? ParseReturnTypeReference(
                line[(parametersGroup.Index + parametersGroup.Length)..])
            : ParseReturnTypeReference(line);
        return explicitType
            ?? ParseTypeDeclarationCharacterReference(
                line,
                match.Groups["name"].Value);
    }

    private static VbaTypeReferenceSyntax? ParseReturnTypeReference(string text)
        => ParseTypeReferenceAfterAs(text);

    private static VbaTypeReferenceSyntax? ParseTypeReference(
        string text,
        bool allowAnyTypeReference = false,
        string? declaredName = null)
        => ParseTypeReferenceAfterAs(text, allowAnyTypeReference, declaredName)
            ?? ParseTypeDeclarationCharacterReference(text, declaredName);

    private static VbaTypeReferenceSyntax? ParseTypeDeclarationCharacterReference(
        string text,
        string? declaredName)
    {
        if (declaredName is null)
        {
            return null;
        }

        var tokens = VbaTokenStream.FromText(text).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.LineContinuation
                and not VbaTokenKind.Comment)
            .ToArray();
        for (var index = 0; index + 1 < tokens.Length; index++)
        {
            var name = tokens[index];
            var typeCharacter = tokens[index + 1];
            if (name.Text.Equals(declaredName, StringComparison.Ordinal)
                && typeCharacter.Range.Start.Offset == name.Range.End.Offset
                && VbaLanguageVocabulary.TryGetTypeDeclarationCharacterTypeName(
                    typeCharacter.Text,
                    out var canonicalTypeName))
            {
                return new VbaTypeReferenceSyntax(
                    canonicalTypeName,
                    Qualifier: null,
                    IsNew: false);
            }
        }

        return null;
    }

    private static VbaTypeReferenceSyntax? ParseTypeReferenceAfterAs(
        string text,
        bool allowAnyTypeReference = false,
        string? declaredName = null)
    {
        var tokens = VbaTokenStream.FromText(text).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.LineContinuation
                and not VbaTokenKind.Comment)
            .ToArray();
        var asIndex = FindTypeClauseAsIndex(tokens, declaredName);
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

    private static int FindTypeClauseAsIndex(
        IReadOnlyList<VbaToken> tokens,
        string? declaredName)
    {
        if (declaredName is null)
        {
            return tokens
                .Select((token, index) => (token, index))
                .Where(candidate => candidate.token.Text.Equals(
                    "As",
                    StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.index)
                .DefaultIfEmpty(-1)
                .First();
        }

        var nameIndex = Enumerable.Range(0, tokens.Count).FirstOrDefault(
            index => VbaIdentifierSyntaxFacts.IsValidDeclaredName(tokens[index])
                && tokens[index].Text.Equals(declaredName, StringComparison.Ordinal),
            -1);
        if (nameIndex < 0)
        {
            return -1;
        }

        var index = nameIndex + 1;
        if (index < tokens.Count
            && tokens[nameIndex].Range.End.Offset == tokens[index].Range.Start.Offset
            && VbaLanguageVocabulary.TryGetTypeDeclarationCharacterTypeName(
                tokens[index].Text,
                out _))
        {
            index++;
        }

        if (index < tokens.Count && tokens[index].Text == "(")
        {
            var closeIndex = VbaBlockHeaderSyntax.FindMatchingCloseParenthesis(
                tokens,
                index);
            if (closeIndex < 0)
            {
                return -1;
            }

            index = closeIndex + 1;
        }

        return index < tokens.Count
            && tokens[index].Text.Equals("As", StringComparison.OrdinalIgnoreCase)
                ? index
                : -1;
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
            if (ContainsBlockTerminatorStatement(lines[lineIndex].Text, keyword)
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

    private static VbaVariableDeclarationIntroducer GetVariableDeclarationIntroducer(string value)
        => value.ToUpperInvariant() switch
        {
            "PUBLIC" => VbaVariableDeclarationIntroducer.Public,
            "PRIVATE" => VbaVariableDeclarationIntroducer.Private,
            "DIM" => VbaVariableDeclarationIntroducer.Dim,
            "FRIEND" => VbaVariableDeclarationIntroducer.Friend,
            "GLOBAL" => VbaVariableDeclarationIntroducer.Global,
            _ => throw new ArgumentException(
                "Unsupported variable declaration introducer.",
                nameof(value))
        };

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
        VbaModuleIdentityMetadata metadata)
    {
        if (metadata.IsAuthoritative
            && metadata.AuthoritativeRecordIndex is int authoritativeRecordIndex)
        {
            var record = metadata.Records[authoritativeRecordIndex];
            return new VbaModuleIdentitySyntax(
                metadata.Name!,
                record.RepairRange,
                metadata);
        }

        var fallbackName = GetFileBaseName(uri);
        return new VbaModuleIdentitySyntax(
            fallbackName,
            new VbaSyntaxRange(sourceText.StartPosition, sourceText.StartPosition),
            metadata);
    }

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
