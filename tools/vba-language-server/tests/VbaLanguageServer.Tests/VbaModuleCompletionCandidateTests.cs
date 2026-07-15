using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaModuleCompletionCandidateTests
{
    [Fact]
    public void ModuleStarterCandidatesRespectTheActiveModuleKind()
    {
        var standard = Complete(
            "file:///C:/work/Main.bas",
            string.Join('\n', [
                "Attribute VB_Name = \"Main\"",
                "|"
            ]));
        var objectModule = Complete(
            "file:///C:/work/Worker.cls",
            string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "|"
            ]));

        Assert.Contains(standard.Candidates, candidate => candidate.Label == "Declare");
        Assert.Contains(standard.Candidates, candidate => candidate.Label == "Type");
        Assert.DoesNotContain(standard.Candidates, candidate => candidate.Label == "Event");
        Assert.DoesNotContain(standard.Candidates, candidate => candidate.Label == "Friend");
        Assert.DoesNotContain(standard.Candidates, candidate => candidate.Label == "Property");
        Assert.DoesNotContain(standard.Candidates, candidate => candidate.Label == "WithEvents");

        Assert.Contains(objectModule.Candidates, candidate => candidate.Label == "Event");
        Assert.Contains(objectModule.Candidates, candidate => candidate.Label == "Friend");
        Assert.Contains(objectModule.Candidates, candidate => candidate.Label == "Property");
        Assert.Contains(objectModule.Candidates, candidate => candidate.Label == "WithEvents");
        Assert.DoesNotContain(objectModule.Candidates, candidate => candidate.Label == "Declare");
        Assert.DoesNotContain(objectModule.Candidates, candidate => candidate.Label == "Enum");
        Assert.DoesNotContain(objectModule.Candidates, candidate => candidate.Label == "Global");
        Assert.DoesNotContain(objectModule.Candidates, candidate => candidate.Label == "Type");
    }

    [Fact]
    public void OptionIsNotACompletionCandidateAfterAProcedureDeclaration()
    {
        var completion = Complete(
            "file:///C:/work/Main.bas",
            string.Join('\n', [
                "Attribute VB_Name = \"Main\"",
                "Public Sub Run()",
                "End Sub",
                "|"
            ]));

        Assert.DoesNotContain(completion.Candidates, candidate => candidate.Label == "Option");
        Assert.Contains(completion.Candidates, candidate => candidate.Label == "Public");
    }

    private static VbaCompletionResult Complete(string uri, string markedSource)
    {
        var marker = markedSource.IndexOf('|');
        Assert.True(marker >= 0);
        var prefix = markedSource[..marker];
        var line = prefix.Count(character => character == '\n');
        var lineStart = prefix.LastIndexOf('\n');
        var character = marker - lineStart - 1;
        var index = VbaSourceIndex.Build(new Dictionary<string, string>
        {
            [uri] = markedSource.Remove(marker, 1)
        });

        return index.GetCompletionResult(uri, line, character);
    }
}
