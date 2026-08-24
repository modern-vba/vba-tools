using System.Reflection;
using System.Text;
using System.Text.Json;
using VbaDev.App.Cli;

namespace VbaDev.App.Diagnostics;

/// <summary>
/// Renders doctor diagnostics into the command result contract.
/// </summary>
public sealed class DoctorReportRenderer
{
    private const string SchemaVersion = "1.0";

    /// <summary>
    /// Renders a doctor command result.
    /// </summary>
    /// <param name="run">The diagnostic run to render.</param>
    /// <param name="request">The Doctor request context.</param>
    /// <returns>The doctor command result.</returns>
    public CommandResult Render(
        DoctorDiagnosticRun run,
        DoctorCommandRequest request)
    {
        run = NormalizeDuplicateCheckIds(run);

        var results = run.Results;
        var aggregateStatus = AggregateStatus(results);
        var canceledWithCleanupProof = run.Canceled &&
            !run.Complete &&
            results.Any(result =>
                result.Id.Equals("excel.processCleanup", StringComparison.Ordinal) &&
                result.Status == DiagnosticStatus.Pass);
        var exitCode = canceledWithCleanupProof && aggregateStatus != "fail"
            ? 130
            : !run.Complete || aggregateStatus is "fail" or "unverified"
                ? 1
                : 0;
        var output = request.Format == DoctorOutputFormat.Json
            ? RenderJson(run, request, aggregateStatus)
            : RenderText(run, request, aggregateStatus);
        return new CommandResult(exitCode, output, string.Empty);
    }

    private static DoctorDiagnosticRun NormalizeDuplicateCheckIds(
        DoctorDiagnosticRun run)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var duplicateCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var results = new List<DiagnosticResult>(run.Results.Count);
        var foundDuplicate = false;
        foreach (var result in run.Results)
        {
            if (seenIds.Add(result.Id))
            {
                results.Add(result);
                continue;
            }

            foundDuplicate = true;
            duplicateCounts.TryGetValue(result.Id, out var duplicateIndex);
            string replacementId;
            do
            {
                duplicateIndex++;
                replacementId = $"doctor.invalidDuplicate." +
                    $"{Uri.EscapeDataString(result.Id)}.{duplicateIndex}";
            }
            while (!seenIds.Add(replacementId));

            duplicateCounts[result.Id] = duplicateIndex;
            var normalizedResult = result.Status == DiagnosticStatus.Fail
                ? DiagnosticResult.Fail(
                    replacementId,
                    result.Name,
                    $"Doctor produced duplicate check identity '{result.Id}' after a failed check.")
                : DiagnosticResult.Unverified(
                    replacementId,
                    result.Name,
                    $"Doctor produced duplicate check identity '{result.Id}'.");
            results.Add(normalizedResult with
            {
                Details = new Dictionary<string, object?>
                {
                    ["duplicateId"] = result.Id
                }
            });
        }

        return foundDuplicate
            ? run with { Results = results, Complete = false }
            : run;
    }

    private static string RenderJson(
        DoctorDiagnosticRun run,
        DoctorCommandRequest request,
        string status)
    {
        var report = new
        {
            schemaVersion = SchemaVersion,
            toolVersion = GetInformationalVersion(),
            scope = request.Scope == DoctorScope.Environment ? "environment" : "project",
            project = run.Project,
            status,
            complete = run.Complete,
            checks = run.Results.Select(result => new
            {
                id = result.Id,
                status = RenderJsonStatus(result.Status),
                message = result.Message,
                durationMilliseconds = result.DurationMilliseconds,
                details = result.Details
            })
        };
        return JsonSerializer.Serialize(report) + Environment.NewLine;
    }

    private static string AggregateStatus(IReadOnlyList<DiagnosticResult> results)
    {
        if (results.Any(result => result.Status == DiagnosticStatus.Fail))
        {
            return "fail";
        }

        if (results.Any(result => result.Status is
                DiagnosticStatus.Unverified or DiagnosticStatus.Skip))
        {
            return "unverified";
        }

        return results.Any(result => result.Status == DiagnosticStatus.Warn)
            ? "warning"
            : "pass";
    }

    private static string RenderJsonStatus(DiagnosticStatus status)
        => status switch
        {
            DiagnosticStatus.Pass => "pass",
            DiagnosticStatus.Warn => "warning",
            DiagnosticStatus.Fail => "fail",
            DiagnosticStatus.Unverified => "unverified",
            DiagnosticStatus.Skip => "skipped",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
        };

    private static string GetInformationalVersion()
        => typeof(DoctorReportRenderer).Assembly
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
               .InformationalVersion
           ?? "0.0.0";

    private static string RenderText(
        DoctorDiagnosticRun run,
        DoctorCommandRequest request,
        string aggregateStatus)
    {
        var builder = new StringBuilder();
        builder.AppendLine("vba-dev doctor");
        builder.AppendLine();
        builder.AppendLine(
            $"Scope: {(request.Scope == DoctorScope.Environment ? "environment" : "project")}");
        builder.AppendLine($"Project: {run.Project ?? "null"}");
        builder.AppendLine($"Status: {aggregateStatus}");
        builder.AppendLine($"Complete: {run.Complete.ToString().ToLowerInvariant()}");
        builder.AppendLine();
        foreach (var result in run.Results)
        {
            builder.AppendLine($"[{RenderStatus(result.Status)}] {result.Name}: {result.Message}");
        }

        return builder.ToString();
    }

    private static string RenderStatus(DiagnosticStatus status)
        => status switch
        {
            DiagnosticStatus.Pass => "PASS",
            DiagnosticStatus.Warn => "WARN",
            DiagnosticStatus.Fail => "FAIL",
            DiagnosticStatus.Unverified => "UNVERIFIED",
            DiagnosticStatus.Skip => "SKIP",
            _ => status.ToString().ToUpperInvariant()
        };
}
