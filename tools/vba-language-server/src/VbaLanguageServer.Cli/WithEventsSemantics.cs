using VbaLanguageServer.Diagnostics;
using VbaTools.Syntax;

namespace VbaLanguageServer.SourceModel;

internal enum VbaWithEventsTypeEligibilityKind
{
    Eligible,
    InvalidEnclosingClass,
    InvalidNotClass,
    InvalidInaccessibleType,
    InvalidNoEvents,
    Indeterminate
}

internal sealed record VbaWithEventsTypeEligibility(
    VbaWithEventsTypeEligibilityKind Kind,
    VbaSourceDefinition? TypeDefinition = null,
    VbaTypeLibEventSurface? TypeLibEventSurface = null,
    VbaIntrinsicHostEventSurface? IntrinsicHostEventSurface = null);

internal enum VbaWithEventsEventBindingStatus
{
    Resolved,
    NotWithEvents,
    NotEvent,
    Indeterminate
}

internal enum VbaEventHandlerValidationAuthority
{
    SourceDeclared,
    CurrentIntrinsicHostCatalog,
    ExternalTypeLibAdvisory
}

internal abstract record VbaResolvedEventContractIdentity;

internal sealed record VbaDefinitionEventContractIdentity(
    VbaDefinitionIdentity DefinitionIdentity)
    : VbaResolvedEventContractIdentity;

internal sealed record VbaProjectedEventContractIdentity(
    string Provider,
    string StableIdentity)
    : VbaResolvedEventContractIdentity
{
    public bool Equals(VbaProjectedEventContractIdentity? other)
        => other is not null
            && Provider.Equals(other.Provider, StringComparison.OrdinalIgnoreCase)
            && StableIdentity.Equals(
                other.StableIdentity,
                StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode()
        => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(Provider),
            StringComparer.OrdinalIgnoreCase.GetHashCode(StableIdentity));
}

internal sealed record VbaResolvedEventParameterTypeEvidence(
    string DisplayName,
    string? ReferenceQualifiedDisplayName,
    object Identity);

internal sealed record VbaIntrinsicParameterTypeIdentity(string Name)
{
    public bool Equals(VbaIntrinsicParameterTypeIdentity? other)
        => other is not null
            && Name.Equals(other.Name, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode()
        => StringComparer.OrdinalIgnoreCase.GetHashCode(Name);
}

internal sealed record VbaTypeLibraryParameterTypeIdentity(
    string Name,
    Guid LibraryGuid,
    int MajorVersion,
    int MinorVersion,
    int Lcid)
{
    public bool Equals(VbaTypeLibraryParameterTypeIdentity? other)
        => other is not null
            && Name.Equals(other.Name, StringComparison.OrdinalIgnoreCase)
            && LibraryGuid == other.LibraryGuid
            && MajorVersion == other.MajorVersion
            && MinorVersion == other.MinorVersion
            && Lcid == other.Lcid;

    public override int GetHashCode()
        => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(Name),
            LibraryGuid,
            MajorVersion,
            MinorVersion,
            Lcid);
}

internal sealed record VbaResolvedEventContract(
    VbaResolvedEventContractIdentity Identity,
    string Name,
    VbaCallableSignature? Signature,
    string? Documentation,
    VbaEventHandlerValidationAuthority ValidationAuthority,
    bool IsConditionalContract,
    bool IsAuthoringAvailable = true,
    VbaSourceDefinition? Definition = null,
    VbaDefinitionLocation? NavigableLocation = null,
    IReadOnlyList<VbaResolvedEventParameterTypeEvidence?>?
        ParameterTypeEvidence = null)
{
    public bool IsDiagnosticAuthoritative
        => ValidationAuthority is
            VbaEventHandlerValidationAuthority.SourceDeclared
                or VbaEventHandlerValidationAuthority.CurrentIntrinsicHostCatalog;
}

internal sealed record VbaResolvedEventSignatureSet(
    IReadOnlyList<VbaResolvedEventContract> Contracts,
    bool HasRecoveredEventEvidence);

internal sealed record VbaWithEventsEventBindingEntry(
    VbaSourceDefinition Variable,
    VbaWithEventsEventBindingStatus Status,
    VbaResolvedNameTarget? EventTarget = null,
    IReadOnlyList<VbaResolvedEventContract>? EventContracts = null,
    bool HasRecoveredEventEvidence = false,
    IReadOnlyList<VbaResolvedNameTarget>? EventTargets = null)
{
    public IReadOnlyList<VbaResolvedNameTarget> ResolvedEventTargets
        => EventTargets
            ?? (EventTarget is null ? [] : [EventTarget]);

    public bool IsDiagnosticAuthoritative
        => !HasRecoveredEventEvidence
            && EventContracts is { Count: > 0 }
            && EventContracts.All(contract => contract.IsDiagnosticAuthoritative);
}

internal sealed record VbaWithEventsEventBindingSet(
    VbaResolvedNameTarget VariableTarget,
    IReadOnlyList<VbaWithEventsEventBindingEntry> Entries)
{
    public IReadOnlyList<VbaWithEventsEventBindingEntry> ResolvedEntries
        => Entries
            .Where(entry => entry.Status == VbaWithEventsEventBindingStatus.Resolved)
            .ToArray();

    public bool IsFullyDiagnosticAuthoritative
        => Entries.Count > 0
            && Entries.All(entry =>
                entry.Status == VbaWithEventsEventBindingStatus.Resolved
                && entry.IsDiagnosticAuthoritative);

    public VbaResolvedEventSignatureSet? ResolvedEventSignatures
    {
        get
        {
            var resolvedEntries = ResolvedEntries;
            var contracts = resolvedEntries
                .SelectMany(entry => entry.EventContracts ?? [])
                .ToArray();
            return contracts.Length == 0
                ? null
                : new VbaResolvedEventSignatureSet(
                    contracts,
                    resolvedEntries.Any(entry => entry.HasRecoveredEventEvidence));
        }
    }
}

internal enum VbaWithEventsHandlerRecognition
{
    ResolvedHandler,
    NonSubProcedureAssociation,
    OrdinaryProcedure,
    IndeterminateCandidate
}

internal sealed record VbaWithEventsHandlerAnalysis(
    VbaSourceDefinition Handler,
    VbaWithEventsHandlerNameDecomposition Decomposition,
    VbaWithEventsEventBindingSet BindingSet,
    VbaWithEventsHandlerRecognition Recognition,
    VbaWithEventsEventNameTarget? EventTarget);

internal enum VbaHandlerEventRenameConvergenceKind
{
    Convergent,
    NotCandidate,
    ConflictingTargets,
    Indeterminate
}

internal sealed record VbaHandlerEventRenameConvergence(
    VbaWithEventsHandlerAnalysis HandlerAnalysis,
    VbaHandlerEventRenameConvergenceKind Kind,
    VbaResolvedNameTarget? EventTarget);

internal sealed record VbaWithEventsDependentRenameTarget(
    VbaResolvedNameTarget Target,
    IReadOnlyList<VbaHandlerEventRenameConvergence> Associations);

internal enum VbaConditionalDependentRenameCoverageKind
{
    CompleteDependent,
    ConclusiveMixed,
    IndeterminateCoverage
}

internal sealed record VbaConditionalDependentRenameCoverage(
    VbaResolvedNameTarget Target,
    VbaConditionalDependentRenameCoverageKind Kind,
    IReadOnlyList<VbaSourceDefinition> PhysicalDefinitions,
    IReadOnlyList<VbaHandlerEventRenameConvergence> Associations);

internal sealed record VbaEventHandlerSignatureCompatibility(
    VbaResolvedEventContract EventContract,
    VbaCallableContractComparisonResult Comparison,
    bool IsConditionalContract)
{
    public VbaCallableContractComparisonState State => Comparison.State;

    public IReadOnlyList<string> MismatchReasons
        => VbaCallableContractComparisonFormatter.FormatMismatchReasons(Comparison);
}

internal sealed record VbaEventHandlerCompatibility(
    IReadOnlyList<VbaEventHandlerSignatureCompatibility> Signatures,
    bool HasRecoveredEventEvidence)
{
    public bool ShouldReportDiagnostic
        => !HasRecoveredEventEvidence
            && Signatures.Count > 0
            && Signatures.All(signature =>
                signature.State == VbaCallableContractComparisonState.Incompatible);

    public IReadOnlyList<VbaDiagnosticDetail> CreateDiagnosticDetails()
        => Signatures
            .Where(signature =>
                signature.State == VbaCallableContractComparisonState.Incompatible)
            .Select(signature =>
            {
                var conditionalMarker = signature.IsConditionalContract
                    ? " [#If]"
                    : "";
                var signatureLabel = signature.EventContract.Signature!.Label
                    + conditionalMarker;
                var reasons = string.Join("; ", signature.MismatchReasons);
                return new VbaDiagnosticDetail(
                    signature.EventContract.NavigableLocation is { } location
                        ? new VbaDiagnosticLocation(location.Uri, location.Range)
                        : null,
                    $"Required contract: {signatureLabel}. Mismatches: {reasons}.",
                    $"Expected signature: {signatureLabel}.\nMismatches: {reasons}.");
            })
            .ToArray();
}

internal sealed record VbaWithEventsHandlerNameDecomposition(
    string VariableName,
    string EventName)
{
    public static bool TryCreate(
        string declarationName,
        out VbaWithEventsHandlerNameDecomposition decomposition)
    {
        decomposition = default!;
        var separatorIndex = declarationName.LastIndexOf('_');
        if (separatorIndex <= 0 || separatorIndex == declarationName.Length - 1)
        {
            return false;
        }

        var variableName = declarationName[..separatorIndex];
        var eventName = declarationName[(separatorIndex + 1)..];
        if (!VbaTools.Syntax.VbaIdentifier.IsIdentifier(variableName)
            || !VbaTools.Syntax.VbaIdentifier.IsIdentifier(eventName))
        {
            return false;
        }

        decomposition = new VbaWithEventsHandlerNameDecomposition(
            variableName,
            eventName);
        return true;
    }
}

internal sealed class VbaWithEventsSemanticModel
{
    private readonly VbaNameResolutionService nameResolution;
    private readonly VbaIntrinsicHostEventSemanticModel intrinsicHostEvents;
    private readonly IReadOnlyDictionary<string, VbaProjectReferenceCatalogIdentity>
        referenceCatalogIdentities;

    public VbaWithEventsSemanticModel(
        VbaNameResolutionService nameResolution,
        VbaIntrinsicHostEventSemanticModel? intrinsicHostEvents = null,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogIdentity>?
            referenceCatalogIdentities = null)
    {
        this.nameResolution = nameResolution;
        this.intrinsicHostEvents = intrinsicHostEvents
            ?? new VbaIntrinsicHostEventSemanticModel(catalog: null);
        this.referenceCatalogIdentities = referenceCatalogIdentities
            ?? new Dictionary<string, VbaProjectReferenceCatalogIdentity>(
                VbaProjectReferenceName.Comparer);
    }

    public VbaHandlerEventRenameConvergence
        AnalyzeHandlerEventRenameConvergence(
            VbaWithEventsHandlerAnalysis handlerAnalysis)
    {
        var bindingSet = handlerAnalysis.BindingSet;
        if (nameResolution.HasIndeterminateConditionalCompilationOwnership(
                handlerAnalysis.Handler)
            || bindingSet.Entries.Any(entry =>
                entry.Status == VbaWithEventsEventBindingStatus.Indeterminate
                || entry.HasRecoveredEventEvidence))
        {
            return new VbaHandlerEventRenameConvergence(
                handlerAnalysis,
                VbaHandlerEventRenameConvergenceKind.Indeterminate,
                EventTarget: null);
        }

        var resolvedEntries = bindingSet.ResolvedEntries;
        var eventTargets = resolvedEntries
            .SelectMany(entry => entry.ResolvedEventTargets)
            .DistinctBy(target => target.Identity)
            .ToArray();
        if (eventTargets.Length == 0)
        {
            return new VbaHandlerEventRenameConvergence(
                handlerAnalysis,
                VbaHandlerEventRenameConvergenceKind.NotCandidate,
                EventTarget: null);
        }

        var sourceEventTargets = eventTargets
            .Where(target => target is not VbaHostEventNameTarget
                && target.PhysicalDefinitions.Count > 0
                && target.PhysicalDefinitions.All(definition =>
                    definition.Identity.Origin == VbaDefinitionOrigin.Source
                    && definition.Kind == VbaSourceDefinitionKind.Event))
            .ToArray();
        if (sourceEventTargets.Length == 0)
        {
            return new VbaHandlerEventRenameConvergence(
                handlerAnalysis,
                VbaHandlerEventRenameConvergenceKind.NotCandidate,
                EventTarget: null);
        }

        var targetlessResolvedEntries = resolvedEntries
            .Where(entry => entry.ResolvedEventTargets.Count == 0)
            .ToArray();
        var hasConclusiveExternalTypeLibAssociation =
            targetlessResolvedEntries.Any(entry =>
                entry.EventContracts is { Count: > 0 }
                && entry.EventContracts.All(contract =>
                    contract.ValidationAuthority
                        == VbaEventHandlerValidationAuthority
                            .ExternalTypeLibAdvisory));
        var hasIndeterminateTargetlessAssociation =
            targetlessResolvedEntries.Any(entry =>
                entry.EventContracts is not { Count: > 0 }
                || entry.EventContracts.Any(contract =>
                    contract.ValidationAuthority
                        != VbaEventHandlerValidationAuthority
                            .ExternalTypeLibAdvisory));
        if (eventTargets.Any(target => target is VbaHostEventNameTarget)
            || hasIndeterminateTargetlessAssociation)
        {
            return new VbaHandlerEventRenameConvergence(
                handlerAnalysis,
                VbaHandlerEventRenameConvergenceKind.Indeterminate,
                EventTarget: null);
        }

        if (sourceEventTargets.Length != eventTargets.Length
            || hasConclusiveExternalTypeLibAssociation
            || sourceEventTargets.Length > 1)
        {
            return new VbaHandlerEventRenameConvergence(
                handlerAnalysis,
                VbaHandlerEventRenameConvergenceKind.ConflictingTargets,
                EventTarget: null);
        }

        return sourceEventTargets.Length == 1
            ? new VbaHandlerEventRenameConvergence(
                handlerAnalysis,
                VbaHandlerEventRenameConvergenceKind.Convergent,
                sourceEventTargets[0])
            : new VbaHandlerEventRenameConvergence(
                handlerAnalysis,
                VbaHandlerEventRenameConvergenceKind.ConflictingTargets,
                EventTarget: null);
    }

    public IReadOnlyList<VbaResolvedEventContract>
        CreateTypeLibEventContracts(
            string? referenceName,
            string typeName,
            VbaTypeLibEventSurface eventSurface,
            string eventName,
            bool isConditionalBinding)
    {
        if (referenceName is null
            || eventSurface.State != VbaTypeLibEventSurfaceState.Complete)
        {
            return [];
        }

        referenceCatalogIdentities.TryGetValue(
            referenceName,
            out var catalogIdentity);
        return eventSurface.ExistingHandlerRecognitionEvents
            .Where(member => member.Name.Equals(
                eventName,
                StringComparison.OrdinalIgnoreCase))
            .Select(member => new VbaResolvedEventContract(
                new VbaProjectedEventContractIdentity(
                    "typeLib",
                    string.Join(
                        '\u001f',
                        catalogIdentity?.Guid ?? referenceName,
                        catalogIdentity?.MajorVersion.ToString() ?? "",
                        catalogIdentity?.MinorVersion.ToString() ?? "",
                        catalogIdentity?.Lcid.ToString() ?? "",
                        typeName,
                        member.Metadata?.MemberId.ToString() ?? "",
                        member.Name)),
                member.Name,
                member.Signature,
                member.Documentation,
                VbaEventHandlerValidationAuthority.ExternalTypeLibAdvisory,
                isConditionalBinding,
                TypeLibCatalogMemberFacts.IsAuthoringAvailable(member)))
            .ToArray();
    }

    public VbaWithEventsTypeEligibility? ClassifyType(
        VbaSourceDocument currentDocument,
        VbaSourceDefinition variable)
    {
        if (!variable.IsWithEvents
            || variable.IsRecoveredWithEventsVariableDeclaration
            || variable.TypeReference is null)
        {
            return null;
        }

        if (nameResolution
            .HasIndeterminateConditionalCompilationOwnership(variable))
        {
            return new VbaWithEventsTypeEligibility(
                VbaWithEventsTypeEligibilityKind.Indeterminate);
        }

        if (variable.TypeReference.Qualifier is null
            && VbaLanguageVocabulary.TryGetCanonicalTypeName(
                variable.TypeReference.Name,
                out _))
        {
            return new VbaWithEventsTypeEligibility(
                VbaWithEventsTypeEligibilityKind.InvalidNotClass);
        }

        var outcome = nameResolution.ResolveTypeDefinitionOutcome(
            currentDocument,
            variable.TypeReference);
        if (outcome.Kind != VbaNameResolutionKind.Resolved
            || outcome.Target is null)
        {
            return new VbaWithEventsTypeEligibility(
                VbaWithEventsTypeEligibilityKind.Indeterminate);
        }

        var typeDefinition = outcome.Target.SelectedDefinition;
        var enclosingClass = currentDocument.Definitions.FirstOrDefault(definition =>
            definition.Identity.Origin == VbaDefinitionOrigin.Source
            && definition.Kind is VbaSourceDefinitionKind.Class or VbaSourceDefinitionKind.Form
            && definition.Name.Equals(
                currentDocument.ModuleName,
                StringComparison.OrdinalIgnoreCase));
        if (enclosingClass is not null
            && typeDefinition.Identity == enclosingClass.Identity)
        {
            return new VbaWithEventsTypeEligibility(
                VbaWithEventsTypeEligibilityKind.InvalidEnclosingClass,
                typeDefinition);
        }

        if (typeDefinition.Identity.Origin == VbaDefinitionOrigin.ProjectReference)
        {
            var surface = nameResolution.GetTypeLibEventSurface(
                typeDefinition.ModuleName,
                typeDefinition.Name);
            if (surface.State != VbaTypeLibEventSurfaceState.Complete)
            {
                return new VbaWithEventsTypeEligibility(
                    VbaWithEventsTypeEligibilityKind.Indeterminate,
                    typeDefinition,
                    surface);
            }

            if (surface.RawTypeKind != TypeLibCatalogRawTypeKind.CoClass)
            {
                return new VbaWithEventsTypeEligibility(
                    VbaWithEventsTypeEligibilityKind.InvalidNotClass,
                    typeDefinition,
                    surface);
            }

            const int restrictedTypeFlag = 0x200;
            if ((surface.TypeFlags & restrictedTypeFlag) != 0)
            {
                return new VbaWithEventsTypeEligibility(
                    VbaWithEventsTypeEligibilityKind.InvalidInaccessibleType,
                    typeDefinition,
                    surface);
            }

            return new VbaWithEventsTypeEligibility(
                surface.StructuralEvents.Count == 0
                    ? VbaWithEventsTypeEligibilityKind.InvalidNoEvents
                    : VbaWithEventsTypeEligibilityKind.Eligible,
                typeDefinition,
                surface);
        }

        if (typeDefinition.Kind is not (
                VbaSourceDefinitionKind.Class or VbaSourceDefinitionKind.Form))
        {
            return new VbaWithEventsTypeEligibility(
                VbaWithEventsTypeEligibilityKind.InvalidNotClass,
                typeDefinition);
        }

        var resolvedType = new VbaResolvedType(
            typeDefinition.Name,
            ReferenceName: null,
            typeDefinition);
        var typeDocument = nameResolution.FindDocument(typeDefinition.Uri);
        var hostSurface = typeDocument is not null
            && intrinsicHostEvents.TryGetEffectiveSurface(
                typeDocument,
                out var effectiveHostSurface)
            ? effectiveHostSurface
            : null;
        var sourceEvents = nameResolution
            .GetPhysicalMembersOfType(resolvedType)
            .Where(member => member.Kind == VbaSourceDefinitionKind.Event)
            .ToArray();
        var hasIncompleteEventSurface = nameResolution
            .HasIncompleteSourceEventSurfaceEvidence(typeDefinition.Uri);
        if (!hasIncompleteEventSurface
            && sourceEvents.Any(eventDefinition =>
                !eventDefinition.IsRecoveredEventDeclaration
                && !nameResolution
                    .HasIndeterminateConditionalCompilationOwnership(
                        eventDefinition)))
        {
            return new VbaWithEventsTypeEligibility(
                VbaWithEventsTypeEligibilityKind.Eligible,
                typeDefinition,
                IntrinsicHostEventSurface: hostSurface);
        }

        if (sourceEvents.Length > 0 || hasIncompleteEventSurface)
        {
            return new VbaWithEventsTypeEligibility(
                VbaWithEventsTypeEligibilityKind.Indeterminate,
                typeDefinition,
                IntrinsicHostEventSurface: hostSurface);
        }

        if (hostSurface is not null)
        {
            return new VbaWithEventsTypeEligibility(
                VbaWithEventsTypeEligibilityKind.Eligible,
                typeDefinition,
                IntrinsicHostEventSurface: hostSurface);
        }

        if (typeDefinition.Kind == VbaSourceDefinitionKind.Form)
        {
            return new VbaWithEventsTypeEligibility(
                VbaWithEventsTypeEligibilityKind.Indeterminate,
                typeDefinition);
        }

        return new VbaWithEventsTypeEligibility(
            VbaWithEventsTypeEligibilityKind.InvalidNoEvents,
            typeDefinition);
    }

    public VbaEventHandlerCompatibility AnalyzeHandlerCompatibility(
        VbaSourceDocument currentDocument,
        VbaWithEventsHandlerAnalysis handlerAnalysis)
        => AnalyzeHandlerCompatibility(
            currentDocument,
            handlerAnalysis.Handler,
            handlerAnalysis.BindingSet.ResolvedEventSignatures);

    public VbaEventHandlerCompatibility AnalyzeHandlerCompatibility(
        VbaSourceDocument currentDocument,
        VbaIntrinsicHostHandlerAnalysis handlerAnalysis)
        => AnalyzeHandlerCompatibility(
            currentDocument,
            handlerAnalysis.Handler,
            new VbaResolvedEventSignatureSet(
                [handlerAnalysis.EventTarget.EventContract],
                HasRecoveredEventEvidence: false));

    private VbaEventHandlerCompatibility AnalyzeHandlerCompatibility(
        VbaSourceDocument currentDocument,
        VbaSourceDefinition handler,
        VbaResolvedEventSignatureSet? signatureSet)
    {
        var handlerSignature = handler.Signature;
        if (handlerSignature is null
            || handler.CallableKind != VbaCallableKind.Sub)
        {
            return new VbaEventHandlerCompatibility([], false);
        }

        var comparisons = new List<VbaEventHandlerSignatureCompatibility>();
        if (signatureSet is null)
        {
            return new VbaEventHandlerCompatibility([], false);
        }

        foreach (var eventContract in signatureSet.Contracts)
        {
            comparisons.Add(CompareSignatures(
                currentDocument,
                handler,
                handlerSignature,
                eventContract));
        }

        return new VbaEventHandlerCompatibility(
            comparisons,
            signatureSet.HasRecoveredEventEvidence);
    }

    private VbaEventHandlerSignatureCompatibility CompareSignatures(
        VbaSourceDocument currentDocument,
        VbaSourceDefinition handler,
        VbaCallableSignature handlerSignature,
        VbaResolvedEventContract eventContract)
    {
        var eventSignature = eventContract.Signature;
        if (eventSignature is null)
        {
            return new VbaEventHandlerSignatureCompatibility(
                eventContract,
                VbaCallableContractComparisonResult
                    .UnavailableContractEvidence(),
                eventContract.IsConditionalContract);
        }

        var expected = new VbaCallableContract(
            eventSignature.Parameters
                .Select((parameter, index) => CreateEventContractParameter(
                    eventContract,
                    index,
                    parameter))
                .ToArray());
        var found = new VbaCallableContract(
            handlerSignature.Parameters
                .Select(parameter => CreateHandlerContractParameter(
                    handler,
                    parameter))
                .ToArray());
        var comparison = VbaCallableContractComparison.Compare(
            expected,
            found,
            VbaCallableContractComparisonPolicy.EventHandler);

        return new VbaEventHandlerSignatureCompatibility(
            eventContract,
            comparison,
            eventContract.IsConditionalContract);
    }

    private VbaCallableContractParameter CreateEventContractParameter(
        VbaResolvedEventContract eventContract,
        int parameterIndex,
        VbaCallableParameter parameter)
    {
        var type = TryGetEventContractParameterType(
                eventContract,
                parameterIndex,
                parameter,
                out var evidence)
            ? CreateCallableContractType(evidence)
            : null;
        var hasAuthoritativeDefaultAbsence =
            eventContract.ValidationAuthority
                == VbaEventHandlerValidationAuthority.SourceDeclared;
        return CreateCallableContractParameter(
            parameter,
            type,
            hasAuthoritativeDefaultAbsence);
    }

    private VbaCallableContractParameter CreateHandlerContractParameter(
        VbaSourceDefinition handler,
        VbaCallableParameter parameter)
        => CreateCallableContractParameter(
            parameter,
            TryGetCanonicalParameterType(handler, parameter, out var evidence)
                ? CreateCallableContractType(evidence)
                : null,
            hasAuthoritativeDefaultAbsence: true);

    private static VbaCallableContractParameter CreateCallableContractParameter(
        VbaCallableParameter parameter,
        VbaCallableContractType? type,
        bool hasAuthoritativeDefaultAbsence)
        => new(
            type,
            parameter.IsArray,
            parameter.IsByRef,
            parameter.IsParamArray
                ? VbaCallableContractParameterRole.ParamArray
                : parameter.IsOptional
                    ? VbaCallableContractParameterRole.Optional
                    : VbaCallableContractParameterRole.Required,
            CreateCallableContractDefault(
                parameter,
                hasAuthoritativeDefaultAbsence));

    private static VbaCallableContractDefault CreateCallableContractDefault(
        VbaCallableParameter parameter,
        bool hasAuthoritativeDefaultAbsence)
    {
        if (parameter.DefaultExpression is { } expression)
        {
            return VbaCallableContractDefault.FromExpression(expression);
        }

        return !parameter.IsOptional || hasAuthoritativeDefaultAbsence
            ? VbaCallableContractDefault.Absent
            : VbaCallableContractDefault.Indeterminate;
    }

    private static VbaCallableContractType CreateCallableContractType(
        VbaResolvedEventParameterTypeEvidence evidence)
        => new(
            evidence.DisplayName,
            evidence.Identity,
            evidence.ReferenceQualifiedDisplayName,
            evidence.Identity is VbaTypeLibraryParameterTypeIdentity,
            IsUnmappedProjectReferenceIdentity(evidence.Identity));

    public IReadOnlyList<VbaResolvedEventParameterTypeEvidence?>
        GetParameterTypeEvidence(VbaSourceDefinition eventDefinition)
        => eventDefinition.Signature is null
            ? []
            : eventDefinition.Signature.Parameters
                .Select(parameter => TryGetCanonicalParameterType(
                        eventDefinition,
                        parameter,
                        out var type)
                    ? type
                    : null)
                .ToArray();

    private bool TryGetEventContractParameterType(
        VbaResolvedEventContract eventContract,
        int parameterIndex,
        VbaCallableParameter parameter,
        out VbaResolvedEventParameterTypeEvidence type)
    {
        if (eventContract.ParameterTypeEvidence is not null)
        {
            if (parameterIndex < eventContract.ParameterTypeEvidence.Count
                && eventContract.ParameterTypeEvidence[parameterIndex] is { } evidence)
            {
                type = evidence;
                return true;
            }

            type = default!;
            return false;
        }

        return TryGetCanonicalParameterType(
            eventContract.Definition,
            parameter,
            out type);
    }

    private bool TryGetCanonicalParameterType(
        VbaSourceDefinition? owner,
        VbaCallableParameter parameter,
        out VbaResolvedEventParameterTypeEvidence type)
    {
        if (parameter.TypeReference is null)
        {
            if (owner?.Identity.Origin != VbaDefinitionOrigin.Source)
            {
                type = default!;
                return false;
            }

            type = new VbaResolvedEventParameterTypeEvidence(
                "Variant",
                ReferenceQualifiedDisplayName: null,
                Identity: new VbaIntrinsicParameterTypeIdentity("Variant"));
            return true;
        }

        if (parameter.TypeReference.Qualifier is null
            && VbaLanguageVocabulary.TryGetCanonicalTypeName(
                parameter.TypeReference.Name,
                out var intrinsicName))
        {
            type = new VbaResolvedEventParameterTypeEvidence(
                intrinsicName,
                ReferenceQualifiedDisplayName: null,
                Identity: new VbaIntrinsicParameterTypeIdentity(intrinsicName));
            return true;
        }

        if (owner is null)
        {
            type = default!;
            return false;
        }

        VbaResolvedNameTarget? target;
        if (owner.Identity.Origin == VbaDefinitionOrigin.ProjectReference)
        {
            target = nameResolution.ResolveProjectReferenceTypeDefinition(
                    owner.Identity.ReferenceName ?? owner.ModuleName,
                    parameter.TypeReference) is { } referenceDefinition
                ? new VbaDefinitionNameTarget(referenceDefinition)
                : null;
        }
        else
        {
            var ownerDocument = nameResolution.FindDocument(owner.Uri);
            target = ownerDocument is null
                ? null
                : nameResolution.ResolveTypeDefinitionOutcome(
                        ownerDocument,
                        parameter.TypeReference)
                    .Target;
        }

        if (target is null)
        {
            type = default!;
            return false;
        }

        var definition = target.SelectedDefinition;
        var qualifier = parameter.TypeReference.Qualifier is null
            ? null
            : nameResolution.GetCanonicalQualifierName(
                definition,
                parameter.TypeReference.Qualifier)
                ?? parameter.TypeReference.Qualifier;
        var preferredReferenceQualifier =
            nameResolution.GetPreferredReferenceQualifierName(definition);
        if (owner.Identity.Origin == VbaDefinitionOrigin.ProjectReference
            && parameter.TypeReference.Qualifier is not null
            && nameResolution.IsReferenceQualifierAmbiguous(
                parameter.TypeReference.Qualifier)
            && !string.IsNullOrEmpty(preferredReferenceQualifier))
        {
            qualifier = preferredReferenceQualifier;
        }

        object identity = TryCreateTypeLibraryParameterTypeIdentity(
                definition,
                out var typeLibraryIdentity)
            ? typeLibraryIdentity
            : target.Identity;
        type = new VbaResolvedEventParameterTypeEvidence(
            qualifier is null
                ? target.CanonicalName
                : $"{qualifier}.{target.CanonicalName}",
            !string.IsNullOrEmpty(preferredReferenceQualifier)
                ? $"{preferredReferenceQualifier}.{target.CanonicalName}"
                : null,
            identity);
        return true;
    }

    private bool TryCreateTypeLibraryParameterTypeIdentity(
        VbaSourceDefinition definition,
        out VbaTypeLibraryParameterTypeIdentity identity)
    {
        identity = default!;
        if (definition.Identity.Origin != VbaDefinitionOrigin.ProjectReference)
        {
            return false;
        }

        var referenceName = definition.Identity.ReferenceName
            ?? definition.ModuleName;
        if (!referenceCatalogIdentities.TryGetValue(
                referenceName,
                out var catalogIdentity)
            || !VbaProjectReferenceName.AreEquivalent(
                referenceName,
                catalogIdentity.ReferenceName)
            || !Guid.TryParse(catalogIdentity.Guid, out var libraryGuid))
        {
            return false;
        }

        identity = new VbaTypeLibraryParameterTypeIdentity(
            definition.Name,
            libraryGuid,
            catalogIdentity.MajorVersion,
            catalogIdentity.MinorVersion,
            catalogIdentity.Lcid);
        return true;
    }

    private static bool IsUnmappedProjectReferenceIdentity(object identity)
        => identity is VbaDefinitionNameTargetIdentity
        {
            DefinitionIdentity.Origin: VbaDefinitionOrigin.ProjectReference
        };

}
