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

        Assert.Null(
            typeof(VbaSemanticInventory).Assembly.GetType(
                "VbaLanguageServer.SourceModel.VbaSourceIndex"));
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
    public void Rename_plan_records_explicit_conditional_family_correspondence()
    {
        const string uri = "file:///C:/work/ConditionalCorrespondence.bas";
        var sourceDocuments = CreateSourceDocuments(new Dictionary<string, string>
        {
            [uri] =
                """
                Attribute VB_Name = "ConditionalCorrespondence"
                #If FIRST_CONFIGURATION Then
                Public Function BuildValue() As Long
                End Function
                #Else
                Public Function buildvalue(ByVal Key As String) As Long
                End Function
                #End If
                Public Sub Run()
                    Debug.Print BuildValue()
                End Sub
                """
        });
        var inventory = VbaSemanticInventory.Create(sourceDocuments);

        var rename = inventory.CreateRenameResult(
            uri,
            9,
            "    Debug.Print ".Length,
            "CreateValue");
        Assert.Null(rename.Failure);
        var plan = Assert.IsType<VbaRenamePlan>(rename.Plan);
        var correspondence = Assert.IsType<VbaRenameTargetCorrespondence>(
            plan.TargetCorrespondence);

        Assert.Equal("BuildValue", correspondence.BeforeTarget.CanonicalName);
        Assert.Equal("CreateValue", correspondence.AfterTarget.CanonicalName);
        Assert.Equal(
            [2, 5],
            correspondence.PhysicalDefinitions.Select(pair =>
                pair.BeforeDefinition.Range.Start.Line));
        Assert.Equal(
            [2, 5],
            correspondence.PhysicalDefinitions.Select(pair =>
                pair.AfterDefinition.Range.Start.Line));
        Assert.All(
            correspondence.PhysicalDefinitions,
            pair =>
            {
                Assert.NotEqual(
                    pair.BeforeDefinition.Identity,
                    pair.AfterDefinition.Identity);
                Assert.Equal(
                    pair.BeforeDefinition.Kind,
                    pair.AfterDefinition.Kind);
                Assert.Equal(
                    pair.BeforeDefinition.PropertyAccessorKind,
                    pair.AfterDefinition.PropertyAccessorKind);
                Assert.Equal(
                    pair.BeforeDefinition.ConditionalCompilationPath!
                        .Branches.Count,
                    pair.AfterDefinition.ConditionalCompilationPath!
                        .Branches.Count);
            });
        var laterVariant = correspondence.PhysicalDefinitions[1];
        var beforeBranch = Assert.Single(
            laterVariant.BeforeDefinition.ConditionalCompilationPath!.Branches);
        var afterBranch = Assert.Single(
            laterVariant.AfterDefinition.ConditionalCompilationPath!.Branches);
        Assert.Equal(beforeBranch.IfDirectiveOffset, afterBranch.IfDirectiveOffset);
        Assert.Equal(
            beforeBranch.BranchDirectiveOffset + 1,
            afterBranch.BranchDirectiveOffset);
        var callCompatibility = Assert.Single(
            correspondence.CallCompatibilities);
        Assert.Equal(uri, callCompatibility.Uri);
        Assert.Equal(
            [
                VbaCallCompatibilityState.Applicable,
                VbaCallCompatibilityState.Inapplicable
            ],
            callCompatibility.Variants.Select(variant =>
                variant.BeforeState));
        Assert.All(
            callCompatibility.Variants,
            variant => Assert.Equal(
                variant.BeforeState,
                variant.AfterState));
        Assert.Equal(
            [2, 5, 9],
            correspondence.OccurrenceTargets.Select(occurrence =>
                occurrence.BeforeRange.Start.Line));
        Assert.All(
            correspondence.OccurrenceTargets,
            occurrence =>
            {
                Assert.Equal(2, occurrence.PossibleDefinitions.Count);
                Assert.Equal(
                    occurrence.PossibleDefinitions.Select(pair =>
                        pair.BeforeDefinition.Range.Start.Line),
                    occurrence.PossibleDefinitions.Select(pair =>
                        pair.AfterDefinition.Range.Start.Line));
            });
    }

    [Fact]
    public void Inventory_rename_accepts_an_exact_japanese_identifier()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var sourceDocuments = CreateSourceDocuments(new Dictionary<string, string>
        {
            [uri] = "Attribute VB_Name = \"Worker\"\n"
                + "Public Function BuildValue() As Long\n"
                + "    BuildValue = 1\n"
                + "End Function"
        });
        var inventory = VbaSemanticInventory.Create(sourceDocuments);

        var renamePlan = Assert.IsType<VbaRenamePlan>(inventory.CreateRenamePlan(
            uri,
            1,
            "Public Function ".Length,
            "集計結果"));

        Assert.All(
            renamePlan.Changes.SelectMany(change => change.Value),
            edit => Assert.Equal("集計結果", edit.NewText));
    }

    [Fact]
    public void InventoryRenameRejectsInvalidExactUntrimmedNames()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var sourceDocuments = CreateSourceDocuments(new Dictionary<string, string>
        {
            [uri] = "Public Sub Run()\nEnd Sub"
        });
        var inventory = VbaSemanticInventory.Create(sourceDocuments);
        string[] invalidNames =
        [
            "",
            " Run",
            "Run ",
            "Run$",
            "[Run]",
            "CDecl",
            "亜ㄱ",
            new('A', 256)
        ];

        Assert.All(
            invalidNames,
            name => Assert.Null(inventory.CreateRenamePlan(
                uri,
                0,
                "Public Sub ".Length,
                name)));
    }

    [Fact]
    public void InventoryRenameAcceptsAnIdentifierAtThe255CharacterLimit()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var sourceDocuments = CreateSourceDocuments(new Dictionary<string, string>
        {
            [uri] = "Public Sub Run()\nEnd Sub"
        });
        var inventory = VbaSemanticInventory.Create(sourceDocuments);
        var newName = new string('A', 255);

        var renamePlan = Assert.IsType<VbaRenamePlan>(inventory.CreateRenamePlan(
            uri,
            0,
            "Public Sub ".Length,
            newName));

        Assert.All(
            renamePlan.Changes.SelectMany(change => change.Value),
            edit => Assert.Equal(newName, edit.NewText));
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
    public void Inventory_owns_source_definitions_and_nested_signature_parameters()
    {
        const string uri = "file:///C:/work/MutableInput.bas";
        const string source =
            """
            Attribute VB_Name = "MutableInput"
            Public Function OriginalProcedure(value As String) As String
            End Function
            Public Sub Caller()
                OriginalProcedure(
            End Sub

            """;
        var projectedDocument = VbaSourceDocumentProjector.Project(
            uri,
            VbaSyntaxTree.ParseModule(uri, source));
        var projectedProcedure = projectedDocument.Definitions
            .Single(definition => definition.Name == "OriginalProcedure");
        var mutableParameters = projectedProcedure.Signature!.Parameters.ToList();
        var originalProcedure = projectedProcedure
            with
            {
                Signature = projectedProcedure.Signature
                    with
                    {
                        Parameters = mutableParameters
                    }
            };
        var mutableDefinitions = projectedDocument.Definitions
            .Select(definition => definition.Name == "OriginalProcedure"
                ? originalProcedure
                : definition)
            .ToList();
        var mutableDocument = new VbaSourceDocument(
            uri,
            source,
            projectedDocument.ModuleName,
            mutableDefinitions,
            projectedDocument.SyntaxTree);
        var sourceDocuments = new Dictionary<string, VbaSourceDocument>
        {
            [uri] = mutableDocument
        };
        var expectedInventory = VbaSemanticInventory.Create(sourceDocuments);
        var inventory = VbaSemanticInventory.Create(sourceDocuments);
        var expectedSemanticTokenData = expectedInventory
            .GetSemanticTokenData(uri)
            .ToArray();

        mutableDefinitions.Clear();
        mutableDefinitions.Add(originalProcedure with { Name = "InjectedProcedure" });
        mutableParameters.Clear();
        mutableParameters.Add(new VbaCallableParameter("injected"));

        var definitions = inventory.GetDocumentDefinitions(uri);
        Assert.Equal(
            ["MutableInput", "OriginalProcedure", "value", "Caller"],
            definitions.Select(definition => definition.Name));
        var mutableDefinitionView =
            Assert.IsAssignableFrom<IList<VbaSourceDefinition>>(definitions);
        Assert.True(mutableDefinitionView.IsReadOnly);
        Assert.Throws<NotSupportedException>(mutableDefinitionView.Clear);

        Assert.Contains(
            inventory.GetWorkspaceSymbols("Original"),
            symbol => symbol.Name == "OriginalProcedure");
        Assert.DoesNotContain(
            inventory.GetWorkspaceSymbols("Injected"),
            symbol => symbol.Name == "InjectedProcedure");

        var completion = inventory.GetCompletionResult(uri, 4, "    ".Length);
        Assert.Contains(
            completion.Candidates,
            candidate => candidate.Label == "OriginalProcedure");
        Assert.DoesNotContain(
            completion.Candidates,
            candidate => candidate.Label == "InjectedProcedure");

        var signatureHelp = Assert.IsType<VbaSignatureHelp>(
            inventory.GetSignatureHelp(
                uri,
                4,
                "    OriginalProcedure(".Length));
        Assert.Equal(
            ["value"],
            signatureHelp.Signature.Parameters.Select(parameter => parameter.Name));
        var mutableParameterView =
            Assert.IsAssignableFrom<IList<VbaCallableParameter>>(
                signatureHelp.Signature.Parameters);
        Assert.True(mutableParameterView.IsReadOnly);
        Assert.Throws<NotSupportedException>(mutableParameterView.Clear);

        var semanticTokenData = inventory.GetSemanticTokenData(uri);
        Assert.Equal(expectedSemanticTokenData, semanticTokenData);
        var mutableSemanticTokenData = Assert.IsAssignableFrom<IList<int>>(semanticTokenData);
        Assert.True(mutableSemanticTokenData.IsReadOnly);
        Assert.Throws<NotSupportedException>(mutableSemanticTokenData.Clear);
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

    [Fact]
    public void Inventory_preserves_each_conditional_variant_visibility()
    {
        const string uri = "file:///C:/work/ConditionalVisibility.cls";
        var sourceDocuments = CreateSourceDocuments(new Dictionary<string, string>
        {
            [uri] =
                """
                Attribute VB_Name = "ConditionalVisibility"
                #If VBA7 Then
                Friend Function BuildValue() As Long
                    BuildValue = 1
                End Function
                #Else
                Private Function BUILDVALUE() As Long
                    BUILDVALUE = 2
                End Function
                #End If
                """
        });
        var inventory = VbaSemanticInventory.Create(sourceDocuments);

        Assert.Equal(
            [("BuildValue", "Friend"), ("BUILDVALUE", "Private")],
            inventory
                .GetDocumentDefinitions(uri)
                .Where(definition => definition.Name.Equals(
                    "BuildValue",
                    StringComparison.OrdinalIgnoreCase))
                .Select(definition => (definition.Name, definition.Visibility.ToString())));
    }

    [Fact]
    public void Inventory_resolves_a_project_visible_friend_conditional_family()
    {
        const string callerUri = "file:///C:/work/Caller.bas";
        const string workerUri = "file:///C:/work/Worker.cls";
        var sourceDocuments = CreateSourceDocuments(new Dictionary<string, string>
        {
            [callerUri] =
                """
                Attribute VB_Name = "Caller"
                Public Sub Run()
                    Dim worker As Worker
                    Set worker = New Worker
                    worker.BuildValue
                End Sub
                """,
            [workerUri] =
                """
                Attribute VB_Name = "Worker"
                #If VBA7 Then
                Friend Sub BuildValue()
                End Sub
                #Else
                Private Sub buildvalue()
                End Sub
                #End If
                """
        });
        var inventory = VbaSemanticInventory.Create(sourceDocuments);

        Assert.Equal(
            [2, 5],
            inventory
                .ResolveDefinitions(
                    callerUri,
                    4,
                    "    worker.BuildValue".Length)
                .Select(location => location.Range.Start.Line));
    }

    [Fact]
    public void Inventory_binds_an_external_use_to_the_conditional_family_not_the_visible_variant()
    {
        const string callerUri = "file:///C:/work/ConditionalCaller.bas";
        const string workerUri = "file:///C:/work/ConditionalWorker.bas";
        var sourceDocuments = CreateSourceDocuments(new Dictionary<string, string>
        {
            [callerUri] =
                """
                Attribute VB_Name = "ConditionalCaller"
                Public Sub Run()
                    Debug.Print BUILDVALUE()
                End Sub
                """,
            [workerUri] =
                """
                Attribute VB_Name = "ConditionalWorker"
                #If FIRST_CONFIGURATION Then
                Private Function buildValue() As Long
                    buildValue = 1
                End Function
                #Else
                Public Function BUILDVALUE() As Long
                    BUILDVALUE = 2
                End Function
                #End If
                """
        });
        var inventory = VbaSemanticInventory.Create(sourceDocuments);

        var useTarget = Assert.IsType<VbaConditionalFamilyNameTarget>(
            inventory.ResolveSourceTarget(
                callerUri,
                2,
                "    Debug.Print ".Length));
        var privateDeclarationTarget = Assert.IsType<VbaConditionalFamilyNameTarget>(
            inventory.ResolveSourceTarget(
                workerUri,
                2,
                "Private Function ".Length));
        var publicDeclarationTarget = Assert.IsType<VbaConditionalFamilyNameTarget>(
            inventory.ResolveSourceTarget(
                workerUri,
                6,
                "Public Function ".Length));

        Assert.Equal(privateDeclarationTarget.Identity, useTarget.Identity);
        Assert.Equal(publicDeclarationTarget.Identity, useTarget.Identity);
        Assert.Equal("buildValue", useTarget.CanonicalName);
        Assert.Equal("BUILDVALUE", useTarget.SelectedDefinition.Name);
        Assert.Equal(
            ["buildValue", "BUILDVALUE"],
            useTarget.PhysicalDefinitions.Select(definition => definition.Name));

        var occurrenceIndex = GetRequiredFieldValue<VbaResolvedIdentifierOccurrenceIndex>(
            inventory);
        var familyOccurrences = occurrenceIndex
            .GetAll()
            .Where(occurrence => occurrence.Occurrence.Name.Equals(
                "buildValue",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(familyOccurrences);
        Assert.All(
            familyOccurrences,
            occurrence => Assert.Equal(
                useTarget.Identity,
                Assert.IsType<VbaConditionalFamilyNameTarget>(
                    occurrence.Target).Identity));
    }

    [Fact]
    public void Inventory_binds_complementary_conditional_accessors_to_one_property_target()
    {
        const string uri = "file:///C:/work/ConditionalProperty.cls";
        var sourceDocuments = CreateSourceDocuments(new Dictionary<string, string>
        {
            [uri] =
                """
                VERSION 1.0 CLASS
                Attribute VB_Name = "ConditionalProperty"
                #If READ_CONFIGURATION Then
                Public Property Get Amount() As Long
                    Amount = 1
                End Property
                #End If
                #If WRITE_CONFIGURATION Then
                Public Property Let amount(ByVal value As Long)
                End Property
                #End If
                Public Sub Run()
                    Debug.Print Amount
                    Amount = 2
                End Sub
                """
        });
        var inventory = VbaSemanticInventory.Create(sourceDocuments);

        var readAccessor = Assert.IsType<VbaPropertyNameTarget>(
            inventory.ResolveSourceTarget(
                uri,
                3,
                "Public Property Get ".Length));
        var writeAccessor = Assert.IsType<VbaPropertyNameTarget>(
            inventory.ResolveSourceTarget(
                uri,
                8,
                "Public Property Let ".Length));
        var readUse = Assert.IsType<VbaPropertyNameTarget>(
            inventory.ResolveSourceTarget(
                uri,
                12,
                "    Debug.Print ".Length));
        var writeUse = Assert.IsType<VbaPropertyNameTarget>(
            inventory.ResolveSourceTarget(uri, 13, 4));

        Assert.Equal(readAccessor.Identity, writeAccessor.Identity);
        Assert.Equal(readAccessor.Identity, readUse.Identity);
        Assert.Equal(readAccessor.Identity, writeUse.Identity);
        Assert.Equal("Amount", readUse.CanonicalName);
        Assert.Equal(
            [3, 8],
            readUse.PhysicalDefinitions.Select(definition =>
                definition.Range.Start.Line));
    }

    [Fact]
    public void Inventory_binds_ordinary_complementary_accessors_to_one_property_target()
    {
        const string uri = "file:///C:/work/OrdinaryProperty.cls";
        var sourceDocuments = CreateSourceDocuments(new Dictionary<string, string>
        {
            [uri] =
                """
                VERSION 1.0 CLASS
                Attribute VB_Name = "OrdinaryProperty"
                Public Property Get Amount() As Long
                    Amount = 1
                End Property
                Public Property Let amount(ByVal value As Long)
                End Property
                Public Sub Run()
                    Debug.Print Amount
                    Amount = 2
                End Sub
                """
        });
        var inventory = VbaSemanticInventory.Create(sourceDocuments);

        var readAccessor = inventory.ResolveSourceTarget(
            uri,
            2,
            "Public Property Get ".Length);
        var writeAccessor = inventory.ResolveSourceTarget(
            uri,
            5,
            "Public Property Let ".Length);
        var readUse = inventory.ResolveSourceTarget(
            uri,
            8,
            "    Debug.Print ".Length);
        var writeUse = inventory.ResolveSourceTarget(uri, 9, 4);
        Assert.NotNull(readAccessor);
        Assert.NotNull(writeAccessor);
        Assert.NotNull(readUse);
        Assert.NotNull(writeUse);

        Assert.Equal(readAccessor.Identity, writeAccessor.Identity);
        Assert.Equal(readAccessor.Identity, readUse.Identity);
        Assert.Equal(readAccessor.Identity, writeUse.Identity);
        Assert.Equal("Amount", readUse.CanonicalName);
        Assert.Equal(
            [2, 5],
            readUse.PhysicalDefinitions.Select(definition =>
                definition.Range.Start.Line));
    }

    [Fact]
    public void Inventory_selects_a_guarded_setter_family_for_a_mixed_provenance_write()
    {
        const string uri = "file:///C:/work/MixedPropertySetterProvenance.cls";
        var sourceDocuments = CreateSourceDocuments(new Dictionary<string, string>
        {
            [uri] =
                """
                VERSION 1.0 CLASS
                Attribute VB_Name = "MixedPropertySetterProvenance"
                Public Property Get value() As Long
                End Property
                #If FIRST_WRITE_CONFIGURATION Then
                Public Property Let Value(ByVal firstAssigned As Long)
                End Property
                #Else
                Public Property Let VALUE(ByVal secondAssigned As Long)
                End Property
                #End If
                Public Sub Run()
                    Value = 1
                End Sub
                """
        });
        var inventory = VbaSemanticInventory.Create(sourceDocuments);

        var getter = Assert.IsType<VbaPropertyNameTarget>(
            inventory.ResolveSourceTarget(
                uri,
                2,
                "Public Property Get ".Length));
        var setter = Assert.IsType<VbaPropertyNameTarget>(
            inventory.ResolveSourceTarget(
                uri,
                5,
                "Public Property Let ".Length));
        Assert.Equal(2, getter.AccessorTargets.Count);
        Assert.False(getter.AccessorTargets[0].IsConditionalFamily);
        Assert.True(getter.AccessorTargets[1].IsConditionalFamily);
        Assert.All(
            getter.AccessorTargets[1].PhysicalDefinitions,
            definition => Assert.True(
                definition.PropertyAccess.HasFlag(
                    VbaPropertyAccess.Writable)));
        Assert.Equal(
            VbaCompletionExpectation.AssignmentTarget,
            sourceDocuments[uri].SyntaxTree!.GetPositionSyntax(
                    12,
                    "    Value".Length)
                .CompletionExpectation);
        var nameResolution = new VbaNameResolutionService(
            sourceDocuments.Values.ToArray(),
            referenceSelection: null,
            VbaProjectReferenceCatalogSet.Empty);
        var writeOutcome = nameResolution.ResolvePreferredOutcome(
            uri,
            new VbaLanguageServer.Diagnostics.VbaPosition(12, 4),
            qualifier: null,
            "Value",
            definition => definition.Kind
                    == VbaSourceDefinitionKind.Property
                && definition.PropertyAccess.HasFlag(
                    VbaPropertyAccess.Writable));
        Assert.Equal(VbaNameResolutionKind.Resolved, writeOutcome.Kind);
        Assert.IsType<VbaPropertyNameTarget>(writeOutcome.Target);
        var writeUse = Assert.IsType<VbaPropertyNameTarget>(
            inventory.ResolveSourceTarget(uri, 12, 4));

        Assert.Equal(getter.Identity, setter.Identity);
        Assert.Equal(getter.Identity, writeUse.Identity);
        Assert.True(writeUse.IsConditionalFamily);
        Assert.Equal(
            [5, 8],
            writeUse.PhysicalDefinitions.Select(definition =>
                definition.Range.Start.Line));
    }

    [Fact]
    public void Inventory_links_property_accessors_before_mixed_kind_conditional_families()
    {
        const string uri = "file:///C:/work/MixedKindConditionalProperty.cls";
        var sourceDocuments = CreateSourceDocuments(new Dictionary<string, string>
        {
            [uri] =
                """
                VERSION 1.0 CLASS
                Attribute VB_Name = "MixedKindConditionalProperty"
                #If FUNCTION_CONFIGURATION Then
                Public Function Value() As Long
                End Function
                #End If
                #If GET_CONFIGURATION Then
                Public Property Get value() As Long
                End Property
                #End If
                #If LET_CONFIGURATION Then
                Public Property Let VALUE(ByVal assigned As Long)
                End Property
                #End If
                """
        });
        var inventory = VbaSemanticInventory.Create(sourceDocuments);

        var readAccessor = Assert.IsType<VbaPropertyNameTarget>(
            inventory.ResolveSourceTarget(
                uri,
                7,
                "Public Property Get ".Length));
        var writeAccessor = Assert.IsType<VbaPropertyNameTarget>(
            inventory.ResolveSourceTarget(
                uri,
                11,
                "Public Property Let ".Length));

        Assert.Equal(readAccessor.Identity, writeAccessor.Identity);
        Assert.Equal(2, readAccessor.AccessorTargets.Count);
        Assert.All(
            readAccessor.AccessorTargets,
            target =>
            {
                Assert.IsType<VbaConditionalFamilyNameTarget>(target);
                Assert.True(target.PhysicalDefinitions
                    .Where(definition =>
                        definition.Kind == VbaSourceDefinitionKind.Property)
                    .Select(definition => definition.PropertyAccessorKind)
                    .Distinct()
                    .Count() <= 1);
            });
        Assert.Equal(
            [3, 7, 11],
            readAccessor.PhysicalDefinitions.Select(definition =>
                definition.Range.Start.Line));
    }

    private static IReadOnlyDictionary<string, VbaSourceDocument> CreateSourceDocuments(
        IReadOnlyDictionary<string, string> sourceTexts)
        => sourceTexts.ToDictionary(
            pair => pair.Key,
            pair => VbaSourceDocumentProjector.Project(
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
