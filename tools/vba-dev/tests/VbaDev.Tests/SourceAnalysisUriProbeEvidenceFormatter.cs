using System.Text.Json;

namespace VbaDev.Tests;

// The read-only exact-input probe bypasses the CLI evidence store. Keep its
// failure-only output bounded and limited to the URI evidence contract.
internal static class SourceAnalysisUriProbeEvidenceFormatter
{
    private const string EvidenceKey = "DEBUG-415-uri-v1";
    private const int MaximumUriHexCharacters = 4096 * 4;
    private const int MaximumOriginTextCharacters = 16384;

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

                var textRemaining = MaximumOriginTextCharacters;
                var capturedUri = TakeHex(evidence, "uriUtf16Hex");
                var capturedPeer = TakeHex(evidence, "comparisonOtherUriUtf16Hex");
                var uriCodeUnitLength = GetInt(evidence, "uriCodeUnitLength");
                if (uriCodeUnitLength is null or < 0 || capturedUri is null)
                {
                    return "{\"status\":\"unavailable\"}";
                }

                var phase = TakeText(evidence, "phase", 128, ref textRemaining);
                var stage = TakeText(evidence, "stage", 128, ref textRemaining);
                var comparisonSide = TakeText(evidence, "comparisonSide", 16, ref textRemaining);
                var origins = TakeText(evidence, "origins", 128, ref textRemaining);
                var originDetails = CaptureOriginDetails(evidence, ref textRemaining, out var detailsCaptured);
                var peerLength = GetInt(evidence, "comparisonOtherUriCodeUnitLength");
                var rawOrigins = evidence.GetValueOrDefault("origins") as string;
                var uriLength = uriCodeUnitLength.Value;
                return JsonSerializer.Serialize(new
                {
                    status = "available",
                    phase,
                    stage,
                    uriCodeUnitLength = uriLength,
                    uriCapturedCodeUnits = capturedUri!.Length / 4,
                    uriCaptureComplete = capturedUri.Length == (long)uriLength * 4,
                    uriUtf16Hex = capturedUri,
                    comparisonSide,
                    comparisonOtherUriCodeUnitLength = peerLength,
                    comparisonOtherUriCapturedCodeUnits = capturedPeer?.Length / 4,
                    comparisonOtherUriCaptureComplete = peerLength is { } length
                        && capturedPeer?.Length == (long)length * 4,
                    comparisonOtherUriUtf16Hex = capturedPeer,
                    origins,
                    originsComplete = GetBool(evidence, "originsComplete") is true
                        && rawOrigins is not null && origins == rawOrigins,
                    inventoryIndex = GetInt(evidence, "inventoryIndex"),
                    originMatchesObserved = GetInt(evidence, "originMatchesObserved"),
                    originScanComplete = GetBool(evidence, "originScanComplete"),
                    originDetailsComplete = GetBool(evidence, "originDetailsComplete") is true
                        && detailsCaptured,
                    originDetails
                });
            }
            catch (Exception)
            {
                return "{\"status\":\"unavailable\"}";
            }
        }

        return null;
    }

    private static string? TakeHex(IReadOnlyDictionary<string, object> evidence, string key)
    {
        if (!evidence.TryGetValue(key, out var value) || value is not string hex)
        {
            return null;
        }

        var length = Math.Min(hex.Length, MaximumUriHexCharacters) / 4 * 4;
        var captured = hex[..length];
        return captured.All(char.IsAsciiHexDigit) ? captured : null;
    }

    private static string? TakeText(
        IReadOnlyDictionary<string, object> evidence,
        string key,
        int maximumLength,
        ref int remaining)
    {
        if (!evidence.TryGetValue(key, out var value) || value is not string text)
        {
            return null;
        }

        var length = Math.Min(text.Length, Math.Min(maximumLength, remaining));
        remaining -= length;
        return text[..length];
    }

    private static int? GetInt(IReadOnlyDictionary<string, object> evidence, string key)
        => evidence.TryGetValue(key, out var value) && value is int number ? number : null;

    private static bool? GetBool(IReadOnlyDictionary<string, object> evidence, string key)
        => evidence.TryGetValue(key, out var value) && value is bool flag ? flag : null;

    private static IReadOnlyList<Dictionary<string, object?>> CaptureOriginDetails(
        IReadOnlyDictionary<string, object> evidence,
        ref int textRemaining,
        out bool complete)
    {
        if (!evidence.TryGetValue("originDetails", out var value)
            || value is not List<Dictionary<string, object>> details)
        {
            complete = false;
            return [];
        }

        complete = details.Count <= 32;
        var result = new List<Dictionary<string, object?>>(Math.Min(details.Count, 32));
        foreach (var detail in details.Take(32))
        {
            var captured = new Dictionary<string, object?>(StringComparer.Ordinal);
            var entryComplete = GetBool(detail, "fieldsComplete") is true;
            foreach (var key in new[] { "category", "documentUri", "moduleName", "definitionName",
                         "definitionModuleName", "definitionKind", "identityOrigin", "identityName",
                         "identitySourceUri", "identityKind", "referenceName", "parentTypeName",
                         "propertyAccessorKind" })
            {
                var text = TakeText(detail, key, 2048, ref textRemaining);
                if (text is not null)
                {
                    captured[key] = text;
                    entryComplete &= detail[key] is string original && text.Length == original.Length;
                }
            }

            foreach (var key in new[] { "documentIndex", "definitionIndex", "startLine",
                         "startCharacter", "endLine", "endCharacter", "identityStartLine",
                         "identityStartCharacter", "identityEndLine", "identityEndCharacter" })
            {
                if (GetInt(detail, key) is { } number)
                {
                    captured[key] = number;
                }
            }

            captured["fieldsComplete"] = entryComplete;
            complete &= entryComplete;

            result.Add(captured);
        }

        return result;
    }
}
