using System.Text.Json;
using VbaDebugAdapter.Build;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class DebugSourceIdentityTests
{
    [Fact]
    public void RetainedIdentityDoesNotChangeTransportJsonAndOriginalUrisRoundTrip()
    {
        var snapshot = CreateSnapshot();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.SerializeToElement(snapshot, options);
        var source = json.GetProperty("sources")[0];
        Assert.Equal(["contentBase64", "encoding", "relativePath", "sourceUri"],
            source.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["character", "line", "sourceUri"],
            json.GetProperty("activeSource").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["line", "sourceUri"],
            json.GetProperty("breakpoints")[0].EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(snapshot.Sources[0].SourceUri, source.GetProperty("sourceUri").GetString());
        var roundTrip = JsonSerializer.Deserialize<TransportedDebugSourceSnapshot>(json, options)!;
        Assert.Equal(snapshot.Sources[0], roundTrip.Sources[0]);
        Assert.Equal(snapshot.ActiveSource, roundTrip.ActiveSource);
        Assert.Equal(snapshot.Breakpoints[0], roundTrip.Breakpoints[0]);
        var admitted = new DebugSourceAdmission(932).Admit(roundTrip, null, null, DebugGenerationId.Initial);
        var breakpoint = Assert.Single(admitted.MappedBreakpoints).Source;
        var breakpointJson = JsonSerializer.SerializeToElement(breakpoint, options);
        Assert.Equal(["editorLine", "sourceUri"],
            breakpointJson.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(snapshot.Breakpoints[0].SourceUri, breakpointJson.GetProperty("sourceUri").GetString());
    }

    [Theory]
    [InlineData("source", "file:///C:/different/Module1.bas")]
    [InlineData("source", @"C:\persistent\Module1.bas")]
    [InlineData("active", "file:///C:/different/Module1.bas")]
    [InlineData("active", @"C:\persistent\Module1.bas")]
    [InlineData("breakpoint", "file:///C:/different/Module1.bas")]
    [InlineData("breakpoint", @"C:\persistent\Module1.bas")]
    public void TransportCopiesRebindTheirUriWithoutChangingPreviouslyAdmittedFacts(string changedPart, string changedUri)
    {
        var original = CreateSnapshot();
        var admission = new DebugSourceAdmission(932);
        var admitted = admission.Admit(original, null, null, DebugGenerationId.Initial);
        var changed = changedPart switch
        {
            "source" => original with { Sources = [original.Sources[0] with { SourceUri = changedUri }] },
            "active" => original with { ActiveSource = original.ActiveSource! with { SourceUri = changedUri } },
            "breakpoint" => original with { Breakpoints = [original.Breakpoints[0] with { SourceUri = changedUri }] },
            _ => throw new ArgumentOutOfRangeException(nameof(changedPart))
        };

        Assert.Throws<DebugSourceRejectedException>(() => admission.Admit(changed, null, null, DebugGenerationId.Initial));

        var readmitted = admission.Admit(original with { }, null, null, DebugGenerationId.Initial);
        Assert.Equal("Module1", readmitted.Target.ModuleName);
        Assert.Equal(original.ActiveSource!.SourceUri, readmitted.ActiveSource!.SourceUri);
        Assert.Equal(original.Breakpoints[0].SourceUri, Assert.Single(readmitted.MappedBreakpoints).Source.SourceUri);
        Assert.Equal(original.ActiveSource.SourceUri, admitted.ActiveSource!.SourceUri);
        Assert.Equal(original.Breakpoints[0].SourceUri, Assert.Single(admitted.MappedBreakpoints).Source.SourceUri);
    }

    private static TransportedDebugSourceSnapshot CreateSnapshot()
        => new(2,
        [
            new("Module1.bas", "file:///C:/persistent/Module1.bas?inventory#original", "utf8bom",
                Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(
                    "Attribute VB_Name = \"Module1\"\r\nPublic Sub Run()\r\n    Debug.Print 1\r\nEnd Sub\r\n")))
        ])
        {
            ActiveSource = new("file:///C%3A/persistent/%4dodule1.bas?active#original", 2, 4),
            Breakpoints = [new("file://localhost/C:/persistent/Module1.bas?breakpoint#original", 2)]
        };
}
