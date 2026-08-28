using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VbaDev.App.Cli;
using VbaDev.App.Projects;

namespace VbaDev.App.HostClasses;

/// <summary>
/// Produces document-scoped intrinsic host-class projections.
/// </summary>
public sealed class HostClassListCommand(IHostClassInspectionAutomation inspectionAutomation)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    /// <summary>
    /// Inspects the selected document and renders its projection after owned process release.
    /// </summary>
    public async Task<CommandResult> RunAsync(
        ResolvedProjectContext context,
        string format,
        CancellationToken cancellationToken)
    {
        var project = Path.GetFullPath(context.ProjectRoot);
        var sourceTemplate = Path.GetFullPath(context.TemplateDocumentPath);
        HostClassInspectionCompletion completion;
        try
        {
            completion = await inspectionAutomation.InspectAsync(
                    new HostClassInspectionRequest(
                        sourceTemplate,
                        new HostClassInspectionTimeouts(
                            ExcelProcessStart: TimeSpan.FromSeconds(30),
                            WorkbookOpen: CommandDefaultResolver.ResolveWorkbookOpenTimeout(context.Manifest),
                            CooperativeCleanup: TimeSpan.FromSeconds(5),
                            ClassEnumeration: TimeSpan.FromSeconds(60),
                            ClassInspection: TimeSpan.FromSeconds(60))),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new CommandResult(1, string.Empty, ex.Message + Environment.NewLine);
        }

        var batch = completion.Batch;
        var warnings = completion.Warnings
            .Select(warning => new HostClassWarningOutput(warning.Code, warning.Message))
            .ToArray();
        var warningText = string.Concat(completion.Warnings.Select(warning =>
            $"[WARNING] {warning.Code}: {warning.Message}{Environment.NewLine}"));

        var duplicateIdentityGroups = batch.Classes
            .GroupBy(entry => entry.Identity, HostClassIdentityComparer.Instance)
            .Where(group => group.Skip(1).Any())
            .ToArray();
        var duplicateIdentities = duplicateIdentityGroups
            .Select(group => group.Key)
            .ToHashSet(HostClassIdentityComparer.Instance);
        var classes = batch.Classes
            .Where(entry => !duplicateIdentities.Contains(entry.Identity))
            .Select(CanonicalizeEntry)
            .OrderBy(entry => CreateKindOrder(entry.Identity.Kind))
            .ThenBy(entry => entry.Identity.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Identity.Name, StringComparer.Ordinal)
            .ToArray();
        var classEnumerationComplete = batch.ClassEnumerationComplete && duplicateIdentityGroups.Length == 0;
        var complete = batch.Outcome == HostClassInspectionOutcome.Completed &&
                       classEnumerationComplete &&
                       classes.All(entry => entry is ResolvedHostClassInspectionEntry);
        var diagnostics = batch.Diagnostics
            .Select(diagnostic => new HostClassDiagnosticOutput(diagnostic.Code, diagnostic.Message))
            .ToList();
        if (duplicateIdentityGroups.Length != 0)
        {
            diagnostics.Add(
                new HostClassDiagnosticOutput(
                    "classEnumerationFailure",
                    "Duplicate host-class identities were observed: " +
                    string.Join(
                        ", ",
                        duplicateIdentityGroups.Select(group =>
                            $"{CreateKindName(group.Key.Kind)} '{group.Key.Name}'")) +
                    "."));
        }
        var exitCode = batch.Outcome == HostClassInspectionOutcome.Cancelled
            ? 130
            : complete
                ? 0
                : 1;
        var hasProjectAuthority =
            !string.IsNullOrWhiteSpace(batch.VbaProjectName)
            && batch.SourceTemplateFingerprint is { Length: 64 } fingerprint
            && fingerprint.All(Uri.IsHexDigit);

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var output = new HostClassProjectionOutput(
                "1.1",
                project,
                context.DocumentName,
                sourceTemplate,
                hasProjectAuthority ? batch.VbaProjectName : null,
                hasProjectAuthority
                    ? batch.SourceTemplateFingerprint!.ToUpperInvariant()
                    : null,
                classEnumerationComplete,
                complete,
                classes.Select(CreateClassOutput).ToArray(),
                diagnostics,
                warnings);
            return new CommandResult(
                exitCode,
                JsonSerializer.Serialize(output, JsonOptions) + Environment.NewLine,
                warningText);
        }

        var text = new StringBuilder()
            .AppendLine($"Project: {project}")
            .AppendLine($"Document: {context.DocumentName}")
            .AppendLine($"Source template: {sourceTemplate}");
        if (hasProjectAuthority)
        {
            text.AppendLine($"VBA project name: {batch.VbaProjectName}")
                .AppendLine(
                    $"Source template fingerprint: {batch.SourceTemplateFingerprint!.ToUpperInvariant()}");
        }

        text
            .AppendLine($"Class enumeration complete: {CreateBooleanText(classEnumerationComplete)}")
            .AppendLine($"Complete: {CreateBooleanText(complete)}")
            .AppendLine("Diagnostics:");
        if (diagnostics.Count == 0)
        {
            text.AppendLine("  (none)");
        }
        else
        {
            foreach (var diagnostic in diagnostics)
            {
                text.AppendLine($"  {diagnostic.Code}: {diagnostic.Message}");
            }
        }

        text
            .AppendLine("Host classes:");
        if (classes.Length == 0)
        {
            text.AppendLine("  (none)");
        }
        else
        {
            foreach (var entry in classes)
            {
                text.AppendLine($"  {CreateKindName(entry.Identity.Kind)} {entry.Identity.Name} [{CreateStatusName(entry)}]");
                if (entry is ResolvedHostClassInspectionEntry resolved)
                {
                    text.AppendLine($"    Intrinsic Event source: {resolved.IntrinsicEventSourceName}");
                    if (resolved.BaseTypeProvenance is { } provenance)
                    {
                        text.AppendLine(
                            $"    Base type: {provenance.Name} " +
                            $"({provenance.LibraryGuid:D} " +
                            $"{provenance.MajorVersion}.{provenance.MinorVersion} " +
                            $"LCID {provenance.Lcid})");
                    }

                    text.AppendLine("    Events:");
                    if (resolved.Events.Count == 0)
                    {
                        text.AppendLine("      (none)");
                    }
                    else
                    {
                        foreach (var inspectedEvent in resolved.Events)
                        {
                            text.Append("      ")
                                .Append(inspectedEvent.Name)
                                .Append('(')
                                .Append(string.Join(", ", inspectedEvent.Parameters.Select(CreateParameterText)))
                                .Append(") [authoringAvailable=")
                                .Append(inspectedEvent.AuthoringAvailable ? "true" : "false")
                                .Append(", existingHandlerRecognizable=")
                                .Append(inspectedEvent.ExistingHandlerRecognizable ? "true" : "false")
                                .AppendLine("]");
                            if (!string.IsNullOrWhiteSpace(inspectedEvent.Documentation))
                            {
                                text.AppendLine($"        {inspectedEvent.Documentation}");
                            }
                        }
                    }
                }
                else if (entry is UnverifiedHostClassInspectionEntry unverified)
                {
                    text.AppendLine($"    {CreateReasonCode(unverified.Reason)}: {unverified.Message}");
                }
            }
        }

        return new CommandResult(exitCode, text.ToString(), warningText);
    }

    private static string CreateBooleanText(bool value) => value ? "true" : "false";

    private static string CreateStatusName(HostClassInspectionEntry entry)
        => entry is ResolvedHostClassInspectionEntry ? "resolved" : "unverified";

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
                $"{typeLib.Name} ({typeLib.LibraryGuid:D} {typeLib.MajorVersion}.{typeLib.MinorVersion} LCID {typeLib.Lcid})",
            UnresolvedHostEventTypeReference unresolved => unresolved.DisplayName,
            _ => throw new InvalidOperationException($"Unsupported host Event type reference: {type.GetType().Name}")
        };

    private sealed record HostClassProjectionOutput(
        string SchemaVersion,
        string Project,
        string Document,
        string SourceTemplate,
        string? VbaProjectName,
        string? SourceTemplateFingerprint,
        bool ClassEnumerationComplete,
        bool Complete,
        IReadOnlyList<object> Classes,
        IReadOnlyList<HostClassDiagnosticOutput> Diagnostics,
        IReadOnlyList<HostClassWarningOutput> Warnings);

    private static object CreateClassOutput(HostClassInspectionEntry entry)
        => entry switch
        {
            ResolvedHostClassInspectionEntry resolved => new ResolvedHostClassOutput(
                CreateIdentityOutput(resolved.Identity),
                "resolved",
                resolved.IntrinsicEventSourceName,
                resolved.Events.Select(CreateEventOutput).ToArray(),
                resolved.BaseTypeProvenance is null
                    ? null
                    : CreateBaseTypeProvenanceOutput(resolved.BaseTypeProvenance)),
            UnverifiedHostClassInspectionEntry unverified => new UnverifiedHostClassOutput(
                CreateIdentityOutput(unverified.Identity),
                "unverified",
                CreateReasonCode(unverified.Reason),
                unverified.Message),
            _ => throw new InvalidOperationException($"Unsupported host-class entry: {entry.GetType().Name}")
        };

    private static string CreateReasonCode(HostClassInspectionFailureReason reason)
        => reason switch
        {
            HostClassInspectionFailureReason.EventEnumerationFailure => "eventEnumerationFailure",
            HostClassInspectionFailureReason.IntrinsicEventSourceNameReadFailure =>
                "intrinsicEventSourceNameReadFailure",
            HostClassInspectionFailureReason.SignatureReadFailure => "signatureReadFailure",
            HostClassInspectionFailureReason.AvailabilityReadFailure => "availabilityReadFailure",
            HostClassInspectionFailureReason.InspectionTimeout => "inspectionTimeout",
            HostClassInspectionFailureReason.InspectionAborted => "inspectionAborted",
            HostClassInspectionFailureReason.Cancelled => "cancelled",
            HostClassInspectionFailureReason.InspectionFailure => "inspectionFailure",
            _ => throw new InvalidOperationException($"Unsupported host-class failure reason: {reason}")
        };

    private static HostClassIdentityOutput CreateIdentityOutput(HostClassIdentity identity)
        => new(
            identity.Name,
            CreateKindName(identity.Kind));

    private static string CreateKindName(HostClassComponentKind kind)
        => kind switch
        {
            HostClassComponentKind.Form => "form",
            HostClassComponentKind.Document => "document",
            _ => throw new InvalidOperationException($"Unsupported host-class kind: {kind}")
        };

    private static int CreateKindOrder(HostClassComponentKind kind)
        => kind switch
        {
            HostClassComponentKind.Document => 0,
            HostClassComponentKind.Form => 1,
            _ => throw new InvalidOperationException($"Unsupported host-class kind: {kind}")
        };

    private static HostClassInspectionEntry CanonicalizeEntry(HostClassInspectionEntry entry)
    {
        if (entry is not ResolvedHostClassInspectionEntry resolved)
        {
            return entry;
        }

        var normalizedEvents = new List<HostEventSignature>();
        foreach (var group in resolved.Events.GroupBy(
                     inspectedEvent => inspectedEvent.Name,
                     StringComparer.OrdinalIgnoreCase))
        {
            var observations = group.ToArray();
            if (observations.Skip(1).Any(observation =>
                    !HasSameCallableContractAndAvailability(observations[0], observation)))
            {
                return new UnverifiedHostClassInspectionEntry(
                    resolved.Identity,
                    HostClassInspectionFailureReason.EventEnumerationFailure,
                    $"Conflicting observations were returned for Event '{observations[0].Name}'.");
            }

            normalizedEvents.Add(observations.Min(HostEventPresentationComparer.Instance)!);
        }

        return resolved with
        {
            Events = normalizedEvents
                .OrderBy(inspectedEvent => inspectedEvent.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(inspectedEvent => inspectedEvent.Name, StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static bool HasSameCallableContractAndAvailability(
        HostEventSignature left,
        HostEventSignature right)
    {
        if (left.Parameters.Count != right.Parameters.Count ||
            left.AuthoringAvailable != right.AuthoringAvailable ||
            left.ExistingHandlerRecognizable != right.ExistingHandlerRecognizable)
        {
            return false;
        }

        for (var index = 0; index < left.Parameters.Count; index++)
        {
            var leftParameter = left.Parameters[index];
            var rightParameter = right.Parameters[index];
            if (leftParameter.Passing != rightParameter.Passing ||
                leftParameter.ArrayShape != rightParameter.ArrayShape ||
                leftParameter.Optional != rightParameter.Optional ||
                leftParameter.ParamArray != rightParameter.ParamArray ||
                !HasSameCanonicalType(leftParameter.Type, rightParameter.Type))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasSameCanonicalType(HostEventTypeReference left, HostEventTypeReference right)
        => (left, right) switch
        {
            (IntrinsicHostEventTypeReference leftIntrinsic,
                IntrinsicHostEventTypeReference rightIntrinsic) =>
                string.Equals(leftIntrinsic.Name, rightIntrinsic.Name, StringComparison.OrdinalIgnoreCase),
            (TypeLibHostEventTypeReference leftTypeLib,
                TypeLibHostEventTypeReference rightTypeLib) =>
                string.Equals(leftTypeLib.Name, rightTypeLib.Name, StringComparison.OrdinalIgnoreCase) &&
                leftTypeLib.LibraryGuid == rightTypeLib.LibraryGuid &&
                leftTypeLib.MajorVersion == rightTypeLib.MajorVersion &&
                leftTypeLib.MinorVersion == rightTypeLib.MinorVersion &&
                leftTypeLib.Lcid == rightTypeLib.Lcid,
            _ => false
        };

    private static HostEventOutput CreateEventOutput(HostEventSignature inspectedEvent)
        => new(
            inspectedEvent.Name,
            inspectedEvent.Parameters.Select(CreateParameterOutput).ToArray(),
            inspectedEvent.Documentation,
            inspectedEvent.AuthoringAvailable,
            inspectedEvent.ExistingHandlerRecognizable);

    private static HostClassBaseTypeProvenanceOutput CreateBaseTypeProvenanceOutput(
        HostClassBaseTypeProvenance provenance)
        => new(
            provenance.Name,
            provenance.LibraryGuid.ToString("D"),
            provenance.MajorVersion,
            provenance.MinorVersion,
            provenance.Lcid);

    private static HostEventParameterOutput CreateParameterOutput(HostEventParameter parameter)
        => new(
            parameter.Name,
            CreateTypeOutput(parameter.Type),
            parameter.Passing switch
            {
                HostEventPassingMechanism.ByVal => "byVal",
                HostEventPassingMechanism.ByRef => "byRef",
                _ => throw new InvalidOperationException($"Unsupported Event passing mechanism: {parameter.Passing}")
            },
            parameter.ArrayShape switch
            {
                HostEventArrayShape.Scalar => "scalar",
                HostEventArrayShape.Array => "array",
                _ => throw new InvalidOperationException($"Unsupported Event array shape: {parameter.ArrayShape}")
            },
            parameter.Optional,
            parameter.ParamArray);

    private static object CreateTypeOutput(HostEventTypeReference type)
        => type switch
        {
            IntrinsicHostEventTypeReference intrinsic => new IntrinsicHostEventTypeOutput(
                "intrinsic",
                intrinsic.Name),
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

    private sealed record ResolvedHostClassOutput(
        HostClassIdentityOutput Identity,
        string Status,
        string IntrinsicEventSourceName,
        IReadOnlyList<HostEventOutput> Events,
        HostClassBaseTypeProvenanceOutput? BaseTypeProvenance);

    private sealed record HostClassBaseTypeProvenanceOutput(
        string Name,
        string LibraryGuid,
        int MajorVersion,
        int MinorVersion,
        int Lcid);

    private sealed record UnverifiedHostClassOutput(
        HostClassIdentityOutput Identity,
        string Status,
        string ReasonCode,
        string Message);

    private sealed record HostClassIdentityOutput(string Name, string Kind);

    private sealed record HostClassDiagnosticOutput(string Code, string Message);

    private sealed record HostClassWarningOutput(string Code, string Message);

    private sealed record HostEventOutput(
        string Name,
        IReadOnlyList<HostEventParameterOutput> Parameters,
        string? Documentation,
        bool AuthoringAvailable,
        bool ExistingHandlerRecognizable);

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

    private sealed class HostClassIdentityComparer : IEqualityComparer<HostClassIdentity>
    {
        public static HostClassIdentityComparer Instance { get; } = new();

        public bool Equals(HostClassIdentity? x, HostClassIdentity? y)
            => ReferenceEquals(x, y) ||
               (x is not null &&
                y is not null &&
                x.Kind == y.Kind &&
                string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));

        public int GetHashCode(HostClassIdentity obj)
            => HashCode.Combine(
                obj.Kind,
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }

    private sealed class HostEventPresentationComparer : IComparer<HostEventSignature>
    {
        public static HostEventPresentationComparer Instance { get; } = new();

        public int Compare(HostEventSignature? x, HostEventSignature? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return 1;
            }

            if (y is null)
            {
                return -1;
            }

            var xDocumented = !string.IsNullOrWhiteSpace(x.Documentation);
            var yDocumented = !string.IsNullOrWhiteSpace(y.Documentation);
            if (xDocumented != yDocumented)
            {
                return xDocumented ? -1 : 1;
            }

            var comparison = ComparePresentationString(x.Name, y.Name);
            if (comparison != 0)
            {
                return comparison;
            }

            for (var index = 0; index < x.Parameters.Count; index++)
            {
                comparison = ComparePresentationString(
                    x.Parameters[index].Name,
                    y.Parameters[index].Name);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return ComparePresentationString(x.Documentation ?? string.Empty, y.Documentation ?? string.Empty);
        }

        private static int ComparePresentationString(string left, string right)
        {
            var comparison = StringComparer.OrdinalIgnoreCase.Compare(left, right);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left, right);
        }
    }
}
