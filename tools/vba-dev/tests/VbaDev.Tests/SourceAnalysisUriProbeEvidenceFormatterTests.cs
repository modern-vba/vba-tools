using System.Text.Json;
using Xunit;

namespace VbaDev.Tests;

public sealed class SourceAnalysisUriProbeEvidenceFormatterTests
{
    [Fact]
    public void NestedUriFailureRetainsExactCodeUnitsWithoutChangingTheOriginalException()
    {
        var failure = new NullReferenceException("original URI failure");
        var evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["phase"] = "Uri.TryCreate",
            ["stage"] = "inventory.admittedIdentities",
            ["uriCodeUnitLength"] = 3,
            ["uriUtf16Hex"] = "0041D842DFB7",
            ["origins"] = "activeReferenceDefinitionUri",
            ["originsComplete"] = true,
            ["inventoryIndex"] = 41,
            ["originMatchesObserved"] = 1,
            ["originScanComplete"] = true,
            ["originDetailsComplete"] = true,
            ["originDetails"] = new List<Dictionary<string, object>>
            {
                new(StringComparer.Ordinal)
                {
                    ["category"] = "activeReferenceDefinitionUri",
                    ["referenceName"] = "Excel",
                    ["definitionIndex"] = 4,
                    ["fieldsComplete"] = true,
                    ["unrelated"] = "must not be printed"
                }
            },
            ["unrelated"] = "must not be printed"
        };
        failure.Data["DEBUG-415-uri-v1"] = evidence;
        var wrapper = new InvalidOperationException("wrapper", failure);

        var formatted = Assert.IsType<string>(SourceAnalysisUriProbeEvidenceFormatter.Format(wrapper));
        using var parsed = JsonDocument.Parse(formatted);
        var captured = parsed.RootElement;
        Assert.Equal("available", captured.GetProperty("status").GetString());
        Assert.Equal("Uri.TryCreate", captured.GetProperty("phase").GetString());
        Assert.Equal("0041D842DFB7", captured.GetProperty("uriUtf16Hex").GetString());
        Assert.True(captured.GetProperty("uriCaptureComplete").GetBoolean());
        Assert.Equal(41, captured.GetProperty("inventoryIndex").GetInt32());
        Assert.Equal("Excel", Assert.Single(captured.GetProperty("originDetails").EnumerateArray())
            .GetProperty("referenceName").GetString());
        Assert.DoesNotContain("must not be printed", formatted);
        Assert.Same(failure, wrapper.InnerException);
        Assert.Same(evidence, failure.Data["DEBUG-415-uri-v1"]);
        Assert.Equal("original URI failure", failure.Message);
    }

    [Fact]
    public void MissingOrMalformedUriEvidenceCannotClaimAnExactInput()
    {
        Assert.Null(SourceAnalysisUriProbeEvidenceFormatter.Format(new NullReferenceException()));

        var failure = new NullReferenceException();
        failure.Data["DEBUG-415-uri-v1"] = new Dictionary<string, object>
        {
            ["uriCodeUnitLength"] = 1,
            ["uriUtf16Hex"] = "not hex"
        };
        Assert.Equal("{\"status\":\"unavailable\"}",
            SourceAnalysisUriProbeEvidenceFormatter.Format(failure));
    }

    [Fact]
    public void OversizedUriEvidenceIsExplicitlyIncomplete()
    {
        var failure = new NullReferenceException();
        failure.Data["DEBUG-415-uri-v1"] = new Dictionary<string, object>
        {
            ["uriCodeUnitLength"] = 5000,
            ["uriUtf16Hex"] = new string('A', 5000 * 4)
        };

        var formatted = Assert.IsType<string>(SourceAnalysisUriProbeEvidenceFormatter.Format(failure));
        using var parsed = JsonDocument.Parse(formatted);
        Assert.Equal(4096, parsed.RootElement.GetProperty("uriCapturedCodeUnits").GetInt32());
        Assert.False(parsed.RootElement.GetProperty("uriCaptureComplete").GetBoolean());
    }
}
