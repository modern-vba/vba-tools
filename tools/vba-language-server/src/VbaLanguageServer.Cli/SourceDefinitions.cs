using System.Text.RegularExpressions;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Syntax;

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

public sealed record VbaTypeReference(string Name, string? Qualifier = null);

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
    VbaCallableSignature? Signature = null,
    string? ParentTypeName = null,
    VbaTypeReference? TypeReference = null,
    bool IsWithEvents = false);

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
    IReadOnlyList<VbaSourceDefinition> Definitions,
    VbaSyntaxTree? SyntaxTree = null);

public sealed record VbaDefinitionLocation(string Uri, VbaRange Range);

public sealed record VbaTextEdit(VbaRange Range, string NewText);

public sealed record VbaWorkspaceSymbol(
    string Name,
    VbaSourceDefinitionKind Kind,
    string Uri,
    VbaRange Range);

public sealed record VbaSemanticToken(
    VbaRange Range,
    string Text,
    string TokenType,
    IReadOnlyList<string> TokenModifiers);

public sealed class VbaSourceIndex
{
    public static readonly IReadOnlyList<string> SemanticTokenTypes = [
        "namespace",
        "type",
        "class",
        "enum",
        "interface",
        "struct",
        "typeParameter",
        "parameter",
        "variable",
        "property",
        "enumMember",
        "event",
        "function",
        "method"
    ];

    public static readonly IReadOnlyList<string> SemanticTokenModifiers = [
        "declaration",
        "definition",
        "readonly",
        "static",
        "deprecated",
        "abstract",
        "async",
        "modification",
        "documentation",
        "defaultLibrary"
    ];

    public static readonly IReadOnlyList<string> LanguageVocabulary = VbaLanguageVocabulary.Keywords;

    private readonly IReadOnlyList<VbaSourceDocument> documents;
    private readonly VbaSemanticResolution semanticResolution;
    private readonly VbaSourceFormatter sourceFormatter;

    private VbaSourceIndex(
        IReadOnlyList<VbaSourceDocument> documents,
        VbaProjectReferenceSelection? referenceSelection,
        VbaProjectReferenceCatalogSet referenceCatalogs)
    {
        this.documents = documents;
        semanticResolution = new VbaSemanticResolution(documents, referenceSelection, referenceCatalogs);
        sourceFormatter = new VbaSourceFormatter(semanticResolution);
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

    public static VbaSourceIndex BuildFromSyntaxTrees(
        IReadOnlyDictionary<string, VbaSyntaxTree> sourceDocuments,
        VbaProjectReferenceSelection? referenceSelection = null,
        VbaProjectReferenceCatalogSet? referenceCatalogs = null)
    {
        var parsedDocuments = sourceDocuments
            .Select(entry => CreateDocument(entry.Key, entry.Value))
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

    public IReadOnlyList<VbaWorkspaceSymbol> GetWorkspaceSymbols(string query)
    {
        var normalizedQuery = query ?? "";
        return documents
            .SelectMany(document => document.Definitions)
            .Where(definition => definition.Visibility != VbaSourceDefinitionVisibility.Local)
            .Where(definition => !VbaProjectReferenceCatalogSet.IsExternalDefinition(definition))
            .Where(definition => string.IsNullOrWhiteSpace(normalizedQuery)
                || definition.Name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .Select(definition => new VbaWorkspaceSymbol(
                definition.Name,
                definition.Kind,
                definition.Uri,
                definition.Range))
            .OrderBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(symbol => symbol.Uri, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<VbaDefinitionLocation> FindReferences(string uri, int line, int character)
    {
        var target = ResolveSourceDefinition(uri, line, character);
        if (target is null)
        {
            return [];
        }

        var references = new List<VbaDefinitionLocation>();
        foreach (var document in documents)
        {
            var lines = VbaSourceText.SplitLines(document.Text);
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                foreach (var occurrence in VbaSourceText.FindIdentifierOccurrences(lines[lineIndex]))
                {
                    var resolved = ResolveSourceDefinition(document.Uri, lineIndex, occurrence.Start);
                    if (resolved is null || !SameDefinition(resolved, target))
                    {
                        continue;
                    }

                    references.Add(new VbaDefinitionLocation(
                        document.Uri,
                        new VbaRange(
                            new VbaPosition(lineIndex, occurrence.Start),
                            new VbaPosition(lineIndex, occurrence.End))));
                }
            }
        }

        return references
            .GroupBy(reference => $"{reference.Uri}:{GetRangeKey(reference.Range)}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(reference => reference.Uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(reference => reference.Range.Start.Line)
            .ThenBy(reference => reference.Range.Start.Character)
            .ToArray();
    }

    public IReadOnlyList<VbaSemanticToken> GetSemanticTokens(string uri)
        => VbaSemanticTokenBuilder.GetSemanticTokens(
            documents,
            uri,
            (line, character) => ResolveSourceDefinition(uri, line, character));

    public IReadOnlyList<int> GetSemanticTokenData(string uri)
        => VbaSemanticTokenBuilder.GetSemanticTokenData(GetSemanticTokens(uri));

    public IReadOnlyList<VbaSourceDefinition> GetCompletionDefinitions(string uri, int line, int character)
        => semanticResolution.GetCompletionDefinitions(uri, line, character);

    public VbaDefinitionLocation? ResolveDefinition(string uri, int line, int character)
    {
        var definition = ResolveSourceDefinition(uri, line, character);
        return definition is null ? null : new VbaDefinitionLocation(definition.Uri, definition.Range);
    }

    public VbaSourceDefinition? ResolveSourceDefinition(string uri, int line, int character)
        => semanticResolution.ResolveSourceDefinition(uri, line, character);

    public VbaSignatureHelp? GetSignatureHelp(string uri, int line, int character)
        => semanticResolution.GetSignatureHelp(uri, line, character);

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
            var lines = VbaSourceText.SplitLines(document.Text);
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                foreach (var occurrence in VbaSourceText.FindIdentifierOccurrences(lines[lineIndex]))
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
        return document is null ? null : sourceFormatter.FormatDocument(document, tabSize);
    }

    private static string GetRangeKey(VbaRange range)
        => $"{range.Start.Line}:{range.Start.Character}:{range.End.Line}:{range.End.Character}";

    private static VbaSourceDocument ParseDocument(string uri, string text)
    {
        var syntaxTree = VbaSyntaxTree.ParseModule(uri, text);
        return CreateDocument(uri, syntaxTree);
    }

    private static VbaSourceDocument CreateDocument(string uri, VbaSyntaxTree syntaxTree)
    {
        var definitions = new List<VbaSourceDefinition>();
        var moduleDefinition = CreateModuleDefinition(uri, syntaxTree.Module);
        definitions.Add(moduleDefinition);
        definitions.AddRange(syntaxTree.Module.Declarations.Select(declaration =>
            CreateSourceDefinition(uri, moduleDefinition.Name, declaration)));

        return new VbaSourceDocument(uri, syntaxTree.Text, moduleDefinition.Name, definitions, syntaxTree);
    }

    private static VbaSourceDefinition CreateModuleDefinition(string uri, VbaModuleSyntax module)
    {
        return new VbaSourceDefinition(
            module.Identity.Name,
            MapModuleKind(module.Kind),
            VbaSourceDefinitionVisibility.Public,
            uri,
            module.Identity.Name,
            MapRange(module.Identity.Range));
    }

    private static VbaSourceDefinition CreateSourceDefinition(
        string uri,
        string moduleName,
        VbaDeclarationSyntax declaration)
    {
        return new VbaSourceDefinition(
            declaration.Name,
            MapDeclarationKind(declaration.Kind),
            MapVisibility(declaration.Visibility),
            uri,
            moduleName,
            MapRange(declaration.Range),
            declaration.ParentProcedureName,
            declaration.ParentProcedureRange is null ? null : MapRange(declaration.ParentProcedureRange),
            declaration.Documentation,
            declaration.Signature is null ? null : MapSignature(declaration.Signature),
            declaration.ParentTypeName,
            declaration.TypeReference is null ? null : MapTypeReference(declaration.TypeReference),
            declaration.IsWithEvents);
    }

    private static VbaSourceDefinitionKind MapModuleKind(VbaModuleKind kind)
        => kind switch
        {
            VbaModuleKind.ClassModule => VbaSourceDefinitionKind.Class,
            VbaModuleKind.FormModule => VbaSourceDefinitionKind.Form,
            _ => VbaSourceDefinitionKind.Module
        };

    private static VbaSourceDefinitionKind MapDeclarationKind(VbaDeclarationKind kind)
        => kind switch
        {
            VbaDeclarationKind.Procedure => VbaSourceDefinitionKind.Procedure,
            VbaDeclarationKind.Property => VbaSourceDefinitionKind.Property,
            VbaDeclarationKind.Constant => VbaSourceDefinitionKind.Constant,
            VbaDeclarationKind.Variable => VbaSourceDefinitionKind.Variable,
            VbaDeclarationKind.Parameter => VbaSourceDefinitionKind.Parameter,
            VbaDeclarationKind.Enum => VbaSourceDefinitionKind.Enum,
            VbaDeclarationKind.EnumMember => VbaSourceDefinitionKind.EnumMember,
            VbaDeclarationKind.Type => VbaSourceDefinitionKind.Type,
            VbaDeclarationKind.TypeMember => VbaSourceDefinitionKind.TypeMember,
            VbaDeclarationKind.Event => VbaSourceDefinitionKind.Event,
            _ => VbaSourceDefinitionKind.Variable
        };

    private static VbaSourceDefinitionVisibility MapVisibility(VbaDeclarationVisibility visibility)
        => visibility switch
        {
            VbaDeclarationVisibility.Public => VbaSourceDefinitionVisibility.Public,
            VbaDeclarationVisibility.Local => VbaSourceDefinitionVisibility.Local,
            _ => VbaSourceDefinitionVisibility.Private
        };

    private static VbaRange MapRange(VbaSyntaxRange range)
        => new(
            new VbaPosition(range.Start.Line, range.Start.Character),
            new VbaPosition(range.End.Line, range.End.Character));

    private static VbaCallableSignature MapSignature(VbaCallableSignatureSyntax signature)
        => new(
            signature.Label,
            signature.Parameters.Select(parameter => new VbaCallableParameter(parameter.Name, parameter.Documentation)).ToArray(),
            signature.Documentation);

    private static VbaTypeReference MapTypeReference(VbaTypeReferenceSyntax typeReference)
        => new(typeReference.Name, typeReference.Qualifier);

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
}
