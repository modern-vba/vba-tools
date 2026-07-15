using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaSyntaxWordCompletionTests
{
    [Fact]
    public void GeneralSyntaxWordExpectationProjectsThroughThePrimarySyntaxFacts()
    {
        const string uri = "file:///C:/work/Main.bas";
        var markedSource = string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Run()",
            "    Select |",
            "End Sub"
        ]);
        var markerOffset = markedSource.IndexOf('|');
        var prefix = markedSource[..markerOffset];
        var line = prefix.Count(character => character == '\n');
        var character = markerOffset - prefix.LastIndexOf('\n') - 1;
        var index = VbaSourceIndex.Build(new Dictionary<string, string>
        {
            [uri] = markedSource.Remove(markerOffset, 1)
        });

        var completion = index.GetCompletionResult(uri, line, character);

        var candidate = Assert.Single(completion.Candidates);
        Assert.Equal("Case", candidate.Label);
        Assert.Equal(VbaCompletionCandidateKind.LanguageVocabulary, candidate.Kind);
    }

    [Fact]
    public void ForTargetCompletionCombinesWritableDefinitionsWithEach()
    {
        const string uri = "file:///C:/work/Main.bas";
        var markedSource = string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Run()",
            "    Dim entry As Variant",
            "    For |",
            "    Next",
            "End Sub"
        ]);
        var markerOffset = markedSource.IndexOf('|');
        var prefix = markedSource[..markerOffset];
        var line = prefix.Count(character => character == '\n');
        var character = markerOffset - prefix.LastIndexOf('\n') - 1;
        var index = VbaSourceIndex.Build(new Dictionary<string, string>
        {
            [uri] = markedSource.Remove(markerOffset, 1)
        });

        var completion = index.GetCompletionResult(uri, line, character);

        Assert.Contains(completion.Candidates, candidate =>
            candidate.Label == "entry"
            && candidate.Kind == VbaCompletionCandidateKind.Definition);
        Assert.Contains(completion.Candidates, candidate =>
            candidate.Label == "Each"
            && candidate.Kind == VbaCompletionCandidateKind.LanguageVocabulary);
        Assert.DoesNotContain(completion.Candidates, candidate => candidate.Label == "If");
    }

    [Fact]
    public void TypeOfTransitionOffersOnlyIsAndReplacesItsPartialWord()
    {
        const string uri = "file:///C:/work/Main.bas";
        var markedSource = string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Run()",
            "    If TypeOf value I|",
            "    End If",
            "End Sub"
        ]);
        var markerOffset = markedSource.IndexOf('|');
        var prefix = markedSource[..markerOffset];
        var line = prefix.Count(character => character == '\n');
        var character = markerOffset - prefix.LastIndexOf('\n') - 1;
        var source = markedSource.Remove(markerOffset, 1);
        var index = VbaSourceIndex.Build(new Dictionary<string, string>
        {
            [uri] = source
        });

        var completion = index.GetCompletionResult(uri, line, character);

        var candidate = Assert.Single(completion.Candidates);
        Assert.Equal("Is", candidate.Label);
        Assert.Equal(VbaCompletionCandidateKind.LanguageVocabulary, candidate.Kind);
        Assert.Equal("Is", candidate.TextEdit?.NewText);
        Assert.Equal(line, candidate.TextEdit!.Range.Start.Line);
        Assert.Equal(character - 1, candidate.TextEdit.Range.Start.Character);
        Assert.Equal(line, candidate.TextEdit.Range.End.Line);
        Assert.Equal(character, candidate.TextEdit.Range.End.Character);
    }

    [Fact]
    public void UnsupportedAddressOfOperandDoesNotLeakGeneralExpressionCandidates()
    {
        const string uri = "file:///C:/work/Main.bas";
        var markedSource = string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Run()",
            "    callback = AddressOf Callb|",
            "End Sub",
            "Public Sub Callback()",
            "End Sub"
        ]);
        var markerOffset = markedSource.IndexOf('|');
        var prefix = markedSource[..markerOffset];
        var line = prefix.Count(character => character == '\n');
        var character = markerOffset - prefix.LastIndexOf('\n') - 1;
        var index = VbaSourceIndex.Build(new Dictionary<string, string>
        {
            [uri] = markedSource.Remove(markerOffset, 1)
        });

        var completion = index.GetCompletionResult(uri, line, character);

        Assert.Empty(completion.Candidates);
    }
}
