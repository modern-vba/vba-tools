using System.Text;
using System.Text.Json.Nodes;
using VbaLanguageServer.SourceModel;
using VbaTools.Syntax;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

internal interface IVbaIntrinsicHostEventCatalogHandler
{
    bool TryParse(
        JsonNode? parameters,
        out VbaIntrinsicHostEventCatalogUpdate update);

    bool TryApply(VbaIntrinsicHostEventCatalogUpdate update);
}

/// <summary>
/// Parses and applies environment-scoped intrinsic host Event catalog notifications.
/// </summary>
public sealed class VbaIntrinsicHostEventCatalogHandler(
    VbaLanguageWorkspace workspace)
    : IVbaIntrinsicHostEventCatalogHandler
{
    public const string Method = "vba/intrinsicHostEventCatalog";
    private const string SchemaVersion = "1.0";

    public bool TryApply(JsonNode? parameters)
        => TryParse(parameters, out var update)
            && TryApply(update);

    bool IVbaIntrinsicHostEventCatalogHandler.TryParse(
        JsonNode? parameters,
        out VbaIntrinsicHostEventCatalogUpdate update)
        => TryParse(parameters, out update);

    bool IVbaIntrinsicHostEventCatalogHandler.TryApply(
        VbaIntrinsicHostEventCatalogUpdate update)
        => TryApply(update);

    private bool TryApply(VbaIntrinsicHostEventCatalogUpdate update)
        => workspace.TryApplyIntrinsicHostEventCatalog(update);

    private static bool TryParse(
        JsonNode? parameters,
        out VbaIntrinsicHostEventCatalogUpdate update)
    {
        update = default!;
        if (parameters is not JsonObject root
            || !HasExactProperties(
                root,
                new HashSet<string>
                {
                    "schemaVersion",
                    "revision",
                    "catalog"
                })
            || !TryGetString(root["schemaVersion"], out var schemaVersion)
            || schemaVersion != SchemaVersion
            || !TryGetInt64(root["revision"], out var revision)
            || revision <= 0)
        {
            return false;
        }

        if (root["catalog"] is null)
        {
            update = new VbaIntrinsicHostEventCatalogUpdate(revision, null);
            return true;
        }

        if (!TryParseCatalog(root["catalog"], out var catalog))
        {
            return false;
        }

        update = new VbaIntrinsicHostEventCatalogUpdate(revision, catalog);
        return true;
    }

    private static bool TryParseCatalog(
        JsonNode? value,
        out VbaIntrinsicHostEventCatalog catalog)
    {
        catalog = default!;
        if (value is not JsonObject catalogObject
            || !HasOnlyProperties(
                catalogObject,
                new HashSet<string>
                {
                    "sourceKind",
                    "intrinsicEventSourceName",
                    "events",
                    "baseTypeProvenance"
                })
            || !TryGetString(catalogObject["sourceKind"], out var sourceKind)
            || sourceKind != "userForm"
            || !TryGetIntrinsicSourceName(
                catalogObject["intrinsicEventSourceName"],
                out var intrinsicEventSourceName)
            || catalogObject["events"] is not JsonArray eventNodes
            || !TryParseEvents(
                eventNodes,
                intrinsicEventSourceName,
                out var events)
            || !TryParseOptionalBaseTypeProvenance(
                catalogObject,
                out var baseTypeProvenance))
        {
            return false;
        }

        catalog = new VbaIntrinsicHostEventCatalog(
            VbaIntrinsicHostEventSourceKind.UserForm,
            intrinsicEventSourceName,
            events,
            baseTypeProvenance);
        return true;
    }

    private static bool TryParseEvents(
        JsonArray values,
        string intrinsicEventSourceName,
        out IReadOnlyList<VbaIntrinsicHostEvent> events)
    {
        var parsed = new List<VbaIntrinsicHostEvent>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (!TryParseEvent(value, intrinsicEventSourceName, out var hostEvent)
                || !identities.Add(
                    hostEvent.Identity.SourceName + "\0" + hostEvent.Name))
            {
                events = [];
                return false;
            }

            parsed.Add(hostEvent);
        }

        if (parsed.Count == 0)
        {
            events = [];
            return false;
        }

        events = parsed.ToArray();
        return true;
    }

    private static bool TryParseEvent(
        JsonNode? value,
        string intrinsicEventSourceName,
        out VbaIntrinsicHostEvent hostEvent)
    {
        hostEvent = default!;
        if (value is not JsonObject eventObject
            || !HasExactProperties(
                eventObject,
                new HashSet<string>
                {
                    "identity",
                    "signature",
                    "authoringAvailable",
                    "existingHandlerRecognizable"
                })
            || !TryParseEventIdentity(
                eventObject["identity"],
                intrinsicEventSourceName,
                out var identity)
            || !TryParseEventSignature(
                eventObject["signature"],
                out var signature)
            || !TryGetBoolean(
                eventObject["authoringAvailable"],
                out var authoringAvailable)
            || !TryGetBoolean(
                eventObject["existingHandlerRecognizable"],
                out var existingHandlerRecognizable)
            || (authoringAvailable || existingHandlerRecognizable)
                && !CanAuthorEvent(intrinsicEventSourceName, identity.Name))
        {
            return false;
        }

        hostEvent = new VbaIntrinsicHostEvent(
            identity,
            signature,
            authoringAvailable,
            existingHandlerRecognizable);
        return true;
    }

    private static bool TryParseEventIdentity(
        JsonNode? value,
        string intrinsicEventSourceName,
        out VbaIntrinsicHostEventIdentity identity)
    {
        identity = default!;
        if (value is not JsonObject identityObject
            || !HasExactProperties(
                identityObject,
                new HashSet<string> { "sourceName", "name" })
            || !TryGetIntrinsicSourceName(
                identityObject["sourceName"],
                out var sourceName)
            || !sourceName.Equals(
                intrinsicEventSourceName,
                StringComparison.Ordinal)
            || !TryGetExactNonemptyString(identityObject["name"], out var name))
        {
            return false;
        }

        identity = new VbaIntrinsicHostEventIdentity(sourceName, name);
        return true;
    }

    private static bool TryParseEventSignature(
        JsonNode? value,
        out VbaIntrinsicHostEventSignature signature)
    {
        signature = default!;
        if (value is not JsonObject signatureObject
            || !HasOnlyProperties(
                signatureObject,
                new HashSet<string> { "parameters", "documentation" })
            || signatureObject["parameters"] is not JsonArray parameterNodes
            || !TryParseParameters(parameterNodes, out var parameters)
            || !TryGetOptionalString(
                signatureObject,
                "documentation",
                out var documentation))
        {
            return false;
        }

        signature = new VbaIntrinsicHostEventSignature(parameters, documentation);
        return true;
    }

    private static bool CanAuthorEvent(
        string intrinsicEventSourceName,
        string eventName)
    {
        if (eventName.EnumerateRunes().Take(256).Count() > 255
            || !VbaIdentifier.IsLexIdentifier(eventName))
        {
            return false;
        }

        var procedureName = $"{intrinsicEventSourceName}_{eventName}";
        return procedureName.Length <= 255
            && VbaIdentifier.IsIdentifier(procedureName);
    }

    private static bool TryParseParameters(
        JsonArray values,
        out IReadOnlyList<VbaHostEventParameter> parameters)
    {
        var parsed = new List<VbaHostEventParameter>();
        foreach (var value in values)
        {
            if (!TryParseParameter(value, out var parameter))
            {
                parameters = [];
                return false;
            }

            parsed.Add(parameter);
        }

        parameters = parsed.ToArray();
        return true;
    }

    private static bool TryParseParameter(
        JsonNode? value,
        out VbaHostEventParameter parameter)
    {
        parameter = default!;
        if (value is not JsonObject parameterObject
            || !HasExactProperties(
                parameterObject,
                new HashSet<string>
                {
                    "name",
                    "type",
                    "passing",
                    "arrayShape",
                    "optional",
                    "paramArray"
                })
            || !TryGetExactNonemptyString(parameterObject["name"], out var name)
            || !TryParseParameterType(parameterObject["type"], out var type)
            || !TryGetString(parameterObject["passing"], out var passing)
            || !TryGetString(parameterObject["arrayShape"], out var arrayShape)
            || !TryGetBoolean(parameterObject["optional"], out var optional)
            || !TryGetBoolean(parameterObject["paramArray"], out var paramArray))
        {
            return false;
        }

        var parsedPassing = passing switch
        {
            "byVal" => VbaHostEventParameterPassing.ByVal,
            "byRef" => VbaHostEventParameterPassing.ByRef,
            _ => (VbaHostEventParameterPassing?)null
        };
        var parsedArrayShape = arrayShape switch
        {
            "scalar" => VbaHostEventParameterArrayShape.Scalar,
            "array" => VbaHostEventParameterArrayShape.Array,
            _ => (VbaHostEventParameterArrayShape?)null
        };
        if (parsedPassing is null || parsedArrayShape is null)
        {
            return false;
        }

        parameter = new VbaHostEventParameter(
            name,
            type,
            parsedPassing.Value,
            parsedArrayShape.Value,
            optional,
            paramArray);
        return true;
    }

    private static bool TryParseParameterType(
        JsonNode? value,
        out VbaHostEventParameterType type)
    {
        type = default!;
        if (value is not JsonObject typeObject
            || !TryGetString(typeObject["kind"], out var kind))
        {
            return false;
        }

        if (kind == "intrinsic")
        {
            if (!HasExactProperties(
                    typeObject,
                    new HashSet<string> { "kind", "name" })
                || !TryGetCanonicalIntrinsicTypeName(
                    typeObject["name"],
                    out var name))
            {
                return false;
            }

            type = new VbaIntrinsicHostEventParameterType(name);
            return true;
        }

        if (kind == "unresolved")
        {
            if (!HasExactProperties(
                    typeObject,
                    new HashSet<string> { "kind", "displayName" })
                || !TryGetExactNonemptyString(
                    typeObject["displayName"],
                    out var displayName))
            {
                return false;
            }

            type = new VbaUnresolvedHostEventParameterType(displayName);
            return true;
        }

        if (kind != "typeLib"
            || !TryParseTypeLibraryIdentity(
                typeObject,
                includeKind: true,
                out var identity))
        {
            return false;
        }

        type = new VbaTypeLibraryHostEventParameterType(
            identity.Name,
            identity.LibraryGuid,
            identity.MajorVersion,
            identity.MinorVersion,
            identity.Lcid);
        return true;
    }

    private static bool TryParseOptionalBaseTypeProvenance(
        JsonObject catalog,
        out VbaIntrinsicHostBaseTypeProvenance? provenance)
    {
        provenance = null;
        if (!catalog.TryGetPropertyValue("baseTypeProvenance", out var value))
        {
            return true;
        }

        if (value is not JsonObject provenanceObject
            || !TryParseTypeLibraryIdentity(
                provenanceObject,
                includeKind: false,
                out var identity))
        {
            return false;
        }

        provenance = new VbaIntrinsicHostBaseTypeProvenance(
            identity.Name,
            identity.LibraryGuid,
            identity.MajorVersion,
            identity.MinorVersion,
            identity.Lcid);
        return true;
    }

    private static bool TryParseTypeLibraryIdentity(
        JsonObject value,
        bool includeKind,
        out ParsedTypeLibraryIdentity identity)
    {
        identity = default!;
        var expected = new HashSet<string>
        {
            "name",
            "libraryGuid",
            "majorVersion",
            "minorVersion",
            "lcid"
        };
        if (includeKind)
        {
            expected.Add("kind");
        }

        if (!HasExactProperties(value, expected)
            || !TryGetExactNonemptyString(value["name"], out var name)
            || !TryGetNonemptyString(value["libraryGuid"], out var libraryGuid)
            || !Guid.TryParseExact(libraryGuid, "D", out _)
            || !TryGetNonnegativeInt32(
                value["majorVersion"],
                out var majorVersion)
            || !TryGetNonnegativeInt32(
                value["minorVersion"],
                out var minorVersion)
            || !TryGetNonnegativeInt32(value["lcid"], out var lcid))
        {
            return false;
        }

        identity = new ParsedTypeLibraryIdentity(
            name,
            libraryGuid,
            majorVersion,
            minorVersion,
            lcid);
        return true;
    }

    private static bool HasExactProperties(
        JsonObject value,
        IReadOnlySet<string> expected)
        => value.Count == expected.Count
            && HasOnlyProperties(value, expected);

    private static bool HasOnlyProperties(
        JsonObject value,
        IReadOnlySet<string> allowed)
        => value.All(property => allowed.Contains(property.Key));

    private static bool TryGetOptionalString(
        JsonObject value,
        string property,
        out string? result)
    {
        result = null;
        if (!value.TryGetPropertyValue(property, out var node))
        {
            return true;
        }

        return TryGetString(node, out result!);
    }

    private static bool TryGetInt64(JsonNode? node, out long value)
    {
        value = 0;
        return node is JsonValue jsonValue
            && jsonValue.TryGetValue(out value);
    }

    private static bool TryGetBoolean(JsonNode? node, out bool value)
    {
        value = false;
        return node is JsonValue jsonValue
            && jsonValue.TryGetValue(out value);
    }

    private static bool TryGetNonnegativeInt32(
        JsonNode? node,
        out int value)
    {
        value = 0;
        return node is JsonValue jsonValue
            && jsonValue.TryGetValue(out value)
            && value >= 0;
    }

    private static bool TryGetString(JsonNode? node, out string value)
    {
        value = "";
        return node is JsonValue jsonValue
            && jsonValue.TryGetValue(out value!);
    }

    private static bool TryGetNonemptyString(JsonNode? node, out string value)
        => TryGetString(node, out value)
            && !string.IsNullOrWhiteSpace(value);

    private static bool TryGetExactNonemptyString(JsonNode? node, out string value)
        => TryGetString(node, out value)
            && value.Length > 0
            && !value.Contains('\r', StringComparison.Ordinal)
            && !value.Contains('\n', StringComparison.Ordinal)
            && !VbaIdentifier.IsWhitespaceOnly(value);

    private static bool TryGetCanonicalIntrinsicTypeName(
        JsonNode? node,
        out string value)
    {
        value = string.Empty;
        return TryGetString(node, out var candidate)
            && VbaLanguageVocabulary.TryGetCanonicalTypeName(candidate, out value);
    }

    private static bool TryGetIntrinsicSourceName(
        JsonNode? node,
        out string value)
        => TryGetString(node, out value)
            && VbaIdentifier.IsIdentifier(value)
            && value.EnumerateRunes().Take(32).Count() <= 31;

    private sealed record ParsedTypeLibraryIdentity(
        string Name,
        string LibraryGuid,
        int MajorVersion,
        int MinorVersion,
        int Lcid);
}
