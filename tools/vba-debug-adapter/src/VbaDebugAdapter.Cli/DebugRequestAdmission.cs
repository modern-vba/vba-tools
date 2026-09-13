using System.Collections.Immutable;
using VbaTools.SourceIdentities;
using System.Text.Json;
using VbaDebugAdapter.Build;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;

namespace VbaDebugAdapter.Cli;

/// <summary>
/// Reads all consumed arguments into immutable request evidence before state changes.
/// Transport framing, Restart correlation and source semantics remain with their owners.
/// </summary>
internal static class DebugRequestAdmission
{
    // JSON accepts escaped unpaired surrogates, while its string/name getters reject
    // them later. Keep this classification at those getters, never around admission.
    internal static bool TryReadString(JsonElement value, out string? text)
    {
        text = null;
        if (value.ValueKind != JsonValueKind.String) { return false; }
        try { text = value.GetString(); return true; }
        catch (InvalidOperationException) { return false; }
    }

    internal static bool TryReadPropertyName(JsonProperty property, out string? name)
    {
        name = null;
        try { name = property.Name; return true; }
        catch (InvalidOperationException) { return false; }
    }

    private static string ReadString(JsonElement value)
        => TryReadString(value, out var text) ? text!
            : throw new DebugRequestRejectedException("A consumed request string contains invalid Unicode.");

    private static string ReadPropertyName(JsonProperty property)
        => TryReadPropertyName(property, out var name) ? name!
            : throw new DebugRequestRejectedException("A request property name contains invalid Unicode.");

    internal static StandaloneVbaDebugLaunchRequest AdmitLaunch(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new DebugRequestRejectedException("The VBA launch request requires an object argument.");
        }

        RejectUnsupportedLaunchField(arguments, "args");
        RejectUnsupportedLaunchField(arguments, "noBuild");
        RejectUnsupportedLaunchField(arguments, "stopOnEntry");
        ValidateExactObjectShape(
            arguments,
            "launch",
            requiredProperties:
            [
                "project",
                "document",
                "__vbaDebugWorkbookFileName",
                "sourceSnapshot"
            ],
            optionalProperties:
            [
                "type",
                "request",
                "name",
                "module",
                "procedure",
                "noDebug",
                "__sessionId",
                "__vbaRestartPreparation"
            ]);
        // VS Code adds an opaque client ID. Only the CLI --session lease establishes ownership.
        _ = OptionalExactString(arguments, "__sessionId");
        if (arguments.TryGetProperty("noDebug", out var noDebug))
        {
            if (noDebug.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                throw new DebugRequestRejectedException(
                    "The VBA launch noDebug property must be a boolean.");
            }
            if (noDebug.GetBoolean())
            {
                throw new DebugRequestRejectedException(
                    "The VBA launch request does not support noDebug mode.");
            }
        }

        var projectRoot = RequiredString(arguments, "project");
        string canonicalProjectRoot;
        try
        {
            if (!Path.IsPathFullyQualified(projectRoot))
            {
                throw new DebugRequestRejectedException(
                    "The VBA launch project must be an absolute path.");
            }
            canonicalProjectRoot = Path.GetFullPath(projectRoot);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DebugRequestRejectedException(
                "The VBA launch project must be a valid absolute path.");
        }
        var documentName = RequiredString(arguments, "document");
        var workbookFileName = RequiredString(arguments, "__vbaDebugWorkbookFileName");
        if (workbookFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(Path.GetFileName(workbookFileName), workbookFileName, StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(workbookFileName), ".xlsm", StringComparison.OrdinalIgnoreCase))
        {
            throw new DebugRequestRejectedException(
                "The VBA launch debug workbook name must be a path-free .xlsm file name.");
        }
        var moduleName = OptionalExactString(arguments, "module");
        var procedureName = OptionalExactString(arguments, "procedure");
        if ((moduleName is null) != (procedureName is null))
        {
            throw new DebugRequestRejectedException(
                "The VBA launch request must specify 'module' and 'procedure' together.");
        }
        if (!arguments.TryGetProperty("sourceSnapshot", out var sourceSnapshot) ||
            sourceSnapshot.ValueKind != JsonValueKind.Object ||
            !sourceSnapshot.TryGetProperty("schemaVersion", out var schemaVersion) ||
            schemaVersion.ValueKind != JsonValueKind.Number ||
            !schemaVersion.TryGetInt32(out var schema) ||
            !sourceSnapshot.TryGetProperty("sources", out var sources) ||
            sources.ValueKind != JsonValueKind.Array)
        {
            throw new DebugRequestRejectedException(
                "The VBA launch request requires sourceSnapshot schemaVersion and sources.");
        }

        ValidateExactObjectShape(
            sourceSnapshot,
            "sourceSnapshot",
            requiredProperties: ["schemaVersion", "sources"],
            optionalProperties: ["activeSource", "breakpoints"]);

        var transportedSources = sources.EnumerateArray()
            .Select(ParseTransportedSource)
            .ToImmutableArray();
        var activeSource = sourceSnapshot.TryGetProperty("activeSource", out var activeSourceValue)
            ? ParseTransportedSourcePosition(activeSourceValue)
            : null;
        var breakpoints = sourceSnapshot.TryGetProperty("breakpoints", out var breakpointsValue)
            ? ParseTransportedBreakpoints(breakpointsValue)
            : [];
        var restartPreparation = arguments.TryGetProperty(
            "__vbaRestartPreparation",
            out var restartPreparationValue)
            ? ParseRestartPreparation(restartPreparationValue)
            : null;
        return new StandaloneVbaDebugLaunchRequest(
            canonicalProjectRoot,
            documentName,
            workbookFileName,
            moduleName,
            procedureName,
            new TransportedDebugSourceSnapshot(schema, transportedSources)
            {
                ActiveSource = activeSource,
                Breakpoints = breakpoints
            })
        {
            RestartPreparation = restartPreparation
        };
    }

    private static RestartPreparationDescriptor ParseRestartPreparation(JsonElement preparation)
    {
        if (preparation.ValueKind != JsonValueKind.Object)
        {
            throw new DebugRequestRejectedException(
                "The VBA launch restart preparation must be an object.");
        }

        ValidateExactObjectShape(
            preparation,
            "__vbaRestartPreparation",
            requiredProperties: ["protocolVersion", "id", "generation"],
            optionalProperties: []);
        var protocolVersion = RequiredInt32(preparation, "protocolVersion");
        if (protocolVersion != 1)
        {
            throw new DebugRequestRejectedException(
                $"Unsupported VBA restart preparation protocol version '{protocolVersion}'.");
        }

        var id = RequiredString(preparation, "id");
        if (!IsCanonicalHex32(id))
        {
            throw new DebugRequestRejectedException(
                "The VBA restart preparation ID must contain 32 lowercase hexadecimal characters.");
        }

        var generation = RequiredInt32(preparation, "generation");
        if (generation < 0)
        {
            throw new DebugRequestRejectedException(
                "The VBA restart preparation generation must be nonnegative.");
        }

        return new RestartPreparationDescriptor(
            DebugRestartPreparationId.Parse(id),
            DebugRestartGeneration.FromValue(generation));
    }

    internal static RestartPreparationResult AdmitRestartPreparation(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new DebugRequestRejectedException(
                "The VBA restart preparation result must be an object.");
        }

        ValidateExactObjectShape(
            arguments,
            "vba/restartPrepared",
            requiredProperties:
            [
                "sessionId",
                "restartRequestSequence",
                "preparationId",
                "generation",
                "success"
            ],
            optionalProperties: ["message", "launch"]);
        if (!arguments.TryGetProperty("success", out var successValue) ||
            successValue.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new DebugRequestRejectedException(
                "The VBA restart preparation result requires a Boolean 'success'.");
        }

        var message = OptionalString(arguments, "message");
        var launch = arguments.TryGetProperty("launch", out var launchValue)
            ? AdmitLaunch(launchValue)
            : null;
        if (successValue.GetBoolean() && launch is null)
        {
            throw new DebugRequestRejectedException(
                "A successful VBA restart preparation requires a fresh launch snapshot.");
        }
        return new RestartPreparationResult(successValue.GetBoolean(), message, launch);
    }

    private static bool IsCanonicalHex32(string value)
        => value.Length == 32 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static AdmittedDapSourceBreakpoints AdmitSourceBreakpoints(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("source", out var source) ||
            source.ValueKind != JsonValueKind.Object)
        {
            throw new DebugRequestRejectedException(
                "The setBreakpoints request requires a source object.");
        }
        ValidateConsumedProperties(arguments, "setBreakpoints", ["source", "breakpoints"], ["source", "breakpoints"]);
        ValidateConsumedProperties(source, "setBreakpoints.source", ["path"], ["path"]);
        var sourcePath = RequiredString(source, "path");
        try
        {
            if (!Path.IsPathFullyQualified(sourcePath))
            {
                throw new DebugRequestRejectedException(
                    "The setBreakpoints source path must be absolute.");
            }
            sourcePath = Path.GetFullPath(sourcePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DebugRequestRejectedException(
                "The setBreakpoints source path must be a valid absolute path.");
        }
        if (!arguments.TryGetProperty("breakpoints", out var breakpoints) ||
            breakpoints.ValueKind != JsonValueKind.Array)
        {
            throw new DebugRequestRejectedException(
                "The setBreakpoints request requires a breakpoints array.");
        }

        var requestedBreakpoints = breakpoints.EnumerateArray().Select(breakpoint =>
        {
            if (breakpoint.ValueKind != JsonValueKind.Object)
            {
                throw new DebugRequestRejectedException(
                    "Each source breakpoint must be an object.");
            }
            ValidateConsumedProperties(breakpoint, "source breakpoint", ["line"],
                ["line", "condition", "hitCondition", "logMessage", "column", "mode"]);
            var line = RequiredInt32(breakpoint, "line");
            if (line <= 0)
            {
                throw new DebugRequestRejectedException(
                    "Each source breakpoint line must be a positive one-based line.");
            }
            return new DapSourceBreakpointIntent(
                line,
                HasOptionalString(breakpoint, "condition"),
                HasOptionalString(breakpoint, "hitCondition"),
                HasOptionalString(breakpoint, "logMessage"),
                HasOptionalPositiveInteger(breakpoint, "column"),
                HasOptionalString(breakpoint, "mode"));
        }).ToImmutableArray();
        if (!SourceIdentity.TryFromPath(sourcePath, out var identity))
        {
            throw new DebugRequestRejectedException(
                "The setBreakpoints source path must be a valid absolute path.");
        }
        return new AdmittedDapSourceBreakpoints(sourcePath, identity, requestedBreakpoints);
    }

    private static bool HasOptionalString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property)) { return false; }
        if (!TryReadString(property, out _))
        {
            throw new DebugRequestRejectedException($"The breakpoint property '{propertyName}' must be a string.");
        }
        return true;
    }

    private static bool HasOptionalPositiveInteger(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out _)) { return false; }
        if (RequiredInt32(value, propertyName) <= 0)
        {
            throw new DebugRequestRejectedException($"The breakpoint property '{propertyName}' must be a positive one-based integer.");
        }
        return true;
    }

    private static void RejectUnsupportedLaunchField(
        JsonElement arguments,
        string propertyName)
    {
        if (arguments.TryGetProperty(propertyName, out _))
        {
            throw new DebugRequestRejectedException(
                $"VBA launch does not support '{propertyName}'.");
        }
    }

    internal static AdmittedDapBreakpointConfiguration AdmitBreakpointConfiguration(
        string command,
        JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new DebugRequestRejectedException(
                $"The {command} request requires an object argument.");
        }

        var propertyNames = command.Equals("setExceptionBreakpoints", StringComparison.Ordinal)
            ? new[] { "filters", "filterOptions", "exceptionOptions" }
            : new[] { "breakpoints" };
        ValidateConsumedProperties(arguments, command, [propertyNames[0]], propertyNames);
        var unsupported = false;
        foreach (var propertyName in propertyNames)
        {
            if (!arguments.TryGetProperty(propertyName, out var values))
            {
                continue;
            }
            if (values.ValueKind != JsonValueKind.Array)
            {
                throw new DebugRequestRejectedException(
                    $"The {command} request property '{propertyName}' must be an array.");
            }
            foreach (var value in values.EnumerateArray())
            {
                ValidateBreakpointConfigurationEntry(command, propertyName, value);
            }
            if (values.GetArrayLength() > 0)
            {
                unsupported = true;
            }
        }
        return new AdmittedDapBreakpointConfiguration(command, unsupported);
    }

    private static void ValidateConsumedProperties(JsonElement value, string displayName,
        IReadOnlyList<string> required, IReadOnlyList<string> consumed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var name = ReadPropertyName(property);
            if (consumed.Contains(name, StringComparer.Ordinal) && !seen.Add(name))
            {
                throw new DebugRequestRejectedException($"The {displayName} request contains duplicate property '{name}'.");
            }
        }
        foreach (var name in required)
        {
            if (!seen.Contains(name))
            {
                throw new DebugRequestRejectedException($"The {displayName} request requires '{name}'.");
            }
        }
    }

    private static void ValidateBreakpointConfigurationEntry(string command, string propertyName, JsonElement value)
    {
        if (propertyName == "filters")
        {
            RequireStringValue(value, "filters[]");
            return;
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new DebugRequestRejectedException($"Each {command} {propertyName} entry must be an object.");
        }
        if (command == "setFunctionBreakpoints")
        {
            ValidateConsumedProperties(value, "function breakpoint", ["name"], ["name", "condition", "hitCondition"]);
            _ = RequiredStringAllowEmpty(value, "name");
            _ = HasOptionalString(value, "condition");
            _ = HasOptionalString(value, "hitCondition");
        }
        else if (command == "setDataBreakpoints")
        {
            ValidateConsumedProperties(value, "data breakpoint", ["dataId"], ["dataId", "accessType", "condition", "hitCondition"]);
            _ = RequiredStringAllowEmpty(value, "dataId");
            if (value.TryGetProperty("accessType", out var accessType))
            {
                RequireStringChoice(accessType, "accessType", ["read", "write", "readWrite"]);
            }
            _ = HasOptionalString(value, "condition");
            _ = HasOptionalString(value, "hitCondition");
        }
        else if (propertyName == "filterOptions")
        {
            ValidateConsumedProperties(value, "exception filter option", ["filterId"], ["filterId", "condition", "mode"]);
            _ = RequiredStringAllowEmpty(value, "filterId");
            _ = HasOptionalString(value, "condition");
            _ = HasOptionalString(value, "mode");
        }
        else if (propertyName == "exceptionOptions")
        {
            ValidateConsumedProperties(value, "exception option", ["breakMode"], ["breakMode", "path"]);
            if (!value.TryGetProperty("breakMode", out var breakMode))
            {
                throw new DebugRequestRejectedException("Each exception option requires 'breakMode'.");
            }
            RequireStringChoice(breakMode, "breakMode", ["never", "always", "unhandled", "userUnhandled"]);
            if (value.TryGetProperty("path", out var path))
            {
                if (path.ValueKind != JsonValueKind.Array)
                {
                    throw new DebugRequestRejectedException("The exception option path must be an array.");
                }
                foreach (var segment in path.EnumerateArray())
                {
                    if (segment.ValueKind != JsonValueKind.Object ||
                        !segment.TryGetProperty("names", out var names) || names.ValueKind != JsonValueKind.Array)
                    {
                        throw new DebugRequestRejectedException("Each exception path segment requires a names array.");
                    }
                    ValidateConsumedProperties(segment, "exception path segment", ["names"], ["names", "negate"]);
                    foreach (var name in names.EnumerateArray()) { RequireStringValue(name, "names[]"); }
                    if (segment.TryGetProperty("negate", out var negate) &&
                        negate.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                    {
                        throw new DebugRequestRejectedException("The exception path negate property must be a boolean.");
                    }
                }
            }
        }
    }

    private static string RequireStringValue(JsonElement value, string displayName)
        => value.ValueKind == JsonValueKind.String ? ReadString(value)!
            : throw new DebugRequestRejectedException($"The breakpoint property '{displayName}' must be a string.");

    private static void RequireStringChoice(JsonElement value, string displayName, IReadOnlyList<string> choices)
    {
        if (!choices.Contains(RequireStringValue(value, displayName), StringComparer.Ordinal))
        {
            throw new DebugRequestRejectedException($"The breakpoint property '{displayName}' must be one of {string.Join(", ", choices)}.");
        }
    }

    internal static string UnsupportedBreakpointKind(string command)
        => command switch
        {
            "setFunctionBreakpoints" => "function",
            "setExceptionBreakpoints" => "exception",
            "setDataBreakpoints" => "data",
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };

    private static TransportedDebugSource ParseTransportedSource(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object)
        {
            throw new DebugRequestRejectedException(
                "Each sourceSnapshot source must be an object.");
        }
        ValidateExactObjectShape(
            source,
            "sourceSnapshot.sources[]",
            requiredProperties: ["relativePath", "contentBase64"],
            optionalProperties: ["sourceUri", "encoding"]);
        var relativePath = RequiredString(source, "relativePath");
        var contentBase64 = RequiredStringAllowEmpty(source, "contentBase64");
        return new TransportedDebugSource(
            relativePath,
            OptionalString(source, "sourceUri"),
            OptionalString(source, "encoding"),
            contentBase64);
    }

    private static TransportedDebugSourcePosition ParseTransportedSourcePosition(
        JsonElement position)
    {
        if (position.ValueKind != JsonValueKind.Object)
        {
            throw new DebugRequestRejectedException(
                "The transported active source must be an object.");
        }
        ValidateExactObjectShape(
            position,
            "sourceSnapshot.activeSource",
            requiredProperties: ["sourceUri", "line", "character"],
            optionalProperties: []);
        return new TransportedDebugSourcePosition(
            RequiredString(position, "sourceUri"),
            RequiredNonnegativeInt32(position, "line"),
            RequiredNonnegativeInt32(position, "character"));
    }

    private static IReadOnlyList<TransportedDebugSourceBreakpoint> ParseTransportedBreakpoints(
        JsonElement breakpoints)
    {
        if (breakpoints.ValueKind != JsonValueKind.Array)
        {
            throw new DebugRequestRejectedException(
                "The transported source breakpoints must be an array.");
        }
        return breakpoints.EnumerateArray().Select(breakpoint =>
        {
            if (breakpoint.ValueKind != JsonValueKind.Object)
            {
                throw new DebugRequestRejectedException(
                    "Each transported source breakpoint must be an object.");
            }
            ValidateExactObjectShape(
                breakpoint,
                "sourceSnapshot.breakpoints[]",
                requiredProperties: ["sourceUri", "line"],
                optionalProperties: []);
            return new TransportedDebugSourceBreakpoint(
                RequiredString(breakpoint, "sourceUri"),
                RequiredNonnegativeInt32(breakpoint, "line"));
        }).ToImmutableArray();
    }

    private static string RequiredString(JsonElement value, string propertyName)
        => OptionalString(value, propertyName)
           ?? throw new DebugRequestRejectedException(
               $"The VBA launch request requires string '{propertyName}'.");

    private static string RequiredStringAllowEmpty(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            throw new DebugRequestRejectedException(
                $"The VBA launch request requires string '{propertyName}'.");
        }
        return ReadString(property)!;
    }

    private static void ValidateExactObjectShape(
        JsonElement value,
        string displayName,
        IReadOnlyList<string> requiredProperties,
        IReadOnlyList<string> optionalProperties)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var name = ReadPropertyName(property);
            if (!requiredProperties.Contains(name, StringComparer.Ordinal) &&
                !optionalProperties.Contains(name, StringComparer.Ordinal))
            {
                throw new DebugRequestRejectedException(
                    $"The VBA launch request does not support property '{displayName}.{name}'.");
            }
            if (!seen.Add(name))
            {
                throw new DebugRequestRejectedException(
                    $"The VBA launch request contains duplicate property '{displayName}.{name}'.");
            }
        }

        foreach (var requiredProperty in requiredProperties)
        {
            if (!seen.Contains(requiredProperty))
            {
                throw new DebugRequestRejectedException(
                    $"The VBA launch request requires '{displayName}.{requiredProperty}'.");
            }
        }
    }

    private static int RequiredInt32(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var result))
        {
            throw new DebugRequestRejectedException(
                $"The VBA launch request requires integer '{propertyName}'.");
        }
        return result;
    }

    private static int RequiredNonnegativeInt32(JsonElement value, string propertyName)
    {
        var result = RequiredInt32(value, propertyName);
        if (result < 0)
        {
            throw new DebugRequestRejectedException($"The VBA launch property '{propertyName}' must be a nonnegative integer.");
        }
        return result;
    }

    private static string? OptionalString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }
        var text = property.ValueKind == JsonValueKind.String ? ReadString(property) : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new DebugRequestRejectedException(
                $"The VBA launch request property '{propertyName}' must be a non-empty string.");
        }
        return text;
    }

    private static string? OptionalExactString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }
        var text = property.ValueKind == JsonValueKind.String ? ReadString(property) : null;
        if (string.IsNullOrEmpty(text))
        {
            throw new DebugRequestRejectedException(
                $"The VBA launch request property '{propertyName}' must be a non-empty string.");
        }
        return text;
    }
}

internal sealed record DapSourceBreakpointIntent(
    int Line,
    bool HasCondition,
    bool HasHitCondition,
    bool HasLogMessage,
    bool HasColumn,
    bool HasMode)
{
    public string? UnsupportedFeature =>
        HasCondition ? "conditional breakpoint" :
        HasHitCondition ? "hit-count breakpoint" :
        HasLogMessage ? "log point" :
        HasColumn ? "column breakpoint" :
        HasMode ? "breakpoint mode" :
        null;
}

internal sealed record AdmittedDapSourceBreakpoints(
    string SourcePath,
    SourceIdentity Identity,
    ImmutableArray<DapSourceBreakpointIntent> Breakpoints);

internal sealed record AdmittedDapBreakpointConfiguration(string Command, bool Unsupported);

internal sealed class DebugRequestRejectedException(string message) : DebugSetupException(message);
