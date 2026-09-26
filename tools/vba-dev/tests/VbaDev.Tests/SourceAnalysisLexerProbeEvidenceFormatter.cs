using System.Text.Json;

namespace VbaDev.Tests;

// [DEBUG-415-lexer-v1] Temporary primitive-only formatter for the read-only probe.
internal static class SourceAnalysisLexerProbeEvidenceFormatter
{
    private const string EvidenceKey = "DEBUG-415-lexer-v1";

    internal static string? Format(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            try
            {
                if (current.Data[EvidenceKey] is not Dictionary<string, object> evidence)
                    continue;
                if (GetPhase(evidence) is not { } phase
                    || GetBool(evidence, "stateIsNull") is not { } stateIsNull
                    || GetBool(evidence, "sourceTextIsNull") is not { } sourceTextIsNull
                    || GetBool(evidence, "textIsNull") is not { } textIsNull
                    || GetBool(evidence, "positionIsNull") is not { } positionIsNull)
                    return "{\"status\":\"unavailable\"}";

                var hash = GetSha256(evidence);
                return JsonSerializer.Serialize(new
                {
                    status = "available",
                    phase,
                    stateIsNull,
                    sourceTextIsNull,
                    textIsNull,
                    textReadFailed = GetBool(evidence, "textReadFailed"),
                    positionIsNull,
                    positionReadFailed = GetBool(evidence, "positionReadFailed"),
                    cachedPositionIsNull = GetBool(evidence, "cachedPositionIsNull"),
                    rawLine = GetInt(evidence, "rawLine"),
                    rawCharacter = GetInt(evidence, "rawCharacter"),
                    rawOffset = GetInt(evidence, "rawOffset"),
                    positionLine = GetInt(evidence, "positionLine"),
                    positionCharacter = GetInt(evidence, "positionCharacter"),
                    positionOffset = GetInt(evidence, "positionOffset"),
                    startLine = GetInt(evidence, "startLine"),
                    startCharacter = GetInt(evidence, "startCharacter"),
                    startOffset = GetInt(evidence, "startOffset"),
                    identifierLength = GetInt(evidence, "identifierLength"),
                    loopIndex = GetInt(evidence, "loopIndex"),
                    sourceLength = GetInt(evidence, "sourceLength"),
                    sourceHashDomain = "utf16-platform-endian-code-units",
                    sourceSha256 = hash,
                    sourceHashComplete = GetBool(evidence, "sourceHashComplete") is true && hash is not null
                });
            }
            catch (Exception)
            {
                return "{\"status\":\"unavailable\"}";
            }
        }

        return null;
    }

    private static int? GetInt(IReadOnlyDictionary<string, object> evidence, string key)
        => evidence.TryGetValue(key, out var value) && value is int number ? number : null;

    private static bool? GetBool(IReadOnlyDictionary<string, object> evidence, string key)
        => evidence.TryGetValue(key, out var value) && value is bool flag ? flag : null;

    private static string? GetPhase(IReadOnlyDictionary<string, object> evidence)
        => evidence.TryGetValue("phase", out var value) && value is string phase
            && phase is "ReadIdentifierOrKeyword.Advance"
                or "ReadIdentifierOrKeyword.StartOffset"
                or "ReadIdentifierOrKeyword.PositionBeforeSlice"
                or "ReadIdentifierOrKeyword.Slice"
                ? phase : null;

    private static string? GetSha256(IReadOnlyDictionary<string, object> evidence)
        => evidence.TryGetValue("sourceSha256", out var value)
            && value is string hash && hash.Length == 64 && hash.All(char.IsAsciiHexDigit)
                ? hash : null;
}
