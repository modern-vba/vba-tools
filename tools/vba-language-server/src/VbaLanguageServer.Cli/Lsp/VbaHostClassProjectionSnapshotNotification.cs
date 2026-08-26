using System.Text.Json.Nodes;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

internal interface IVbaHostClassProjectionSnapshotHandler
{
    bool TryParse(
        JsonNode? parameters,
        out VbaHostClassProjectionSnapshotUpdate update);

    bool TryApply(VbaHostClassProjectionSnapshotUpdate update);
}

/// <summary>
/// Validates and applies consumer-owned host-class projection snapshot notifications.
/// </summary>
public sealed class VbaHostClassProjectionSnapshotHandler(
    VbaLanguageWorkspace workspace)
    : IVbaHostClassProjectionSnapshotHandler
{
    public const string Method = "vba/hostClassProjectionSnapshot";

    private static readonly HashSet<string> PresentProperties =
    [
        "schemaVersion",
        "revision",
        "project",
        "document",
        "sourceTemplate",
        "state",
        "classEnumerationComplete",
        "classes"
    ];
    private static readonly HashSet<string> ClearedProperties =
    [
        "schemaVersion",
        "revision",
        "project",
        "document",
        "sourceTemplate",
        "state"
    ];

    /// <summary>
    /// Applies a complete schema-1 snapshot when it matches a current manifest document context.
    /// </summary>
    public bool TryApply(JsonNode? parameters)
        => TryParse(parameters, out var update)
            && TryApply(update);

    bool IVbaHostClassProjectionSnapshotHandler.TryParse(
        JsonNode? parameters,
        out VbaHostClassProjectionSnapshotUpdate update)
        => TryParse(parameters, out update);

    bool IVbaHostClassProjectionSnapshotHandler.TryApply(
        VbaHostClassProjectionSnapshotUpdate update)
        => TryApply(update);

    private bool TryApply(VbaHostClassProjectionSnapshotUpdate update)
        => workspace.TryApplyHostClassProjectionSnapshot(update);

    private static bool TryParse(
        JsonNode? parameters,
        out VbaHostClassProjectionSnapshotUpdate update)
    {
        update = default!;
        if (parameters is not JsonObject payload
            || !TryGetInt64(payload["schemaVersion"], out var schemaVersion)
            || schemaVersion != 1
            || !TryGetInt64(payload["revision"], out var revision)
            || revision <= 0
            || !TryGetCanonicalAbsolutePath(payload["project"], out var project)
            || !TryGetNonemptyString(payload["document"], out var document)
            || !TryGetCanonicalAbsolutePath(
                payload["sourceTemplate"],
                out var sourceTemplate)
            || !TryGetString(payload["state"], out var state))
        {
            return false;
        }

        var context = new VbaHostClassProjectionContext(
            project,
            document,
            sourceTemplate);
        if (state == "cleared")
        {
            if (!HasExactProperties(payload, ClearedProperties))
            {
                return false;
            }

            update = new VbaHostClassProjectionSnapshotUpdate(
                context,
                revision,
                Snapshot: null);
            return true;
        }

        if (state != "present"
            || !HasExactProperties(payload, PresentProperties)
            || !TryGetBoolean(
                payload["classEnumerationComplete"],
                out var classEnumerationComplete)
            || payload["classes"] is not JsonArray classesNode
            || !TryParseClasses(classesNode, out var classes))
        {
            return false;
        }

        var snapshot = new VbaHostClassProjectionSnapshot(
            revision,
            context,
            classEnumerationComplete,
            classes);
        update = new VbaHostClassProjectionSnapshotUpdate(
            context,
            revision,
            snapshot);
        return true;
    }

    private static bool HasExactProperties(
        JsonObject value,
        IReadOnlySet<string> expected)
        => value.Count == expected.Count
            && value.Select(property => property.Key).All(expected.Contains);

    private static bool HasOnlyProperties(
        JsonObject value,
        IReadOnlySet<string> allowed)
        => value.Select(property => property.Key).All(allowed.Contains);

    private static bool TryParseClasses(
        JsonArray values,
        out IReadOnlyList<VbaHostClassProjectionEntry> classes)
    {
        var parsed = new List<VbaHostClassProjectionEntry>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (!TryParseProjectionEntry(value, out var entry)
                || !identities.Add(
                    $"{entry.Identity.Kind}\u001e{entry.Identity.Name}"))
            {
                classes = [];
                return false;
            }

            parsed.Add(entry);
        }

        classes = parsed.ToArray();
        return true;
    }

    private static bool TryParseProjectionEntry(
        JsonNode? value,
        out VbaHostClassProjectionEntry entry)
    {
        entry = default!;
        if (value is not JsonObject entryObject
            || !TryParseIdentity(entryObject["identity"], out var identity)
            || !TryGetString(entryObject["authority"], out var authority))
        {
            return false;
        }

        if (authority == "indeterminate")
        {
            if (!HasExactProperties(
                entryObject,
                new HashSet<string> { "identity", "authority" }))
            {
                return false;
            }

            entry = new VbaIndeterminateHostClassProjectionEntry(identity);
            return true;
        }

        if (authority is not ("current" or "lastKnownGood")
            || !HasExactProperties(
                entryObject,
                new HashSet<string>
                {
                    "identity",
                    "authority",
                    "projection"
                })
            || !TryParseProjection(
                entryObject["projection"],
                out var projection))
        {
            return false;
        }

        entry = authority == "current"
            ? new VbaCurrentHostClassProjectionEntry(
                identity,
                projection)
            : new VbaLastKnownGoodHostClassProjectionEntry(
                identity,
                projection);
        return true;
    }

    private static bool TryParseIdentity(
        JsonNode? value,
        out VbaHostClassIdentity identity)
    {
        identity = default!;
        if (value is not JsonObject identityObject
            || !HasExactProperties(
                identityObject,
                new HashSet<string> { "name", "kind" })
            || !TryGetIdentifier(identityObject["name"], out var name)
            || !TryGetString(identityObject["kind"], out var kind))
        {
            return false;
        }

        var parsedKind = kind switch
        {
            "form" => VbaHostClassKind.Form,
            "document" => VbaHostClassKind.Document,
            _ => (VbaHostClassKind?)null
        };
        if (parsedKind is null)
        {
            return false;
        }

        identity = new VbaHostClassIdentity(name, parsedKind.Value);
        return true;
    }

    private static bool TryParseProjection(
        JsonNode? value,
        out VbaHostClassProjection projection)
    {
        projection = default!;
        var allowed = new HashSet<string>
        {
            "intrinsicEventSourceName",
            "events",
            "baseTypeProvenance"
        };
        if (value is not JsonObject projectionObject
            || !HasOnlyProperties(projectionObject, allowed)
            || !TryGetIntrinsicSourceName(
                projectionObject["intrinsicEventSourceName"],
                out var intrinsicEventSourceName)
            || projectionObject["events"] is not JsonArray eventsNode
            || !TryParseEvents(eventsNode, intrinsicEventSourceName, out var events)
            || !TryParseOptionalBaseTypeProvenance(
                projectionObject,
                out var baseTypeProvenance))
        {
            return false;
        }

        projection = new VbaHostClassProjection(
            intrinsicEventSourceName,
            events,
            baseTypeProvenance);
        return true;
    }

    private static bool TryParseEvents(
        JsonArray values,
        string intrinsicEventSourceName,
        out IReadOnlyList<VbaHostEventSignature> events)
    {
        var parsed = new List<VbaHostEventSignature>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (!TryParseEvent(value, intrinsicEventSourceName, out var hostEvent)
                || !names.Add(hostEvent.Name))
            {
                events = [];
                return false;
            }

            parsed.Add(hostEvent);
        }

        events = parsed.ToArray();
        return true;
    }

    private static bool TryParseEvent(
        JsonNode? value,
        string intrinsicEventSourceName,
        out VbaHostEventSignature hostEvent)
    {
        hostEvent = default!;
        var allowed = new HashSet<string>
        {
            "name",
            "parameters",
            "documentation",
            "authoringAvailable",
            "existingHandlerRecognizable"
        };
        if (value is not JsonObject eventObject
            || !HasOnlyProperties(eventObject, allowed)
            || !TryGetExactNonemptyString(eventObject["name"], out var name)
            || eventObject["parameters"] is not JsonArray parametersNode
            || !TryParseParameters(parametersNode, out var parameters)
            || !TryGetOptionalString(
                eventObject,
                "documentation",
                out var documentation)
            || !TryGetBoolean(
                eventObject["authoringAvailable"],
                out var authoringAvailable)
            || !TryGetBoolean(
                eventObject["existingHandlerRecognizable"],
                out var existingHandlerRecognizable))
        {
            return false;
        }

        if ((authoringAvailable || existingHandlerRecognizable)
            && !CanAuthorEvent(intrinsicEventSourceName, name))
        {
            return false;
        }

        hostEvent = new VbaHostEventSignature(
            name,
            parameters,
            documentation,
            authoringAvailable,
            existingHandlerRecognizable);
        return true;
    }

    private static bool CanAuthorEvent(string intrinsicEventSourceName, string eventName)
    {
        if (eventName.EnumerateRunes().Take(256).Count() > 255
            || !VbaIdentifier.IsLexIdentifier(eventName))
        {
            return false;
        }

        var procedureName = $"{intrinsicEventSourceName}_{eventName}";
        return procedureName.Length <= 255 && VbaIdentifier.IsIdentifier(procedureName);
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
            || !TryParseParameterType(
                parameterObject["type"],
                out var type)
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
        JsonObject projection,
        out VbaHostClassBaseTypeProvenance? provenance)
    {
        provenance = null;
        if (!projection.TryGetPropertyValue(
                "baseTypeProvenance",
                out var value))
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

        provenance = new VbaHostClassBaseTypeProvenance(
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

    private static bool TryGetIdentifier(JsonNode? node, out string value)
        => TryGetString(node, out value)
            && VbaIdentifier.IsIdentifier(value);

    private static bool TryGetCanonicalIntrinsicTypeName(
        JsonNode? node,
        out string value)
    {
        value = string.Empty;
        return TryGetString(node, out var candidate)
            && VbaLanguageVocabulary.TryGetCanonicalTypeName(candidate, out value);
    }

    private static bool TryGetIntrinsicSourceName(JsonNode? node, out string value)
        => TryGetIdentifier(node, out value)
            && value.EnumerateRunes().Take(32).Count() <= 31;

    private static bool TryGetCanonicalAbsolutePath(
        JsonNode? node,
        out string value)
    {
        value = "";
        if (!TryGetNonemptyString(node, out var candidate))
        {
            return false;
        }

        try
        {
            if (!Path.IsPathFullyQualified(candidate))
            {
                return false;
            }

            var canonical = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(candidate));
            if (!candidate.Equals(canonical, StringComparison.Ordinal))
            {
                return false;
            }

            value = canonical;
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    private sealed record ParsedTypeLibraryIdentity(
        string Name,
        string LibraryGuid,
        int MajorVersion,
        int MinorVersion,
        int Lcid);
}
