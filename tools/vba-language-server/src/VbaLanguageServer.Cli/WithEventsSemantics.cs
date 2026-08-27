using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Syntax;

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
    VbaHostClassEventSurface? HostClassEventSurface = null);

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
    CurrentHostProjected,
    ExternalTypeLibAdvisory,
    LastKnownGoodHostAdvisory
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
                or VbaEventHandlerValidationAuthority.CurrentHostProjected;
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

internal enum VbaEventHandlerCompatibilityState
{
    Compatible,
    Incompatible,
    Indeterminate
}

internal sealed record VbaEventHandlerSignatureCompatibility(
    VbaResolvedEventContract EventContract,
    VbaEventHandlerCompatibilityState State,
    IReadOnlyList<string> MismatchReasons,
    bool IsConditionalContract);

internal sealed record VbaEventHandlerCompatibility(
    IReadOnlyList<VbaEventHandlerSignatureCompatibility> Signatures,
    bool HasRecoveredEventEvidence)
{
    public bool ShouldReportDiagnostic
        => !HasRecoveredEventEvidence
            && Signatures.Count > 0
            && Signatures.All(signature =>
                signature.State == VbaEventHandlerCompatibilityState.Incompatible);

    public IReadOnlyList<VbaDiagnosticDetail> CreateDiagnosticDetails()
        => Signatures
            .Where(signature =>
                signature.State == VbaEventHandlerCompatibilityState.Incompatible)
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
        if (!VbaLanguageServer.Syntax.VbaIdentifier.IsIdentifier(variableName)
            || !VbaLanguageServer.Syntax.VbaIdentifier.IsIdentifier(eventName))
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
    private readonly VbaHostClassEventSemanticModel hostClassEvents;
    private readonly IReadOnlyDictionary<string, VbaProjectReferenceCatalogIdentity>
        referenceCatalogIdentities;

    public VbaWithEventsSemanticModel(
        VbaNameResolutionService nameResolution,
        VbaHostClassEventSemanticModel? hostClassEvents = null,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogIdentity>?
            referenceCatalogIdentities = null)
    {
        this.nameResolution = nameResolution;
        this.hostClassEvents = hostClassEvents
            ?? new VbaHostClassEventSemanticModel(snapshot: null);
        this.referenceCatalogIdentities = referenceCatalogIdentities
            ?? new Dictionary<string, VbaProjectReferenceCatalogIdentity>(
                VbaProjectReferenceName.Comparer);
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
            && hostClassEvents.TryGetEffectiveSurface(
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
                HostClassEventSurface: hostSurface);
        }

        if (sourceEvents.Length > 0 || hasIncompleteEventSurface)
        {
            return new VbaWithEventsTypeEligibility(
                VbaWithEventsTypeEligibilityKind.Indeterminate,
                typeDefinition,
                HostClassEventSurface: hostSurface);
        }

        if (hostSurface is not null)
        {
            return new VbaWithEventsTypeEligibility(
                hostSurface.Authority == VbaHostClassEventAuthority.Current
                    ? hostSurface.Projection.Events.Count == 0
                        ? VbaWithEventsTypeEligibilityKind.InvalidNoEvents
                        : VbaWithEventsTypeEligibilityKind.Eligible
                    : VbaWithEventsTypeEligibilityKind.Indeterminate,
                typeDefinition,
                HostClassEventSurface: hostSurface);
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
                VbaEventHandlerCompatibilityState.Indeterminate,
                [],
                eventContract.IsConditionalContract);
        }

        var mismatches = new List<string>();
        var hasIndeterminateEvidence = false;
        if (eventSignature.Parameters.Count != handlerSignature.Parameters.Count)
        {
            mismatches.Add(
                $"parameter count: expected {eventSignature.Parameters.Count}, "
                    + $"found {handlerSignature.Parameters.Count}");
        }

        var commonParameterCount = Math.Min(
            eventSignature.Parameters.Count,
            handlerSignature.Parameters.Count);
        for (var index = 0; index < commonParameterCount; index++)
        {
            var expected = eventSignature.Parameters[index];
            var found = handlerSignature.Parameters[index];
            var position = index + 1;
            if (TryGetEventContractParameterType(
                    eventContract,
                    index,
                    expected,
                    out var expectedType)
                && TryGetCanonicalParameterType(
                    handler,
                    found,
                    out var foundType))
            {
                if (!Equals(expectedType.Identity, foundType.Identity)
                    && HasIncompletePortableTypeComparison(
                        expectedType.Identity,
                        foundType.Identity))
                {
                    hasIndeterminateEvidence = true;
                }
                else if (!Equals(expectedType.Identity, foundType.Identity))
                {
                    var diagnosticTypeNames = GetDiagnosticTypeNames(
                        expectedType,
                        foundType);
                    mismatches.Add(
                        $"parameter {position} type: expected "
                            + $"{diagnosticTypeNames.Expected}, "
                            + $"found {diagnosticTypeNames.Found}");
                }
            }
            else
            {
                hasIndeterminateEvidence = true;
            }

            if (expected.IsArray != found.IsArray)
            {
                mismatches.Add(
                    $"parameter {position} array shape: expected "
                        + $"{GetArrayShape(expected.IsArray)}, found {GetArrayShape(found.IsArray)}");
            }

            if (expected.IsByRef is bool expectedByRef
                && found.IsByRef is bool foundByRef)
            {
                if (expectedByRef != foundByRef)
                {
                    mismatches.Add(
                        $"parameter {position} passing: expected "
                            + $"{GetPassingMechanism(expectedByRef)}, "
                            + $"found {GetPassingMechanism(foundByRef)}");
                }
            }
            else
            {
                hasIndeterminateEvidence = true;
            }

            var expectedRole = GetParameterRole(expected);
            var foundRole = GetParameterRole(found);
            if (!expectedRole.Equals(foundRole, StringComparison.Ordinal))
            {
                mismatches.Add(
                    $"parameter {position} role: expected {expectedRole}, "
                        + $"found {foundRole}");
            }
        }

        return new VbaEventHandlerSignatureCompatibility(
            eventContract,
            mismatches.Count > 0
                ? VbaEventHandlerCompatibilityState.Incompatible
                : hasIndeterminateEvidence
                    ? VbaEventHandlerCompatibilityState.Indeterminate
                    : VbaEventHandlerCompatibilityState.Compatible,
            mismatches,
            eventContract.IsConditionalContract);
    }

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

    private static bool HasIncompletePortableTypeComparison(
        object expectedIdentity,
        object foundIdentity)
        => expectedIdentity is VbaTypeLibraryParameterTypeIdentity
                && IsUnmappedProjectReferenceIdentity(foundIdentity)
            || foundIdentity is VbaTypeLibraryParameterTypeIdentity
                && IsUnmappedProjectReferenceIdentity(expectedIdentity);

    private static bool IsUnmappedProjectReferenceIdentity(object identity)
        => identity is VbaDefinitionNameTargetIdentity
        {
            DefinitionIdentity.Origin: VbaDefinitionOrigin.ProjectReference
        };

    private static (string Expected, string Found) GetDiagnosticTypeNames(
        VbaResolvedEventParameterTypeEvidence expected,
        VbaResolvedEventParameterTypeEvidence found)
    {
        if (!expected.DisplayName.Equals(
                found.DisplayName,
                StringComparison.OrdinalIgnoreCase)
            || Equals(expected.Identity, found.Identity))
        {
            return (expected.DisplayName, found.DisplayName);
        }

        return (
            expected.ReferenceQualifiedDisplayName ?? expected.DisplayName,
            found.ReferenceQualifiedDisplayName ?? found.DisplayName);
    }

    private static string GetArrayShape(bool isArray)
        => isArray ? "array" : "scalar";

    private static string GetPassingMechanism(bool isByRef)
        => isByRef ? "ByRef" : "ByVal";

    private static string GetParameterRole(VbaCallableParameter parameter)
        => parameter.IsParamArray
            ? "ParamArray"
            : parameter.IsOptional
                ? "Optional"
                : "required";

}
