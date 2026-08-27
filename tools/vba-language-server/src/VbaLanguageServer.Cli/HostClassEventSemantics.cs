using System.Text;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

internal enum VbaHostClassEventAuthority
{
    Current,
    LastKnownGood
}

internal sealed record VbaHostEventIdentity(
    string Project,
    string Document,
    string SourceTemplate,
    string HostClassName,
    VbaHostClassKind HostClassKind,
    string EventName)
{
    public bool Equals(VbaHostEventIdentity? other)
        => other is not null
            && Project.Equals(other.Project, StringComparison.OrdinalIgnoreCase)
            && Document.Equals(other.Document, StringComparison.OrdinalIgnoreCase)
            && SourceTemplate.Equals(
                other.SourceTemplate,
                StringComparison.OrdinalIgnoreCase)
            && HostClassName.Equals(
                other.HostClassName,
                StringComparison.OrdinalIgnoreCase)
            && HostClassKind == other.HostClassKind
            && EventName.Equals(other.EventName, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode()
        => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(Project),
            StringComparer.OrdinalIgnoreCase.GetHashCode(Document),
            StringComparer.OrdinalIgnoreCase.GetHashCode(SourceTemplate),
            StringComparer.OrdinalIgnoreCase.GetHashCode(HostClassName),
            HostClassKind,
            StringComparer.OrdinalIgnoreCase.GetHashCode(EventName));

    public string ToStableIdentity()
        => string.Join(
            "|",
            Encode(Project),
            Encode(Document),
            Encode(SourceTemplate),
            Encode(HostClassName),
            HostClassKind,
            Encode(EventName));

    private static string Encode(string value)
        => $"{value.Length}:{value}";
}

internal sealed record VbaHostClassEventSurface(
    VbaHostClassProjectionContext Context,
    VbaHostClassIdentity Identity,
    VbaHostClassProjection Projection,
    VbaHostClassEventAuthority Authority);

internal enum VbaIntrinsicHostHandlerRecognition
{
    ResolvedHandler,
    NonSubProcedureAssociation
}

internal sealed record VbaIntrinsicHostHandlerAnalysis(
    VbaSourceDefinition Handler,
    VbaHostClassEventSurface Surface,
    VbaHostEventSignature HostEvent,
    VbaHostEventNameTarget EventTarget,
    VbaIntrinsicHostHandlerRecognition Recognition);

internal sealed class VbaHostClassEventSemanticModel
{
    private readonly VbaHostClassProjectionSnapshot? snapshot;
    private readonly VbaNameResolutionService? nameResolution;
    private readonly IReadOnlyDictionary<string, VbaProjectReferenceCatalogIdentity>
        referenceCatalogIdentities;

    public VbaHostClassEventSemanticModel(
        VbaHostClassProjectionSnapshot? snapshot,
        VbaNameResolutionService? nameResolution = null,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogIdentity>?
            referenceCatalogIdentities = null)
    {
        this.snapshot = snapshot;
        this.nameResolution = nameResolution;
        this.referenceCatalogIdentities = referenceCatalogIdentities
            ?? new Dictionary<string, VbaProjectReferenceCatalogIdentity>(
                VbaProjectReferenceName.Comparer);
    }

    public VbaIntrinsicHostHandlerAnalysis? AnalyzeIntrinsicHandler(
        VbaSourceDocument currentDocument,
        VbaSourceDefinition handler)
    {
        if (handler.Kind is not (
                VbaSourceDefinitionKind.Procedure
                    or VbaSourceDefinitionKind.Property)
            || !TryGetEffectiveSurface(currentDocument, out var surface))
        {
            return null;
        }

        var matchingEvents = surface.Projection.Events.Where(hostEvent =>
            handler.Name.Equals(
                $"{surface.Projection.IntrinsicEventSourceName}_{hostEvent.Name}",
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matchingEvents.Length != 1
            || !TryCreateExistingHandlerEventTarget(
                surface,
                matchingEvents[0].Name,
                handler,
                out var target))
        {
            return null;
        }

        var hostEvent = matchingEvents[0];
        return new VbaIntrinsicHostHandlerAnalysis(
            handler,
            surface,
            hostEvent,
            target,
            handler.CallableKind == VbaCallableKind.Sub
                ? VbaIntrinsicHostHandlerRecognition.ResolvedHandler
                : VbaIntrinsicHostHandlerRecognition.NonSubProcedureAssociation);
    }

    public bool TryCreateExistingHandlerEventTarget(
        VbaHostClassEventSurface surface,
        string eventName,
        VbaSourceDefinition handler,
        out VbaHostEventNameTarget target)
    {
        target = default!;
        var matchingEvents = surface.Projection.Events
            .Where(hostEvent => hostEvent.ExistingHandlerRecognizable)
            .Where(hostEvent => hostEvent.Name.Equals(
                eventName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matchingEvents.Length != 1)
        {
            return false;
        }

        var hostEvent = matchingEvents[0];
        var identity = new VbaHostEventIdentity(
            surface.Context.Project,
            surface.Context.Document,
            surface.Context.SourceTemplate,
            surface.Identity.Name,
            surface.Identity.Kind,
            hostEvent.Name);
        var signature = CreateSignature(hostEvent);
        var navigableDefinition = ResolveNavigableDefinition(
            surface,
            hostEvent.Name);
        var contract = new VbaResolvedEventContract(
            new VbaProjectedEventContractIdentity(
                "hostClassProjection",
                identity.ToStableIdentity()),
            hostEvent.Name,
            signature,
            hostEvent.Documentation,
            surface.Authority == VbaHostClassEventAuthority.Current
                ? VbaEventHandlerValidationAuthority.CurrentHostProjected
                : VbaEventHandlerValidationAuthority.LastKnownGoodHostAdvisory,
            IsConditionalContract: false,
            hostEvent.AuthoringAvailable,
            Definition: navigableDefinition,
            NavigableLocation: navigableDefinition?.Identity.Origin
                == VbaDefinitionOrigin.Source
                ? navigableDefinition.Location
                : null,
            ParameterTypeEvidence: CreateParameterTypeEvidence(hostEvent));
        target = new VbaHostEventNameTarget(
            identity,
            handler,
            contract,
            navigableDefinition);
        return true;
    }

    internal static VbaCallableSignature CreateHandlerSignature(
        VbaHostClassEventSurface surface,
        VbaHostEventSignature hostEvent)
        => CreateHandlerSignature(
            surface.Projection.IntrinsicEventSourceName,
            hostEvent);

    internal static VbaCallableSignature CreateHandlerSignature(
        string eventSourceName,
        VbaHostEventSignature hostEvent)
    {
        var eventSignature = CreateSignature(hostEvent);
        return eventSignature with
        {
            Label = eventSourceName
                + "_"
                + hostEvent.Name
                + "("
                + string.Join(
                    ", ",
                    eventSignature.Parameters.Select(parameter =>
                        parameter.DisplayLabel ?? parameter.Label))
                + ")",
            Documentation = hostEvent.Documentation,
            CallableKind = VbaCallableKind.Sub
        };
    }

    internal static VbaCallableSignature CreateEventSignature(
        VbaHostEventSignature hostEvent)
        => CreateSignature(hostEvent);

    private VbaSourceDefinition? ResolveNavigableDefinition(
        VbaHostClassEventSurface surface,
        string eventName)
    {
        if (nameResolution is null
            || surface.Projection.BaseTypeProvenance is not { } provenance)
        {
            return null;
        }

        if (!TryResolveReferenceName(
                provenance.LibraryGuid,
                provenance.MajorVersion,
                provenance.MinorVersion,
                provenance.Lcid,
                out var referenceName))
        {
            return null;
        }

        var baseType = nameResolution.ResolveProjectReferenceTypeDefinition(
            referenceName,
            new VbaTypeReference(provenance.Name));
        if (baseType is null)
        {
            return null;
        }

        return nameResolution.ResolveProjectReferenceMemberDefinition(
            referenceName,
            baseType.Name,
            eventName,
            VbaSourceDefinitionKind.Event);
    }

    private IReadOnlyList<VbaResolvedEventParameterTypeEvidence?>
        CreateParameterTypeEvidence(VbaHostEventSignature hostEvent)
        => hostEvent.Parameters
            .Select(parameter => parameter.Type switch
            {
                VbaIntrinsicHostEventParameterType intrinsic
                    => CreateIntrinsicParameterTypeEvidence(intrinsic),
                VbaTypeLibraryHostEventParameterType typeLibrary
                    => CreateTypeLibraryParameterTypeEvidence(typeLibrary),
                _ => null
            })
            .ToArray();

    private static VbaResolvedEventParameterTypeEvidence?
        CreateIntrinsicParameterTypeEvidence(
            VbaIntrinsicHostEventParameterType intrinsic)
    {
        if (!VbaLanguageVocabulary.TryGetCanonicalTypeName(
                intrinsic.Name,
                out var canonicalName))
        {
            return null;
        }

        return new VbaResolvedEventParameterTypeEvidence(
            canonicalName,
            ReferenceQualifiedDisplayName: null,
            new VbaIntrinsicParameterTypeIdentity(canonicalName));
    }

    private VbaResolvedEventParameterTypeEvidence?
        CreateTypeLibraryParameterTypeEvidence(
            VbaTypeLibraryHostEventParameterType typeLibrary)
    {
        if (!Guid.TryParse(typeLibrary.LibraryGuid, out var libraryGuid))
        {
            return null;
        }

        return new VbaResolvedEventParameterTypeEvidence(
            typeLibrary.Name,
            ReferenceQualifiedDisplayName: null,
            new VbaTypeLibraryParameterTypeIdentity(
                typeLibrary.Name,
                libraryGuid,
                typeLibrary.MajorVersion,
                typeLibrary.MinorVersion,
                typeLibrary.Lcid));
    }

    private bool TryResolveReferenceName(
        string libraryGuid,
        int majorVersion,
        int minorVersion,
        int lcid,
        out string referenceName)
    {
        referenceName = string.Empty;
        var matchingIdentities = referenceCatalogIdentities
            .Where(pair => VbaProjectReferenceName.AreEquivalent(
                pair.Key,
                pair.Value.ReferenceName))
            .Where(pair => HaveEquivalentGuids(
                pair.Value.Guid,
                libraryGuid))
            .Where(pair => pair.Value.MajorVersion == majorVersion)
            .Where(pair => pair.Value.MinorVersion == minorVersion)
            .Where(pair => pair.Value.Lcid == lcid)
            .ToArray();
        if (matchingIdentities.Length != 1)
        {
            return false;
        }

        referenceName = matchingIdentities[0].Value.ReferenceName;
        return true;
    }

    private static bool HaveEquivalentGuids(string left, string right)
        => Guid.TryParse(left, out var leftGuid)
            && Guid.TryParse(right, out var rightGuid)
            && leftGuid == rightGuid;

    public bool TryGetEffectiveSurface(
        VbaSourceDocument currentDocument,
        out VbaHostClassEventSurface surface)
    {
        surface = default!;
        if (snapshot is null
            || snapshot.Context is null
            || snapshot.Classes is null
            || string.IsNullOrWhiteSpace(snapshot.Context.Project)
            || string.IsNullOrWhiteSpace(snapshot.Context.Document)
            || string.IsNullOrWhiteSpace(snapshot.Context.SourceTemplate))
        {
            return false;
        }

        var syntaxTree = currentDocument.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(currentDocument.Uri, currentDocument.Text);
        var moduleIdentity = VbaModuleIdentityMetadataReader.Read(
            currentDocument.Text,
            VbaModuleIdentitySourceKind.ObjectModule);
        if (!moduleIdentity.IsAuthoritative
            || !moduleIdentity.Name!.Equals(
                currentDocument.ModuleName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expectedKind = syntaxTree.Module.Kind switch
        {
            VbaModuleKind.FormModule => VbaHostClassKind.Form,
            _ => (VbaHostClassKind?)null
        };
        if (expectedKind is null)
        {
            return false;
        }

        var matchingEntries = snapshot.Classes
            .Where(entry => entry?.Identity is not null)
            .Where(entry => entry.Identity.Kind == expectedKind)
            .Where(entry => entry.Identity.Name.Equals(
                moduleIdentity.Name,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matchingEntries.Length != 1)
        {
            return false;
        }

        var (projection, authority) = matchingEntries[0] switch
        {
            VbaCurrentHostClassProjectionEntry current =>
                (current.Projection, (VbaHostClassEventAuthority?)
                    VbaHostClassEventAuthority.Current),
            VbaLastKnownGoodHostClassProjectionEntry lastKnownGood =>
                (lastKnownGood.Projection, (VbaHostClassEventAuthority?)
                    VbaHostClassEventAuthority.LastKnownGood),
            _ => (null, null)
        };
        if (projection is null
            || authority is null
            || !IsCompleteProjection(projection))
        {
            return false;
        }

        surface = new VbaHostClassEventSurface(
            snapshot.Context,
            matchingEntries[0].Identity,
            projection,
            authority.Value);
        return true;
    }

    private static bool IsCompleteProjection(VbaHostClassProjection projection)
    {
        if (string.IsNullOrWhiteSpace(projection.IntrinsicEventSourceName)
            || !VbaIdentifier.IsIdentifier(projection.IntrinsicEventSourceName)
            || projection.Events is null)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hostEvent in projection.Events)
        {
            if (hostEvent is null
                || string.IsNullOrWhiteSpace(hostEvent.Name)
                || !VbaIdentifier.IsIdentifier(hostEvent.Name)
                || hostEvent.Parameters is null
                || !names.Add(hostEvent.Name)
                || hostEvent.Parameters.Any(parameter =>
                    parameter is null
                    || string.IsNullOrWhiteSpace(parameter.Name)
                    || !VbaIdentifier.IsIdentifier(parameter.Name)
                    || parameter.Type is null))
            {
                return false;
            }
        }

        return true;
    }

    private static VbaCallableSignature CreateSignature(
        VbaHostEventSignature hostEvent)
    {
        var parameters = hostEvent.Parameters
            .Select(CreateParameter)
            .ToArray();
        return new VbaCallableSignature(
            $"Event {hostEvent.Name}({string.Join(", ", parameters.Select(
                parameter => parameter.DisplayLabel))})",
            parameters,
            CallableKind: VbaCallableKind.Event);
    }

    private static VbaCallableParameter CreateParameter(
        VbaHostEventParameter parameter)
    {
        var typeName = parameter.Type switch
        {
            VbaIntrinsicHostEventParameterType intrinsic => intrinsic.Name,
            VbaTypeLibraryHostEventParameterType typeLibrary => typeLibrary.Name,
            VbaUnresolvedHostEventParameterType unresolved => unresolved.DisplayName,
            _ => "Variant"
        };
        var label = new StringBuilder();
        if (parameter.Optional)
        {
            label.Append("Optional ");
        }

        if (parameter.ParamArray)
        {
            label.Append("ParamArray ");
        }

        label.Append(parameter.Passing == VbaHostEventParameterPassing.ByRef
            ? "ByRef "
            : "ByVal ");
        label.Append(parameter.Name);
        if (parameter.ArrayShape == VbaHostEventParameterArrayShape.Array)
        {
            label.Append("()");
        }

        label.Append(" As ");
        label.Append(typeName);
        return new VbaCallableParameter(
            parameter.Name,
            IsOptional: parameter.Optional,
            DisplayLabel: label.ToString(),
            TypeReference: new VbaTypeReference(typeName),
            IsByRef: parameter.Passing == VbaHostEventParameterPassing.ByRef,
            IsParamArray: parameter.ParamArray,
            IsArray: parameter.ArrayShape == VbaHostEventParameterArrayShape.Array);
    }
}
