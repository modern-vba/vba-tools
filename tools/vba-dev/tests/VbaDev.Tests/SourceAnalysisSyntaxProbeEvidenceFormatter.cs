using System.Text.Json;

namespace VbaDev.Tests;

// [DEBUG-415-syntax-v1] Temporary whitelist for the read-only position-syntax probe.
internal static class SourceAnalysisSyntaxProbeEvidenceFormatter
{
    private const string EvidenceKey = "DEBUG-415-syntax-v1";
    private const int MaximumUriCharacters = 2048;

    internal static string? Format(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            try
            {
                if (current.Data[EvidenceKey] is not Dictionary<string, object> evidence)
                {
                    continue;
                }

                var phase = GetPhase(evidence);
                var uri = GetText(evidence, "uri", MaximumUriCharacters);
                var uriLength = GetInt(evidence, "uriLength");
                if (phase is null || uri is null || uriLength is null or < -1)
                {
                    return "{\"status\":\"unavailable\"}";
                }

                return JsonSerializer.Serialize(new
                {
                    status = "available",
                    phase,
                    uri,
                    uriLength,
                    uriCaptureComplete = GetBool(evidence, "uriCaptureComplete") is true
                        && uriLength == uri.Length,
                    positionIsNull = GetBool(evidence, "positionIsNull"),
                    positionLine = GetInt(evidence, "positionLine"),
                    positionCharacter = GetInt(evidence, "positionCharacter"),
                    positionOffset = GetInt(evidence, "positionOffset"),
                    statementStartOffset = GetInt(evidence, "statementStartOffset"),
                    statementEndOffset = GetInt(evidence, "statementEndOffset"),
                    statementNextOffset = GetInt(evidence, "statementNextOffset"),
                    significantTokenCount = GetInt(evidence, "significantTokenCount"),
                    inspectedTokenCount = GetInt(evidence, "inspectedTokenCount"),
                    inspectionComplete = GetBool(evidence, "inspectionComplete"),
                    firstBadIndex = GetInt(evidence, "firstBadIndex"),
                    firstBadReferenceKind = GetText(evidence, "firstBadReferenceKind", 32),
                    postFaultGraphStatus = GetText(evidence, "postFaultGraphStatus", 32)
                });
            }
            catch (Exception)
            {
                return "{\"status\":\"unavailable\"}";
            }
        }

        return null;
    }

    private static string? GetText(
        IReadOnlyDictionary<string, object> evidence,
        string key,
        int maximumLength)
        => evidence.TryGetValue(key, out var value) && value is string text
            ? text[..Math.Min(text.Length, maximumLength)]
            : null;

    private static string? GetPhase(IReadOnlyDictionary<string, object> evidence)
        => evidence.TryGetValue("phase", out var value) && value is string phase
            && phase is "TryGetLabelReference.prefix" or "FindIdentifier.query"
                or "GetProcedureSyntaxWords.prefix"
                ? phase : null;

    private static int? GetInt(IReadOnlyDictionary<string, object> evidence, string key)
        => evidence.TryGetValue(key, out var value) && value is int number ? number : null;

    private static bool? GetBool(IReadOnlyDictionary<string, object> evidence, string key)
        => evidence.TryGetValue(key, out var value) && value is bool flag ? flag : null;
}
