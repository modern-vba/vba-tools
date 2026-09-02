using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VbaDev.App.Cli;

namespace VbaDev.App.HostEvents;

/// <summary>
/// Produces the environment-scoped generic UserForm Event catalog.
/// </summary>
public sealed class HostEventListCommand(IHostEventCatalogAutomation catalogAutomation)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    /// <summary>
    /// Reads and renders the intrinsic catalog after owned process release.
    /// </summary>
    public async Task<CommandResult> RunAsync(string format, CancellationToken cancellationToken)
    {
        IntrinsicHostEventCatalog catalog;
        try
        {
            catalog = await catalogAutomation.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new CommandResult(
                130,
                string.Empty,
                "Host Event catalog acquisition was cancelled." + Environment.NewLine);
        }
        catch (Exception exception)
        {
            return new CommandResult(1, string.Empty, exception.Message + Environment.NewLine);
        }

        if (!TryCanonicalize(catalog, out var canonical, out var error))
        {
            return new CommandResult(1, string.Empty, error + Environment.NewLine);
        }

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return new CommandResult(
                0,
                JsonSerializer.Serialize(CreateOutput(canonical), JsonOptions) + Environment.NewLine,
                string.Empty);
        }

        var text = new StringBuilder()
            .AppendLine("Source kind: userForm")
            .AppendLine($"Intrinsic Event source: {canonical.IntrinsicEventSourceName}")
            .AppendLine("Events:");
        if (canonical.Events.Count == 0)
        {
            text.AppendLine("  (none)");
        }
        else
        {
            foreach (var inspectedEvent in canonical.Events)
            {
                text.Append("  ")
                    .Append(inspectedEvent.Identity.Name)
                    .Append('(')
                    .Append(string.Join(", ", inspectedEvent.Signature.Parameters.Select(CreateParameterText)))
                    .Append(") [authoringAvailable=")
                    .Append(inspectedEvent.AuthoringAvailable ? "true" : "false")
                    .Append(", existingHandlerRecognizable=")
                    .Append(inspectedEvent.ExistingHandlerRecognizable ? "true" : "false")
                    .AppendLine("]");
            }
        }

        return new CommandResult(0, text.ToString(), string.Empty);
    }

    private static bool TryCanonicalize(
        IntrinsicHostEventCatalog catalog,
        out IntrinsicHostEventCatalog canonical,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!string.Equals(catalog.IntrinsicEventSourceName, "UserForm", StringComparison.Ordinal))
        {
            canonical = catalog;
            error = "The generated intrinsic Event source must be exactly 'UserForm'.";
            return false;
        }

        if (catalog.Events.Count == 0)
        {
            canonical = catalog;
            error = "The generated UserForm exposed no Events, so the environment catalog is not authoritative.";
            return false;
        }

        var duplicate = catalog.Events
            .GroupBy(inspectedEvent => inspectedEvent.Identity.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicate is not null)
        {
            canonical = catalog;
            error = $"Duplicate intrinsic Event identity '{duplicate.Key}' was observed.";
            return false;
        }

        foreach (var inspectedEvent in catalog.Events)
        {
            if (!string.Equals(
                    inspectedEvent.Identity.SourceName,
                    catalog.IntrinsicEventSourceName,
                    StringComparison.Ordinal))
            {
                canonical = catalog;
                error = $"Event '{inspectedEvent.Identity.Name}' does not belong to source 'UserForm'.";
                return false;
            }
        }

        canonical = catalog with
        {
            Events = catalog.Events
                .OrderBy(inspectedEvent => inspectedEvent.Identity.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(inspectedEvent => inspectedEvent.Identity.Name, StringComparer.Ordinal)
                .ToArray()
        };
        error = string.Empty;
        return true;
    }

    private static HostEventCatalogOutput CreateOutput(IntrinsicHostEventCatalog catalog)
        => new(
            "1.0",
            "userForm",
            catalog.IntrinsicEventSourceName,
            catalog.Events.Select(CreateEventOutput).ToArray(),
            catalog.BaseTypeProvenance is null
                ? null
                : new HostEventBaseTypeProvenanceOutput(
                    catalog.BaseTypeProvenance.Name,
                    catalog.BaseTypeProvenance.LibraryGuid.ToString("D"),
                    catalog.BaseTypeProvenance.MajorVersion,
                    catalog.BaseTypeProvenance.MinorVersion,
                    catalog.BaseTypeProvenance.Lcid));

    private static HostEventOutput CreateEventOutput(HostEvent inspectedEvent)
        => new(
            new HostEventIdentityOutput(
                inspectedEvent.Identity.SourceName,
                inspectedEvent.Identity.Name),
            new HostEventSignatureOutput(
                inspectedEvent.Signature.Parameters.Select(CreateParameterOutput).ToArray(),
                inspectedEvent.Signature.Documentation),
            inspectedEvent.AuthoringAvailable,
            inspectedEvent.ExistingHandlerRecognizable);

    private static HostEventParameterOutput CreateParameterOutput(HostEventParameter parameter)
        => new(
            parameter.Name,
            CreateTypeOutput(parameter.Type),
            parameter.Passing == HostEventPassingMechanism.ByVal ? "byVal" : "byRef",
            parameter.ArrayShape == HostEventArrayShape.Scalar ? "scalar" : "array",
            parameter.Optional,
            parameter.ParamArray);

    private static object CreateTypeOutput(HostEventTypeReference type)
        => type switch
        {
            IntrinsicHostEventTypeReference intrinsic => new IntrinsicHostEventTypeOutput("intrinsic", intrinsic.Name),
            TypeLibHostEventTypeReference typeLib => new TypeLibHostEventTypeOutput(
                "typeLib",
                typeLib.Name,
                typeLib.LibraryGuid.ToString("D"),
                typeLib.MajorVersion,
                typeLib.MinorVersion,
                typeLib.Lcid),
            UnresolvedHostEventTypeReference unresolved => new UnresolvedHostEventTypeOutput(
                "unresolved",
                unresolved.DisplayName),
            _ => throw new InvalidOperationException($"Unsupported host Event type reference: {type.GetType().Name}")
        };

    private static string CreateParameterText(HostEventParameter parameter)
    {
        var prefix = parameter.ParamArray
            ? "ParamArray "
            : parameter.Optional
                ? "Optional "
                : string.Empty;
        var passing = parameter.Passing == HostEventPassingMechanism.ByRef ? "ByRef" : "ByVal";
        var array = parameter.ArrayShape == HostEventArrayShape.Array ? "()" : string.Empty;
        return $"{prefix}{passing} {parameter.Name}{array} As {CreateTypeText(parameter.Type)}";
    }

    private static string CreateTypeText(HostEventTypeReference type)
        => type switch
        {
            IntrinsicHostEventTypeReference intrinsic => intrinsic.Name,
            TypeLibHostEventTypeReference typeLib =>
                $"{typeLib.Name} ({typeLib.LibraryGuid:D} " +
                $"{typeLib.MajorVersion}.{typeLib.MinorVersion} LCID {typeLib.Lcid})",
            UnresolvedHostEventTypeReference unresolved => unresolved.DisplayName,
            _ => throw new InvalidOperationException($"Unsupported host Event type reference: {type.GetType().Name}")
        };

    private sealed record HostEventCatalogOutput(
        string SchemaVersion,
        string SourceKind,
        string IntrinsicEventSourceName,
        IReadOnlyList<HostEventOutput> Events,
        HostEventBaseTypeProvenanceOutput? BaseTypeProvenance);

    private sealed record HostEventOutput(
        HostEventIdentityOutput Identity,
        HostEventSignatureOutput Signature,
        bool AuthoringAvailable,
        bool ExistingHandlerRecognizable);

    private sealed record HostEventIdentityOutput(string SourceName, string Name);

    private sealed record HostEventSignatureOutput(
        IReadOnlyList<HostEventParameterOutput> Parameters,
        string? Documentation);

    private sealed record HostEventParameterOutput(
        string Name,
        object Type,
        string Passing,
        string ArrayShape,
        bool Optional,
        bool ParamArray);

    private sealed record IntrinsicHostEventTypeOutput(string Kind, string Name);

    private sealed record TypeLibHostEventTypeOutput(
        string Kind,
        string Name,
        string LibraryGuid,
        int MajorVersion,
        int MinorVersion,
        int Lcid);

    private sealed record UnresolvedHostEventTypeOutput(string Kind, string DisplayName);

    private sealed record HostEventBaseTypeProvenanceOutput(
        string Name,
        string LibraryGuid,
        int MajorVersion,
        int MinorVersion,
        int Lcid);
}
