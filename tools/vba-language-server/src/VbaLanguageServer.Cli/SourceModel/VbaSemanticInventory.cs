using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaTools.Syntax;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Owns project-scope semantic lookup structures shaped around editor query patterns.
/// </summary>
public sealed class VbaSemanticInventory
{
    private readonly IReadOnlyList<VbaSourceDocument> sourceDocuments;
    private readonly VbaNameCandidateInventory definitionCandidates;
    private readonly VbaResolutionPolicy resolutionPolicy;
    private readonly VbaSemanticResolution semanticResolution;
    private readonly VbaResolvedIdentifierOccurrenceIndex resolvedOccurrences;
    private readonly object projectValidationGate = new();
    private VbaSemanticDiagnosticIndex? projectValidationDiagnostics;
    private readonly VbaSourceFormatter sourceFormatter;
    private readonly VbaProjectReferenceSelection? referenceSelection;
    private readonly VbaProjectReferenceCatalogSet referenceCatalogs;
    private readonly IReadOnlyDictionary<string, VbaProjectReferenceCatalogSource>
        referenceCatalogSources;
    private readonly IReadOnlyDictionary<string, VbaProjectReferenceCatalogIdentity>
        referenceCatalogIdentities;
    private readonly VbaProjectResolution? projectResolution;
    private readonly IReadOnlyDictionary<string, string>
        authoritativeReferencedProjectNames;
    private readonly object semanticTokenCacheGate = new();
    private readonly Dictionary<
        VbaDocumentIdentity,
        IReadOnlyList<VbaSemanticToken>> semanticTokenCache = [];
    private readonly Dictionary<
        VbaDocumentIdentity,
        IReadOnlyList<int>> semanticTokenDataCache = [];
    private readonly VbaIntrinsicHostEventCatalog? intrinsicHostEventCatalog;
    private readonly string? validationActiveUri;
    private readonly IVbaProjectSnapshotBuildObserver? validationBuildObserver;

    /// <summary>
    /// Detaches immutable analysis and cancellation-safe semantic caches from
    /// the validation lifecycle before bounded inactive retention.
    /// </summary>
    internal VbaSemanticInventory CreateForRetainedAnalysis()
        => new(this, validationActiveUri: null, validationBuildObserver: null);

    /// <summary>
    /// Starts a fresh validation lifecycle after the snapshot provider proves
    /// equality of every current semantic input with retained analysis.
    /// </summary>
    internal VbaSemanticInventory CreateFromRetainedAnalysis(
        string activeUri,
        IVbaProjectSnapshotBuildObserver observer)
        => new(this, activeUri, observer);

    /// <summary>
    /// Estimates the complete retained analysis footprint, including capacity
    /// for occurrence, token, and effective declared type caches that have not
    /// been requested yet. Definition and parameter allowances reserve each
    /// declared type result, lazy publication, identity, and display strings.
    /// Shared objects are deliberately charged to every retained scope. This
    /// is a conservative admission estimate, not a CLR heap measurement.
    /// </summary>
    internal long EstimateRetainedAnalysisBytes()
    {
        long bytes = 16 * 1024;
        foreach (var document in sourceDocuments)
        {
            if (document.SyntaxTree is not { } tree)
            {
                // There is no bounded syntax inventory for this projection.
                return long.MaxValue;
            }

            var module = tree.Module;
            var syntaxNodeCount = (long)module.Attributes.Count
                + module.Options.Count + module.Members.Count
                + module.Declarations.Count + module.CallableDeclarations.Count
                + module.Statements.Count + module.Expressions.Count
                + module.ArgumentLists.Count + module.Blocks.Count
                + module.LineLabels.Count + module.PreprocessorDirectives.Count
                + module.PreprocessorBlocks.Count
                + module.ImplementsRelationships.Count
                + module.DefTypeDirectives.Count + tree.Diagnostics.Count;
            bytes += 4096 + EstimateRetainedTextBytes(document.Text)
                + EstimateRetainedTextBytes(document.Uri)
                + syntaxNodeCount * 512;
            // Reserve lexical/range indexes, two resolved occurrences and
            // forward/reverse maps, token objects and encoded LSP integers
            // for every token, independently of which lazy shards are warm.
            bytes += (long)tree.TokenStream.Tokens.Count * 512;
            foreach (var definition in document.Definitions)
            {
                bytes += EstimateRetainedDefinitionBytes(definition);
            }
        }

        foreach (var candidate in definitionCandidates.GetReferenceCandidates(null))
        {
            bytes += EstimateRetainedDefinitionBytes(candidate.Definition);
        }

        // The captured catalog set retains inactive catalogs and structural
        // TypeLib Event metadata as well as projected active definitions.
        foreach (var name in referenceCatalogs.ReferenceNames)
        {
            var catalog = referenceCatalogs.FindCatalog(name)!;
            bytes += 4096 + EstimateRetainedTextBytes(name)
                + EstimateRetainedTextBytes(catalog.ReferencedVbaProjectName);
            foreach (var qualifier in catalog.QualifierAliases)
            {
                bytes += 256 + EstimateRetainedTextBytes(qualifier);
            }

            foreach (var definition in catalog.Definitions)
            {
                bytes += 2048L * (1 + catalog.QualifierAliases.Count)
                    + EstimateRetainedTextBytes(definition.Name)
                    + EstimateRetainedTextBytes(definition.Documentation)
                    + EstimateRetainedTextBytes(definition.ParentTypeName)
                    + EstimateRetainedTypeBytes(definition.TypeReference)
                    + EstimateRetainedSignatureBytes(definition.Signature);
            }

            foreach (var type in catalog.TypeLibTypes ?? [])
            {
                bytes += 2048 + EstimateRetainedTextBytes(type.Name)
                    + EstimateRetainedTextBytes(type.Documentation);
                foreach (var member in type.Members)
                {
                    bytes += EstimateRetainedCatalogMemberBytes(member);
                }

                foreach (var relationship in type.Metadata?.ImplementedInterfaces ?? [])
                {
                    bytes += 1024 + EstimateRetainedTextBytes(relationship.Name);
                    foreach (var member in relationship.CallableMembers)
                    {
                        bytes += EstimateRetainedCatalogMemberBytes(member);
                    }
                }
            }
        }

        if (intrinsicHostEventCatalog is { } hostCatalog)
        {
            bytes += 4096 + EstimateRetainedTextBytes(hostCatalog.IntrinsicEventSourceName);
            foreach (var hostEvent in hostCatalog.Events)
            {
                bytes += 2048 + EstimateRetainedTextBytes(hostEvent.Name)
                    + EstimateRetainedTextBytes(hostEvent.Documentation);
                foreach (var parameter in hostEvent.Parameters)
                {
                    bytes += 1024 + EstimateRetainedTextBytes(parameter.Name)
                        + (parameter.Type switch
                        {
                            VbaIntrinsicHostEventParameterType intrinsic =>
                                EstimateRetainedTextBytes(intrinsic.Name),
                            VbaTypeLibraryHostEventParameterType library =>
                                EstimateRetainedTextBytes(library.Name)
                                    + EstimateRetainedTextBytes(library.LibraryGuid),
                            VbaUnresolvedHostEventParameterType unresolved =>
                                EstimateRetainedTextBytes(unresolved.DisplayName),
                            _ => 0
                        });
                }
            }
        }

        return bytes;
    }

    private static long EstimateRetainedDefinitionBytes(VbaSourceDefinition definition)
        => 2048 + EstimateRetainedTextBytes(definition.Name)
            + EstimateRetainedTextBytes(definition.Uri)
            + EstimateRetainedTextBytes(definition.ModuleName)
            + EstimateRetainedTextBytes(definition.ParentProcedureName)
            + EstimateRetainedTextBytes(definition.ParentTypeName)
            + EstimateRetainedTextBytes(definition.Documentation)
            + EstimateRetainedTextBytes(definition.DeclarationLabel)
            + EstimateRetainedTypeBytes(definition.TypeReference)
            + EstimateRetainedSignatureBytes(definition.Signature)
            + (definition.ConditionalCompilationPath?.Branches.Count ?? 0) * 128L;

    private static long EstimateRetainedCatalogMemberBytes(TypeLibCatalogMember member)
        => 2048 + EstimateRetainedTextBytes(member.Name)
            + EstimateRetainedTextBytes(member.Documentation)
            + EstimateRetainedTypeBytes(member.TypeReference)
            + EstimateRetainedSignatureBytes(member.Signature);

    private static long EstimateRetainedSignatureBytes(VbaCallableSignature? signature)
        => signature is null
            ? 0
            : 512 + EstimateRetainedTextBytes(signature.Label)
                + EstimateRetainedTextBytes(signature.Documentation)
                + signature.Parameters.Sum(parameter =>
                    1024 + EstimateRetainedTextBytes(parameter.Name)
                        + EstimateRetainedTextBytes(parameter.Documentation)
                        + EstimateRetainedTextBytes(parameter.DisplayLabel)
                        + EstimateRetainedTextBytes(parameter.DefaultExpression)
                        + EstimateRetainedTypeBytes(parameter.TypeReference));

    private static long EstimateRetainedTypeBytes(VbaTypeReference? type)
        => type is null
            ? 0
            : 128 + EstimateRetainedTextBytes(type.Name)
                + EstimateRetainedTextBytes(type.Qualifier);

    private static long EstimateRetainedTextBytes(string? text)
        => text is null ? 0 : 32 + text.Length * 8L;

    private VbaSemanticInventory(
        VbaSemanticInventory retained,
        string? validationActiveUri,
        IVbaProjectSnapshotBuildObserver? validationBuildObserver)
    {
        sourceDocuments = retained.sourceDocuments;
        definitionCandidates = retained.definitionCandidates;
        resolutionPolicy = retained.resolutionPolicy;
        semanticResolution = retained.semanticResolution;
        resolvedOccurrences = retained.resolvedOccurrences;
        sourceFormatter = retained.sourceFormatter;
        referenceSelection = retained.referenceSelection;
        referenceCatalogs = retained.referenceCatalogs;
        referenceCatalogSources = retained.referenceCatalogSources;
        referenceCatalogIdentities = retained.referenceCatalogIdentities;
        projectResolution = retained.projectResolution;
        authoritativeReferencedProjectNames =
            retained.authoritativeReferencedProjectNames;
        intrinsicHostEventCatalog = retained.intrinsicHostEventCatalog;
        semanticTokenCacheGate = retained.semanticTokenCacheGate;
        semanticTokenCache = retained.semanticTokenCache;
        semanticTokenDataCache = retained.semanticTokenDataCache;
        this.validationActiveUri = validationActiveUri;
        this.validationBuildObserver = validationBuildObserver;
    }

    private VbaSemanticInventory(
        IReadOnlyList<VbaSourceDocument> sourceDocuments,
        VbaNameCandidateInventory definitionCandidates,
        VbaProjectReferenceSelection? referenceSelection,
        VbaProjectReferenceCatalogSet referenceCatalogs,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogSource>
            referenceCatalogSources,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogIdentity>
            referenceCatalogIdentities,
        VbaProjectResolution? projectResolution,
        IReadOnlyDictionary<string, string> authoritativeReferencedProjectNames,
        VbaIntrinsicHostEventCatalog? intrinsicHostEventCatalog,
        string? validationActiveUri,
        IVbaProjectSnapshotBuildObserver? validationBuildObserver,
        CancellationToken cancellationToken)
    {
        this.sourceDocuments = sourceDocuments;
        this.definitionCandidates = definitionCandidates;
        resolutionPolicy = new VbaResolutionPolicy(
            definitionCandidates.ConditionalFamilies);
        this.referenceSelection = referenceSelection;
        this.referenceCatalogs = referenceCatalogs;
        this.referenceCatalogSources = referenceCatalogSources;
        this.referenceCatalogIdentities = referenceCatalogIdentities;
        this.projectResolution = projectResolution;
        this.authoritativeReferencedProjectNames =
            authoritativeReferencedProjectNames;
        this.intrinsicHostEventCatalog = intrinsicHostEventCatalog;
        this.validationActiveUri = validationActiveUri;
        this.validationBuildObserver = validationBuildObserver;
        semanticResolution = new VbaSemanticResolution(
            definitionCandidates,
            resolutionPolicy,
            referenceCatalogIdentities,
            intrinsicHostEventCatalog);
        resolvedOccurrences = new VbaResolvedIdentifierOccurrenceIndex(
            sourceDocuments,
            semanticResolution.ResolveSourceTarget);
        cancellationToken.ThrowIfCancellationRequested();
        sourceFormatter = new VbaSourceFormatter(
            semanticResolution,
            resolvedOccurrences);
    }

    /// <summary>
    /// Creates a semantic inventory from projected source documents and active reference metadata.
    /// </summary>
    public static VbaSemanticInventory Create(
        IReadOnlyDictionary<string, VbaSourceDocument> sourceDocuments,
        VbaProjectReferenceSelection? referenceSelection = null,
        VbaProjectReferenceCatalogSet? referenceCatalogs = null,
        VbaIntrinsicHostEventCatalog? intrinsicHostEventCatalog = null,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogSource>?
            referenceCatalogSources = null,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogIdentity>?
            referenceCatalogIdentities = null,
        VbaProjectResolution? projectResolution = null,
        IReadOnlyDictionary<string, string>?
            authoritativeReferencedProjectNames = null)
        => CreateCore(
            sourceDocuments,
            referenceSelection,
            referenceCatalogs,
            intrinsicHostEventCatalog,
            referenceCatalogSources,
            referenceCatalogIdentities,
            projectResolution,
            authoritativeReferencedProjectNames,
            validationActiveUri: null,
            validationBuildObserver: null,
            CancellationToken.None);

    internal static VbaSemanticInventory CreateForProjectSnapshot(
        IReadOnlyDictionary<string, VbaSourceDocument> sourceDocuments,
        VbaProjectReferenceSelection? referenceSelection,
        VbaProjectReferenceCatalogSet? referenceCatalogs,
        VbaIntrinsicHostEventCatalog? intrinsicHostEventCatalog,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogSource>?
            referenceCatalogSources,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogIdentity>?
            referenceCatalogIdentities,
        VbaProjectResolution? projectResolution,
        IReadOnlyDictionary<string, string>?
            authoritativeReferencedProjectNames,
        string validationActiveUri,
        IVbaProjectSnapshotBuildObserver validationBuildObserver,
        CancellationToken cancellationToken)
        => CreateCore(
            sourceDocuments,
            referenceSelection,
            referenceCatalogs,
            intrinsicHostEventCatalog,
            referenceCatalogSources,
            referenceCatalogIdentities,
            projectResolution,
            authoritativeReferencedProjectNames,
            validationActiveUri,
            validationBuildObserver,
            cancellationToken);

    private static VbaSemanticInventory CreateCore(
        IReadOnlyDictionary<string, VbaSourceDocument> sourceDocuments,
        VbaProjectReferenceSelection? referenceSelection,
        VbaProjectReferenceCatalogSet? referenceCatalogs,
        VbaIntrinsicHostEventCatalog? intrinsicHostEventCatalog,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogSource>?
            referenceCatalogSources,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogIdentity>?
            referenceCatalogIdentities,
        VbaProjectResolution? projectResolution,
        IReadOnlyDictionary<string, string>?
            authoritativeReferencedProjectNames,
        string? validationActiveUri,
        IVbaProjectSnapshotBuildObserver? validationBuildObserver,
        CancellationToken cancellationToken)
    {
        var documents = FreezeList(
            sourceDocuments.Values.Select(CaptureDocument));
        var capturedReferenceSelection = CaptureReferenceSelection(referenceSelection);
        var semanticInputs = VbaProjectSemanticInputs.Capture(capturedReferenceSelection.ToSemanticSelection(),
            referenceCatalogs ?? VbaProjectReferenceCatalogSet.Empty, intrinsicHostEventCatalog,
            referenceCatalogIdentities, referenceCatalogSources);
        var catalogs = semanticInputs.ReferenceCatalogs;
        var capturedCatalogSources = semanticInputs.ReferenceCatalogSources;
        var capturedCatalogIdentities = semanticInputs.ReferenceCatalogIdentities;
        var capturedProjectResolution = projectResolution is null
            ? null
            : projectResolution with
            {
                References = projectResolution.ReferenceEntries.ToArray(),
                CommonModules = projectResolution.InstalledCommonModuleEntries
                    .ToArray()
            };
        var capturedAuthoritativeReferencedProjectNames =
            authoritativeReferencedProjectNames is null
                ? new Dictionary<string, string>(VbaProjectReferenceName.Comparer)
                : new Dictionary<string, string>(
                    authoritativeReferencedProjectNames,
                    VbaProjectReferenceName.Comparer);
        var activeReferenceDefinitions = FreezeList(
            catalogs
                .GetActiveDefinitions(capturedReferenceSelection.ToSemanticSelection())
                .Select(CaptureDefinition));
        var definitionCandidates = new VbaNameCandidateInventory(
            documents,
            capturedReferenceSelection.ToSemanticSelection(),
            catalogs,
            activeReferenceDefinitions,
            capturedCatalogSources);
        return new VbaSemanticInventory(
            documents,
            definitionCandidates,
            capturedReferenceSelection,
            catalogs,
            capturedCatalogSources,
            capturedCatalogIdentities,
            capturedProjectResolution,
            capturedAuthoritativeReferencedProjectNames,
            semanticInputs.IntrinsicHostEvents,
            validationActiveUri,
            validationBuildObserver,
            cancellationToken);
    }

    /// <summary>
    /// Gets the immutable environment-scoped intrinsic host Event catalog.
    /// </summary>
    public VbaIntrinsicHostEventCatalog? IntrinsicHostEventCatalog
        => intrinsicHostEventCatalog;

    /// <summary>
    /// Gets definitions declared in a document.
    /// </summary>
    public IReadOnlyList<VbaSourceDefinition> GetDocumentDefinitions(string uri)
        => definitionCandidates.GetDocumentDefinitions(uri);

    internal IReadOnlyList<VbaProjectValidationDiagnostic>
        GetProjectValidationDiagnostics(
            string uri,
            VbaProjectIdentityReadResult? projectIdentityRead = null,
            CancellationToken cancellationToken = default)
    {
        var diagnostics = GetOrCreateProjectValidationDiagnostics(
                cancellationToken)
            .GetDiagnostics(uri)
            .Select(diagnostic => new VbaProjectValidationDiagnostic(diagnostic.Code, diagnostic.Message,
                diagnostic.Range, diagnostic.Severity, Details: diagnostic.Details)).ToArray();
        var moduleIdentityDiagnostics =
            CreateModuleIdentityNameConflictDiagnostics(
                uri,
                projectIdentityRead,
                cancellationToken);
        return moduleIdentityDiagnostics.Count == 0
            ? diagnostics
            : diagnostics.Concat(moduleIdentityDiagnostics).ToArray();
    }

    private VbaSemanticDiagnosticIndex
        GetOrCreateProjectValidationDiagnostics(
            CancellationToken cancellationToken)
    {
        lock (projectValidationGate)
        {
            if (projectValidationDiagnostics is not null)
            {
                return projectValidationDiagnostics;
            }

            if (validationBuildObserver is not null
                && validationActiveUri is not null)
            {
                validationBuildObserver.BeforeBuildProjectValidation(
                    validationActiveUri,
                    cancellationToken);
            }

            try
            {
                projectValidationDiagnostics =
                    new VbaSemanticDiagnosticIndex(
                        sourceDocuments,
                        semanticResolution.Core,
                        cancellationToken);
                return projectValidationDiagnostics;
            }
            finally
            {
                if (validationBuildObserver is not null
                    && validationActiveUri is not null)
                {
                    validationBuildObserver.AfterBuildProjectValidation(
                        validationActiveUri);
                }
            }
        }
    }

    private IReadOnlyList<VbaProjectValidationDiagnostic>
        CreateModuleIdentityNameConflictDiagnostics(
            string uri,
            VbaProjectIdentityReadResult? projectIdentityRead,
            CancellationToken cancellationToken)
    {
        if (projectResolution is null
            || projectResolution.Kind == VbaProjectResolutionKind.ManifestDocument
                && projectIdentityRead?.Identity is null)
        {
            return [];
        }

        var references = new List<VbaReferencedProjectNamespace>();
        foreach (var referenceName in GetActiveReferenceNamesInSelectionOrder())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetCurrentReferencedProjectName(referenceName, out var projectName))
            {
                return [];
            }
            references.Add(new(referenceName, projectName));
        }
        var namespaces = VbaProjectNamespaceIdentity.Capture(
            projectResolution.Kind == VbaProjectResolutionKind.ManifestDocument
                ? projectIdentityRead?.Identity?.VbaProjectName : null, references);
        return sourceDocuments.Where(document => VbaProjectIdentityModel.SameDocument(document.Uri, uri))
            .SelectMany(document => VbaModuleIdentityDiagnostics.Collect(document, namespaces, cancellationToken))
            .Select(diagnostic => new VbaProjectValidationDiagnostic(
                diagnostic.Code, diagnostic.Message, diagnostic.Range, diagnostic.Severity,
                Data: new Dictionary<string, object?>
                {
                    ["conflicts"] = diagnostic.NamespaceConflicts!.Select(CreateModuleIdentityConflictData).ToArray()
                }, Details: diagnostic.Details))
            .ToArray();
    }

    private static IReadOnlyDictionary<string, object?>
        CreateModuleIdentityConflictData(VbaModuleNamespaceConflict conflict)
    {
        var data = new Dictionary<string, object?>
        {
            ["collisionKind"] = conflict.CollisionKind,
            ["name"] = conflict.Name
        };
        if (conflict.ReferenceName is not null)
        {
            data["referenceName"] = conflict.ReferenceName;
        }

        return data;
    }

    /// <summary>
    /// Searches workspace symbols across indexed source documents.
    /// </summary>
    public IReadOnlyList<VbaWorkspaceSymbol> GetWorkspaceSymbols(string query)
    {
        var normalizedQuery = query ?? "";
        return definitionCandidates.GetWorkspaceSymbolDefinitions()
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

    public VbaCompletionResult GetCompletionResult(string uri, int line, int character)
        => semanticResolution.GetCompletionResult(uri, line, character);

    internal VbaCompletionResult GetCompletionResult(
        string uri,
        int line,
        int character,
        VbaCompletionInvocation invocation)
        => semanticResolution.GetCompletionResult(
            uri,
            line,
            character,
            invocation);

    public VbaDefinitionLocation? ResolveDefinition(string uri, int line, int character)
        => ResolveDefinitions(uri, line, character).FirstOrDefault();

    public IReadOnlyList<VbaDefinitionLocation> ResolveDefinitions(
        string uri,
        int line,
        int character)
    {
        var currentDocument = definitionCandidates.FindDocument(uri);
        if (currentDocument is not null)
        {
            var interfaceContracts = semanticResolution
                .ResolveInterfaceAccessorContractDefinitions(
                    currentDocument,
                    line,
                    character);
            if (interfaceContracts.Count > 0)
            {
                return interfaceContracts
                    .Select(definition => definition.Location)
                    .ToArray();
            }
        }

        var target = ResolveSourceTarget(uri, line, character);
        if (target is null)
        {
            return [];
        }

        if (target is VbaHostEventNameTarget)
        {
            return [];
        }

        if (target is VbaWithEventsEventNameTarget withEventsTarget)
        {
            return withEventsTarget.EventTargets
                .SelectMany(eventTarget => eventTarget switch
                {
                    VbaHostEventNameTarget => [],
                    _ => GetLogicalNavigationDefinitions(eventTarget)
                        .Where(definition => definition.Identity.Origin
                            == VbaDefinitionOrigin.Source)
                        .Select(definition => definition.Location)
                })
                .Distinct()
                .ToArray();
        }

        var definitions = target.IsConditionalFamily
            ? GetLogicalNavigationDefinitions(target)
            : [target.SelectedDefinition];
        return definitions
            .Where(variant => variant.Identity.Origin
                == VbaDefinitionOrigin.Source)
            .Select(variant => variant.Location)
            .ToArray();
    }

    public IReadOnlyList<VbaDefinitionLocation> FindReferences(
        string uri,
        int line,
        int character,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = ResolveSourceTarget(uri, line, character);
        if (target is null)
        {
            return [];
        }

        var declarationDefinitions = target is VbaPropertyNameTarget property
            ? property.Property.PropertyDefinitions
            : GetLogicalNavigationDefinitions(target);
        IReadOnlyList<VbaResolvedNameTarget> occurrenceTargets =
            target is VbaWithEventsEventNameTarget withEventsTarget
                ? withEventsTarget.EventTargets
                : [target];
        var interfaceDependentDeclarationRanges = sourceDocuments
            .SelectMany(document => semanticResolution
                .GetConclusiveSourceInterfaceImplementationAssociations(
                    document))
            .GroupBy(association => association.ImplementationTarget.Identity)
            .ToDictionary(
                group => group.Key,
                group => group
                    .SelectMany(association =>
                        GetLogicalRenameTargetDefinitions(
                            association.Implementation))
                    .DistinctBy(definition => definition.Identity)
                    .ToArray());
        var references = occurrenceTargets
            .SelectMany(occurrenceTarget => resolvedOccurrences.FindMatching(
                    occurrenceTarget,
                    cancellationToken)
                .Where(occurrence =>
                    !interfaceDependentDeclarationRanges.TryGetValue(
                        occurrenceTarget.Identity,
                        out var dependentDeclarationDefinitions)
                    || !dependentDeclarationDefinitions.Any(definition =>
                        VbaProjectIdentityModel.SameDocument(
                            definition.Uri,
                            occurrence.Uri)
                        && definition.Range != occurrence.Range
                        && Contains(definition.Range, occurrence.Range.Start)
                        && Contains(definition.Range, occurrence.Range.End))))
            .Select(occurrence => new VbaDefinitionLocation(occurrence.Uri, occurrence.Range))
            .Concat(declarationDefinitions
                .Where(definition => definition.Identity.Origin
                    == VbaDefinitionOrigin.Source)
                .Select(definition => definition.Location))
            .GroupBy(
                reference => CreateOccurrenceKey(
                    reference.Uri,
                    reference.Range),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(reference => reference.Uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(reference => reference.Range.Start.Line)
            .ThenBy(reference => reference.Range.Start.Character)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return references;
    }

    private IReadOnlyList<VbaSourceDefinition> GetLogicalNavigationDefinitions(
        VbaResolvedNameTarget target)
    {
        if (target is VbaPropertyNameTarget
            {
                IsConditionalFamily: false
            } ordinaryProperty)
        {
            return ordinaryProperty.Property.PropertyDefinitions
                .DistinctBy(definition => definition.Identity)
                .OrderBy(
                    definition => definition.Uri,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(definition => definition.Uri, StringComparer.Ordinal)
                .ThenBy(definition => definition.Range.Start.Line)
                .ThenBy(definition => definition.Range.Start.Character)
                .ThenBy(definition => definition.Range.End.Line)
                .ThenBy(definition => definition.Range.End.Character)
                .ToArray();
        }

        var definitions = new Dictionary<
            VbaDefinitionIdentity,
            VbaSourceDefinition>();
        var pending = new Queue<VbaSourceDefinition>();
        foreach (var physicalDefinition in target.PhysicalDefinitions)
        {
            pending.Enqueue(physicalDefinition);
        }
        while (pending.TryDequeue(out var definition))
        {
            if (!definitions.TryAdd(definition.Identity, definition))
            {
                continue;
            }

            foreach (var variant in definitionCandidates.ConditionalFamilies
                .GetLogicalDefinitions(definition))
            {
                pending.Enqueue(variant);
            }

            if (definition.Kind != VbaSourceDefinitionKind.Property)
            {
                continue;
            }

            if (resolutionPolicy.CreateNameTarget(definition)
                is not VbaPropertyNameTarget
                {
                    IsConditionalFamily: true
                } conditionalProperty)
            {
                continue;
            }

            foreach (var propertyCandidate in
                conditionalProperty.PhysicalDefinitions)
            {
                pending.Enqueue(propertyCandidate);
            }
        }

        return definitions.Values
            .OrderBy(definition => definition.Uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(definition => definition.Uri, StringComparer.Ordinal)
            .ThenBy(definition => definition.Range.Start.Line)
            .ThenBy(definition => definition.Range.Start.Character)
            .ThenBy(definition => definition.Range.End.Line)
            .ThenBy(definition => definition.Range.End.Character)
            .ToArray();
    }

    public VbaSourceDefinition? ResolveSourceDefinition(string uri, int line, int character)
        => semanticResolution.ResolveSourceDefinition(uri, line, character);

    internal VbaResolvedNameTarget? ResolveSourceTarget(
        string uri,
        int line,
        int character)
        => semanticResolution.ResolveSourceTarget(uri, line, character);

    internal VbaHoverResult? ResolveHover(string uri, int line, int character)
    {
        var target = ResolveSourceTarget(uri, line, character);
        var currentDocument = definitionCandidates.FindDocument(uri);
        if (target is null || currentDocument is null)
        {
            return null;
        }

        var syntaxTree = currentDocument.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(currentDocument.Uri, currentDocument.Text);
        var positionSyntax = syntaxTree.GetPositionSyntax(line, character);
        var identifier = positionSyntax.Identifier;
        if (identifier is null)
        {
            return null;
        }

        VbaHostEventNameTarget[] projectedHostEventTargets = target switch
        {
            VbaHostEventNameTarget hostEvent => [hostEvent],
            VbaWithEventsEventNameTarget withEventsEvent
                => withEventsEvent.EventTargets
                    .OfType<VbaHostEventNameTarget>()
                    .ToArray(),
            _ => []
        };
        if (projectedHostEventTargets.Length > 0)
        {
            return new VbaHoverResult(
                target.CanonicalName,
                target is VbaWithEventsEventNameTarget
                    ? target.PhysicalDefinitions
                    : [],
                target.IsConditionalFamily,
                new VbaRange(
                    new VbaPosition(
                        identifier.Range.Start.Line,
                        identifier.Range.Start.Character),
                    new VbaPosition(
                        identifier.Range.End.Line,
                        identifier.Range.End.Character)),
                ProjectedEventContract: null,
                ProjectedEventContracts: projectedHostEventTargets
                    .Select(hostEvent => hostEvent.EventContract)
                    .ToArray());
        }

        var callablePropertyTarget = target as VbaPropertyNameTarget;
        var isPropertyDeclarationIdentifier = callablePropertyTarget?.Property
            .PropertyDefinitions.Any(definition =>
                VbaProjectIdentityModel.SameDocument(
                    definition.Uri,
                    uri)
                && definition.Range.Start.Line == identifier.Range.Start.Line
                && definition.Range.Start.Character == identifier.Range.Start.Character
                && definition.Range.End.Line == identifier.Range.End.Line
                && definition.Range.End.Character == identifier.Range.End.Character) == true;
        var followingToken = syntaxTree.TokenStream.Tokens
            .Where(token => token.Range.Start.Offset >= identifier.Range.End.Offset)
            .Where(token => token.Kind is not (
                VbaTokenKind.Whitespace
                or VbaTokenKind.Comment
                or VbaTokenKind.NewLine
                or VbaTokenKind.LineContinuation))
            .FirstOrDefault();
        var isCallablePropertyTarget = callablePropertyTarget is not null
            && !isPropertyDeclarationIdentifier
            && (syntaxTree.Module.ArgumentLists.Any(argumentList =>
                    argumentList.CalleeRange == identifier.Range
                    && argumentList.Form is VbaCallSyntaxForm.Parenthesized
                        or VbaCallSyntaxForm.Statement)
                || followingToken?.Text == "("
                && followingToken.Range.Start.Line == identifier.Range.End.Line);
        var definitions = (isCallablePropertyTarget
            ? callablePropertyTarget!.Property.PropertyDefinitions
            : target.IsConditionalFamily
                ? target.PhysicalDefinitions
                : [target.SelectedDefinition])
            .Select(semanticResolution.ProjectDefinitionPresentation)
            .ToArray();
        var isMultiDefinitionPresentation = isCallablePropertyTarget
            || target.IsConditionalFamily;
        return new VbaHoverResult(
            isCallablePropertyTarget
                ? callablePropertyTarget!.Property.CanonicalName
                : target.CanonicalName,
            definitions,
            isMultiDefinitionPresentation,
            new VbaRange(
                new VbaPosition(
                    identifier.Range.Start.Line,
                    identifier.Range.Start.Character),
                new VbaPosition(
                    identifier.Range.End.Line,
                    identifier.Range.End.Character)));
    }

    public VbaSignatureHelp? GetSignatureHelp(
        string uri,
        int line,
        int character,
        VbaSignaturePresentationIdentity? retriggerIdentity = null)
        => semanticResolution.GetSignatureHelp(
            uri,
            line,
            character,
            retriggerIdentity);

    public VbaPrepareRenameResult? PrepareRename(
        string uri,
        int line,
        int character)
        => CreatePrepareRenameOutcome(uri, line, character).Result;

    internal VbaPrepareRenameOutcome CreatePrepareRenameOutcome(
        string uri,
        int line,
        int character)
    {
        var document = definitionCandidates.FindDocument(uri);
        var syntaxTree = document?.SyntaxTree
            ?? (document is null
                ? null
                : VbaSyntaxTree.ParseModule(document.Uri, document.Text));
        if (document is not null
            && syntaxTree is not null
            && TryGetAuthoritativeModuleIdentityAtPosition(
                document,
                syntaxTree,
                line,
                character,
                out var moduleIdentity))
        {
            var moduleTarget = FindAuthoritativeModuleIdentityDefinitionAtPosition(
                document,
                line,
                character);
            var directOwnershipFailure = moduleTarget is null
                ? null
                : GetModuleIdentityOwnershipFailure(moduleTarget);
            if (directOwnershipFailure is not null)
            {
                return new VbaPrepareRenameOutcome(
                    Result: null,
                    directOwnershipFailure);
            }

            if (moduleTarget is not null
                && HasIncompleteSourceInterfaceDependentCoverage(
                    moduleTarget))
            {
                return new VbaPrepareRenameOutcome(
                    Result: null,
                    AnalysisIncomplete(
                        "Prepare Rename could not establish complete source "
                        + "Implements dependent coverage."));
            }

            return new VbaPrepareRenameOutcome(
                new VbaPrepareRenameResult(
                    new VbaRange(
                        new VbaPosition(
                            moduleIdentity.Range.Start.Line,
                            moduleIdentity.Range.Start.Character),
                        new VbaPosition(
                            moduleIdentity.Range.End.Line,
                            moduleIdentity.Range.End.Character)),
                    moduleIdentity.Name),
                Failure: null);
        }

        if (document is not null
            && syntaxTree is not null
            && TryGetInvalidModuleIdentityMetadataAtPosition(
                document,
                syntaxTree,
                line,
                character,
                out var invalidModuleIdentityMetadata))
        {
            return new VbaPrepareRenameOutcome(
                Result: null,
                CreateInvalidModuleIdentityFailure(
                    invalidModuleIdentityMetadata));
        }

        var identifier = syntaxTree?
            .GetPositionSyntax(line, character)
            .Identifier;
        if (identifier is null)
        {
            return new VbaPrepareRenameOutcome(Result: null, Failure: null);
        }

        if (document is not null
            && TryResolveSourceInterfaceImplementationSegment(
                document,
                line,
                character,
                out var interfaceSegment))
        {
            if (IsIncompleteSourceInterfaceImplementationTarget(
                    interfaceSegment.CompleteTarget)
                || interfaceSegment.Target is not null
                    && HasIncompleteSourceInterfaceDependentCoverage(
                        interfaceSegment.Target.SelectedDefinition))
            {
                return new VbaPrepareRenameOutcome(
                    Result: null,
                    AnalysisIncomplete(
                        "Prepare Rename could not establish complete source "
                        + "Implements contract evidence."));
            }

            return new VbaPrepareRenameOutcome(
                interfaceSegment.Target is null
                    || interfaceSegment.Range is null
                    ? null
                    : new VbaPrepareRenameResult(
                        interfaceSegment.Range,
                        interfaceSegment.Target.CanonicalName),
                Failure: null);
        }

        if (document is not null
            && TryResolveWithEventsHandlerSegment(
                document,
                line,
                character,
                out var handlerSegment))
        {
            return new VbaPrepareRenameOutcome(
                handlerSegment.Target is null
                    || handlerSegment.Range is null
                    ? null
                    : new VbaPrepareRenameResult(
                        handlerSegment.Range,
                        handlerSegment.Target.CanonicalName),
                Failure: null);
        }

        var resolvedTarget = ResolveSourceTarget(uri, line, character);
        if (resolvedTarget is not null
            && IsIncompleteSourceInterfaceImplementationTarget(resolvedTarget))
        {
            return new VbaPrepareRenameOutcome(
                Result: null,
                AnalysisIncomplete(
                    "Prepare Rename could not establish complete source "
                    + "Implements contract evidence."));
        }

        var declarationAtPosition = document?.Definitions.FirstOrDefault(
            definition => definition.Range.Start.Line == line
                && ContainsCharacter(
                    definition.Range,
                    new VbaPosition(line, character)));
        if (document is not null
            && declarationAtPosition is not null
            && semanticResolution.IsPotentialInterfaceImplementationDeclaration(
                document,
                declarationAtPosition))
        {
            if (semanticResolution
                .HasIndeterminateConditionalCompilationOwnership(
                    declarationAtPosition))
            {
                return new VbaPrepareRenameOutcome(
                    Result: null,
                    AnalysisIncomplete(
                        "Prepare Rename could not establish complete source "
                        + "Implements ownership."));
            }

            return new VbaPrepareRenameOutcome(Result: null, Failure: null);
        }

        if (document is not null
            && resolvedTarget is not null
            && HasIntrinsicHostHandlerAssociation(document, resolvedTarget))
        {
            return new VbaPrepareRenameOutcome(Result: null, Failure: null);
        }

        if (resolvedTarget is not null
            && HasWithEventsDependentRenameVariant(resolvedTarget))
        {
            return new VbaPrepareRenameOutcome(Result: null, Failure: null);
        }

        if (resolvedTarget is not null
            && ClassifySourceInterfaceDependentRenameTarget(resolvedTarget)
                is not null)
        {
            return new VbaPrepareRenameOutcome(Result: null, Failure: null);
        }

        var target = ResolveSourceDefinition(uri, line, character);
        if (target is null)
        {
            var classification = semanticResolution.ClassifySourceDefinition(
                uri,
                line,
                character);
            if (classification.Kind is VbaNameResolutionKind.Ambiguous
                or VbaNameResolutionKind.AnalysisIncomplete)
            {
                return new VbaPrepareRenameOutcome(
                    Result: null,
                    AnalysisIncomplete(
                        "Prepare Rename could not establish one unambiguous "
                        + "source-owned target."));
            }

            return new VbaPrepareRenameOutcome(Result: null, Failure: null);
        }

        var isExplicitModuleIdentityTarget = IsExplicitModuleIdentityTarget(target);
        var moduleIdentityMetadata = IsModuleIdentity(target)
            ? GetModuleIdentityMetadata(target)
            : null;
        if (moduleIdentityMetadata?.State
            == VbaModuleIdentityMetadataState.Invalid)
        {
            return new VbaPrepareRenameOutcome(
                Result: null,
                CreateInvalidModuleIdentityFailure(moduleIdentityMetadata));
        }

        if (IsModuleIdentity(target)
            && !isExplicitModuleIdentityTarget
            && moduleIdentityMetadata?.State
                == VbaModuleIdentityMetadataState.Missing)
        {
            return new VbaPrepareRenameOutcome(
                Result: null,
                new VbaRenameFailure(
                    "moduleIdentityNotExplicit",
                    "Module identity Rename requires an explicit valid "
                    + "Attribute VB_Name record; re-export or repair the source first."));
        }

        if (isExplicitModuleIdentityTarget
            && GetModuleIdentityOwnershipFailure(target) is { } ownershipFailure)
        {
            return new VbaPrepareRenameOutcome(
                Result: null,
                ownershipFailure);
        }

        if (!isExplicitModuleIdentityTarget
            && !resolutionPolicy.IsRenameTarget(target))
        {
            return new VbaPrepareRenameOutcome(
                Result: null,
                new VbaRenameFailure(
                    "notRenameTarget",
                    $"'{target.Name}' is known semantic metadata but is not "
                    + "a source-owned Rename target."));
        }

        if (HasIndeterminateConditionalFamilyCoverage(target))
        {
            return new VbaPrepareRenameOutcome(
                Result: null,
                AnalysisIncomplete(
                    "Prepare Rename could not establish the target's "
                    + "complete conditional declaration family."));
        }

        target = GetLogicalRenameTarget(target);
        if (HasIncompleteSourceInterfaceDependentCoverage(target))
        {
            return new VbaPrepareRenameOutcome(
                Result: null,
                AnalysisIncomplete(
                    "Prepare Rename could not establish complete source "
                    + "Implements dependent coverage."));
        }

        var logicalTarget = resolutionPolicy.CreateNameTarget(target);
        var occurrenceRange = resolvedOccurrences
            .GetDocumentOccurrences(uri)
            .Where(occurrence => occurrence.Target.Identity
                == logicalTarget.Identity)
            .Where(occurrence => Contains(
                occurrence.Range,
                new VbaPosition(line, character)))
            .OrderBy(occurrence =>
                occurrence.Range.End.Character
                - occurrence.Range.Start.Character)
            .Select(occurrence => occurrence.Range)
            .FirstOrDefault();
        var prepareRange = occurrenceRange
            ?? new VbaRange(
                new VbaPosition(
                    identifier.Range.Start.Line,
                    identifier.Range.Start.Character),
                new VbaPosition(
                    identifier.Range.End.Line,
                    identifier.Range.End.Character));

        return new VbaPrepareRenameOutcome(
            new VbaPrepareRenameResult(
                prepareRange,
                target.Name),
            Failure: null);
    }

    private bool TryResolveSourceInterfaceImplementationSegment(
        VbaSourceDocument document,
        int line,
        int character,
        out VbaInterfaceImplementationSegmentResolution result)
    {
        result = null!;
        var associations = semanticResolution
            .GetConclusiveSourceInterfaceImplementationAssociations(document)
            .Where(association => association.Implementation.Range.Start.Line
                    == line
                && association.Implementation.Range.Start.Character
                    <= character
                && character < association.Implementation.Range.End.Character)
            .ToArray();
        if (associations.Length == 0)
        {
            return false;
        }

        var position = new VbaPosition(line, character);
        var candidates = associations.Select(association =>
                ContainsCharacter(association.SeparatorRange, position)
                    ? new VbaInterfaceImplementationSegmentCandidate(
                        VbaInterfaceImplementationSegmentKind.Separator,
                        Target: null,
                        association.SeparatorRange)
                    : ContainsCharacter(
                        association.InterfacePrefixRange,
                        position)
                        ? new VbaInterfaceImplementationSegmentCandidate(
                            VbaInterfaceImplementationSegmentKind.InterfacePrefix,
                            association.Relationship.InterfaceTarget,
                            association.InterfacePrefixRange)
                        : new VbaInterfaceImplementationSegmentCandidate(
                            VbaInterfaceImplementationSegmentKind.MemberSuffix,
                            association.MemberTarget,
                            association.MemberSuffixRange))
            .ToArray();
        var first = candidates[0];
        var hasOneMeaning = first.Target is not null
            && candidates.All(candidate => candidate.Kind == first.Kind
                && candidate.Range == first.Range
                && candidate.Target?.Identity == first.Target.Identity);
        result = new VbaInterfaceImplementationSegmentResolution(
            hasOneMeaning ? first.Target : null,
            hasOneMeaning ? first.Range : null,
            associations[0].ImplementationTarget);
        return true;
    }

    private enum VbaInterfaceImplementationSegmentKind
    {
        InterfacePrefix,
        Separator,
        MemberSuffix
    }

    private sealed record VbaInterfaceImplementationSegmentCandidate(
        VbaInterfaceImplementationSegmentKind Kind,
        VbaResolvedNameTarget? Target,
        VbaRange Range);

    private sealed record VbaInterfaceImplementationSegmentResolution(
        VbaResolvedNameTarget? Target,
        VbaRange? Range,
        VbaResolvedNameTarget CompleteTarget);

    private sealed record VbaWithEventsHandlerSegmentResolution(
        VbaResolvedNameTarget? Target,
        VbaRange? Range,
        VbaResolvedNameTarget CompleteTarget);

    private sealed record VbaWithEventsRenameDependency(
        VbaHandlerEventRenameConvergence Convergence,
        bool RenamesEvent,
        bool RenamesVariable);

    private sealed record VbaWithEventsAssociationProofKey(
        string Handler,
        string HandlerTarget,
        string VariableTarget,
        string Prefix,
        string Separator,
        string Suffix,
        VbaWithEventsHandlerRecognition Recognition,
        VbaHandlerEventRenameConvergenceKind Convergence,
        string BindingEntries);

    private sealed record VbaInterfaceAssociationProofKey(
        string Relationship,
        string InterfaceTarget,
        string ContractOrigin,
        string MemberTarget,
        VbaInterfaceAccessorContractKind ContractKind,
        bool IsDerivedVariableAccessor,
        VbaCallableContractComparisonState CompatibilityState,
        string Implementation,
        string ImplementationTarget,
        string InterfacePrefix,
        string Separator,
        string MemberSuffix);

    private enum VbaIncompleteInterfaceTargetRole
    {
        Upstream,
        Dependent
    }

    private sealed record VbaIncompleteInterfaceTargetProofKey(
        string ImplementingDocument,
        VbaIncompleteInterfaceTargetRole Role,
        string Target);

    private static bool ContainsCharacter(
        VbaRange range,
        VbaPosition position)
        => IsAtOrAfter(position, range.Start)
            && !IsAtOrAfter(position, range.End);

    private VbaDependentRenameTarget?
        ClassifySourceInterfaceDependentRenameTarget(
        VbaResolvedNameTarget target)
    {
        var associations = sourceDocuments
            .SelectMany(document => semanticResolution
                .GetConclusiveSourceInterfaceImplementationAssociations(
                    document))
            .Where(association => association.ImplementationTarget.Identity
                == target.Identity)
            .ToArray();
        return associations.Length == 0
            ? null
            : new VbaDependentRenameTarget(target, associations);
    }

    private bool IsIncompleteSourceInterfaceImplementationTarget(
        VbaResolvedNameTarget target)
        => sourceDocuments
            .Select(semanticResolution
                .AnalyzeSourceInterfaceImplementationAssociations)
            .SelectMany(analysis => analysis.IncompleteDependentTargets)
            .Any(incompleteTarget => incompleteTarget.Identity
                == target.Identity);

    private bool IsSourceInterfaceImplementationDeclarationPosition(
        VbaSourceDocument document,
        int line,
        int character)
        => semanticResolution
            .GetConclusiveSourceInterfaceImplementationAssociations(document)
            .Any(association => association.Implementation.Range.Start.Line
                    == line
                && association.Implementation.Range.Start.Character
                    <= character
                && character < association.Implementation.Range.End.Character);

    private static bool TryGetAuthoritativeModuleIdentityAtPosition(
        VbaSourceDocument document,
        VbaSyntaxTree syntaxTree,
        int line,
        int character,
        out VbaModuleIdentitySyntax moduleIdentity)
    {
        moduleIdentity = syntaxTree.Module.Identity;
        var metadata = moduleIdentity.Metadata
            ?? ReadModuleIdentityMetadata(document, syntaxTree);
        return metadata.IsAuthoritative
            && metadata.Name!.Equals(moduleIdentity.Name, StringComparison.Ordinal)
            && line == moduleIdentity.Range.Start.Line
            && line == moduleIdentity.Range.End.Line
            && character >= moduleIdentity.Range.Start.Character
            && character < moduleIdentity.Range.End.Character;
    }

    private static bool TryGetInvalidModuleIdentityMetadataAtPosition(
        VbaSourceDocument document,
        VbaSyntaxTree syntaxTree,
        int line,
        int character,
        out VbaModuleIdentityMetadata metadata)
    {
        metadata = syntaxTree.Module.Identity.Metadata
            ?? ReadModuleIdentityMetadata(document, syntaxTree);
        return metadata.State == VbaModuleIdentityMetadataState.Invalid
            && metadata.Records.Any(record =>
                ContainsPosition(record.RepairRange, line, character));
    }

    private static VbaModuleIdentityMetadata ReadModuleIdentityMetadata(
        VbaSourceDocument document,
        VbaSyntaxTree syntaxTree)
        => VbaModuleIdentityMetadataReader.Read(
            document.Text,
            syntaxTree.Module.Kind == VbaModuleKind.StandardModule
                ? VbaModuleIdentitySourceKind.StandardModule
                : VbaModuleIdentitySourceKind.ObjectModule);

    private static bool ContainsPosition(
        VbaSyntaxRange range,
        int line,
        int character)
        => (line > range.Start.Line
                || line == range.Start.Line
                    && character >= range.Start.Character)
            && (line < range.End.Line
                || line == range.End.Line
                    && character < range.End.Character);

    private bool HasIntrinsicHostHandlerAssociation(
        VbaSourceDocument document,
        VbaResolvedNameTarget target)
        => GetIntrinsicHostHandlerAnalyses(document, target).Count > 0;

    private IReadOnlyList<VbaIntrinsicHostHandlerAnalysis>
        GetIntrinsicHostHandlerAnalyses(
            VbaSourceDocument document,
            VbaResolvedNameTarget target)
    {
        IEnumerable<VbaSourceDefinition> candidates = target switch
        {
            VbaHostEventNameTarget hostEventTarget
                => [hostEventTarget.SelectedDefinition],
            _ => target.PhysicalDefinitions
        };
        return candidates
            .Where(candidate => VbaProjectIdentityModel.SameDocument(
                candidate.Uri,
                document.Uri))
            .Select(candidate => semanticResolution.AnalyzeIntrinsicHostHandler(
                document,
                candidate))
            .Where(analysis => analysis is not null)
            .Select(analysis => analysis!)
            .DistinctBy(analysis => analysis.Handler.Identity)
            .ToArray();
    }

    public VbaRenamePlan? CreateRenamePlan(
        string uri,
        int line,
        int character,
        string newName,
        CancellationToken cancellationToken = default)
        => CreateRenameResult(
            uri,
            line,
            character,
            newName,
            cancellationToken).Plan;

    internal bool RequiresFileFollowingModuleRename(
        string uri,
        int line,
        int character,
        string newName,
        VbaProjectIdentityReadResult? projectIdentityRead = null)
    {
        var document = definitionCandidates.FindDocument(uri);
        var target = document is null
            ? null
            : FindAuthoritativeModuleIdentityDefinitionAtPosition(
                document,
                line,
                character);
        target ??= ResolveSourceDefinition(uri, line, character);
        return target is not null
            && IsModuleIdentity(target)
            && IsExplicitModuleIdentityTarget(target)
            && GetModuleIdentityOwnershipFailure(target) is null
            && GetModuleIdentityMutationAuthorityFailure(
                target,
                projectIdentityRead) is null
            && !target.Name.Equals(newName, StringComparison.Ordinal)
            && CreateModuleIdentityFileRenames(target, newName).Count > 0;
    }

    internal bool RequiresSourceTemplateIdentityFence(
        string uri,
        int line,
        int character,
        string newName,
        VbaProjectIdentityReadResult? projectIdentityRead)
    {
        var document = definitionCandidates.FindDocument(uri);
        var target = document is null
            ? null
            : FindAuthoritativeModuleIdentityDefinitionAtPosition(
                document,
                line,
                character);
        target ??= ResolveSourceDefinition(uri, line, character);
        return projectResolution?.Kind
                == VbaProjectResolutionKind.ManifestDocument
            && target is not null
            && IsModuleIdentity(target)
            && IsExplicitModuleIdentityTarget(target)
            && GetModuleIdentityOwnershipFailure(target) is null
            && projectIdentityRead?.Identity is not null
            && ValidateRenameTargetName(target, newName) is null
            && !target.Name.Equals(newName, StringComparison.Ordinal);
    }

    internal VbaRenameResult CreateRenameResult(
        string uri,
        int line,
        int character,
        string newName,
        CancellationToken cancellationToken = default,
        VbaProjectIdentityReadResult? projectIdentityRead = null,
        VbaRenameCollisionMode collisionMode = VbaRenameCollisionMode.Reject,
        bool retainOriginalPaths = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var nameFailure = ValidateRenameName(newName);
        if (nameFailure is not null)
        {
            return new VbaRenameResult(
                Plan: null,
                nameFailure);
        }

        var document = definitionCandidates.FindDocument(uri);
        var syntaxTree = document?.SyntaxTree
            ?? (document is null
                ? null
                : VbaSyntaxTree.ParseModule(document.Uri, document.Text));
        if (document is not null
            && syntaxTree is not null
            && TryGetInvalidModuleIdentityMetadataAtPosition(
                document,
                syntaxTree,
                line,
                character,
                out var invalidModuleIdentityMetadata))
        {
            return new VbaRenameResult(
                Plan: null,
                CreateInvalidModuleIdentityFailure(
                    invalidModuleIdentityMetadata));
        }

        var moduleTarget = document is null
            ? null
            : FindAuthoritativeModuleIdentityDefinitionAtPosition(
                document,
                line,
                character);
        VbaInterfaceImplementationSegmentResolution? interfaceSegment = null;
        if (moduleTarget is null
            && document is not null
            && TryResolveSourceInterfaceImplementationSegment(
                document,
                line,
                character,
                out var resolvedInterfaceSegment))
        {
            interfaceSegment = resolvedInterfaceSegment;
            if (IsIncompleteSourceInterfaceImplementationTarget(
                    interfaceSegment.CompleteTarget)
                || interfaceSegment.Target is not null
                    && HasIncompleteSourceInterfaceDependentCoverage(
                        interfaceSegment.Target.SelectedDefinition))
            {
                var noOpName = interfaceSegment.Target?.CanonicalName
                    ?? interfaceSegment.CompleteTarget.CanonicalName;
                if (string.Equals(
                    noOpName,
                    newName,
                    StringComparison.Ordinal))
                {
                    return new VbaRenameResult(Plan: null, Failure: null);
                }

                return new VbaRenameResult(
                    Plan: null,
                    AnalysisIncomplete(
                        "Rename could not establish complete source Implements "
                        + "contract evidence."));
            }

            if (interfaceSegment.Target is null)
            {
                if (string.Equals(
                    interfaceSegment.CompleteTarget.CanonicalName,
                    newName,
                    StringComparison.Ordinal))
                {
                    return new VbaRenameResult(Plan: null, Failure: null);
                }

                return new VbaRenameResult(
                    Plan: null,
                    new VbaRenameFailure(
                        "notRenameTarget",
                        "The semantic separator of an Implements implementation "
                        + "is not a Rename target."));
            }
        }

        var resolvedTarget = moduleTarget is null
            ? interfaceSegment?.Target
                ?? ResolveSourceTarget(uri, line, character)
            : resolutionPolicy.CreateNameTarget(moduleTarget);
        if (document is not null && resolvedTarget is not null)
        {
            var intrinsicAssociations = GetIntrinsicHostHandlerAnalyses(
                document,
                resolvedTarget);
            if (intrinsicAssociations.Count > 0)
            {
                var selectedAssociation = intrinsicAssociations
                    .FirstOrDefault(association => association.Handler.Identity
                        == resolvedTarget.SelectedDefinition.Identity)
                    ?? intrinsicAssociations[0];
                if (string.Equals(
                    selectedAssociation.Handler.Name,
                    newName,
                    StringComparison.Ordinal))
                {
                    return new VbaRenameResult(Plan: null, Failure: null);
                }

                return new VbaRenameResult(
                    Plan: null,
                    new VbaRenameFailure(
                        "notRenameTarget",
                        "An intrinsic host Event handler name is a fixed catalog contract."));
            }
        }

        if (resolvedTarget is not null
            && IsIncompleteSourceInterfaceImplementationTarget(resolvedTarget))
        {
            if (string.Equals(
                resolvedTarget.CanonicalName,
                newName,
                StringComparison.Ordinal))
            {
                return new VbaRenameResult(Plan: null, Failure: null);
            }

            return new VbaRenameResult(
                Plan: null,
                AnalysisIncomplete(
                    "Rename could not establish complete source Implements "
                    + "contract evidence."));
        }

        if (resolvedTarget is not null
            && HasWithEventsDependentRenameVariant(resolvedTarget))
        {
            if (string.Equals(
                resolvedTarget.CanonicalName,
                newName,
                StringComparison.Ordinal))
            {
                return new VbaRenameResult(Plan: null, Failure: null);
            }

            return new VbaRenameResult(
                Plan: null,
                new VbaRenameFailure(
                    "notRenameTarget",
                    "A WithEvents handler is a dependent Rename target; "
                    + "rename its WithEvents variable or source Event instead."));
        }

        if (document is not null
            && resolvedTarget is not null
            && ClassifySourceInterfaceDependentRenameTarget(resolvedTarget)
                is not null
            && !IsSourceInterfaceImplementationDeclarationPosition(
                document,
                line,
                character))
        {
            if (string.Equals(
                resolvedTarget.CanonicalName,
                newName,
                StringComparison.Ordinal))
            {
                return new VbaRenameResult(Plan: null, Failure: null);
            }

            return new VbaRenameResult(
                Plan: null,
                new VbaRenameFailure(
                    "notRenameTarget",
                    "An Implements implementation is a dependent Rename target; "
                    + "rename its source interface type or member instead."));
        }

        var target = moduleTarget
            ?? interfaceSegment?.Target?.SelectedDefinition
            ?? ResolveSourceDefinition(uri, line, character);
        if (target is null)
        {
            var classification = semanticResolution.ClassifySourceDefinition(
                uri,
                line,
                character);
            if (classification.Kind is VbaNameResolutionKind.Ambiguous
                or VbaNameResolutionKind.AnalysisIncomplete)
            {
                return new VbaRenameResult(
                    Plan: null,
                    AnalysisIncomplete(
                        "Rename could not establish one unambiguous "
                        + "source-owned target."));
            }

            return new VbaRenameResult(
                Plan: null,
                new VbaRenameFailure(
                    "notRenameTarget",
                    "Rename requires a source-defined VBA rename target at "
                    + "the requested position."));
        }

        if (IsModuleIdentity(target))
        {
            var moduleIdentityMetadata = GetModuleIdentityMetadata(target);
            if (moduleIdentityMetadata?.State
                == VbaModuleIdentityMetadataState.Invalid)
            {
                return new VbaRenameResult(
                    Plan: null,
                    CreateInvalidModuleIdentityFailure(
                        moduleIdentityMetadata));
            }

            var moduleNameFailure = ValidateRenameTargetName(target, newName);
            if (moduleNameFailure is not null)
            {
                return new VbaRenameResult(
                    Plan: null,
                    moduleNameFailure);
            }

            if (string.Equals(target.Name, newName, StringComparison.Ordinal))
            {
                return new VbaRenameResult(Plan: null, Failure: null);
            }
        }

        var isExplicitModuleIdentityTarget = IsExplicitModuleIdentityTarget(target);
        if (IsModuleIdentity(target)
            && !isExplicitModuleIdentityTarget
            && GetModuleIdentityMetadata(target)?.State
                == VbaModuleIdentityMetadataState.Missing)
        {
            return new VbaRenameResult(
                Plan: null,
                new VbaRenameFailure(
                    "moduleIdentityNotExplicit",
                    "Module identity Rename requires an explicit valid "
                    + "Attribute VB_Name record; re-export or repair the source first."));
        }

        if (isExplicitModuleIdentityTarget
            && GetModuleIdentityOwnershipFailure(target) is { } ownershipFailure)
        {
            return new VbaRenameResult(
                Plan: null,
                ownershipFailure);
        }

        if (isExplicitModuleIdentityTarget
            && GetModuleIdentityMutationAuthorityFailure(
                target,
                projectIdentityRead)
                is { } mutationAuthorityFailure)
        {
            return new VbaRenameResult(
                Plan: null,
                mutationAuthorityFailure);
        }

        if (!isExplicitModuleIdentityTarget
            && !resolutionPolicy.IsRenameTarget(target))
        {
            return new VbaRenameResult(
                Plan: null,
                new VbaRenameFailure(
                    "notRenameTarget",
                    "Rename requires a source-defined VBA rename target at "
                    + "the requested position."));
        }

        if (HasIndeterminateConditionalFamilyCoverage(target))
        {
            return new VbaRenameResult(
                Plan: null,
                AnalysisIncomplete(
                    "Rename could not establish the target's complete "
                    + "conditional declaration family."));
        }

        target = GetLogicalRenameTarget(target);

        var targetNameFailure = ValidateRenameTargetName(target, newName);
        if (targetNameFailure is not null)
        {
            return new VbaRenameResult(
                Plan: null,
                targetNameFailure);
        }

        if (string.Equals(target.Name, newName, StringComparison.Ordinal))
        {
            return new VbaRenameResult(Plan: null, Failure: null);
        }

        if (HasIncompleteSourceInterfaceDependentCoverage(target))
        {
            return new VbaRenameResult(
                Plan: null,
                AnalysisIncomplete(
                    "Rename could not establish complete source Implements "
                    + "dependent coverage."));
        }

        var withEventsCoverageFailure =
            GetWithEventsDependentRenameCoverageFailure(target);
        if (withEventsCoverageFailure is not null)
        {
            return new VbaRenameResult(
                Plan: null,
                withEventsCoverageFailure);
        }

        var invalidTargetConflicts = FindInvalidPropertyFamilyConflicts(target);
        if (invalidTargetConflicts.Count > 0)
        {
            var locations = string.Join(
                ", ",
                invalidTargetConflicts.Select(conflict =>
                    $"'{conflict.Name}' at {conflict.Uri}:"
                    + $"{conflict.Range!.Start.Line + 1}:"
                    + $"{conflict.Range.Start.Character + 1}"));
            return new VbaRenameResult(
                Plan: null,
                new VbaRenameFailure(
                    "sameScopeCollision",
                    "Rename target has repeated Property accessors at "
                    + $"{locations}.",
                    invalidTargetConflicts));
        }

        var collisions = FindSameScopeCollisions(
                target,
                newName,
                projectIdentityRead)
            .Concat(FindInterfaceDependentRenameCollisions(target, newName))
            .Concat(FindWithEventsDependentRenameCollisions(target, newName))
            .Distinct()
            .ToArray();
        if (collisions.Length > 0 && collisionMode == VbaRenameCollisionMode.Reject)
        {
            var locations = string.Join(
                ", ",
                collisions.Select(CreateRenameConflictDescription));
            return new VbaRenameResult(
                Plan: null,
                new VbaRenameFailure(
                    "sameScopeCollision",
                    CreateRenameCollisionMessage(
                        target,
                        newName,
                        collisions,
                        locations),
                    collisions));
        }

        var targetOccurrences = GetLogicalRenameTargetDefinitions(target)
            .Select(resolutionPolicy.CreateNameTarget)
            .DistinctBy(logicalTarget => logicalTarget.Identity)
            .SelectMany(logicalTarget => resolvedOccurrences.FindMatching(
                logicalTarget,
                cancellationToken))
            .Concat(isExplicitModuleIdentityTarget
                ? CreateModuleIdentityDeclarationOccurrences(target)
                : [])
            .GroupBy(
                occurrence => CreateOccurrenceKey(
                    occurrence.Uri,
                    occurrence.Range),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(occurrence => occurrence.Uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(occurrence => occurrence.Range.Start.Line)
            .ThenBy(occurrence => occurrence.Range.Start.Character)
            .ToArray();
        if (target.Kind == VbaSourceDefinitionKind.Event
            && HasIntrinsicHostEventAlternative(targetOccurrences, cancellationToken))
        {
            return new VbaRenameResult(
                Plan: null,
                AnalysisIncomplete(
                    "Rename cannot prove complete dependent-handler coverage across source and intrinsic UserForm Event alternatives."));
        }

        var formSourceUnitFailure = TryCreateFormSourceUnitRenameEdits(
            target,
            newName,
            out var formSourceUnitEdits,
            out var formSourceUnit,
            retainOriginalPaths);
        if (formSourceUnitFailure is not null)
        {
            return new VbaRenameResult(Plan: null, formSourceUnitFailure);
        }

        var plannedEdits = targetOccurrences
            .Select(occurrence => new KeyValuePair<string, VbaTextEdit>(
                occurrence.Uri,
                new VbaTextEdit(occurrence.Range, newName)))
            .Concat(CreateInterfaceDependentRenameEdits(
                target,
                newName,
                cancellationToken))
            .Concat(CreateWithEventsDependentRenameEdits(
                target,
                newName,
                cancellationToken))
            .Concat(formSourceUnitEdits);
        var changeSetFailure = TryCreateRenameChangeSet(
            plannedEdits,
            out var changes);
        if (changeSetFailure is not null)
        {
            return new VbaRenameResult(Plan: null, changeSetFailure);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (collisionMode == VbaRenameCollisionMode.PrepareConfirmation)
        {
            collisions = collisions.Concat(FindBindingCaptureCollisions(
                target, newName, changes, cancellationToken)).Distinct().ToArray();
        }
        VbaRenameCollisionProof? collisionProof = null;
        if (collisions.Length > 0)
        {
            var controlFailure = TryCreateCollisionControlProof(
                uri, line, character, target, newName, changes, collisions,
                projectIdentityRead, retainOriginalPaths, cancellationToken,
                out collisionProof);
            if (controlFailure is not null)
            {
                return new VbaRenameResult(Plan: null, controlFailure);
            }
        }
        var proofFailure = ProveBindingsArePreserved(
            target,
            targetOccurrences,
            changes,
            newName,
            cancellationToken,
            out var targetCorrespondence,
            collisionProof);
        if (proofFailure is not null)
        {
            return new VbaRenameResult(Plan: null, proofFailure);
        }

        return new VbaRenameResult(
            changes.Count == 0
                ? null
                : new VbaRenamePlan(target.Range, changes)
                {
                    FileRenames = retainOriginalPaths ? [] : CreateModuleIdentityFileRenames(
                        target,
                        newName),
                    FormSourceUnits = formSourceUnit is null
                        ? []
                        : [formSourceUnit],
                    TargetCorrespondence = targetCorrespondence
                },
            Failure: null,
            CollisionReview: collisionProof is null
                ? null
                : new VbaRenameCollisionReview(
                    target.Name,
                    newName,
                    collisions,
                    Array.AsReadOnly(collisionProof.Impacts.ToArray())));
    }

    private VbaRenameFailure? TryCreateFormSourceUnitRenameEdits(
        VbaSourceDefinition target,
        string newName,
        out IReadOnlyList<KeyValuePair<string, VbaTextEdit>> edits,
        out VbaFormSourceUnit? sourceUnit,
        bool retainOriginalPaths)
    {
        edits = [];
        sourceUnit = null;
        if (!IsModuleIdentity(target))
        {
            return null;
        }

        var document = definitionCandidates.FindDocument(target.Uri);
        if (document is null)
        {
            return null;
        }

        var syntaxTree = document.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(document.Uri, document.Text);
        if (syntaxTree.Module.Kind != VbaModuleKind.FormModule)
        {
            return null;
        }

        var sourcePath = VbaProjectResolver.TryGetLocalPath(target.Uri);
        var designer = syntaxTree.Module.FormDesignerBlock;
        if (designer is null || designer.EvidenceProblems.Count > 0)
        {
            var problem = designer?.EvidenceProblems.FirstOrDefault();
            return new VbaRenameFailure(
                "resourceOperationConflict",
                "The form designer could not be proven structurally complete for Rename.",
                Condition: problem?.Kind switch
                {
                    VbaFormDesignerEvidenceProblemKind.RootMissing
                        => "designerRootMissing",
                    VbaFormDesignerEvidenceProblemKind.RootAmbiguous
                        => "designerRootAmbiguous",
                    VbaFormDesignerEvidenceProblemKind.ResourceReferenceMalformed
                        => "sidecarReferenceMalformed",
                    VbaFormDesignerEvidenceProblemKind.ResourceReferenceUnsafe
                        => "sidecarReferenceUnsafe",
                    _ => "designerStructureMalformed"
                },
                Path: sourcePath,
                Guidance: "Re-export or repair the complete .frm/.frx source unit, then retry Rename.");
        }

        if (designer.Root is not { } root
            || !root.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase))
        {
            return new VbaRenameFailure(
                "resourceOperationConflict",
                "The top-level form designer identity does not match the authoritative module identity.",
                Condition: "designerIdentityConflict",
                Path: sourcePath,
                Guidance: "Re-export or repair the form designer identity, then retry Rename.");
        }

        if (sourcePath is null)
        {
            return AnalysisIncomplete(
                "Rename could not identify the form source-unit path needed to validate designer resources.");
        }

        var sourceBaseName = Path.GetFileNameWithoutExtension(sourcePath);
        var sourceDirectory = Path.GetDirectoryName(sourcePath);
        if (sourceDirectory is null)
        {
            return AnalysisIncomplete(
                "Rename could not identify the form source-unit directory needed to validate designer resources.");
        }

        var sidecarFileName = sourceBaseName + ".frx";
        var conflictingReference = designer.ResourceReferences
            .FirstOrDefault(reference => !reference.FileName.Equals(
                sidecarFileName,
                StringComparison.OrdinalIgnoreCase));
        if (conflictingReference is not null)
        {
            return new VbaRenameFailure(
                "resourceOperationConflict",
                "The form designer identifies a different or ambiguous sidecar resource.",
                Condition: "sidecarReferenceConflict",
                Path: Path.Combine(sourceDirectory, conflictingReference.FileName),
                Guidance: "Re-export or repair every designer resource reference to use the matching sidecar basename, then retry Rename.");
        }

        var result = new List<KeyValuePair<string, VbaTextEdit>>
        {
            new(
                target.Uri,
                new VbaTextEdit(ToRange(root.NameRange), newName))
        };
        var sidecarPathFollowsIdentity = !retainOriginalPaths && sourceBaseName.Equals(
            target.Name,
            StringComparison.OrdinalIgnoreCase);
        if (sidecarPathFollowsIdentity)
        {
            result.AddRange(designer.ResourceReferences.Select(reference =>
                new KeyValuePair<string, VbaTextEdit>(
                    target.Uri,
                    new VbaTextEdit(
                        ToRange(reference.FileNameRange),
                        newName + Path.GetExtension(reference.FileName)))));
        }

        edits = result.ToArray();
        var sourceSidecarFileName = designer.ResourceReferences
            .Select(reference => reference.FileName)
            .FirstOrDefault()
            ?? sidecarFileName;
        var sourceSidecarPath = Path.Combine(
            sourceDirectory,
            sourceSidecarFileName);
        var destinationSidecarPath = sidecarPathFollowsIdentity
            ? Path.Combine(
                sourceDirectory,
                newName + Path.GetExtension(sourceSidecarFileName))
            : sourceSidecarPath;
        sourceUnit = new VbaFormSourceUnit(
            target.Uri,
            new Uri(sourceSidecarPath).AbsoluteUri,
            new Uri(destinationSidecarPath).AbsoluteUri,
            designer.ResourceReferences.Count > 0,
            sidecarPathFollowsIdentity);
        return null;
    }

    private static VbaRenameFailure? TryCreateRenameChangeSet(
        IEnumerable<KeyValuePair<string, VbaTextEdit>> plannedEdits,
        out IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes)
    {
        var result = new Dictionary<string, IReadOnlyList<VbaTextEdit>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var documentGroup in plannedEdits
                     .GroupBy(edit => edit.Key,
                         StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key,
                         StringComparer.OrdinalIgnoreCase))
        {
            var edits = new List<VbaTextEdit>();
            foreach (var rangeGroup in documentGroup
                         .Select(edit => edit.Value)
                         .GroupBy(edit => GetRangeKey(edit.Range)))
            {
                var replacements = rangeGroup
                    .Select(edit => edit.NewText)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (replacements.Length != 1)
                {
                    changes = new Dictionary<
                        string,
                        IReadOnlyList<VbaTextEdit>>(
                            StringComparer.OrdinalIgnoreCase);
                    return AnalysisIncomplete(
                        "Rename produced conflicting replacements for one "
                        + "source range.");
                }

                edits.Add(new VbaTextEdit(
                    rangeGroup.First().Range,
                    replacements[0]));
            }

            edits.Sort((left, right) =>
            {
                var line = left.Range.Start.Line.CompareTo(
                    right.Range.Start.Line);
                return line != 0
                    ? line
                    : left.Range.Start.Character.CompareTo(
                        right.Range.Start.Character);
            });
            for (var index = 0; index < edits.Count; index++)
            {
                for (var laterIndex = index + 1;
                     laterIndex < edits.Count;
                     laterIndex++)
                {
                    if (!IsStrictlyBefore(
                            edits[laterIndex].Range.Start,
                            edits[index].Range.End))
                    {
                        break;
                    }

                    if (IsStrictlyBefore(
                        edits[index].Range.Start,
                        edits[laterIndex].Range.End))
                    {
                        changes = new Dictionary<
                            string,
                            IReadOnlyList<VbaTextEdit>>(
                                StringComparer.OrdinalIgnoreCase);
                        return AnalysisIncomplete(
                            "Rename produced overlapping source edits that "
                            + "could not be applied atomically.");
                    }
                }
            }

            result[documentGroup.Key] = edits.ToArray();
        }

        changes = result;
        return null;
    }

    private static bool IsStrictlyBefore(
        VbaPosition left,
        VbaPosition right)
        => left.Line < right.Line
            || left.Line == right.Line
                && left.Character < right.Character;

    private IReadOnlyList<KeyValuePair<string, VbaTextEdit>>
        CreateInterfaceDependentRenameEdits(
            VbaSourceDefinition target,
            string newName,
            CancellationToken cancellationToken)
    {
        var targetIdentity = resolutionPolicy.CreateNameTarget(target).Identity;
        var associations = sourceDocuments
            .SelectMany(document => semanticResolution
                .GetConclusiveSourceInterfaceImplementationAssociations(
                    document))
            .Select(association => new
            {
                Association = association,
                RenamesInterfaceType = association.Relationship.InterfaceTarget.Identity
                    == targetIdentity,
                RenamesInterfaceMember = resolutionPolicy.CreateNameTarget(
                        GetLogicalRenameTarget(
                            association.Contract.OriginDefinition))
                    .Identity == targetIdentity
            })
            .Where(dependency => dependency.RenamesInterfaceType
                || dependency.RenamesInterfaceMember)
            .ToArray();
        if (associations.Length == 0)
        {
            return [];
        }

        var edits = new List<KeyValuePair<string, VbaTextEdit>>();
        foreach (var dependentGroup in associations.GroupBy(dependency =>
                     resolutionPolicy.CreateNameTarget(
                         GetLogicalRenameTarget(
                             dependency.Association.Implementation))
                         .Identity))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dependency = dependentGroup
                .OrderBy(candidate => candidate.Association.Implementation.Uri,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Association.Implementation.Uri,
                    StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Association.Implementation.Range.Start.Line)
                .ThenBy(candidate => candidate.Association.Implementation.Range.Start.Character)
                .First();
            var association = dependency.Association;
            var dependentTargetDefinition = GetLogicalRenameTarget(
                association.Implementation);
            var dependentTarget = resolutionPolicy.CreateNameTarget(
                dependentTargetDefinition);
            var dependentDefinitions = GetLogicalRenameTargetDefinitions(
                dependentTargetDefinition);
            var dependentName = dependency.RenamesInterfaceType
                ? $"{newName}_{association.MemberSuffix}"
                : $"{association.InterfacePrefix}_{newName}";
            var separatorOffset = association.InterfacePrefix.Length;
            foreach (var definition in dependentDefinitions.Where(definition =>
                         (association.Implementation.Kind
                                 == VbaSourceDefinitionKind.Property
                             ? definition.Kind
                                 == VbaSourceDefinitionKind.Property
                             : definition.Kind
                                     == VbaSourceDefinitionKind.Procedure
                                 && definition.CallableKind
                                     == association.Implementation.CallableKind)
                         && definition.Range.Start.Line == definition.Range.End.Line
                         && definition.Name.Equals(
                             association.Implementation.Name,
                             StringComparison.OrdinalIgnoreCase)
                         && separatorOffset > 0
                         && separatorOffset < definition.Name.Length - 1
                         && definition.Name[separatorOffset] == '_'))
            {
                var start = definition.Range.Start;
                var segmentRange = dependency.RenamesInterfaceType
                    ? new VbaRange(
                        start,
                        new VbaPosition(
                            start.Line,
                            start.Character + separatorOffset))
                    : new VbaRange(
                        new VbaPosition(
                            start.Line,
                            start.Character + separatorOffset + 1),
                        definition.Range.End);
                edits.Add(new KeyValuePair<string, VbaTextEdit>(
                    definition.Uri,
                    new VbaTextEdit(segmentRange, newName)));
            }

            foreach (var occurrence in resolvedOccurrences.FindMatching(
                         dependentTarget,
                         cancellationToken))
            {
                var isDeclarationSegment = dependentDefinitions.Any(definition =>
                    VbaProjectIdentityModel.SameDocument(
                        definition.Uri,
                        occurrence.Uri)
                    && definition.Range.Start.Line == occurrence.Range.Start.Line
                    && definition.Range.Start.Character
                        <= occurrence.Range.Start.Character
                    && occurrence.Range.End.Character
                        <= definition.Range.End.Character);
                if (isDeclarationSegment)
                {
                    continue;
                }

                edits.Add(new KeyValuePair<string, VbaTextEdit>(
                    occurrence.Uri,
                    new VbaTextEdit(occurrence.Range, dependentName)));
            }
        }

        return edits;
    }

    private IReadOnlyList<VbaHandlerEventRenameConvergence>
        GetHandlerEventRenameConvergences()
        => sourceDocuments
            .SelectMany(document => document.Definitions
                .Where(definition => definition.Kind is
                    VbaSourceDefinitionKind.Procedure
                        or VbaSourceDefinitionKind.Property)
                .Select(definition => semanticResolution
                    .AnalyzeWithEventsHandler(document, definition))
                .Where(analysis => analysis is not null)
                .Select(analysis => semanticResolution
                    .AnalyzeHandlerEventRenameConvergence(analysis!)))
            .ToArray();

    private bool TryResolveWithEventsHandlerSegment(
        VbaSourceDocument document,
        int line,
        int character,
        out VbaWithEventsHandlerSegmentResolution result)
    {
        result = null!;
        var position = new VbaPosition(line, character);
        var handler = document.Definitions.FirstOrDefault(definition =>
            definition.Kind is VbaSourceDefinitionKind.Procedure
                or VbaSourceDefinitionKind.Property
            && ContainsCharacter(definition.Range, position));
        if (handler is null
            || semanticResolution.AnalyzeWithEventsHandler(document, handler)
                is not { } analysis
            || analysis.Recognition
                == VbaWithEventsHandlerRecognition.OrdinaryProcedure
            || handler.Range.Start.Line != handler.Range.End.Line)
        {
            return false;
        }

        var separatorOffset = analysis.Decomposition.VariableName.Length;
        if (separatorOffset <= 0
            || separatorOffset >= handler.Name.Length - 1
            || handler.Name[separatorOffset] != '_')
        {
            return false;
        }

        var start = handler.Range.Start;
        var separatorCharacter = start.Character + separatorOffset;
        var prefixRange = new VbaRange(
            start,
            new VbaPosition(start.Line, separatorCharacter));
        var separatorRange = new VbaRange(
            new VbaPosition(start.Line, separatorCharacter),
            new VbaPosition(start.Line, separatorCharacter + 1));
        var suffixRange = new VbaRange(
            new VbaPosition(start.Line, separatorCharacter + 1),
            handler.Range.End);
        var completeTarget = resolutionPolicy.CreateNameTarget(
            GetLogicalRenameTarget(handler));
        if (ContainsCharacter(prefixRange, position))
        {
            result = new VbaWithEventsHandlerSegmentResolution(
                analysis.BindingSet.VariableTarget,
                prefixRange,
                completeTarget);
            return true;
        }

        if (ContainsCharacter(separatorRange, position))
        {
            result = new VbaWithEventsHandlerSegmentResolution(
                Target: null,
                separatorRange,
                completeTarget);
            return true;
        }

        var convergence = semanticResolution
            .AnalyzeHandlerEventRenameConvergence(analysis);
        result = new VbaWithEventsHandlerSegmentResolution(
            convergence.Kind
                    == VbaHandlerEventRenameConvergenceKind.Convergent
                ? convergence.EventTarget
                : null,
            suffixRange,
            completeTarget);
        return true;
    }

    private VbaWithEventsDependentRenameTarget?
        ClassifyWithEventsDependentRenameTarget(
            VbaResolvedNameTarget target)
    {
        var coverage = GetConditionalDependentRenameCoverages(
                GetHandlerEventRenameConvergences())
            .SingleOrDefault(candidate => candidate.Target.Identity
                == target.Identity);
        return coverage?.Kind
                != VbaConditionalDependentRenameCoverageKind.CompleteDependent
            ? null
            : new VbaWithEventsDependentRenameTarget(
                target,
                coverage.Associations);
    }

    private bool HasWithEventsDependentRenameVariant(
        VbaResolvedNameTarget target)
        => ClassifyWithEventsDependentRenameTarget(target) is not null
            || GetHandlerEventRenameConvergences().Any(convergence =>
                convergence.HandlerAnalysis.Recognition is
                    VbaWithEventsHandlerRecognition.ResolvedHandler
                        or VbaWithEventsHandlerRecognition
                            .NonSubProcedureAssociation
                && resolutionPolicy.CreateNameTarget(
                    GetLogicalRenameTarget(
                        convergence.HandlerAnalysis.Handler)).Identity
                    == target.Identity);

    private IReadOnlyList<VbaConditionalDependentRenameCoverage>
        GetConditionalDependentRenameCoverages(
            IReadOnlyList<VbaHandlerEventRenameConvergence> convergences)
        => convergences
            .GroupBy(convergence => resolutionPolicy.CreateNameTarget(
                GetLogicalRenameTarget(
                    convergence.HandlerAnalysis.Handler)).Identity)
            .Select(group =>
            {
                var first = group.First();
                var targetDefinition = GetLogicalRenameTarget(
                    first.HandlerAnalysis.Handler);
                var target = resolutionPolicy.CreateNameTarget(
                    targetDefinition);
                var physicalDefinitions = GetLogicalRenameTargetDefinitions(
                    targetDefinition);
                var associations = group.ToArray();
                var hasConclusiveNonDependentDefinition = false;
                var hasIndeterminateDefinition = false;
                foreach (var definition in physicalDefinitions)
                {
                    var definitionAssociations = associations
                        .Where(association => association.HandlerAnalysis
                            .Handler.Identity == definition.Identity)
                        .ToArray();
                    if (definition.Kind is not (
                            VbaSourceDefinitionKind.Procedure
                                or VbaSourceDefinitionKind.Property))
                    {
                        hasConclusiveNonDependentDefinition = true;
                        continue;
                    }

                    if (definitionAssociations.Length == 0
                        || semanticResolution
                            .HasIndeterminateConditionalCompilationOwnership(
                                definition))
                    {
                        hasIndeterminateDefinition = true;
                        continue;
                    }

                    if (definitionAssociations.Any(association =>
                            association.HandlerAnalysis.Recognition
                                == VbaWithEventsHandlerRecognition
                                    .OrdinaryProcedure))
                    {
                        hasConclusiveNonDependentDefinition = true;
                        continue;
                    }

                    if (definitionAssociations.Any(association =>
                            association.HandlerAnalysis.Recognition
                                == VbaWithEventsHandlerRecognition
                                    .IndeterminateCandidate))
                    {
                        hasIndeterminateDefinition = true;
                        continue;
                    }

                    if (definitionAssociations.Any(association =>
                            association.HandlerAnalysis.Recognition is not (
                                VbaWithEventsHandlerRecognition.ResolvedHandler
                                    or VbaWithEventsHandlerRecognition
                                        .NonSubProcedureAssociation)))
                    {
                        hasConclusiveNonDependentDefinition = true;
                    }
                }

                var kind = hasConclusiveNonDependentDefinition
                    ? VbaConditionalDependentRenameCoverageKind.ConclusiveMixed
                    : hasIndeterminateDefinition
                        ? VbaConditionalDependentRenameCoverageKind
                            .IndeterminateCoverage
                        : VbaConditionalDependentRenameCoverageKind
                            .CompleteDependent;
                return new VbaConditionalDependentRenameCoverage(
                    target,
                    kind,
                    physicalDefinitions,
                    associations);
            })
            .ToArray();

    private VbaRenameFailure?
        GetWithEventsDependentRenameCoverageFailure(
            VbaSourceDefinition target)
    {
        var logicalDefinitions = GetLogicalRenameTargetDefinitions(target);
        var targetIdentities = logicalDefinitions
            .Select(resolutionPolicy.CreateNameTarget)
            .Select(logicalTarget => logicalTarget.Identity)
            .ToHashSet();
        var hasEventTarget = logicalDefinitions.Any(definition =>
            definition.Kind == VbaSourceDefinitionKind.Event);
        var hasModuleVariableTarget = logicalDefinitions
            .Any(definition =>
                definition.Kind == VbaSourceDefinitionKind.Variable
                && definition.ParentProcedureName is null);
        if (!hasEventTarget && !hasModuleVariableTarget)
        {
            return null;
        }

        var convergences = GetHandlerEventRenameConvergences();
        var relevantEventConvergences = hasEventTarget
            ? convergences.Where(convergence => convergence.HandlerAnalysis
                    .BindingSet.ResolvedEntries
                    .SelectMany(entry => entry.ResolvedEventTargets)
                    .Any(eventTarget => targetIdentities.Contains(
                        eventTarget.Identity)))
                .ToArray()
            : [];
        if (relevantEventConvergences.Any(convergence =>
                convergence.Kind
                    == VbaHandlerEventRenameConvergenceKind
                        .ConflictingTargets))
        {
            return ResolutionChanged(
                "Rename would change a handler shared by distinct source "
                + "Event targets.");
        }

        var relevantVariableConvergences = hasModuleVariableTarget
            ? convergences
            .Where(convergence => targetIdentities.Contains(
                convergence.HandlerAnalysis.BindingSet.VariableTarget
                    .Identity))
            .ToArray()
            : [];

        var relevantDependentTargetIdentities = relevantEventConvergences
            .Concat(relevantVariableConvergences.Where(convergence =>
                convergence.HandlerAnalysis.Recognition is
                    VbaWithEventsHandlerRecognition.ResolvedHandler
                        or VbaWithEventsHandlerRecognition
                            .NonSubProcedureAssociation))
            .Select(convergence => resolutionPolicy.CreateNameTarget(
                GetLogicalRenameTarget(
                    convergence.HandlerAnalysis.Handler)).Identity)
            .ToHashSet();
        var relevantCoverage = GetConditionalDependentRenameCoverages(
                convergences)
            .Where(coverage => relevantDependentTargetIdentities.Contains(
                coverage.Target.Identity))
            .ToArray();
        if (relevantCoverage.Any(coverage => coverage.Kind
                == VbaConditionalDependentRenameCoverageKind
                    .ConclusiveMixed))
        {
            return ResolutionChanged(
                "Rename would change a conclusively mixed conditional "
                + "WithEvents dependent family.");
        }

        if (relevantCoverage.Any(coverage => coverage.Kind
                == VbaConditionalDependentRenameCoverageKind
                    .IndeterminateCoverage))
        {
            return AnalysisIncomplete(
                "Rename could not establish complete conditional "
                + "WithEvents dependent coverage.");
        }

        if (relevantEventConvergences.Any(convergence =>
                convergence.Kind
                    == VbaHandlerEventRenameConvergenceKind.Indeterminate))
        {
            return AnalysisIncomplete(
                "Rename could not establish complete conditional handler "
                + "coverage for the source Event.");
        }

        var eventDefinitions = logicalDefinitions
            .Where(definition => definition.Kind
                == VbaSourceDefinitionKind.Event)
            .ToArray();
        if (hasEventTarget
            && convergences.Any(convergence =>
                convergence.Kind
                    == VbaHandlerEventRenameConvergenceKind.Indeterminate
                && eventDefinitions.Any(eventDefinition =>
                    eventDefinition.Name.Equals(
                        convergence.HandlerAnalysis.Decomposition.EventName,
                        StringComparison.OrdinalIgnoreCase))
                && convergence.HandlerAnalysis.BindingSet.Entries.Any(entry =>
                    eventDefinitions.Any(eventDefinition =>
                        IsPotentialIndeterminateWithEventsBindingForEvent(
                            entry,
                            eventDefinition)))))
        {
            return AnalysisIncomplete(
                "Rename could not exclude an indeterminate WithEvents "
                + "handler candidate for the source Event.");
        }

        if (relevantVariableConvergences.Any(convergence =>
                convergence.HandlerAnalysis.Recognition
                    == VbaWithEventsHandlerRecognition
                        .IndeterminateCandidate
                || convergence.HandlerAnalysis.BindingSet.Entries.Any(entry =>
                    entry.Status
                        == VbaWithEventsEventBindingStatus.Indeterminate
                    || entry.HasRecoveredEventEvidence)))
        {
            return AnalysisIncomplete(
                "Rename could not establish complete WithEvents handler "
                + "ownership for every conditional variable variant.");
        }

        return null;
    }

    private bool IsPotentialIndeterminateWithEventsBindingForEvent(
        VbaWithEventsEventBindingEntry entry,
        VbaSourceDefinition eventDefinition)
    {
        if (entry.Status != VbaWithEventsEventBindingStatus.Indeterminate
            || entry.Variable.TypeReference is not { } typeReference
            || !eventDefinition.ModuleName.Equals(
                typeReference.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var variableDocument = sourceDocuments.FirstOrDefault(document =>
            VbaProjectIdentityModel.SameDocument(
                document.Uri,
                entry.Variable.Uri));
        var eligibility = variableDocument is null
            ? null
            : semanticResolution.GetWithEventsTypeEligibility(
                variableDocument,
                entry.Variable);
        return eligibility?.TypeDefinition?.Identity.Origin
            != VbaDefinitionOrigin.ProjectReference;
    }

    private IReadOnlyList<KeyValuePair<string, VbaTextEdit>>
        CreateWithEventsDependentRenameEdits(
            VbaSourceDefinition target,
            string newName,
            CancellationToken cancellationToken)
    {
        var dependencies = GetWithEventsRenameDependencies(target);
        var edits = new List<KeyValuePair<string, VbaTextEdit>>();
        foreach (var dependentGroup in dependencies.GroupBy(dependency =>
                     resolutionPolicy.CreateNameTarget(
                         GetLogicalRenameTarget(
                             dependency.Convergence.HandlerAnalysis.Handler))
                         .Identity))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dependency = dependentGroup
                .OrderBy(candidate =>
                    candidate.Convergence.HandlerAnalysis.Handler.Uri,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate =>
                    candidate.Convergence.HandlerAnalysis.Handler.Uri,
                    StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Convergence.HandlerAnalysis
                    .Handler.Range.Start.Line)
                .ThenBy(candidate => candidate.Convergence.HandlerAnalysis
                    .Handler.Range.Start.Character)
                .First();
            var analysis = dependency.Convergence.HandlerAnalysis;
            var dependentTargetDefinition = GetLogicalRenameTarget(
                analysis.Handler);
            var dependentTarget = resolutionPolicy.CreateNameTarget(
                dependentTargetDefinition);
            var dependentDefinitions = GetLogicalRenameTargetDefinitions(
                dependentTargetDefinition);
            var dependentName = dependency.RenamesVariable
                ? $"{newName}_{analysis.Decomposition.EventName}"
                : $"{analysis.Decomposition.VariableName}_{newName}";
            var separatorOffset = analysis.Decomposition.VariableName.Length;
            foreach (var definition in dependentDefinitions.Where(definition =>
                         (analysis.Handler.Kind
                                 == VbaSourceDefinitionKind.Property
                             ? definition.Kind
                                 == VbaSourceDefinitionKind.Property
                             : definition.Kind
                                     == VbaSourceDefinitionKind.Procedure
                                 && definition.CallableKind
                                     == analysis.Handler.CallableKind)
                         && definition.Range.Start.Line
                             == definition.Range.End.Line
                         && definition.Name.Equals(
                             analysis.Handler.Name,
                             StringComparison.OrdinalIgnoreCase)
                         && separatorOffset > 0
                         && separatorOffset < definition.Name.Length - 1
                         && definition.Name[separatorOffset] == '_'))
            {
                edits.Add(new KeyValuePair<string, VbaTextEdit>(
                    definition.Uri,
                    new VbaTextEdit(
                        dependency.RenamesVariable
                            ? new VbaRange(
                                definition.Range.Start,
                                new VbaPosition(
                                    definition.Range.Start.Line,
                                    definition.Range.Start.Character
                                        + separatorOffset))
                            : new VbaRange(
                                new VbaPosition(
                                    definition.Range.Start.Line,
                                    definition.Range.Start.Character
                                        + separatorOffset + 1),
                                definition.Range.End),
                        newName)));
            }

            foreach (var occurrence in resolvedOccurrences.FindMatching(
                         dependentTarget,
                         cancellationToken))
            {
                var isDeclarationSegment = dependentDefinitions.Any(definition =>
                    VbaProjectIdentityModel.SameDocument(
                        definition.Uri,
                        occurrence.Uri)
                    && definition.Range.Start.Line
                        == occurrence.Range.Start.Line
                    && definition.Range.Start.Character
                        <= occurrence.Range.Start.Character
                    && occurrence.Range.End.Character
                        <= definition.Range.End.Character);
                if (isDeclarationSegment)
                {
                    continue;
                }

                edits.Add(new KeyValuePair<string, VbaTextEdit>(
                    occurrence.Uri,
                    new VbaTextEdit(occurrence.Range, dependentName)));
            }
        }

        return edits;
    }

    private IReadOnlyList<VbaWithEventsRenameDependency>
        GetWithEventsRenameDependencies(VbaSourceDefinition target)
    {
        var logicalDefinitions = GetLogicalRenameTargetDefinitions(target);
        var targetIdentities = logicalDefinitions
            .Select(resolutionPolicy.CreateNameTarget)
            .Select(logicalTarget => logicalTarget.Identity)
            .ToHashSet();
        var hasEventTarget = logicalDefinitions.Any(definition =>
            definition.Kind == VbaSourceDefinitionKind.Event);
        var hasVariableTarget = logicalDefinitions.Any(definition =>
            definition.Kind == VbaSourceDefinitionKind.Variable
            && definition.ParentProcedureName is null);
        return GetHandlerEventRenameConvergences()
            .Select(convergence => new VbaWithEventsRenameDependency(
                convergence,
                RenamesEvent: hasEventTarget
                    && convergence.Kind
                        == VbaHandlerEventRenameConvergenceKind.Convergent
                    && convergence.EventTarget is { } eventTarget
                    && targetIdentities.Contains(eventTarget.Identity),
                RenamesVariable: hasVariableTarget
                    && targetIdentities.Contains(convergence.HandlerAnalysis
                        .BindingSet.VariableTarget.Identity)
                    && convergence.HandlerAnalysis.Recognition is
                        VbaWithEventsHandlerRecognition.ResolvedHandler
                            or VbaWithEventsHandlerRecognition
                                .NonSubProcedureAssociation))
            .Where(dependency => dependency.RenamesEvent
                || dependency.RenamesVariable)
            .ToArray();
    }

    private IReadOnlyList<VbaRenameConflict>
        FindWithEventsDependentRenameCollisions(
            VbaSourceDefinition target,
            string newName)
        => GetWithEventsRenameDependencies(target)
            .GroupBy(dependency => resolutionPolicy.CreateNameTarget(
                GetLogicalRenameTarget(
                    dependency.Convergence.HandlerAnalysis.Handler)).Identity)
            .SelectMany(dependentGroup =>
            {
                var dependency = dependentGroup.First();
                var analysis = dependency.Convergence.HandlerAnalysis;
                var dependentTarget = GetLogicalRenameTarget(
                    analysis.Handler);
                var dependentName = dependency.RenamesVariable
                    ? $"{newName}_{analysis.Decomposition.EventName}"
                    : $"{analysis.Decomposition.VariableName}_{newName}";
                return FindSameScopeCollisions(
                    dependentTarget,
                    dependentName);
            })
            .Distinct()
            .OrderBy(conflict => conflict.Uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(conflict => conflict.Uri, StringComparer.Ordinal)
            .ThenBy(conflict => conflict.Range?.Start.Line)
            .ThenBy(conflict => conflict.Range?.Start.Character)
            .ToArray();

    private IReadOnlyList<VbaRenameConflict>
        FindInterfaceDependentRenameCollisions(
            VbaSourceDefinition target,
            string newName)
    {
        var targetIdentity = resolutionPolicy.CreateNameTarget(target).Identity;
        return sourceDocuments
            .SelectMany(document => semanticResolution
                .GetConclusiveSourceInterfaceImplementationAssociations(
                    document))
            .Select(association => new
            {
                Association = association,
                RenamesInterfaceType = association.Relationship.InterfaceTarget.Identity
                    == targetIdentity,
                RenamesInterfaceMember = resolutionPolicy.CreateNameTarget(
                        GetLogicalRenameTarget(
                            association.Contract.OriginDefinition))
                    .Identity == targetIdentity
            })
            .Where(dependency => dependency.RenamesInterfaceType
                || dependency.RenamesInterfaceMember)
            .GroupBy(dependency => resolutionPolicy.CreateNameTarget(
                GetLogicalRenameTarget(
                    dependency.Association.Implementation)).Identity)
            .SelectMany(dependentGroup =>
            {
                var dependency = dependentGroup.First();
                var association = dependency.Association;
                var dependentTarget = GetLogicalRenameTarget(
                    association.Implementation);
                var dependentName = dependency.RenamesInterfaceType
                    ? $"{newName}_{association.MemberSuffix}"
                    : $"{association.InterfacePrefix}_{newName}";
                return FindSameScopeCollisions(
                    dependentTarget,
                    dependentName);
            })
            .Distinct()
            .OrderBy(conflict => conflict.Uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(conflict => conflict.Uri, StringComparer.Ordinal)
            .ThenBy(conflict => conflict.Range?.Start.Line)
            .ThenBy(conflict => conflict.Range?.Start.Character)
            .ToArray();
    }

    private IReadOnlySet<string> GetWithEventsDependentRenameNames(
        VbaSourceDefinition target,
        string newName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in GetWithEventsRenameDependencies(target))
        {
            var analysis = dependency.Convergence.HandlerAnalysis;
            names.Add(analysis.Handler.Name);
            names.Add(dependency.RenamesVariable
                ? $"{newName}_{analysis.Decomposition.EventName}"
                : $"{analysis.Decomposition.VariableName}_{newName}");
        }

        return names;
    }

    private IReadOnlySet<string> GetInterfaceDependentRenameNames(
        VbaSourceDefinition target,
        string newName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetIdentity = resolutionPolicy.CreateNameTarget(target).Identity;
        foreach (var association in sourceDocuments.SelectMany(document =>
                     semanticResolution
                         .GetConclusiveSourceInterfaceImplementationAssociations(
                             document)))
        {
            var renamesInterfaceType =
                association.Relationship.InterfaceTarget.Identity
                    == targetIdentity;
            var renamesInterfaceMember = resolutionPolicy.CreateNameTarget(
                    GetLogicalRenameTarget(
                        association.Contract.OriginDefinition))
                .Identity == targetIdentity;
            if (!renamesInterfaceType && !renamesInterfaceMember)
            {
                continue;
            }

            names.Add(association.Implementation.Name);
            names.Add(renamesInterfaceType
                ? $"{newName}_{association.MemberSuffix}"
                : $"{association.InterfacePrefix}_{newName}");
        }

        return names;
    }

    private static IReadOnlyList<VbaRenameFileOperation>
        CreateModuleIdentityFileRenames(
            VbaSourceDefinition target,
            string newName)
    {
        if (!IsModuleIdentity(target)
            || !Uri.TryCreate(target.Uri, UriKind.Absolute, out var sourceUri)
            || !sourceUri.IsFile
            || VbaProjectResolver.TryGetLocalPath(target.Uri) is not { }
                sourcePath)
        {
            return [];
        }

        var extension = Path.GetExtension(sourcePath);
        if (extension is not ".bas" and not ".cls" and not ".frm"
            && !extension.Equals(".bas", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".cls", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".frm", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        if (!Path.GetFileNameWithoutExtension(sourcePath).Equals(
            target.Name,
            StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var directory = Path.GetDirectoryName(sourcePath);
        if (directory is null)
        {
            return [];
        }

        var destinationPath = Path.Combine(directory, newName + extension);
        if (sourcePath.Equals(destinationPath, StringComparison.Ordinal))
        {
            return [];
        }

        return
        [
            new VbaRenameFileOperation(
                sourceUri.AbsoluteUri,
                new Uri(destinationPath).AbsoluteUri)
        ];
    }

    private VbaSourceDefinition? FindAuthoritativeModuleIdentityDefinitionAtPosition(
        VbaSourceDocument document,
        int line,
        int character)
    {
        var syntaxTree = document.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(document.Uri, document.Text);
        if (!TryGetAuthoritativeModuleIdentityAtPosition(
            document,
            syntaxTree,
            line,
            character,
            out var moduleIdentity))
        {
            return null;
        }

        return document.Definitions.FirstOrDefault(definition =>
            IsModuleIdentity(definition)
            && definition.Range.Start.Line == moduleIdentity.Range.Start.Line
            && definition.Range.Start.Character == moduleIdentity.Range.Start.Character
            && definition.Range.End.Line == moduleIdentity.Range.End.Line
            && definition.Range.End.Character == moduleIdentity.Range.End.Character);
    }

    private bool IsExplicitModuleIdentityTarget(VbaSourceDefinition target)
        => definitionCandidates.FindDocument(target.Uri) is { } document
            && VbaModuleIdentityDiagnostics.IsExplicitModuleIdentity(document, target);

    private VbaModuleIdentityMetadata? GetModuleIdentityMetadata(
        VbaSourceDefinition target)
    {
        var document = definitionCandidates.FindDocument(target.Uri);
        if (document is null)
        {
            return null;
        }

        var syntaxTree = document.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(document.Uri, document.Text);
        return syntaxTree.Module.Identity.Metadata
            ?? ReadModuleIdentityMetadata(document, syntaxTree);
    }

    private VbaRenameFailure? GetModuleIdentityOwnershipFailure(
        VbaSourceDefinition target)
    {
        if (!IsModuleIdentity(target))
        {
            return null;
        }

        var sourcePath = VbaProjectResolver.TryGetLocalPath(target.Uri);
        var document = definitionCandidates.FindDocument(target.Uri);
        if (projectResolution?.Kind
            != VbaProjectResolutionKind.ManifestDocument)
        {
            return null;
        }

        if (sourcePath is null)
        {
            return null;
        }

        var installedModule = projectResolution.InstalledCommonModuleEntries
            .FirstOrDefault(module => PathsEqual(
                sourcePath,
                Path.Combine(projectResolution.RootPath, module.ModuleFile)));
        if (installedModule is not null)
        {
            return new VbaRenameFailure(
                "managedModuleIdentity",
                $"Module identity '{target.Name}' is managed by the CommonModules installation contract.",
                Path: sourcePath,
                Guidance: "Rename it in the canonical CommonModules source or explicitly detach it into project-local source first.");
        }

        return null;
    }

    private VbaRenameFailure? GetModuleIdentityMutationAuthorityFailure(
        VbaSourceDefinition target,
        VbaProjectIdentityReadResult? projectIdentityRead)
    {
        if (!IsModuleIdentity(target) || projectResolution is null)
        {
            return null;
        }

        if (projectResolution.Kind == VbaProjectResolutionKind.ManifestDocument
            && projectIdentityRead?.Identity is null)
        {
            return new VbaRenameFailure(
                "analysisIncomplete",
                "Module identity Rename requires a readable containing VBProject.Name from the exact source-template package.",
                Condition: "containingProjectNameUnavailable",
                Path: projectResolution.SourceTemplatePath,
                Guidance: "Restore or re-export a valid supported unencrypted source template, then retry Rename.");
        }

        foreach (var referenceName in GetActiveReferenceNamesInSelectionOrder())
        {
            if (!TryGetCurrentReferencedProjectName(referenceName, out _))
            {
                return new VbaRenameFailure(
                    "analysisIncomplete",
                    $"Module identity Rename requires a current authoritative "
                    + $"ReferencedVbaProjectName for active reference '{referenceName}'.",
                    Condition: "referenceProjectNameUnavailable",
                    Guidance: "Refresh the exact active reference selection and retry Rename.");
            }
        }

        return null;
    }

    private static string CreateRenameConflictDescription(
        VbaRenameConflict conflict)
        => conflict.Uri is not null && conflict.Range is not null
            ? $"'{conflict.Name}' at {conflict.Uri}:"
                + $"{conflict.Range.Start.Line + 1}:"
                + $"{conflict.Range.Start.Character + 1}"
            : $"'{conflict.Name}' ({conflict.CollisionKind})";

    private static string CreateRenameCollisionMessage(
        VbaSourceDefinition target,
        string newName,
        IReadOnlyList<VbaRenameConflict> conflicts,
        string locations)
    {
        if (!IsModuleIdentity(target))
        {
            return $"Rename to '{newName}' conflicts with declarations {locations}.";
        }

        if (conflicts.Count == 1)
        {
            return conflicts[0].CollisionKind switch
            {
                "containingProject" =>
                    $"Module name '{newName}' conflicts with containing VBA project "
                    + $"'{conflicts[0].Name}'.",
                "referencedProject" =>
                    $"Module name '{newName}' conflicts with referenced project or "
                    + $"object library '{conflicts[0].Name}'.",
                _ => $"Module name '{newName}' conflicts with source declaration "
                    + $"{CreateRenameConflictDescription(conflicts[0])}."
            };
        }

        var descriptions = conflicts.Select(conflict => conflict.CollisionKind switch
        {
            "sourceDeclaration" =>
                $"source declaration {CreateRenameConflictDescription(conflict)}",
            "containingProject" => $"containing VBA project '{conflict.Name}'",
            "referencedProject" =>
                $"referenced project or object library '{conflict.Name}'",
            _ => CreateRenameConflictDescription(conflict)
        });
        return $"Module name '{newName}' conflicts with "
            + string.Join(", ", descriptions)
            + ".";
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return Path.GetFullPath(left).Equals(
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    private static VbaRenameFailure CreateInvalidModuleIdentityFailure(
        VbaModuleIdentityMetadata metadata)
        => new(
            "moduleIdentityInvalid",
            "Module identity metadata is invalid; re-export or repair the "
            + "source before Rename.",
            Condition: metadata.Condition switch
            {
                VbaModuleIdentityMetadataCondition.Duplicate => "duplicate",
                _ => "malformed"
            });

    private IEnumerable<VbaResolvedIdentifierOccurrence>
        CreateModuleIdentityDeclarationOccurrences(VbaSourceDefinition target)
    {
        foreach (var definition in GetLogicalRenameTargetDefinitions(target))
        {
            var logicalTarget = resolutionPolicy.CreateNameTarget(definition);
            yield return new VbaResolvedIdentifierOccurrence(
                definition.Uri,
                new VbaIdentifierOccurrence(
                    definition.Name,
                    definition.Range.Start.Character,
                    definition.Range.End.Character),
                definition.Range,
                logicalTarget);
        }
    }

    private bool HasIntrinsicHostEventAlternative(
        IReadOnlyList<VbaResolvedIdentifierOccurrence> targetOccurrences,
        CancellationToken cancellationToken)
    {
        var targetRanges = targetOccurrences
            .Select(occurrence => CreateOccurrenceKey(
                occurrence.Uri,
                occurrence.Range))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return resolvedOccurrences.GetAll(cancellationToken).Any(occurrence =>
            targetRanges.Contains(CreateOccurrenceKey(
                occurrence.Uri,
                occurrence.Range))
            && occurrence.Target is VbaHostEventNameTarget);
    }

    private IReadOnlyList<VbaRenameConflict> FindSameScopeCollisions(
        VbaSourceDefinition target,
        string newName,
        VbaProjectIdentityReadResult? projectIdentityRead = null)
    {
        var physicalTargets = GetLogicalRenameTargetDefinitions(target);
        var candidates = sourceDocuments
            .SelectMany(document => document.Definitions)
            .Where(candidate => string.Equals(
                candidate.Name,
                newName,
                StringComparison.OrdinalIgnoreCase))
            .Where(candidate => physicalTargets.Any(physical =>
                IsSameDeclarationScope(physical, candidate)))
            .Where(candidate => physicalTargets.All(physical =>
                physical.Identity != candidate.Identity))
            .ToArray();
        var conflicts = VbaPropertyAccessorCoalescing.Coalesce(candidates)
            .OrderBy(candidate => candidate.Uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Uri, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Range.Start.Line)
            .ThenBy(candidate => candidate.Range.Start.Character)
            .Select(candidate => new VbaRenameConflict(
                "sourceDeclaration",
                candidate.Name,
                candidate.Uri,
                candidate.Range))
            .ToList();
        if (IsModuleIdentity(target)
            && projectIdentityRead?.Identity?.VbaProjectName is { } projectName
            && projectName.Equals(newName, StringComparison.OrdinalIgnoreCase))
        {
            conflicts.Add(new VbaRenameConflict(
                "containingProject",
                projectName,
                projectResolution?.SourceTemplatePath is { } sourceTemplatePath
                    ? new Uri(Path.GetFullPath(sourceTemplatePath)).AbsoluteUri
                    : null,
                Range: null));
        }

        if (IsModuleIdentity(target))
        {
            foreach (var referenceName in GetActiveReferenceNamesInSelectionOrder())
            {
                if (!TryGetCurrentReferencedProjectName(
                        referenceName,
                        out var referencedProjectName)
                    || !referencedProjectName.Equals(
                        newName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                conflicts.Add(new VbaRenameConflict(
                    "referencedProject",
                    referencedProjectName,
                    Uri: null,
                    Range: null,
                    ReferenceName: referenceName));
            }
        }

        return conflicts;
    }

    private IEnumerable<string> GetActiveReferenceNamesInSelectionOrder()
    {
        var seen = new HashSet<string>(VbaProjectReferenceName.Comparer);
        if (referenceCatalogs.FindCatalog(
                VbaProjectReferenceCatalogSet.StandardLibraryReferenceName)
            is not null
            && seen.Add(VbaProjectReferenceCatalogSet.StandardLibraryReferenceName))
        {
            yield return VbaProjectReferenceCatalogSet.StandardLibraryReferenceName;
        }

        if (referenceSelection is null)
        {
            yield break;
        }

        foreach (var reference in referenceSelection.References)
        {
            if (seen.Add(reference.Name))
            {
                yield return reference.Name;
            }
        }
    }

    private bool TryGetCurrentReferencedProjectName(
        string referenceName,
        out string referencedProjectName)
    {
        referencedProjectName = string.Empty;
        return authoritativeReferencedProjectNames.TryGetValue(
            referenceName,
            out referencedProjectName!);
    }

    private bool HasIndeterminateConditionalFamilyCoverage(
        VbaSourceDefinition target)
    {
        var knownTargetDefinitions = GetLogicalRenameTargetDefinitions(target);
        return sourceDocuments
            .SelectMany(document => document.Definitions)
            .Where(candidate => candidate.Name.Equals(
                target.Name,
                StringComparison.OrdinalIgnoreCase))
            .Where(semanticResolution
                .HasIndeterminateConditionalCompilationOwnership)
            .Any(candidate => knownTargetDefinitions.Any(definition =>
                IsSameDeclarationScope(definition, candidate)));
    }

    private bool HasIncompleteSourceInterfaceDependentCoverage(
        VbaSourceDefinition target)
    {
        var targetIdentities = GetLogicalRenameTargetDefinitions(target)
            .Select(resolutionPolicy.CreateNameTarget)
            .Select(logicalTarget => logicalTarget.Identity)
            .ToHashSet();
        var analyses = sourceDocuments
            .Select(semanticResolution
                .AnalyzeSourceInterfaceImplementationAssociations)
            .ToArray();
        if (analyses
            .SelectMany(analysis => analysis.IncompleteUpstreamTargets)
            .Any(incompleteTarget =>
                targetIdentities.Contains(incompleteTarget.Identity)))
        {
            return true;
        }

        var incompleteDependentIdentities = analyses
            .SelectMany(analysis => analysis.IncompleteDependentTargets)
            .Select(incompleteTarget => incompleteTarget.Identity)
            .ToHashSet();
        return incompleteDependentIdentities.Count > 0
            && analyses
                .SelectMany(analysis => analysis.Associations)
                .Any(association => incompleteDependentIdentities.Contains(
                        association.ImplementationTarget.Identity)
                    && (targetIdentities.Contains(
                            association.Relationship.InterfaceTarget.Identity)
                        || targetIdentities.Contains(
                            resolutionPolicy.CreateNameTarget(
                                GetLogicalRenameTarget(
                                    association.Contract.OriginDefinition))
                                .Identity)));
    }

    private VbaSourceDefinition GetLogicalRenameTarget(
        VbaSourceDefinition target)
    {
        var definitions = GetLogicalRenameTargetDefinitions(target);
        if (definitions.Count == 1)
        {
            return definitions[0];
        }

        var logicalTarget = resolutionPolicy.CreateNameTarget(target);
        var canonicalName = logicalTarget is VbaPropertyNameTarget propertyTarget
            ? propertyTarget.Property.CanonicalName
            : logicalTarget.CanonicalName;
        return definitions.FirstOrDefault(definition => string.Equals(
                definition.Name,
                canonicalName,
                StringComparison.Ordinal))
            ?? definitions[0];
    }

    private IReadOnlyList<VbaRenameConflict> FindInvalidPropertyFamilyConflicts(
        VbaSourceDefinition target)
    {
        if (target.Kind != VbaSourceDefinitionKind.Property)
        {
            return [];
        }

        var candidates = GetPropertyFamilyCandidates(target);
        var logicalCandidates = definitionCandidates.ConditionalFamilies
            .Coalesce(candidates);
        if (logicalCandidates.Count <= 1
            || VbaPropertyAccessorCoalescing
                .Coalesce(logicalCandidates).Count == 1)
        {
            return [];
        }

        var repeatedAccessorCandidates = logicalCandidates
            .GroupBy(candidate => candidate.PropertyAccessorKind)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToArray();
        var conflicts = repeatedAccessorCandidates.Length > 0
            ? repeatedAccessorCandidates
            : logicalCandidates;
        return conflicts
            .Where(candidate => !AreMembersOfSameRenameTarget(
                target,
                candidate))
            .OrderBy(candidate => candidate.Uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Range.Start.Line)
            .ThenBy(candidate => candidate.Range.Start.Character)
            .Select(candidate => new VbaRenameConflict(
                "sourceDeclaration",
                candidate.Name,
                candidate.Uri,
                candidate.Range))
            .ToArray();
    }

    private IReadOnlyList<VbaSourceDefinition> GetLogicalRenameTargetDefinitions(
        VbaSourceDefinition target)
    {
        var logicalTarget = resolutionPolicy.CreateNameTarget(target);
        return logicalTarget is VbaPropertyNameTarget propertyTarget
            ? propertyTarget.Property.UnifiedPhysicalDefinitions
            : logicalTarget.PhysicalDefinitions;
    }

    private VbaSourceDefinition[] GetPropertyFamilyCandidates(
        VbaSourceDefinition target)
        => sourceDocuments
            .SelectMany(document => document.Definitions)
            .Where(candidate => candidate.Kind == VbaSourceDefinitionKind.Property)
            .Where(candidate => VbaProjectIdentityModel.SameDocument(
                candidate.Uri,
                target.Uri))
            .Where(candidate => string.Equals(
                candidate.ModuleName,
                target.ModuleName,
                StringComparison.OrdinalIgnoreCase))
            .Where(candidate => string.Equals(
                candidate.Name,
                target.Name,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private bool IsSameDeclarationScope(
        VbaSourceDefinition target,
        VbaSourceDefinition candidate)
        => VbaDeclarationRelationshipPolicy.ShareDeclarationSpace(target, candidate)
            || definitionCandidates.ConditionalFamilies.HaveSameLogicalMemberScope(target, candidate);

    private static bool IsModuleIdentity(VbaSourceDefinition definition)
        => definition.Kind is VbaSourceDefinitionKind.Module
            or VbaSourceDefinitionKind.Class
            or VbaSourceDefinitionKind.Form;

    private static bool IsAtOrAfter(VbaPosition left, VbaPosition right)
        => left.Line > right.Line
            || left.Line == right.Line
                && left.Character >= right.Character;

    private static bool Contains(VbaRange range, VbaPosition position)
        => IsAtOrAfter(position, range.Start)
            && IsAtOrAfter(range.End, position);

    private bool AreMembersOfSameRenameTarget(
        VbaSourceDefinition target,
        VbaSourceDefinition candidate)
        => GetLogicalRenameTargetDefinitions(target)
            .Any(definition => definition.Identity == candidate.Identity);

    private VbaRenameFailure? ProveBindingsArePreserved(
        VbaSourceDefinition target,
        IReadOnlyList<VbaResolvedIdentifierOccurrence> targetOccurrences,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        string newName,
        CancellationToken cancellationToken,
        out VbaRenameTargetCorrespondence? targetCorrespondence,
        VbaRenameCollisionProof? collisionProof = null)
    {
        targetCorrespondence = null;
        cancellationToken.ThrowIfCancellationRequested();
        if (targetOccurrences.Count == 0)
        {
            return AnalysisIncomplete(
                "Rename could not establish the complete target occurrence set.");
        }

        var hypothetical = CreateHypotheticalInventory(changes, cancellationToken);
        var interfaceAssociationFailure =
            ProveSourceInterfaceAssociationsArePreserved(
                hypothetical,
                changes,
                collisionProof);
        if (interfaceAssociationFailure is not null)
        {
            return interfaceAssociationFailure;
        }

        var withEventsAssociationFailure =
            ProveWithEventsAssociationsArePreserved(
                hypothetical,
                changes,
                collisionProof);
        if (withEventsAssociationFailure is not null)
        {
            return withEventsAssociationFailure;
        }

        var hypotheticalTarget = FindHypotheticalDefinition(
            hypothetical,
            target,
            changes,
            newName);
        if (hypotheticalTarget is null)
        {
            return AnalysisIncomplete(
                "Rename could not establish the renamed target declaration.");
        }

        var correspondenceFailure = TryCreateTargetCorrespondence(
            hypothetical,
            target,
            hypotheticalTarget,
            changes,
            newName,
            out targetCorrespondence,
            collisionProof);
        if (correspondenceFailure is not null)
        {
            return correspondenceFailure;
        }

        if (targetCorrespondence is null)
        {
            return AnalysisIncomplete(
                "Rename could not retain the target correspondence proof.");
        }

        var targetRanges = targetOccurrences
            .Select(occurrence => CreateOccurrenceKey(occurrence.Uri, occurrence.Range))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var occurrenceTargetCorrespondences = new List<
            VbaRenameOccurrenceTargetCorrespondence>(targetOccurrences.Count);
        foreach (var occurrence in targetOccurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mappedRange = MapRange(occurrence.Uri, occurrence.Range, changes);
            VbaResolvedNameTarget? postTarget;
            if (IsDeclarationOccurrence(occurrence))
            {
                var postDefinition = FindHypotheticalDefinition(
                    hypothetical,
                    occurrence.Target.SelectedDefinition,
                    changes,
                    newName);
                postTarget = postDefinition is null
                    ? null
                    : hypothetical.resolutionPolicy.CreateNameTarget(
                        postDefinition);
            }
            else
            {
                postTarget = hypothetical.ResolveSourceTarget(
                    occurrence.Uri,
                    mappedRange.Start.Line,
                    mappedRange.Start.Character);
            }

            if (!AreLogicalDefinitionsEquivalent(
                hypothetical,
                postTarget?.SelectedDefinition,
                hypotheticalTarget))
            {
                if (TryRecordCollisionBindingImpact(
                    hypothetical, target, occurrence, mappedRange, changes,
                    collisionProof, VbaRenameImpactKind.TargetBindingChanged)
                    || TryRecordSourceModuleQualifierCollisionImpact(
                        hypothetical, target, occurrence, mappedRange, changes,
                        collisionProof, VbaRenameImpactKind.TargetBindingChanged)
                    || TryRecordDirectMemberCollisionImpact(
                        hypothetical, target, occurrence, mappedRange, changes,
                        collisionProof, VbaRenameImpactKind.TargetBindingChanged))
                {
                    continue;
                }
                return ResolutionChanged(
                    "Rename would change the binding of a target occurrence.");
            }

            if (postTarget is null)
            {
                return AnalysisIncomplete(
                    "Rename could not establish a target occurrence "
                    + "correspondence.");
            }

            var occurrenceCorrespondenceFailure =
                TryCreateOccurrenceTargetCorrespondence(
                    occurrence,
                    mappedRange,
                    postTarget,
                    targetCorrespondence,
                    out var occurrenceTargetCorrespondence);
            if (occurrenceCorrespondenceFailure is not null)
            {
                if (TryRecordCollisionBindingImpact(
                    hypothetical, target, occurrence, mappedRange, changes,
                    collisionProof, VbaRenameImpactKind.TargetBindingChanged))
                {
                    continue;
                }
                return occurrenceCorrespondenceFailure;
            }

            occurrenceTargetCorrespondences.Add(
                occurrenceTargetCorrespondence!);
        }

        foreach (var occurrence in resolvedOccurrences.GetAll(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (targetRanges.Contains(
                CreateOccurrenceKey(occurrence.Uri, occurrence.Range)))
            {
                continue;
            }

            var mappedOccurrenceRange = MapRange(
                occurrence.Uri,
                occurrence.Range,
                changes);
            if (occurrence.Target is VbaHostEventNameTarget hostEventTarget)
            {
                var projectedPostTarget = hypothetical.ResolveSourceTarget(
                    occurrence.Uri,
                    mappedOccurrenceRange.Start.Line,
                    mappedOccurrenceRange.Start.Character);
                var postHostTargets = projectedPostTarget switch
                {
                    VbaHostEventNameTarget projectedHostTarget =>
                        [projectedHostTarget],
                    VbaWithEventsEventNameTarget projectedWithEventsTarget =>
                        projectedWithEventsTarget.EventTargets
                        .OfType<VbaHostEventNameTarget>()
                        .ToArray(),
                    _ => []
                };
                var matchingPostHostTargets = postHostTargets
                    .Where(target => target.HostEventIdentity
                        == hostEventTarget.HostEventIdentity)
                    .ToArray();
                if (matchingPostHostTargets.Length != 1)
                {
                    return ResolutionChanged(
                        "Rename would change an existing intrinsic UserForm "
                        + "Event binding.");
                }

                var postHostTarget = matchingPostHostTargets[0];
                if (postHostTarget.EventContract.Identity
                        != hostEventTarget.EventContract.Identity
                    || postHostTarget.EventContract.ValidationAuthority
                        != hostEventTarget.EventContract.ValidationAuthority)
                {
                    return ResolutionChanged(
                        "Rename would change intrinsic UserForm Event authority.");
                }

                continue;
            }

            var postDefinition = IsDeclarationOccurrence(occurrence)
                ? FindHypotheticalDefinition(
                    hypothetical,
                    occurrence.Target.SelectedDefinition,
                    changes)
                : hypothetical.ResolveSourceDefinition(
                    occurrence.Uri,
                    mappedOccurrenceRange.Start.Line,
                    mappedOccurrenceRange.Start.Character);
            var expectedDefinition = FindHypotheticalDefinition(
                hypothetical,
                occurrence.Target.SelectedDefinition,
                changes);
            if (expectedDefinition is null)
            {
                return AnalysisIncomplete(
                    "Rename could not establish a non-target declaration correspondence.");
            }

            if (!AreLogicalDefinitionsEquivalent(
                hypothetical,
                postDefinition,
                expectedDefinition))
            {
                if (TryRecordCollisionBindingImpact(
                    hypothetical, target, occurrence, mappedOccurrenceRange, changes,
                    collisionProof, VbaRenameImpactKind.NonTargetBindingChanged)
                    || TryRecordDependentCollisionBindingImpact(
                        hypothetical, occurrence, mappedOccurrenceRange, changes, collisionProof)
                    || TryRecordReceiverTypeCollisionImpact(
                        hypothetical, occurrence, mappedOccurrenceRange, changes, collisionProof)
                    || TryRecordSourceModuleQualifierCollisionImpact(
                        hypothetical, target, occurrence, mappedOccurrenceRange, changes,
                        collisionProof, VbaRenameImpactKind.NonTargetBindingChanged)
                    || TryRecordReferencedProjectQualifierCollisionImpact(
                        hypothetical, target, occurrence.Uri, occurrence.Range, mappedOccurrenceRange,
                        VbaNameResolutionOutcome.Resolved(occurrence.Target), changes,
                        collisionProof, VbaRenameImpactKind.NonTargetBindingChanged)
                    || TryRecordDirectMemberCollisionImpact(
                        hypothetical, target, occurrence, mappedOccurrenceRange, changes,
                        collisionProof, VbaRenameImpactKind.NonTargetBindingChanged))
                {
                    continue;
                }
                return ResolutionChanged(
                    "Rename would change an existing non-target binding.");
            }
        }

        var affectedSemanticNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            target.Name,
            newName
        };
        affectedSemanticNames.UnionWith(
            GetInterfaceDependentRenameNames(target, newName));
        affectedSemanticNames.UnionWith(
            GetWithEventsDependentRenameNames(target, newName));
        foreach (var occurrence in GetUnresolvedSemanticOccurrences(
            cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!affectedSemanticNames.Contains(occurrence.Name))
            {
                continue;
            }

            var mappedRange = MapRange(
                occurrence.Uri,
                occurrence.Range,
                changes);
            var postClassification = hypothetical.semanticResolution
                .ClassifySourceDefinition(
                occurrence.Uri,
                mappedRange.Start.Line,
                mappedRange.Start.Character);
            if (occurrence.Classification
                    == VbaNameResolutionKind.AnalysisIncomplete
                || postClassification.Kind
                    == VbaNameResolutionKind.AnalysisIncomplete)
            {
                return AnalysisIncomplete(
                    "Rename could not completely classify an affected "
                    + "non-target semantic occurrence.");
            }

            if (occurrence.Classification != postClassification.Kind)
            {
                if (TryRecordCollisionClassificationImpact(
                    hypothetical, target, occurrence, mappedRange, postClassification, changes, collisionProof)
                    || TryRecordReferencedProjectQualifierCollisionImpact(
                        hypothetical, target, occurrence.Uri, occurrence.Range, mappedRange,
                        new VbaNameResolutionOutcome(occurrence.Classification, Target: null), changes,
                        collisionProof, VbaRenameImpactKind.ClassificationChanged))
                {
                    continue;
                }
                return ResolutionChanged(
                    "Rename would change the unresolved or ambiguous "
                    + "classification of a non-target occurrence.");
            }
        }

        var declaredTypeFailure = ProveEffectiveDeclaredTypesArePreserved(
            hypothetical, changes, cancellationToken, collisionProof);
        if (declaredTypeFailure is not null)
        {
            return declaredTypeFailure;
        }

        var callCompatibilityFailure =
            ProveConditionalCallCompatibilitiesArePreserved(
                hypothetical,
                targetOccurrences,
                changes,
                targetCorrespondence,
                cancellationToken,
                out var callCompatibilities);
        if (callCompatibilityFailure is not null)
        {
            return callCompatibilityFailure;
        }

        targetCorrespondence = targetCorrespondence with
        {
            CallCompatibilities = callCompatibilities,
            OccurrenceTargets = Array.AsReadOnly(
                occurrenceTargetCorrespondences.ToArray())
        };

        return null;
    }

    private bool TryRecordCollisionClassificationImpact(
        VbaSemanticInventory hypothetical,
        VbaSourceDefinition target,
        VbaSemanticOccurrence occurrence,
        VbaRange mappedRange,
        VbaNameResolutionOutcome after,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        VbaRenameCollisionProof? proof)
    {
        if (proof is null || occurrence.Classification is not (VbaNameResolutionKind.Unresolved or VbaNameResolutionKind.Ambiguous)
            || after.Kind != VbaNameResolutionKind.Resolved || after.Target is null
            || after.Target.PhysicalDefinitions.Count == 0)
        {
            return false;
        }

        var controlRange = MapRange(occurrence.Uri, occurrence.Range, proof.ControlPlan.Changes);
        var control = proof.ControlInventory.semanticResolution.ClassifySourceDefinition(
            occurrence.Uri, controlRange.Start.Line, controlRange.Start.Character);
        if (control.Kind != occurrence.Classification)
        {
            return false;
        }

        var causes = CreateCollisionDeclarationCorrespondences(hypothetical, target, changes, proof);
        if (causes is null || !after.Target.PhysicalDefinitions.All(definition =>
                causes.Any(cause => cause.AfterDefinition.Identity == definition.Identity))
            || !after.Target.PhysicalDefinitions.Any(definition => causes.Any(cause =>
                cause.AfterDefinition.Identity == definition.Identity
                && !cause.BeforeDefinition.Name.Equals(cause.AfterDefinition.Name, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        if (occurrence.Classification == VbaNameResolutionKind.Ambiguous)
        {
            var before = semanticResolution.ClassifySourceDefinition(
                occurrence.Uri, occurrence.Range.Start.Line, occurrence.Range.Start.Character);
            var beforeDefinitions = before.AmbiguousCandidates.SelectMany(candidate => candidate.PhysicalDefinitions)
                .Select(definition => definition.Identity).ToHashSet();
            var controlDefinitions = control.AmbiguousCandidates.SelectMany(candidate => candidate.PhysicalDefinitions)
                .Select(definition => definition.Identity).ToHashSet();
            var candidateCauses = causes.Where(cause => beforeDefinitions.Contains(cause.BeforeDefinition.Identity)).ToArray();
            if (before.Kind != VbaNameResolutionKind.Ambiguous
                || before.AmbiguousCandidates.Count < 2 || control.AmbiguousCandidates.Count < 2
                || candidateCauses.Length != beforeDefinitions.Count
                || !controlDefinitions.SetEquals(candidateCauses.Select(cause => cause.ControlDefinition.Identity)))
            {
                return false;
            }
        }

        proof.Impacts.Add(new VbaRenameImpact(
            VbaRenameImpactKind.ClassificationChanged,
            "The confirmed declaration collision resolves this previously unresolved or ambiguous reference; manual consolidation may be required.",
            occurrence.Uri, occurrence.Range)
        {
            Evidence = new VbaRenameImpactEvidence(
                occurrence.Classification, control.Kind, after.Kind,
                controlRange, mappedRange, causes)
        });
        return true;
    }

    private IEnumerable<VbaRenameConflict> FindBindingCaptureCollisions(
        VbaSourceDefinition target,
        string newName,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        CancellationToken cancellationToken)
    {
        var targets = GetLogicalRenameTargetDefinitions(target);
        var candidates = sourceDocuments.SelectMany(document => document.Definitions)
            .Where(definition => definition.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)
                && targets.All(original => original.Identity != definition.Identity
                    && !IsSameDeclarationScope(original, definition)))
            .ToArray();
        if (candidates.Length == 0)
        {
            yield break;
        }
        var hypothetical = CreateHypotheticalInventory(changes, cancellationToken);
        var mappedTargets = targets.Select(definition => FindHypotheticalDefinition(hypothetical, definition, changes))
            .Where(definition => definition is not null).Select(definition => definition!.Identity).ToHashSet();
        var candidateMappings = candidates.Select(definition => new
        {
            Before = definition,
            After = FindHypotheticalDefinition(hypothetical, definition, changes)
        }).Where(pair => pair.After is not null).ToArray();
        foreach (var occurrence in resolvedOccurrences.GetAll(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsDeclarationOccurrence(occurrence))
            {
                continue;
            }
            var mappedRange = MapRange(occurrence.Uri, occurrence.Range, changes);
            var after = hypothetical.ResolveSourceDefinition(
                occurrence.Uri, mappedRange.Start.Line, mappedRange.Start.Character);
            if (after is null)
            {
                var classification = hypothetical.semanticResolution.ClassifySourceDefinition(
                    occurrence.Uri, mappedRange.Start.Line, mappedRange.Start.Character);
                var originalBindings = occurrence.Target.PhysicalDefinitions
                    .Select(definition => definition.Identity).ToHashSet();
                var capturedCandidates = candidateMappings
                    .Where(pair => originalBindings.Contains(pair.Before.Identity)).ToArray();
                if (classification.Kind == VbaNameResolutionKind.Ambiguous
                    && mappedTargets.Count == targets.Count
                    && capturedCandidates.Length > 0
                    && capturedCandidates.Length == originalBindings.Count
                    && capturedCandidates.All(pair => pair.After!.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var candidate in capturedCandidates)
                    {
                        yield return new VbaRenameConflict(
                            "bindingCapture", candidate.Before.Name, candidate.Before.Uri, candidate.Before.Range);
                    }
                }
                continue;
            }
            var before = occurrence.Target.SelectedDefinition;
            var collision = mappedTargets.Contains(after.Identity)
                ? candidateMappings.FirstOrDefault(pair => pair.Before.Identity == before.Identity)?.Before
                : targets.Any(definition => definition.Identity == before.Identity)
                    ? candidateMappings.FirstOrDefault(pair => pair.After!.Identity == after.Identity)?.Before
                    : null;
            if (collision is not null)
            {
                yield return new VbaRenameConflict("bindingCapture", collision.Name, collision.Uri, collision.Range);
            }
        }

        foreach (var occurrence in GetUnresolvedSemanticOccurrences(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (occurrence.Classification != VbaNameResolutionKind.Ambiguous
                || !occurrence.Name.Equals(newName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var mappedRange = MapRange(occurrence.Uri, occurrence.Range, changes);
            var after = hypothetical.semanticResolution.ClassifySourceDefinition(
                occurrence.Uri, mappedRange.Start.Line, mappedRange.Start.Character);
            if (after.Kind != VbaNameResolutionKind.Resolved || after.Target is null
                || after.Target.PhysicalDefinitions.Count == 0 || mappedTargets.Count != targets.Count
                || !after.Target.PhysicalDefinitions.All(definition => mappedTargets.Contains(definition.Identity)))
            {
                continue;
            }

            var before = semanticResolution.ClassifySourceDefinition(
                occurrence.Uri, occurrence.Range.Start.Line, occurrence.Range.Start.Character);
            var originalBindings = before.AmbiguousCandidates.SelectMany(candidate => candidate.PhysicalDefinitions)
                .Select(definition => definition.Identity).ToHashSet();
            var capturedCandidates = candidateMappings.Where(pair => originalBindings.Contains(pair.Before.Identity)).ToArray();
            if (before.Kind != VbaNameResolutionKind.Ambiguous || before.AmbiguousCandidates.Count < 2
                || capturedCandidates.Length != originalBindings.Count)
            {
                continue;
            }
            foreach (var candidate in capturedCandidates)
            {
                yield return new VbaRenameConflict(
                    "bindingCapture", candidate.Before.Name, candidate.Before.Uri, candidate.Before.Range);
            }
        }
    }

    private VbaRenameFailure? TryCreateCollisionControlProof(
        string uri,
        int line,
        int character,
        VbaSourceDefinition target,
        string newName,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        IReadOnlyList<VbaRenameConflict> conflicts,
        VbaProjectIdentityReadResult? projectIdentityRead,
        bool retainOriginalPaths,
        CancellationToken cancellationToken,
        out VbaRenameCollisionProof? proof)
    {
        proof = null;
        var sourceNames = sourceDocuments.SelectMany(document =>
                (document.SyntaxTree ?? VbaSyntaxTree.ParseModule(document.Uri, document.Text))
                    .TokenStream.Tokens.Select(token => token.Text))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var initial = newName.EnumerateRunes().First().ToString();
        string controlName;
        for (var suffix = 0; ; suffix++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            controlName = initial + "RenameProof" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!sourceNames.Contains(controlName)
                && ValidateRenameName(controlName) is null
                && ValidateRenameTargetName(target, controlName) is null
                && FindSameScopeCollisions(target, controlName, projectIdentityRead).Count == 0
                && !FindInterfaceDependentRenameCollisions(target, controlName).Any()
                && !FindWithEventsDependentRenameCollisions(target, controlName).Any())
            {
                break;
            }
        }

        var control = CreateRenameResult(
            uri, line, character, controlName, cancellationToken, projectIdentityRead,
            VbaRenameCollisionMode.Reject, retainOriginalPaths);
        if (control.Failure is not null)
        {
            return control.Failure;
        }
        if (control.Plan?.TargetCorrespondence is null)
        {
            return AnalysisIncomplete("Rename could not establish an independent declaration correspondence before collision review.");
        }

        var originalClosure = changes.SelectMany(pair => pair.Value.Select(edit => CreateOccurrenceKey(pair.Key, edit.Range)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var controlClosure = control.Plan.Changes.SelectMany(pair => pair.Value.Select(edit => CreateOccurrenceKey(pair.Key, edit.Range)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!originalClosure.SetEquals(controlClosure))
        {
            return AnalysisIncomplete("Rename could not retain the same complete pre-edit closure for its independent collision proof.");
        }

        proof = new VbaRenameCollisionProof(
            conflicts,
            CreateHypotheticalInventory(control.Plan.Changes, cancellationToken),
            control.Plan);
        return null;
    }

    private sealed class VbaRenameCollisionProof(
        IReadOnlyList<VbaRenameConflict> conflicts,
        VbaSemanticInventory controlInventory,
        VbaRenamePlan controlPlan)
    {
        public IReadOnlyList<VbaRenameConflict> Conflicts { get; } = conflicts;
        public VbaSemanticInventory ControlInventory { get; } = controlInventory;
        public VbaRenamePlan ControlPlan { get; } = controlPlan;

        public List<VbaRenameImpact> Impacts { get; } = conflicts.Select(conflict => new VbaRenameImpact(
            VbaRenameImpactKind.DeclarationCollision,
            $"The requested name collides with {CreateRenameConflictDescription(conflict)}; manual consolidation may be required.",
            conflict.Uri,
            conflict.Range)).ToList();
    }

    private IReadOnlyList<VbaRenameCausalDefinitionCorrespondence>? CreateCollisionDeclarationCorrespondences(
        VbaSemanticInventory hypothetical,
        VbaSourceDefinition target,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        VbaRenameCollisionProof? proof)
    {
        if (proof is null)
        {
            return null;
        }
        var originalDefinitions = GetLogicalRenameTargetDefinitions(target);
        var actualTarget = FindHypotheticalDefinition(hypothetical, target, changes);
        if (actualTarget is null)
        {
            return null;
        }
        var collidingDefinitions = sourceDocuments.SelectMany(document => document.Definitions)
            .Where(definition => proof.Conflicts.Any(conflict => conflict.Range == definition.Range
                && VbaProjectIdentityModel.SameDocument(conflict.Uri ?? string.Empty, definition.Uri)))
            .SelectMany(GetLogicalRenameTargetDefinitions)
            .Where(definition => definition.Name.Equals(actualTarget.Name, StringComparison.OrdinalIgnoreCase))
            .Where(definition => originalDefinitions.Any(original => IsSameDeclarationScope(original, definition))
                || proof.Conflicts.Any(conflict => conflict.CollisionKind == "bindingCapture"
                    && VbaProjectIdentityModel.SameDocument(conflict.Uri ?? string.Empty, definition.Uri)
                    && conflict.Range == definition.Range))
            .Where(definition => originalDefinitions.All(original => original.Identity != definition.Identity))
            .ToArray();
        if (collidingDefinitions.Length == 0)
        {
            return null;
        }
        var results = new List<VbaRenameCausalDefinitionCorrespondence>();
        foreach (var before in originalDefinitions.Concat(collidingDefinitions).DistinctBy(definition => definition.Identity))
        {
            var control = FindHypotheticalDefinition(proof.ControlInventory, before, proof.ControlPlan.Changes);
            var after = FindHypotheticalDefinition(hypothetical, before, changes);
            if (control is null || after is null
                || before.Kind != after.Kind || before.PropertyAccessorKind != after.PropertyAccessorKind
                || before.Visibility != after.Visibility
                || !AreConditionalCompilationPathsCorrespondent(before, after, changes)
                || !AreConditionalCompilationPathsCorrespondent(before, control, proof.ControlPlan.Changes)
                || results.Count > 0 && !results[0].AfterDefinition.Name.Equals(after.Name, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            results.Add(new VbaRenameCausalDefinitionCorrespondence(before, control, after));
        }
        return Array.AsReadOnly(results.ToArray());
    }

    private bool TryRecordCollisionBindingImpact(
        VbaSemanticInventory hypothetical,
        VbaSourceDefinition target,
        VbaResolvedIdentifierOccurrence occurrence,
        VbaRange mappedRange,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        VbaRenameCollisionProof? proof,
        VbaRenameImpactKind impactKind)
    {
        var causes = CreateCollisionDeclarationCorrespondences(hypothetical, target, changes, proof);
        if (proof is null || causes is null || occurrence.Target.PhysicalDefinitions.Count == 0)
        {
            return false;
        }
        var beforeBindings = occurrence.Target.PhysicalDefinitions.Select(definition => definition.Identity).ToHashSet();
        if (beforeBindings.Any(identity => !causes.Any(cause => cause.BeforeDefinition.Identity == identity)))
        {
            return false;
        }
        var selectedCause = causes.Single(cause => cause.BeforeDefinition.Identity == occurrence.Target.SelectedDefinition.Identity);
        var controlRange = MapRange(occurrence.Uri, occurrence.Range, proof.ControlPlan.Changes);
        var controlClassification = IsDeclarationOccurrence(occurrence)
            ? VbaNameResolutionOutcome.Resolved(proof.ControlInventory.resolutionPolicy.CreateNameTarget(selectedCause.ControlDefinition))
            : proof.ControlInventory.semanticResolution.ClassifySourceDefinition(
                occurrence.Uri, controlRange.Start.Line, controlRange.Start.Character);
        var expectedControlBindings = causes.Where(cause => beforeBindings.Contains(cause.BeforeDefinition.Identity))
            .Select(cause => cause.ControlDefinition.Identity).ToHashSet();
        if (controlClassification.Kind != VbaNameResolutionKind.Resolved
            || !expectedControlBindings.SetEquals(controlClassification.Target!.PhysicalDefinitions.Select(definition => definition.Identity)))
        {
            return false;
        }

        var after = IsDeclarationOccurrence(occurrence)
            ? VbaNameResolutionOutcome.Resolved(hypothetical.resolutionPolicy.CreateNameTarget(selectedCause.AfterDefinition))
            : hypothetical.semanticResolution.ClassifySourceDefinition(
                occurrence.Uri, mappedRange.Start.Line, mappedRange.Start.Character);
        if (after.Kind != VbaNameResolutionKind.Ambiguous
            && (after.Kind != VbaNameResolutionKind.Resolved
                || !after.Target!.PhysicalDefinitions.All(definition => causes.Any(cause =>
                    cause.AfterDefinition.Identity == definition.Identity))))
        {
            return TryRecordWithEventsSegmentCollisionImpact(
                hypothetical, occurrence, controlRange, mappedRange, controlClassification, after, changes, proof, impactKind)
                || TryRecordInterfacePrefixCollisionImpact(
                    occurrence, controlRange, mappedRange, controlClassification, after, proof, impactKind);
        }

        proof.Impacts.Add(new VbaRenameImpact(
            impactKind,
            "The declaration collision changes this previously resolved binding; manual consolidation may be required.",
            occurrence.Uri,
            occurrence.Range)
        {
            Evidence = new VbaRenameImpactEvidence(
                VbaNameResolutionKind.Resolved,
                controlClassification.Kind,
                after.Kind,
                controlRange,
                mappedRange,
                causes)
        });
        return true;
    }

    private bool TryRecordDirectMemberCollisionImpact(
        VbaSemanticInventory hypothetical,
        VbaSourceDefinition target,
        VbaResolvedIdentifierOccurrence occurrence,
        VbaRange mappedRange,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        VbaRenameCollisionProof? proof,
        VbaRenameImpactKind impactKind)
    {
        if (proof is null || IsDeclarationOccurrence(occurrence) || occurrence.Target.PhysicalDefinitions.Count == 0
            || definitionCandidates.FindDocument(occurrence.Uri) is not { } document)
        {
            return false;
        }
        var syntax = (document.SyntaxTree ?? VbaSyntaxTree.ParseModule(document.Uri, document.Text))
            .GetPositionSyntax(occurrence.Range.Start.Line, occurrence.Range.Start.Character);
        if (syntax.MemberAccess is not { IsLeadingDot: false, IsIncomplete: false, TargetSegmentIndex: 1 } access
            || access.Segments.Count != 2)
        {
            return false;
        }
        var causes = CreateCollisionDeclarationCorrespondences(hypothetical, target, changes, proof);
        var controlRange = MapRange(occurrence.Uri, occurrence.Range, proof.ControlPlan.Changes);
        var before = semanticResolution.ResolveMemberChainAt(
            occurrence.Uri, occurrence.Range.Start.Line, occurrence.Range.Start.Character);
        var control = proof.ControlInventory.semanticResolution.ResolveMemberChainAt(
            occurrence.Uri, controlRange.Start.Line, controlRange.Start.Character);
        var after = hypothetical.semanticResolution.ResolveMemberChainAt(
            occurrence.Uri, mappedRange.Start.Line, mappedRange.Start.Character);
        if (causes is null || causes.Count < 2
            || before?.StopReason != VbaMemberChainResolutionStopReason.Resolved || before.Member is null
            || before.ReceiverType?.SourceDefinition is not { } beforeType
            || control?.StopReason != VbaMemberChainResolutionStopReason.Resolved || control.Member is null
            || control.ReceiverType?.SourceDefinition is not { } controlType
            || after?.StopReason != VbaMemberChainResolutionStopReason.UnresolvedMember || after.Member is not null
            || after.ReceiverType?.SourceDefinition is not { } afterType
            || FindHypotheticalDefinition(proof.ControlInventory, beforeType, proof.ControlPlan.Changes)?.Identity != controlType.Identity
            || FindHypotheticalDefinition(hypothetical, beforeType, changes)?.Identity != afterType.Identity
            || causes.Any(cause => !IsMemberOwnedByType(cause.BeforeDefinition, beforeType)
                || !IsMemberOwnedByType(cause.ControlDefinition, controlType)
                || !IsMemberOwnedByType(cause.AfterDefinition, afterType)
                || !cause.AfterDefinition.Name.Equals(after.Segments[^1], StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        var beforeBindings = occurrence.Target.PhysicalDefinitions.Select(definition => definition.Identity).ToHashSet();
        var originalCauses = causes.Where(cause => beforeBindings.Contains(cause.BeforeDefinition.Identity)).ToArray();
        if (originalCauses.Length != beforeBindings.Count
            || !beforeBindings.SetEquals(resolutionPolicy.CreateNameTarget(before.Member).PhysicalDefinitions
                .Select(definition => definition.Identity))
            || !originalCauses.Select(cause => cause.ControlDefinition.Identity).ToHashSet()
                .SetEquals(proof.ControlInventory.resolutionPolicy.CreateNameTarget(control.Member).PhysicalDefinitions
                    .Select(definition => definition.Identity)))
        {
            return false;
        }
        proof.Impacts.Add(new VbaRenameImpact(impactKind,
            "The confirmed member-name collision makes this established member binding ambiguous within its unchanged receiver type; manual consolidation may be required.",
            occurrence.Uri, occurrence.Range)
        {
            Evidence = new VbaRenameImpactEvidence(
                VbaNameResolutionKind.Resolved, VbaNameResolutionKind.Resolved, VbaNameResolutionKind.Unresolved,
                controlRange, mappedRange,
                Array.AsReadOnly(causes.Append(new VbaRenameCausalDefinitionCorrespondence(beforeType, controlType, afterType)).ToArray()))
        });
        return true;
    }

    private bool TryRecordReferencedProjectQualifierCollisionImpact(
        VbaSemanticInventory hypothetical,
        VbaSourceDefinition target,
        string uri,
        VbaRange range,
        VbaRange mappedRange,
        VbaNameResolutionOutcome before,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        VbaRenameCollisionProof? proof,
        VbaRenameImpactKind impactKind)
    {
        if (proof is null || !IsModuleIdentity(target)
            || target.Identity.Origin != VbaDefinitionOrigin.Source
            || proof.ControlPlan.TargetCorrespondence!.BeforeTarget.PhysicalDefinitions.Count != 1
            || definitionCandidates.FindDocument(uri) is not { } document)
        {
            return false;
        }
        var syntax = (document.SyntaxTree ?? VbaSyntaxTree.ParseModule(uri, document.Text))
            .GetPositionSyntax(range.Start.Line, range.Start.Character);
        if (syntax.MemberAccess is not { IsLeadingDot: false, IsIncomplete: false } access
            || access.Segments.Count != 2 || access.TargetSegmentIndex is < 0 or > 1)
        {
            return false;
        }
        var moduleControl = FindHypotheticalDefinition(proof.ControlInventory, target, proof.ControlPlan.Changes);
        var moduleAfter = FindHypotheticalDefinition(hypothetical, target, changes);
        if (moduleControl is null || moduleAfter is null
            || !access.Segments[0].Name.Equals(moduleAfter.Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var qualifierRange = ToRange(access.Segments[0].Range);
        var memberRange = ToRange(access.Segments[1].Range);
        var controlQualifierRange = MapRange(uri, qualifierRange, proof.ControlPlan.Changes);
        var afterQualifierRange = MapRange(uri, qualifierRange, changes);
        var controlMemberRange = MapRange(uri, memberRange, proof.ControlPlan.Changes);
        var afterMemberRange = MapRange(uri, memberRange, changes);
        var beforeQualifier = semanticResolution.ClassifySourceDefinition(uri, qualifierRange.Start.Line, qualifierRange.Start.Character);
        var controlQualifier = proof.ControlInventory.semanticResolution.ClassifySourceDefinition(
            uri, controlQualifierRange.Start.Line, controlQualifierRange.Start.Character);
        var afterQualifier = hypothetical.semanticResolution.ClassifySourceModuleValueQualifier(
            uri, afterQualifierRange.Start.Line, afterQualifierRange.Start.Character);
        var beforeMember = semanticResolution.ClassifySourceDefinition(uri, memberRange.Start.Line, memberRange.Start.Character);
        var controlMember = proof.ControlInventory.semanticResolution.ClassifySourceDefinition(
            uri, controlMemberRange.Start.Line, controlMemberRange.Start.Character);
        var afterMember = hypothetical.semanticResolution.ClassifySourceDefinition(
            uri, afterMemberRange.Start.Line, afterMemberRange.Start.Character);
        if (beforeQualifier.Kind != VbaNameResolutionKind.Unresolved || controlQualifier.Kind != VbaNameResolutionKind.Unresolved
            || afterQualifier.Kind != VbaNameResolutionKind.Resolved || afterQualifier.Target is null
            || afterQualifier.Target.PhysicalDefinitions.Count != 1
            || afterQualifier.Target.SelectedDefinition.Identity != moduleAfter.Identity
            || beforeMember.Kind != VbaNameResolutionKind.Resolved || beforeMember.Target is null
            || controlMember.Kind != VbaNameResolutionKind.Resolved || controlMember.Target is null
            || afterMember.Kind != VbaNameResolutionKind.Resolved || afterMember.Target is null
            || beforeMember.Target.PhysicalDefinitions.Count == 0 || afterMember.Target.PhysicalDefinitions.Count == 0)
        {
            return false;
        }
        var referenceName = beforeMember.Target.SelectedDefinition.ModuleName;
        if (beforeMember.Target.PhysicalDefinitions.Any(definition => definition.Identity.Origin != VbaDefinitionOrigin.ProjectReference
                || !VbaProjectReferenceName.AreEquivalent(definition.ModuleName, referenceName))
            || !beforeMember.Target.PhysicalDefinitions.Select(definition => definition.Identity).ToHashSet()
                .SetEquals(controlMember.Target.PhysicalDefinitions.Select(definition => definition.Identity))
            || !GetActiveReferenceNamesInSelectionOrder().Any(name => VbaProjectReferenceName.AreEquivalent(name, referenceName))
            || !TryGetCurrentReferencedProjectName(referenceName, out var projectName)
            || !projectName.Equals(moduleAfter.Name, StringComparison.OrdinalIgnoreCase)
            || !proof.ControlInventory.TryGetCurrentReferencedProjectName(referenceName, out var controlProjectName)
            || !projectName.Equals(controlProjectName, StringComparison.OrdinalIgnoreCase)
            || !hypothetical.TryGetCurrentReferencedProjectName(referenceName, out var afterProjectName)
            || !projectName.Equals(afterProjectName, StringComparison.OrdinalIgnoreCase)
            || !proof.Conflicts.Any(conflict => conflict.CollisionKind == "referencedProject"
                && conflict.ReferenceName is not null && VbaProjectReferenceName.AreEquivalent(conflict.ReferenceName, referenceName)
                && conflict.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        if (access.TargetSegmentIndex == 0
            ? before.Kind != VbaNameResolutionKind.Unresolved
            : before.Kind != VbaNameResolutionKind.Resolved || before.Target is null
                || !before.Target.PhysicalDefinitions.Select(definition => definition.Identity).ToHashSet()
                    .SetEquals(beforeMember.Target.PhysicalDefinitions.Select(definition => definition.Identity)))
        {
            return false;
        }
        var actualMembers = afterMember.Target.PhysicalDefinitions.Select(definition => definition.Identity).ToHashSet();
        var memberCauses = GetDocumentDefinitions(target.Uri).Where(definition => IsMemberOwnedByType(definition, target))
            .Select(definition => new
            {
                Before = definition,
                Control = FindHypotheticalDefinition(proof.ControlInventory, definition, proof.ControlPlan.Changes),
                After = FindHypotheticalDefinition(hypothetical, definition, changes)
            }).Where(cause => cause.After is not null && actualMembers.Contains(cause.After.Identity)).ToArray();
        if (memberCauses.Any(cause => cause.Control is null)
            || !actualMembers.SetEquals(memberCauses.Select(cause => cause.After!.Identity))
            || afterMember.Target.PhysicalDefinitions.Any(definition => !IsMemberOwnedByType(definition, moduleAfter)))
        {
            return false;
        }
        var moduleCause = new VbaRenameCausalDefinitionCorrespondence(target, moduleControl, moduleAfter);
        var controlRange = MapRange(uri, range, proof.ControlPlan.Changes);
        proof.Impacts.Add(new VbaRenameImpact(impactKind,
            "The confirmed project-name collision captures this established library-qualified reference through the renamed source module; its text is retained.",
            uri, range)
        {
            ReferenceProjectEvidence = new VbaRenameReferenceProjectImpactEvidence(
                referenceName, projectName, moduleCause, beforeMember.Target, controlMember.Target, afterMember.Target),
            Evidence = new VbaRenameImpactEvidence(before.Kind,
                access.TargetSegmentIndex == 0 ? controlQualifier.Kind : controlMember.Kind,
                access.TargetSegmentIndex == 0 ? afterQualifier.Kind : afterMember.Kind,
                controlRange, mappedRange, Array.AsReadOnly(memberCauses.Select(cause =>
                    new VbaRenameCausalDefinitionCorrespondence(cause.Before, cause.Control!, cause.After!))
                    .Prepend(moduleCause).ToArray()))
        });
        return true;
    }

    private bool TryRecordSourceModuleQualifierCollisionImpact(
        VbaSemanticInventory hypothetical,
        VbaSourceDefinition target,
        VbaResolvedIdentifierOccurrence occurrence,
        VbaRange mappedRange,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        VbaRenameCollisionProof? proof,
        VbaRenameImpactKind impactKind)
    {
        if (proof is null || !IsModuleIdentity(target) || IsDeclarationOccurrence(occurrence)
            || occurrence.Target.PhysicalDefinitions.Count == 0
            || definitionCandidates.FindDocument(occurrence.Uri) is not { } document)
        {
            return false;
        }
        var syntax = (document.SyntaxTree ?? VbaSyntaxTree.ParseModule(document.Uri, document.Text))
            .GetPositionSyntax(occurrence.Range.Start.Line, occurrence.Range.Start.Character);
        if (syntax.MemberAccess is not { IsLeadingDot: false, IsIncomplete: false } access
            || access.Segments.Count != 2 || access.TargetSegmentIndex is < 0 or > 1)
        {
            return false;
        }
        var causes = CreateCollisionDeclarationCorrespondences(hypothetical, target, changes, proof);
        if (causes is null || causes.Count < 2
            || causes.Any(cause => !IsModuleIdentity(cause.BeforeDefinition)))
        {
            return false;
        }
        var qualifierRange = ToRange(access.Segments[0].Range);
        var controlQualifierRange = MapRange(occurrence.Uri, qualifierRange, proof.ControlPlan.Changes);
        var afterQualifierRange = MapRange(occurrence.Uri, qualifierRange, changes);
        var beforeQualifier = semanticResolution.ClassifySourceModuleValueQualifier(
            occurrence.Uri, qualifierRange.Start.Line, qualifierRange.Start.Character);
        var controlQualifier = proof.ControlInventory.semanticResolution.ClassifySourceModuleValueQualifier(
            occurrence.Uri, controlQualifierRange.Start.Line, controlQualifierRange.Start.Character);
        var afterQualifier = hypothetical.semanticResolution.ClassifySourceModuleValueQualifier(
            occurrence.Uri, afterQualifierRange.Start.Line, afterQualifierRange.Start.Character);
        if (beforeQualifier.Kind != VbaNameResolutionKind.Resolved || beforeQualifier.Target is null
            || controlQualifier.Kind != VbaNameResolutionKind.Resolved || controlQualifier.Target is null
            || afterQualifier.Kind != VbaNameResolutionKind.Ambiguous
            || afterQualifier.AmbiguousCandidates.Count == 0)
        {
            return false;
        }
        var beforeRoots = beforeQualifier.Target.PhysicalDefinitions.Select(definition => definition.Identity).ToHashSet();
        var qualifierCauses = causes.Where(cause => beforeRoots.Contains(cause.BeforeDefinition.Identity)).ToArray();
        if (qualifierCauses.Length != beforeRoots.Count
            || !qualifierCauses.Select(cause => cause.ControlDefinition.Identity).ToHashSet()
                .SetEquals(controlQualifier.Target.PhysicalDefinitions.Select(definition => definition.Identity))
            || !causes.Select(cause => cause.AfterDefinition.Identity).ToHashSet()
                .SetEquals(afterQualifier.AmbiguousCandidates.SelectMany(candidate => candidate.PhysicalDefinitions)
                    .Select(definition => definition.Identity)))
        {
            return false;
        }
        var controlRange = MapRange(occurrence.Uri, occurrence.Range, proof.ControlPlan.Changes);
        var control = proof.ControlInventory.semanticResolution.ClassifySourceDefinition(
            occurrence.Uri, controlRange.Start.Line, controlRange.Start.Character);
        var after = hypothetical.semanticResolution.ClassifySourceDefinition(
            occurrence.Uri, mappedRange.Start.Line, mappedRange.Start.Character);
        var occurrenceCauses = occurrence.Target.PhysicalDefinitions.Select(before => new
        {
            Before = before,
            Control = FindHypotheticalDefinition(proof.ControlInventory, before, proof.ControlPlan.Changes),
            After = FindHypotheticalDefinition(hypothetical, before, changes)
        }).ToArray();
        if (occurrenceCauses.Any(cause => cause.Control is null || cause.After is null)
            || control.Kind != VbaNameResolutionKind.Resolved || control.Target is null
            || !occurrenceCauses.Select(cause => cause.Control!.Identity).ToHashSet()
                .SetEquals(control.Target.PhysicalDefinitions.Select(definition => definition.Identity)))
        {
            return false;
        }
        if (access.TargetSegmentIndex == 0)
        {
            if (!beforeRoots.SetEquals(occurrence.Target.PhysicalDefinitions.Select(definition => definition.Identity))
                || after.Kind is not (VbaNameResolutionKind.Unresolved or VbaNameResolutionKind.Ambiguous))
            {
                return false;
            }
        }
        else
        {
            IReadOnlyList<VbaResolvedNameTarget> afterTargets = after.Kind switch
            {
                VbaNameResolutionKind.Resolved when after.Target is not null => [after.Target],
                VbaNameResolutionKind.Ambiguous => after.AmbiguousCandidates,
                _ => Array.Empty<VbaResolvedNameTarget>()
            };
            if (afterTargets.Count == 0
                || !occurrenceCauses.All(member => qualifierCauses.Any(root =>
                    IsMemberOwnedByType(member.Before, root.BeforeDefinition)))
                || !afterTargets.SelectMany(candidate => candidate.PhysicalDefinitions).All(member =>
                    member.Name.Equals(occurrence.Target.SelectedDefinition.Name, StringComparison.OrdinalIgnoreCase)
                    && causes.Any(root => IsMemberOwnedByType(member, root.AfterDefinition))))
            {
                return false;
            }
        }
        proof.Impacts.Add(new VbaRenameImpact(
            impactKind,
            "The confirmed module-name collision makes this source qualifier ambiguous and changes its binding; manual consolidation may be required.",
            occurrence.Uri, occurrence.Range)
        {
            Evidence = new VbaRenameImpactEvidence(
                VbaNameResolutionKind.Resolved, control.Kind, after.Kind, controlRange, mappedRange,
                Array.AsReadOnly(causes.Concat(occurrenceCauses.Select(cause =>
                    new VbaRenameCausalDefinitionCorrespondence(cause.Before, cause.Control!, cause.After!)))
                    .DistinctBy(cause => cause.BeforeDefinition.Identity).ToArray()))
        });
        return true;
    }

    private bool TryRecordReceiverTypeCollisionImpact(
        VbaSemanticInventory hypothetical,
        VbaResolvedIdentifierOccurrence occurrence,
        VbaRange mappedRange,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        VbaRenameCollisionProof? proof)
    {
        if (proof is null || IsDeclarationOccurrence(occurrence)
            || occurrence.Target.PhysicalDefinitions.Count != 1
            || definitionCandidates.FindDocument(occurrence.Uri) is not { } document)
        {
            return false;
        }
        var syntax = (document.SyntaxTree ?? VbaSyntaxTree.ParseModule(document.Uri, document.Text))
            .GetPositionSyntax(occurrence.Range.Start.Line, occurrence.Range.Start.Character);
        if (syntax.MemberAccess is not { } access)
        {
            return false;
        }
        VbaRange receiverRange;
        if (access.IsLeadingDot)
        {
            if (access.ReceiverSegments.Count != 0 || syntax.EnclosingWithScopes.Count != 1
                || syntax.EnclosingWithScopes[0].Receiver is not { IsLeadingDot: false } withReceiver
                || withReceiver.Segments.Count != 1)
            {
                return false;
            }
            receiverRange = ToRange(withReceiver.Segments[0].Range);
        }
        else
        {
            if (access.ReceiverSegments.Count == 0)
            {
                return false;
            }
            receiverRange = ToRange(access.ReceiverSegments[^1].Range);
        }
        var beforeReceiver = ResolveSourceDefinition(
            occurrence.Uri, receiverRange.Start.Line, receiverRange.Start.Character);
        if (beforeReceiver is null)
        {
            return false;
        }
        var controlReceiver = FindHypotheticalDefinition(proof.ControlInventory, beforeReceiver, proof.ControlPlan.Changes);
        var afterReceiver = FindHypotheticalDefinition(hypothetical, beforeReceiver, changes);
        var controlReceiverRange = MapRange(occurrence.Uri, receiverRange, proof.ControlPlan.Changes);
        var afterReceiverRange = MapRange(occurrence.Uri, receiverRange, changes);
        if (controlReceiver is null || afterReceiver is null
            || proof.ControlInventory.ResolveSourceDefinition(occurrence.Uri,
                controlReceiverRange.Start.Line, controlReceiverRange.Start.Character)?.Identity != controlReceiver.Identity
            || hypothetical.ResolveSourceDefinition(occurrence.Uri,
                afterReceiverRange.Start.Line, afterReceiverRange.Start.Character)?.Identity != afterReceiver.Identity)
        {
            return false;
        }

        var beforeType = semanticResolution.GetEffectiveDeclaredType(beforeReceiver);
        var afterType = hypothetical.semanticResolution.GetEffectiveDeclaredType(afterReceiver);
        var beforeMember = occurrence.Target.SelectedDefinition;
        var controlMember = FindHypotheticalDefinition(proof.ControlInventory, beforeMember, proof.ControlPlan.Changes);
        var afterMember = FindHypotheticalDefinition(hypothetical, beforeMember, changes);
        var controlRange = MapRange(occurrence.Uri, occurrence.Range, proof.ControlPlan.Changes);
        var beforeChain = semanticResolution.ResolveMemberChainAt(
            occurrence.Uri, occurrence.Range.Start.Line, occurrence.Range.Start.Character);
        var controlChain = proof.ControlInventory.semanticResolution.ResolveMemberChainAt(
            occurrence.Uri, controlRange.Start.Line, controlRange.Start.Character);
        var afterChain = hypothetical.semanticResolution.ResolveMemberChainAt(
            occurrence.Uri, mappedRange.Start.Line, mappedRange.Start.Character);
        if (beforeChain?.StopReason != VbaMemberChainResolutionStopReason.Resolved
            || beforeChain.Member?.Identity != beforeMember.Identity
            || controlChain?.StopReason != VbaMemberChainResolutionStopReason.Resolved
            || controlMember is null || controlChain.Member?.Identity != controlMember.Identity || afterChain is null)
        {
            return false;
        }
        var controlOutcome = VbaNameResolutionOutcome.Resolved(
            proof.ControlInventory.resolutionPolicy.CreateNameTarget(controlChain.Member!));
        var afterOutcome = afterChain.Member is { } resolvedAfterMember
            ? VbaNameResolutionOutcome.Resolved(hypothetical.resolutionPolicy.CreateNameTarget(resolvedAfterMember))
            : VbaNameResolutionOutcome.Unresolved;
        if (beforeType.Target is null || controlMember is null || afterMember is null
            || !beforeType.Target.PhysicalDefinitions.Any(type => IsMemberOwnedByType(beforeMember, type))
            || afterOutcome.Kind == VbaNameResolutionKind.Resolved
                && (afterType.Target is null || !afterOutcome.Target!.PhysicalDefinitions.All(member =>
                    afterType.Target.PhysicalDefinitions.Any(type => IsMemberOwnedByType(member, type))))
            || !TryRecordTypeNameCollisionImpact(
                hypothetical, beforeReceiver, afterReceiver, beforeType, afterType, changes, proof))
        {
            return false;
        }

        var typeEvidence = proof.Impacts[^1].DeclaredTypeEvidence!;
        proof.Impacts.Add(new VbaRenameImpact(
            VbaRenameImpactKind.NonTargetBindingChanged,
            "The confirmed type-name collision changes this member binding through its original receiver type; manual consolidation may be required.",
            occurrence.Uri, occurrence.Range)
        {
            DeclaredTypeEvidence = typeEvidence,
            Evidence = new VbaRenameImpactEvidence(
                VbaNameResolutionKind.Resolved, controlOutcome.Kind, afterOutcome.Kind,
                controlRange, mappedRange,
                Array.AsReadOnly(typeEvidence.TypeDeclarationCauses
                    .Append(new VbaRenameCausalDefinitionCorrespondence(beforeMember, controlMember, afterMember))
                    .ToArray()))
        });
        return true;
    }

    private static bool IsMemberOwnedByType(VbaSourceDefinition member, VbaSourceDefinition type)
        => VbaProjectIdentityModel.SameDocument(member.Uri, type.Uri)
            && (IsModuleIdentity(type)
                && member.ModuleName.Equals(type.Name, StringComparison.OrdinalIgnoreCase)
                || type.Kind == VbaSourceDefinitionKind.Type
                    && member.ModuleName.Equals(type.ModuleName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(member.ParentTypeName, type.Name, StringComparison.OrdinalIgnoreCase));

    private VbaRenameFailure? ProveEffectiveDeclaredTypesArePreserved(
        VbaSemanticInventory hypothetical,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        CancellationToken cancellationToken,
        VbaRenameCollisionProof? collisionProof = null)
    {
        foreach (var before in sourceDocuments.SelectMany(document => document.Definitions)
                     .Where(definition => definition.Kind is VbaSourceDefinitionKind.Variable
                         or VbaSourceDefinitionKind.Parameter or VbaSourceDefinitionKind.Procedure
                         or VbaSourceDefinitionKind.Property or VbaSourceDefinitionKind.TypeMember))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var after = FindHypotheticalDefinition(hypothetical, before, changes);
            if (after is null)
            {
                return AnalysisIncomplete("Rename could not match an affected typed declaration.");
            }
            if (before.Name.Equals(after.Name, StringComparison.OrdinalIgnoreCase)
                && before.TypeReference == after.TypeReference)
            {
                continue;
            }

            var beforeType = semanticResolution.GetEffectiveDeclaredType(before);
            var afterType = hypothetical.semanticResolution.GetEffectiveDeclaredType(after);
            if (beforeType.State == VbaEffectiveDeclaredTypeState.NoReturnType
                && afterType.State == VbaEffectiveDeclaredTypeState.NoReturnType)
            {
                continue;
            }
            if (beforeType.State != VbaEffectiveDeclaredTypeState.Known
                || afterType.State != VbaEffectiveDeclaredTypeState.Known)
            {
                if (TryRecordTypeNameCollisionImpact(
                    hypothetical, before, after, beforeType, afterType, changes, collisionProof))
                {
                    continue;
                }
                return AnalysisIncomplete($"Rename could not prove the effective type of '{before.Name}' is preserved.");
            }

            var sameType = Equals(beforeType.Identity, afterType.Identity);
            if (beforeType.Target is { } beforeTarget && afterType.Target is { } afterTarget)
            {
                var expected = beforeTarget.PhysicalDefinitions
                    .Select(definition => FindHypotheticalDefinition(hypothetical, definition, changes))
                    .ToArray();
                if (expected.Any(definition => definition is null))
                {
                    return AnalysisIncomplete($"Rename could not match the type declaration of '{before.Name}'.");
                }
                var expectedIdentities = expected.Select(definition => definition!.Identity).ToHashSet();
                var actualIdentities = afterTarget.PhysicalDefinitions.Select(definition => definition.Identity).ToHashSet();
                sameType = beforeTarget.IsConditionalFamily == afterTarget.IsConditionalFamily
                    && expectedIdentities.Count == expected.Length
                    && actualIdentities.Count == afterTarget.PhysicalDefinitions.Count
                    && expectedIdentities.SetEquals(actualIdentities);
            }
            if (!sameType)
            {
                if (TryRecordTypeNameCollisionImpact(
                    hypothetical, before, after, beforeType, afterType, changes, collisionProof))
                {
                    continue;
                }
                return ResolutionChanged($"Rename would change the effective type of '{before.Name}' "
                    + $"from {beforeType.DisplayName} to {afterType.DisplayName}. Choose a name that preserves its declared type.");
            }
        }
        return null;
    }

    private bool TryRecordTypeNameCollisionImpact(
        VbaSemanticInventory hypothetical,
        VbaSourceDefinition before,
        VbaSourceDefinition after,
        VbaEffectiveDeclaredType beforeType,
        VbaEffectiveDeclaredType afterType,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        VbaRenameCollisionProof? proof)
    {
        if (proof is null
            || beforeType.State != VbaEffectiveDeclaredTypeState.Known
            || beforeType.Target is null
            || before.TypeReference is null
            || after.TypeReference is null
            || afterType.State is not (VbaEffectiveDeclaredTypeState.Known or VbaEffectiveDeclaredTypeState.ExplicitlyUnresolved))
        {
            return false;
        }

        var roots = proof.ControlPlan.TargetCorrespondence!.BeforeTarget.PhysicalDefinitions
            .Concat(sourceDocuments.SelectMany(document => document.Definitions)
                .Where(definition => proof.Conflicts.Any(conflict => conflict.Uri is not null
                    && VbaProjectIdentityModel.SameDocument(conflict.Uri, definition.Uri)
                    && conflict.Range == definition.Range)))
            .Where(definition => IsModuleIdentity(definition)
                || definition.Kind is VbaSourceDefinitionKind.Type or VbaSourceDefinitionKind.Enum)
            .DistinctBy(definition => definition.Identity)
            .ToArray();
        bool IsOwnedType(VbaSourceDefinition definition, VbaSourceDefinition root)
            => definition.Identity == root.Identity
                || IsModuleIdentity(root)
                    && VbaProjectIdentityModel.SameDocument(definition.Uri, root.Uri)
                    && definition.ModuleName.Equals(root.Name, StringComparison.OrdinalIgnoreCase);
        if (roots.Length == 0
            || !beforeType.Target.PhysicalDefinitions.All(definition => roots.Any(root => IsOwnedType(definition, root))))
        {
            return false;
        }

        var controlDeclaration = FindHypotheticalDefinition(proof.ControlInventory, before, proof.ControlPlan.Changes);
        if (controlDeclaration is null)
        {
            return false;
        }
        var controlType = proof.ControlInventory.semanticResolution.GetEffectiveDeclaredType(controlDeclaration);
        if (controlType.State != VbaEffectiveDeclaredTypeState.Known || controlType.Target is null)
        {
            return false;
        }
        var expectedControlTypes = beforeType.Target.PhysicalDefinitions.Select(definition =>
            FindHypotheticalDefinition(proof.ControlInventory, definition, proof.ControlPlan.Changes)).ToArray();
        if (expectedControlTypes.Any(definition => definition is null)
            || !expectedControlTypes.Select(definition => definition!.Identity).ToHashSet()
                .SetEquals(controlType.Target.PhysicalDefinitions.Select(definition => definition.Identity)))
        {
            return false;
        }

        var controlRoots = roots.Select(definition =>
            FindHypotheticalDefinition(proof.ControlInventory, definition, proof.ControlPlan.Changes)).ToArray();
        var actualRoots = roots.Select(definition => FindHypotheticalDefinition(hypothetical, definition, changes)).ToArray();
        if (controlRoots.Any(definition => definition is null)
            || actualRoots.Any(definition => definition is null)
            || !actualRoots.Any(root => after.TypeReference.Name.Equals(root!.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(after.TypeReference.Qualifier, root!.Name, StringComparison.OrdinalIgnoreCase))
            || afterType.Target is not null
                && !afterType.Target.PhysicalDefinitions.All(definition => actualRoots.Any(root => IsOwnedType(definition, root!))))
        {
            return false;
        }

        proof.Impacts.Add(new VbaRenameImpact(
            VbaRenameImpactKind.TypeNameResolutionChanged,
            $"The confirmed type-name collision changes the source type resolution of '{before.Name}'; manual consolidation may be required.",
            before.Uri,
            before.TypeReferenceRange ?? before.Range)
        {
            DeclaredTypeEvidence = new VbaRenameDeclaredTypeImpactEvidence(
                new VbaRenameCausalDefinitionCorrespondence(before, controlDeclaration, after),
                beforeType,
                controlType,
                afterType,
                Array.AsReadOnly(roots.Select((definition, index) => new VbaRenameCausalDefinitionCorrespondence(
                    definition, controlRoots[index]!, actualRoots[index]!)).ToArray()))
        });
        return true;
    }

    private VbaRenameFailure? ProveSourceInterfaceAssociationsArePreserved(
        VbaSemanticInventory hypothetical,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        VbaRenameCollisionProof? collisionProof = null)
    {
        var beforeAnalyses = sourceDocuments
            .Select(document => new
            {
                Document = document,
                Analysis = semanticResolution
                    .AnalyzeSourceInterfaceImplementationAssociations(document)
            })
            .ToArray();
        var afterAnalyses = hypothetical.sourceDocuments
            .Select(document => new
            {
                Document = document,
                Analysis = hypothetical.semanticResolution
                    .AnalyzeSourceInterfaceImplementationAssociations(document)
            })
            .ToArray();
        var permittedAssociations = collisionProof is null
            ? new HashSet<VbaInterfaceAssociationProofKey>()
            : RecordInterfaceCollisionImpacts(
                hypothetical,
                beforeAnalyses.SelectMany(item => item.Analysis.Associations).ToArray(),
                afterAnalyses.SelectMany(item => item.Analysis.Associations).ToArray(),
                changes,
                collisionProof);
        var beforeCounts = beforeAnalyses
            .SelectMany(item => item.Analysis.Associations)
            .Select(association => CreateInterfaceAssociationProofKey(
                association,
                changes))
            .Where(key => !permittedAssociations.Contains(key))
            .GroupBy(key => key)
            .ToDictionary(group => group.Key, group => group.Count());
        var afterCounts = afterAnalyses
            .SelectMany(item => item.Analysis.Associations)
            .Select(association => hypothetical
                .CreateInterfaceAssociationProofKey(
                    association,
                    changes: null))
            .Where(key => !permittedAssociations.Contains(key))
            .GroupBy(key => key)
            .ToDictionary(group => group.Key, group => group.Count());
        if (beforeCounts.Count != afterCounts.Count
            || beforeCounts.Any(pair =>
                !afterCounts.TryGetValue(pair.Key, out var afterCount)
                || afterCount != pair.Value))
        {
            return ResolutionChanged(
                "Rename would change a source Implements association.");
        }

        var beforeIncompleteCounts = beforeAnalyses
            .SelectMany(item => item.Analysis.IncompleteUpstreamTargets
                .Select(target => new VbaIncompleteInterfaceTargetProofKey(
                    item.Document.Uri,
                    VbaIncompleteInterfaceTargetRole.Upstream,
                    CreateInterfaceTargetProofKey(target, changes)))
                .Concat(item.Analysis.IncompleteDependentTargets.Select(
                    target => new VbaIncompleteInterfaceTargetProofKey(
                        item.Document.Uri,
                        VbaIncompleteInterfaceTargetRole.Dependent,
                        CreateInterfaceTargetProofKey(target, changes)))))
            .GroupBy(key => key)
            .ToDictionary(group => group.Key, group => group.Count());
        var afterIncompleteCounts = afterAnalyses
            .SelectMany(item => item.Analysis.IncompleteUpstreamTargets
                .Select(target => new VbaIncompleteInterfaceTargetProofKey(
                    item.Document.Uri,
                    VbaIncompleteInterfaceTargetRole.Upstream,
                    hypothetical.CreateInterfaceTargetProofKey(
                        target,
                        changes: null)))
                .Concat(item.Analysis.IncompleteDependentTargets.Select(
                    target => new VbaIncompleteInterfaceTargetProofKey(
                        item.Document.Uri,
                        VbaIncompleteInterfaceTargetRole.Dependent,
                        hypothetical.CreateInterfaceTargetProofKey(
                            target,
                            changes: null)))))
            .GroupBy(key => key)
            .ToDictionary(group => group.Key, group => group.Count());
        if (beforeIncompleteCounts.Count != afterIncompleteCounts.Count
            || beforeIncompleteCounts.Any(pair =>
                !afterIncompleteCounts.TryGetValue(
                    pair.Key,
                    out var afterCount)
                || afterCount != pair.Value))
        {
            return AnalysisIncomplete(
                "Rename would change incomplete source Implements "
                + "association evidence.");
        }

        return null;
    }

    private IReadOnlySet<VbaInterfaceAssociationProofKey> RecordInterfaceCollisionImpacts(
        VbaSemanticInventory hypothetical,
        IReadOnlyList<VbaInterfaceImplementationAssociation> before,
        IReadOnlyList<VbaInterfaceImplementationAssociation> after,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        VbaRenameCollisionProof proof)
    {
        var permitted = new HashSet<VbaInterfaceAssociationProofKey>();
        var root = proof.ControlPlan.TargetCorrespondence!.BeforeTarget.SelectedDefinition;
        var causes = CreateCollisionDeclarationCorrespondences(hypothetical, root, changes, proof);
        if (causes is null)
        {
            return permitted;
        }
        var control = proof.ControlInventory.sourceDocuments.SelectMany(document =>
            proof.ControlInventory.semanticResolution.GetConclusiveSourceInterfaceImplementationAssociations(document)).ToArray();
        foreach (var original in before)
        {
            if (root.Kind == VbaSourceDefinitionKind.Class
                && TryRecordInterfaceTypeCollisionImpact(hypothetical, original, control, after, changes, causes, proof))
            {
                permitted.Add(CreateInterfaceAssociationProofKey(original, changes));
                continue;
            }
            if (original.MemberTarget.PhysicalDefinitions.Count == 0
                || original.MemberTarget.PhysicalDefinitions.Any(definition =>
                    !causes.Any(cause => cause.BeforeDefinition.Identity == definition.Identity)))
            {
                continue;
            }
            var actualImplementation = FindHypotheticalDefinition(hypothetical, original.Implementation, changes);
            var controlImplementation = FindHypotheticalDefinition(proof.ControlInventory, original.Implementation, proof.ControlPlan.Changes);
            var actualMember = FindHypotheticalDefinition(hypothetical, original.Contract.OriginDefinition, changes);
            var controlKey = CreateInterfaceAssociationProofKey(original, proof.ControlPlan.Changes);
            var controlMatches = control.Where(candidate =>
                proof.ControlInventory.CreateInterfaceAssociationProofKey(candidate, changes: null) == controlKey).ToArray();
            if (actualImplementation is null || controlImplementation is null || actualMember is null || controlMatches.Length != 1)
            {
                continue;
            }
            var implementingUri = original.Relationship.ImplementingDocument.Uri;
            var relationshipRange = MapRange(implementingUri, original.Relationship.InterfaceTypeRange, changes);
            var actual = after.Where(candidate => candidate.Implementation.Identity == actualImplementation.Identity
                && candidate.Relationship.ImplementingDocument.Uri.Equals(implementingUri, StringComparison.OrdinalIgnoreCase)
                && candidate.Relationship.InterfaceTypeRange == relationshipRange).ToArray();
            if (!actual.Any(candidate => candidate.Contract.OriginDefinition.Identity == actualMember.Identity)
                || actual.Any(candidate =>
                    !causes.Any(cause => cause.AfterDefinition.Identity == candidate.Contract.OriginDefinition.Identity)
                    || candidate.MemberTarget.PhysicalDefinitions.Any(definition =>
                        !causes.Any(cause => cause.AfterDefinition.Identity == definition.Identity))
                    || CreateInterfaceTargetProofKey(original.Relationship.InterfaceTarget, changes)
                        != hypothetical.CreateInterfaceTargetProofKey(candidate.Relationship.InterfaceTarget, changes: null)
                    || CreateInterfaceTargetProofKey(original.ImplementationTarget, changes)
                        != hypothetical.CreateInterfaceTargetProofKey(candidate.ImplementationTarget, changes: null)
                    || candidate.Contract.Kind != original.Contract.Kind
                    || candidate.Contract.IsDerivedVariableAccessor != original.Contract.IsDerivedVariableAccessor
                    || candidate.CompatibilityState != original.CompatibilityState
                    || candidate.InterfacePrefixRange != MapRange(original.Implementation.Uri, original.InterfacePrefixRange, changes)
                    || candidate.SeparatorRange != MapRange(original.Implementation.Uri, original.SeparatorRange, changes)
                    || candidate.MemberSuffixRange != MapRange(original.Implementation.Uri, original.MemberSuffixRange, changes)))
            {
                continue;
            }
            var originalKey = CreateInterfaceAssociationProofKey(original, changes);
            var actualKeys = actual.Select(candidate => hypothetical.CreateInterfaceAssociationProofKey(candidate, changes: null)).ToArray();
            if (actualKeys.Length == 1 && actualKeys[0] == originalKey)
            {
                continue;
            }
            var evidence = Array.AsReadOnly(causes.Append(new VbaRenameCausalDefinitionCorrespondence(
                original.Implementation, controlImplementation, actualImplementation)).ToArray());
            proof.Impacts.Add(new VbaRenameImpact(
                VbaRenameImpactKind.DependentAssociationChanged,
                "The confirmed member collision changes this established Implements association; its original implementation edit closure is retained.",
                original.Implementation.Uri, original.Implementation.Range)
            {
                InterfaceEvidence = new VbaRenameInterfaceImpactEvidence(
                    original, controlMatches[0], Array.AsReadOnly(actual), evidence)
            });
            permitted.Add(originalKey);
            permitted.UnionWith(actualKeys);
        }
        return permitted;
    }

    private bool TryRecordInterfaceTypeCollisionImpact(
        VbaSemanticInventory hypothetical,
        VbaInterfaceImplementationAssociation original,
        IReadOnlyList<VbaInterfaceImplementationAssociation> control,
        IReadOnlyList<VbaInterfaceImplementationAssociation> after,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        IReadOnlyList<VbaRenameCausalDefinitionCorrespondence> causes,
        VbaRenameCollisionProof proof)
    {
        var interfaceDefinitions = original.Relationship.InterfaceTarget.PhysicalDefinitions;
        if (interfaceDefinitions.Count == 0 || interfaceDefinitions.Any(definition =>
                definition.Kind != VbaSourceDefinitionKind.Class
                || !causes.Any(cause => cause.BeforeDefinition.Identity == definition.Identity)))
        {
            return false;
        }
        var controlKey = CreateInterfaceAssociationProofKey(original, proof.ControlPlan.Changes);
        var controlMatches = control.Where(candidate =>
            proof.ControlInventory.CreateInterfaceAssociationProofKey(candidate, changes: null) == controlKey).ToArray();
        if (controlMatches.Length != 1)
        {
            return false;
        }
        var uri = original.Relationship.ImplementingDocument.Uri;
        var originalRange = original.Relationship.InterfaceTypeRange;
        var nameRange = new VbaRange(new VbaPosition(originalRange.End.Line,
            originalRange.End.Character - original.Relationship.InterfaceType.Name.Length), originalRange.End);
        var controlRange = MapRange(uri, nameRange, proof.ControlPlan.Changes);
        var actualRange = MapRange(uri, nameRange, changes);
        var beforeBinding = semanticResolution.ClassifySourceDefinition(uri, nameRange.Start.Line, nameRange.Start.Character);
        var controlBinding = proof.ControlInventory.semanticResolution.ClassifySourceDefinition(
            uri, controlRange.Start.Line, controlRange.Start.Character);
        var actualBinding = hypothetical.semanticResolution.ClassifySourceDefinition(
            uri, actualRange.Start.Line, actualRange.Start.Character);
        var expectedControl = causes.Where(cause => interfaceDefinitions.Any(definition =>
            cause.BeforeDefinition.Identity == definition.Identity)).Select(cause => cause.ControlDefinition.Identity).ToHashSet();
        if (beforeBinding.Kind != VbaNameResolutionKind.Resolved
            || !interfaceDefinitions.Select(definition => definition.Identity).ToHashSet()
                .SetEquals(beforeBinding.Target!.PhysicalDefinitions.Select(definition => definition.Identity))
            || controlBinding.Kind != VbaNameResolutionKind.Resolved
            || !expectedControl.SetEquals(controlBinding.Target!.PhysicalDefinitions.Select(definition => definition.Identity))
            || actualBinding.Kind != VbaNameResolutionKind.Ambiguous
            || actualBinding.AmbiguousCandidates.Count < 2
            || actualBinding.AmbiguousCandidates.SelectMany(candidate => candidate.PhysicalDefinitions).Any(definition =>
                definition.Kind != VbaSourceDefinitionKind.Class
                || !causes.Any(cause => cause.AfterDefinition.Identity == definition.Identity)))
        {
            return false;
        }
        var actualImplementation = FindHypotheticalDefinition(hypothetical, original.Implementation, changes);
        var controlImplementation = FindHypotheticalDefinition(proof.ControlInventory, original.Implementation, proof.ControlPlan.Changes);
        var actualMember = FindHypotheticalDefinition(hypothetical, original.Contract.OriginDefinition, changes);
        var controlMember = FindHypotheticalDefinition(proof.ControlInventory, original.Contract.OriginDefinition, proof.ControlPlan.Changes);
        if (actualImplementation is null || controlImplementation is null || actualMember is null || controlMember is null
            || after.Any(candidate => candidate.Relationship.ImplementingDocument.Uri.Equals(uri, StringComparison.OrdinalIgnoreCase)
                && candidate.Relationship.InterfaceTypeRange == MapRange(uri, originalRange, changes)))
        {
            return false;
        }
        var evidence = Array.AsReadOnly(causes.Concat([
            new VbaRenameCausalDefinitionCorrespondence(original.Implementation, controlImplementation, actualImplementation),
            new VbaRenameCausalDefinitionCorrespondence(original.Contract.OriginDefinition, controlMember, actualMember)
        ]).DistinctBy(cause => cause.BeforeDefinition.Identity).ToArray());
        proof.Impacts.Add(new VbaRenameImpact(
            VbaRenameImpactKind.DependentAssociationChanged,
            "The confirmed source-interface type collision makes this established Implements relationship ambiguous; its original implementation edit closure is retained.",
            uri, originalRange)
        {
            Evidence = new VbaRenameImpactEvidence(beforeBinding.Kind, controlBinding.Kind, actualBinding.Kind,
                controlRange, actualRange, evidence),
            InterfaceEvidence = new VbaRenameInterfaceImpactEvidence(original, controlMatches[0], [], evidence)
        });
        return true;
    }

    private static bool TryRecordInterfacePrefixCollisionImpact(
        VbaResolvedIdentifierOccurrence occurrence,
        VbaRange controlRange,
        VbaRange mappedRange,
        VbaNameResolutionOutcome controlClassification,
        VbaNameResolutionOutcome after,
        VbaRenameCollisionProof proof,
        VbaRenameImpactKind impactKind)
    {
        if (controlClassification.Kind != VbaNameResolutionKind.Resolved || after.Kind != VbaNameResolutionKind.Resolved)
        {
            return false;
        }
        var associations = proof.Impacts.Where(impact => impact.InterfaceEvidence is { After.Count: 0 })
            .Select(impact => impact.InterfaceEvidence!).ToArray();
        foreach (var association in associations)
        {
            var original = association.Before;
            var implementation = association.DeclarationCauses.Single(cause =>
                cause.BeforeDefinition.Identity == original.Implementation.Identity);
            if (!occurrence.Uri.Equals(original.Implementation.Uri, StringComparison.OrdinalIgnoreCase)
                || occurrence.Range != original.InterfacePrefixRange
                || !occurrence.Target.PhysicalDefinitions.Select(definition => definition.Identity).ToHashSet()
                    .SetEquals(original.Relationship.InterfaceTarget.PhysicalDefinitions.Select(definition => definition.Identity))
                || !controlClassification.Target!.PhysicalDefinitions.Select(definition => definition.Identity).ToHashSet()
                    .SetEquals(association.Control.Relationship.InterfaceTarget.PhysicalDefinitions.Select(definition => definition.Identity))
                || after.Target!.PhysicalDefinitions.Count != 1
                || after.Target.SelectedDefinition.Identity != implementation.AfterDefinition.Identity)
            {
                continue;
            }
            proof.Impacts.Add(new VbaRenameImpact(
                impactKind,
                "The confirmed source-interface type collision removes this established implementation-prefix binding; its original edit is retained.",
                occurrence.Uri, occurrence.Range)
            {
                Evidence = new VbaRenameImpactEvidence(VbaNameResolutionKind.Resolved, controlClassification.Kind, after.Kind,
                    controlRange, mappedRange, association.DeclarationCauses),
                InterfaceEvidence = association
            });
            return true;
        }
        return false;
    }

    private IReadOnlySet<string> RecordWithEventsCollisionImpacts(
        VbaSemanticInventory hypothetical,
        IReadOnlyList<VbaHandlerEventRenameConvergence> before,
        IReadOnlyList<VbaHandlerEventRenameConvergence> after,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        VbaRenameCollisionProof proof)
    {
        var permitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = proof.ControlPlan.TargetCorrespondence!.BeforeTarget.SelectedDefinition;
        var causes = CreateCollisionDeclarationCorrespondences(hypothetical, root, changes, proof);
        if (causes is null)
        {
            return permitted;
        }
        var control = proof.ControlInventory.GetHandlerEventRenameConvergences();
        foreach (var original in before)
        {
            var handler = original.HandlerAnalysis.Handler;
            var actualHandler = FindHypotheticalDefinition(hypothetical, handler, changes);
            var controlHandler = FindHypotheticalDefinition(proof.ControlInventory, handler, proof.ControlPlan.Changes);
            var actualMatches = after.Where(candidate => candidate.HandlerAnalysis.Handler.Identity == actualHandler?.Identity).ToArray();
            var controlMatches = control.Where(candidate => candidate.HandlerAnalysis.Handler.Identity == controlHandler?.Identity).ToArray();
            if (actualHandler is null || controlHandler is null || actualMatches.Length > 1 || controlMatches.Length != 1)
            {
                continue;
            }
            var actual = actualMatches.SingleOrDefault();
            var independent = controlMatches[0];
            if (actual is not null && CreateWithEventsAssociationProofKey(original, changes)
                    == hypothetical.CreateWithEventsAssociationProofKey(actual, changes: null)
                || original.Kind == VbaHandlerEventRenameConvergenceKind.Indeterminate
                || original.HandlerAnalysis.Recognition == VbaWithEventsHandlerRecognition.IndeterminateCandidate
                || original.HandlerAnalysis.BindingSet.Entries.Any(entry => entry.HasRecoveredEventEvidence)
                || CreateWithEventsAssociationProofKey(original, proof.ControlPlan.Changes)
                    != proof.ControlInventory.CreateWithEventsAssociationProofKey(independent, changes: null))
            {
                continue;
            }
            var variableCause = original.HandlerAnalysis.BindingSet.VariableTarget.PhysicalDefinitions.Any(definition =>
                causes.Any(cause => cause.BeforeDefinition.Identity == definition.Identity));
            var eventCause = original.HandlerAnalysis.BindingSet.Entries.SelectMany(entry => entry.ResolvedEventTargets)
                .SelectMany(target => target.PhysicalDefinitions).Any(definition =>
                    causes.Any(cause => cause.BeforeDefinition.Identity == definition.Identity));
            if (!variableCause && !eventCause)
            {
                if (TryRecordWithEventsSourceTypeCollisionImpact(
                    hypothetical, original, independent, actual, controlHandler, actualHandler, causes, changes, proof))
                {
                    permitted.Add(CreateOccurrenceKey(actualHandler.Uri, actualHandler.Range));
                }
                continue;
            }
            if (actual is null)
            {
                var variableAmbiguity = variableCause && causes.Count(cause =>
                    cause.AfterDefinition.Kind == VbaSourceDefinitionKind.Variable
                    && actualHandler.Name.Equals(cause.AfterDefinition.Name + "_" + original.HandlerAnalysis.Decomposition.EventName,
                        StringComparison.OrdinalIgnoreCase)) > 1;
                var eventAmbiguity = eventCause && causes.Count(cause =>
                    cause.AfterDefinition.Kind == VbaSourceDefinitionKind.Event
                    && actualHandler.Name.Equals(original.HandlerAnalysis.Decomposition.VariableName + "_" + cause.AfterDefinition.Name,
                        StringComparison.OrdinalIgnoreCase)) > 1;
                if (!variableAmbiguity && !eventAmbiguity)
                {
                    continue;
                }
            }
            if (actual is not null && variableCause && actual.HandlerAnalysis.BindingSet.VariableTarget.PhysicalDefinitions.Any(definition =>
                    !causes.Any(cause => cause.AfterDefinition.Identity == definition.Identity)))
            {
                continue;
            }
            if (actual is not null && eventCause && actual.HandlerAnalysis.BindingSet.Entries.SelectMany(entry => entry.ResolvedEventTargets)
                    .SelectMany(target => target.PhysicalDefinitions).Any(definition =>
                        !causes.Any(cause => cause.AfterDefinition.Identity == definition.Identity)))
            {
                continue;
            }

            var evidence = Array.AsReadOnly(causes.Append(new VbaRenameCausalDefinitionCorrespondence(
                handler, controlHandler, actualHandler)).ToArray());
            proof.Impacts.Add(new VbaRenameImpact(
                VbaRenameImpactKind.DependentAssociationChanged,
                "The confirmed collision changes this established WithEvents association; its original dependent edit closure is retained.",
                handler.Uri, handler.Range)
            {
                WithEventsEvidence = new VbaRenameWithEventsImpactEvidence(original, independent, actual, evidence)
            });
            permitted.Add(CreateOccurrenceKey(actualHandler.Uri, actualHandler.Range));
        }
        return permitted;
    }

    private bool TryRecordWithEventsSourceTypeCollisionImpact(
        VbaSemanticInventory hypothetical,
        VbaHandlerEventRenameConvergence before,
        VbaHandlerEventRenameConvergence control,
        VbaHandlerEventRenameConvergence? after,
        VbaSourceDefinition controlHandler,
        VbaSourceDefinition afterHandler,
        IReadOnlyList<VbaRenameCausalDefinitionCorrespondence> causes,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        VbaRenameCollisionProof proof)
    {
        var handler = before.HandlerAnalysis.Handler;
        if (after is null || after.Kind != VbaHandlerEventRenameConvergenceKind.Indeterminate
            || causes.Count < 2 || causes.Any(cause => cause.BeforeDefinition.Kind != VbaSourceDefinitionKind.Class)
            || !handler.Name.Equals(controlHandler.Name, StringComparison.Ordinal)
            || !handler.Name.Equals(afterHandler.Name, StringComparison.Ordinal)
            || before.HandlerAnalysis.BindingSet.Entries.Count == 0
            || before.HandlerAnalysis.BindingSet.Entries.Any(entry =>
                entry.Status != VbaWithEventsEventBindingStatus.Resolved || entry.HasRecoveredEventEvidence)
            || after.HandlerAnalysis.BindingSet.Entries.Count != before.HandlerAnalysis.BindingSet.Entries.Count
            || after.HandlerAnalysis.BindingSet.Entries.Any(entry =>
                entry.Status != VbaWithEventsEventBindingStatus.Indeterminate || entry.HasRecoveredEventEvidence
                || entry.ResolvedEventTargets.Count != 0))
        {
            return false;
        }
        var variables = before.HandlerAnalysis.BindingSet.Entries.Select(entry => new
        {
            Entry = entry,
            Before = entry.Variable,
            Control = FindHypotheticalDefinition(proof.ControlInventory, entry.Variable, proof.ControlPlan.Changes),
            After = FindHypotheticalDefinition(hypothetical, entry.Variable, changes)
        }).ToArray();
        if (variables.Any(variable => variable.Control is null || variable.After is null)
            || !variables.Select(variable => variable.After!.Identity).ToHashSet().SetEquals(
                after.HandlerAnalysis.BindingSet.VariableTarget.PhysicalDefinitions.Select(definition => definition.Identity))
            || !variables.Select(variable => variable.After!.Identity).ToHashSet().SetEquals(
                after.HandlerAnalysis.BindingSet.Entries.Select(entry => entry.Variable.Identity)))
        {
            return false;
        }
        var typeEvidence = new List<VbaRenameDeclaredTypeImpactEvidence>();
        foreach (var variable in variables)
        {
            var beforeType = semanticResolution.GetEffectiveDeclaredType(variable.Before);
            var afterType = hypothetical.semanticResolution.GetEffectiveDeclaredType(variable.After!);
            if (beforeType.State != VbaEffectiveDeclaredTypeState.Known || beforeType.Target is null
                || afterType.State != VbaEffectiveDeclaredTypeState.ExplicitlyUnresolved
                || variable.After!.TypeReferenceRange is not { } typeRange
                || variable.Entry.ResolvedEventTargets.Count == 0
                || !variable.Entry.ResolvedEventTargets.SelectMany(target => target.PhysicalDefinitions).All(eventDefinition =>
                    beforeType.Target.PhysicalDefinitions.Any(type => IsMemberOwnedByType(eventDefinition, type))))
            {
                return false;
            }
            var typeOutcome = hypothetical.semanticResolution.ClassifySourceDefinition(
                variable.After.Uri, typeRange.Start.Line, typeRange.Start.Character);
            if (typeOutcome.Kind != VbaNameResolutionKind.Ambiguous || typeOutcome.AmbiguousCandidates.Count == 0
                || !causes.Select(cause => cause.AfterDefinition.Identity).ToHashSet().SetEquals(
                    typeOutcome.AmbiguousCandidates.SelectMany(candidate => candidate.PhysicalDefinitions)
                        .Select(definition => definition.Identity))
                || !TryRecordTypeNameCollisionImpact(
                    hypothetical, variable.Before, variable.After, beforeType, afterType, changes, proof))
            {
                return false;
            }
            typeEvidence.Add(proof.Impacts[^1].DeclaredTypeEvidence!);
        }
        var evidence = Array.AsReadOnly(causes
            .Concat(variables.Select(variable => new VbaRenameCausalDefinitionCorrespondence(
                variable.Before, variable.Control!, variable.After!)))
            .Append(new VbaRenameCausalDefinitionCorrespondence(handler, controlHandler, afterHandler)).ToArray());
        foreach (var declaredType in typeEvidence)
        {
            proof.Impacts.Add(new VbaRenameImpact(
                VbaRenameImpactKind.DependentAssociationChanged,
                "The confirmed event-source type collision makes this established WithEvents association indeterminate; its handler text is retained.",
                handler.Uri, handler.Range)
            {
                DeclaredTypeEvidence = declaredType,
                WithEventsEvidence = new VbaRenameWithEventsImpactEvidence(before, control, after, evidence)
            });
        }
        return true;
    }

    private bool TryRecordWithEventsSegmentCollisionImpact(
        VbaSemanticInventory hypothetical,
        VbaResolvedIdentifierOccurrence occurrence,
        VbaRange controlRange,
        VbaRange mappedRange,
        VbaNameResolutionOutcome controlClassification,
        VbaNameResolutionOutcome after,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        VbaRenameCollisionProof proof,
        VbaRenameImpactKind impactKind)
    {
        if (TryRecordWithEventsEventNameCollisionImpact(
            hypothetical, occurrence, controlRange, mappedRange, controlClassification, after, changes, proof, impactKind))
        {
            return true;
        }
        if (controlClassification.Kind == VbaNameResolutionKind.Resolved
            && after.Kind == VbaNameResolutionKind.AnalysisIncomplete)
        {
            var typeAssociations = proof.Impacts.Where(impact => impact.DeclaredTypeEvidence is not null
                && impact.WithEventsEvidence?.After?.Kind == VbaHandlerEventRenameConvergenceKind.Indeterminate).ToArray();
            foreach (var impact in typeAssociations)
            {
                var association = impact.WithEventsEvidence!;
                var handler = association.Before.HandlerAnalysis.Handler;
                var suffixRange = new VbaRange(new VbaPosition(handler.Range.Start.Line,
                    handler.Range.Start.Character + association.Before.HandlerAnalysis.Decomposition.VariableName.Length + 1),
                    handler.Range.End);
                if (!VbaProjectIdentityModel.SameDocument(occurrence.Uri, handler.Uri)
                    || occurrence.Range != suffixRange || association.After!.HandlerAnalysis.EventTarget is not null)
                {
                    continue;
                }
                var beforeBindings = association.Before.HandlerAnalysis.BindingSet.Entries
                    .SelectMany(entry => entry.ResolvedEventTargets).SelectMany(target => target.PhysicalDefinitions)
                    .DistinctBy(definition => definition.Identity).ToArray();
                var controlBindings = association.Control.HandlerAnalysis.BindingSet.Entries
                    .SelectMany(entry => entry.ResolvedEventTargets).SelectMany(target => target.PhysicalDefinitions)
                    .Select(definition => definition.Identity).ToHashSet();
                var bindingCauses = beforeBindings.Select(definition => new
                {
                    Before = definition,
                    Control = FindHypotheticalDefinition(proof.ControlInventory, definition, proof.ControlPlan.Changes),
                    After = FindHypotheticalDefinition(hypothetical, definition, changes)
                }).ToArray();
                if (beforeBindings.Length == 0
                    || !beforeBindings.Select(definition => definition.Identity).ToHashSet()
                        .SetEquals(occurrence.Target.PhysicalDefinitions.Select(definition => definition.Identity))
                    || !controlBindings.SetEquals(controlClassification.Target!.PhysicalDefinitions.Select(definition => definition.Identity))
                    || bindingCauses.Any(cause => cause.Control is null || cause.After is null)
                    || !controlBindings.SetEquals(bindingCauses.Select(cause => cause.Control!.Identity)))
                {
                    continue;
                }
                proof.Impacts.Add(new VbaRenameImpact(
                    impactKind,
                    "The confirmed event-source type collision makes this established handler Event binding indeterminate; its text is retained.",
                    occurrence.Uri, occurrence.Range)
                {
                    DeclaredTypeEvidence = impact.DeclaredTypeEvidence,
                    WithEventsEvidence = association,
                    Evidence = new VbaRenameImpactEvidence(
                        VbaNameResolutionKind.Resolved, controlClassification.Kind, after.Kind, controlRange, mappedRange,
                        Array.AsReadOnly(association.DeclarationCauses.Concat(bindingCauses.Select(cause =>
                            new VbaRenameCausalDefinitionCorrespondence(cause.Before, cause.Control!, cause.After!)))
                            .DistinctBy(cause => cause.BeforeDefinition.Identity).ToArray()))
                });
                return true;
            }
        }
        if (controlClassification.Kind != VbaNameResolutionKind.Resolved
            || after.Kind != VbaNameResolutionKind.Resolved)
        {
            return false;
        }
        var associations = proof.Impacts.Where(impact => impact.WithEventsEvidence is not null)
            .Select(impact => impact.WithEventsEvidence!).ToArray();
        foreach (var association in associations)
        {
            var handler = association.Before.HandlerAnalysis.Handler;
            var prefixRange = new VbaRange(handler.Range.Start, new VbaPosition(
                handler.Range.Start.Line,
                handler.Range.Start.Character + association.Before.HandlerAnalysis.Decomposition.VariableName.Length));
            var suffixRange = new VbaRange(new VbaPosition(prefixRange.End.Line, prefixRange.End.Character + 1), handler.Range.End);
            var isPrefix = occurrence.Range == prefixRange;
            static IEnumerable<VbaSourceDefinition> SegmentDefinitions(VbaWithEventsHandlerAnalysis analysis, bool prefix)
                => prefix
                    ? analysis.BindingSet.VariableTarget.PhysicalDefinitions
                    : analysis.BindingSet.Entries.SelectMany(entry => entry.ResolvedEventTargets)
                        .SelectMany(target => target.PhysicalDefinitions).DistinctBy(definition => definition.Identity);
            var beforeBindings = SegmentDefinitions(association.Before.HandlerAnalysis, isPrefix).ToArray();
            var controlBindings = SegmentDefinitions(association.Control.HandlerAnalysis, isPrefix).ToArray();
            var handlerCause = association.DeclarationCauses.Single(cause =>
                cause.BeforeDefinition.Identity == handler.Identity);
            if (association.After is not null
                || !occurrence.Uri.Equals(handler.Uri, StringComparison.OrdinalIgnoreCase)
                || !isPrefix && occurrence.Range != suffixRange
                || !occurrence.Target.PhysicalDefinitions.Select(definition => definition.Identity).ToHashSet()
                    .SetEquals(beforeBindings.Select(definition => definition.Identity))
                || !controlClassification.Target!.PhysicalDefinitions.Select(definition => definition.Identity).ToHashSet()
                    .SetEquals(controlBindings.Select(definition => definition.Identity))
                || after.Target!.PhysicalDefinitions.Count != 1
                || after.Target.SelectedDefinition.Identity != handlerCause.AfterDefinition.Identity)
            {
                continue;
            }
            var bindingCauses = beforeBindings.Select(definition => new
            {
                Before = definition,
                Control = FindHypotheticalDefinition(proof.ControlInventory, definition, proof.ControlPlan.Changes),
                After = FindHypotheticalDefinition(hypothetical, definition, changes)
            }).ToArray();
            if (bindingCauses.Any(cause => cause.Control is null || cause.After is null
                || !controlBindings.Any(binding => binding.Identity == cause.Control.Identity)))
            {
                continue;
            }
            var evidence = Array.AsReadOnly(association.DeclarationCauses.Concat(bindingCauses.Select(cause =>
                new VbaRenameCausalDefinitionCorrespondence(cause.Before, cause.Control!, cause.After!)))
                .DistinctBy(cause => cause.BeforeDefinition.Identity).ToArray());
            proof.Impacts.Add(new VbaRenameImpact(
                impactKind,
                "The confirmed WithEvents collision removes this established handler-segment binding; the original dependent edit closure is retained.",
                occurrence.Uri, occurrence.Range)
            {
                Evidence = new VbaRenameImpactEvidence(
                    VbaNameResolutionKind.Resolved, controlClassification.Kind, after.Kind,
                    controlRange, mappedRange, evidence),
                WithEventsEvidence = association
            });
            return true;
        }
        return false;
    }

    private bool TryRecordWithEventsEventNameCollisionImpact(
        VbaSemanticInventory hypothetical,
        VbaResolvedIdentifierOccurrence occurrence,
        VbaRange controlRange,
        VbaRange mappedRange,
        VbaNameResolutionOutcome controlClassification,
        VbaNameResolutionOutcome after,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        VbaRenameCollisionProof proof,
        VbaRenameImpactKind impactKind)
    {
        if (controlClassification.Kind != VbaNameResolutionKind.Resolved
            || after.Kind != VbaNameResolutionKind.AnalysisIncomplete)
        {
            return false;
        }
        var associations = proof.Impacts.Where(impact => impact.WithEventsEvidence?.After?.Kind
                == VbaHandlerEventRenameConvergenceKind.Indeterminate)
            .Select(impact => impact.WithEventsEvidence!).ToArray();
        foreach (var association in associations)
        {
            var original = association.Before.HandlerAnalysis;
            var actual = association.After!.HandlerAnalysis;
            var suffixRange = new VbaRange(new VbaPosition(original.Handler.Range.Start.Line,
                original.Handler.Range.Start.Character + original.Decomposition.VariableName.Length + 1), original.Handler.Range.End);
            var events = association.DeclarationCauses.Where(cause => cause.BeforeDefinition.Kind == VbaSourceDefinitionKind.Event).ToArray();
            var originalEvents = original.BindingSet.Entries.SelectMany(entry => entry.ResolvedEventTargets)
                .SelectMany(target => target.PhysicalDefinitions).Select(definition => definition.Identity).ToHashSet();
            var controlEvents = association.Control.HandlerAnalysis.BindingSet.Entries.SelectMany(entry => entry.ResolvedEventTargets)
                .SelectMany(target => target.PhysicalDefinitions).Select(definition => definition.Identity).ToHashSet();
            if (!VbaProjectIdentityModel.SameDocument(occurrence.Uri, original.Handler.Uri)
                || occurrence.Range != suffixRange || events.Length < 2 || actual.EventTarget is not null
                || !originalEvents.SetEquals(occurrence.Target.PhysicalDefinitions.Select(definition => definition.Identity))
                || !controlEvents.SetEquals(controlClassification.Target!.PhysicalDefinitions.Select(definition => definition.Identity))
                || !originalEvents.All(identity => events.Any(cause => cause.BeforeDefinition.Identity == identity))
                || original.BindingSet.Entries.Count == 0
                || original.BindingSet.Entries.Any(entry => entry.Status != VbaWithEventsEventBindingStatus.Resolved || entry.HasRecoveredEventEvidence)
                || actual.BindingSet.Entries.Count != original.BindingSet.Entries.Count
                || actual.BindingSet.Entries.Any(entry => entry.Status != VbaWithEventsEventBindingStatus.Indeterminate
                    || entry.HasRecoveredEventEvidence || entry.ResolvedEventTargets.Count != 0))
            {
                continue;
            }
            var evidence = association.DeclarationCauses.ToList();
            var allVariablesProved = true;
            foreach (var entry in original.BindingSet.Entries)
            {
                var controlVariable = FindHypotheticalDefinition(proof.ControlInventory, entry.Variable, proof.ControlPlan.Changes);
                var actualVariable = FindHypotheticalDefinition(hypothetical, entry.Variable, changes);
                if (controlVariable is null || actualVariable is null
                    || !actual.BindingSet.Entries.Any(candidate => candidate.Variable.Identity == actualVariable.Identity))
                {
                    allVariablesProved = false;
                    break;
                }
                var beforeType = semanticResolution.GetEffectiveDeclaredType(entry.Variable);
                var controlType = proof.ControlInventory.semanticResolution.GetEffectiveDeclaredType(controlVariable);
                var actualType = hypothetical.semanticResolution.GetEffectiveDeclaredType(actualVariable);
                if (beforeType.State != VbaEffectiveDeclaredTypeState.Known || beforeType.Target is null
                    || controlType.State != VbaEffectiveDeclaredTypeState.Known || controlType.Target is null
                    || actualType.State != VbaEffectiveDeclaredTypeState.Known || actualType.Target is null)
                {
                    allVariablesProved = false;
                    break;
                }
                var typeCauses = beforeType.Target.PhysicalDefinitions.Select(definition => new
                {
                    Before = definition,
                    Control = FindHypotheticalDefinition(proof.ControlInventory, definition, proof.ControlPlan.Changes),
                    After = FindHypotheticalDefinition(hypothetical, definition, changes)
                }).ToArray();
                if (typeCauses.Any(cause => cause.Control is null || cause.After is null)
                    || !typeCauses.Select(cause => cause.Control!.Identity).ToHashSet()
                        .SetEquals(controlType.Target.PhysicalDefinitions.Select(definition => definition.Identity))
                    || !typeCauses.Select(cause => cause.After!.Identity).ToHashSet()
                        .SetEquals(actualType.Target.PhysicalDefinitions.Select(definition => definition.Identity))
                    || !entry.ResolvedEventTargets.SelectMany(target => target.PhysicalDefinitions).All(definition =>
                        beforeType.Target.PhysicalDefinitions.Any(type => IsMemberOwnedByType(definition, type)))
                    || events.Count(cause => cause.AfterDefinition.Name.Equals(actual.Decomposition.EventName, StringComparison.OrdinalIgnoreCase)
                        && actualType.Target.PhysicalDefinitions.Any(type => IsMemberOwnedByType(cause.AfterDefinition, type))) < 2)
                {
                    allVariablesProved = false;
                    break;
                }
                evidence.Add(new VbaRenameCausalDefinitionCorrespondence(entry.Variable, controlVariable, actualVariable));
                evidence.AddRange(typeCauses.Select(cause => new VbaRenameCausalDefinitionCorrespondence(cause.Before, cause.Control!, cause.After!)));
            }
            if (!allVariablesProved)
            {
                continue;
            }
            proof.Impacts.Add(new VbaRenameImpact(
                impactKind,
                "The confirmed Event-name collision makes this established handler suffix ambiguous within the same source Event type; its original edit is retained.",
                occurrence.Uri, occurrence.Range)
            {
                Evidence = new VbaRenameImpactEvidence(VbaNameResolutionKind.Resolved, controlClassification.Kind, after.Kind,
                    controlRange, mappedRange, Array.AsReadOnly(evidence.DistinctBy(cause => cause.BeforeDefinition.Identity).ToArray())),
                WithEventsEvidence = association
            });
            return true;
        }
        return false;
    }

    private bool TryRecordDependentCollisionBindingImpact(
        VbaSemanticInventory hypothetical,
        VbaResolvedIdentifierOccurrence occurrence,
        VbaRange mappedRange,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        VbaRenameCollisionProof? proof)
    {
        if (proof is null)
        {
            return false;
        }
        var controlRange = MapRange(occurrence.Uri, occurrence.Range, proof.ControlPlan.Changes);
        if (TryRecordWithEventsSegmentCollisionImpact(
            hypothetical, occurrence, controlRange, mappedRange,
            proof.ControlInventory.semanticResolution.ClassifySourceDefinition(
                occurrence.Uri, controlRange.Start.Line, controlRange.Start.Character),
            hypothetical.semanticResolution.ClassifySourceDefinition(
                occurrence.Uri, mappedRange.Start.Line, mappedRange.Start.Character),
            changes, proof, VbaRenameImpactKind.NonTargetBindingChanged))
        {
            return true;
        }
        var dependents = proof.Impacts.Where(impact => impact.WithEventsEvidence is not null)
            .Select(impact => impact.WithEventsEvidence!.Before.HandlerAnalysis.Handler)
            .Concat(proof.Impacts.Where(impact => impact.InterfaceEvidence is not null)
                .Select(impact => impact.InterfaceEvidence!.Before.Implementation))
            .DistinctBy(definition => definition.Identity).ToArray();
        return dependents.Any(dependent => TryRecordCollisionBindingImpact(
            hypothetical, dependent, occurrence, mappedRange, changes, proof, VbaRenameImpactKind.NonTargetBindingChanged));
    }

    private VbaRenameFailure? ProveWithEventsAssociationsArePreserved(
        VbaSemanticInventory hypothetical,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        VbaRenameCollisionProof? collisionProof = null)
    {
        var before = GetHandlerEventRenameConvergences();
        var after = hypothetical.GetHandlerEventRenameConvergences();
        if (collisionProof is not null)
        {
            var permittedHandlers = RecordWithEventsCollisionImpacts(hypothetical, before, after, changes, collisionProof);
            before = before.Where(convergence => !permittedHandlers.Contains(CreateOccurrenceKey(
                convergence.HandlerAnalysis.Handler.Uri,
                MapRange(convergence.HandlerAnalysis.Handler.Uri, convergence.HandlerAnalysis.Handler.Range, changes)))).ToArray();
            after = after.Where(convergence => !permittedHandlers.Contains(CreateOccurrenceKey(
                convergence.HandlerAnalysis.Handler.Uri, convergence.HandlerAnalysis.Handler.Range))).ToArray();
        }
        var beforeIncompleteCounts = before
            .Where(convergence => convergence.Kind
                == VbaHandlerEventRenameConvergenceKind.Indeterminate)
            .Select(convergence => CreateWithEventsAssociationProofKey(
                convergence,
                changes))
            .GroupBy(key => key)
            .ToDictionary(group => group.Key, group => group.Count());
        var afterIncompleteCounts = after
            .Where(convergence => convergence.Kind
                == VbaHandlerEventRenameConvergenceKind.Indeterminate)
            .Select(convergence => hypothetical
                .CreateWithEventsAssociationProofKey(
                    convergence,
                    changes: null))
            .GroupBy(key => key)
            .ToDictionary(group => group.Key, group => group.Count());
        if (!HaveEqualProofCounts(
                beforeIncompleteCounts,
                afterIncompleteCounts))
        {
            return AnalysisIncomplete(
                "Rename would change incomplete WithEvents handler "
                + "association evidence.");
        }

        var beforeCounts = before
            .Where(convergence => convergence.Kind
                    != VbaHandlerEventRenameConvergenceKind.Indeterminate
                && convergence.HandlerAnalysis.Recognition is
                    VbaWithEventsHandlerRecognition.ResolvedHandler
                        or VbaWithEventsHandlerRecognition
                            .NonSubProcedureAssociation)
            .Select(convergence => CreateWithEventsAssociationProofKey(
                convergence,
                changes))
            .GroupBy(key => key)
            .ToDictionary(group => group.Key, group => group.Count());
        var afterCounts = after
            .Where(convergence => convergence.Kind
                    != VbaHandlerEventRenameConvergenceKind.Indeterminate
                && convergence.HandlerAnalysis.Recognition is
                    VbaWithEventsHandlerRecognition.ResolvedHandler
                        or VbaWithEventsHandlerRecognition
                            .NonSubProcedureAssociation)
            .Select(convergence => hypothetical
                .CreateWithEventsAssociationProofKey(
                    convergence,
                    changes: null))
            .GroupBy(key => key)
            .ToDictionary(group => group.Key, group => group.Count());
        return HaveEqualProofCounts(beforeCounts, afterCounts)
            ? null
            : ResolutionChanged(
                "Rename would change a WithEvents handler association.");
    }

    private VbaWithEventsAssociationProofKey
        CreateWithEventsAssociationProofKey(
            VbaHandlerEventRenameConvergence convergence,
            IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>>? changes)
    {
        VbaRange Map(string uri, VbaRange range)
            => changes is null ? range : MapRange(uri, range, changes);

        var analysis = convergence.HandlerAnalysis;
        var handler = analysis.Handler;
        var separatorOffset = analysis.Decomposition.VariableName.Length;
        var separatorCharacter = handler.Range.Start.Character
            + separatorOffset;
        var prefixRange = new VbaRange(
            handler.Range.Start,
            new VbaPosition(handler.Range.Start.Line, separatorCharacter));
        var separatorRange = new VbaRange(
            new VbaPosition(handler.Range.Start.Line, separatorCharacter),
            new VbaPosition(handler.Range.Start.Line, separatorCharacter + 1));
        var suffixRange = new VbaRange(
            new VbaPosition(handler.Range.Start.Line, separatorCharacter + 1),
            handler.Range.End);
        var handlerTarget = resolutionPolicy.CreateNameTarget(
            GetLogicalRenameTarget(handler));
        var bindingEntries = string.Join(
            "\u001d",
            analysis.BindingSet.Entries
                .Select(entry => CreateWithEventsBindingEntryProofKey(
                    entry,
                    changes))
                .OrderBy(key => key, StringComparer.Ordinal));
        return new VbaWithEventsAssociationProofKey(
            CreateOccurrenceKey(handler.Uri, Map(handler.Uri, handler.Range)),
            CreateInterfaceTargetProofKey(handlerTarget, changes),
            CreateInterfaceTargetProofKey(
                analysis.BindingSet.VariableTarget,
                changes),
            CreateOccurrenceKey(handler.Uri, Map(handler.Uri, prefixRange)),
            CreateOccurrenceKey(handler.Uri, Map(handler.Uri, separatorRange)),
            CreateOccurrenceKey(handler.Uri, Map(handler.Uri, suffixRange)),
            analysis.Recognition,
            convergence.Kind,
            bindingEntries);
    }

    private string CreateWithEventsBindingEntryProofKey(
        VbaWithEventsEventBindingEntry entry,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>>? changes)
    {
        var variableRange = changes is null
            ? entry.Variable.Range
            : MapRange(entry.Variable.Uri, entry.Variable.Range, changes);
        var eventTargets = string.Join(
            "\u001c",
            entry.ResolvedEventTargets
                .Select(target => CreateWithEventsEventTargetProofKey(
                    target,
                    changes))
                .OrderBy(key => key, StringComparer.Ordinal));
        var eventContracts = string.Join(
            "\u001c",
            (entry.EventContracts ?? [])
                .Select(contract => CreateWithEventsEventContractProofKey(
                    contract,
                    changes))
                .OrderBy(key => key, StringComparer.Ordinal));
        return $"{entry.Status}:{entry.HasRecoveredEventEvidence}:"
            + $"{entry.Variable.Kind}:"
            + CreateOccurrenceKey(entry.Variable.Uri, variableRange)
            + $":{eventTargets}:{eventContracts}";
    }

    private string CreateWithEventsEventTargetProofKey(
        VbaResolvedNameTarget target,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>>? changes)
        => target switch
        {
            VbaHostEventNameTarget hostTarget =>
                "host:"
                + CreateWithEventsEventContractProofKey(
                    hostTarget.EventContract,
                    changes),
            _ => "definition:" + CreateInterfaceTargetProofKey(
                target,
                changes)
        };

    private string CreateWithEventsEventContractProofKey(
        VbaResolvedEventContract contract,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>>? changes)
    {
        var identity = contract.Identity switch
        {
            VbaProjectedEventContractIdentity projected =>
                $"projected:{projected.Provider}:{projected.StableIdentity}",
            VbaDefinitionEventContractIdentity when contract.Definition is { }
                definition =>
                "definition:"
                + CreateOccurrenceKey(
                    definition.Uri,
                    changes is null
                        ? definition.Range
                        : MapRange(definition.Uri, definition.Range, changes)),
            _ => contract.Identity.ToString() ?? contract.Identity.GetType().Name
        };
        return $"{identity}:{contract.ValidationAuthority}:"
            + $"{contract.IsConditionalContract}:"
            + $"{contract.IsAuthoringAvailable}";
    }

    private static bool HaveEqualProofCounts<TKey>(
        IReadOnlyDictionary<TKey, int> before,
        IReadOnlyDictionary<TKey, int> after)
        where TKey : notnull
        => before.Count == after.Count
            && before.All(pair =>
                after.TryGetValue(pair.Key, out var afterCount)
                && afterCount == pair.Value);

    private VbaInterfaceAssociationProofKey CreateInterfaceAssociationProofKey(
        VbaInterfaceImplementationAssociation association,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>>? changes)
    {
        VbaRange Map(string uri, VbaRange range)
            => changes is null ? range : MapRange(uri, range, changes);

        var implementingUri = association.Relationship.ImplementingDocument.Uri;
        var implementationUri = association.Implementation.Uri;
        var contractUri = association.Contract.OriginDefinition.Uri;
        return new VbaInterfaceAssociationProofKey(
            CreateOccurrenceKey(
                implementingUri,
                Map(
                    implementingUri,
                    association.Relationship.InterfaceTypeRange)),
            CreateInterfaceTargetProofKey(
                association.Relationship.InterfaceTarget,
                changes),
            CreateOccurrenceKey(
                contractUri,
                Map(contractUri, association.Contract.OriginDefinition.Range)),
            CreateInterfaceTargetProofKey(association.MemberTarget, changes),
            association.Contract.Kind,
            association.Contract.IsDerivedVariableAccessor,
            association.CompatibilityState,
            CreateOccurrenceKey(
                implementationUri,
                Map(implementationUri, association.Implementation.Range)),
            CreateInterfaceTargetProofKey(
                association.ImplementationTarget,
                changes),
            CreateOccurrenceKey(
                implementationUri,
                Map(implementationUri, association.InterfacePrefixRange)),
            CreateOccurrenceKey(
                implementationUri,
                Map(implementationUri, association.SeparatorRange)),
            CreateOccurrenceKey(
                implementationUri,
                Map(implementationUri, association.MemberSuffixRange)));
    }

    private string CreateInterfaceTargetProofKey(
        VbaResolvedNameTarget target,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>>? changes)
        => string.Join(
            ";",
            target.PhysicalDefinitions
                .OrderBy(
                    definition => definition.Uri,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(definition => definition.Uri, StringComparer.Ordinal)
                .ThenBy(definition => definition.Range.Start.Line)
                .ThenBy(definition => definition.Range.Start.Character)
                .Select(definition =>
                    $"{definition.Kind}:{definition.PropertyAccessorKind}:"
                    + CreateOccurrenceKey(
                        definition.Uri,
                        changes is null
                            ? definition.Range
                            : MapRange(
                                definition.Uri,
                                definition.Range,
                                changes))));

    private static VbaRenameFailure?
        TryCreateOccurrenceTargetCorrespondence(
            VbaResolvedIdentifierOccurrence occurrence,
            VbaRange mappedRange,
            VbaResolvedNameTarget postTarget,
            VbaRenameTargetCorrespondence targetCorrespondence,
            out VbaRenameOccurrenceTargetCorrespondence? correspondence)
    {
        correspondence = null;
        var definitionCorrespondence = targetCorrespondence
            .PhysicalDefinitions
            .ToDictionary(pair => pair.BeforeDefinition.Identity);
        var possibleDefinitions = new List<
            VbaRenamePhysicalDefinitionCorrespondence>(
                occurrence.Target.PhysicalDefinitions.Count);
        foreach (var definition in occurrence.Target.PhysicalDefinitions)
        {
            if (!definitionCorrespondence.TryGetValue(
                    definition.Identity,
                    out var definitionPair))
            {
                return AnalysisIncomplete(
                    "Rename could not compare a target occurrence's "
                    + "possible definitions completely.");
            }

            possibleDefinitions.Add(definitionPair);
        }

        var expectedAfterIdentities = possibleDefinitions
            .Select(pair => pair.AfterDefinition.Identity)
            .ToHashSet();
        var actualAfterIdentities = postTarget.PhysicalDefinitions
            .Select(definition => definition.Identity)
            .ToHashSet();
        if (possibleDefinitions.Count == 0
            || expectedAfterIdentities.Count != possibleDefinitions.Count
            || actualAfterIdentities.Count
                != postTarget.PhysicalDefinitions.Count)
        {
            return AnalysisIncomplete(
                "Rename could not establish one-to-one target occurrence "
                + "possible-definition correspondence.");
        }

        if (!expectedAfterIdentities.SetEquals(actualAfterIdentities)
            || postTarget is not VbaWithEventsEventNameTarget
                && occurrence.Target.IsConditionalFamily
                    != postTarget.IsConditionalFamily)
        {
            return ResolutionChanged(
                "Rename would change a target occurrence's possible "
                + "definitions.");
        }

        correspondence = new VbaRenameOccurrenceTargetCorrespondence(
            occurrence.Uri,
            occurrence.Range,
            mappedRange,
            occurrence.Target,
            postTarget,
            Array.AsReadOnly(possibleDefinitions.ToArray()));
        return null;
    }

    private VbaRenameFailure?
        ProveConditionalCallCompatibilitiesArePreserved(
            VbaSemanticInventory hypothetical,
            IReadOnlyList<VbaResolvedIdentifierOccurrence> targetOccurrences,
            IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
            VbaRenameTargetCorrespondence targetCorrespondence,
            CancellationToken cancellationToken,
            out IReadOnlyList<VbaRenameCallCompatibilityCorrespondence>
                callCompatibilities)
    {
        callCompatibilities = [];
        if (!targetCorrespondence.PhysicalDefinitions.Any(pair =>
                pair.BeforeDefinition.ConditionalCompilationPath is
                    { IsEmpty: false }))
        {
            return null;
        }

        if (!targetCorrespondence.PhysicalDefinitions.Any(pair =>
                pair.BeforeDefinition.Kind is
                    VbaSourceDefinitionKind.Procedure
                    or VbaSourceDefinitionKind.Property
                    or VbaSourceDefinitionKind.Event))
        {
            return null;
        }

        var definitionCorrespondence = targetCorrespondence
            .PhysicalDefinitions
            .ToDictionary(pair => pair.BeforeDefinition.Identity);
        var postDefinitionIdentities = targetCorrespondence
            .PhysicalDefinitions
            .Select(pair => pair.AfterDefinition.Identity)
            .ToHashSet();
        var results = new List<VbaRenameCallCompatibilityCorrespondence>();
        foreach (var occurrence in targetOccurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var beforeDocument = definitionCandidates.FindDocument(
                occurrence.Uri);
            if (beforeDocument is null)
            {
                return AnalysisIncomplete(
                    "Rename could not inspect a target call occurrence.");
            }

            var beforeSyntaxTree = beforeDocument.SyntaxTree
                ?? VbaSyntaxTree.ParseModule(
                    beforeDocument.Uri,
                    beforeDocument.Text);
            var beforeCalls = beforeSyntaxTree.Module.ArgumentLists
                .Where(argumentList => argumentList.CalleeRange is { } range
                    && IsTerminalCalleeIdentifier(
                        range,
                        occurrence.Range))
                .ToArray();
            if (beforeCalls.Length == 0)
            {
                continue;
            }

            if (beforeCalls.Length != 1)
            {
                return AnalysisIncomplete(
                    "Rename could not identify one complete target call.");
            }

            var beforeCall = beforeCalls[0];
            var beforeCompatibility = semanticResolution.AnalyzeCompleteCall(
                occurrence.Uri,
                beforeCall);
            var beforeIsResultAssignment =
                VbaSemanticResolution.IsCallableResultAssignment(
                    beforeDocument,
                    beforeCall,
                    beforeCall.CalleeRange!);

            var beforeRange = ToRange(beforeCall.Range);
            var mappedRange = MapRange(
                occurrence.Uri,
                beforeRange,
                changes);
            var mappedCalleeRange = MapRange(
                occurrence.Uri,
                ToRange(beforeCall.CalleeRange!),
                changes);
            var afterDocument = hypothetical.definitionCandidates.FindDocument(
                occurrence.Uri);
            if (afterDocument is null)
            {
                return AnalysisIncomplete(
                    "Rename could not inspect the hypothetical target call.");
            }

            var afterSyntaxTree = afterDocument.SyntaxTree
                ?? VbaSyntaxTree.ParseModule(
                    afterDocument.Uri,
                    afterDocument.Text);
            var afterCalls = afterSyntaxTree.Module.ArgumentLists
                .Where(argumentList => argumentList.Form == beforeCall.Form)
                .Where(argumentList => argumentList.CalleeRange is { } range
                    && ToRange(range) == mappedCalleeRange)
                .Where(argumentList => ToRange(argumentList.Range)
                    == mappedRange)
                .ToArray();
            if (afterCalls.Length != 1)
            {
                return AnalysisIncomplete(
                    "Rename could not establish the hypothetical target "
                    + "call correspondence.");
            }

            var afterCall = afterCalls[0];
            var afterCompatibility = hypothetical.semanticResolution
                .AnalyzeCompleteCall(occurrence.Uri, afterCall);
            var afterIsResultAssignment =
                VbaSemanticResolution.IsCallableResultAssignment(
                    afterDocument,
                    afterCall,
                    afterCall.CalleeRange!);
            if (beforeIsResultAssignment != afterIsResultAssignment)
            {
                return ResolutionChanged(
                    "Rename would change a target occurrence's callable "
                    + "result role.");
            }

            if (beforeIsResultAssignment)
            {
                continue;
            }

            if (beforeCompatibility is null || afterCompatibility is null)
            {
                return AnalysisIncomplete(
                    "Rename could not classify a target call "
                    + "completely.");
            }

            var beforeVariants = beforeCompatibility.Variants
                .Where(variant => definitionCorrespondence.ContainsKey(
                    variant.Definition.Identity))
                .ToArray();
            if (beforeVariants.Length == 0)
            {
                return AnalysisIncomplete(
                    "Rename could not relate a target call to its physical "
                    + "declarations.");
            }

            if (beforeCompatibility.Context != afterCompatibility.Context)
            {
                return ResolutionChanged(
                    "Rename would change a target call's invocation context.");
            }

            var afterVariants = afterCompatibility.Variants
                .Where(variant => postDefinitionIdentities.Contains(
                    variant.Definition.Identity))
                .ToArray();
            var expectedAfterIdentities = beforeVariants
                .Select(variant => definitionCorrespondence[
                    variant.Definition.Identity].AfterDefinition.Identity)
                .ToHashSet();
            var actualAfterIdentities = afterVariants
                .Select(variant => variant.Definition.Identity)
                .ToHashSet();
            if (expectedAfterIdentities.Count != beforeVariants.Length
                || actualAfterIdentities.Count != afterVariants.Length)
            {
                return AnalysisIncomplete(
                    "Rename could not establish one-to-one target call "
                    + "variant correspondence.");
            }

            if (!expectedAfterIdentities.SetEquals(actualAfterIdentities))
            {
                return ResolutionChanged(
                    "Rename would change a target call's possible "
                    + "declaration set.");
            }

            var variantResults = new List<
                VbaRenameCallVariantCorrespondence>(beforeVariants.Length);
            foreach (var beforeVariant in beforeVariants)
            {
                var definitionPair = definitionCorrespondence[
                    beforeVariant.Definition.Identity];
                var matchingAfterVariants = afterVariants
                    .Where(variant => variant.Definition.Identity
                        == definitionPair.AfterDefinition.Identity)
                    .ToArray();
                if (matchingAfterVariants.Length != 1)
                {
                    return AnalysisIncomplete(
                        "Rename could not establish one target call variant "
                        + "correspondence.");
                }

                var afterVariant = matchingAfterVariants[0];
                if (beforeVariant.State != afterVariant.State)
                {
                    return ResolutionChanged(
                        "Rename would change a target call's conditional "
                        + "compatibility.");
                }

                variantResults.Add(new VbaRenameCallVariantCorrespondence(
                    definitionPair,
                    beforeVariant.State,
                    afterVariant.State));
            }

            if (variantResults.Any(result =>
                    result.BeforeState == VbaCallCompatibilityState.Indeterminate
                    || result.AfterState
                        == VbaCallCompatibilityState.Indeterminate))
            {
                return AnalysisIncomplete(
                    "Rename could not compare a target call's conditional "
                    + "compatibility completely.");
            }

            results.Add(new VbaRenameCallCompatibilityCorrespondence(
                occurrence.Uri,
                beforeRange,
                mappedRange,
                beforeCompatibility.Context,
                afterCompatibility.Context,
                Array.AsReadOnly(variantResults.ToArray())));
        }

        callCompatibilities = Array.AsReadOnly(results.ToArray());
        return null;
    }

    private static bool IsTerminalCalleeIdentifier(
        VbaSyntaxRange calleeRange,
        VbaRange identifierRange)
    {
        var callee = ToRange(calleeRange);
        return callee.End == identifierRange.End
            && IsAtOrAfter(identifierRange.Start, callee.Start);
    }

    private VbaRenameFailure? TryCreateTargetCorrespondence(
        VbaSemanticInventory hypothetical,
        VbaSourceDefinition beforeDefinition,
        VbaSourceDefinition afterDefinition,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        string newName,
        out VbaRenameTargetCorrespondence? correspondence,
        VbaRenameCollisionProof? collisionProof = null)
    {
        correspondence = null;
        var beforeTarget = resolutionPolicy.CreateNameTarget(beforeDefinition);
        var afterTarget = hypothetical.resolutionPolicy.CreateNameTarget(
            afterDefinition);
        var beforePhysicalDefinitions = GetLogicalRenameTargetDefinitions(
            beforeDefinition);
        var afterPhysicalDefinitions = hypothetical
            .GetLogicalRenameTargetDefinitions(afterDefinition);
        var physicalCorrespondence = new List<
            VbaRenamePhysicalDefinitionCorrespondence>(
                beforePhysicalDefinitions.Count);
        foreach (var physicalBefore in beforePhysicalDefinitions)
        {
            var physicalAfter = FindHypotheticalDefinition(
                hypothetical,
                physicalBefore,
                changes,
                newName);
            if (physicalAfter is null)
            {
                return AnalysisIncomplete(
                    "Rename could not establish complete physical target "
                    + "correspondence.");
            }

            if (!VbaProjectIdentityModel.SameDocument(
                    physicalBefore.Uri,
                    physicalAfter.Uri)
                || physicalBefore.Kind != physicalAfter.Kind
                || physicalBefore.PropertyAccessorKind
                    != physicalAfter.PropertyAccessorKind
                || physicalBefore.Visibility != physicalAfter.Visibility
                || !AreConditionalCompilationPathsCorrespondent(
                    physicalBefore,
                    physicalAfter,
                    changes))
            {
                return ResolutionChanged(
                    "Rename would change a physical target declaration's "
                    + "conditional-family meaning.");
            }

            physicalCorrespondence.Add(
                new VbaRenamePhysicalDefinitionCorrespondence(
                    physicalBefore,
                    physicalAfter));
        }

        var mappedAfterIdentities = physicalCorrespondence
            .Select(pair => pair.AfterDefinition.Identity)
            .ToHashSet();
        if (mappedAfterIdentities.Count != physicalCorrespondence.Count)
        {
            return AnalysisIncomplete(
                "Rename could not establish one-to-one physical target "
                + "correspondence.");
        }

        if (afterPhysicalDefinitions.Count
                != physicalCorrespondence.Count
            || afterPhysicalDefinitions.Any(definition =>
                !mappedAfterIdentities.Contains(definition.Identity))
            || beforeTarget.IsConditionalFamily
                != afterTarget.IsConditionalFamily
            || !afterTarget.CanonicalName.Equals(
                newName,
                StringComparison.Ordinal))
        {
            var causes = CreateCollisionDeclarationCorrespondences(
                hypothetical, beforeDefinition, changes, collisionProof);
            if (collisionProof is null || causes is null || !afterTarget.CanonicalName.Equals(newName, StringComparison.OrdinalIgnoreCase)
                || afterPhysicalDefinitions.Any(definition => !causes.Any(cause =>
                    cause.AfterDefinition.Identity == definition.Identity)))
            {
                return ResolutionChanged(
                    "Rename would change the target's physical declaration set "
                    + "or logical-family meaning.");
            }
            var controlDefinition = causes.Single(cause => cause.BeforeDefinition.Identity == beforeDefinition.Identity)
                .ControlDefinition;
            collisionProof.Impacts.Add(new VbaRenameImpact(
                VbaRenameImpactKind.LogicalGroupingChanged,
                "The confirmed collision changes the logical declaration group; only the original physical declarations are renamed.",
                beforeDefinition.Uri, beforeDefinition.Range)
            {
                GroupingEvidence = new VbaRenameGroupingImpactEvidence(
                    beforeTarget, collisionProof.ControlInventory.resolutionPolicy.CreateNameTarget(controlDefinition),
                    afterTarget, causes)
            });
        }

        correspondence = new VbaRenameTargetCorrespondence(
            beforeTarget,
            afterTarget,
            Array.AsReadOnly(physicalCorrespondence.ToArray()));
        return null;
    }

    private bool AreConditionalCompilationPathsCorrespondent(
        VbaSourceDefinition beforeDefinition,
        VbaSourceDefinition afterDefinition,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes)
    {
        var beforePath = beforeDefinition.ConditionalCompilationPath;
        var afterPath = afterDefinition.ConditionalCompilationPath;
        if (beforePath is null || afterPath is null)
        {
            return beforePath is null && afterPath is null;
        }

        if (beforePath.Branches.Count != afterPath.Branches.Count)
        {
            return false;
        }

        for (var index = 0; index < beforePath.Branches.Count; index++)
        {
            var beforeBranch = beforePath.Branches[index];
            var afterBranch = afterPath.Branches[index];
            if (MapDocumentOffset(
                    beforeDefinition.Uri,
                    beforeBranch.IfDirectiveOffset,
                    changes) != afterBranch.IfDirectiveOffset
                || MapDocumentOffset(
                    beforeDefinition.Uri,
                    beforeBranch.BranchDirectiveOffset,
                    changes) != afterBranch.BranchDirectiveOffset)
            {
                return false;
            }
        }

        return true;
    }

    private int MapDocumentOffset(
        string uri,
        int offset,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes)
    {
        if (!changes.TryGetValue(uri, out var edits)
            || definitionCandidates.FindDocument(uri) is not { } document)
        {
            return offset;
        }

        var lineStarts = GetLineStarts(document.Text);
        var mappedOffset = offset;
        foreach (var edit in edits)
        {
            var editStart = GetOffset(lineStarts, edit.Range.Start);
            var editEnd = GetOffset(lineStarts, edit.Range.End);
            if (editEnd <= offset)
            {
                mappedOffset += edit.NewText.Length - (editEnd - editStart);
            }
        }

        return mappedOffset;
    }

    private IReadOnlyList<VbaSemanticOccurrence> GetUnresolvedSemanticOccurrences(
        CancellationToken cancellationToken)
    {
        var occurrences = new List<VbaSemanticOccurrence>();
        foreach (var document in sourceDocuments)
        {
            var syntaxTree = document.SyntaxTree
                ?? VbaSyntaxTree.ParseModule(document.Uri, document.Text);
            foreach (var token in syntaxTree.TokenStream.Tokens.Where(token =>
                VbaIdentifier.IsIdentifier(token.Text)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var positionSyntax = syntaxTree.GetPositionSyntax(
                    token.Range.Start.Line,
                    token.Range.Start.Character);
                if (positionSyntax.Region != VbaPositionRegion.Code
                    || positionSyntax.Identifier?.IsKeyword != false)
                {
                    continue;
                }


                var classification = semanticResolution
                    .ClassifySourceDefinition(
                        document.Uri,
                        token.Range.Start.Line,
                        token.Range.Start.Character);
                if (classification.Kind is VbaNameResolutionKind.Resolved
                    or VbaNameResolutionKind.NonSemantic)
                {
                    continue;
                }

                occurrences.Add(new VbaSemanticOccurrence(
                    document.Uri,
                    token.Text,
                    new VbaRange(
                        new VbaPosition(
                            token.Range.Start.Line,
                            token.Range.Start.Character),
                        new VbaPosition(
                            token.Range.End.Line,
                            token.Range.End.Character)),
                    classification.Kind));
            }
        }

        return occurrences
            .OrderBy(occurrence => occurrence.Uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(occurrence => occurrence.Range.Start.Line)
            .ThenBy(occurrence => occurrence.Range.Start.Character)
            .ToArray();
    }

    private VbaSemanticInventory CreateHypotheticalInventory(
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        CancellationToken cancellationToken)
    {
        var documents = new Dictionary<string, VbaSourceDocument>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var document in sourceDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = changes.TryGetValue(document.Uri, out var edits)
                ? ApplyTextEdits(document.Text, edits)
                : document.Text;
            var syntaxTree = VbaSyntaxTree.ParseModule(document.Uri, text);
            documents[document.Uri] = VbaSourceDocumentProjector.Project(
                document.Uri,
                syntaxTree);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Create(
            documents,
            referenceSelection,
            referenceCatalogs,
            intrinsicHostEventCatalog,
            referenceCatalogSources,
            referenceCatalogIdentities,
            projectResolution,
            authoritativeReferencedProjectNames);
    }

    private static VbaSourceDefinition? FindHypotheticalDefinition(
        VbaSemanticInventory hypothetical,
        VbaSourceDefinition definition,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes,
        string? expectedName = null)
    {
        if (definition.Identity.Origin == VbaDefinitionOrigin.ProjectReference)
        {
            return hypothetical.resolvedOccurrences
                .GetAll()
                .Select(occurrence => occurrence.Target.SelectedDefinition)
                .FirstOrDefault(candidate => candidate.Identity == definition.Identity)
                ?? definition;
        }

        var mappedRange = MapRange(definition.Uri, definition.Range, changes);
        var effectiveExpectedName = expectedName
            ?? GetHypotheticalDefinitionName(definition, changes);
        return hypothetical.GetDocumentDefinitions(definition.Uri)
            .FirstOrDefault(candidate =>
                candidate.Kind == definition.Kind
                && candidate.Range.Start == mappedRange.Start
                && string.Equals(
                    candidate.Name,
                    effectiveExpectedName,
                    expectedName is null
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal));
    }

    private static string GetHypotheticalDefinitionName(
        VbaSourceDefinition definition,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes)
    {
        if (!changes.TryGetValue(definition.Uri, out var edits)
            || definition.Range.Start.Line != definition.Range.End.Line)
        {
            return definition.Name;
        }

        var name = definition.Name;
        foreach (var edit in edits
            .Where(edit => edit.Range.Start.Line
                    == definition.Range.Start.Line
                && edit.Range.End.Line == definition.Range.End.Line
                && edit.Range.Start.Character
                    >= definition.Range.Start.Character
                && edit.Range.End.Character
                    <= definition.Range.End.Character)
            .OrderByDescending(edit => edit.Range.Start.Character))
        {
            var relativeStart = edit.Range.Start.Character
                - definition.Range.Start.Character;
            var relativeLength = edit.Range.End.Character
                - edit.Range.Start.Character;
            name = name.Remove(relativeStart, relativeLength)
                .Insert(relativeStart, edit.NewText);
        }

        return name;
    }

    private static bool AreLogicalDefinitionsEquivalent(
        VbaSemanticInventory inventory,
        VbaSourceDefinition? left,
        VbaSourceDefinition? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        return inventory.resolutionPolicy.CreateNameTarget(left).Identity
            == inventory.resolutionPolicy.CreateNameTarget(right).Identity;
    }

    private static string ApplyTextEdits(
        string text,
        IReadOnlyList<VbaTextEdit> edits)
    {
        var lineStarts = GetLineStarts(text);
        foreach (var edit in edits
            .OrderByDescending(edit => GetOffset(lineStarts, edit.Range.Start)))
        {
            var start = GetOffset(lineStarts, edit.Range.Start);
            var end = GetOffset(lineStarts, edit.Range.End);
            text = text[..start] + edit.NewText + text[end..];
        }

        return text;
    }

    private static VbaRange MapRange(
        string uri,
        VbaRange range,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes)
    {
        if (!changes.TryGetValue(uri, out var edits))
        {
            return range;
        }

        return new VbaRange(
            MapPosition(range.Start, edits, isRangeEnd: false),
            MapPosition(range.End, edits, isRangeEnd: true));
    }

    private static VbaRange ToRange(VbaSyntaxRange range)
        => new(
            new VbaPosition(
                range.Start.Line,
                range.Start.Character),
            new VbaPosition(
                range.End.Line,
                range.End.Character));

    private static VbaPosition MapPosition(
        VbaPosition position,
        IReadOnlyList<VbaTextEdit> edits,
        bool isRangeEnd)
    {
        var character = position.Character;
        foreach (var edit in edits
            .Where(edit => edit.Range.Start.Line == position.Line)
            .OrderBy(edit => edit.Range.Start.Character))
        {
            var oldLength = edit.Range.End.Character
                - edit.Range.Start.Character;
            var delta = edit.NewText.Length - oldLength;
            if (edit.Range.End.Character < position.Character
                || (edit.Range.End.Character == position.Character
                    && (isRangeEnd
                        || edit.Range.Start.Character
                            != position.Character)))
            {
                character += delta;
            }
        }

        return new VbaPosition(position.Line, character);
    }

    private static IReadOnlyList<int> GetLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                starts.Add(index + 1);
            }
        }

        return starts;
    }

    private static int GetOffset(
        IReadOnlyList<int> lineStarts,
        VbaPosition position)
        => lineStarts[position.Line] + position.Character;

    private static string CreateOccurrenceKey(string uri, VbaRange range)
        => $"{VbaProjectIdentityModel.GetDocumentStableKey(uri)}"
            + $"\u001f{GetRangeKey(range)}";

    private static bool IsDeclarationOccurrence(
        VbaResolvedIdentifierOccurrence occurrence)
        => VbaProjectIdentityModel.SameDocument(
                occurrence.Uri,
                occurrence.Target.SelectedDefinition.Uri)
            && occurrence.Range == occurrence.Target.SelectedDefinition.Range;

    private static VbaRenameFailure ResolutionChanged(string message)
        => new("resolutionChanged", message);

    private static VbaRenameFailure AnalysisIncomplete(string message)
        => new("analysisIncomplete", message);

    private sealed record VbaSemanticOccurrence(
        string Uri,
        string Name,
        VbaRange Range,
        VbaNameResolutionKind Classification);

    public VbaTextEdit? FormatDocument(
        string uri,
        VbaIndentationStyle indentationStyle,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = definitionCandidates.FindDocument(uri);
        return document is null
            ? null
            : sourceFormatter.FormatDocument(
                document,
                indentationStyle,
                cancellationToken);
    }

    public IReadOnlyList<int> GetSemanticTokenData(
        string uri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hasDocumentIdentity =
            VbaProjectIdentityModel.TryIdentifyDocument(
                uri,
                out var documentIdentity);
        if (hasDocumentIdentity)
        {
            lock (semanticTokenCacheGate)
            {
                if (semanticTokenDataCache.TryGetValue(
                        documentIdentity,
                        out var cachedData))
                {
                    return cachedData;
                }
            }
        }

        var data = FreezeList(
            VbaSemanticTokenBuilder.GetSemanticTokenData(
                GetSemanticTokens(uri, cancellationToken),
                cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        if (!hasDocumentIdentity)
        {
            return data;
        }

        lock (semanticTokenCacheGate)
        {
            if (semanticTokenDataCache.TryGetValue(
                    documentIdentity,
                    out var cachedData))
            {
                return cachedData;
            }

            semanticTokenDataCache[documentIdentity] = data;
            return data;
        }
    }

    internal IReadOnlyList<VbaSemanticToken> GetSemanticTokens(
        string uri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hasDocumentIdentity =
            VbaProjectIdentityModel.TryIdentifyDocument(
                uri,
                out var documentIdentity);
        if (hasDocumentIdentity)
        {
            lock (semanticTokenCacheGate)
            {
                if (semanticTokenCache.TryGetValue(
                        documentIdentity,
                        out var cachedTokens))
                {
                    return cachedTokens;
                }
            }
        }

        var tokens = FreezeList(
            VbaSemanticTokenBuilder.GetSemanticTokens(
                    sourceDocuments,
                    uri,
                    resolvedOccurrences.GetDocumentOccurrences(uri, cancellationToken),
                    cancellationToken)
                .Select(token => token with
                {
                    TokenModifiers = FreezeList(token.TokenModifiers)
                }));
        cancellationToken.ThrowIfCancellationRequested();
        if (!hasDocumentIdentity)
        {
            return tokens;
        }

        lock (semanticTokenCacheGate)
        {
            if (semanticTokenCache.TryGetValue(
                    documentIdentity,
                    out var cachedTokens))
            {
                return cachedTokens;
            }

            semanticTokenCache[documentIdentity] = tokens;
            return tokens;
        }
    }

    private static string GetRangeKey(VbaRange range)
        => $"{range.Start.Line}:{range.Start.Character}:{range.End.Line}:{range.End.Character}";

    private static VbaSourceDocument CaptureDocument(VbaSourceDocument document)
        => new(
            document.Uri,
            document.Text,
            document.ModuleName,
            FreezeList(document.Definitions.Select(CaptureDefinition)),
            document.SyntaxTree);

    internal static VbaSourceDefinition CaptureDefinition(VbaSourceDefinition definition)
        => VbaSemanticFacts.CaptureDefinition(definition);

    private static VbaProjectReferenceSelection? CaptureReferenceSelection(
        VbaProjectReferenceSelection? referenceSelection)
        => referenceSelection is null
            ? null
            : referenceSelection with
            {
                References = FreezeList(referenceSelection.References)
            };

    private static IReadOnlyList<T> FreezeList<T>(IEnumerable<T> values)
        => Array.AsReadOnly(values.ToArray());

    private static bool IsIdentifierName(string value)
        => value.Length is > 0 and <= 255
            && VbaIdentifier.IsIdentifier(value);

    internal static VbaRenameFailure? ValidateRenameName(string value)
        => IsIdentifierName(value)
            ? null
            : new VbaRenameFailure(
                "invalidName",
                "Rename requires a valid VBA identifier of 1 through 255 "
                + "characters without trimming, a typed-name suffix, "
                + "FOREIGN-NAME, or a reserved word.");

    private static VbaRenameFailure? ValidateRenameTargetName(
        VbaSourceDefinition target,
        string value)
    {
        if (IsModuleIdentity(target)
            && value.EnumerateRunes().Take(32).Count() > 31)
        {
            return new VbaRenameFailure(
                "invalidName",
                "Module identity Rename requires a VBA identifier of no more "
                + "than 31 Unicode code points.");
        }

        return target.Kind == VbaSourceDefinitionKind.Event
            && value.Contains('_', StringComparison.Ordinal)
            ? new VbaRenameFailure(
                "invalidName",
                "Event Rename requires a VBA identifier without an "
                + "ASCII underscore.")
            : null;
    }
}
