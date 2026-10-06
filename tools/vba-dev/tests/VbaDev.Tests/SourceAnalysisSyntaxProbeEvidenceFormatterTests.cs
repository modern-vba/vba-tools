using System.Text.Json;
using Xunit;

namespace VbaDev.Tests;

public sealed class SourceAnalysisSyntaxProbeEvidenceFormatterTests
{
    [Theory]
    [InlineData("TryGetLabelReference.prefix")]
    [InlineData("FindIdentifier.query")]
    [InlineData("GetProcedureSyntaxWords.prefix")]
    public void NestedFailurePrintsOnlyBoundedWhitelistedPositionEvidence(string phase)
    {
        var original = new NullReferenceException("original syntax failure");
        var evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["phase"] = phase,
            ["uri"] = "file:///source/Module.bas",
            ["uriLength"] = 25,
            ["uriCaptureComplete"] = true,
            ["positionIsNull"] = false,
            ["positionLine"] = 4,
            ["positionCharacter"] = 7,
            ["positionOffset"] = 35,
            ["statementStartOffset"] = 28,
            ["statementEndOffset"] = 42,
            ["statementNextOffset"] = 43,
            ["significantTokenCount"] = 2,
            ["inspectedTokenCount"] = 2,
            ["inspectionComplete"] = false,
            ["firstBadIndex"] = 1,
            ["firstBadReferenceKind"] = "start",
            ["postFaultGraphStatus"] = "broken",
            ["unrelated"] = "PRIVATE_SOURCE_TEXT"
        };
        original.Data["DEBUG-415-syntax-v1"] = evidence;
        var wrapper = new InvalidOperationException("wrapper", original);

        var formatted = Assert.IsType<string>(SourceAnalysisSyntaxProbeEvidenceFormatter.Format(wrapper));
        using var parsed = JsonDocument.Parse(formatted);
        var captured = parsed.RootElement;
        Assert.Equal("available", captured.GetProperty("status").GetString());
        Assert.Equal(phase, captured.GetProperty("phase").GetString());
        Assert.Equal("file:///source/Module.bas", captured.GetProperty("uri").GetString());
        Assert.True(captured.GetProperty("uriCaptureComplete").GetBoolean());
        Assert.False(captured.GetProperty("positionIsNull").GetBoolean());
        Assert.Equal(35, captured.GetProperty("positionOffset").GetInt32());
        Assert.Equal(1, captured.GetProperty("firstBadIndex").GetInt32());
        Assert.Equal("start", captured.GetProperty("firstBadReferenceKind").GetString());
        Assert.DoesNotContain("PRIVATE_SOURCE_TEXT", formatted);
        Assert.Same(evidence, original.Data["DEBUG-415-syntax-v1"]);
        Assert.Same(original, wrapper.InnerException);
    }

    [Fact]
    public void MalformedEvidenceCannotClaimPositionObservation()
    {
        Assert.Null(SourceAnalysisSyntaxProbeEvidenceFormatter.Format(new NullReferenceException()));
        var original = new NullReferenceException();
        original.Data["DEBUG-415-syntax-v1"] = new Dictionary<string, object>
        {
            ["uri"] = "file:///source/Module.bas",
            ["uriLength"] = "invalid"
        };

        Assert.Equal("{\"status\":\"unavailable\"}",
            SourceAnalysisSyntaxProbeEvidenceFormatter.Format(original));
    }

    [Fact]
    public void FormatterMarksTruncatedUriAsIncomplete()
    {
        var original = new NullReferenceException();
        original.Data["DEBUG-415-syntax-v1"] = new Dictionary<string, object>
        {
            ["phase"] = "TryGetLabelReference.prefix",
            ["uri"] = new string('u', 3000),
            ["uriLength"] = 3000,
            ["uriCaptureComplete"] = true
        };

        var formatted = Assert.IsType<string>(SourceAnalysisSyntaxProbeEvidenceFormatter.Format(original));
        using var parsed = JsonDocument.Parse(formatted);
        Assert.Equal(2048, parsed.RootElement.GetProperty("uri").GetString()!.Length);
        Assert.False(parsed.RootElement.GetProperty("uriCaptureComplete").GetBoolean());
    }

    [Fact]
    public void UnknownPhaseCannotBeReportedAsObserved()
    {
        var original = new NullReferenceException();
        original.Data["DEBUG-415-syntax-v1"] = new Dictionary<string, object>
        {
            ["phase"] = "unrelated",
            ["uri"] = "file:///source/Module.bas",
            ["uriLength"] = 25
        };

        Assert.Equal("{\"status\":\"unavailable\"}",
            SourceAnalysisSyntaxProbeEvidenceFormatter.Format(original));
    }
}
