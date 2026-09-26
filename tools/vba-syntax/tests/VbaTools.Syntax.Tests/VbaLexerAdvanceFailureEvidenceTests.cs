using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Xunit;

namespace VbaTools.Syntax.Tests;

public sealed class VbaLexerAdvanceFailureEvidenceTests
{
    [Fact]
    public void FailureRecorderPreservesExceptionAndHashesSourceWithoutRetainingText()
    {
        const string source = "PRIVATE_SOURCE_TEXT\nName";
        var original = new NullReferenceException("original lexer failure");
        var position = new VbaSyntaxPosition(1, 2, 22);
        var start = new VbaSyntaxPosition(1, 0, 20);

        VbaLexerAdvanceFailureEvidence.Capture(
            original, "ReadIdentifierOrKeyword.PositionBeforeSlice",
            false, false, source, false, position, false, true,
            1, 2, 22, start, 4, 2);

        var evidence = Assert.IsType<Dictionary<string, object>>(
            original.Data[VbaLexerAdvanceFailureEvidence.Key]);
        Assert.Equal("ReadIdentifierOrKeyword.PositionBeforeSlice", evidence["phase"]);
        Assert.Equal(false, evidence["stateIsNull"]);
        Assert.Equal(false, evidence["sourceTextIsNull"]);
        Assert.Equal(false, evidence["textIsNull"]);
        Assert.Equal(1, evidence["rawLine"]);
        Assert.Equal(22, evidence["rawOffset"]);
        Assert.Equal(4, evidence["identifierLength"]);
        Assert.Equal(2, evidence["loopIndex"]);
        Assert.Equal(source.Length, evidence["sourceLength"]);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(source.AsSpan()))),
            evidence["sourceSha256"]);
        Assert.Equal(true, evidence["sourceHashComplete"]);
        Assert.DoesNotContain(source, string.Join(" ", evidence.Values));
        Assert.Equal("original lexer failure", original.Message);
    }

    [Fact]
    public void MissingStateAndTextRemainExplicitWithSentinelCoordinates()
    {
        var original = new NullReferenceException("original null state");

        VbaLexerAdvanceFailureEvidence.Capture(
            original, "ReadIdentifierOrKeyword.Advance",
            true, true, null, false, null, false, true,
            -1, -1, -1, null, 3, 0);

        var evidence = Assert.IsType<Dictionary<string, object>>(
            original.Data[VbaLexerAdvanceFailureEvidence.Key]);
        Assert.Equal("ReadIdentifierOrKeyword.Advance", evidence["phase"]);
        Assert.Equal(true, evidence["stateIsNull"]);
        Assert.Equal(true, evidence["sourceTextIsNull"]);
        Assert.Equal(true, evidence["textIsNull"]);
        Assert.Equal(true, evidence["positionIsNull"]);
        Assert.Equal(-1, evidence["positionOffset"]);
        Assert.Equal(-1, evidence["sourceLength"]);
        Assert.Equal(string.Empty, evidence["sourceSha256"]);
        Assert.Equal(false, evidence["sourceHashComplete"]);
        Assert.Equal("original null state", original.Message);
    }
}
