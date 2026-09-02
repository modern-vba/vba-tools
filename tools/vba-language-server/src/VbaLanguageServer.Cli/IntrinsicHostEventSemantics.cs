using System.Text;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

internal sealed record VbaHostEventIdentity(
    VbaIntrinsicHostEventSourceKind SourceKind,
    string SourceName,
    string HostModuleUri,
    string HostModuleName,
    string EventName)
{
    public bool Equals(VbaHostEventIdentity? other)
        => other is not null
            && SourceKind == other.SourceKind
            && SourceName.Equals(other.SourceName, StringComparison.OrdinalIgnoreCase)
            && HostModuleUri.Equals(
                other.HostModuleUri,
                StringComparison.OrdinalIgnoreCase)
            && HostModuleName.Equals(
                other.HostModuleName,
                StringComparison.OrdinalIgnoreCase)
            && EventName.Equals(other.EventName, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode()
        => HashCode.Combine(
            SourceKind,
            StringComparer.OrdinalIgnoreCase.GetHashCode(SourceName),
            StringComparer.OrdinalIgnoreCase.GetHashCode(HostModuleUri),
            StringComparer.OrdinalIgnoreCase.GetHashCode(HostModuleName),
            StringComparer.OrdinalIgnoreCase.GetHashCode(EventName));

    public string ToStableIdentity()
        => string.Join(
            "|",
            SourceKind,
            Encode(SourceName),
            Encode(HostModuleUri),
            Encode(HostModuleName),
            Encode(EventName));

    private static string Encode(string value)
        => $"{value.Length}:{value}";
}

internal sealed record VbaIntrinsicHostEventSurface(
    string HostModuleUri,
    string HostModuleName,
    VbaIntrinsicHostEventCatalog Catalog);

internal enum VbaIntrinsicHostHandlerRecognition
{
    ResolvedHandler,
    NonSubProcedureAssociation
}

internal sealed record VbaIntrinsicHostHandlerAnalysis(
    VbaSourceDefinition Handler,
    VbaIntrinsicHostEventSurface Surface,
    VbaIntrinsicHostEvent HostEvent,
    VbaHostEventNameTarget EventTarget,
    VbaIntrinsicHostHandlerRecognition Recognition);

internal sealed class VbaIntrinsicHostEventSemanticModel
{
    private readonly VbaIntrinsicHostEventCatalog? catalog;
    private readonly VbaNameResolutionService? nameResolution;
    private readonly IReadOnlyDictionary<string, VbaProjectReferenceCatalogIdentity>
        referenceCatalogIdentities;

    public VbaIntrinsicHostEventSemanticModel(
        VbaIntrinsicHostEventCatalog? catalog,
        VbaNameResolutionService? nameResolution = null,
        IReadOnlyDictionary<string, VbaProjectReferenceCatalogIdentity>?
            referenceCatalogIdentities = null)
    {
        this.catalog = catalog;
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

        var matchingEvents = surface.Catalog.Events.Where(hostEvent =>
            handler.Name.Equals(
                $"{surface.Catalog.IntrinsicEventSourceName}_{hostEvent.Name}",
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
        VbaIntrinsicHostEventSurface surface,
        string eventName,
        VbaSourceDefinition handler,
        out VbaHostEventNameTarget target)
    {
        target = default!;
        var matchingEvents = surface.Catalog.Events
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
            surface.Catalog.SourceKind,
            hostEvent.Identity.SourceName,
            surface.HostModuleUri,
            surface.HostModuleName,
            hostEvent.Name);
        var signature = CreateSignature(hostEvent);
        var navigableDefinition = ResolveNavigableDefinition(
            surface,
            hostEvent.Name);
        var contract = new VbaResolvedEventContract(
            new VbaProjectedEventContractIdentity(
                "intrinsicHostEventCatalog",
                identity.ToStableIdentity()),
            hostEvent.Name,
            signature,
            hostEvent.Documentation,
            VbaEventHandlerValidationAuthority.CurrentIntrinsicHostCatalog,
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
        VbaIntrinsicHostEventSurface surface,
        VbaIntrinsicHostEvent hostEvent)
        => CreateHandlerSignature(
            surface.Catalog.IntrinsicEventSourceName,
            hostEvent);

    internal static VbaCallableSignature CreateHandlerSignature(
        string eventSourceName,
        VbaIntrinsicHostEvent hostEvent)
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
        VbaIntrinsicHostEvent hostEvent)
        => CreateSignature(hostEvent);

    private VbaSourceDefinition? ResolveNavigableDefinition(
        VbaIntrinsicHostEventSurface surface,
        string eventName)
    {
        if (nameResolution is null
            || surface.Catalog.BaseTypeProvenance is not { } provenance)
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
        CreateParameterTypeEvidence(VbaIntrinsicHostEvent hostEvent)
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
        out VbaIntrinsicHostEventSurface surface)
    {
        surface = default!;
        if (catalog is null
            || catalog.SourceKind != VbaIntrinsicHostEventSourceKind.UserForm
            || !IsCompleteCatalog(catalog))
        {
            return false;
        }

        var syntaxTree = currentDocument.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(currentDocument.Uri, currentDocument.Text);
        if (syntaxTree.Module.Kind != VbaModuleKind.FormModule)
        {
            return false;
        }

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

        surface = new VbaIntrinsicHostEventSurface(
            currentDocument.Uri,
            moduleIdentity.Name,
            catalog);
        return true;
    }

    private static bool IsCompleteCatalog(VbaIntrinsicHostEventCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(catalog.IntrinsicEventSourceName)
            || !VbaIdentifier.IsIdentifier(catalog.IntrinsicEventSourceName)
            || catalog.Events is null
            || catalog.Events.Count == 0)
        {
            return false;
        }

        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hostEvent in catalog.Events)
        {
            if (hostEvent is null
                || hostEvent.Identity is null
                || !hostEvent.Identity.SourceName.Equals(
                    catalog.IntrinsicEventSourceName,
                    StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(hostEvent.Name)
                || !VbaIdentifier.IsIdentifier(hostEvent.Name)
                || hostEvent.Parameters is null
                || !identities.Add(
                    hostEvent.Identity.SourceName + "\0" + hostEvent.Name)
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
        VbaIntrinsicHostEvent hostEvent)
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
