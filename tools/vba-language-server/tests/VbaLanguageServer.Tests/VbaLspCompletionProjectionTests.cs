using System.Text.Json;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Lsp;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaLspCompletionProjectionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ProjectionKeepsUnrankedCandidatesInTheNeutralReferenceSortGroup()
    {
        var items = VbaLspFeatureProjection.CreateCompletionItems(
            new VbaCompletionResult([
                new VbaCompletionCandidate(
                    "AlphaArg",
                    VbaCompletionCandidateKind.NamedArgument,
                    InsertText: "AlphaArg:="),
                new VbaCompletionCandidate(
                    "ZuluCatalog",
                    VbaCompletionCandidateKind.Definition)
                {
                    SortRank = 3
                }
            ]));
        var projected = items
            .Select(item => JsonSerializer.SerializeToElement(item, JsonOptions))
            .ToDictionary(item => item.GetProperty("label").GetString()!);

        Assert.True(StringComparer.Ordinal.Compare(
            projected["AlphaArg"].GetProperty("sortText").GetString(),
            projected["ZuluCatalog"].GetProperty("sortText").GetString()) < 0);
    }

    [Fact]
    public void ProjectionAddsDefinitionAndQualifierDetails()
    {
        var range = new VbaRange(
            new VbaPosition(0, 0),
            new VbaPosition(0, 0));
        var definition = new VbaSourceDefinition(
            VbaDefinitionIdentity.ForProjectReference(
                "Visual Basic For Applications",
                "Constants",
                VbaSourceDefinitionKind.Constant,
                "vbCrLf"),
            new VbaDefinitionLocation("vba-reference://vba/constants/vbcrlf", range),
            "vbCrLf",
            VbaSourceDefinitionKind.Constant,
            VbaSourceDefinitionVisibility.Public,
            "Visual Basic For Applications",
            ParentTypeName: "Constants",
            DeclarationLabel: "Const vbCrLf As String");
        var items = VbaLspFeatureProjection.CreateCompletionItems(
            new VbaCompletionResult([
                new VbaCompletionCandidate(
                    "vbCrLf",
                    VbaCompletionCandidateKind.Definition,
                    Definition: definition)
                {
                    SortRank = 3
                },
                new VbaCompletionCandidate(
                    "VBA",
                    VbaCompletionCandidateKind.ReferenceQualifier,
                    InsertText: "VBA.",
                    FilterText: "VBA")
                {
                    SortRank = 3
                }
            ]));
        var projected = items
            .Select(item => JsonSerializer.SerializeToElement(item, JsonOptions))
            .ToDictionary(item => item.GetProperty("label").GetString()!);

        Assert.Equal(
            "Const vbCrLf As String",
            projected["vbCrLf"].GetProperty("detail").GetString());
        Assert.Equal(
            "Reference qualifier",
            projected["VBA"].GetProperty("detail").GetString());
    }

    [Fact]
    public void ProjectionUsesOnlyCompletedSemanticCandidates()
    {
        var items = VbaLspFeatureProjection.CreateCompletionItems(
            new VbaCompletionResult([
                new VbaCompletionCandidate(
                    "End If",
                    VbaCompletionCandidateKind.ContextualStatement)
            ]));
        var item = JsonSerializer.SerializeToElement(Assert.Single(items), JsonOptions);

        Assert.Equal("End If", item.GetProperty("label").GetString());
        Assert.Equal(14, item.GetProperty("kind").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("sortText").GetString()));
        Assert.Equal(3, item.EnumerateObject().Count());
    }

    [Fact]
    public void ProjectionPreservesNamedArgumentInsertionAndReplacementEdits()
    {
        var range = new VbaRange(
            new VbaPosition(3, 12),
            new VbaPosition(3, 15));
        var items = VbaLspFeatureProjection.CreateCompletionItems(
            new VbaCompletionResult([
                new VbaCompletionCandidate(
                    "Arg2",
                    VbaCompletionCandidateKind.NamedArgument,
                    InsertText: "Arg2:=",
                    FilterText: "Arg2"),
                new VbaCompletionCandidate(
                    "End If",
                    VbaCompletionCandidateKind.ContextualStatement,
                    TextEdit: new VbaTextEdit(range, "End If"))
            ]));
        var projected = items
            .Select(item => JsonSerializer.SerializeToElement(item, JsonOptions))
            .ToDictionary(item => item.GetProperty("label").GetString()!);

        Assert.Equal("Arg2", projected["Arg2"].GetProperty("filterText").GetString());
        Assert.Equal("Arg2:=", projected["Arg2"].GetProperty("insertText").GetString());
        Assert.False(projected["Arg2"].TryGetProperty("textEdit", out _));

        var textEdit = projected["End If"].GetProperty("textEdit");
        Assert.Equal("End If", textEdit.GetProperty("newText").GetString());
        Assert.Equal(12, textEdit
            .GetProperty("range")
            .GetProperty("start")
            .GetProperty("character")
            .GetInt32());
        Assert.False(projected["End If"].TryGetProperty("insertText", out _));
    }

    [Fact]
    public void ProjectionPreservesQualifierInsertionTextAndKind()
    {
        var items = VbaLspFeatureProjection.CreateCompletionItems(
            new VbaCompletionResult([
                new VbaCompletionCandidate(
                    "Excel",
                    VbaCompletionCandidateKind.ReferenceQualifier,
                    InsertText: "Excel.",
                    FilterText: "Excel")
            ]));
        var item = JsonSerializer.SerializeToElement(Assert.Single(items), JsonOptions);

        Assert.Equal("Excel", item.GetProperty("label").GetString());
        Assert.Equal("Excel.", item.GetProperty("insertText").GetString());
        Assert.Equal("Excel", item.GetProperty("filterText").GetString());
        Assert.Equal(9, item.GetProperty("kind").GetInt32());
    }
}
