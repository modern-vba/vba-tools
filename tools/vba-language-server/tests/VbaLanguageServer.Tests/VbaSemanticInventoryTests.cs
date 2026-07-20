using System.Reflection;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaSemanticInventoryTests
{
    [Fact]
    public void Inventory_serves_definition_oriented_queries_from_project_source()
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
                    value = ReadValue(
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
        var inventory = VbaSemanticInventory.Create(
            sourceDocuments,
            referenceSelection: null,
            referenceCatalogs: VbaProjectReferenceCatalogSet.Empty);

        Assert.DoesNotContain(
            typeof(VbaSemanticInventory).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(VbaSourceIndex));
        Assert.Equal(
            ["Caller", "Main", "value"],
            inventory.GetDocumentDefinitions(callerUri).Select(definition => definition.Name));

        var workspaceSymbol = Assert.Single(inventory.GetWorkspaceSymbols("Read"));
        Assert.Equal("ReadValue", workspaceSymbol.Name);
        Assert.Equal(VbaSourceDefinitionKind.Procedure, workspaceSymbol.Kind);
        Assert.Equal(libraryUri, workspaceSymbol.Uri);

        var completion = inventory.GetCompletionResult(callerUri, 3, "    value = ".Length);
        var completionCandidate = Assert.Single(
            completion.Candidates,
            candidate => candidate.Kind == VbaCompletionCandidateKind.Definition
                && candidate.Label == "ReadValue");
        Assert.Equal(libraryUri, completionCandidate.Definition?.Uri);
        Assert.Equal(VbaResolutionPolicy.ProjectRank, completionCandidate.SortRank);

        var resolvedDefinition = Assert.IsType<VbaSourceDefinition>(
            inventory.ResolveSourceDefinition(
                callerUri,
                3,
                "    value = ReadValue".Length));
        Assert.Equal("ReadValue", resolvedDefinition.Name);
        Assert.Equal(libraryUri, resolvedDefinition.Uri);
        Assert.Equal(
            new VbaDefinitionLocation(libraryUri, resolvedDefinition.Range),
            inventory.ResolveDefinition(callerUri, 3, "    value = ReadValue".Length));

        var signatureHelp = Assert.IsType<VbaSignatureHelp>(
            inventory.GetSignatureHelp(callerUri, 3, "    value = ReadValue(".Length));
        Assert.Equal("Function ReadValue(ByRef Key As String) As String", signatureHelp.Signature.Label);
        Assert.Equal(0, signatureHelp.ActiveParameter);

        var references = inventory.FindReferences(
            callerUri,
            3,
            "    value = ReadValue".Length);
        Assert.Equal(3, references.Count);
        Assert.Equal(
            [(callerUri, 3), (libraryUri, 1), (libraryUri, 2)],
            references
                .Select(reference => (reference.Uri, reference.Range.Start.Line))
                .OrderBy(reference => reference.Uri, StringComparer.OrdinalIgnoreCase));

        var renamePlan = Assert.IsType<VbaRenamePlan>(
            inventory.CreateRenamePlan(
                callerUri,
                3,
                "    value = ReadValue".Length,
                "ReadText"));
        Assert.Equal(resolvedDefinition.Range, renamePlan.TargetRange);
        Assert.Equal([callerUri, libraryUri], renamePlan.Changes.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(
            renamePlan.Changes.SelectMany(pair => pair.Value),
            edit => Assert.Equal("ReadText", edit.NewText));
    }

    [Fact]
    public void Inventory_does_not_expose_legacy_definition_maps()
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
        var inventory = VbaSemanticInventory.Create(sourceDocuments);

        Assert.Equal(
            ["Customer", "CustomerKind", "FindCustomer", "Inventory", "Name", "Retail"],
            inventory.GetWorkspaceSymbols("")
                .Select(symbol => symbol.Name)
                .Order(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(VbaSemanticInventory).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => property.Name is
                "DefinitionsByNormalizedName"
                or "DefinitionsByModule"
                or "DefinitionsByType"
                or "DefinitionsByParentType"
                or "DefinitionsByQualifier"
                or "DefinitionsByCallableIdentity");
    }

    [Fact]
    public void Inventory_shares_one_definition_candidate_inventory_with_semantic_resolution()
    {
        const string uri = "file:///C:/work/Inventory.bas";
        var sourceDocuments = CreateSourceDocuments(new Dictionary<string, string>
        {
            [uri] =
                """
                Attribute VB_Name = "Inventory"
                Public Function FindCustomer(Id As String) As String
                End Function

                """
        });
        var inventory = VbaSemanticInventory.Create(sourceDocuments);

        var definitionCandidates = GetRequiredFieldValue<VbaNameCandidateInventory>(inventory);
        var semanticResolution = GetRequiredFieldValue<VbaSemanticResolution>(inventory);
        var nameResolution = GetRequiredFieldValue<VbaNameResolutionService>(semanticResolution);

        Assert.Same(
            definitionCandidates,
            GetRequiredFieldValue<VbaNameCandidateInventory>(semanticResolution));
        Assert.Same(
            definitionCandidates,
            GetRequiredFieldValue<VbaNameCandidateInventory>(nameResolution));
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
            var inventory = VbaSemanticInventory.Create(sourceDocuments);

            Assert.Equal(
                procedureNames.Order(StringComparer.OrdinalIgnoreCase),
                inventory.GetWorkspaceSymbols("Procedure").Select(symbol => symbol.Name));
            Assert.All(
                inventory.GetWorkspaceSymbols("Procedure"),
                symbol =>
                {
                    Assert.Equal(VbaSourceDefinitionKind.Procedure, symbol.Kind);
                    Assert.Equal(uri, symbol.Uri);
                });
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

    private static T GetRequiredFieldValue<T>(object owner)
        where T : class
    {
        var field = Assert.Single(
            owner.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            candidate => candidate.FieldType == typeof(T));
        return Assert.IsType<T>(field.GetValue(owner));
    }

}
