using System.Text.RegularExpressions;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.SourceModel;

namespace VbaLanguageServer.Parsing;

public sealed record VbaModuleSyntaxTree(
    string Uri,
    string Text,
    IReadOnlyList<string> Lines,
    VbaModuleIdentity Identity,
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
    int LineIndex,
    string OriginalLine);

public sealed record VbaCallableParameterSyntax(
    string Name,
    VbaRange Range,
    string? Documentation);

public static class VbaModuleParser
{
    private static readonly Regex AttributeNamePattern = new(
        "^\\s*Attribute\\s+VB_Name\\s*=\\s*\"(?<name>[^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ProcedurePattern = new(
        "^\\s*(?:(?<visibility>Public|Private|Friend)\\s+)?(?:(?<kind>Sub|Function)|Property\\s+(?<propertyKind>Get|Let|Set))\\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\\s*(?:\\((?<parameters>[^)]*)\\))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex IdentifierPattern = new(
        "[A-Za-z_][A-Za-z0-9_]*",
        RegexOptions.CultureInvariant);

    public static VbaModuleSyntaxTree Parse(string uri, string text)
    {
        var lines = SplitLines(text);
        var identity = ParseModuleIdentity(uri, lines);
        var codeStartLine = GetCodeStartLine(uri, lines);
        var callableDeclarations = new List<VbaCallableDeclaration>();

        for (var lineIndex = codeStartLine; lineIndex < lines.Length; lineIndex++)
        {
            var codeLine = StripApostropheComment(lines[lineIndex]);
            if (string.IsNullOrWhiteSpace(codeLine))
            {
                continue;
            }

            var procedureMatch = ProcedurePattern.Match(codeLine);
            if (!procedureMatch.Success)
            {
                continue;
            }

            var declaration = CreateCallableDeclaration(
                procedureMatch,
                uri,
                lines,
                lineIndex);
            callableDeclarations.Add(declaration);
            lineIndex = declaration.BlockRange.End.Line;
        }

        return new VbaModuleSyntaxTree(
            uri,
            text,
            lines,
            identity,
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
            lineIndex,
            lines[lineIndex]);
    }

    private static IReadOnlyList<VbaCallableParameterSyntax> ParseCallableParameters(
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
                        : null));
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
    {
        var match = Regex.Match(
            line,
            "\\)\\s+As\\s+(?<type>[A-Za-z_][A-Za-z0-9_.]*)\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["type"].Value : null;
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

        return defaultPublic
            ? VbaSourceDefinitionVisibility.Public
            : VbaSourceDefinitionVisibility.Private;
    }

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
