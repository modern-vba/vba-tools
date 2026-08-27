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
            referenceCatalogSources)
    {
        this.sourceDocuments = sourceDocuments;
        this.definitionCandidates = definitionCandidates;
        resolutionPolicy = new VbaResolutionPolicy(
            definitionCandidates.ConditionalFamilies);
        this.referenceSelection = referenceSelection;
        this.referenceCatalogs = referenceCatalogs;
        this.referenceCatalogSources = referenceCatalogSources;
        this.hostClassProjectionSnapshot = hostClassProjectionSnapshot;
        semanticResolution = new VbaSemanticResolution(
            definitionCandidates,
            resolutionPolicy);
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
            referenceCatalogSources = null)
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
            capturedCatalogSources);
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
        GetProjectValidationDiagnostics(string uri)
        => projectValidationDiagnostics.GetDiagnostics(uri);

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

    public VbaDefinitionLocation? ResolveDefinition(string uri, int line, int character)
        => ResolveDefinitions(uri, line, character).FirstOrDefault();

    public IReadOnlyList<VbaDefinitionLocation> ResolveDefinitions(
        string uri,
        int line,
        int character)
    {
        var target = ResolveSourceTarget(uri, line, character);
        if (target is null)
        {
            return [];
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
        var references = occurrenceTargets
            .SelectMany(occurrenceTarget => resolvedOccurrences.FindMatching(
                occurrenceTarget,
                cancellationToken))
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
        var definitions = isCallablePropertyTarget
            ? callablePropertyTarget!.Property.PropertyDefinitions
            : target.IsConditionalFamily
                ? target.PhysicalDefinitions
                : [target.SelectedDefinition];
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
        var identifier = syntaxTree?
            .GetPositionSyntax(line, character)
            .Identifier;
        if (identifier is null)
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

        if (!resolutionPolicy.IsRenameTarget(target))
        {
            return new VbaPrepareRenameOutcome(
                Result: null,
                new VbaRenameFailure(
                    "notRenameTarget",
                    $"'{target.Name}' is known semantic metadata but is not "
                    + "a source-owned Rename target."));
        }

        target = GetLogicalRenameTarget(target);

        return new VbaPrepareRenameOutcome(
            new VbaPrepareRenameResult(
                new VbaRange(
                    new VbaPosition(
                        identifier.Range.Start.Line,
                        identifier.Range.Start.Character),
                    new VbaPosition(
                        identifier.Range.End.Line,
                        identifier.Range.End.Character)),
                target.Name),
            Failure: null);
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

    internal VbaRenameResult CreateRenameResult(
        string uri,
        int line,
        int character,
        string newName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var nameFailure = ValidateRenameName(newName);
        if (nameFailure is not null)
        {
            return new VbaRenameResult(
                Plan: null,
                nameFailure);
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

        if (!resolutionPolicy.IsRenameTarget(target))
        {
            return new VbaRenameResult(
                Plan: null,
                new VbaRenameFailure(
                    "notRenameTarget",
                    "Rename requires a source-defined VBA rename target at "
                    + "the requested position."));
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

        var invalidTargetConflicts = FindInvalidPropertyFamilyConflicts(target);
        if (invalidTargetConflicts.Count > 0)
        {
            var locations = string.Join(
                ", ",
                invalidTargetConflicts.Select(conflict =>
                    $"'{conflict.Name}' at {conflict.Uri}:"
                    + $"{conflict.Range.Start.Line + 1}:"
                    + $"{conflict.Range.Start.Character + 1}"));
            return new VbaRenameResult(
                Plan: null,
                new VbaRenameFailure(
                    "sameScopeCollision",
                    "Rename target has repeated Property accessors at "
                    + $"{locations}.",
                    invalidTargetConflicts));
        }

        var collisions = FindSameScopeCollisions(target, newName);
        if (collisions.Count > 0)
        {
            var locations = string.Join(
                ", ",
                collisions.Select(conflict =>
                    $"'{conflict.Name}' at {conflict.Uri}:"
                    + $"{conflict.Range.Start.Line + 1}:"
                    + $"{conflict.Range.Start.Character + 1}"));
            return new VbaRenameResult(
                Plan: null,
                new VbaRenameFailure(
                    "sameScopeCollision",
                    $"Rename to '{newName}' conflicts with declarations {locations}.",
                    collisions));
        }

        var targetOccurrences = GetLogicalRenameTargetDefinitions(target)
            .Select(resolutionPolicy.CreateNameTarget)
            .DistinctBy(logicalTarget => logicalTarget.Identity)
            .SelectMany(logicalTarget => resolvedOccurrences.FindMatching(
                logicalTarget,
                cancellationToken))
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
        var changes = targetOccurrences
            .GroupBy(occurrence => occurrence.Uri, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<VbaTextEdit>)group
                    .Select(occurrence => new VbaTextEdit(occurrence.Range, newName))
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        cancellationToken.ThrowIfCancellationRequested();
        var proofFailure = ProveBindingsArePreserved(
            target,
            targetOccurrences,
            changes,
            cancellationToken);
        if (proofFailure is not null)
        {
            return new VbaRenameResult(Plan: null, proofFailure);
        }

        return new VbaRenameResult(
            changes.Count == 0
                ? null
                : new VbaRenamePlan(target.Range, changes),
            Failure: null);
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
        return VbaPropertyAccessorCoalescing.Coalesce(candidates)
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

    private VbaSourceDefinition GetLogicalRenameTarget(
        VbaSourceDefinition target)
    {
        var definitions = GetLogicalRenameTargetDefinitions(target);
        if (definitions.Count == 1)
        {
            return definitions[0];
        }

        var logicalTarget = resolutionPolicy.CreateNameTarget(target);
        return definitions.FirstOrDefault(definition => string.Equals(
                definition.Name,
                logicalTarget.CanonicalName,
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
        if (candidates.Length <= 1
            || VbaPropertyAccessorCoalescing.Coalesce(candidates).Count == 1)
        {
            return [];
        }

        var repeatedAccessorCandidates = candidates
            .GroupBy(candidate => candidate.PropertyAccessorKind)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToArray();
        var conflicts = repeatedAccessorCandidates.Length > 0
            ? repeatedAccessorCandidates
            : candidates;
        return conflicts
            .Where(candidate => candidate.Identity != target.Identity)
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
        if (target.Kind != VbaSourceDefinitionKind.Property)
        {
            var logicalTarget = resolutionPolicy.CreateNameTarget(target);
            return logicalTarget.IsConditionalFamily
                ? logicalTarget.PhysicalDefinitions
                : [target];
        }

        var candidates = GetPropertyFamilyCandidates(target);
        return VbaPropertyAccessorCoalescing.Coalesce(candidates).Count == 1
            ? candidates
            : [target];
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
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (targetOccurrences.Count == 0)
        {
            return AnalysisIncomplete(
                "Rename could not establish the complete target occurrence set.");
        }

        var hypothetical = CreateHypotheticalInventory(changes, cancellationToken);
        var newName = changes.Values
            .SelectMany(edits => edits)
            .Select(edit => edit.NewText)
            .FirstOrDefault() ?? target.Name;
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

        var targetRanges = targetOccurrences
            .Select(occurrence => CreateOccurrenceKey(occurrence.Uri, occurrence.Range))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var occurrence in targetOccurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mappedRange = MapRange(occurrence.Uri, occurrence.Range, changes);
            var postDefinition = IsDeclarationOccurrence(occurrence)
                ? FindHypotheticalDefinition(
                    hypothetical,
                    occurrence.Target.SelectedDefinition,
                    changes,
                    newName)
                : hypothetical.ResolveSourceDefinition(
                    occurrence.Uri,
                    mappedRange.Start.Line,
                    mappedRange.Start.Character);
            if (!AreLogicalDefinitionsEquivalent(
                hypothetical,
                postDefinition,
                hypotheticalTarget))
            {
                return ResolutionChanged(
                    "Rename would change the binding of a target occurrence.");
            }
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

        foreach (var occurrence in GetUnresolvedSemanticOccurrences(
            cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(
                    occurrence.Name,
                    target.Name,
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    occurrence.Name,
                    newName,
                    StringComparison.OrdinalIgnoreCase))
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

        return null;
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
            referenceCatalogSources);
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
        return hypothetical.GetDocumentDefinitions(definition.Uri)
            .FirstOrDefault(candidate =>
                candidate.Kind == definition.Kind
                && candidate.Range.Start == mappedRange.Start
                && string.Equals(
                    candidate.Name,
                    expectedName ?? definition.Name,
                    StringComparison.OrdinalIgnoreCase));
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
            document.SyntaxTree);

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
        => target.Kind == VbaSourceDefinitionKind.Event
            && value.Contains('_', StringComparison.Ordinal)
                ? new VbaRenameFailure(
                    "invalidName",
                    "Event Rename requires a VBA identifier without an "
                    + "ASCII underscore.")
                : null;
}
