using System.Text.RegularExpressions;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Parsing;
using VbaLanguageServer.ProjectModel;

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
    VbaRange? ParentProcedureRange = null,
    string? Documentation = null,
    VbaCallableSignature? Signature = null);

public sealed record VbaCallableParameter(string Name, string? Documentation = null);

public sealed record VbaCallableSignature(
    string Label,
    IReadOnlyList<VbaCallableParameter> Parameters,
    string? Documentation = null);

public sealed record VbaSignatureHelp(VbaCallableSignature Signature, int ActiveParameter);

public sealed record VbaSourceDocument(
    string Uri,
    string Text,
    string ModuleName,
    IReadOnlyList<VbaSourceDefinition> Definitions);

public sealed record VbaDefinitionLocation(string Uri, VbaRange Range);

public sealed record VbaTextEdit(VbaRange Range, string NewText);

public sealed class VbaSourceIndex
{
    private static readonly IReadOnlyDictionary<string, string> LanguageKeywords =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["as"] = "As",
            ["byref"] = "ByRef",
            ["byval"] = "ByVal",
            ["const"] = "Const",
            ["dim"] = "Dim",
            ["else"] = "Else",
            ["elseif"] = "ElseIf",
            ["end"] = "End",
            ["enum"] = "Enum",
            ["explicit"] = "Explicit",
            ["false"] = "False",
            ["for"] = "For",
            ["function"] = "Function",
            ["if"] = "If",
            ["next"] = "Next",
            ["nothing"] = "Nothing",
            ["option"] = "Option",
            ["private"] = "Private",
            ["property"] = "Property",
            ["public"] = "Public",
            ["set"] = "Set",
            ["string"] = "String",
            ["sub"] = "Sub",
            ["then"] = "Then",
            ["true"] = "True",
            ["type"] = "Type",
            ["while"] = "While",
            ["with"] = "With"
        };

    public static readonly IReadOnlyList<string> LanguageVocabulary = [
        "As",
        "ByRef",
        "ByVal",
        "Const",
        "Dim",
        "Else",
        "ElseIf",
        "End",
        "Enum",
        "Explicit",
        "False",
        "For",
        "Function",
        "If",
        "Next",
        "Nothing",
        "Option",
        "Private",
        "Property",
        "Public",
        "Set",
        "String",
        "Sub",
        "Then",
        "True",
        "Type",
        "While",
        "With"
    ];

    private readonly IReadOnlyList<VbaSourceDocument> documents;
    private readonly VbaProjectReferenceSelection? referenceSelection;
    private readonly VbaProjectReferenceCatalogSet referenceCatalogs;

    private VbaSourceIndex(
        IReadOnlyList<VbaSourceDocument> documents,
        VbaProjectReferenceSelection? referenceSelection,
        VbaProjectReferenceCatalogSet referenceCatalogs)
    {
        this.documents = documents;
        this.referenceSelection = referenceSelection;
        this.referenceCatalogs = referenceCatalogs;
    }

    public static VbaSourceIndex Build(
        IReadOnlyDictionary<string, string> sourceDocuments,
        VbaProjectReferenceSelection? referenceSelection = null,
        VbaProjectReferenceCatalogSet? referenceCatalogs = null)
    {
        var parsedDocuments = sourceDocuments
            .Select(entry => ParseDocument(entry.Key, entry.Value))
            .ToArray();
        return new VbaSourceIndex(
            parsedDocuments,
            referenceSelection,
            referenceCatalogs ?? VbaProjectReferenceCatalogSet.Empty);
    }

    public IReadOnlyList<VbaSourceDefinition> GetDocumentDefinitions(string uri)
        => documents
            .FirstOrDefault(document => SameUri(document.Uri, uri))
            ?.Definitions
            ?? Array.Empty<VbaSourceDefinition>();

    public IReadOnlyList<VbaSourceDefinition> GetCompletionDefinitions(string uri, int line, int character)
    {
        var currentDocument = documents.FirstOrDefault(document => SameUri(document.Uri, uri));
        if (currentDocument is null)
        {
            return Array.Empty<VbaSourceDefinition>();
        }

        var position = new VbaPosition(line, character);
        var sourceDefinitions = currentDocument.Definitions
            .Where(definition =>
                IsReferenceTarget(definition)
                || (definition.Visibility == VbaSourceDefinitionVisibility.Local && ContainsPosition(definition, position)))
            .Concat(documents
                .Where(document => !SameUri(document.Uri, currentDocument.Uri))
                .SelectMany(document => document.Definitions)
                .Where(IsReferenceTarget)
                .Where(definition => definition.Visibility == VbaSourceDefinitionVisibility.Public))
            .GroupBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var referenceDefinitions = referenceSelection is null
            ? Array.Empty<VbaSourceDefinition>()
            : referenceCatalogs.GetCompletionDefinitions(referenceSelection);
        return sourceDefinitions
            .Concat(referenceDefinitions)
            .GroupBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public VbaDefinitionLocation? ResolveDefinition(string uri, int line, int character)
    {
        var definition = ResolveSourceDefinition(uri, line, character);
        return definition is null ? null : new VbaDefinitionLocation(definition.Uri, definition.Range);
    }

    public VbaSourceDefinition? ResolveSourceDefinition(string uri, int line, int character)
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

        if (!IsCodePosition(lines[line], character))
        {
            return null;
        }

        var identifier = GetIdentifierAt(lines[line], character);
        if (identifier is null)
        {
            return null;
        }

        var qualifier = GetQualifierBefore(lines[line], identifier.Start);
        var sourceDefinition = qualifier is null
            ? ResolveUnqualified(currentDocument, new VbaPosition(line, character), identifier.Name)
            : ResolveQualified(currentDocument, qualifier, identifier.Name);

        if (sourceDefinition is not null || referenceSelection is null)
        {
            return sourceDefinition;
        }

        return qualifier is null
            ? referenceCatalogs.ResolveUnqualified(referenceSelection, identifier.Name)
            : referenceCatalogs.ResolveQualified(referenceSelection, qualifier, identifier.Name);
    }

    public VbaSignatureHelp? GetSignatureHelp(string uri, int line, int character)
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

        var prefix = lines[line][..Math.Clamp(character, 0, lines[line].Length)];
        var callMatch = Regex.Matches(
                prefix,
                "(?<name>[A-Za-z_][A-Za-z0-9_]*)\\s*\\((?<arguments>[^()]*)$",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .LastOrDefault();
        if (callMatch is null)
        {
            return null;
        }

        var namePosition = callMatch.Groups["name"].Index;
        var definition = ResolveSourceDefinition(uri, line, namePosition);
        if (definition?.Signature is null)
        {
            return null;
        }

        var activeParameter = Math.Min(
            callMatch.Groups["arguments"].Value.Count(characterValue => characterValue == ','),
            Math.Max(0, definition.Signature.Parameters.Count - 1));
        return new VbaSignatureHelp(definition.Signature, activeParameter);
    }

    public IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>>? CreateRenameChanges(
        string uri,
        int line,
        int character,
        string newName)
    {
        if (!IsIdentifierName(newName))
        {
            return null;
        }

        var target = ResolveSourceDefinition(uri, line, character);
        if (target is null || !IsRenameTarget(target))
        {
            return null;
        }

        var changes = new Dictionary<string, IReadOnlyList<VbaTextEdit>>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in documents)
        {
            var edits = new List<VbaTextEdit>();
            var lines = SplitLines(document.Text);
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                foreach (var occurrence in FindIdentifierOccurrences(lines[lineIndex]))
                {
                    if (!SameName(occurrence.Name, target.Name))
                    {
                        continue;
                    }

                    var resolved = ResolveSourceDefinition(document.Uri, lineIndex, occurrence.Start);
                    if (resolved is not null && SameDefinition(resolved, target))
                    {
                        edits.Add(new VbaTextEdit(
                            new VbaRange(
                                new VbaPosition(lineIndex, occurrence.Start),
                                new VbaPosition(lineIndex, occurrence.End)),
                            newName));
                    }
                }
            }

            if (edits.Count > 0)
            {
                changes[document.Uri] = edits;
            }
        }

        return changes.Count == 0 ? null : changes;
    }

    public VbaTextEdit? FormatDocument(string uri, int tabSize)
    {
        var document = documents.FirstOrDefault(candidate => SameUri(candidate.Uri, uri));
        if (document is null)
        {
            return null;
        }

        var formattedText = FormatText(document, Math.Max(1, tabSize));
        if (string.Equals(formattedText, document.Text, StringComparison.Ordinal))
        {
            return null;
        }

        var lines = SplitLines(document.Text);
        return new VbaTextEdit(
            new VbaRange(
                new VbaPosition(0, 0),
                new VbaPosition(Math.Max(0, lines.Length - 1), lines.Length == 0 ? 0 : lines[^1].Length)),
            formattedText);
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
        var syntaxTree = VbaModuleParser.Parse(uri, text);
        var definitions = new List<VbaSourceDefinition>();
        var moduleDefinition = CreateModuleDefinition(uri, syntaxTree.Identity);
        definitions.Add(moduleDefinition);
        definitions.AddRange(syntaxTree.Declarations.Select(declaration =>
            CreateSourceDefinition(uri, moduleDefinition.Name, declaration)));

        return new VbaSourceDocument(uri, text, moduleDefinition.Name, definitions);
    }

    private static VbaSourceDefinition CreateModuleDefinition(string uri, VbaModuleIdentity identity)
    {
        return new VbaSourceDefinition(
            identity.Name,
            identity.Kind,
            VbaSourceDefinitionVisibility.Public,
            uri,
            identity.Name,
            identity.Range);
    }

    private static VbaSourceDefinition CreateSourceDefinition(
        string uri,
        string moduleName,
        VbaSourceDeclarationSyntax declaration)
    {
        return new VbaSourceDefinition(
            declaration.Name,
            declaration.Kind,
            declaration.Visibility,
            uri,
            moduleName,
            declaration.Range,
            declaration.ParentProcedureName,
            declaration.ParentProcedureRange,
            declaration.Documentation,
            declaration.Signature);
    }

    private static bool IsReferenceTarget(VbaSourceDefinition definition)
        => definition.Visibility != VbaSourceDefinitionVisibility.Local
            && definition.Kind != VbaSourceDefinitionKind.Module
            && definition.Kind != VbaSourceDefinitionKind.Class
            && definition.Kind != VbaSourceDefinitionKind.Form;

    private static bool IsRenameTarget(VbaSourceDefinition definition)
        => !VbaProjectReferenceCatalogSet.IsExternalDefinition(definition)
            && (definition.Visibility == VbaSourceDefinitionVisibility.Local || IsReferenceTarget(definition));

    private static bool SameDefinition(VbaSourceDefinition left, VbaSourceDefinition right)
        => SameUri(left.Uri, right.Uri)
            && SameName(left.Name, right.Name)
            && ComparePosition(left.Range.Start, right.Range.Start) == 0
            && ComparePosition(left.Range.End, right.Range.End) == 0;

    private static string[] SplitLines(string source)
        => source.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private string FormatText(VbaSourceDocument document, int tabSize)
    {
        var canonicalNames = documents
            .SelectMany(sourceDocument => sourceDocument.Definitions)
            .Where(IsReferenceTarget)
            .GroupBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);
        var lines = SplitLines(document.Text);
        var formattedLines = new List<string>(lines.Length);
        var depth = 0;
        var indent = new string(' ', tabSize);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                formattedLines.Add("");
                continue;
            }

            var casedLine = FormatLineCasing(line, canonicalNames);
            var trimmed = casedLine.TrimStart();
            if (ShouldDedentBefore(trimmed))
            {
                depth = Math.Max(0, depth - 1);
            }

            formattedLines.Add($"{string.Concat(Enumerable.Repeat(indent, depth))}{trimmed}");

            if (ShouldIndentAfter(trimmed))
            {
                depth++;
            }
        }

        return string.Join('\n', formattedLines);
    }

    private static string FormatLineCasing(string line, IReadOnlyDictionary<string, string> canonicalNames)
    {
        var commentStart = FindApostropheCommentStart(line);
        var codePart = commentStart < 0 ? line : line[..commentStart];
        var commentPart = commentStart < 0 ? "" : line[commentStart..];

        codePart = Regex.Replace(
            codePart,
            "^\\s*Attribute\\s+VB_Name",
            match => match.Value[..^"Attribute VB_Name".Length] + "Attribute VB_Name",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        foreach (var keyword in LanguageKeywords)
        {
            codePart = Regex.Replace(
                codePart,
                $"\\b{Regex.Escape(keyword.Key)}\\b",
                keyword.Value,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        foreach (var canonicalName in canonicalNames)
        {
            codePart = Regex.Replace(
                codePart,
                $"\\b{Regex.Escape(canonicalName.Key)}\\b",
                canonicalName.Value,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return codePart + commentPart;
    }

    private static bool ShouldDedentBefore(string trimmedLine)
        => Regex.IsMatch(
            trimmedLine,
            "^(End\\s+(Sub|Function|Property|If|Select|With|Enum|Type)|Else\\b|ElseIf\\b|Case\\b|Loop\\b|Wend\\b|Next\\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool ShouldIndentAfter(string trimmedLine)
        => Regex.IsMatch(
            trimmedLine,
            "^((Public|Private|Friend)\\s+)?(Sub|Function|Property)\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || Regex.IsMatch(trimmedLine, "^If\\b.*\\bThen\\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || Regex.IsMatch(trimmedLine, "^(Else\\b|ElseIf\\b|Case\\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || Regex.IsMatch(trimmedLine, "^(For\\b|Do\\b|While\\b|With\\b|Select\\s+Case\\b|Enum\\b|Type\\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static IEnumerable<IdentifierAtPosition> FindIdentifierOccurrences(string line)
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
                yield break;
            }

            if (inString || !IsIdentifierStart(current))
            {
                continue;
            }

            var start = index;
            index++;
            while (index < line.Length && IsIdentifierCharacter(line[index]))
            {
                index++;
            }

            yield return new IdentifierAtPosition(line[start..index], start, index);
            index--;
        }
    }

    private static int FindApostropheCommentStart(string line)
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
                return index;
            }
        }

        return -1;
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

    private static bool IsCodePosition(string line, int character)
    {
        var inString = false;
        var clamped = Math.Clamp(character, 0, Math.Max(0, line.Length - 1));
        for (var index = 0; index <= clamped && index < line.Length; index++)
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
                if (index == clamped)
                {
                    return false;
                }

                continue;
            }

            if (!inString && current == '\'')
            {
                return false;
            }
        }

        return !inString;
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

    private static bool IsIdentifierStart(char value)
        => char.IsAsciiLetter(value) || value == '_';

    private static bool IsIdentifierCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value == '_';

    private static bool IsIdentifierName(string value)
        => Regex.IsMatch(
            value,
            "^[A-Za-z_][A-Za-z0-9_]*$",
            RegexOptions.CultureInvariant);

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
