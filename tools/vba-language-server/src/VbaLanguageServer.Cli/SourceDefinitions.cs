using System.Text.RegularExpressions;
using VbaLanguageServer.Diagnostics;

namespace VbaLanguageServer.SourceModel;

public enum VbaSourceDefinitionKind
{
    Module,
    Class,
    Form,
    Procedure,
    Property,
    Constant,
    Variable,
    Parameter,
    Enum,
    EnumMember,
    Type,
    TypeMember,
    Event
}

public enum VbaSourceDefinitionVisibility
{
    Public,
    Private,
    Local
}

public sealed record VbaSourceDefinition(
    string Name,
    VbaSourceDefinitionKind Kind,
    VbaSourceDefinitionVisibility Visibility,
    string Uri,
    string ModuleName,
    VbaRange Range,
    string? ParentProcedureName = null,
    VbaRange? ParentProcedureRange = null);

public sealed record VbaSourceDocument(
    string Uri,
    string Text,
    string ModuleName,
    IReadOnlyList<VbaSourceDefinition> Definitions);

public sealed record VbaDefinitionLocation(string Uri, VbaRange Range);

public sealed class VbaSourceIndex
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

    private readonly IReadOnlyList<VbaSourceDocument> documents;

    private VbaSourceIndex(IReadOnlyList<VbaSourceDocument> documents)
    {
        this.documents = documents;
    }

    public static VbaSourceIndex Build(IReadOnlyDictionary<string, string> sourceDocuments)
    {
        var parsedDocuments = sourceDocuments
            .Select(entry => ParseDocument(entry.Key, entry.Value))
            .ToArray();
        return new VbaSourceIndex(parsedDocuments);
    }

    public IReadOnlyList<VbaSourceDefinition> GetDocumentDefinitions(string uri)
        => documents
            .FirstOrDefault(document => SameUri(document.Uri, uri))
            ?.Definitions
            ?? Array.Empty<VbaSourceDefinition>();

    public VbaDefinitionLocation? ResolveDefinition(string uri, int line, int character)
    {
        var currentDocument = documents.FirstOrDefault(document => SameUri(document.Uri, uri));
        if (currentDocument is null)
        {
            return null;
        }

        var lines = SplitLines(currentDocument.Text);
        if (line < 0 || line >= lines.Length)
        {
            return null;
        }

        var identifier = GetIdentifierAt(lines[line], character);
        if (identifier is null)
        {
            return null;
        }

        var qualifier = GetQualifierBefore(lines[line], identifier.Start);
        var definition = qualifier is null
            ? ResolveUnqualified(currentDocument, new VbaPosition(line, character), identifier.Name)
            : ResolveQualified(currentDocument, qualifier, identifier.Name);

        return definition is null ? null : new VbaDefinitionLocation(definition.Uri, definition.Range);
    }

    private VbaSourceDefinition? ResolveUnqualified(
        VbaSourceDocument currentDocument,
        VbaPosition position,
        string identifier)
    {
        var localDefinition = currentDocument.Definitions
            .Where(definition => definition.Visibility == VbaSourceDefinitionVisibility.Local)
            .Where(definition => ContainsPosition(definition, position))
            .FirstOrDefault(definition => SameName(definition.Name, identifier));
        if (localDefinition is not null)
        {
            return localDefinition;
        }

        var currentModuleMatches = currentDocument.Definitions
            .Where(IsReferenceTarget)
            .Where(definition => SameName(definition.Name, identifier))
            .ToArray();
        if (currentModuleMatches.Length == 1)
        {
            return currentModuleMatches[0];
        }

        if (currentModuleMatches.Length > 1)
        {
            return null;
        }

        var projectMatches = documents
            .Where(document => !SameUri(document.Uri, currentDocument.Uri))
            .SelectMany(document => document.Definitions)
            .Where(IsReferenceTarget)
            .Where(definition => definition.Visibility == VbaSourceDefinitionVisibility.Public)
            .Where(definition => SameName(definition.Name, identifier))
            .ToArray();
        return projectMatches.Length == 1 ? projectMatches[0] : null;
    }

    private VbaSourceDefinition? ResolveQualified(
        VbaSourceDocument currentDocument,
        string qualifier,
        string memberName)
    {
        var qualifiedDocument = documents.FirstOrDefault(document => SameName(document.ModuleName, qualifier));
        if (qualifiedDocument is null)
        {
            return null;
        }

        var allowPrivate = SameUri(currentDocument.Uri, qualifiedDocument.Uri);
        var matches = qualifiedDocument.Definitions
            .Where(IsReferenceTarget)
            .Where(definition => allowPrivate || definition.Visibility == VbaSourceDefinitionVisibility.Public)
            .Where(definition => SameName(definition.Name, memberName))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool ContainsPosition(VbaSourceDefinition definition, VbaPosition position)
    {
        if (definition.ParentProcedureRange is null)
        {
            return false;
        }

        return ComparePosition(definition.ParentProcedureRange.Start, position) <= 0
            && ComparePosition(position, definition.ParentProcedureRange.End) <= 0;
    }

    private static VbaSourceDocument ParseDocument(string uri, string text)
    {
        var lines = SplitLines(text);
        var definitions = new List<VbaSourceDefinition>();
        var moduleDefinition = ParseModuleDefinition(uri, lines);
        definitions.Add(moduleDefinition);

        var startLine = GetCodeStartLine(uri, lines);
        for (var lineIndex = startLine; lineIndex < lines.Length; lineIndex++)
        {
            var codeLine = StripApostropheComment(lines[lineIndex]);
            if (string.IsNullOrWhiteSpace(codeLine))
            {
                continue;
            }

            var eventMatch = EventPattern.Match(codeLine);
            if (eventMatch.Success)
            {
                definitions.Add(CreateDefinition(
                    eventMatch,
                    "name",
                    uri,
                    moduleDefinition.Name,
                    VbaSourceDefinitionKind.Event,
                    GetVisibility(eventMatch.Groups["visibility"].Value, defaultPublic: true),
                    lineIndex,
                    lines[lineIndex]));
                AddParameters(definitions, eventMatch, uri, moduleDefinition.Name, lineIndex, lines[lineIndex], null);
                continue;
            }

            var enumMatch = EnumPattern.Match(codeLine);
            if (enumMatch.Success)
            {
                var visibility = GetVisibility(enumMatch.Groups["visibility"].Value, defaultPublic: true);
                definitions.Add(CreateDefinition(
                    enumMatch,
                    "name",
                    uri,
                    moduleDefinition.Name,
                    VbaSourceDefinitionKind.Enum,
                    visibility,
                    lineIndex,
                    lines[lineIndex]));

                var endLine = FindBlockEndLine(lines, lineIndex + 1, "Enum");
                AddEnumMembers(definitions, uri, moduleDefinition.Name, lines, lineIndex + 1, endLine, visibility);
                lineIndex = endLine;
                continue;
            }

            var typeMatch = TypePattern.Match(codeLine);
            if (typeMatch.Success)
            {
                var visibility = GetVisibility(typeMatch.Groups["visibility"].Value, defaultPublic: true);
                definitions.Add(CreateDefinition(
                    typeMatch,
                    "name",
                    uri,
                    moduleDefinition.Name,
                    VbaSourceDefinitionKind.Type,
                    visibility,
                    lineIndex,
                    lines[lineIndex]));

                var endLine = FindBlockEndLine(lines, lineIndex + 1, "Type");
                AddTypeMembers(definitions, uri, moduleDefinition.Name, lines, lineIndex + 1, endLine, visibility);
                lineIndex = endLine;
                continue;
            }

            var constMatch = ConstPattern.Match(codeLine);
            if (constMatch.Success)
            {
                definitions.Add(CreateDefinition(
                    constMatch,
                    "name",
                    uri,
                    moduleDefinition.Name,
                    VbaSourceDefinitionKind.Constant,
                    GetVisibility(constMatch.Groups["visibility"].Value, defaultPublic: true),
                    lineIndex,
                    lines[lineIndex]));
                continue;
            }

            var procedureMatch = ProcedurePattern.Match(codeLine);
            if (procedureMatch.Success)
            {
                var procedureKind = procedureMatch.Groups["kind"].Success
                    ? VbaSourceDefinitionKind.Procedure
                    : VbaSourceDefinitionKind.Property;
                var procedureDefinition = CreateDefinition(
                    procedureMatch,
                    "name",
                    uri,
                    moduleDefinition.Name,
                    procedureKind,
                    GetVisibility(procedureMatch.Groups["visibility"].Value, defaultPublic: true),
                    lineIndex,
                    lines[lineIndex]);
                definitions.Add(procedureDefinition);
                var endKeyword = procedureKind == VbaSourceDefinitionKind.Property
                    ? "Property"
                    : procedureMatch.Groups["kind"].Value;
                var endLine = FindBlockEndLine(lines, lineIndex + 1, endKeyword);
                var procedureRange = new VbaRange(
                    new VbaPosition(lineIndex, 0),
                    new VbaPosition(endLine, lines[endLine].Length));
                AddParameters(
                    definitions,
                    procedureMatch,
                    uri,
                    moduleDefinition.Name,
                    lineIndex,
                    lines[lineIndex],
                    procedureDefinition.Name,
                    procedureRange);
                AddProcedureLocals(
                    definitions,
                    uri,
                    moduleDefinition.Name,
                    procedureDefinition.Name,
                    procedureRange,
                    lines,
                    lineIndex + 1,
                    endLine);
                lineIndex = endLine;
                continue;
            }

            var variableMatch = ModuleVariablePattern.Match(codeLine);
            if (variableMatch.Success && IsModuleVariableDeclaration(codeLine))
            {
                definitions.Add(CreateDefinition(
                    variableMatch,
                    "name",
                    uri,
                    moduleDefinition.Name,
                    VbaSourceDefinitionKind.Variable,
                    GetVisibility(variableMatch.Groups["visibility"].Value, defaultPublic: false),
                    lineIndex,
                    lines[lineIndex]));
            }
        }

        return new VbaSourceDocument(uri, text, moduleDefinition.Name, definitions);
    }

    private static VbaSourceDefinition ParseModuleDefinition(string uri, string[] lines)
    {
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var match = AttributeNamePattern.Match(lines[lineIndex]);
            if (!match.Success)
            {
                continue;
            }

            return CreateDefinition(
                match,
                "name",
                uri,
                match.Groups["name"].Value,
                GetModuleKind(uri),
                VbaSourceDefinitionVisibility.Public,
                lineIndex,
                lines[lineIndex]);
        }

        var fallbackName = GetFileBaseName(uri);
        return new VbaSourceDefinition(
            fallbackName,
            GetModuleKind(uri),
            VbaSourceDefinitionVisibility.Public,
            uri,
            fallbackName,
            new VbaRange(new VbaPosition(0, 0), new VbaPosition(0, fallbackName.Length)));
    }

    private static VbaSourceDefinition CreateDefinition(
        Match match,
        string groupName,
        string uri,
        string moduleName,
        VbaSourceDefinitionKind kind,
        VbaSourceDefinitionVisibility visibility,
        int line,
        string originalLine,
        string? parentProcedureName = null,
        VbaRange? parentProcedureRange = null)
    {
        var name = match.Groups[groupName].Value;
        var start = originalLine.IndexOf(name, StringComparison.Ordinal);
        if (start < 0)
        {
            start = match.Groups[groupName].Index;
        }

        return new VbaSourceDefinition(
            name,
            kind,
            visibility,
            uri,
            moduleName,
            new VbaRange(new VbaPosition(line, start), new VbaPosition(line, start + name.Length)),
            parentProcedureName,
            parentProcedureRange);
    }

    private static void AddParameters(
        ICollection<VbaSourceDefinition> definitions,
        Match match,
        string uri,
        string moduleName,
        int lineIndex,
        string line,
        string? procedureName,
        VbaRange? procedureRange = null)
    {
        var parametersGroup = match.Groups["parameters"];
        if (!parametersGroup.Success || string.IsNullOrWhiteSpace(parametersGroup.Value))
        {
            return;
        }

        foreach (var parameter in parametersGroup.Value.Split(','))
        {
            var parameterName = ParseParameterName(parameter);
            if (parameterName is null)
            {
                continue;
            }

            var searchStart = Math.Max(0, parametersGroup.Index);
            var start = line.IndexOf(parameterName, searchStart, StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            definitions.Add(new VbaSourceDefinition(
                parameterName,
                VbaSourceDefinitionKind.Parameter,
                VbaSourceDefinitionVisibility.Local,
                uri,
                moduleName,
                new VbaRange(new VbaPosition(lineIndex, start), new VbaPosition(lineIndex, start + parameterName.Length)),
                procedureName,
                procedureRange));
        }
    }

    private static void AddEnumMembers(
        ICollection<VbaSourceDefinition> definitions,
        string uri,
        string moduleName,
        string[] lines,
        int startLine,
        int endLine,
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

            AddLineDefinition(definitions, uri, moduleName, lines[lineIndex], lineIndex, match.Value, VbaSourceDefinitionKind.EnumMember, visibility);
        }
    }

    private static void AddTypeMembers(
        ICollection<VbaSourceDefinition> definitions,
        string uri,
        string moduleName,
        string[] lines,
        int startLine,
        int endLine,
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

            AddLineDefinition(definitions, uri, moduleName, lines[lineIndex], lineIndex, match.Value, VbaSourceDefinitionKind.TypeMember, visibility);
        }
    }

    private static void AddProcedureLocals(
        ICollection<VbaSourceDefinition> definitions,
        string uri,
        string moduleName,
        string procedureName,
        VbaRange procedureRange,
        string[] lines,
        int startLine,
        int endLine)
    {
        for (var lineIndex = startLine; lineIndex < endLine; lineIndex++)
        {
            var codeLine = StripApostropheComment(lines[lineIndex]);
            var match = LocalVariablePattern.Match(codeLine);
            if (!match.Success)
            {
                continue;
            }

            definitions.Add(CreateDefinition(
                match,
                "name",
                uri,
                moduleName,
                VbaSourceDefinitionKind.Variable,
                VbaSourceDefinitionVisibility.Local,
                lineIndex,
                lines[lineIndex],
                procedureName,
                procedureRange));
        }
    }

    private static void AddLineDefinition(
        ICollection<VbaSourceDefinition> definitions,
        string uri,
        string moduleName,
        string line,
        int lineIndex,
        string name,
        VbaSourceDefinitionKind kind,
        VbaSourceDefinitionVisibility visibility)
    {
        var start = line.IndexOf(name, StringComparison.Ordinal);
        definitions.Add(new VbaSourceDefinition(
            name,
            kind,
            visibility,
            uri,
            moduleName,
            new VbaRange(new VbaPosition(lineIndex, start), new VbaPosition(lineIndex, start + name.Length))));
    }

    private static bool IsReferenceTarget(VbaSourceDefinition definition)
        => definition.Visibility != VbaSourceDefinitionVisibility.Local
            && definition.Kind != VbaSourceDefinitionKind.Module
            && definition.Kind != VbaSourceDefinitionKind.Class
            && definition.Kind != VbaSourceDefinitionKind.Form;

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

    private static int FindBlockEndLine(string[] lines, int startLine, string keyword)
    {
        var pattern = new Regex(
            $"^\\s*End\\s+{Regex.Escape(keyword)}\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        for (var lineIndex = startLine; lineIndex < lines.Length; lineIndex++)
        {
            if (pattern.IsMatch(StripApostropheComment(lines[lineIndex])))
            {
                return lineIndex;
            }
        }

        return lines.Length - 1;
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

    private static int GetCodeStartLine(string uri, string[] lines)
    {
        if (!uri.EndsWith(".frm", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var attributeIndex = Array.FindIndex(lines, line =>
            line.TrimStart().StartsWith("Attribute VB_Name", StringComparison.OrdinalIgnoreCase));
        return attributeIndex < 0 ? 0 : attributeIndex;
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

    private static IdentifierAtPosition? GetIdentifierAt(string line, int character)
    {
        if (line.Length == 0)
        {
            return null;
        }

        var clamped = Math.Clamp(character, 0, line.Length - 1);
        if (!IsIdentifierCharacter(line[clamped]) && clamped > 0 && IsIdentifierCharacter(line[clamped - 1]))
        {
            clamped--;
        }

        if (!IsIdentifierCharacter(line[clamped]))
        {
            return null;
        }

        var start = clamped;
        while (start > 0 && IsIdentifierCharacter(line[start - 1]))
        {
            start--;
        }

        var end = clamped + 1;
        while (end < line.Length && IsIdentifierCharacter(line[end]))
        {
            end++;
        }

        return new IdentifierAtPosition(line[start..end], start, end);
    }

    private static string? GetQualifierBefore(string line, int identifierStart)
    {
        var dotIndex = identifierStart - 1;
        while (dotIndex >= 0 && char.IsWhiteSpace(line[dotIndex]))
        {
            dotIndex--;
        }

        if (dotIndex < 0 || line[dotIndex] != '.')
        {
            return null;
        }

        var qualifierEnd = dotIndex - 1;
        while (qualifierEnd >= 0 && char.IsWhiteSpace(line[qualifierEnd]))
        {
            qualifierEnd--;
        }

        if (qualifierEnd < 0 || !IsIdentifierCharacter(line[qualifierEnd]))
        {
            return null;
        }

        var qualifierStart = qualifierEnd;
        while (qualifierStart > 0 && IsIdentifierCharacter(line[qualifierStart - 1]))
        {
            qualifierStart--;
        }

        return line[qualifierStart..(qualifierEnd + 1)];
    }

    private static bool IsIdentifierCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value == '_';

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

    private static bool SameUri(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool SameName(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static int ComparePosition(VbaPosition left, VbaPosition right)
    {
        var lineComparison = left.Line.CompareTo(right.Line);
        return lineComparison != 0 ? lineComparison : left.Character.CompareTo(right.Character);
    }

    private sealed record IdentifierAtPosition(string Name, int Start, int End);
}
