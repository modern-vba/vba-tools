using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.SourceModel;

public sealed record VbaProjectReferenceDefinition(
    string ReferenceName,
    string Name,
    VbaSourceDefinitionKind Kind,
    string? Documentation = null,
    VbaCallableSignature? Signature = null);

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
                        "Run",
                        VbaSourceDefinitionKind.Procedure,
                        "Runs a macro or calls a function.",
                        new VbaCallableSignature(
                            "Run(Macro, Arg1)",
                            [
                                new VbaCallableParameter("Macro", "The macro or function to run."),
                                new VbaCallableParameter("Arg1", "The first argument passed to the macro.")
                            ],
                            "Runs a macro or calls a function."))
                ]),
            new VbaProjectReferenceCatalog(
                "Microsoft Scripting Runtime",
                ["Scripting"],
                [
                    new VbaProjectReferenceDefinition(
                        "Microsoft Scripting Runtime",
                        "Dictionary",
                        VbaSourceDefinitionKind.Class,
                        "Represents a key/item collection provided by Microsoft Scripting Runtime.")
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

    public IReadOnlyList<VbaSourceDefinition> GetCompletionDefinitions(VbaProjectReferenceSelection selection)
    {
        return GetActiveDefinitions(selection)
            .GroupBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => ResolveCandidates(selection, group.ToArray()))
            .Where(definition => definition is not null)
            .Select(definition => definition!)
            .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> GetMissingCatalogReferenceNames(VbaProjectReferenceSelection selection)
    {
        return selection.References
            .Where(reference => !catalogs.ContainsKey(reference.Name))
            .Select(reference => reference.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public VbaSourceDefinition? ResolveUnqualified(VbaProjectReferenceSelection selection, string identifier)
    {
        var candidates = GetActiveDefinitions(selection)
            .Where(definition => definition.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return ResolveCandidates(selection, candidates);
    }

    public VbaSourceDefinition? ResolveQualified(
        VbaProjectReferenceSelection selection,
        string qualifier,
        string memberName)
    {
        var candidates = GetActiveCatalogs(selection)
            .Where(catalog => catalog.Catalog.QualifierAliases.Any(alias =>
                alias.Equals(qualifier, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(catalog => catalog.Catalog.Definitions)
            .Where(definition => definition.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return ResolveCandidates(selection, candidates);
    }

    private VbaSourceDefinition? ResolveCandidates(
        VbaProjectReferenceSelection selection,
        IReadOnlyList<VbaProjectReferenceDefinition> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count == 1)
        {
            return ToSourceDefinition(candidates[0]);
        }

        if (selection.MainVbaProjectReference is not null)
        {
            var mainCandidates = candidates
                .Where(definition => definition.ReferenceName.Equals(
                    selection.MainVbaProjectReference.Name,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (mainCandidates.Length == 1)
            {
                return ToSourceDefinition(mainCandidates[0]);
            }
        }

        return null;
    }

    private IEnumerable<VbaProjectReferenceDefinition> GetActiveDefinitions(VbaProjectReferenceSelection selection)
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
            Signature: definition.Signature);
    }

    private sealed record ActiveReferenceCatalog(string ManifestReferenceName, VbaProjectReferenceCatalog Catalog);
}
