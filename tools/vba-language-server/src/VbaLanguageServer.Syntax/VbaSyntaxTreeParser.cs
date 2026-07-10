using System.Text.RegularExpressions;

namespace VbaLanguageServer.Syntax;

internal static class VbaSyntaxTreeParser
{
    private static readonly Regex AttributePattern = new(
        "^\\s*Attribute\\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*(?<value>.+?)\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex OptionPattern = new(
        "^\\s*Option\\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ProcedurePattern = new(
        "^\\s*(?:(?<visibility>Public|Private|Friend)\\s+)?(?:(?<static>Static)\\s+)?(?:(?<kind>Sub|Function)|Property\\s+(?<propertyKind>Get|Let|Set))\\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\\s*(?:\\((?<parameters>[^)]*)\\))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DeclarePattern = new(
        "^\\s*(?:(?<visibility>Public|Private)\\s+)?Declare\\s+(?:PtrSafe\\s+)?(?<kind>Sub|Function)\\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\\s+Lib\\s+\"[^\"]+\"(?:\\s+Alias\\s+\"[^\"]+\")?\\s*(?:\\((?<parameters>[^)]*)\\))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EventPattern = new(
        "^\\s*(?:(?<visibility>Public|Private|Friend)\\s+)?Event\\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\\s*(?:\\((?<parameters>[^)]*)\\))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EnumPattern = new(
        "^\\s*(?:(?<visibility>Public|Private|Friend)\\s+)?Enum\\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex TypePattern = new(
        "^\\s*(?:(?<visibility>Public|Private|Friend)\\s+)?Type\\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ConstPattern = new(
        "^\\s*(?:(?<visibility>Public|Private|Friend)\\s+)?Const\\s+(?<declarations>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ModuleVariablePattern = new(
        "^\\s*(?<visibility>Public|Private|Friend|Dim)\\s+(?<declarations>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LocalVariablePattern = new(
        "^\\s*(?:Dim|Static)\\s+(?<declarations>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex IdentifierPattern = new(
        "[A-Za-z_][A-Za-z0-9_]*",
        RegexOptions.CultureInvariant);

    public static VbaSyntaxTree ParseModule(string uri, string source)
    {
        var tokenStream = VbaTokenStream.FromText(source);
        var sourceText = SourceText.From(source);
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

        var attributes = ParseAttributes(sourceText, codeStartLine);
        var options = ParseOptions(sourceText, codeStartLine);
        var identity = CreateIdentity(uri, sourceText, kind, attributes);
        var parsedMembers = ParseMembersAndDeclarations(sourceText, codeStartLine);
        var module = new VbaModuleSyntax(
            kind,
            identity,
            attributes,
            options,
            parsedMembers.Members,
            parsedMembers.Declarations,
            parsedMembers.CallableDeclarations,
            designerBlock,
            codeStartLine,
            sourceText.FullRange);
        return new VbaSyntaxTree(uri, source, tokenStream, module, diagnostics);
    }

    private static IReadOnlyList<VbaModuleAttributeSyntax> ParseAttributes(SourceText sourceText, int startLine)
    {
        var attributes = new List<VbaModuleAttributeSyntax>();
        for (var index = startLine; index < sourceText.Lines.Count; index++)
        {
            var line = sourceText.Lines[index];
            var match = AttributePattern.Match(line.Text);
            if (!match.Success)
            {
                continue;
            }

            var nameGroup = match.Groups["name"];
            var valueGroup = match.Groups["value"];
            var rawValue = valueGroup.Value.Trim();
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

    private static IReadOnlyList<VbaModuleOptionSyntax> ParseOptions(SourceText sourceText, int startLine)
    {
        var options = new List<VbaModuleOptionSyntax>();
        for (var index = startLine; index < sourceText.Lines.Count; index++)
        {
            var line = sourceText.Lines[index];
            var match = OptionPattern.Match(line.Text);
            if (!match.Success)
            {
                continue;
            }

            var text = match.Value.Trim();
            var startCharacter = line.Text.IndexOf(text, StringComparison.Ordinal);
            options.Add(new VbaModuleOptionSyntax(
                text,
                sourceText.RangeForLine(line, startCharacter, startCharacter + text.Length)));
        }

        return options;
    }

    private static ParsedMembers ParseMembersAndDeclarations(SourceText sourceText, int codeStartLine)
    {
        var members = new List<VbaModuleMemberSyntax>();
        var declarations = new List<VbaDeclarationSyntax>();
        var callableDeclarations = new List<VbaCallableDeclarationSyntax>();

        for (var lineIndex = codeStartLine; lineIndex < sourceText.Lines.Count; lineIndex++)
        {
            var line = sourceText.Lines[lineIndex];
            var codeLine = StripApostropheComment(line.Text);
            if (string.IsNullOrWhiteSpace(codeLine))
            {
                continue;
            }

            var declareMatch = DeclarePattern.Match(codeLine);
            if (declareMatch.Success)
            {
                var declaration = CreateCallableDeclaration(
                    sourceText,
                    declareMatch,
                    line,
                    lineIndex,
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

            var eventMatch = EventPattern.Match(codeLine);
            if (eventMatch.Success)
            {
                var documentation = ParseDocumentationComment(sourceText.Lines, lineIndex);
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
                    documentation: documentation?.HoverText));
                foreach (var parameter in ParseParameterSyntax(sourceText, eventMatch, line, documentation))
                {
                    declarations.Add(CreateParameterDeclaration(parameter, parameter.Range.Start.Line));
                }

                continue;
            }

            var enumMatch = EnumPattern.Match(codeLine);
            if (enumMatch.Success)
            {
                var visibility = GetVisibility(enumMatch.Groups["visibility"].Value, defaultPublic: true);
                declarations.Add(CreateDeclaration(
                    sourceText,
                    enumMatch,
                    "name",
                    VbaDeclarationKind.Enum,
                    visibility,
                    line));
                var endLine = FindBlockEndLine(sourceText.Lines, lineIndex + 1, "Enum");
                AddMemberDeclarations(
                    sourceText,
                    declarations,
                    lineIndex + 1,
                    endLine,
                    VbaDeclarationKind.EnumMember,
                    visibility);
                members.Add(new VbaModuleMemberSyntax(
                    enumMatch.Groups["name"].Value,
                    VbaDeclarationKind.Enum,
                    CreateBlockRange(sourceText.Lines, lineIndex, endLine)));
                lineIndex = endLine;
                continue;
            }

            var typeMatch = TypePattern.Match(codeLine);
            if (typeMatch.Success)
            {
                var visibility = GetVisibility(typeMatch.Groups["visibility"].Value, defaultPublic: true);
                declarations.Add(CreateDeclaration(
                    sourceText,
                    typeMatch,
                    "name",
                    VbaDeclarationKind.Type,
                    visibility,
                    line));
                var endLine = FindBlockEndLine(sourceText.Lines, lineIndex + 1, "Type");
                AddMemberDeclarations(
                    sourceText,
                    declarations,
                    lineIndex + 1,
                    endLine,
                    VbaDeclarationKind.TypeMember,
                    visibility);
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

            var procedureMatch = ProcedurePattern.Match(codeLine);
            if (procedureMatch.Success)
            {
                var declaration = CreateCallableDeclaration(
                    sourceText,
                    procedureMatch,
                    line,
                    lineIndex,
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

    private static VbaCallableDeclarationSyntax CreateCallableDeclaration(
        SourceText sourceText,
        Match match,
        SourceLine line,
        int lineIndex,
        bool isExternal = false,
        bool isStatic = false)
    {
        var name = match.Groups["name"].Value;
        var documentation = ParseDocumentationComment(sourceText.Lines, lineIndex);
        var parameters = ParseParameterSyntax(sourceText, match, line, documentation);
        var signature = CreateSignature(name, parameters, line.Text, documentation);
        var typeReference = ParseReturnTypeReference(line.Text);
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
            : FindBlockEndLine(sourceText.Lines, lineIndex + 1, endKeyword);

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
            IsStatic: isStatic);
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
            IsStatic: declaration.IsStatic);

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
            TypeReference: parameter.TypeReference);

    private static VbaDeclarationSyntax CreateDeclaration(
        SourceText sourceText,
        Match match,
        string groupName,
        VbaDeclarationKind kind,
        VbaDeclarationVisibility visibility,
        SourceLine line,
        string? documentation = null,
        VbaCallableSignatureSyntax? signature = null,
        string? parentProcedureName = null,
        VbaSyntaxRange? parentProcedureRange = null,
        string? parentTypeName = null,
        VbaTypeReferenceSyntax? typeReference = null,
        bool isWithEvents = false,
        bool isExternal = false,
        bool isStatic = false)
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
            IsStatic: isStatic);
    }

    private static VbaModuleMemberSyntax CreateSingleLineMember(
        SourceText sourceText,
        Match match,
        string groupName,
        VbaDeclarationKind kind,
        SourceLine line)
        => new(
            match.Groups[groupName].Value,
            kind,
            CreateLineRange(line));

    private static void AddMemberDeclarations(
        SourceText sourceText,
        ICollection<VbaDeclarationSyntax> declarations,
        int startLine,
        int endLine,
        VbaDeclarationKind kind,
        VbaDeclarationVisibility visibility)
    {
        for (var lineIndex = startLine; lineIndex < endLine; lineIndex++)
        {
            var line = sourceText.Lines[lineIndex];
            var codeLine = StripApostropheComment(line.Text);
            var match = IdentifierPattern.Match(codeLine);
            if (!match.Success)
            {
                continue;
            }

            declarations.Add(new VbaDeclarationSyntax(
                match.Value,
                kind,
                visibility,
                sourceText.RangeForLine(line, match.Index, match.Index + match.Length),
                lineIndex,
                TypeReference: ParseTypeReference(line.Text)));
        }
    }

    private static void AddLocalVariableDeclarations(
        SourceText sourceText,
        ICollection<VbaDeclarationSyntax> declarations,
        int startLine,
        int endLine,
        string parentProcedureName,
        VbaSyntaxRange parentProcedureRange)
    {
        for (var lineIndex = startLine; lineIndex < endLine; lineIndex++)
        {
            var line = sourceText.Lines[lineIndex];
            var codeLine = StripApostropheComment(line.Text);
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
                parentProcedureRange: parentProcedureRange))
            {
                declarations.Add(declaration);
            }
        }
    }

    private static IReadOnlyList<VbaDeclarationSyntax> ParseVariableLikeDeclarations(
        SourceText sourceText,
        Group declarationsGroup,
        SourceLine line,
        VbaDeclarationKind kind,
        VbaDeclarationVisibility visibility,
        string? documentation = null,
        string? parentProcedureName = null,
        VbaSyntaxRange? parentProcedureRange = null,
        bool isWithEventsDefault = false)
    {
        var declarations = new List<VbaDeclarationSyntax>();
        foreach (var segment in SplitDeclarationSegments(declarationsGroup.Value))
        {
            var segmentStart = declarationsGroup.Index + segment.Start;
            var nameMatch = Regex.Match(
                segment.Text,
                "^\\s*(?:WithEvents\\s+)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!nameMatch.Success)
            {
                continue;
            }

            var name = nameMatch.Groups["name"].Value;
            var nameStart = segmentStart + nameMatch.Groups["name"].Index;
            var isWithEvents = isWithEventsDefault
                || Regex.IsMatch(segment.Text, "\\bWithEvents\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            declarations.Add(new VbaDeclarationSyntax(
                name,
                kind,
                visibility,
                sourceText.RangeForLine(line, nameStart, nameStart + name.Length),
                line.LineNumber,
                Documentation: documentation,
                ParentProcedureName: parentProcedureName,
                ParentProcedureRange: parentProcedureRange,
                TypeReference: ParseTypeReference(segment.Text),
                IsWithEvents: isWithEvents));
        }

        return declarations;
    }

    private static IReadOnlyList<VbaCallableParameterSyntax> ParseParameterSyntax(
        SourceText sourceText,
        Match match,
        SourceLine line,
        DocumentationComment? documentation)
    {
        var parametersGroup = match.Groups["parameters"];
        if (!parametersGroup.Success || string.IsNullOrWhiteSpace(parametersGroup.Value))
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
                ParseTypeReference(segment.Text)));
        }

        return parameters;
    }

    private static VbaCallableSignatureSyntax CreateSignature(
        string name,
        IReadOnlyList<VbaCallableParameterSyntax> parameters,
        string line,
        DocumentationComment? documentation)
    {
        var returnTypeName = ParseReturnTypeReference(line)?.Name;
        var label = $"{name}({string.Join(", ", parameters.Select(parameter => parameter.Name))})";
        if (!string.IsNullOrWhiteSpace(returnTypeName))
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
            parameters.Select(parameter => new VbaCallableParameterInfoSyntax(parameter.Name, parameter.Documentation)).ToArray(),
            documentationLines.Count == 0 ? null : string.Join('\n', documentationLines));
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

    private static DocumentationComment? ParseDocumentationComment(IReadOnlyList<SourceLine> lines, int declarationLine)
    {
        var rawLines = new Stack<string>();
        for (var lineIndex = declarationLine - 1; lineIndex >= 0; lineIndex--)
        {
            var trimmed = lines[lineIndex].Text.TrimStart();
            if (!trimmed.StartsWith("'*", StringComparison.Ordinal))
            {
                break;
            }

            rawLines.Push(trimmed[2..].TrimStart());
        }

        if (rawLines.Count == 0)
        {
            return null;
        }

        var bodyLines = new List<string>();
        var parameterDocs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? returnDocumentation = null;
        foreach (var rawLine in rawLines)
        {
            if (rawLine.StartsWith("@brief ", StringComparison.OrdinalIgnoreCase))
            {
                bodyLines.Add(rawLine["@brief ".Length..].Trim());
                continue;
            }

            if (rawLine.StartsWith("@details ", StringComparison.OrdinalIgnoreCase))
            {
                if (bodyLines.Count > 0 && bodyLines[^1].Length != 0)
                {
                    bodyLines.Add("");
                }

                bodyLines.Add(rawLine["@details ".Length..].Trim());
                continue;
            }

            if (rawLine.StartsWith("@param ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = rawLine["@param ".Length..].Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    parameterDocs[parts[0]] = parts[1].Trim();
                }

                continue;
            }

            if (rawLine.StartsWith("@return ", StringComparison.OrdinalIgnoreCase))
            {
                returnDocumentation = rawLine["@return ".Length..].Trim();
                continue;
            }

            bodyLines.Add(rawLine.Trim());
        }

        var hoverLines = new List<string>(bodyLines);
        foreach (var parameter in parameterDocs)
        {
            if (hoverLines.Count > 0 && hoverLines[^1].Length != 0)
            {
                hoverLines.Add("");
            }

            hoverLines.Add($"@param {parameter.Key} {parameter.Value}");
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

    private static string? ParseParameterName(string parameter)
    {
        var normalized = Regex.Replace(
            parameter,
            "\\b(ByVal|ByRef|Optional|ParamArray)\\b",
            "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var match = IdentifierPattern.Match(normalized);
        return match.Success ? match.Value : null;
    }

    private static VbaTypeReferenceSyntax? ParseReturnTypeReference(string line)
    {
        var match = Regex.Match(
            line,
            "\\)\\s+As\\s+(?<new>New\\s+)?(?:(?<qualifier>[A-Za-z_][A-Za-z0-9_]*)\\.)?(?<type>[A-Za-z_][A-Za-z0-9_]*)\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return CreateTypeReference(match);
    }

    private static VbaTypeReferenceSyntax? ParseTypeReference(string text)
    {
        var match = Regex.Match(
            text,
            "\\bAs\\s+(?<new>New\\s+)?(?:(?<qualifier>[A-Za-z_][A-Za-z0-9_]*)\\.)?(?<type>[A-Za-z_][A-Za-z0-9_]*)\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return CreateTypeReference(match);
    }

    private static VbaTypeReferenceSyntax? CreateTypeReference(Match match)
    {
        if (!match.Success)
        {
            return null;
        }

        var qualifier = match.Groups["qualifier"].Success
            ? match.Groups["qualifier"].Value
            : null;
        return new VbaTypeReferenceSyntax(
            match.Groups["type"].Value,
            qualifier,
            match.Groups["new"].Success);
    }

    private static int FindBlockEndLine(IReadOnlyList<SourceLine> lines, int startLine, string keyword)
    {
        var pattern = new Regex(
            $"^\\s*End\\s+{Regex.Escape(keyword)}\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        for (var lineIndex = startLine; lineIndex < lines.Count; lineIndex++)
        {
            if (pattern.IsMatch(StripApostropheComment(lines[lineIndex].Text)))
            {
                return lineIndex;
            }
        }

        return lines.Count - 1;
    }

    private static VbaDeclarationVisibility GetVisibility(string visibility, bool defaultPublic)
    {
        if (visibility.Equals("Private", StringComparison.OrdinalIgnoreCase)
            || visibility.Equals("Dim", StringComparison.OrdinalIgnoreCase))
        {
            return VbaDeclarationVisibility.Private;
        }

        return defaultPublic
            ? VbaDeclarationVisibility.Public
            : VbaDeclarationVisibility.Private;
    }

    private static bool IsModuleVariableDeclaration(string codeLine)
    {
        var afterVisibility = Regex.Replace(
            codeLine.TrimStart(),
            "^(Public|Private|Friend|Dim)\\s+",
            "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return !Regex.IsMatch(
            afterVisibility,
            "^(Static\\s+)?(Sub|Function|Property)\\b|^Declare\\b|^Const\\b|^Event\\b|^Enum\\b|^Type\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsWithEventsVariableDeclaration(string codeLine)
        => Regex.IsMatch(codeLine, "\\bWithEvents\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static VbaSyntaxRange CreateRange(SourceText sourceText, Match match, string groupName, SourceLine line)
    {
        var group = match.Groups[groupName];
        return sourceText.RangeForLine(line, group.Index, group.Index + group.Length);
    }

    private static VbaSyntaxRange CreateLineRange(SourceLine line)
        => new(
            new VbaSyntaxPosition(line.LineNumber, 0, line.StartOffset),
            new VbaSyntaxPosition(line.LineNumber, line.Text.Length, line.EndOffset));

    private static VbaSyntaxRange CreateBlockRange(IReadOnlyList<SourceLine> lines, int startLine, int endLine)
        => new(
            new VbaSyntaxPosition(startLine, 0, lines[startLine].StartOffset),
            new VbaSyntaxPosition(endLine, lines[endLine].Text.Length, lines[endLine].EndOffset));

    private static string StripApostropheComment(string line)
    {
        var inString = false;
        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];
            if (current == '"' && inString && index + 1 < line.Length && line[index + 1] == '"')
            {
                index++;
                continue;
            }

            if (current == '"')
            {
                inString = !inString;
                continue;
            }

            if (!inString && current == '\'')
            {
                return line[..index];
            }
        }

        return line;
    }

    private static VbaModuleIdentitySyntax CreateIdentity(
        string uri,
        SourceText sourceText,
        VbaModuleKind kind,
        IReadOnlyList<VbaModuleAttributeSyntax> attributes)
    {
        var nameAttribute = attributes.FirstOrDefault(attribute =>
            attribute.Name.Equals("VB_Name", StringComparison.OrdinalIgnoreCase));
        if (nameAttribute is not null)
        {
            return new VbaModuleIdentitySyntax(nameAttribute.Value, nameAttribute.ValueRange);
        }

        var fallbackName = GetFileBaseName(uri);
        return new VbaModuleIdentitySyntax(
            fallbackName,
            new VbaSyntaxRange(sourceText.StartPosition, sourceText.StartPosition));
    }

    private static SourceLine? FindAttributeNameLine(SourceText sourceText)
        => sourceText.Lines.FirstOrDefault(line =>
            AttributePattern.Match(line.Text) is { Success: true } match
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

    private sealed record SourceText(
        string Text,
        IReadOnlyList<SourceLine> Lines,
        VbaSyntaxPosition StartPosition,
        VbaSyntaxRange FullRange)
    {
        public bool IsEmpty => Text.Length == 0;

        public static SourceText From(string source)
        {
            var lines = new List<SourceLine>();
            var line = 0;
            var offset = 0;
            while (offset <= source.Length)
            {
                var startOffset = offset;
                while (offset < source.Length && source[offset] is not '\r' and not '\n')
                {
                    offset++;
                }

                lines.Add(new SourceLine(line, source[startOffset..offset], startOffset, offset));
                if (offset >= source.Length)
                {
                    break;
                }

                if (source[offset] == '\r' && offset + 1 < source.Length && source[offset + 1] == '\n')
                {
                    offset += 2;
                }
                else
                {
                    offset++;
                }

                line++;
            }

            var startPosition = new VbaSyntaxPosition(0, 0, 0);
            var endPosition = PositionAt(source, source.Length);
            return new SourceText(source, lines, startPosition, new VbaSyntaxRange(startPosition, endPosition));
        }

        public VbaSyntaxPosition PositionAt(int offset)
            => PositionAt(Text, offset);

        public VbaSyntaxRange RangeForLine(SourceLine line, int startCharacter, int endCharacter)
            => new(
                new VbaSyntaxPosition(line.LineNumber, startCharacter, line.StartOffset + startCharacter),
                new VbaSyntaxPosition(line.LineNumber, endCharacter, line.StartOffset + endCharacter));

        private static VbaSyntaxPosition PositionAt(string source, int offset)
        {
            var line = 0;
            var character = 0;
            for (var index = 0; index < offset; index++)
            {
                if (source[index] == '\r')
                {
                    if (index + 1 < source.Length && source[index + 1] == '\n')
                    {
                        index++;
                    }

                    line++;
                    character = 0;
                    continue;
                }

                if (source[index] == '\n')
                {
                    line++;
                    character = 0;
                    continue;
                }

                character++;
            }

            return new VbaSyntaxPosition(line, character, offset);
        }
    }
}

internal sealed record SourceLine(
    int LineNumber,
    string Text,
    int StartOffset,
    int EndOffset);

internal sealed record ParsedMembers(
    IReadOnlyList<VbaModuleMemberSyntax> Members,
    IReadOnlyList<VbaDeclarationSyntax> Declarations,
    IReadOnlyList<VbaCallableDeclarationSyntax> CallableDeclarations);

internal sealed record DeclarationSegment(int Start, string Text);

internal sealed record DocumentationComment(
    string HoverText,
    string? Summary,
    IReadOnlyDictionary<string, string> ParameterDocs,
    string? ReturnDocumentation);
