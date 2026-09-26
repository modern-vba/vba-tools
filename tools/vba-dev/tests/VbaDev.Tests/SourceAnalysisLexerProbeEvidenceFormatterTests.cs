using System.Text.Json;
using Xunit;

namespace VbaDev.Tests;

public sealed class SourceAnalysisLexerProbeEvidenceFormatterTests
{
    [Fact]
    public void NestedFailurePrintsOnlyPrimitiveLexerEvidence()
    {
        var original = new NullReferenceException("original lexer failure");
        var evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["phase"] = "ReadIdentifierOrKeyword.PositionBeforeSlice",
            ["stateIsNull"] = false,
            ["sourceTextIsNull"] = false,
            ["textIsNull"] = false,
            ["textReadFailed"] = false,
            ["positionIsNull"] = false,
            ["positionReadFailed"] = false,
            ["cachedPositionIsNull"] = true,
            ["rawLine"] = 1,
            ["rawCharacter"] = 2,
            ["rawOffset"] = 22,
            ["positionLine"] = 1,
            ["positionCharacter"] = 2,
            ["positionOffset"] = 22,
            ["startLine"] = 1,
            ["startCharacter"] = 0,
            ["startOffset"] = 20,
            ["identifierLength"] = 4,
            ["loopIndex"] = 2,
            ["sourceLength"] = 24,
            ["sourceSha256"] = new string('A', 64),
            ["sourceHashComplete"] = true,
            ["unrelated"] = "PRIVATE_SOURCE_TEXT"
        };
        original.Data["DEBUG-415-lexer-v1"] = evidence;
        var wrapper = new InvalidOperationException("wrapper", original);

        var formatted = Assert.IsType<string>(SourceAnalysisLexerProbeEvidenceFormatter.Format(wrapper));
        using var parsed = JsonDocument.Parse(formatted);
        var captured = parsed.RootElement;
        Assert.Equal("available", captured.GetProperty("status").GetString());
        Assert.Equal("ReadIdentifierOrKeyword.PositionBeforeSlice", captured.GetProperty("phase").GetString());
        Assert.Equal(22, captured.GetProperty("rawOffset").GetInt32());
        Assert.Equal(2, captured.GetProperty("loopIndex").GetInt32());
        Assert.True(captured.GetProperty("sourceHashComplete").GetBoolean());
        Assert.DoesNotContain("PRIVATE_SOURCE_TEXT", formatted);
        Assert.Same(evidence, original.Data["DEBUG-415-lexer-v1"]);
        Assert.Same(original, wrapper.InnerException);
    }

    [Fact]
    public void MissingOrInvalidEvidenceCannotClaimACompleteSourceHash()
    {
        Assert.Null(SourceAnalysisLexerProbeEvidenceFormatter.Format(new NullReferenceException()));
        var malformed = new NullReferenceException();
        malformed.Data["DEBUG-415-lexer-v1"] = new Dictionary<string, object>
        {
            ["stateIsNull"] = "invalid"
        };
        Assert.Equal("{\"status\":\"unavailable\"}",
            SourceAnalysisLexerProbeEvidenceFormatter.Format(malformed));

        var incomplete = new NullReferenceException();
        incomplete.Data["DEBUG-415-lexer-v1"] = new Dictionary<string, object>
        {
            ["phase"] = "ReadIdentifierOrKeyword.Slice",
            ["stateIsNull"] = false,
            ["sourceTextIsNull"] = false,
            ["textIsNull"] = false,
            ["positionIsNull"] = false,
            ["sourceSha256"] = "invalid",
            ["sourceHashComplete"] = true
        };
        var formatted = Assert.IsType<string>(SourceAnalysisLexerProbeEvidenceFormatter.Format(incomplete));
        using var parsed = JsonDocument.Parse(formatted);
        Assert.False(parsed.RootElement.GetProperty("sourceHashComplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("sourceSha256").ValueKind);
    }

    [Theory]
    [InlineData("ReadIdentifierOrKeyword.Advance")]
    [InlineData("ReadIdentifierOrKeyword.StartOffset")]
    [InlineData("ReadIdentifierOrKeyword.PositionBeforeSlice")]
    [InlineData("ReadIdentifierOrKeyword.Slice")]
    public void FormatterPreservesTheObservedPhase(string phase)
    {
        var original = new NullReferenceException();
        original.Data["DEBUG-415-lexer-v1"] = new Dictionary<string, object>
        {
            ["phase"] = phase,
            ["stateIsNull"] = false,
            ["sourceTextIsNull"] = false,
            ["textIsNull"] = false,
            ["positionIsNull"] = false
        };

        var formatted = Assert.IsType<string>(SourceAnalysisLexerProbeEvidenceFormatter.Format(original));
        using var parsed = JsonDocument.Parse(formatted);
        Assert.Equal(phase, parsed.RootElement.GetProperty("phase").GetString());
    }
}
