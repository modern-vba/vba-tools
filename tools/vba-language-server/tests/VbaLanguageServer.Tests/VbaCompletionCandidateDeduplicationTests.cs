using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaCompletionCandidateDeduplicationTests
{
    [Fact]
    public void SameLabelAndInsertionRemainDistinctWhenSemanticRolesDiffer()
    {
        const string workerUri = "file:///C:/work/Main.bas";
        const string classUri = "file:///C:/work/String.cls";
        var workerSource = string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Probe()",
            "    Dim result As ",
            "End Sub"
        ]);
        var classSource = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"String\"",
            "Attribute VB_Exposed = True"
        ]);
        var index = VbaSemanticInventoryFixture.Create(new Dictionary<string, string>
        {
            [workerUri] = workerSource,
            [classUri] = classSource
        });

        var completion = index.GetCompletionResult(
            workerUri,
            2,
            "    Dim result As ".Length);
        var sameLabel = completion.Candidates
            .Where(candidate => candidate.Label == "String")
            .ToArray();

        Assert.Equal(2, sameLabel.Length);
        Assert.Contains(sameLabel, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.Definition
            && candidate.Definition?.Kind == VbaSourceDefinitionKind.Class);
        Assert.Contains(sameLabel, candidate =>
            candidate.Kind == VbaCompletionCandidateKind.LanguageVocabulary);
    }

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
        var index = VbaSemanticInventoryFixture.Create(new Dictionary<string, string>
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
