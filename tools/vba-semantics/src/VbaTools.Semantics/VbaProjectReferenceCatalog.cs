using VbaTools.Syntax;

namespace VbaTools.Semantics;

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
/// <param name="GlobalExposure">The definition's explicit public root exposure.</param>
/// <param name="IsAuthoringAvailable">Whether ordinary completion may offer this definition.</param>
/// <param name="IsCallableMetadataComplete">Whether the catalog supplied a complete callable signature.</param>
public sealed record VbaProjectReferenceDefinition(
    string ReferenceName,
    string Name,
    VbaSourceDefinitionKind Kind,
    string? Documentation = null,
    VbaCallableSignature? Signature = null,
    string? ParentTypeName = null,
    VbaTypeReference? TypeReference = null,
    VbaPropertyAccess PropertyAccess = VbaPropertyAccess.Unknown,
    bool IsCreatable = false,
    ReferenceDefinitionGlobalExposure GlobalExposure = ReferenceDefinitionGlobalExposure.None,
    bool IsAuthoringAvailable = true,
    bool IsCallableMetadataComplete = true)
{
    /// <summary>
    /// Gets whether TypeLib metadata identifies this callable as the type's default member.
    /// </summary>
    public bool IsDefaultMember { get; init; }

    /// <summary>
    /// Gets the physical TypeLib Property invoke kind, when known.
    /// </summary>
    public VbaPropertyAccessorKind? PropertyAccessorKind { get; init; }

    /// <summary>
    /// Gets whether a Function or Property Get result is an array, or null when unavailable.
    /// </summary>
    public bool? IsReturnArray { get; init; }

    /// <summary>
    /// Gets the physical callable kind retained independently from signature completeness.
    /// </summary>
    public VbaCallableKind? CallableKind { get; init; }
}

/// <summary>
/// Contains reference-catalog definitions and qualifier aliases for one VBA project reference.
/// </summary>
/// <param name="ReferenceName">The manifest reference name.</param>
/// <param name="QualifierAliases">The qualifier aliases that can address this reference explicitly.</param>
/// <param name="Definitions">The definitions supplied by the reference catalog.</param>
public sealed record VbaProjectReferenceCatalog(
    string ReferenceName,
    IReadOnlyList<string> QualifierAliases,
    IReadOnlyList<VbaProjectReferenceDefinition> Definitions,
    IReadOnlyList<TypeLibCatalogType>? TypeLibTypes = null)
{
    /// <summary>
    /// Gets the authoritative VBA project name exported by this concrete library.
    /// Display names and qualifier aliases are deliberately not substitutes.
    /// </summary>
    public string? ReferencedVbaProjectName { get; init; }
}

internal enum VbaTypeLibEventSurfaceState
{
    Complete,
    Partial,
    Indeterminate
}

internal sealed record VbaTypeLibEventSurface(
    VbaTypeLibEventSurfaceState State,
    TypeLibCatalogRawTypeKind? RawTypeKind,
    int TypeFlags,
    IReadOnlyList<TypeLibCatalogMember> StructuralEvents,
    IReadOnlyList<TypeLibCatalogMember>? PartialExistingHandlerRecognitionEvents = null)
{
    public IReadOnlyList<TypeLibCatalogMember> AuthoringEvents
        => State == VbaTypeLibEventSurfaceState.Complete
            ? StructuralEvents
            .Where(TypeLibCatalogMemberFacts.IsAuthoringAvailable)
            .ToArray()
            : [];

    public IReadOnlyList<TypeLibCatalogMember> ExistingHandlerRecognitionEvents
        => PartialExistingHandlerRecognitionEvents ?? StructuralEvents;

    public static VbaTypeLibEventSurface Indeterminate { get; } =
        new(
            VbaTypeLibEventSurfaceState.Indeterminate,
            RawTypeKind: null,
            TypeFlags: 0,
            StructuralEvents: []);
}

/// <summary>
/// Stores available VBA project reference catalogs and projects them into source-model definitions.
/// </summary>
public sealed class VbaProjectReferenceCatalogSet
{
    /// <summary>
    /// The manifest/catalog name used by the VBA standard library.
    /// </summary>
    public const string StandardLibraryReferenceName = "Visual Basic For Applications";

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
        new(new Dictionary<string, VbaProjectReferenceCatalog>(VbaReferenceName.Comparer));

    /// <summary>
    /// Creates the bundled minimal reference catalog set shipped with the language server.
    /// </summary>
    /// <returns>The bundled reference catalog set.</returns>
    public static VbaProjectReferenceCatalogSet CreateBundled()
    {
        var bundledCatalogs = new[]
        {
            new VbaProjectReferenceCatalog(
                StandardLibraryReferenceName,
                ["VBA"],
                [
                    new VbaProjectReferenceDefinition(
                        StandardLibraryReferenceName,
                        "Collection",
                        VbaSourceDefinitionKind.Class,
                        "Represents an ordered set of items.",
                        IsCreatable: true),
                    new VbaProjectReferenceDefinition(
                        StandardLibraryReferenceName,
                        "MsgBox",
                        VbaSourceDefinitionKind.Procedure,
                        "Displays a message in a dialog box.",
                        new VbaCallableSignature(
                            "MsgBox(Prompt, [Buttons], [Title], [HelpFile], [Context])",
                            [
                                new VbaCallableParameter("Prompt", "The message to display."),
                                new VbaCallableParameter("Buttons", "The buttons and icon style.", IsOptional: true),
                                new VbaCallableParameter("Title", "The dialog box title.", IsOptional: true),
                                new VbaCallableParameter("HelpFile", "The Help file to use with Context.", IsOptional: true),
                                new VbaCallableParameter("Context", "The Help context number to use with HelpFile.", IsOptional: true)
                            ],
                            "Displays a message in a dialog box.",
                            CallableKind: VbaCallableKind.Function,
                            SupportsNamedArguments: true),
                        GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal),
                    new VbaProjectReferenceDefinition(
                        StandardLibraryReferenceName,
                        "vbCrLf",
                        VbaSourceDefinitionKind.Constant,
                        "Carriage return-linefeed character combination.",
                        ParentTypeName: "Constants",
                        TypeReference: new VbaTypeReference("String"),
                        GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal)
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
                        "Application",
                        VbaSourceDefinitionKind.Property,
                        "Returns the Microsoft Excel application.",
                        ParentTypeName: "Application",
                        TypeReference: new VbaTypeReference("Application", "Excel"),
                        PropertyAccess: VbaPropertyAccess.Readable,
                        GlobalExposure: ReferenceDefinitionGlobalExposure.MainHostGlobal),
                    new VbaProjectReferenceDefinition(
                        "Microsoft Excel 16.0 Object Library",
                        "Window",
                        VbaSourceDefinitionKind.Class,
                        "Represents a Microsoft Excel window."),
                    new VbaProjectReferenceDefinition(
                        "Microsoft Excel 16.0 Object Library",
                        "ActiveWindow",
                        VbaSourceDefinitionKind.Property,
                        "Returns the active window.",
                        ParentTypeName: "Application",
                        TypeReference: new VbaTypeReference("Window", "Excel"),
                        PropertyAccess: VbaPropertyAccess.Readable,
                        GlobalExposure: ReferenceDefinitionGlobalExposure.MainHostGlobal),
                    new VbaProjectReferenceDefinition(
                        "Microsoft Excel 16.0 Object Library",
                        "Range",
                        VbaSourceDefinitionKind.Class,
                        "Represents a cell or range of cells."),
                    new VbaProjectReferenceDefinition(
                        "Microsoft Excel 16.0 Object Library",
                        "Row",
                        VbaSourceDefinitionKind.Property,
                        "Returns the number of the first row in the range.",
                        ParentTypeName: "Range",
                        TypeReference: new VbaTypeReference("Long"),
                        PropertyAccess: VbaPropertyAccess.Readable),
                    new VbaProjectReferenceDefinition(
                        "Microsoft Excel 16.0 Object Library",
                        "ActiveCell",
                        VbaSourceDefinitionKind.Property,
                        "Returns the active cell.",
                        ParentTypeName: "Application",
                        TypeReference: new VbaTypeReference("Range", "Excel"),
                        PropertyAccess: VbaPropertyAccess.Readable,
                        GlobalExposure: ReferenceDefinitionGlobalExposure.MainHostGlobal),
                    new VbaProjectReferenceDefinition(
                        "Microsoft Excel 16.0 Object Library",
                        "ActiveSheet",
                        VbaSourceDefinitionKind.Property,
                        "Returns the active sheet.",
                        ParentTypeName: "Application",
                        PropertyAccess: VbaPropertyAccess.Readable,
                        GlobalExposure: ReferenceDefinitionGlobalExposure.MainHostGlobal),
                    new VbaProjectReferenceDefinition(
                        "Microsoft Excel 16.0 Object Library",
                        "ActiveWorkbook",
                        VbaSourceDefinitionKind.Property,
                        "Returns the workbook in the active window.",
                        ParentTypeName: "Application",
                        TypeReference: new VbaTypeReference("Workbook", "Excel"),
                        PropertyAccess: VbaPropertyAccess.Readable,
                        GlobalExposure: ReferenceDefinitionGlobalExposure.MainHostGlobal),
                    new VbaProjectReferenceDefinition(
                        "Microsoft Excel 16.0 Object Library",
                        "ThisWorkbook",
                        VbaSourceDefinitionKind.Property,
                        "Returns the workbook containing the current macro code.",
                        ParentTypeName: "Application",
                        TypeReference: new VbaTypeReference("Workbook", "Excel"),
                        PropertyAccess: VbaPropertyAccess.Readable,
                        GlobalExposure: ReferenceDefinitionGlobalExposure.MainHostGlobal),
                    new VbaProjectReferenceDefinition(
                        "Microsoft Excel 16.0 Object Library",
                        "XlHAlign",
                        VbaSourceDefinitionKind.Enum,
                        "Specifies horizontal alignment."),
                    new VbaProjectReferenceDefinition(
                        "Microsoft Excel 16.0 Object Library",
                        "xlCenter",
                        VbaSourceDefinitionKind.EnumMember,
                        "Centers content horizontally.",
                        ParentTypeName: "XlHAlign",
                        TypeReference: new VbaTypeReference("Long"),
                        GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal),
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
                        PropertyAccess: VbaPropertyAccess.Readable,
                        GlobalExposure: ReferenceDefinitionGlobalExposure.MainHostGlobal),
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
                            CallableKind: VbaCallableKind.Function,
                            SupportsNamedArguments: true),
                        ParentTypeName: "Application",
                        GlobalExposure: ReferenceDefinitionGlobalExposure.MainHostGlobal),
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
                            CallableKind: VbaCallableKind.Function,
                            SupportsNamedArguments: true),
                        ParentTypeName: "Workbooks",
                        TypeReference: new VbaTypeReference("Workbook", "Excel")),
                    new VbaProjectReferenceDefinition(
                        "Microsoft Excel 16.0 Object Library",
                        "Name",
                        VbaSourceDefinitionKind.Property,
                        "Returns the workbook name.",
                        ParentTypeName: "Workbook",
                        TypeReference: new VbaTypeReference("String"),
                        PropertyAccess: VbaPropertyAccess.Readable),
                    new VbaProjectReferenceDefinition(
                        "Microsoft Excel 16.0 Object Library",
                        "WorkbookOpen",
                        VbaSourceDefinitionKind.Event,
                        "Occurs when a workbook is opened.",
                        new VbaCallableSignature(
                            "WorkbookOpen(Wb)",
                            [
                                new VbaCallableParameter(
                                    "Wb",
                                    "The opened workbook.",
                                    TypeReference: new VbaTypeReference(
                                        "Workbook",
                                        "Excel"),
                                    IsByRef: false)
                            ],
                            "Occurs when a workbook is opened.",
                            CallableKind: VbaCallableKind.Event,
                            SupportsNamedArguments: true),
                        ParentTypeName: "Application",
                        GlobalExposure: ReferenceDefinitionGlobalExposure.MainHostGlobal)
                ],
                [
                    new TypeLibCatalogType(
                        "Application",
                        VbaSourceDefinitionKind.Class,
                        "Represents the Microsoft Excel application.",
                        Members: [],
                        IsCreatable: true,
                        IsApplicationObject: true,
                        Metadata: new TypeLibCatalogTypeMetadata(
                            TypeLibCatalogRawTypeKind.CoClass,
                            TypeFlags: 0,
                            ImplementedInterfaces:
                            [
                                new TypeLibCatalogImplementedInterface(
                                    "AppEvents",
                                    TypeFlags: 0,
                                    ImplementationFlags: 0x1 | 0x2,
                                    CallableMembers:
                                    [
                                        new TypeLibCatalogMember(
                                            "WorkbookOpen",
                                            VbaSourceDefinitionKind.Event,
                                            "Occurs when a workbook is opened.",
                                            new VbaCallableSignature(
                                                "WorkbookOpen(Wb)",
                                                [
                                                    new VbaCallableParameter(
                                                        "Wb",
                                                        "The opened workbook.",
                                                        TypeReference:
                                                            new VbaTypeReference(
                                                                "Workbook",
                                                                "Excel"),
                                                        IsByRef: false)
                                                ],
                                                "Occurs when a workbook is opened.",
                                                CallableKind: VbaCallableKind.Event,
                                                SupportsNamedArguments: true),
                                             Metadata: new TypeLibCatalogCallableMetadata(
                                                 MemberId: 1,
                                                 FunctionFlags: 0))
                                    ],
                                    RawTypeKind:
                                        TypeLibCatalogRawTypeKind.Dispatch,
                                    IsComplete: false)
                            ]))
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
                            CallableKind: VbaCallableKind.Function,
                            SupportsNamedArguments: true),
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
            bundledCatalogs
                .Select(catalog => catalog with
                {
                    ReferencedVbaProjectName = catalog.ReferenceName switch
                    {
                        StandardLibraryReferenceName => "VBA",
                        "Microsoft Excel 16.0 Object Library" => "Excel",
                        "Microsoft Scripting Runtime" => "Scripting",
                        "Microsoft Office 16.0 Object Library" => "Office",
                        "Microsoft Outlook 16.0 Object Library" => "Outlook",
                        _ => null
                    }
                })
                .Select(VbaSemanticFacts.CaptureCatalog)
                .ToDictionary(
                catalog => catalog.ReferenceName,
                VbaReferenceName.Comparer));
    }

    internal VbaProjectReferenceCatalog? FindCatalog(string referenceName)
        => catalogs.TryGetValue(referenceName, out var catalog)
            ? catalog
            : null;

    /// <summary>
    /// Gets the reference names that currently have catalogs.
    /// </summary>
    public IReadOnlyList<string> ReferenceNames
        => catalogs.Keys
            .OrderBy(referenceName => referenceName, VbaReferenceName.OrderingComparer)
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
        var merged = new Dictionary<string, VbaProjectReferenceCatalog>(catalogs, VbaReferenceName.Comparer)
        {
            [catalog.ReferenceName] = VbaSemanticFacts.CaptureCatalog(catalog)
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
    public IReadOnlyList<VbaSourceDefinition> GetActiveDefinitions(VbaReferenceSelection? selection)
        => GetActiveReferenceDefinitions(selection)
            .Select(ToSourceDefinition)
            .ToArray();

    /// <summary>
    /// Gets selected reference names that do not currently have catalogs.
    /// </summary>
    /// <param name="selection">The active reference selection.</param>
    /// <returns>The missing reference names ordered for deterministic reporting.</returns>
    public IReadOnlyList<string> GetMissingCatalogReferenceNames(VbaReferenceSelection selection)
    {
        return selection.References
            .Where(reference => !catalogs.ContainsKey(reference.Name))
            .Select(reference => reference.Name)
            .Distinct(VbaReferenceName.Comparer)
            .OrderBy(name => name, VbaReferenceName.OrderingComparer)
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
        VbaReferenceSelection? selection,
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
        VbaReferenceSelection? selection,
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
        VbaReferenceSelection? selection,
        string referenceName,
        string qualifier)
    {
        return GetActiveCatalogs(selection)
            .Where(catalog => VbaReferenceName.AreEquivalent(
                catalog.Catalog.ReferenceName,
                referenceName))
            .SelectMany(catalog => catalog.Catalog.QualifierAliases)
            .FirstOrDefault(alias => alias.Equals(qualifier, StringComparison.OrdinalIgnoreCase));
    }

    internal IReadOnlyList<(string ReferenceName, string Qualifier)> GetActiveQualifierAliases(
        VbaReferenceSelection? selection)
        => GetActiveCatalogs(selection)
            .SelectMany(catalog => catalog.Catalog.QualifierAliases.Select(alias => (
                catalog.Catalog.ReferenceName,
                Qualifier: alias)))
            .ToArray();

    internal VbaTypeLibEventSurface GetTypeLibEventSurface(
        string referenceName,
        string typeName)
    {
        if (!catalogs.TryGetValue(referenceName, out var catalog)
            || catalog.TypeLibTypes is null
            || catalog.TypeLibTypes.Any(type =>
                type is null || string.IsNullOrEmpty(type.Name)))
        {
            return VbaTypeLibEventSurface.Indeterminate;
        }

        var matchingTypes = catalog.TypeLibTypes
            .Where(type => type.Name.Equals(
                typeName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matchingTypes.Length != 1
            || matchingTypes[0].Metadata is null)
        {
            return VbaTypeLibEventSurface.Indeterminate;
        }

        var metadata = matchingTypes[0].Metadata!;
        if (!metadata.IsComplete
            || metadata.ImplementedInterfaces is null
            || metadata.ImplementedInterfaces.Any(implemented =>
                implemented is null
                || string.IsNullOrEmpty(implemented.Name)
                || implemented.RawTypeKind is not (
                    TypeLibCatalogRawTypeKind.Interface
                        or TypeLibCatalogRawTypeKind.Dispatch)))
        {
            return VbaTypeLibEventSurface.Indeterminate;
        }

        if (metadata.RawTypeKind != TypeLibCatalogRawTypeKind.CoClass)
        {
            return new VbaTypeLibEventSurface(
                VbaTypeLibEventSurfaceState.Complete,
                metadata.RawTypeKind,
                metadata.TypeFlags,
                StructuralEvents: []);
        }

        const int defaultSourceFlags = 0x1 | 0x2;
        var defaultSources = metadata.ImplementedInterfaces
            .Where(implemented =>
                (implemented.ImplementationFlags & defaultSourceFlags)
                    == defaultSourceFlags)
            .ToArray();
        if (defaultSources.Length > 1)
        {
            return VbaTypeLibEventSurface.Indeterminate;
        }

        if (defaultSources.Length == 0)
        {
            return new VbaTypeLibEventSurface(
                VbaTypeLibEventSurfaceState.Complete,
                metadata.RawTypeKind,
                metadata.TypeFlags,
                StructuralEvents: []);
        }

        var defaultSource = defaultSources[0];
        if (defaultSource.RawTypeKind is not (
                TypeLibCatalogRawTypeKind.Interface
                    or TypeLibCatalogRawTypeKind.Dispatch))
        {
            return VbaTypeLibEventSurface.Indeterminate;
        }

        if (string.IsNullOrEmpty(defaultSource.Name)
            || defaultSource.CallableMembers is null)
        {
            return VbaTypeLibEventSurface.Indeterminate;
        }

        var completeCallableMembers = defaultSource.CallableMembers
            .Where(IsCompleteTypeLibCallable)
            .ToArray();
        var hasIncompleteCallableEvidence = !defaultSource.IsComplete
            || completeCallableMembers.Length
                != defaultSource.CallableMembers.Count;
        var callableGroups = completeCallableMembers
            .GroupBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (callableGroups.Any(group => group
            .Skip(1)
            .Any(member => !HaveEquivalentTypeLibCallableContracts(
                group.First(),
                member))))
        {
            return VbaTypeLibEventSurface.Indeterminate;
        }

        var callableMembers = callableGroups
            .Select(group => group.First())
            .ToArray();
        if (hasIncompleteCallableEvidence)
        {
            return new VbaTypeLibEventSurface(
                callableMembers.Length == 0
                    ? VbaTypeLibEventSurfaceState.Indeterminate
                    : VbaTypeLibEventSurfaceState.Partial,
                metadata.RawTypeKind,
                metadata.TypeFlags,
                StructuralEvents: [],
                PartialExistingHandlerRecognitionEvents: callableMembers);
        }

        return new VbaTypeLibEventSurface(
            VbaTypeLibEventSurfaceState.Complete,
            metadata.RawTypeKind,
            metadata.TypeFlags,
            callableMembers);
    }

    private static bool IsCompleteTypeLibCallable(TypeLibCatalogMember? member)
    {
        if (member is null
            || string.IsNullOrEmpty(member.Name)
            || member.Metadata?.IsComplete != true
            || member.Signature is not { Parameters: { } parameters } signature
            || parameters.Any(parameter =>
                parameter is null
                || !HasCompleteTypeReference(parameter.TypeReference)))
        {
            return false;
        }

        var hasResult = signature.CallableKind == VbaCallableKind.Function
            || (signature.CallableKind == VbaCallableKind.Property
                && member.Metadata.PropertyAccessorKind
                    == VbaPropertyAccessorKind.Get);
        return !hasResult
            || (member.TypeReference is not null
                && HasCompleteTypeReference(member.TypeReference)
                && member.Metadata.IsReturnArray is not null);
    }

    private static bool HasCompleteTypeReference(VbaTypeReference? typeReference)
        => typeReference is not null
            && !string.IsNullOrEmpty(typeReference.Name);

    private static bool HaveEquivalentTypeLibCallableContracts(
        TypeLibCatalogMember left,
        TypeLibCatalogMember right)
        => HaveEquivalentTypeLibCallableMetadata(left.Metadata, right.Metadata)
            && left.Kind == right.Kind
            && HaveEquivalentTypeReferences(left.TypeReference, right.TypeReference)
            && HaveEquivalentTypeLibCallableSignatures(left.Signature, right.Signature);

    private static bool HaveEquivalentTypeLibCallableMetadata(
        TypeLibCatalogCallableMetadata? left,
        TypeLibCatalogCallableMetadata? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.MemberId == right.MemberId
            && left.FunctionFlags == right.FunctionFlags
            && left.IsComplete == right.IsComplete
            && left.IsReturnArray == right.IsReturnArray
            && left.PropertyAccessorKind == right.PropertyAccessorKind;
    }

    private static bool HaveEquivalentTypeLibCallableSignatures(
        VbaCallableSignature? left,
        VbaCallableSignature? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left.CallableKind != right.CallableKind
            || left.PassingConvention != right.PassingConvention
            || left.Parameters is null
            || right.Parameters is null
            || left.Parameters.Count != right.Parameters.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Parameters.Count; index++)
        {
            var leftParameter = left.Parameters[index];
            var rightParameter = right.Parameters[index];
            if (leftParameter is null
                || rightParameter is null
                || leftParameter.IsOptional != rightParameter.IsOptional
                || leftParameter.IsByRef != rightParameter.IsByRef
                || leftParameter.TypeLibPassing != rightParameter.TypeLibPassing
                || leftParameter.IsParamArray != rightParameter.IsParamArray
                || leftParameter.IsArray != rightParameter.IsArray
                || !HaveEquivalentTypeReferences(
                    leftParameter.TypeReference,
                    rightParameter.TypeReference))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HaveEquivalentTypeReferences(
        VbaTypeReference? left,
        VbaTypeReference? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                left.Qualifier,
                right.Qualifier,
                StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<VbaProjectReferenceDefinition> GetActiveReferenceDefinitions(VbaReferenceSelection? selection)
        => GetActiveCatalogs(selection).SelectMany(catalog => catalog.Catalog.Definitions);

    private IEnumerable<ActiveReferenceCatalog> GetActiveCatalogs(VbaReferenceSelection? selection)
    {
        if (catalogs.TryGetValue(StandardLibraryReferenceName, out var standardLibraryCatalog))
        {
            yield return new ActiveReferenceCatalog(StandardLibraryReferenceName, standardLibraryCatalog);
        }

        if (selection is null)
        {
            yield break;
        }

        foreach (var reference in selection.References)
        {
            if (VbaReferenceName.AreEquivalent(
                    reference.Name,
                    StandardLibraryReferenceName))
            {
                continue;
            }

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
                definition.Name,
                definition.PropertyAccessorKind),
            Location: location,
            Name: definition.Name,
            Kind: definition.Kind,
            Visibility: VbaSourceDefinitionVisibility.Public,
            ModuleName: definition.ReferenceName,
            Documentation: definition.Documentation,
            Signature: signature,
            ParentTypeName: definition.ParentTypeName,
            TypeReference: definition.TypeReference,
            DeclarationLabel: signature?.Label ?? CreateDeclarationLabel(definition),
            PropertyAccess: definition.PropertyAccess,
            PropertyAccessorKind: definition.PropertyAccessorKind,
            IsCreatable: definition.IsCreatable,
            ReferenceGlobalExposure: definition.GlobalExposure,
            CallableKind: definition.CallableKind ?? signature?.CallableKind,
            IsAuthoringAvailable: definition.IsAuthoringAvailable,
            IsCallableMetadataComplete: definition.IsCallableMetadataComplete)
        {
            IsReturnArray = definition.IsReturnArray,
            IsDefaultMember = definition.IsDefaultMember
        };
    }

    private static VbaCallableSignature? CreateSourceSignature(VbaProjectReferenceDefinition definition)
    {
        if (definition.Signature is null)
        {
            return null;
        }

        var kind = definition.Kind switch
        {
            VbaSourceDefinitionKind.Property => VbaCallableKind.Property,
            VbaSourceDefinitionKind.Event => VbaCallableKind.Event,
            _ => definition.CallableKind ?? definition.Signature.CallableKind
        };
        return VbaCallableSignaturePresentation.Assemble(
            new VbaCallablePresentationShape(definition.Name, kind, definition.TypeReference,
                IsReturnArray: definition.IsReturnArray),
            definition.Signature);
    }

    private static string? GetCallableKindLabel(VbaProjectReferenceDefinition definition)
        => definition.Kind switch
        {
            VbaSourceDefinitionKind.Property => "Property",
            VbaSourceDefinitionKind.Event => "Event",
            VbaSourceDefinitionKind.Procedure => definition.Signature?.CallableKind?.ToString(),
            _ => null
        };

    private static string? CreateDeclarationLabel(
        VbaProjectReferenceDefinition definition)
        => definition.Kind switch
        {
            VbaSourceDefinitionKind.Procedure => CreateCallableDeclarationLabel(definition),
            VbaSourceDefinitionKind.Property when definition.GlobalExposure == ReferenceDefinitionGlobalExposure.MainHostGlobal =>
                CreateValueLabel(definition),
            VbaSourceDefinitionKind.Property => $"Property {CreateValueLabel(definition)}",
            VbaSourceDefinitionKind.Event => $"Event {definition.Name}()",
            VbaSourceDefinitionKind.Variable => CreateValueLabel(definition),
            VbaSourceDefinitionKind.Constant => $"Const {CreateValueLabel(definition)}",
            VbaSourceDefinitionKind.Enum => $"Enum {definition.Name}",
            VbaSourceDefinitionKind.Type => $"Type {definition.Name}",
            VbaSourceDefinitionKind.EnumMember or VbaSourceDefinitionKind.TypeMember => CreateValueLabel(definition),
            _ => null
        };

    private static string CreateCallableDeclarationLabel(
        VbaProjectReferenceDefinition definition)
    {
        var callableKind = GetCallableKindLabel(definition);
        var callableLabel = CreateValueLabel(definition);
        return callableKind is null ? callableLabel : $"{callableKind} {callableLabel}";
    }

    private static string CreateValueLabel(VbaProjectReferenceDefinition definition)
        => definition.TypeReference is null
            ? definition.Name
            : $"{definition.Name} As {definition.TypeReference.Name}";

    private sealed record ActiveReferenceCatalog(string ManifestReferenceName, VbaProjectReferenceCatalog Catalog);
}
