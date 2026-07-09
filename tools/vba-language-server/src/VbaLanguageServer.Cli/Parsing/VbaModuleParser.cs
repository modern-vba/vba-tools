using System.Text.RegularExpressions;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.SourceModel;

namespace VbaLanguageServer.Parsing;

public sealed record VbaModuleSyntaxTree(
    string Uri,
    string Text,
    IReadOnlyList<string> Lines,
    VbaModuleIdentity Identity,
    IReadOnlyList<VbaSourceDeclarationSyntax> Declarations,
    IReadOnlyList<VbaCallableDeclaration> CallableDeclarations,
    int CodeStartLine);

public sealed record VbaModuleIdentity(
    string Name,
    VbaSourceDefinitionKind Kind,
    VbaRange Range);

public sealed record VbaCallableDeclaration(
    string Name,
    VbaSourceDefinitionKind Kind,
    VbaSourceDefinitionVisibility Visibility,
    VbaRange Range,
    VbaRange BlockRange,
    IReadOnlyList<VbaCallableParameterSyntax> Parameters,
    string? Documentation,
    VbaCallableSignature Signature,
    VbaTypeReference? TypeReference,
    int LineIndex,
    string OriginalLine);

public sealed record VbaCallableParameterSyntax(
    string Name,
    VbaRange Range,
    string? Documentation,
    VbaTypeReference? TypeReference);

public sealed record VbaSourceDeclarationSyntax(
    string Name,
    VbaSourceDefinitionKind Kind,
    VbaSourceDefinitionVisibility Visibility,
    VbaRange Range,
    int LineIndex,
    string? Documentation = null,
    VbaCallableSignature? Signature = null,
    string? ParentProcedureName = null,
    VbaRange? ParentProcedureRange = null,
    string? ParentTypeName = null,
    VbaTypeReference? TypeReference = null);

public static class VbaModuleParser
{
    private static readonly Regex AttributeNamePattern = new(
        "^\\s*Attribute\\s+VB_Name\\s*=\\s*\"(?<name>[^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ProcedurePattern = new(
        "^\\s*(?:(?<visibility>Public|Private|Friend)\\s+)?(?:(?<kind>Sub|Function)|Property\\s+(?<propertyKind>Get|Let|Set))\\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\\s*(?:\\((?<parameters>[^)]*)\\))?",
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
        "^\\s*(?:(?<visibility>Public|Private|Friend)\\s+)?Const\\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ModuleVariablePattern = new(
        "^\\s*(?:(?<visibility>Public|Private|Friend|Dim)\\s+)(?:WithEvents\\s+)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LocalVariablePattern = new(
        "^\\s*(?:Dim|Static)\\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex IdentifierPattern = new(
        "[A-Za-z_][A-Za-z0-9_]*",
        RegexOptions.CultureInvariant);

    public static VbaModuleSyntaxTree Parse(string uri, string text)
    {
        var lines = SplitLines(text);
        var identity = ParseModuleIdentity(uri, lines);
        var codeStartLine = GetCodeStartLine(uri, lines);
        var declarations = new List<VbaSourceDeclarationSyntax>();
        var callableDeclarations = new List<VbaCallableDeclaration>();

        for (var lineIndex = codeStartLine; lineIndex < lines.Length; lineIndex++)
        {
            var codeLine = StripApostropheComment(lines[lineIndex]);
            if (string.IsNullOrWhiteSpace(codeLine))
            {
                continue;
            }

            var eventMatch = EventPattern.Match(codeLine);
            if (eventMatch.Success)
            {
                var documentation = ParseDocumentationComment(lines, lineIndex);
                declarations.Add(CreateDeclaration(
                    eventMatch,
                    "name",
                    VbaSourceDefinitionKind.Event,
                    GetVisibility(eventMatch.Groups["visibility"].Value, defaultPublic: true),
                    lineIndex,
                    lines[lineIndex],
                    documentation: documentation?.HoverText));
                foreach (var parameter in ParseParameterSyntax(eventMatch, lineIndex, lines[lineIndex], documentation))
                {
                    declarations.Add(CreateParameterDeclaration(parameter, lineIndex));
                }

                continue;
            }

            var enumMatch = EnumPattern.Match(codeLine);
            if (enumMatch.Success)
            {
                var visibility = GetVisibility(enumMatch.Groups["visibility"].Value, defaultPublic: true);
                declarations.Add(CreateDeclaration(
                    enumMatch,
                    "name",
                    VbaSourceDefinitionKind.Enum,
                    visibility,
                    lineIndex,
                    lines[lineIndex]));
                var endLine = FindBlockEndLine(lines, lineIndex + 1, "Enum");
                AddMemberDeclarations(
                    declarations,
                    lines,
                    lineIndex + 1,
                    endLine,
                    VbaSourceDefinitionKind.EnumMember,
                    visibility);
                lineIndex = endLine;
                continue;
            }

            var typeMatch = TypePattern.Match(codeLine);
            if (typeMatch.Success)
            {
                var visibility = GetVisibility(typeMatch.Groups["visibility"].Value, defaultPublic: true);
                declarations.Add(CreateDeclaration(
                    typeMatch,
                    "name",
                    VbaSourceDefinitionKind.Type,
                    visibility,
                    lineIndex,
                    lines[lineIndex]));
                var endLine = FindBlockEndLine(lines, lineIndex + 1, "Type");
                AddMemberDeclarations(
                    declarations,
                    lines,
                    lineIndex + 1,
                    endLine,
                    VbaSourceDefinitionKind.TypeMember,
                    visibility);
                lineIndex = endLine;
                continue;
            }

            var constMatch = ConstPattern.Match(codeLine);
            if (constMatch.Success)
            {
                var documentation = ParseDocumentationComment(lines, lineIndex);
                declarations.Add(CreateDeclaration(
                    constMatch,
                    "name",
                    VbaSourceDefinitionKind.Constant,
                    GetVisibility(constMatch.Groups["visibility"].Value, defaultPublic: true),
                    lineIndex,
                    lines[lineIndex],
                    documentation: documentation?.HoverText,
                    typeReference: ParseTypeReference(lines[lineIndex])));
                continue;
            }

            var procedureMatch = ProcedurePattern.Match(codeLine);
            if (procedureMatch.Success)
            {
                var declaration = CreateCallableDeclaration(
                    procedureMatch,
                    uri,
                    lines,
                    lineIndex);
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
                    declarations,
                    lines,
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
                declarations.Add(CreateDeclaration(
                    variableMatch,
                    "name",
                    VbaSourceDefinitionKind.Variable,
                    GetVisibility(variableMatch.Groups["visibility"].Value, defaultPublic: false),
                    lineIndex,
                    lines[lineIndex],
                    typeReference: ParseTypeReference(lines[lineIndex])));
            }
        }

        return new VbaModuleSyntaxTree(
            uri,
            text,
            lines,
            identity,
            declarations,
            callableDeclarations,
            codeStartLine);
    }

    private static VbaModuleIdentity ParseModuleIdentity(string uri, IReadOnlyList<string> lines)
    {
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var match = AttributeNamePattern.Match(lines[lineIndex]);
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups["name"].Value;
            return new VbaModuleIdentity(
                name,
                GetModuleKind(uri),
                CreateRange(match, "name", lineIndex, lines[lineIndex]));
        }

        var fallbackName = GetFileBaseName(uri);
        return new VbaModuleIdentity(
            fallbackName,
            GetModuleKind(uri),
            new VbaRange(new VbaPosition(0, 0), new VbaPosition(0, fallbackName.Length)));
    }

    private static VbaCallableDeclaration CreateCallableDeclaration(
        Match match,
        string uri,
        IReadOnlyList<string> lines,
        int lineIndex)
    {
        var name = match.Groups["name"].Value;
        var documentation = ParseDocumentationComment(lines, lineIndex);
        var parameters = ParseCallableParameters(match, lineIndex, lines[lineIndex], documentation);
        var signature = CreateSignature(name, parameters, lines[lineIndex], documentation);
        var typeReference = ParseReturnTypeReference(lines[lineIndex]);
        var kind = match.Groups["kind"].Success
            ? VbaSourceDefinitionKind.Procedure
            : VbaSourceDefinitionKind.Property;
        var endKeyword = kind == VbaSourceDefinitionKind.Property ? "Property" : match.Groups["kind"].Value;
        var endLine = FindBlockEndLine(lines, lineIndex + 1, endKeyword);

        return new VbaCallableDeclaration(
            name,
            kind,
            GetVisibility(match.Groups["visibility"].Value, defaultPublic: true),
            CreateRange(match, "name", lineIndex, lines[lineIndex]),
            new VbaRange(
                new VbaPosition(lineIndex, 0),
                new VbaPosition(endLine, lines[endLine].Length)),
            parameters,
            documentation?.HoverText,
            signature,
            typeReference,
            lineIndex,
            lines[lineIndex]);
    }

    private static VbaSourceDeclarationSyntax CreateCallableSourceDeclaration(VbaCallableDeclaration declaration)
        => new(
            declaration.Name,
            declaration.Kind,
            declaration.Visibility,
            declaration.Range,
            declaration.LineIndex,
            Documentation: declaration.Documentation,
            Signature: declaration.Signature,
            TypeReference: declaration.TypeReference);

    private static VbaSourceDeclarationSyntax CreateParameterDeclaration(
        VbaCallableParameterSyntax parameter,
        int lineIndex,
        string? parentProcedureName = null,
        VbaRange? parentProcedureRange = null)
        => new(
            parameter.Name,
            VbaSourceDefinitionKind.Parameter,
            VbaSourceDefinitionVisibility.Local,
            parameter.Range,
            lineIndex,
            Documentation: parameter.Documentation,
            ParentProcedureName: parentProcedureName,
            ParentProcedureRange: parentProcedureRange,
            TypeReference: parameter.TypeReference);

    private static VbaSourceDeclarationSyntax CreateDeclaration(
        Match match,
        string groupName,
        VbaSourceDefinitionKind kind,
        VbaSourceDefinitionVisibility visibility,
        int lineIndex,
        string originalLine,
        string? documentation = null,
        VbaCallableSignature? signature = null,
        string? parentProcedureName = null,
        VbaRange? parentProcedureRange = null,
        string? parentTypeName = null,
        VbaTypeReference? typeReference = null)
    {
        var name = match.Groups[groupName].Value;
        return new VbaSourceDeclarationSyntax(
            name,
            kind,
            visibility,
            CreateRange(match, groupName, lineIndex, originalLine),
            lineIndex,
            Documentation: documentation,
            Signature: signature,
            ParentProcedureName: parentProcedureName,
            ParentProcedureRange: parentProcedureRange,
            ParentTypeName: parentTypeName,
            TypeReference: typeReference);
    }

    private static void AddMemberDeclarations(
        ICollection<VbaSourceDeclarationSyntax> declarations,
        IReadOnlyList<string> lines,
        int startLine,
        int endLine,
        VbaSourceDefinitionKind kind,
        VbaSourceDefinitionVisibility visibility)
    {
        for (var lineIndex = startLine; lineIndex < endLine; lineIndex++)
        {
            var codeLine = StripApostropheComment(lines[lineIndex]);
            var match = IdentifierPattern.Match(codeLine);
            if (!match.Success)
            {
                continue;
            }

            declarations.Add(new VbaSourceDeclarationSyntax(
                match.Value,
                kind,
                visibility,
                CreateLineRange(lines[lineIndex], lineIndex, match.Value),
                lineIndex,
                TypeReference: ParseTypeReference(lines[lineIndex])));
        }
    }

    private static void AddLocalVariableDeclarations(
        ICollection<VbaSourceDeclarationSyntax> declarations,
        IReadOnlyList<string> lines,
        int startLine,
        int endLine,
        string parentProcedureName,
        VbaRange parentProcedureRange)
    {
        for (var lineIndex = startLine; lineIndex < endLine; lineIndex++)
        {
            var codeLine = StripApostropheComment(lines[lineIndex]);
            var match = LocalVariablePattern.Match(codeLine);
            if (!match.Success)
            {
                continue;
            }

            declarations.Add(CreateDeclaration(
                match,
                "name",
                VbaSourceDefinitionKind.Variable,
                VbaSourceDefinitionVisibility.Local,
                lineIndex,
                lines[lineIndex],
                parentProcedureName: parentProcedureName,
                parentProcedureRange: parentProcedureRange,
                typeReference: ParseTypeReference(lines[lineIndex])));
        }
    }

    private static IReadOnlyList<VbaCallableParameterSyntax> ParseCallableParameters(
        Match match,
        int lineIndex,
        string line,
        DocumentationComment? documentation)
        => ParseParameterSyntax(match, lineIndex, line, documentation);

    private static IReadOnlyList<VbaCallableParameterSyntax> ParseParameterSyntax(
        Match match,
        int lineIndex,
        string line,
        DocumentationComment? documentation)
    {
        var parametersGroup = match.Groups["parameters"];
        if (!parametersGroup.Success || string.IsNullOrWhiteSpace(parametersGroup.Value))
        {
            return [];
        }

        var parametersStart = line.IndexOf(parametersGroup.Value, StringComparison.Ordinal);
        if (parametersStart < 0)
        {
            return [];
        }

        var parameters = new List<VbaCallableParameterSyntax>();
        var segmentStart = 0;
        foreach (var segment in parametersGroup.Value.Split(','))
        {
            var name = ParseParameterName(segment);
            if (name is not null)
            {
                var nameOffset = segment.IndexOf(name, StringComparison.Ordinal);
                var start = parametersStart + segmentStart + nameOffset;
                parameters.Add(new VbaCallableParameterSyntax(
                    name,
                    new VbaRange(
                        new VbaPosition(lineIndex, start),
                        new VbaPosition(lineIndex, start + name.Length)),
                    documentation?.ParameterDocs.TryGetValue(name, out var parameterDocumentation) == true
                        ? parameterDocumentation
                        : null,
                    ParseTypeReference(segment)));
            }

            segmentStart += segment.Length + 1;
        }

        return parameters;
    }

    private static VbaCallableSignature CreateSignature(
        string name,
        IReadOnlyList<VbaCallableParameterSyntax> parameters,
        string line,
        DocumentationComment? documentation)
    {
        var returnTypeName = ParseReturnTypeName(line);
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

        return new VbaCallableSignature(
            label,
            parameters
                .Select(parameter => new VbaCallableParameter(parameter.Name, parameter.Documentation))
                .ToArray(),
            documentationLines.Count == 0 ? null : string.Join('\n', documentationLines));
    }

    private static VbaRange CreateRange(Match match, string groupName, int lineIndex, string originalLine)
    {
        var name = match.Groups[groupName].Value;
        var start = originalLine.IndexOf(name, StringComparison.Ordinal);
        if (start < 0)
        {
            start = match.Groups[groupName].Index;
        }

        return new VbaRange(
            new VbaPosition(lineIndex, start),
            new VbaPosition(lineIndex, start + name.Length));
    }

    private static VbaRange CreateLineRange(string line, int lineIndex, string name)
    {
        var start = line.IndexOf(name, StringComparison.Ordinal);
        return new VbaRange(
            new VbaPosition(lineIndex, start),
            new VbaPosition(lineIndex, start + name.Length));
    }

    private static DocumentationComment? ParseDocumentationComment(IReadOnlyList<string> lines, int declarationLine)
    {
        var rawLines = new Stack<string>();
        for (var lineIndex = declarationLine - 1; lineIndex >= 0; lineIndex--)
        {
            var trimmed = lines[lineIndex].TrimStart();
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

    private static string? ParseReturnTypeName(string line)
        => ParseReturnTypeReference(line)?.Name;

    private static VbaTypeReference? ParseReturnTypeReference(string line)
    {
        var match = Regex.Match(
            line,
            "\\)\\s+As\\s+(?:(?<qualifier>[A-Za-z_][A-Za-z0-9_]*)\\.)?(?<type>[A-Za-z_][A-Za-z0-9_]*)\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return CreateTypeReference(match);
    }

    private static VbaTypeReference? ParseTypeReference(string text)
    {
        var match = Regex.Match(
            text,
            "\\bAs\\s+(?:(?<qualifier>[A-Za-z_][A-Za-z0-9_]*)\\.)?(?<type>[A-Za-z_][A-Za-z0-9_]*)\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return CreateTypeReference(match);
    }

    private static VbaTypeReference? CreateTypeReference(Match match)
    {
        if (!match.Success)
        {
            return null;
        }

        var qualifier = match.Groups["qualifier"].Success
            ? match.Groups["qualifier"].Value
            : null;
        return new VbaTypeReference(match.Groups["type"].Value, qualifier);
    }

    private static int FindBlockEndLine(IReadOnlyList<string> lines, int startLine, string keyword)
    {
        var pattern = new Regex(
            $"^\\s*End\\s+{Regex.Escape(keyword)}\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        for (var lineIndex = startLine; lineIndex < lines.Count; lineIndex++)
        {
            if (pattern.IsMatch(StripApostropheComment(lines[lineIndex])))
            {
                return lineIndex;
            }
        }

        return lines.Count - 1;
    }

    private static VbaSourceDefinitionVisibility GetVisibility(string visibility, bool defaultPublic)
    {
        if (visibility.Equals("Private", StringComparison.OrdinalIgnoreCase))
        {
            return VbaSourceDefinitionVisibility.Private;
        }

        if (visibility.Equals("Dim", StringComparison.OrdinalIgnoreCase))
        {
            return VbaSourceDefinitionVisibility.Private;
        }

        return defaultPublic
            ? VbaSourceDefinitionVisibility.Public
            : VbaSourceDefinitionVisibility.Private;
    }

    private static bool IsModuleVariableDeclaration(string codeLine)
        => Regex.IsMatch(codeLine, "\\bAs\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static VbaSourceDefinitionKind GetModuleKind(string uri)
    {
        if (uri.EndsWith(".cls", StringComparison.OrdinalIgnoreCase))
        {
            return VbaSourceDefinitionKind.Class;
        }

        if (uri.EndsWith(".frm", StringComparison.OrdinalIgnoreCase))
        {
            return VbaSourceDefinitionKind.Form;
        }

        return VbaSourceDefinitionKind.Module;
    }

    private static int GetCodeStartLine(string uri, IReadOnlyList<string> lines)
    {
        if (!uri.EndsWith(".frm", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            if (lines[lineIndex].TrimStart().StartsWith("Attribute VB_Name", StringComparison.OrdinalIgnoreCase))
            {
                return lineIndex;
            }
        }

        return 0;
    }

    private static string[] SplitLines(string source)
        => source.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

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

    private sealed record DocumentationComment(
        string HoverText,
        string? Summary,
        IReadOnlyDictionary<string, string> ParameterDocs,
        string? ReturnDocumentation);
}
