using Xunit;

namespace VbaTools.Syntax.Tests;

public sealed class VbaPositionSyntaxPrefixFailureEvidenceTests
{
    private static readonly VbaSyntaxPosition Position = new(4, 7, 35);

    [Theory]
    [InlineData("FindIdentifier.query")]
    [InlineData("GetProcedureSyntaxWords.prefix")]
    public void BrokenTokenReferenceIsRecordedWithoutSourceTextOrReplacingTheFailure(string phase)
    {
        var original = new NullReferenceException("original position failure");
        var tokens = new VbaToken[]
        {
            new(
                VbaTokenKind.Identifier,
                "PRIVATE_SOURCE_TEXT",
                new VbaSyntaxRange(new VbaSyntaxPosition(4, 0, 28), Position)),
            null!
        };

        VbaPositionSyntaxPrefixFailureEvidence.Capture(
            original, phase, "file:///source/Module.bas", Position, 28, 42, 43, tokens);

        var evidence = Assert.IsType<Dictionary<string, object>>(
            original.Data[VbaPositionSyntaxPrefixFailureEvidence.Key]);
        Assert.Equal(phase, evidence["phase"]);
        Assert.Equal("file:///source/Module.bas", evidence["uri"]);
        Assert.Equal(false, evidence["positionIsNull"]);
        Assert.Equal(4, evidence["positionLine"]);
        Assert.Equal(35, evidence["positionOffset"]);
        Assert.Equal(28, evidence["statementStartOffset"]);
        Assert.Equal(42, evidence["statementEndOffset"]);
        Assert.Equal(2, evidence["significantTokenCount"]);
        Assert.Equal(1, evidence["firstBadIndex"]);
        Assert.Equal("token", evidence["firstBadReferenceKind"]);
        Assert.Equal("broken", evidence["postFaultGraphStatus"]);
        Assert.DoesNotContain("PRIVATE_SOURCE_TEXT", string.Join(" ", evidence.Values));
        Assert.Equal("original position failure", original.Message);
    }

    [Fact]
    public void RangeAndStartFailuresAreDistinguishedFromAnIntactPostFaultGraph()
    {
        var valid = new VbaToken(
            VbaTokenKind.Identifier,
            "PRIVATE_SOURCE_TEXT",
            new VbaSyntaxRange(new VbaSyntaxPosition(4, 0, 28), Position));

        Assert.Equal("range", CaptureKind(new VbaToken[] { valid, valid with { Range = null! } }));
        Assert.Equal("start", CaptureKind(new VbaToken[]
        {
            valid,
            valid with { Range = new VbaSyntaxRange(null!, Position) }
        }));

        var original = new NullReferenceException("original");
        VbaPositionSyntaxPrefixFailureEvidence.Capture(
            original, "TryGetLabelReference.prefix", "file:///source/Module.bas", Position, 28, 42, 43, [valid]);
        var evidence = Assert.IsType<Dictionary<string, object>>(
            original.Data[VbaPositionSyntaxPrefixFailureEvidence.Key]);
        Assert.Equal("intact", evidence["postFaultGraphStatus"]);
        Assert.Equal(true, evidence["inspectionComplete"]);
        Assert.Equal(-1, evidence["firstBadIndex"]);
    }

    [Fact]
    public void OversizedUriAndTokenListRemainBoundedAndUnverified()
    {
        var original = new NullReferenceException("original");
        var valid = new VbaToken(
            VbaTokenKind.Identifier,
            "PRIVATE_SOURCE_TEXT",
            new VbaSyntaxRange(Position, Position));
        var tokens = Enumerable.Repeat(valid, 4097).ToArray();

        VbaPositionSyntaxPrefixFailureEvidence.Capture(
            original, "TryGetLabelReference.prefix", new string('u', 3000), Position, 28, 42, 43, tokens);

        var evidence = Assert.IsType<Dictionary<string, object>>(
            original.Data[VbaPositionSyntaxPrefixFailureEvidence.Key]);
        Assert.Equal(2048, Assert.IsType<string>(evidence["uri"]).Length);
        Assert.Equal(3000, evidence["uriLength"]);
        Assert.Equal(false, evidence["uriCaptureComplete"]);
        Assert.Equal(4097, evidence["significantTokenCount"]);
        Assert.Equal(4096, evidence["inspectedTokenCount"]);
        Assert.Equal(false, evidence["inspectionComplete"]);
        Assert.Equal("unverified", evidence["postFaultGraphStatus"]);
    }

    [Fact]
    public void MissingTokenListCannotReplaceTheOriginalFailure()
    {
        var original = new NullReferenceException("original");

        VbaPositionSyntaxPrefixFailureEvidence.Capture(
            original, "TryGetLabelReference.prefix", null, Position, 28, 42, 43, null);

        var evidence = Assert.IsType<Dictionary<string, object>>(
            original.Data[VbaPositionSyntaxPrefixFailureEvidence.Key]);
        Assert.Equal("significantTokens", evidence["firstBadReferenceKind"]);
        Assert.Equal("broken", evidence["postFaultGraphStatus"]);
        Assert.Equal(-1, evidence["uriLength"]);
        Assert.Equal("original", original.Message);
    }

    [Fact]
    public void NullPositionIsRecordedWithSentinelCoordinatesWithoutReplacingTheFailure()
    {
        var original = new NullReferenceException("original null position failure");

        VbaPositionSyntaxPrefixFailureEvidence.Capture(
            original, "TryGetLabelReference.prefix", "file:///source/Module.bas", null, 28, 42, 43, []);

        var evidence = Assert.IsType<Dictionary<string, object>>(
            original.Data[VbaPositionSyntaxPrefixFailureEvidence.Key]);
        Assert.Equal(true, evidence["positionIsNull"]);
        Assert.Equal(-1, evidence["positionLine"]);
        Assert.Equal(-1, evidence["positionCharacter"]);
        Assert.Equal(-1, evidence["positionOffset"]);
        Assert.Equal("intact", evidence["postFaultGraphStatus"]);
        Assert.Equal("original null position failure", original.Message);
    }

    private static string CaptureKind(IReadOnlyList<VbaToken> tokens)
    {
        var original = new NullReferenceException("original");
        VbaPositionSyntaxPrefixFailureEvidence.Capture(
            original, "TryGetLabelReference.prefix", "file:///source/Module.bas", Position, 28, 42, 43, tokens);
        var evidence = Assert.IsType<Dictionary<string, object>>(
            original.Data[VbaPositionSyntaxPrefixFailureEvidence.Key]);
        Assert.Equal(1, evidence["firstBadIndex"]);
        Assert.Equal("broken", evidence["postFaultGraphStatus"]);
        return Assert.IsType<string>(evidence["firstBadReferenceKind"]);
    }
}
