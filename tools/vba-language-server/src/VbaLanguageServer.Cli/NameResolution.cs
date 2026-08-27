using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Resolves VBA names by applying local, current-module, project, and reference precedence rules.
/// </summary>
public sealed class VbaNameResolutionService
{
    private readonly VbaNameCandidateInventory candidates;
    private readonly VbaResolutionPolicy resolutionPolicy;

    /// <summary>
    /// Creates a name resolution service over indexed source documents and active references.
    /// </summary>
    /// <param name="documents">The indexed source documents.</param>
    /// <param name="referenceSelection">The active reference selection for the project.</param>
    /// <param name="referenceCatalogs">The available reference catalogs.</param>
    /// <param name="activeReferenceDefinitions">The active reference definitions projected for this index.</param>
    public VbaNameResolutionService(
        IReadOnlyList<VbaSourceDocument> documents,
        VbaProjectReferenceSelection? referenceSelection,
        VbaProjectReferenceCatalogSet referenceCatalogs,
        IReadOnlyList<VbaSourceDefinition>? activeReferenceDefinitions = null)
    {
        candidates = new VbaNameCandidateInventory(
            documents,
            referenceSelection,
            referenceCatalogs,
            activeReferenceDefinitions
                ?? referenceCatalogs.GetActiveDefinitions(referenceSelection));
        resolutionPolicy = new VbaResolutionPolicy(
            candidates.ConditionalFamilies);
    }

    internal VbaNameResolutionService(
        IReadOnlyList<VbaSourceDocument> documents,
        VbaProjectReferenceSelection? referenceSelection,
        VbaProjectReferenceCatalogSet referenceCatalogs,
        IReadOnlyList<VbaSourceDefinition>? activeReferenceDefinitions,
        VbaResolutionPolicy resolutionPolicy)
        : this(
            new VbaNameCandidateInventory(
                documents,
                referenceSelection,
                referenceCatalogs,
                activeReferenceDefinitions
                    ?? referenceCatalogs.GetActiveDefinitions(referenceSelection)),
            resolutionPolicy)
    {
    }

    internal VbaNameResolutionService(
        VbaNameCandidateInventory candidates,
        VbaResolutionPolicy resolutionPolicy)
    {
        this.resolutionPolicy = resolutionPolicy;
        this.candidates = candidates;
    }

    /// <summary>
    /// Gets unqualified completion definitions visible at a position.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="position">The source position.</param>
    /// <returns>The visible and unambiguous completion definitions.</returns>
    public IReadOnlyList<VbaSourceDefinition> GetCompletionDefinitions(string uri, VbaPosition position)
        => GetCompletionDefinitions(uri, position, definitionFilter: null);

    internal IReadOnlyList<VbaSourceDefinition> GetCompletionDefinitions(
        string uri,
        VbaPosition position,
        Func<VbaSourceDefinition, bool>? definitionFilter)
        => GetRankedCompletionDefinitions(uri, position, definitionFilter)
            .Select(candidate => candidate.Definition)
            .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal IReadOnlyList<VbaRankedDefinition> GetRankedCompletionDefinitions(
        string uri,
        VbaPosition position,
        Func<VbaSourceDefinition, bool>? definitionFilter,
        Func<VbaSourceDefinition, bool>? candidateDomainFilter = null)
    {
        var currentDocument = candidates.FindDocument(uri);
        if (currentDocument is null)
        {
            return [];
        }

        return ResolveRankedCompletionCandidates(
                GetUnqualifiedCandidates(currentDocument, position, includeLocals: true)
                    .Where(candidate => candidate.Definition.IsAuthoringAvailable),
                definitionFilter,
                candidateDomainFilter: candidateDomainFilter);
    }

    /// <summary>
    /// Gets active reference qualifier aliases that are visible at a position.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="position">The source position.</param>
    /// <returns>The visible qualifier aliases.</returns>
    public IReadOnlyList<string> GetCompletionReferenceQualifiers(string uri, VbaPosition position)
    {
        var currentDocument = candidates.FindDocument(uri);
        if (currentDocument is null)
        {
            return [];
        }

        return candidates.GetReferenceQualifiers()
            .Where(qualifier => !HasSourceQualifierShadow(currentDocument, position, qualifier))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(qualifier => qualifier, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Gets source module qualifier names that are visible at a position.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="position">The source position.</param>
    /// <returns>The visible source module qualifier names.</returns>
    public IReadOnlyList<string> GetCompletionSourceQualifiers(string uri, VbaPosition position)
    {
        var currentDocument = candidates.FindDocument(uri);
        if (currentDocument is null)
        {
            return [];
        }

        return candidates.GetSourceModuleNames()
            .Where(moduleName => !HasLocalQualifierShadow(currentDocument, position, moduleName))
            .Where(moduleName => GetSourceQualifiedCompletionDefinitions(currentDocument, position, moduleName).Count > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(moduleName => moduleName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Gets reference definitions exposed through a visible qualifier alias.
    /// </summary>
    /// <param name="currentDocument">The current document.</param>
    /// <param name="position">The source position.</param>
    /// <param name="qualifier">The qualifier alias.</param>
    /// <returns>The qualified reference definitions, or an empty list when a source definition shadows the alias.</returns>
    public IReadOnlyList<VbaSourceDefinition> GetQualifiedCompletionDefinitions(
        VbaSourceDocument currentDocument,
        VbaPosition position,
        string qualifier)
        => HasSourceQualifierShadow(currentDocument, position, qualifier)
            ? []
            : candidates.GetQualifiedReferenceDefinitions(qualifier)
                .Where(IsQualifiedReferenceRootDefinition)
                .ToArray();

    /// <summary>
    /// Gets definitions exposed through a source module qualifier.
    /// </summary>
    /// <param name="currentDocument">The current document.</param>
    /// <param name="position">The source position.</param>
    /// <param name="qualifier">The source module qualifier.</param>
    /// <returns>The qualified source definitions, or an empty list when a local definition shadows the qualifier.</returns>
    public IReadOnlyList<VbaSourceDefinition> GetSourceQualifiedCompletionDefinitions(
        VbaSourceDocument currentDocument,
        VbaPosition position,
        string qualifier)
        => HasLocalQualifierShadow(currentDocument, position, qualifier)
            ? []
            : GetVisibleSourceModuleDefinitions(currentDocument, qualifier);

    internal IReadOnlyList<VbaSourceDefinition>
        GetResolvedSourceQualifiedCompletionDefinitions(
            VbaSourceDocument currentDocument,
            VbaPosition position,
            string qualifier,
            Func<VbaSourceDefinition, bool> candidateDomainFilter,
            Func<VbaSourceDefinition, bool> definitionFilter)
        => HasLocalQualifierShadow(currentDocument, position, qualifier)
            ? []
            : ResolveRankedCompletionCandidates(
                    GetVisibleSourceModuleDefinitions(
                            currentDocument,
                            qualifier)
                        .Select(definition => new VbaRankedDefinition(
                            definition,
                            VbaResolutionPolicy.CurrentModuleRank)),
                    definitionFilter,
                    preferEligibleRepresentative: true,
                    candidateDomainFilter: candidateDomainFilter)
                .Select(candidate => candidate.Definition)
                .ToArray();

    /// <summary>
    /// Gets project-level definitions that can participate in document formatting.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <returns>The definitions that can supply canonical casing outside local scope.</returns>
    public IReadOnlyList<VbaSourceDefinition> GetFormattingDefinitions(string uri)
    {
        var currentDocument = candidates.FindDocument(uri);
        if (currentDocument is null)
        {
            return [];
        }

        return ResolveCompletionCandidates(GetUnqualifiedCandidates(currentDocument, new VbaPosition(0, 0), includeLocals: false))
            .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Resolves an identifier with an optional qualifier at a source position.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="position">The source position used for local visibility.</param>
    /// <param name="qualifier">The optional qualifier preceding the identifier.</param>
    /// <param name="identifier">The identifier to resolve.</param>
    /// <returns>The resolved definition, or null when unresolved or ambiguous.</returns>
    public VbaSourceDefinition? Resolve(
        string uri,
        VbaPosition position,
        string? qualifier,
        string identifier)
        => ResolvePreferredCore(
            uri,
            position,
            qualifier,
            identifier,
            definition => !resolutionPolicy.IsTypeDefinition(definition),
            fallbackToUnfiltered: true);

    internal VbaSourceDefinition? ResolveValue(
        string uri,
        VbaPosition position,
        string? qualifier,
        string identifier)
        => ResolvePreferredCore(
            uri,
            position,
            qualifier,
            identifier,
            definition => !resolutionPolicy.IsTypeDefinition(definition),
            fallbackToUnfiltered: false);

    internal VbaNameResolutionOutcome ResolveValueOutcome(
        string uri,
        VbaPosition position,
        string? qualifier,
        string identifier)
        => ResolvePreferredOutcomeCore(
            uri,
            position,
            qualifier,
            identifier,
            definition => !resolutionPolicy.IsTypeDefinition(definition),
            fallbackToUnfiltered: false);

    internal VbaNameResolutionOutcome ResolveCurrentDocumentEventOutcome(
        string uri,
        string identifier)
    {
        var currentDocument = candidates.FindDocument(uri);
        if (currentDocument is null)
        {
            return VbaNameResolutionOutcome.AnalysisIncomplete;
        }

        return resolutionPolicy.ResolveRankedCandidatesOutcome(
            currentDocument.Definitions
                .Where(definition => definition.Kind == VbaSourceDefinitionKind.Event
                    && definition.Name.Equals(
                        identifier,
                        StringComparison.OrdinalIgnoreCase))
                .Select(definition => new VbaRankedDefinition(
                    definition,
                    VbaResolutionPolicy.CurrentModuleRank)),
            candidates.ReferenceSelection);
    }

    internal VbaNameResolutionOutcome ResolveCurrentDocumentModuleVariableOutcome(
        VbaSourceDocument currentDocument,
        string identifier)
        => resolutionPolicy.ResolveRankedCandidatesOutcome(
            currentDocument.Definitions
                .Where(definition =>
                    definition.Kind == VbaSourceDefinitionKind.Variable
                    && definition.ParentProcedureName is null
                    && definition.Name.Equals(
                        identifier,
                        StringComparison.OrdinalIgnoreCase))
                .Select(definition => new VbaRankedDefinition(
                    definition,
                    VbaResolutionPolicy.CurrentModuleRank)),
            candidates.ReferenceSelection);

    internal VbaSourceDefinition? ResolvePreferred(
        string uri,
        VbaPosition position,
        string? qualifier,
        string identifier,
        Func<VbaSourceDefinition, bool> preferredDefinition)
        => ResolvePreferredCore(
            uri,
            position,
            qualifier,
            identifier,
            preferredDefinition,
            fallbackToUnfiltered: true);

    internal VbaNameResolutionOutcome ResolvePreferredOutcome(
        string uri,
        VbaPosition position,
        string? qualifier,
        string identifier,
        Func<VbaSourceDefinition, bool> preferredDefinition)
        => ResolvePreferredOutcomeCore(
            uri,
            position,
            qualifier,
            identifier,
            preferredDefinition,
            fallbackToUnfiltered: true);

    private VbaSourceDefinition? ResolvePreferredCore(
        string uri,
        VbaPosition position,
        string? qualifier,
        string identifier,
        Func<VbaSourceDefinition, bool> preferredDefinition,
        bool fallbackToUnfiltered)
        => ResolvePreferredOutcomeCore(
            uri,
            position,
            qualifier,
            identifier,
            preferredDefinition,
            fallbackToUnfiltered).Target?.SelectedDefinition;

    private VbaNameResolutionOutcome ResolvePreferredOutcomeCore(
        string uri,
        VbaPosition position,
        string? qualifier,
        string identifier,
        Func<VbaSourceDefinition, bool> preferredDefinition,
        bool fallbackToUnfiltered)
    {
        var currentDocument = candidates.FindDocument(uri);
        if (currentDocument is null)
        {
            return VbaNameResolutionOutcome.AnalysisIncomplete;
        }

        var rankedCandidates = qualifier is null
            ? GetUnqualifiedCandidates(currentDocument, position, includeLocals: true, identifier)
            : GetQualifiedCandidates(currentDocument, qualifier)
                .Where(candidate => SameName(candidate.Definition.Name, identifier));
        return ResolveBestRankCandidatesOutcome(
            rankedCandidates,
            preferredDefinition,
            fallbackToUnfiltered);
    }

    private IEnumerable<VbaRankedDefinition> GetUnqualifiedCandidates(
        VbaSourceDocument currentDocument,
        VbaPosition position,
        bool includeLocals,
        string? requestedName = null)
    {
        if (includeLocals)
        {
            foreach (var candidate in candidates.GetSourceCandidates(currentDocument)
                .Where(candidate => candidate.Visibility == VbaSourceDefinitionVisibility.Local)
                .Where(candidate => ContainsPosition(candidate.Definition, position))
                .Where(candidate => MatchesRequestedName(candidate, requestedName)))
            {
                yield return new VbaRankedDefinition(candidate.Definition, VbaResolutionPolicy.LocalRank);
            }
        }

        foreach (var candidate in candidates.GetSourceCandidates(currentDocument)
            .Where(candidate => resolutionPolicy.IsReferenceTarget(candidate.Definition))
            .Where(candidate => candidate.Definition.Kind
                != VbaSourceDefinitionKind.TypeMember)
            .Where(candidate => MatchesRequestedName(candidate, requestedName)))
        {
            yield return new VbaRankedDefinition(candidate.Definition, VbaResolutionPolicy.CurrentModuleRank);
        }

        foreach (var candidate in candidates.GetSourceCandidates(requestedName)
            .Where(candidate => !SameUri(candidate.Uri, currentDocument.Uri))
            .Where(candidate => resolutionPolicy.IsReferenceTarget(candidate.Definition))
            .Where(candidate => candidate.Definition.Kind
                != VbaSourceDefinitionKind.TypeMember)
            .Where(candidate => candidate.Visibility.IsProjectVisible()))
        {
            yield return new VbaRankedDefinition(candidate.Definition, VbaResolutionPolicy.ProjectRank);
        }

        if (candidates.HasReferenceSelection)
        {
            foreach (var candidate in candidates.GetReferenceCandidates(requestedName)
                .Where(candidate => IsUnqualifiedReferenceRootDefinition(candidate.Definition)))
            {
                yield return new VbaRankedDefinition(candidate.Definition, VbaResolutionPolicy.ReferenceRank);
            }
        }
    }

    private IEnumerable<VbaRankedDefinition> GetQualifiedCandidates(VbaSourceDocument currentDocument, string qualifier)
    {
        foreach (var candidate in candidates.GetSourceCandidatesByModule(qualifier))
        {
            var allowPrivate = SameUri(currentDocument.Uri, candidate.Uri);
            if (resolutionPolicy.IsReferenceTarget(candidate.Definition)
                && candidate.Definition.Kind != VbaSourceDefinitionKind.TypeMember
                && (allowPrivate || candidate.Visibility.IsProjectVisible()))
            {
                yield return new VbaRankedDefinition(candidate.Definition, VbaResolutionPolicy.CurrentModuleRank);
            }
        }

        if (candidates.HasReferenceSelection)
        {
            foreach (var definition in candidates.GetQualifiedReferenceDefinitions(qualifier)
                .Where(IsQualifiedReferenceRootDefinition))
            {
                yield return new VbaRankedDefinition(definition, VbaResolutionPolicy.ReferenceRank);
            }
        }
    }

    private bool HasSourceQualifierShadow(
        VbaSourceDocument currentDocument,
        VbaPosition position,
        string qualifier)
    {
        if (candidates.HasSourceModule(qualifier))
        {
            return true;
        }

        if (candidates.GetSourceCandidates(currentDocument)
            .Where(candidate => candidate.Visibility == VbaSourceDefinitionVisibility.Local)
            .Where(candidate => ContainsPosition(candidate.Definition, position))
            .Any(candidate => SameName(candidate.Name, qualifier)))
        {
            return true;
        }

        if (candidates.GetSourceCandidates(currentDocument)
            .Where(candidate => resolutionPolicy.IsReferenceTarget(candidate.Definition))
            .Any(candidate => SameName(candidate.Name, qualifier)))
        {
            return true;
        }

        return candidates.GetSourceCandidates(qualifier)
            .Where(candidate => !SameUri(candidate.Uri, currentDocument.Uri))
            .Where(candidate => resolutionPolicy.IsReferenceTarget(candidate.Definition))
            .Any(candidate => candidate.Visibility.IsProjectVisible());
    }

    private bool HasLocalQualifierShadow(
        VbaSourceDocument currentDocument,
        VbaPosition position,
        string qualifier)
        => candidates.GetSourceCandidates(currentDocument)
            .Where(candidate => candidate.Visibility == VbaSourceDefinitionVisibility.Local)
            .Where(candidate => ContainsPosition(candidate.Definition, position))
            .Any(candidate => SameName(candidate.Name, qualifier));

    private IReadOnlyList<VbaSourceDefinition> GetVisibleSourceModuleDefinitions(
        VbaSourceDocument currentDocument,
        string qualifier)
    {
        var definitions = candidates.GetSourceCandidatesByModule(qualifier)
            .Where(candidate => resolutionPolicy.IsReferenceTarget(candidate.Definition))
            .Where(candidate => SameUri(currentDocument.Uri, candidate.Uri)
                || candidate.Visibility.IsProjectVisible())
            .Select(candidate => candidate.Definition)
            .ToArray();
        return definitions;
    }

    private IReadOnlyList<VbaSourceDefinition> ResolveCompletionCandidates(
        IEnumerable<VbaRankedDefinition> candidates,
        Func<VbaSourceDefinition, bool>? definitionFilter = null)
        => ResolveRankedCompletionCandidates(candidates, definitionFilter)
            .Select(candidate => candidate.Definition)
            .ToArray();

    private IReadOnlyList<VbaRankedDefinition> ResolveRankedCompletionCandidates(
        IEnumerable<VbaRankedDefinition> candidates,
        Func<VbaSourceDefinition, bool>? definitionFilter = null,
        bool preferEligibleRepresentative = false,
        Func<VbaSourceDefinition, bool>? candidateDomainFilter = null)
    {
        return candidates
            .GroupBy(candidate => candidate.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var domainCandidates = candidateDomainFilter is null
                    ? group.ToArray()
                    : group.Where(candidate => candidateDomainFilter(
                            candidate.Definition))
                        .ToArray();
                if (domainCandidates.Length == 0)
                {
                    return null;
                }

                var bestRank = domainCandidates.Min(candidate => candidate.Rank);
                var outcome = resolutionPolicy.ResolveRankedCandidatesOutcome(
                    domainCandidates,
                    this.candidates.ReferenceSelection);
                if (outcome.Target is null)
                {
                    return null;
                }

                VbaSourceDefinition? definition;
                if (definitionFilter is null)
                {
                    definition = outcome.Target.SelectedDefinition;
                }
                else
                {
                    var eligibleOutcome = resolutionPolicy
                            .ResolveRankedCandidatesOutcome(
                            domainCandidates
                                .Where(candidate =>
                                    definitionFilter(candidate.Definition))
                                .Where(candidate => resolutionPolicy
                                    .CreateNameTarget(candidate.Definition)
                                    .Identity == outcome.Target.Identity),
                            this.candidates.ReferenceSelection);
                    var preferEligiblePropertyAccessor =
                        outcome.Target is VbaPropertyNameTarget propertyTarget
                        && propertyTarget.AccessorTargets.Any(
                            target => target.IsConditionalFamily);
                    definition = eligibleOutcome.Target is null
                        ? null
                        : preferEligibleRepresentative
                            || preferEligiblePropertyAccessor
                            || outcome.Target.IsConditionalFamily
                            || eligibleOutcome.Target.IsConditionalFamily
                                ? eligibleOutcome.Target.SelectedDefinition
                                : outcome.Target.SelectedDefinition;
                }
                return definition is null
                    ? null
                    : new VbaRankedDefinition(definition, bestRank);
            })
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .ToArray();
    }

    private VbaSourceDefinition? ResolveRankedCandidates(IEnumerable<VbaRankedDefinition> candidates)
        => resolutionPolicy.ResolveRankedCandidates(candidates, this.candidates.ReferenceSelection);

    private VbaSourceDefinition? ResolveBestRankCandidates(
        IEnumerable<VbaRankedDefinition> candidates,
        Func<VbaSourceDefinition, bool> definitionFilter,
        bool fallbackToUnfiltered)
        => ResolveBestRankCandidatesOutcome(
            candidates,
            definitionFilter,
            fallbackToUnfiltered).Target?.SelectedDefinition;

    private VbaNameResolutionOutcome ResolveBestRankCandidatesOutcome(
        IEnumerable<VbaRankedDefinition> candidates,
        Func<VbaSourceDefinition, bool> definitionFilter,
        bool fallbackToUnfiltered)
    {
        var rankedCandidates = candidates.ToArray();
        if (rankedCandidates.Length == 0)
        {
            return VbaNameResolutionOutcome.Unresolved;
        }

        var bestRank = rankedCandidates.Min(candidate => candidate.Rank);
        var bestCandidates = rankedCandidates
            .Where(candidate => candidate.Rank == bestRank)
            .ToArray();
        var filteredCandidates = bestCandidates
            .Where(candidate => definitionFilter(candidate.Definition))
            .ToArray();
        return resolutionPolicy.ResolveRankedCandidatesOutcome(
            filteredCandidates.Length > 0 || !fallbackToUnfiltered
                ? filteredCandidates
                : bestCandidates,
            this.candidates.ReferenceSelection);
    }

    internal VbaSourceDocument? FindDocument(string uri)
        => candidates.FindDocument(uri);

    internal IReadOnlyList<VbaSourceDefinition> GetLogicalDefinitions(
        VbaSourceDefinition definition)
        => candidates.ConditionalFamilies.GetLogicalDefinitions(definition);

    internal bool HasIndeterminateConditionalCompilationOwnership(
        VbaSourceDefinition definition)
        => definition.Identity.Origin == VbaDefinitionOrigin.Source
            && definition.ConditionalCompilationPath is null
            && FindDocument(definition.Uri)?
                .SyntaxTree?
                .Diagnostics
                .Any(diagnostic => diagnostic.Code.StartsWith(
                    "syntax.malformedPreprocessor",
                    StringComparison.Ordinal)) == true;

    internal bool HasIncompleteSourceEventSurfaceEvidence(string uri)
    {
        var document = FindDocument(uri);
        return document?.SyntaxTree?.Module.IncompleteEventDeclarationRanges.Count > 0
            || document?.Definitions.Any(definition =>
                definition.Kind == VbaSourceDefinitionKind.Event
                && (definition.IsRecoveredEventDeclaration
                    || HasIndeterminateConditionalCompilationOwnership(
                        definition))) == true;
    }

    internal VbaSourceDefinition? ResolveTypeDefinition(
        VbaSourceDocument currentDocument,
        VbaTypeReference typeReference)
        => ResolveTypeDefinitionOutcome(
            currentDocument,
            typeReference).Target?.SelectedDefinition;

    internal VbaNameResolutionOutcome ResolveTypeDefinitionOutcome(
        VbaSourceDocument currentDocument,
        VbaTypeReference typeReference)
    {
        if (!string.IsNullOrEmpty(typeReference.Qualifier))
        {
            return candidates.HasSourceModule(typeReference.Qualifier)
                ? ResolveSourceTypeDefinitionOutcome(
                    currentDocument,
                    typeReference.Name,
                    typeReference.Qualifier)
                : ResolveReferenceTypeDefinitionOutcome(
                    typeReference.Qualifier,
                    typeReference.Name);
        }

        var source = ResolveSourceTypeDefinitionOutcome(
            currentDocument,
            typeReference.Name,
            qualifier: null);
        return source.Kind == VbaNameResolutionKind.Unresolved
            ? ResolveReferenceCandidatesOutcome(
                candidates.GetReferenceCandidates(typeReference.Name)
                    .Where(candidate => resolutionPolicy.IsTypeDefinition(
                        candidate.Definition))
                    .Where(candidate => candidate.ParentTypeName is null)
                    .Select(candidate => candidate.Definition))
            : source;
    }

    internal VbaSourceDefinition? ResolveProjectReferenceTypeDefinition(
        string owningReferenceName,
        VbaTypeReference typeReference)
    {
        IEnumerable<VbaSourceDefinition> definitions;
        if (string.IsNullOrEmpty(typeReference.Qualifier))
        {
            definitions = candidates.GetReferenceCandidates(typeReference.Name)
                .Where(candidate => VbaProjectReferenceName.AreEquivalent(
                    candidate.ModuleName,
                    owningReferenceName))
                .Select(candidate => candidate.Definition);
        }
        else
        {
            definitions = candidates.GetQualifiedReferenceDefinitions(
                typeReference.Qualifier,
                typeReference.Name);
            if (candidates.GetCanonicalReferenceQualifier(
                    owningReferenceName,
                    typeReference.Qualifier) is not null)
            {
                definitions = definitions.Where(definition =>
                    VbaProjectReferenceName.AreEquivalent(
                        definition.Identity.ReferenceName ?? definition.ModuleName,
                        owningReferenceName));
            }
        }

        return ResolveReferenceCandidates(definitions
            .Where(resolutionPolicy.IsTypeDefinition)
            .Where(definition => definition.ParentTypeName is null));
    }

    internal VbaSourceDefinition? ResolveProjectReferenceMemberDefinition(
        string owningReferenceName,
        string parentTypeName,
        string memberName,
        VbaSourceDefinitionKind kind)
        => ResolveReferenceCandidates(
            candidates.GetReferenceCandidatesByParentType(parentTypeName)
                .Where(candidate => VbaProjectReferenceName.AreEquivalent(
                    candidate.ModuleName,
                    owningReferenceName))
                .Where(candidate => candidate.Name.Equals(
                    memberName,
                    StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.Definition)
                .Where(definition => definition.Kind == kind));

    internal IReadOnlyList<VbaSourceDefinition>
        GetProjectReferencePhysicalMembers(
            string owningReferenceName,
            string parentTypeName)
        => candidates.GetReferenceCandidatesByParentType(parentTypeName)
            .Where(candidate => VbaProjectReferenceName.AreEquivalent(
                candidate.ModuleName,
                owningReferenceName))
            .Select(candidate => candidate.Definition)
            .Where(definition => definition.Identity.Origin
                    == VbaDefinitionOrigin.ProjectReference
                && definition.IsAuthoringAvailable)
            .DistinctBy(definition => definition.Identity)
            .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(definition => definition.Name, StringComparer.Ordinal)
            .ThenBy(definition => definition.PropertyAccessorKind)
            .ToArray();

    internal IReadOnlyList<VbaSourceDefinition> GetVisibleTypeDefinitions(
        VbaSourceDocument currentDocument,
        string? qualifier = null)
        => GetRankedVisibleTypeDefinitions(currentDocument, qualifier)
            .Select(candidate => candidate.Definition)
            .ToArray();

    internal IReadOnlyList<VbaRankedDefinition> GetRankedVisibleTypeDefinitions(
        VbaSourceDocument currentDocument,
        string? qualifier = null)
    {
        if (!string.IsNullOrEmpty(qualifier))
        {
            if (candidates.HasSourceModule(qualifier))
            {
                return candidates.GetSourceCandidatesByModule(qualifier)
                    .Where(candidate => resolutionPolicy.IsTypeDefinition(candidate.Definition))
                    .Where(candidate => candidate.Definition.Kind is not (
                        VbaSourceDefinitionKind.Class or VbaSourceDefinitionKind.Form))
                    .Where(candidate => SameUri(candidate.Uri, currentDocument.Uri)
                        || candidate.Visibility.IsProjectVisible())
                    .Select(candidate => new VbaRankedDefinition(
                        candidate.Definition,
                        VbaResolutionPolicy.ReferenceRank))
                    .ToArray();
            }

            return candidates.HasReferenceSelection
                ? candidates.GetQualifiedReferenceDefinitions(qualifier)
                    .Where(resolutionPolicy.IsTypeDefinition)
                    .Where(definition => definition.ParentTypeName is null)
                    .Select(definition => new VbaRankedDefinition(
                        definition,
                        VbaResolutionPolicy.ReferenceRank))
                    .ToArray()
                : [];
        }

        var visibleDefinitions = new List<VbaRankedDefinition>();
        visibleDefinitions.AddRange(candidates.GetSourceCandidates(currentDocument)
            .Where(candidate => resolutionPolicy.IsTypeDefinition(candidate.Definition))
            .Select(candidate => new VbaRankedDefinition(
                candidate.Definition,
                VbaResolutionPolicy.CurrentModuleRank)));
        visibleDefinitions.AddRange(candidates.GetSourceCandidates(requestedName: null)
            .Where(candidate => !SameUri(candidate.Uri, currentDocument.Uri))
            .Where(candidate => resolutionPolicy.IsTypeDefinition(candidate.Definition))
            .Where(candidate => candidate.Visibility.IsProjectVisible())
            .Select(candidate => new VbaRankedDefinition(
                candidate.Definition,
                VbaResolutionPolicy.ProjectRank)));
        visibleDefinitions.AddRange(candidates.GetReferenceCandidates(requestedName: null)
            .Where(candidate => resolutionPolicy.IsTypeDefinition(candidate.Definition))
            .Where(candidate => candidate.ParentTypeName is null)
            .Select(candidate => new VbaRankedDefinition(
                candidate.Definition,
                VbaResolutionPolicy.ReferenceRank)));
        return visibleDefinitions;
    }

    internal IReadOnlyList<VbaSourceDefinition> GetMembersOfType(
        VbaSourceDocument currentDocument,
        VbaResolvedType resolvedType)
    {
        return GetMemberCandidates(currentDocument, resolvedType)
            .Where(candidate => candidate.Definition.IsAuthoringAvailable)
            .GroupBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => ResolveMemberCandidateGroup(group.Select(candidate => candidate.Definition)))
            .Where(definition => definition is not null)
            .Select(definition => definition!)
            .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal IReadOnlyList<VbaSourceDefinition> GetPhysicalMembersOfType(
        VbaResolvedType resolvedType)
    {
        var owner = resolvedType.SourceDefinition;
        if (resolvedType.ReferenceName is not null || owner is null)
        {
            return [];
        }

        return candidates.ConditionalFamilies
            .GetLogicalDefinitions(owner)
            .SelectMany(ownerVariant =>
            {
                var ownerCandidates = ownerVariant.Kind is VbaSourceDefinitionKind.Type
                        or VbaSourceDefinitionKind.Enum
                    ? candidates.GetSourceCandidatesByParentType(ownerVariant.Name)
                    : candidates.GetSourceCandidatesByModule(ownerVariant.Name);
                return ownerCandidates.Where(candidate =>
                    SameUri(candidate.Uri, ownerVariant.Uri));
            })
            .Select(candidate => candidate.Definition)
            .DistinctBy(definition => definition.Identity)
            .Where(resolutionPolicy.IsReferenceTarget)
            .ToArray();
    }

    internal IReadOnlyList<VbaSourceDefinition> GetMembersOfType(
        VbaSourceDocument currentDocument,
        string typeName,
        string? referenceName)
        => GetMemberCandidates(currentDocument, typeName, referenceName)
            .Where(candidate => candidate.Definition.IsAuthoringAvailable)
            .GroupBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => ResolveMemberCandidateGroup(
                group.Select(candidate => candidate.Definition)))
            .Where(definition => definition is not null)
            .Select(definition => definition!)
            .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal VbaSourceDefinition? ResolveMember(
        VbaSourceDocument currentDocument,
        VbaResolvedType resolvedType,
        string memberName,
        VbaSourceDefinitionKind? requiredKind = null)
        => ResolveMemberOutcome(
            currentDocument,
            resolvedType,
            memberName,
            requiredKind).Target?.SelectedDefinition;

    internal VbaNameResolutionOutcome ResolveMemberOutcome(
        VbaSourceDocument currentDocument,
        VbaResolvedType resolvedType,
        string memberName,
        VbaSourceDefinitionKind? requiredKind = null)
    {
        var matchingCandidates = GetMemberCandidates(currentDocument, resolvedType)
            .Where(candidate => SameName(candidate.Name, memberName))
            .Where(candidate => requiredKind is null || candidate.Definition.Kind == requiredKind)
            .Where(candidate => requiredKind != VbaSourceDefinitionKind.Event
                || candidate.Definition.IsEventNameProjectionEligible)
            .Select(candidate => candidate.Definition)
            .ToArray();
        return resolutionPolicy.ResolveRankedCandidatesOutcome(
            matchingCandidates.Select(definition => new VbaRankedDefinition(
                definition,
                VbaResolutionPolicy.CurrentModuleRank)),
            candidates.ReferenceSelection);
    }

    internal VbaSourceDefinition? ResolveMember(
        VbaSourceDocument currentDocument,
        string typeName,
        string? referenceName,
        string memberName,
        VbaSourceDefinitionKind? requiredKind = null)
    {
        var matchingCandidates = GetMemberCandidates(
                currentDocument,
                typeName,
                referenceName)
            .Where(candidate => SameName(candidate.Name, memberName))
            .Where(candidate => requiredKind is null
                || candidate.Definition.Kind == requiredKind)
            .Select(candidate => candidate.Definition)
            .ToArray();
        return ResolveMemberCandidateGroup(matchingCandidates);
    }

    internal VbaSourceDefinition? ResolveSourceTypeCompletionGroup(IReadOnlyList<VbaSourceDefinition> definitions)
    {
        var sourceDefinitions = definitions
            .Where(definition => definition.Identity.Origin != VbaDefinitionOrigin.ProjectReference)
            .ToArray();
        var logicalSourceDefinitions = candidates.ConditionalFamilies
            .Coalesce(sourceDefinitions);
        if (logicalSourceDefinitions.Count == 1)
        {
            return logicalSourceDefinitions[0];
        }

        return logicalSourceDefinitions.Count > 1
            ? null
            : ResolveReferenceCandidates(definitions);
    }

    internal bool IsTypeDefinition(VbaSourceDefinition definition)
        => resolutionPolicy.IsTypeDefinition(definition);

    internal string? GetCanonicalQualifierName(VbaSourceDefinition definition, string qualifier)
        => definition.Identity.Origin != VbaDefinitionOrigin.ProjectReference
            ? definition.ModuleName
            : candidates.GetCanonicalReferenceQualifier(definition.ModuleName, qualifier);

    internal string? GetPreferredReferenceQualifierName(VbaSourceDefinition definition)
        => definition.Identity.Origin == VbaDefinitionOrigin.ProjectReference
            ? candidates.GetCanonicalReferenceQualifier(definition.ModuleName)
            : null;

    internal bool IsReferenceQualifierAmbiguous(string qualifier)
        => candidates.IsReferenceQualifierAmbiguous(qualifier);

    internal VbaTypeLibEventSurface GetTypeLibEventSurface(
        string referenceName,
        string typeName)
        => candidates.GetTypeLibEventSurface(referenceName, typeName);

    private VbaSourceDefinition? ResolveReferenceTypeDefinition(string qualifier, string typeName)
        => ResolveReferenceTypeDefinitionOutcome(
            qualifier,
            typeName).Target?.SelectedDefinition;

    private VbaNameResolutionOutcome ResolveReferenceTypeDefinitionOutcome(
        string qualifier,
        string typeName)
        => ResolveReferenceCandidatesOutcome(candidates.GetQualifiedReferenceDefinitions(qualifier, typeName)
            .Where(resolutionPolicy.IsTypeDefinition)
            .Where(definition => definition.ParentTypeName is null));

    private VbaSourceDefinition? ResolveSourceTypeDefinition(
        VbaSourceDocument currentDocument,
        string typeName,
        string? qualifier)
        => ResolveSourceTypeDefinitionOutcome(
            currentDocument,
            typeName,
            qualifier).Target?.SelectedDefinition;

    private VbaNameResolutionOutcome ResolveSourceTypeDefinitionOutcome(
        VbaSourceDocument currentDocument,
        string typeName,
        string? qualifier)
    {
        var definitions = candidates.GetSourceCandidates(typeName)
            .Where(candidate => resolutionPolicy.IsTypeDefinition(candidate.Definition))
            .Where(candidate => qualifier is null || SameName(candidate.ModuleName, qualifier))
            .Where(candidate => SameUri(candidate.Uri, currentDocument.Uri)
                || candidate.Visibility.IsProjectVisible())
            .Select(candidate => new VbaRankedDefinition(
                candidate.Definition,
                SameUri(candidate.Uri, currentDocument.Uri)
                    ? VbaResolutionPolicy.CurrentModuleRank
                    : VbaResolutionPolicy.ProjectRank))
            .ToArray();
        return resolutionPolicy.ResolveRankedCandidatesOutcome(
            definitions,
            candidates.ReferenceSelection);
    }

    private VbaSourceDefinition? ResolveReferenceCandidates(IEnumerable<VbaSourceDefinition> definitions)
        => resolutionPolicy.ResolveReferenceCandidates(definitions, candidates.ReferenceSelection);

    private VbaNameResolutionOutcome ResolveReferenceCandidatesOutcome(
        IEnumerable<VbaSourceDefinition> definitions)
        => resolutionPolicy.ResolveRankedCandidatesOutcome(
            definitions.Select(definition => new VbaRankedDefinition(
                definition,
                VbaResolutionPolicy.ReferenceRank)),
            candidates.ReferenceSelection);

    private bool IsUnqualifiedReferenceRootDefinition(VbaSourceDefinition definition)
    {
        return definition.ReferenceGlobalExposure switch
        {
            ReferenceDefinitionGlobalExposure.LibraryGlobal => true,
            ReferenceDefinitionGlobalExposure.MainHostGlobal =>
                candidates.ReferenceSelection?.MainVbaProjectReference is not null
                && VbaProjectReferenceName.AreEquivalent(
                    candidates.ReferenceSelection.MainVbaProjectReference.Name,
                    definition.ModuleName),
            _ => definition.ParentTypeName is null
                && resolutionPolicy.IsTypeDefinition(definition)
        };
    }

    private bool IsQualifiedReferenceRootDefinition(VbaSourceDefinition definition)
        => definition.ReferenceGlobalExposure != ReferenceDefinitionGlobalExposure.None
            || (definition.ParentTypeName is null
                && resolutionPolicy.IsTypeDefinition(definition));

    private VbaSourceDefinition? ResolveMemberCandidateGroup(
        IEnumerable<VbaSourceDefinition> definitions)
        => resolutionPolicy.ResolveRankedCandidates(
            definitions.Select(definition => new VbaRankedDefinition(
                definition,
                VbaResolutionPolicy.CurrentModuleRank)),
            candidates.ReferenceSelection);

    private IEnumerable<VbaNameCandidate> GetMemberCandidates(
        VbaSourceDocument currentDocument,
        VbaResolvedType resolvedType)
    {
        if (resolvedType.ReferenceName is not null)
        {
            return GetMemberCandidates(
                currentDocument,
                resolvedType.Name,
                resolvedType.ReferenceName);
        }

        var owner = resolvedType.SourceDefinition;
        if (owner is null)
        {
            return [];
        }

        var ownerVariants = candidates.ConditionalFamilies
            .GetLogicalDefinitions(owner);
        return ownerVariants
            .SelectMany(ownerVariant =>
            {
                var ownerCandidates = ownerVariant.Kind is VbaSourceDefinitionKind.Type
                        or VbaSourceDefinitionKind.Enum
                    ? candidates.GetSourceCandidatesByParentType(ownerVariant.Name)
                    : candidates.GetSourceCandidatesByModule(ownerVariant.Name);
                return ownerCandidates.Where(candidate =>
                    SameUri(candidate.Uri, ownerVariant.Uri));
            })
            .DistinctBy(candidate => candidate.Definition.Identity)
            .Where(candidate => resolutionPolicy.IsReferenceTarget(
                candidate.Definition))
            .Where(candidate => SameUri(candidate.Uri, currentDocument.Uri)
                || candidate.Visibility.IsProjectVisible());
    }

    private IEnumerable<VbaNameCandidate> GetMemberCandidates(
        VbaSourceDocument currentDocument,
        string typeName,
        string? referenceName)
        => referenceName is not null
            ? candidates.GetReferenceCandidatesByParentType(typeName)
                .Where(candidate => SameName(candidate.ModuleName, referenceName))
            : candidates.GetSourceCandidatesByModule(typeName)
                .Concat(candidates.GetSourceCandidatesByParentType(typeName))
                .Where(candidate => resolutionPolicy.IsReferenceTarget(
                    candidate.Definition))
                .Where(candidate => SameUri(candidate.Uri, currentDocument.Uri)
                    || candidate.Visibility.IsProjectVisible());

    private static bool MatchesRequestedName(VbaNameCandidate candidate, string? requestedName)
        => requestedName is null || SameName(candidate.Name, requestedName);

    private static bool ContainsPosition(VbaSourceDefinition definition, VbaPosition position)
    {
        if (definition.ParentProcedureRange is null)
        {
            return false;
        }

        return ComparePosition(definition.ParentProcedureRange.Start, position) <= 0
            && ComparePosition(position, definition.ParentProcedureRange.End) <= 0;
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
}

/// <summary>
/// Captures immutable, case-insensitive lookup facts used by name and type resolution.
/// </summary>
internal sealed class VbaNameCandidateInventory
{
    private readonly VbaProjectReferenceCatalogSet referenceCatalogs;
    private readonly IReadOnlyDictionary<string, VbaProjectReferenceCatalogSource>
        referenceCatalogSources;
    private readonly IReadOnlyList<(string ReferenceName, string Qualifier)> activeReferenceQualifiers;
    private readonly IReadOnlyList<VbaNameCandidate> sourceCandidates;
    private readonly IReadOnlyList<VbaNameCandidate> referenceCandidates;
    private readonly ILookup<string, VbaSourceDocument> documentsByUri;
    private readonly ILookup<string, VbaNameCandidate> sourceCandidatesByDocument;
    private readonly ILookup<string, VbaNameCandidate> sourceCandidatesByName;
    private readonly ILookup<string, VbaNameCandidate> sourceCandidatesByModule;
    private readonly ILookup<string, VbaNameCandidate> sourceCandidatesByParentType;
    private readonly ILookup<string, VbaNameCandidate> referenceCandidatesByName;
    private readonly ILookup<string, VbaNameCandidate> referenceCandidatesByParentType;
    private readonly ILookup<string, VbaSourceDefinition> qualifiedReferenceDefinitionsByQualifier;
    private readonly IReadOnlyList<VbaSourceDefinition> workspaceSymbolDefinitions;

    public VbaNameCandidateInventory(
        IReadOnlyList<VbaSourceDocument> documents,
        VbaProjectReferenceSelection? referenceSelection,
        VbaProjectReferenceCatalogSet referenceCatalogs,
        IReadOnlyList<VbaSourceDefinition> activeReferenceDefinitions,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogSource>?
            referenceCatalogSources = null)
    {
        ReferenceSelection = referenceSelection;
        this.referenceCatalogs = referenceCatalogs;
        this.referenceCatalogSources = referenceCatalogSources is null
            ? new Dictionary<string, VbaProjectReferenceCatalogSource>(
                VbaProjectReferenceName.Comparer)
            : new Dictionary<string, VbaProjectReferenceCatalogSource>(
                referenceCatalogSources,
                VbaProjectReferenceName.Comparer);
        documentsByUri = documents.ToLookup(
            document => document.Uri,
            StringComparer.OrdinalIgnoreCase);
        sourceCandidates = documents
            .SelectMany(document => document.Definitions.Select(definition => new VbaNameCandidate(definition, document)))
            .ToArray();
        referenceCandidates = activeReferenceDefinitions
            .Select(definition => new VbaNameCandidate(definition, Document: null))
            .ToArray();
        sourceCandidatesByDocument = sourceCandidates.ToLookup(
            candidate => candidate.Uri,
            StringComparer.OrdinalIgnoreCase);
        sourceCandidatesByName = sourceCandidates.ToLookup(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase);
        sourceCandidatesByModule = sourceCandidates.ToLookup(candidate => candidate.ModuleName, StringComparer.OrdinalIgnoreCase);
        sourceCandidatesByParentType = sourceCandidates
            .Where(candidate => candidate.ParentTypeName is not null)
            .ToLookup(candidate => candidate.ParentTypeName!, StringComparer.OrdinalIgnoreCase);
        referenceCandidatesByName = referenceCandidates.ToLookup(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase);
        referenceCandidatesByParentType = referenceCandidates
            .Where(candidate => candidate.ParentTypeName is not null)
            .ToLookup(candidate => candidate.ParentTypeName!, StringComparer.OrdinalIgnoreCase);
        activeReferenceQualifiers = referenceCatalogs.GetActiveQualifierAliases(referenceSelection);
        ConditionalFamilies = new VbaConditionalDeclarationFamilyIndex(documents);
        var qualifiedReferenceDefinitions = activeReferenceQualifiers
            .Select(candidate => candidate.Qualifier)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(qualifier => referenceCatalogs
                .GetQualifiedDefinitions(referenceSelection, qualifier)
                .Select(definition => new VbaQualifiedReferenceDefinition(
                    qualifier,
                    VbaSemanticInventory.CaptureDefinition(definition))))
            .ToArray();
        qualifiedReferenceDefinitionsByQualifier = qualifiedReferenceDefinitions.ToLookup(
            candidate => candidate.Qualifier,
            candidate => candidate.Definition,
            StringComparer.OrdinalIgnoreCase);
        workspaceSymbolDefinitions = sourceCandidates
            .Select(candidate => candidate.Definition)
            .Where(definition => definition.Visibility != VbaSourceDefinitionVisibility.Local)
            .Where(definition => !VbaProjectReferenceCatalogSet.IsExternalDefinition(definition))
            .ToArray();
    }

    public VbaProjectReferenceSelection? ReferenceSelection { get; }

    public VbaConditionalDeclarationFamilyIndex ConditionalFamilies { get; }

    public bool HasReferenceSelection => referenceCandidates.Count > 0 || activeReferenceQualifiers.Count > 0;

    internal VbaTypeLibEventSurface GetTypeLibEventSurface(
        string referenceName,
        string typeName)
    {
        if (referenceCatalogSources.TryGetValue(referenceName, out var source)
            && source is VbaProjectReferenceCatalogSource.StalePersisted
                or VbaProjectReferenceCatalogSource.Unavailable)
        {
            return VbaTypeLibEventSurface.Indeterminate;
        }

        return referenceCatalogs.GetTypeLibEventSurface(referenceName, typeName);
    }

    public VbaSourceDocument? FindDocument(string uri)
        => documentsByUri[uri].FirstOrDefault();

    public IReadOnlyList<VbaSourceDefinition> GetDocumentDefinitions(string uri)
        => FindDocument(uri)?.Definitions
            ?? Array.Empty<VbaSourceDefinition>();

    public IReadOnlyList<VbaSourceDefinition> GetWorkspaceSymbolDefinitions()
        => workspaceSymbolDefinitions;

    public IEnumerable<VbaNameCandidate> GetSourceCandidates(VbaSourceDocument document)
        => sourceCandidatesByDocument[document.Uri];

    public IEnumerable<VbaNameCandidate> GetSourceCandidates(string? requestedName)
        => requestedName is null ? sourceCandidates : sourceCandidatesByName[requestedName];

    public IEnumerable<VbaNameCandidate> GetSourceCandidatesByModule(string moduleName)
        => sourceCandidatesByModule[moduleName];

    public IEnumerable<VbaNameCandidate> GetSourceCandidatesByParentType(
        string parentTypeName)
        => sourceCandidatesByParentType[parentTypeName];

    public bool HasSourceModule(string moduleName)
        => sourceCandidatesByModule[moduleName].Any();

    public IEnumerable<VbaNameCandidate> GetReferenceCandidates(string? requestedName)
        => requestedName is null ? referenceCandidates : referenceCandidatesByName[requestedName];

    public IEnumerable<VbaNameCandidate> GetReferenceCandidatesByParentType(string parentTypeName)
        => referenceCandidatesByParentType[parentTypeName];

    public IEnumerable<string> GetSourceModuleNames()
        => sourceCandidates
            .Where(candidate => candidate.Definition.Kind == VbaSourceDefinitionKind.Module)
            .Select(candidate => candidate.Name);

    public IEnumerable<string> GetReferenceQualifiers()
        => activeReferenceQualifiers.Select(candidate => candidate.Qualifier);

    public IReadOnlyList<VbaSourceDefinition> GetQualifiedReferenceDefinitions(string qualifier)
        => qualifiedReferenceDefinitionsByQualifier[qualifier].ToArray();

    public IReadOnlyList<VbaSourceDefinition> GetQualifiedReferenceDefinitions(string qualifier, string name)
        => qualifiedReferenceDefinitionsByQualifier[qualifier]
            .Where(definition => definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public string? GetCanonicalReferenceQualifier(string referenceName, string qualifier)
        => activeReferenceQualifiers
            .Where(candidate => VbaProjectReferenceName.AreEquivalent(
                candidate.ReferenceName,
                referenceName))
            .Select(candidate => candidate.Qualifier)
            .FirstOrDefault(candidate => candidate.Equals(qualifier, StringComparison.OrdinalIgnoreCase));

    public string? GetCanonicalReferenceQualifier(string referenceName)
        => activeReferenceQualifiers
            .Where(candidate => VbaProjectReferenceName.AreEquivalent(
                candidate.ReferenceName,
                referenceName))
            .Select(candidate => candidate.Qualifier)
            .FirstOrDefault();

    public bool IsReferenceQualifierAmbiguous(string qualifier)
        => activeReferenceQualifiers
            .Where(candidate => candidate.Qualifier.Equals(
                qualifier,
                StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.ReferenceName)
            .Distinct(VbaProjectReferenceName.Comparer)
            .Skip(1)
            .Any();
}

/// <summary>
/// Stores the resolution facts for one definition candidate.
/// </summary>
internal sealed record VbaNameCandidate(VbaSourceDefinition Definition, VbaSourceDocument? Document)
{
    public string Uri => Definition.Uri;

    public string Name => Definition.Name;

    public string ModuleName => Definition.ModuleName;

    public string? ParentTypeName => Definition.ParentTypeName;

    public VbaDefinitionOrigin Origin => Definition.Identity.Origin;

    public VbaSourceDefinitionVisibility Visibility => Definition.Visibility;
}

internal sealed record VbaQualifiedReferenceDefinition(string Qualifier, VbaSourceDefinition Definition);
