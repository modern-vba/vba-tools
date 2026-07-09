using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.SourceModel;

public sealed record VbaProjectReferenceDefinition(
    string ReferenceName,
    string Name,
    VbaSourceDefinitionKind Kind,
    string? Documentation = null,
    VbaCallableSignature? Signature = null,
    string? ParentTypeName = null,
    VbaTypeReference? TypeReference = null);

public sealed record VbaProjectReferenceCatalog(
    string ReferenceName,
    IReadOnlyList<string> QualifierAliases,
    IReadOnlyList<VbaProjectReferenceDefinition> Definitions);

public sealed class VbaProjectReferenceCatalogSet
{
    public const string ExternalDefinitionUriPrefix = "vba-reference://";

    private readonly IReadOnlyDictionary<string, VbaProjectReferenceCatalog> catalogs;

    private VbaProjectReferenceCatalogSet(IReadOnlyDictionary<string, VbaProjectReferenceCatalog> catalogs)
    {
        this.catalogs = catalogs;
    }

    public static VbaProjectReferenceCatalogSet Empty { get; } =
        new(new Dictionary<string, VbaProjectReferenceCatalog>(StringComparer.OrdinalIgnoreCase));

    public static VbaProjectReferenceCatalogSet CreateBundled()
    {
        var bundledCatalogs = new[]
        {
            new VbaProjectReferenceCatalog(
                "Visual Basic For Applications",
                ["VBA"],
                [
                    new VbaProjectReferenceDefinition(
                        "Visual Basic For Applications",
                        "Collection",
                        VbaSourceDefinitionKind.Class,
                        "Represents an ordered set of items."),
                    new VbaProjectReferenceDefinition(
                        "Visual Basic For Applications",
                        "MsgBox",
                        VbaSourceDefinitionKind.Procedure,
                        "Displays a message in a dialog box.",
                        new VbaCallableSignature(
                            "MsgBox(Prompt, Buttons, Title)",
                            [
                                new VbaCallableParameter("Prompt", "The message to display."),
                                new VbaCallableParameter("Buttons", "The buttons and icon style."),
                                new VbaCallableParameter("Title", "The dialog box title.")
                            ],
                            "Displays a message in a dialog box."))
                ]),
            new VbaProjectReferenceCatalog(
                "Microsoft Excel 16.0 Object Library",
                ["Excel"],
                [
                    new VbaProjectReferenceDefinition(
                        "Microsoft Excel 16.0 Object Library",
                        "Application",
                        VbaSourceDefinitionKind.Class,
                        "Represents the Microsoft Excel application."),
                    new VbaProjectReferenceDefinition(
                        "Microsoft Excel 16.0 Object Library",
                        "Workbooks",
                        VbaSourceDefinitionKind.Class,
                        "Represents the collection of open Microsoft Excel workbooks."),
                    new VbaProjectReferenceDefinition(
                        "Microsoft Excel 16.0 Object Library",
                        "Workbooks",
                        VbaSourceDefinitionKind.Property,
                        "Returns the open Microsoft Excel workbooks.",
                        ParentTypeName: "Application",
                        TypeReference: new VbaTypeReference("Workbooks", "Excel")),
                    new VbaProjectReferenceDefinition(
                        "Microsoft Excel 16.0 Object Library",
                        "Run",
                        VbaSourceDefinitionKind.Procedure,
                        "Runs a macro or calls a function.",
                        new VbaCallableSignature(
                            "Run(Macro, Arg1)",
                            [
                                new VbaCallableParameter("Macro", "The macro or function to run."),
                                new VbaCallableParameter("Arg1", "The first argument passed to the macro.")
                            ],
                            "Runs a macro or calls a function."),
                        ParentTypeName: "Application"),
                    new VbaProjectReferenceDefinition(
                        "Microsoft Excel 16.0 Object Library",
                        "Workbook",
                        VbaSourceDefinitionKind.Class,
                        "Represents a Microsoft Excel workbook."),
                    new VbaProjectReferenceDefinition(
                        "Microsoft Excel 16.0 Object Library",
                        "Open",
                        VbaSourceDefinitionKind.Procedure,
                        "Opens a workbook.",
                        new VbaCallableSignature(
                            "Open(FileName)",
                            [
                                new VbaCallableParameter("FileName", "The workbook file name.")
                            ],
                            "Opens a workbook."),
                        ParentTypeName: "Workbooks",
                        TypeReference: new VbaTypeReference("Workbook", "Excel")),
                    new VbaProjectReferenceDefinition(
                        "Microsoft Excel 16.0 Object Library",
                        "Name",
                        VbaSourceDefinitionKind.Property,
                        "Returns the workbook name.",
                        ParentTypeName: "Workbook")
                ]),
            new VbaProjectReferenceCatalog(
                "Microsoft Scripting Runtime",
                ["Scripting"],
                [
                    new VbaProjectReferenceDefinition(
                        "Microsoft Scripting Runtime",
                        "Dictionary",
                        VbaSourceDefinitionKind.Class,
                        "Represents a key/item collection provided by Microsoft Scripting Runtime."),
                    new VbaProjectReferenceDefinition(
                        "Microsoft Scripting Runtime",
                        "Exists",
                        VbaSourceDefinitionKind.Procedure,
                        "Returns whether a key exists in the dictionary.",
                        new VbaCallableSignature(
                            "Exists(Key)",
                            [
                                new VbaCallableParameter("Key", "The key to find.")
                            ],
                            "Returns whether a key exists in the dictionary."),
                        ParentTypeName: "Dictionary",
                        TypeReference: new VbaTypeReference("Boolean"))
                ]),
            new VbaProjectReferenceCatalog(
                "Microsoft Office 16.0 Object Library",
                ["Office"],
                [
                    new VbaProjectReferenceDefinition(
                        "Microsoft Office 16.0 Object Library",
                        "Application",
                        VbaSourceDefinitionKind.Class,
                        "Represents a Microsoft Office application.")
                ]),
            new VbaProjectReferenceCatalog(
                "Microsoft Outlook 16.0 Object Library",
                ["Outlook"],
                [
                    new VbaProjectReferenceDefinition(
                        "Microsoft Outlook 16.0 Object Library",
                        "Application",
                        VbaSourceDefinitionKind.Class,
                        "Represents a Microsoft Outlook application.")
                ])
        };

        return new VbaProjectReferenceCatalogSet(
            bundledCatalogs.ToDictionary(
                catalog => catalog.ReferenceName,
                StringComparer.OrdinalIgnoreCase));
    }

    public static bool IsExternalDefinition(VbaSourceDefinition definition)
        => definition.Uri.StartsWith(ExternalDefinitionUriPrefix, StringComparison.OrdinalIgnoreCase);

    public VbaProjectReferenceCatalogSet WithCatalog(VbaProjectReferenceCatalog catalog)
    {
        var merged = new Dictionary<string, VbaProjectReferenceCatalog>(catalogs, StringComparer.OrdinalIgnoreCase)
        {
            [catalog.ReferenceName] = catalog
        };
        return new VbaProjectReferenceCatalogSet(merged);
    }

    public bool HasCatalog(string referenceName)
        => catalogs.ContainsKey(referenceName);

    public IReadOnlyList<VbaSourceDefinition> GetActiveDefinitions(VbaProjectReferenceSelection selection)
        => GetActiveReferenceDefinitions(selection)
            .Select(ToSourceDefinition)
            .ToArray();

    public IReadOnlyList<string> GetMissingCatalogReferenceNames(VbaProjectReferenceSelection selection)
    {
        return selection.References
            .Where(reference => !catalogs.ContainsKey(reference.Name))
            .Select(reference => reference.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<VbaSourceDefinition> GetQualifiedDefinitions(
        VbaProjectReferenceSelection selection,
        string qualifier,
        string memberName)
    {
        return GetQualifiedDefinitions(selection, qualifier)
            .Where(definition => definition.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public IReadOnlyList<VbaSourceDefinition> GetQualifiedDefinitions(
        VbaProjectReferenceSelection selection,
        string qualifier)
    {
        return GetActiveCatalogs(selection)
            .Where(catalog => catalog.Catalog.QualifierAliases.Any(alias =>
                alias.Equals(qualifier, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(catalog => catalog.Catalog.Definitions)
            .Select(ToSourceDefinition)
            .ToArray();
    }

    private IEnumerable<VbaProjectReferenceDefinition> GetActiveReferenceDefinitions(VbaProjectReferenceSelection selection)
        => GetActiveCatalogs(selection).SelectMany(catalog => catalog.Catalog.Definitions);

    private IEnumerable<ActiveReferenceCatalog> GetActiveCatalogs(VbaProjectReferenceSelection selection)
    {
        foreach (var reference in selection.References)
        {
            if (catalogs.TryGetValue(reference.Name, out var catalog))
            {
                yield return new ActiveReferenceCatalog(reference.Name, catalog);
            }
        }
    }

    private static VbaSourceDefinition ToSourceDefinition(VbaProjectReferenceDefinition definition)
    {
        return new VbaSourceDefinition(
            definition.Name,
            definition.Kind,
            VbaSourceDefinitionVisibility.Public,
            $"{ExternalDefinitionUriPrefix}{Uri.EscapeDataString(definition.ReferenceName)}/{Uri.EscapeDataString(definition.Name)}",
            definition.ReferenceName,
            new VbaRange(new VbaPosition(0, 0), new VbaPosition(0, definition.Name.Length)),
            Documentation: definition.Documentation,
            Signature: definition.Signature,
            ParentTypeName: definition.ParentTypeName,
            TypeReference: definition.TypeReference);
    }

    private sealed record ActiveReferenceCatalog(string ManifestReferenceName, VbaProjectReferenceCatalog Catalog);
}
