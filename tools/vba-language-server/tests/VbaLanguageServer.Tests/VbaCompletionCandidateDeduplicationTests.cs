using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaCompletionCandidateDeduplicationTests
{
    [Fact]
    public void NamedArgumentAndReadableDefinitionWithSameLabelRemainDistinctCandidates()
    {
        const string uri = "file:///C:/work/Main.bas";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Function Convert(ByVal Value As Long) As Long",
            "End Function",
            "Public Sub Probe()",
            "    Dim result As Long",
            "    Dim Value As Long",
            "    result = Convert(",
            "End Sub"
        ]);
        var index = VbaSourceIndex.Build(new Dictionary<string, string>
        {
            [uri] = source
        });

        var completion = index.GetCompletionResult(uri, 6, "    result = Convert(".Length);
        var sameLabel = completion.Candidates
            .Where(candidate => candidate.Label == "Value")
            .ToArray();

        Assert.Equal(2, sameLabel.Length);
        Assert.Contains(sameLabel, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.Definition
            && candidate.InsertText is null);
        Assert.Contains(sameLabel, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.NamedArgument
            && candidate.InsertText == "Value:=");
    }
}
