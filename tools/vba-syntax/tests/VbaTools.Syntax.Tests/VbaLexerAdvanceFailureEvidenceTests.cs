using System.Reflection;
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

    [Fact]
    public void SliceFailureRecorderPreservesOriginalExceptionAndPrePostCursorWithoutSourceText()
    {
        const string source = "PRIVATE_SOURCE_TEXT\nName";
        var original = new ArgumentOutOfRangeException("endOffset", "original slice failure");
        var position = new VbaSyntaxPosition(1, 3, 23);

        VbaLexerAdvanceFailureEvidence.CaptureSlice(
            original, source, source.Length, true, false, false, position, false, true,
            1, 2, 22, 1, 3, 23, 20, 30);

        var evidence = Assert.IsType<Dictionary<string, object>>(
            original.Data[VbaLexerAdvanceFailureEvidence.Key]);
        Assert.Equal("LexerState.Slice", evidence["phase"]);
        Assert.Equal(20, evidence["sliceStartOffset"]);
        Assert.Equal(30, evidence["sliceEndOffset"]);
        Assert.Equal(1, evidence["slicePreLine"]);
        Assert.Equal(2, evidence["slicePreCharacter"]);
        Assert.Equal(22, evidence["slicePreOffset"]);
        Assert.Equal(1, evidence["slicePostLine"]);
        Assert.Equal(3, evidence["slicePostCharacter"]);
        Assert.Equal(23, evidence["slicePostOffset"]);
        Assert.Equal(source.Length, evidence["sourceLength"]);
        Assert.Equal(source.Length, evidence["slicePreSourceLength"]);
        Assert.Equal(true, evidence["slicePrePostSourceSameReference"]);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(source.AsSpan()))),
            evidence["sourceSha256"]);
        Assert.DoesNotContain(source, string.Join(" ", evidence.Values));
        Assert.Contains("original slice failure", original.Message);
    }

    [Fact]
    public void InvalidLexerSliceRetainsOriginalRangeExceptionWithFailureOnlyEvidence()
    {
        // [DEBUG-415-lexer-v1] The private slice boundary is the only deterministic way to
        // exercise this temporary failure probe; ordinary tokenization always supplies valid offsets.
        const string source = "PRIVATE_SOURCE_TEXT";
        var stateType = Assert.IsType<Type>(
            typeof(VbaLexer).GetNestedType("LexerState", BindingFlags.NonPublic), exactMatch: false);
        var constructor = Assert.Single(stateType.GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        var state = constructor.Invoke([VbaSourceText.From(source)]);
        var slice = Assert.IsType<MethodInfo>(stateType.GetMethod("Slice"), exactMatch: false);

        var wrapper = Assert.Throws<TargetInvocationException>(() =>
            slice.Invoke(state, [0, source.Length + 1]));

        var original = Assert.IsType<ArgumentOutOfRangeException>(wrapper.InnerException);
        var evidence = Assert.IsType<Dictionary<string, object>>(
            original.Data[VbaLexerAdvanceFailureEvidence.Key]);
        Assert.Equal("LexerState.Slice", evidence["phase"]);
        Assert.Equal(0, evidence["sliceStartOffset"]);
        Assert.Equal(source.Length + 1, evidence["sliceEndOffset"]);
        Assert.Equal(0, evidence["slicePreOffset"]);
        Assert.Equal(0, evidence["slicePostOffset"]);
        Assert.Equal(source.Length, evidence["sourceLength"]);
        Assert.Equal(source.Length, evidence["slicePreSourceLength"]);
        Assert.Equal(true, evidence["slicePrePostSourceSameReference"]);
        Assert.DoesNotContain(source, string.Join(" ", evidence.Values));
    }
}
