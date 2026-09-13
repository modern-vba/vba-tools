using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace VbaTools.Capabilities;

/// <summary>The versions consumed by one caller, copied with ordinal-exact keys.</summary>
public sealed class CapabilityRequirements
{
    /// <summary>Null or empty maps declare no requirements for that capability collection.</summary>
    public CapabilityRequirements(
        IReadOnlyDictionary<string, string>? commandSchemaVersions = null,
        IReadOnlyDictionary<string, string>? featureVersions = null)
    {
        CommandSchemaVersions = Copy(commandSchemaVersions);
        FeatureVersions = Copy(featureVersions);
    }

    public IReadOnlyDictionary<string, string> CommandSchemaVersions { get; }
    public IReadOnlyDictionary<string, string> FeatureVersions { get; }

    private static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string>? source)
        => new ReadOnlyDictionary<string, string>(source is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(source, StringComparer.Ordinal));
}

/// <summary>Only the consumed capability facts, independent of the parsed response lifetime.</summary>
public sealed class CapabilityFacts
{
    internal CapabilityFacts(Dictionary<string, string> commands, Dictionary<string, string> features)
    {
        CommandSchemaVersions = new ReadOnlyDictionary<string, string>(commands);
        FeatureVersions = new ReadOnlyDictionary<string, string>(features);
    }

    public IReadOnlyDictionary<string, string> CommandSchemaVersions { get; }
    public IReadOnlyDictionary<string, string> FeatureVersions { get; }
}

public enum CapabilityRejectionKind
{
    InvalidJson,
    DuplicateProperty,
    MissingCapability,
    InvalidConsumedValue,
    VersionMismatch
}

/// <summary>Stable rejection classification and the relevant consumed field or property.</summary>
public sealed record CapabilityRejection(
    CapabilityRejectionKind Kind, string Field, string? Expected = null, string? Actual = null);

public sealed class CapabilityAdmissionResult
{
    internal CapabilityAdmissionResult(CapabilityFacts facts) => Facts = facts;
    internal CapabilityAdmissionResult(CapabilityRejection rejection) => Rejection = rejection;

    public bool IsAccepted => Facts is not null;
    public CapabilityFacts? Facts { get; }
    public CapabilityRejection? Rejection { get; }
}

/// <summary>Admits public vba-dev capability JSON against the caller's consumed versions.</summary>
public static class VbaDevCapabilityAdmission
{
    /// <summary>Returns copied consumed facts, or the reason the response cannot be admitted.</summary>
    /// <remarks>
    /// JSON validity and ordinal-exact decoded property uniqueness are checked throughout the
    /// response before consumed versions. Unknown values remain uninterpreted. Consumed version
    /// strings must decode successfully and match exactly; no offered ordering is significant.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static CapabilityAdmissionResult Admit(string rawJson, CapabilityRequirements requirements)
    {
        ArgumentNullException.ThrowIfNull(rawJson);
        ArgumentNullException.ThrowIfNull(requirements);
        JsonDocument document;
        try { document = JsonDocument.Parse(rawJson, new JsonDocumentOptions { MaxDepth = int.MaxValue }); }
        catch (Exception exception) when (exception is JsonException or EncoderFallbackException
            or ArgumentException { InnerException: EncoderFallbackException })
        {
            return new(new CapabilityRejection(CapabilityRejectionKind.InvalidJson, "$"));
        }
        using (document) { return AdmitDocument(document.RootElement, requirements); }
    }

    private static CapabilityAdmissionResult AdmitDocument(JsonElement root, CapabilityRequirements requirements)
    {
        if (InspectPropertyNames(root) is { } propertyRejection)
        {
            return new(propertyRejection);
        }
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Reject(CapabilityRejectionKind.InvalidConsumedValue, "$", "Object", root.ValueKind.ToString());
        }
        var commands = new Dictionary<string, string>(StringComparer.Ordinal);
        if (requirements.CommandSchemaVersions.Count != 0)
        {
            if (!root.TryGetProperty("commands", out var offered))
            {
                return Reject(CapabilityRejectionKind.MissingCapability, "commands");
            }
            if (offered.ValueKind != JsonValueKind.Object)
            {
                return Reject(CapabilityRejectionKind.InvalidConsumedValue, "commands", "Object", offered.ValueKind.ToString());
            }
            foreach (var required in requirements.CommandSchemaVersions)
            {
                var field = "commands." + required.Key;
                if (!offered.TryGetProperty(required.Key, out var command))
                {
                    return Reject(CapabilityRejectionKind.MissingCapability, field);
                }
                if (command.ValueKind != JsonValueKind.Object)
                {
                    return Reject(CapabilityRejectionKind.InvalidConsumedValue, field, "Object", command.ValueKind.ToString());
                }
                field += ".outputSchemaVersion";
                if (!command.TryGetProperty("outputSchemaVersion", out var version))
                {
                    return Reject(CapabilityRejectionKind.MissingCapability, field, required.Value);
                }
                if (ReadRequiredVersion(version, field, required.Value, out var actual) is { } rejection)
                {
                    return new(rejection);
                }
                commands.Add(required.Key, actual);
            }
        }
        var features = new Dictionary<string, string>(StringComparer.Ordinal);
        if (requirements.FeatureVersions.Count != 0)
        {
            if (!root.TryGetProperty("featureVersions", out var offered))
            {
                return Reject(CapabilityRejectionKind.MissingCapability, "featureVersions");
            }
            if (offered.ValueKind != JsonValueKind.Object)
            {
                return Reject(CapabilityRejectionKind.InvalidConsumedValue, "featureVersions", "Object", offered.ValueKind.ToString());
            }
            foreach (var required in requirements.FeatureVersions)
            {
                var field = "featureVersions." + required.Key;
                if (!offered.TryGetProperty(required.Key, out var version))
                {
                    return Reject(CapabilityRejectionKind.MissingCapability, field, required.Value);
                }
                if (ReadRequiredVersion(version, field, required.Value, out var actual) is { } rejection)
                {
                    return new(rejection);
                }
                features.Add(required.Key, actual);
            }
        }
        return new(new CapabilityFacts(commands, features));
    }

    private static CapabilityRejection? ReadRequiredVersion(JsonElement version, string field,
        string expected, out string actual)
    {
        actual = string.Empty;
        if (version.ValueKind != JsonValueKind.String)
        {
            return new(CapabilityRejectionKind.InvalidConsumedValue, field, "String", version.ValueKind.ToString());
        }
        try { actual = version.GetString()!; }
        catch (InvalidOperationException)
        {
            return new(CapabilityRejectionKind.InvalidConsumedValue, field, "Decodable string");
        }
        return string.Equals(actual, expected, StringComparison.Ordinal)
            ? null : new(CapabilityRejectionKind.VersionMismatch, field, expected, actual);
    }

    private static CapabilityRejection? InspectPropertyNames(JsonElement root)
    {
        // Finish inspecting names after a duplicate so a later undecodable key remains InvalidJson.
        CapabilityRejection? duplicate = null;
        var pending = new Stack<JsonElement>();
        pending.Push(root);
        while (pending.TryPop(out var element))
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    string name;
                    try { name = property.Name; }
                    catch (InvalidOperationException)
                    {
                        return new(CapabilityRejectionKind.InvalidJson, "$", "Decodable property name");
                    }
                    if (!names.Add(name))
                    {
                        duplicate ??= new(CapabilityRejectionKind.DuplicateProperty, name);
                    }
                    pending.Push(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) { pending.Push(item); }
            }
        }
        return duplicate;
    }

    private static CapabilityAdmissionResult Reject(CapabilityRejectionKind kind, string field,
        string? expected = null, string? actual = null)
        => new(new CapabilityRejection(kind, field, expected, actual));
}
