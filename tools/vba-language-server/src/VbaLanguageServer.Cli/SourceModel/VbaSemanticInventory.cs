using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Syntax;

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
    private readonly VbaProjectValidationDiagnosticIndex projectValidationDiagnostics;
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
    private readonly Dictionary<string, IReadOnlyList<VbaSemanticToken>> semanticTokenCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<int>> semanticTokenDataCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly VbaHostClassProjectionSnapshot? hostClassProjectionSnapshot;

    private VbaSemanticInventory(
        IReadOnlyList<VbaSourceDocument> sourceDocuments,
        VbaNameCandidateInventory definitionCandidates,
        VbaProjectReferenceSelection? referenceSelection,
        VbaProjectReferenceCatalogSet referenceCatalogs,
        VbaHostClassProjectionSnapshot? hostClassProjectionSnapshot,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogSource>
            referenceCatalogSources,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogIdentity>
            referenceCatalogIdentities,
        VbaProjectResolution? projectResolution,
        IReadOnlyDictionary<string, string> authoritativeReferencedProjectNames)
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
        this.hostClassProjectionSnapshot = hostClassProjectionSnapshot;
        semanticResolution = new VbaSemanticResolution(
            definitionCandidates,
            resolutionPolicy,
            hostClassProjectionSnapshot,
            referenceCatalogIdentities);
        resolvedOccurrences = new VbaResolvedIdentifierOccurrenceIndex(
            sourceDocuments,
            semanticResolution.ResolveSourceTarget);
        projectValidationDiagnostics = new VbaProjectValidationDiagnosticIndex(
            sourceDocuments,
            semanticResolution);
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
        VbaHostClassProjectionSnapshot? hostClassProjectionSnapshot = null,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogSource>?
            referenceCatalogSources = null,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogIdentity>?
            referenceCatalogIdentities = null,
        VbaProjectResolution? projectResolution = null,
        IReadOnlyDictionary<string, string>?
            authoritativeReferencedProjectNames = null)
    {
        var documents = FreezeList(
            sourceDocuments.Values.Select(CaptureDocument));
        var capturedReferenceSelection = CaptureReferenceSelection(referenceSelection);
        var catalogs = referenceCatalogs ?? VbaProjectReferenceCatalogSet.Empty;
        var capturedCatalogSources = referenceCatalogSources is null
            ? new Dictionary<string, VbaProjectReferenceCatalogSource>(
                VbaProjectReferenceName.Comparer)
            : new Dictionary<string, VbaProjectReferenceCatalogSource>(
                referenceCatalogSources,
                VbaProjectReferenceName.Comparer);
        var capturedCatalogIdentities = referenceCatalogIdentities is null
            ? new Dictionary<string, VbaProjectReferenceCatalogIdentity>(
                VbaProjectReferenceName.Comparer)
            : new Dictionary<string, VbaProjectReferenceCatalogIdentity>(
                referenceCatalogIdentities,
                VbaProjectReferenceName.Comparer);
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
                .GetActiveDefinitions(capturedReferenceSelection)
                .Select(CaptureDefinition));
        var definitionCandidates = new VbaNameCandidateInventory(
            documents,
            capturedReferenceSelection,
            catalogs,
            activeReferenceDefinitions,
            capturedCatalogSources);
        return new VbaSemanticInventory(
            documents,
            definitionCandidates,
            capturedReferenceSelection,
            catalogs,
            hostClassProjectionSnapshot,
            capturedCatalogSources,
            capturedCatalogIdentities,
            capturedProjectResolution,
            capturedAuthoritativeReferencedProjectNames);
    }

    /// <summary>
    /// Gets the immutable consumer-owned host-class projection for this project snapshot.
    /// </summary>
    public VbaHostClassProjectionSnapshot? HostClassProjectionSnapshot
        => hostClassProjectionSnapshot;

    /// <summary>
    /// Gets definitions declared in a document.
    /// </summary>
    public IReadOnlyList<VbaSourceDefinition> GetDocumentDefinitions(string uri)
        => definitionCandidates.GetDocumentDefinitions(uri);

    internal IReadOnlyList<VbaProjectValidationDiagnostic>
        GetProjectValidationDiagnostics(
            string uri,
            string? sourceTemplateFingerprint = null)
    {
        var diagnostics = projectValidationDiagnostics.GetDiagnostics(uri);
        var moduleIdentityDiagnostics =
            CreateModuleIdentityNameConflictDiagnostics(
                uri,
                sourceTemplateFingerprint);
        return moduleIdentityDiagnostics.Count == 0
            ? diagnostics
            : diagnostics.Concat(moduleIdentityDiagnostics).ToArray();
    }

    private IReadOnlyList<VbaProjectValidationDiagnostic>
        CreateModuleIdentityNameConflictDiagnostics(
            string uri,
            string? sourceTemplateFingerprint)
    {
        if (projectResolution is null)
        {
            return [];
        }

        var diagnostics = new List<VbaProjectValidationDiagnostic>();
        foreach (var target in sourceDocuments
            .Where(document => document.Uri.Equals(
                uri,
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(document => document.Definitions)
            .Where(IsModuleIdentity)
            .Where(IsExplicitModuleIdentityTarget))
        {
            if (GetModuleIdentityMutationAuthorityFailure(
                    target,
                    sourceTemplateFingerprint) is not null)
            {
                continue;
            }

            var conflicts = FindExternalModuleIdentityNameConflicts(
                target.Name);
            if (conflicts.Count == 0)
            {
                continue;
            }

            diagnostics.Add(new VbaProjectValidationDiagnostic(
                "validation.moduleIdentityNameConflict",
                CreateRenameCollisionMessage(
                    target,
                    target.Name,
                    conflicts,
                    locations: ""),
                target.Range,
                Data: new Dictionary<string, object?>
                {
                    ["conflicts"] = conflicts
                        .Select(CreateModuleIdentityConflictData)
                        .ToArray()
                }));
        }

        return diagnostics;
    }

    private IReadOnlyList<VbaRenameConflict>
        FindExternalModuleIdentityNameConflicts(string moduleName)
    {
        var conflicts = new List<VbaRenameConflict>();
        if (projectResolution?.Kind
                == VbaProjectResolutionKind.ManifestDocument
            && hostClassProjectionSnapshot?.VbaProjectName is { } projectName
            && projectName.Equals(
                moduleName,
                StringComparison.OrdinalIgnoreCase))
        {
            conflicts.Add(new VbaRenameConflict(
                "containingProject",
                projectName,
                Uri: null,
                Range: null));
        }

        foreach (var referenceName in GetActiveReferenceNamesInSelectionOrder())
        {
            if (!TryGetCurrentReferencedProjectName(
                    referenceName,
                    out var referencedProjectName)
                || !referencedProjectName.Equals(
                    moduleName,
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

        return conflicts;
    }

    private static IReadOnlyDictionary<string, object?>
        CreateModuleIdentityConflictData(VbaRenameConflict conflict)
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
                        definition.Uri.Equals(
                            occurrence.Uri,
                            StringComparison.OrdinalIgnoreCase)
                        && definition.Range != occurrence.Range
                        && Contains(definition.Range, occurrence.Range.Start)
                        && Contains(definition.Range, occurrence.Range.End))))
            .Select(occurrence => new VbaDefinitionLocation(occurrence.Uri, occurrence.Range))
            .Concat(declarationDefinitions
                .Where(definition => definition.Identity.Origin
                    == VbaDefinitionOrigin.Source)
                .Select(definition => definition.Location))
            .GroupBy(reference => $"{reference.Uri}:{GetRangeKey(reference.Range)}", StringComparer.OrdinalIgnoreCase)
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
                definition.Uri.Equals(uri, StringComparison.OrdinalIgnoreCase)
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
            .Select(semanticResolution.ProjectSourceInterfaceDocumentation)
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
            && GetIntrinsicHostHandlerAuthority(document, resolvedTarget) is not null)
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

    private sealed record VbaInterfaceAssociationProofKey(
        string Relationship,
        string InterfaceTarget,
        string ContractOrigin,
        string MemberTarget,
        VbaInterfaceAccessorContractKind ContractKind,
        bool IsDerivedVariableAccessor,
        VbaInterfaceContractCompatibilityState CompatibilityState,
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

    private VbaHostClassEventAuthority? GetIntrinsicHostHandlerAuthority(
        VbaSourceDocument document,
        VbaResolvedNameTarget target)
        => GetIntrinsicHostHandlerAnalyses(document, target)
            .Select(analysis => analysis.Surface.Authority)
            .OrderBy(authority => authority == VbaHostClassEventAuthority.Current
                ? 0
                : 1)
            .Cast<VbaHostClassEventAuthority?>()
            .FirstOrDefault();

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
            .Where(candidate => candidate.Uri.Equals(
                document.Uri,
                StringComparison.OrdinalIgnoreCase))
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
        string? sourceTemplateFingerprint = null)
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
                sourceTemplateFingerprint) is null
            && !target.Name.Equals(newName, StringComparison.Ordinal)
            && CreateModuleIdentityFileRenames(target, newName).Count > 0;
    }

    internal VbaRenameResult CreateRenameResult(
        string uri,
        int line,
        int character,
        string newName,
        CancellationToken cancellationToken = default,
        string? sourceTemplateFingerprint = null)
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

                return selectedAssociation.Surface.Authority
                    == VbaHostClassEventAuthority.Current
                    ? new VbaRenameResult(
                        Plan: null,
                        new VbaRenameFailure(
                            "notRenameTarget",
                            "A current intrinsic host Event handler name is a fixed host contract."))
                    : new VbaRenameResult(
                        Plan: null,
                        AnalysisIncomplete(
                            "Rename requires current host Event evidence for an intrinsic handler."));
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
                sourceTemplateFingerprint)
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

        var collisions = FindSameScopeCollisions(target, newName)
            .Concat(FindInterfaceDependentRenameCollisions(target, newName))
            .Distinct()
            .ToArray();
        if (collisions.Length > 0)
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
            && HasProjectedHostAlternative(targetOccurrences, cancellationToken))
        {
            return new VbaRenameResult(
                Plan: null,
                AnalysisIncomplete(
                    "Rename cannot prove complete dependent-handler coverage across source and projected host Event alternatives."));
        }

        var plannedEdits = targetOccurrences
            .Select(occurrence => new KeyValuePair<string, VbaTextEdit>(
                occurrence.Uri,
                new VbaTextEdit(occurrence.Range, newName)))
            .Concat(CreateInterfaceDependentRenameEdits(
                target,
                newName,
                cancellationToken));
        var changeSetFailure = TryCreateRenameChangeSet(
            plannedEdits,
            out var changes);
        if (changeSetFailure is not null)
        {
            return new VbaRenameResult(Plan: null, changeSetFailure);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var proofFailure = ProveBindingsArePreserved(
            target,
            targetOccurrences,
            changes,
            newName,
            cancellationToken,
            out var targetCorrespondence);
        if (proofFailure is not null)
        {
            return new VbaRenameResult(Plan: null, proofFailure);
        }

        return new VbaRenameResult(
            changes.Count == 0
                ? null
                : new VbaRenamePlan(target.Range, changes)
                {
                    FileRenames = CreateModuleIdentityFileRenames(
                        target,
                        newName),
                    TargetCorrespondence = targetCorrespondence
                },
            Failure: null);
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
                    definition.Uri.Equals(
                        occurrence.Uri,
                        StringComparison.OrdinalIgnoreCase)
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
    {
        if (!IsModuleIdentity(target))
        {
            return false;
        }

        var document = definitionCandidates.FindDocument(target.Uri);
        if (document is null)
        {
            return false;
        }

        var syntaxTree = document.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(document.Uri, document.Text);
        var metadata = syntaxTree.Module.Identity.Metadata
            ?? ReadModuleIdentityMetadata(document, syntaxTree);
        return metadata.IsAuthoritative
            && metadata.Name!.Equals(target.Name, StringComparison.Ordinal)
            && target.Range.Start.Line == syntaxTree.Module.Identity.Range.Start.Line
            && target.Range.Start.Character == syntaxTree.Module.Identity.Range.Start.Character
            && target.Range.End.Line == syntaxTree.Module.Identity.Range.End.Line
            && target.Range.End.Character == syntaxTree.Module.Identity.Range.End.Character;
    }

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
        if (document?.Provenance
            == VbaSourceDocumentProvenance.IntrinsicDocument)
        {
            return new VbaRenameFailure(
                "hostManagedModuleIdentity",
                $"Module identity '{target.Name}' belongs to a source-template intrinsic document component.",
                Path: sourcePath,
                Guidance: "Use the workbook-backed source template refactoring workflow so the intrinsic document component and projected source stay associated.");
        }

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

        if (document is null)
        {
            return null;
        }

        var syntaxTree = document.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(document.Uri, document.Text);
        VbaHostClassKind? expectedHostKind = syntaxTree.Module.Kind switch
        {
            VbaModuleKind.FormModule => VbaHostClassKind.Form,
            _ => null
        };
        if (expectedHostKind is null)
        {
            return null;
        }

        var matchingEntries = hostClassProjectionSnapshot?.Classes
            .Where(entry => entry.Identity.Name.Equals(
                target.Name,
                StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? [];
        var exactEntry = matchingEntries.FirstOrDefault(entry =>
            entry.Identity.Kind == expectedHostKind);
        if (exactEntry is VbaCurrentHostClassProjectionEntry
            || expectedHostKind == VbaHostClassKind.Document
                && exactEntry is not null)
        {
            return new VbaRenameFailure(
                "hostManagedModuleIdentity",
                $"Module identity '{target.Name}' belongs to a source-template host component.",
                Path: sourcePath,
                Guidance: "Use the workbook-backed source template refactoring workflow so the host component and exported source stay associated.");
        }

        if (exactEntry is VbaLastKnownGoodHostClassProjectionEntry
            or VbaIndeterminateHostClassProjectionEntry
            || matchingEntries.Any()
            || hostClassProjectionSnapshot is null
            || !hostClassProjectionSnapshot.ClassEnumerationComplete)
        {
            return new VbaRenameFailure(
                "analysisIncomplete",
                $"Module identity '{target.Name}' does not have conclusive current host-ownership evidence.",
                Condition: "hostOwnershipUnavailable",
                Path: sourcePath,
                Guidance: "Refresh the source-template host-class projection and retry Rename.");
        }

        return null;
    }

    private VbaRenameFailure? GetModuleIdentityMutationAuthorityFailure(
        VbaSourceDefinition target,
        string? sourceTemplateFingerprint)
    {
        if (!IsModuleIdentity(target) || projectResolution is null)
        {
            return null;
        }

        if (projectResolution.Kind == VbaProjectResolutionKind.ManifestDocument
            && (string.IsNullOrWhiteSpace(
                    hostClassProjectionSnapshot?.VbaProjectName)
                || string.IsNullOrWhiteSpace(
                    hostClassProjectionSnapshot.SourceTemplateFingerprint)
                || string.IsNullOrWhiteSpace(sourceTemplateFingerprint)
                || !hostClassProjectionSnapshot.SourceTemplateFingerprint.Equals(
                    sourceTemplateFingerprint,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return new VbaRenameFailure(
                "analysisIncomplete",
                "Module identity Rename requires the containing source template's current actual VBProject.Name authority.",
                Condition: "containingProjectNameUnavailable",
                Path: projectResolution.SourceTemplatePath,
                Guidance: "Refresh the source-template host-class projection and retry Rename.");
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

    private bool HasProjectedHostAlternative(
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
        string newName)
    {
        var candidates = sourceDocuments
            .SelectMany(document => document.Definitions)
            .Where(candidate => string.Equals(
                candidate.Name,
                newName,
                StringComparison.OrdinalIgnoreCase))
            .Where(candidate => IsSameDeclarationScope(target, candidate))
            .Where(candidate => !AreMembersOfSameRenameTarget(target, candidate))
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
            && hostClassProjectionSnapshot?.VbaProjectName is { } projectName
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
            .Where(candidate => string.Equals(
                candidate.Uri,
                target.Uri,
                StringComparison.OrdinalIgnoreCase))
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
    {
        if (IsProjectNamespaceCollision(target, candidate))
        {
            return true;
        }

        if (definitionCandidates.ConditionalFamilies.HaveSameLogicalMemberScope(
            target,
            candidate))
        {
            return true;
        }

        if (!string.Equals(
            target.Uri,
            candidate.Uri,
            StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (target.Visibility == VbaSourceDefinitionVisibility.Local
            || candidate.Visibility == VbaSourceDefinitionVisibility.Local)
        {
            return IsProcedureLocalCollision(target, candidate);
        }

        if (target.Kind == VbaSourceDefinitionKind.TypeMember
            || candidate.Kind == VbaSourceDefinitionKind.TypeMember)
        {
            return target.Kind == VbaSourceDefinitionKind.TypeMember
                && candidate.Kind == VbaSourceDefinitionKind.TypeMember
                && string.Equals(
                    target.ParentTypeName,
                    candidate.ParentTypeName,
                    StringComparison.OrdinalIgnoreCase);
        }

        if (target.Kind == VbaSourceDefinitionKind.Event
            || candidate.Kind == VbaSourceDefinitionKind.Event)
        {
            return target.Kind == VbaSourceDefinitionKind.Event
                && candidate.Kind == VbaSourceDefinitionKind.Event;
        }

        if (IsModuleValueDeclaration(target)
            && IsModuleValueDeclaration(candidate))
        {
            return true;
        }

        return IsDeclaredType(target) && IsDeclaredType(candidate);
    }

    private bool IsProcedureLocalCollision(
        VbaSourceDefinition target,
        VbaSourceDefinition candidate)
    {
        if (target.Visibility == VbaSourceDefinitionVisibility.Local
            && candidate.Visibility == VbaSourceDefinitionVisibility.Local)
        {
            return target.ParentProcedureRange
                == candidate.ParentProcedureRange;
        }

        var local = target.Visibility == VbaSourceDefinitionVisibility.Local
            ? target
            : candidate;
        var moduleDeclaration = ReferenceEquals(local, target)
            ? candidate
            : target;
        var possibleResultDeclarations =
            moduleDeclaration.Kind == VbaSourceDefinitionKind.Property
                ? sourceDocuments
                    .SelectMany(document => document.Definitions)
                    .Where(definition => AreMembersOfSameRenameTarget(
                        moduleDeclaration,
                        definition))
                : [moduleDeclaration];
        return possibleResultDeclarations.Any(declaration =>
            IsResultBindingDeclaration(declaration, local)
            && IsPhysicalContainingProcedure(declaration, local));
    }

    private static bool IsResultBindingDeclaration(
        VbaSourceDefinition declaration,
        VbaSourceDefinition local)
        => local.Kind == VbaSourceDefinitionKind.Parameter
            ? declaration.Kind == VbaSourceDefinitionKind.Procedure
                && declaration.Signature?.CallableKind
                    == VbaCallableKind.Function
            : declaration.Kind == VbaSourceDefinitionKind.Procedure
                    && declaration.Signature?.CallableKind
                        == VbaCallableKind.Function
                || declaration.Kind == VbaSourceDefinitionKind.Property
                    && declaration.PropertyAccessorKind
                        == VbaPropertyAccessorKind.Get;

    private static bool IsPhysicalContainingProcedure(
        VbaSourceDefinition declaration,
        VbaSourceDefinition local)
        => local.ParentProcedureRange is { } parentRange
            && Contains(parentRange, declaration.Range.Start);

    private static bool Contains(VbaRange range, VbaPosition position)
        => IsAtOrAfter(position, range.Start)
            && IsAtOrAfter(range.End, position);

    private static bool IsAtOrAfter(VbaPosition left, VbaPosition right)
        => left.Line > right.Line
            || left.Line == right.Line
                && left.Character >= right.Character;

    private static bool IsProjectNamespaceCollision(
        VbaSourceDefinition target,
        VbaSourceDefinition candidate)
    {
        var targetIsModule = IsModuleIdentity(target);
        var candidateIsModule = IsModuleIdentity(candidate);
        var targetIsProjectVisibleType = IsProjectVisibleType(target);
        var candidateIsProjectVisibleType = IsProjectVisibleType(candidate);
        return targetIsModule && (candidateIsModule || candidateIsProjectVisibleType)
            || candidateIsModule && targetIsProjectVisibleType
            || targetIsProjectVisibleType && candidateIsProjectVisibleType;
    }

    private static bool IsModuleIdentity(VbaSourceDefinition definition)
        => definition.Kind is VbaSourceDefinitionKind.Module
            or VbaSourceDefinitionKind.Class
            or VbaSourceDefinitionKind.Form;

    private static bool IsProjectVisibleType(VbaSourceDefinition definition)
        => definition.Visibility.IsProjectVisible()
            && IsDeclaredType(definition);

    private static bool IsDeclaredType(VbaSourceDefinition definition)
        => definition.Kind is VbaSourceDefinitionKind.Enum
            or VbaSourceDefinitionKind.Type;

    private static bool IsModuleValueDeclaration(
        VbaSourceDefinition definition)
        => definition.Kind is VbaSourceDefinitionKind.Procedure
            or VbaSourceDefinitionKind.Property
            or VbaSourceDefinitionKind.Constant
            or VbaSourceDefinitionKind.Variable
            or VbaSourceDefinitionKind.EnumMember;

    private static bool IsContainingProcedure(
        VbaSourceDefinition procedure,
        VbaSourceDefinition local)
        => procedure.Kind is VbaSourceDefinitionKind.Procedure
                or VbaSourceDefinitionKind.Property
            && local.ParentProcedureName is not null
            && string.Equals(
                procedure.Name,
                local.ParentProcedureName,
                StringComparison.OrdinalIgnoreCase);

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
        out VbaRenameTargetCorrespondence? targetCorrespondence)
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
                changes);
        if (interfaceAssociationFailure is not null)
        {
            return interfaceAssociationFailure;
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
            out targetCorrespondence);
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
                return ResolutionChanged(
                    "Rename would change the unresolved or ambiguous "
                    + "classification of a non-target occurrence.");
            }
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

    private VbaRenameFailure? ProveSourceInterfaceAssociationsArePreserved(
        VbaSemanticInventory hypothetical,
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> changes)
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
        var beforeCounts = beforeAnalyses
            .SelectMany(item => item.Analysis.Associations)
            .Select(association => CreateInterfaceAssociationProofKey(
                association,
                changes))
            .GroupBy(key => key)
            .ToDictionary(group => group.Key, group => group.Count());
        var afterCounts = afterAnalyses
            .SelectMany(item => item.Analysis.Associations)
            .Select(association => hypothetical
                .CreateInterfaceAssociationProofKey(
                    association,
                    changes: null))
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
            || occurrence.Target.IsConditionalFamily
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
        out VbaRenameTargetCorrespondence? correspondence)
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

            if (!physicalBefore.Uri.Equals(
                    physicalAfter.Uri,
                    StringComparison.OrdinalIgnoreCase)
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
            return ResolutionChanged(
                "Rename would change the target's physical declaration set "
                + "or logical-family meaning.");
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
            hostClassProjectionSnapshot,
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
        => $"{uri}\u001f{GetRangeKey(range)}";

    private static bool IsDeclarationOccurrence(
        VbaResolvedIdentifierOccurrence occurrence)
        => string.Equals(
                occurrence.Uri,
                occurrence.Target.SelectedDefinition.Uri,
                StringComparison.OrdinalIgnoreCase)
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
        lock (semanticTokenCacheGate)
        {
            if (semanticTokenDataCache.TryGetValue(uri, out var cachedData))
            {
                return cachedData;
            }
        }

        var data = FreezeList(
            VbaSemanticTokenBuilder.GetSemanticTokenData(
                GetSemanticTokens(uri, cancellationToken),
                cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        lock (semanticTokenCacheGate)
        {
            if (semanticTokenDataCache.TryGetValue(uri, out var cachedData))
            {
                return cachedData;
            }

            semanticTokenDataCache[uri] = data;
            return data;
        }
    }

    internal IReadOnlyList<VbaSemanticToken> GetSemanticTokens(
        string uri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (semanticTokenCacheGate)
        {
            if (semanticTokenCache.TryGetValue(uri, out var cachedTokens))
            {
                return cachedTokens;
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
        lock (semanticTokenCacheGate)
        {
            if (semanticTokenCache.TryGetValue(uri, out var cachedTokens))
            {
                return cachedTokens;
            }

            semanticTokenCache[uri] = tokens;
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
            document.SyntaxTree)
        {
            Provenance = document.Provenance
        };

    internal static VbaSourceDefinition CaptureDefinition(VbaSourceDefinition definition)
        => definition.Signature is null
            ? definition
            : definition with
            {
                Signature = definition.Signature with
                {
                    Parameters = FreezeList(definition.Signature.Parameters)
                }
            };

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
