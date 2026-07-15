using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Represents one definition supplied by an active VBA project reference catalog.
/// </summary>
/// <param name="ReferenceName">The manifest reference name that owns the definition.</param>
/// <param name="Name">The definition name.</param>
/// <param name="Kind">The editor-facing definition kind.</param>
/// <param name="Documentation">The documentation text supplied by the catalog.</param>
/// <param name="Signature">The callable signature supplied by the catalog.</param>
/// <param name="ParentTypeName">The containing type name for members.</param>
/// <param name="TypeReference">The result or member type reference supplied by the catalog.</param>
/// <param name="PropertyAccess">The supported property operations, or Unknown when unavailable.</param>
/// <param name="IsCreatable">Whether the type can be used as the target of a New expression.</param>
public sealed record VbaProjectReferenceDefinition(
    string ReferenceName,
    string Name,
    VbaSourceDefinitionKind Kind,
    string? Documentation = null,
    VbaCallableSignature? Signature = null,
    string? ParentTypeName = null,
    VbaTypeReference? TypeReference = null,
    VbaPropertyAccess PropertyAccess = VbaPropertyAccess.Unknown,
    bool IsCreatable = false);

/// <summary>
/// Contains reference-catalog definitions and qualifier aliases for one VBA project reference.
/// </summary>
/// <param name="ReferenceName">The manifest reference name.</param>
/// <param name="QualifierAliases">The qualifier aliases that can address this reference explicitly.</param>
/// <param name="Definitions">The definitions supplied by the reference catalog.</param>
public sealed record VbaProjectReferenceCatalog(
    string ReferenceName,
    IReadOnlyList<string> QualifierAliases,
    IReadOnlyList<VbaProjectReferenceDefinition> Definitions);

/// <summary>
/// Stores available VBA project reference catalogs and projects them into source-model definitions.
/// </summary>
public sealed class VbaProjectReferenceCatalogSet
{
    /// <summary>
    /// The URI prefix used for definitions that originate from reference catalogs.
    /// </summary>
    public const string ExternalDefinitionUriPrefix = "vba-reference://";

    private readonly IReadOnlyDictionary<string, VbaProjectReferenceCatalog> catalogs;

    private VbaProjectReferenceCatalogSet(IReadOnlyDictionary<string, VbaProjectReferenceCatalog> catalogs)
    {
        this.catalogs = catalogs;
    }

    /// <summary>
    /// Gets an empty catalog set.
    /// </summary>
    public static VbaProjectReferenceCatalogSet Empty { get; } =
        new(new Dictionary<string, VbaProjectReferenceCatalog>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Creates the bundled minimal reference catalog set shipped with the language server.
    /// </summary>
    /// <returns>The bundled reference catalog set.</returns>
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
                        "Represents an ordered set of items.",
                        IsCreatable: true),
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
                            "Displays a message in a dialog box.",
                            CallableKind: VbaCallableKind.Function))
                ]),
            new VbaProjectReferenceCatalog(
                "Microsoft Excel 16.0 Object Library",
                ["Excel"],
                [
                    new VbaProjectReferenceDefinition(
                        "Microsoft Excel 16.0 Object Library",
                        "Application",
                        VbaSourceDefinitionKind.Class,
                        "Represents the Microsoft Excel application.",
                        IsCreatable: true),
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
                        TypeReference: new VbaTypeReference("Workbooks", "Excel"),
                        PropertyAccess: VbaPropertyAccess.Readable),
                    new VbaProjectReferenceDefinition(
                        "Microsoft Excel 16.0 Object Library",
                        "Run",
                        VbaSourceDefinitionKind.Procedure,
                        "Runs a macro or calls a function.",
                        new VbaCallableSignature(
                            "Run(Macro, [Arg1])",
                            [
                                new VbaCallableParameter("Macro", "The macro or function to run."),
                                new VbaCallableParameter(
                                    "Arg1",
                                    "The first argument passed to the macro.",
                                    IsOptional: true)
                            ],
                            "Runs a macro or calls a function.",
                            CallableKind: VbaCallableKind.Function),
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
                            "Opens a workbook.",
                            CallableKind: VbaCallableKind.Function),
                        ParentTypeName: "Workbooks",
                        TypeReference: new VbaTypeReference("Workbook", "Excel")),
                    new VbaProjectReferenceDefinition(
                        "Microsoft Excel 16.0 Object Library",
                        "Name",
                        VbaSourceDefinitionKind.Property,
                        "Returns the workbook name.",
                        ParentTypeName: "Workbook",
                        PropertyAccess: VbaPropertyAccess.Readable),
                    new VbaProjectReferenceDefinition(
                        "Microsoft Excel 16.0 Object Library",
                        "WorkbookOpen",
                        VbaSourceDefinitionKind.Event,
                        "Occurs when a workbook is opened.",
                        new VbaCallableSignature(
                            "WorkbookOpen(Wb)",
                            [
                                new VbaCallableParameter("Wb", "The opened workbook.")
                            ],
                            "Occurs when a workbook is opened.",
                            CallableKind: VbaCallableKind.Event),
                        ParentTypeName: "Application")
                ]),
            new VbaProjectReferenceCatalog(
                "Microsoft Scripting Runtime",
                ["Scripting"],
                [
                    new VbaProjectReferenceDefinition(
                        "Microsoft Scripting Runtime",
                        "Dictionary",
                        VbaSourceDefinitionKind.Class,
                        "Represents a key/item collection provided by Microsoft Scripting Runtime.",
                        IsCreatable: true),
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
                            "Returns whether a key exists in the dictionary.",
                            CallableKind: VbaCallableKind.Function),
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
                        "Represents a Microsoft Outlook application.",
                        IsCreatable: true)
                ])
        };

        return new VbaProjectReferenceCatalogSet(
            bundledCatalogs.ToDictionary(
                catalog => catalog.ReferenceName,
                StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the reference names that currently have catalogs.
    /// </summary>
    public IReadOnlyList<string> ReferenceNames
        => catalogs.Keys
            .OrderBy(referenceName => referenceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Determines whether a source definition originated from a reference catalog.
    /// </summary>
    /// <param name="definition">The definition to inspect.</param>
    /// <returns>True when the definition identity originates from a project reference.</returns>
    public static bool IsExternalDefinition(VbaSourceDefinition definition)
        => definition.Identity.Origin == VbaDefinitionOrigin.ProjectReference;

    /// <summary>
    /// Returns a new catalog set with a catalog added or replaced.
    /// </summary>
    /// <param name="catalog">The catalog to add.</param>
    /// <returns>The merged catalog set.</returns>
    public VbaProjectReferenceCatalogSet WithCatalog(VbaProjectReferenceCatalog catalog)
    {
        var merged = new Dictionary<string, VbaProjectReferenceCatalog>(catalogs, StringComparer.OrdinalIgnoreCase)
        {
            [catalog.ReferenceName] = catalog
        };
        return new VbaProjectReferenceCatalogSet(merged);
    }

    /// <summary>
    /// Determines whether a catalog is available for a reference name.
    /// </summary>
    /// <param name="referenceName">The manifest reference name.</param>
    /// <returns>True when the catalog set contains the reference.</returns>
    public bool HasCatalog(string referenceName)
        => catalogs.ContainsKey(referenceName);

    /// <summary>
    /// Gets all definitions contributed by catalogs active in a reference selection.
    /// </summary>
    /// <param name="selection">The active reference selection.</param>
    /// <returns>The active catalog definitions projected into source definitions.</returns>
    public IReadOnlyList<VbaSourceDefinition> GetActiveDefinitions(VbaProjectReferenceSelection selection)
        => GetActiveReferenceDefinitions(selection)
            .Select(ToSourceDefinition)
            .ToArray();

    /// <summary>
    /// Gets selected reference names that do not currently have catalogs.
    /// </summary>
    /// <param name="selection">The active reference selection.</param>
    /// <returns>The missing reference names ordered for deterministic reporting.</returns>
    public IReadOnlyList<string> GetMissingCatalogReferenceNames(VbaProjectReferenceSelection selection)
    {
        return selection.References
            .Where(reference => !catalogs.ContainsKey(reference.Name))
            .Select(reference => reference.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Gets active reference definitions addressed by a qualifier and member name.
    /// </summary>
    /// <param name="selection">The active reference selection.</param>
    /// <param name="qualifier">The qualifier alias used in source.</param>
    /// <param name="memberName">The requested member or root definition name.</param>
    /// <returns>The matching reference definitions.</returns>
    public IReadOnlyList<VbaSourceDefinition> GetQualifiedDefinitions(
        VbaProjectReferenceSelection selection,
        string qualifier,
        string memberName)
    {
        return GetQualifiedDefinitions(selection, qualifier)
            .Where(definition => definition.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    /// <summary>
    /// Gets all active reference definitions addressed by a qualifier alias.
    /// </summary>
    /// <param name="selection">The active reference selection.</param>
    /// <param name="qualifier">The qualifier alias used in source.</param>
    /// <returns>The matching reference definitions.</returns>
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

    /// <summary>
    /// Gets the canonical active qualifier alias that matches a typed qualifier.
    /// </summary>
    /// <param name="selection">The active reference selection.</param>
    /// <param name="referenceName">The reference name that owns the definition.</param>
    /// <param name="qualifier">The qualifier spelling found in source.</param>
    /// <returns>The canonical qualifier alias, or null when it is not active.</returns>
    public string? GetActiveCanonicalQualifierAlias(
        VbaProjectReferenceSelection selection,
        string referenceName,
        string qualifier)
    {
        return GetActiveCatalogs(selection)
            .Where(catalog => catalog.Catalog.ReferenceName.Equals(referenceName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(catalog => catalog.Catalog.QualifierAliases)
            .FirstOrDefault(alias => alias.Equals(qualifier, StringComparison.OrdinalIgnoreCase));
    }

    internal IReadOnlyList<(string ReferenceName, string Qualifier)> GetActiveQualifierAliases(
        VbaProjectReferenceSelection selection)
        => GetActiveCatalogs(selection)
            .SelectMany(catalog => catalog.Catalog.QualifierAliases.Select(alias => (
                catalog.Catalog.ReferenceName,
                Qualifier: alias)))
            .ToArray();

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
        var location = new VbaDefinitionLocation(
            $"{ExternalDefinitionUriPrefix}{Uri.EscapeDataString(definition.ReferenceName)}/{Uri.EscapeDataString(definition.Name)}",
            new VbaRange(new VbaPosition(0, 0), new VbaPosition(0, definition.Name.Length)));
        var signature = CreateSourceSignature(definition);
        return new VbaSourceDefinition(
            Identity: VbaDefinitionIdentity.ForProjectReference(
                definition.ReferenceName,
                definition.ParentTypeName,
                definition.Kind,
                definition.Name),
            Location: location,
            Name: definition.Name,
            Kind: definition.Kind,
            Visibility: VbaSourceDefinitionVisibility.Public,
            ModuleName: definition.ReferenceName,
            Documentation: definition.Documentation,
            Signature: signature,
            ParentTypeName: definition.ParentTypeName,
            TypeReference: definition.TypeReference,
            DeclarationLabel: CreateDeclarationLabel(definition, signature),
            PropertyAccess: definition.PropertyAccess,
            IsCreatable: definition.IsCreatable);
    }

    private static VbaCallableSignature? CreateSourceSignature(VbaProjectReferenceDefinition definition)
    {
        if (definition.Signature is null)
        {
            return null;
        }

        var parameterLabels = definition.Signature.Parameters.Select(CreateRichParameterLabel).ToArray();
        var callableKind = GetCallableKindLabel(definition);
        var callablePrefix = callableKind is null ? "" : $"{callableKind} ";
        var label = $"{callablePrefix}{definition.Name}({string.Join(", ", parameterLabels)})";
        if (definition.TypeReference is not null)
        {
            label = $"{label} As {definition.TypeReference.Name}";
        }

        return definition.Signature with
        {
            Label = label,
            Parameters = definition.Signature.Parameters
                .Select((parameter, index) => parameter with { DisplayLabel = parameterLabels[index] })
                .ToArray()
        };
    }

    private static string? GetCallableKindLabel(VbaProjectReferenceDefinition definition)
        => definition.Kind switch
        {
            VbaSourceDefinitionKind.Property => "Property",
            VbaSourceDefinitionKind.Event => "Event",
            VbaSourceDefinitionKind.Procedure => definition.Signature?.CallableKind?.ToString(),
            _ => null
        };

    private static string CreateRichParameterLabel(VbaCallableParameter parameter)
    {
        var parts = new List<string>();
        if (parameter.IsParamArray)
        {
            parts.Add("ParamArray");
        }
        else if (parameter.IsByRef == true)
        {
            parts.Add("ByRef");
        }

        parts.Add(parameter.IsArray ? $"{parameter.Name}()" : parameter.Name);
        if (parameter.TypeReference is not null)
        {
            parts.Add($"As {parameter.TypeReference.Name}");
        }

        var label = string.Join(" ", parts);
        return parameter.IsOptional ? $"[{label}]" : label;
    }

    private static string CreateCompactParameterLabel(VbaCallableParameter parameter)
        => parameter.IsOptional ? $"[{parameter.Name}]" : parameter.Name;

    private static string? CreateDeclarationLabel(
        VbaProjectReferenceDefinition definition,
        VbaCallableSignature? signature)
        => definition.Kind switch
        {
            VbaSourceDefinitionKind.Procedure => CreateCallableDeclarationLabel(definition, signature),
            VbaSourceDefinitionKind.Property when signature is not null => $"Property {CreateCompactCallableLabel(definition, signature)}",
            VbaSourceDefinitionKind.Property => $"Property {CreateValueLabel(definition)}",
            VbaSourceDefinitionKind.Event => $"Event {CreateCompactCallableLabel(definition, signature)}",
            VbaSourceDefinitionKind.Variable => CreateValueLabel(definition),
            VbaSourceDefinitionKind.Constant => $"Const {CreateValueLabel(definition)}",
            VbaSourceDefinitionKind.Enum => $"Enum {definition.Name}",
            VbaSourceDefinitionKind.Type => $"Type {definition.Name}",
            VbaSourceDefinitionKind.EnumMember or VbaSourceDefinitionKind.TypeMember => CreateValueLabel(definition),
            _ => null
        };

    private static string CreateCallableDeclarationLabel(
        VbaProjectReferenceDefinition definition,
        VbaCallableSignature? signature)
    {
        var callableKind = GetCallableKindLabel(definition);
        var callableLabel = CreateCompactCallableLabel(definition, signature);
        return callableKind is null ? callableLabel : $"{callableKind} {callableLabel}";
    }

    private static string CreateCompactCallableLabel(
        VbaProjectReferenceDefinition definition,
        VbaCallableSignature? signature)
    {
        if (signature is null)
        {
            return definition.Kind == VbaSourceDefinitionKind.Event
                ? $"{definition.Name}()"
                : CreateValueLabel(definition);
        }

        var label = $"{definition.Name}({string.Join(", ", signature.Parameters.Select(CreateCompactParameterLabel))})";
        return definition.TypeReference is null
            ? label
            : $"{label} As {definition.TypeReference.Name}";
    }

    private static string CreateValueLabel(VbaProjectReferenceDefinition definition)
        => definition.TypeReference is null
            ? definition.Name
            : $"{definition.Name} As {definition.TypeReference.Name}";

    private sealed record ActiveReferenceCatalog(string ManifestReferenceName, VbaProjectReferenceCatalog Catalog);
}
