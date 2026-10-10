using System.Text.Json;
using VbaDebugAdapter.Cli;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;

namespace VbaDebugAdapter.Build;

internal sealed record DebugSourceWorkbookDescription(
    string ProjectRoot, string DocumentName, string WorkbookPath)
{
    internal IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>The public CLI, not the adapter, resolves the project document's source workbook.</summary>
internal sealed class VbaDevSourceWorkbookResolver(IVbaDevBuildProcess process)
{
    private const string LegacyWorkbookBinWarningPrefix = "[WARN] project-workbook-bin-deprecated: ";

    internal async Task<DebugSourceWorkbookDescription> ResolveAsync(string executable,
        string projectRoot, string documentName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentName);
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)
            || !Path.IsPathFullyQualified(projectRoot) || !Directory.Exists(projectRoot))
            throw new DebugSetupException("Source workbook resolution requires an existing absolute CLI and project path.");
        var canonicalProject = Path.GetFullPath(projectRoot);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await process.RunAsync(Path.GetFullPath(executable),
            ["prepare-debug", "--describe", "--project", canonicalProject, "--document", documentName],
            deadline.Token).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.CleanupOutcome is not { } owner
                || !owner.Evidence.Any(item => item.Kind == DebugResourceKind.Process && item.Released)
                || !owner.Evidence.Any(item => item.Kind == DebugResourceKind.Handle && item.Released))
                throw new DebugSetupException("The source workbook description process did not prove terminal release.");
            owner.ThrowWithEvidence();
            if (result.ExitCode != 0)
                throw new DebugSetupException(
                    $"vba-dev source workbook description exited with code {result.ExitCode} " +
                    $"(0x{unchecked((uint)result.ExitCode):X8}). {result.StandardError.TrimEnd()}");
            using var json = JsonDocument.Parse(result.StandardOutput);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new DebugSetupException("The source workbook description must be a JSON object.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!DebugRequestAdmission.TryReadPropertyName(property, out var name)
                    || name is not ("type" or "schemaVersion" or "projectRoot" or "documentName" or "workbookPath")
                    || !DebugRequestAdmission.TryReadString(property.Value, out var value)
                    || !values.TryAdd(name, value!))
                    throw new DebugSetupException("The source workbook description contains a malformed or duplicate field.");
            }
            if (values.Count != 5 || values["type"] != "debugWorkbookDescription"
                || values["schemaVersion"] != "1.0"
                || !values["projectRoot"].Equals(canonicalProject, StringComparison.OrdinalIgnoreCase)
                || !values["documentName"].Equals(documentName, StringComparison.OrdinalIgnoreCase)
                || !Path.IsPathFullyQualified(values["workbookPath"])
                || !Path.GetExtension(values["workbookPath"]).Equals(".xlsm", StringComparison.OrdinalIgnoreCase))
                throw new DebugSetupException("The source workbook description does not match the selected project and document.");
            var warnings = result.StandardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith(LegacyWorkbookBinWarningPrefix, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(line[LegacyWorkbookBinWarningPrefix.Length..]))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return new(canonicalProject, documentName, Path.GetFullPath(values["workbookPath"]))
            {
                Warnings = warnings.Length == 0 ? Array.Empty<string>() : warnings
            };
        }
        catch (Exception failure)
        {
            var completion = new DebugFailureCompletion(failure);
            if (result.CleanupOutcome is { } outcome) completion.Merge(outcome);
            foreach (var kind in new[] { DebugResourceKind.Process, DebugResourceKind.Handle })
                if (result.CleanupOutcome is null
                    || !result.CleanupOutcome.Evidence.Any(item => item.Kind == kind))
                    completion.AddEvidence(new("description-child-release", executable, kind, false,
                        "The description process supplied no terminal ownership evidence for this resource."));
            completion.Complete().ThrowWithEvidence();
            throw;
        }
    }
}
