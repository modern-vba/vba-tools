using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaSemanticInventoryTests
{
    [Fact]
    public void Inventory_matches_compatibility_index_for_definition_oriented_queries()
    {
        const string callerUri = "file:///C:/work/Caller.bas";
        const string libraryUri = "file:///C:/work/Library.bas";
        var sourceTexts = new Dictionary<string, string>
        {
            [callerUri] =
                """
                Attribute VB_Name = "Caller"
                Public Sub Main()
                    Dim value As String
                    value = ReadValue("id")
                End Sub

                """,
            [libraryUri] =
                """
                Attribute VB_Name = "Library"
                Public Function ReadValue(Key As String) As String
                    ReadValue = Key
                End Function

                """
        };
        var sourceDocuments = CreateSourceDocuments(sourceTexts);
        var compatibilityIndex = VbaSourceIndex.BuildFromSourceDocuments(sourceDocuments);
        var inventory = VbaSemanticInventory.Create(compatibilityIndex, sourceDocuments);

        Assert.Equal(
            compatibilityIndex.GetDocumentDefinitions(callerUri),
            inventory.GetDocumentDefinitions(callerUri));
        Assert.Equal(
            compatibilityIndex.GetWorkspaceSymbols("Read"),
            inventory.GetWorkspaceSymbols("Read"));
        Assert.Equal(
            compatibilityIndex.GetCompletionResult(callerUri, 3, "    value = ".Length).Candidates,
            inventory.GetCompletionResult(callerUri, 3, "    value = ".Length).Candidates);
        Assert.Equal(
            compatibilityIndex.ResolveDefinition(callerUri, 3, "    value = ReadValue".Length),
            inventory.ResolveDefinition(callerUri, 3, "    value = ReadValue".Length));
        Assert.Equal(
            compatibilityIndex.ResolveSourceDefinition(callerUri, 3, "    value = ReadValue".Length),
            inventory.ResolveSourceDefinition(callerUri, 3, "    value = ReadValue".Length));
        Assert.Equal(
            compatibilityIndex.GetSignatureHelp(callerUri, 3, "    value = ReadValue(".Length),
            inventory.GetSignatureHelp(callerUri, 3, "    value = ReadValue(".Length));
        Assert.Equal(
            compatibilityIndex.FindReferences(callerUri, 3, "    value = ReadValue".Length),
            inventory.FindReferences(callerUri, 3, "    value = ReadValue".Length));
        AssertSameRenamePlan(
            compatibilityIndex.CreateRenamePlan(callerUri, 3, "    value = ReadValue".Length, "ReadText"),
            inventory.CreateRenamePlan(callerUri, 3, "    value = ReadValue".Length, "ReadText"));
        Assert.Equal(
            compatibilityIndex.GetSemanticTokenData(callerUri),
            inventory.GetSemanticTokenData(callerUri));
    }

    [Fact]
    public void Inventory_builds_query_shaped_definition_maps()
    {
        const string uri = "file:///C:/work/Inventory.bas";
        var sourceDocuments = CreateSourceDocuments(new Dictionary<string, string>
        {
            [uri] =
                """
                Attribute VB_Name = "Inventory"
                Public Type Customer
                    Name As String
                End Type
                Public Enum CustomerKind
                    Retail = 1
                End Enum
                Public Function FindCustomer(Id As String) As Customer
                End Function

                """
        });
        var compatibilityIndex = VbaSourceIndex.BuildFromSourceDocuments(sourceDocuments);
        var inventory = VbaSemanticInventory.Create(compatibilityIndex, sourceDocuments);

        Assert.True(inventory.DefinitionsByNormalizedName.ContainsKey("FINDCUSTOMER"));
        Assert.True(inventory.DefinitionsByModule.ContainsKey("INVENTORY"));
        Assert.True(inventory.DefinitionsByType.ContainsKey("CUSTOMER"));
        Assert.NotNull(inventory.DefinitionsByParentType);
        Assert.NotNull(inventory.DefinitionsByQualifier);
        Assert.Contains(
            inventory.DefinitionsByCallableIdentity.Values.SelectMany(definitions => definitions),
            definition => definition.Name == "FindCustomer");
    }

    [Fact]
    public void Randomized_rename_and_delete_sequences_preserve_workspace_symbol_results()
    {
        const string uri = "file:///C:/work/Randomized.bas";
        for (var seed = 0; seed < 12; seed++)
        {
            var random = new Random(seed);
            var procedureNames = Enumerable.Range(0, 8)
                .Select(index => $"Procedure{index}")
                .Where(_ => random.Next(4) != 0)
                .Select(name => random.Next(3) == 0 ? $"{name}Renamed" : name)
                .ToArray();
            var sourceDocuments = CreateSourceDocuments(new Dictionary<string, string>
            {
                [uri] = CreateModule("Randomized", procedureNames)
            });
            var compatibilityIndex = VbaSourceIndex.BuildFromSourceDocuments(sourceDocuments);
            var inventory = VbaSemanticInventory.Create(compatibilityIndex, sourceDocuments);

            Assert.Equal(
                compatibilityIndex.GetWorkspaceSymbols("Procedure"),
                inventory.GetWorkspaceSymbols("Procedure"));
        }
    }

    private static IReadOnlyDictionary<string, VbaSourceDocument> CreateSourceDocuments(
        IReadOnlyDictionary<string, string> sourceTexts)
        => sourceTexts.ToDictionary(
            pair => pair.Key,
            pair => VbaSourceIndex.CreateDocument(
                pair.Key,
                VbaSyntaxTree.ParseModule(pair.Key, pair.Value)),
            StringComparer.OrdinalIgnoreCase);

    private static string CreateModule(string moduleName, IReadOnlyList<string> procedureNames)
    {
        var lines = new List<string>
        {
            $"Attribute VB_Name = \"{moduleName}\""
        };
        foreach (var procedureName in procedureNames)
        {
            lines.Add($"Public Sub {procedureName}()");
            lines.Add("End Sub");
        }

        return string.Join('\n', lines);
    }

    private static void AssertSameRenamePlan(VbaRenamePlan? expected, VbaRenamePlan? actual)
    {
        Assert.Equal(expected?.TargetRange, actual?.TargetRange);
        Assert.Equal(
            FlattenRenameChanges(expected),
            FlattenRenameChanges(actual));
    }

    private static IReadOnlyList<string> FlattenRenameChanges(VbaRenamePlan? plan)
        => plan?.Changes
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(pair => pair.Value
                .OrderBy(edit => edit.Range.Start.Line)
                .ThenBy(edit => edit.Range.Start.Character)
                .Select(edit => $"{pair.Key}:{edit.Range}:{edit.NewText}"))
            .ToArray()
            ?? [];
}
