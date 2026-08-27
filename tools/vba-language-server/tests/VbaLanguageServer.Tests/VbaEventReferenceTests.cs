using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaEventReferenceTests
{
    [Fact]
    public void OmittedEventVisibilityIsPublicAndCallableInAClassModule()
    {
        const string uri = "file:///C:/work/Worker.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Event Saved(ByVal Value As Long)",
            "Public Sub Run()",
            "    RaiseEvent Saved(1)",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var definition = index.ResolveSourceDefinition(
            uri,
            4,
            "    RaiseEvent ".Length);

        Assert.Equal(VbaSourceDefinitionVisibility.Public, definition?.Visibility);
        Assert.Equal("Event Saved(Value As Long)", definition?.Signature?.Label);
        Assert.Null(definition?.TypeReference);
        Assert.DoesNotContain(
            VbaSyntaxDiagnostics.Collect(text, uri),
            diagnostic => diagnostic.Code.StartsWith("syntax.event", StringComparison.Ordinal));
    }

    [Fact]
    public void EventIsCallableFromAFormModuleCodeSection()
    {
        const string uri = "file:///C:/work/Dialog.frm";
        var text = string.Join('\n', [
            "VERSION 5.00",
            "Begin VB.Form Dialog",
            "End",
            "Attribute VB_Name = \"Dialog\"",
            "Event Saved()",
            "Public Sub Run()",
            "    RaiseEvent Saved",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var definition = index.ResolveSourceDefinition(
            uri,
            6,
            "    RaiseEvent ".Length);

        Assert.Equal(VbaSourceDefinitionKind.Event, definition?.Kind);
        Assert.Equal(VbaSourceDefinitionVisibility.Public, definition?.Visibility);
        Assert.DoesNotContain(
            VbaSyntaxDiagnostics.Collect(text, uri),
            diagnostic => diagnostic.Code.StartsWith("syntax.event", StringComparison.Ordinal));
    }

    [Fact]
    public void RaiseEventResolvesCurrentModuleEventDefinition()
    {
        const string uri = "file:///C:/work/Worker.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Public Event Saved()",
            "Public Sub Run()",
            "    RaiseEvent Saved",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var definition = index.ResolveSourceDefinition(uri, 4, "    RaiseEvent ".Length);

        Assert.Equal("Saved", definition?.Name);
        Assert.Equal(VbaSourceDefinitionKind.Event, definition?.Kind);
        Assert.Equal(uri, definition?.Uri);
    }

    [Fact]
    public void RaiseEventDefinitionIgnoresASameNamedLocalVariable()
    {
        const string uri = "file:///C:/work/Worker.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Public Event Saved()",
            "Public Sub Run()",
            "    Dim Saved As Variant",
            "    RaiseEvent Saved",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var definition = index.ResolveSourceDefinition(
            uri,
            5,
            "    RaiseEvent ".Length);

        Assert.Equal("Saved", definition?.Name);
        Assert.Equal(VbaSourceDefinitionKind.Event, definition?.Kind);
        Assert.Equal(2, definition?.Range.Start.Line);
    }

    [Fact]
    public void RaiseEventReferencesIgnoreASameNamedLocalVariable()
    {
        const string uri = "file:///C:/work/Worker.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Public Event Saved()",
            "Public Sub Run()",
            "    Dim Saved As Variant",
            "    RaiseEvent Saved",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var references = index.FindReferences(
            uri,
            5,
            "    RaiseEvent ".Length);

        Assert.Equal(2, references.Count);
        Assert.Contains(references, reference => reference.Range.Start.Line == 2);
        Assert.Contains(references, reference => reference.Range.Start.Line == 5);
        Assert.DoesNotContain(references, reference => reference.Range.Start.Line == 4);
    }

    [Fact]
    public void RaiseEventDefinitionRetainsAnEventRecoveredFromAProcedureBody()
    {
        const string uri = "file:///C:/work/Worker.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Event Saved()",
            "    RaiseEvent Saved",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var definition = index.ResolveSourceDefinition(
            uri,
            4,
            "    RaiseEvent ".Length);

        Assert.Equal("Saved", definition?.Name);
        Assert.Equal(VbaSourceDefinitionKind.Event, definition?.Kind);
        Assert.Equal(3, definition?.Range.Start.Line);
        Assert.True(definition?.IsRecoveredEventDeclaration);
        Assert.Equal(
            VbaEventRecoveryReason.InvalidPlacement,
            definition?.EventRecoveryReasons);
        Assert.Null(definition?.Signature);
    }

    [Fact]
    public void RepairRenameOfARecoveredEventIncludesItsDeclarationAndRaiseEvent()
    {
        const string workerUri = "file:///C:/work/Worker.cls";
        const string publisherUri = "file:///C:/work/Publisher.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Public Event publisher_Bad()",
            "Public Sub Run()",
            "    RaiseEvent publisher_Bad",
            "End Sub"
        ]);
        var publisherText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Publisher\"",
            "Public Event Bad()"
        ]);
        var index = BuildIndex(new Dictionary<string, string>
        {
            [workerUri] = workerText,
            [publisherUri] = publisherText
        });

        var rename = Assert.IsType<VbaRenamePlan>(index.CreateRenamePlan(
            workerUri,
            3,
            "Public Event ".Length,
            "Good"));

        var workerEdits = Assert.Single(rename.Changes);
        Assert.Equal(workerUri, workerEdits.Key);
        Assert.Equal(
            [3, 5],
            workerEdits.Value
                .Select(edit => edit.Range.Start.Line)
                .Order()
                .ToArray());
    }

    [Fact]
    public void InvalidlyVisibleEventRemainsARecoveredNavigationTarget()
    {
        const string uri = "file:///C:/work/Worker.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private Event Hidden()",
            "Public Sub Run()",
            "    RaiseEvent Hidden",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var definition = index.ResolveSourceDefinition(
            uri,
            4,
            "    RaiseEvent ".Length);
        var references = index.FindReferences(
            uri,
            4,
            "    RaiseEvent ".Length);

        Assert.Equal(VbaEventRecoveryReason.InvalidVisibility, definition?.EventRecoveryReasons);
        Assert.Null(definition?.Signature);
        Assert.Equal([2, 4], references.Select(reference => reference.Range.Start.Line).ToArray());
    }

    [Fact]
    public void OptionalParameterEventRetainsItsRecoveredNavigationFamily()
    {
        const string uri = "file:///C:/work/Worker.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Public Event Saved(Optional ByVal Value As Long)",
            "Public Sub Run()",
            "    RaiseEvent Saved(1)",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var definition = index.ResolveSourceDefinition(
            uri,
            4,
            "    RaiseEvent ".Length);
        var references = index.FindReferences(
            uri,
            4,
            "    RaiseEvent ".Length);

        Assert.Equal(VbaEventRecoveryReason.OptionalParameter, definition?.EventRecoveryReasons);
        Assert.Null(definition?.Signature);
        Assert.Equal([2, 4], references.Select(reference => reference.Range.Start.Line).ToArray());
    }

    [Theory]
    [InlineData(
        "Optional ByVal Value As Long",
        "syntax.eventOptionalParameterNotAllowed",
        VbaEventRecoveryReason.OptionalParameter)]
    [InlineData(
        "ParamArray Values() As Variant",
        "syntax.eventParamArrayParameterNotAllowed",
        VbaEventRecoveryReason.ParamArrayParameter)]
    public void MalformedEventParameterModifierRetainsIndependentRecoveryReasons(
        string parameter,
        string diagnosticCode,
        VbaEventRecoveryReason modifierReason)
    {
        const string uri = "file:///C:/work/Worker.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            $"Public Event Saved({parameter}",
            "Public Sub Run()",
            "    RaiseEvent Saved(1)",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var definition = index.ResolveSourceDefinition(
            uri,
            2,
            "Public Event ".Length);

        Assert.Contains(
            VbaSyntaxDiagnostics.Collect(text, uri),
            diagnostic => diagnostic.Code == diagnosticCode);
        Assert.Equal(
            VbaEventRecoveryReason.MissingOrInvalidSignature
                | modifierReason,
            definition?.EventRecoveryReasons);
        Assert.Null(definition?.Signature);
    }

    [Fact]
    public void ParamArrayEventRetainsItsRecoveredNavigationFamily()
    {
        const string uri = "file:///C:/work/Worker.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Public Event Saved(ParamArray Values() As Variant)",
            "Public Sub Run()",
            "    RaiseEvent Saved(1)",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var definition = index.ResolveSourceDefinition(
            uri,
            4,
            "    RaiseEvent ".Length);
        var references = index.FindReferences(
            uri,
            4,
            "    RaiseEvent ".Length);

        Assert.Equal(VbaEventRecoveryReason.ParamArrayParameter, definition?.EventRecoveryReasons);
        Assert.Null(definition?.Signature);
        Assert.Equal([2, 4], references.Select(reference => reference.Range.Start.Line).ToArray());
    }

    [Fact]
    public void MalformedEventRetainsARecoveredDefinitionWithoutASignature()
    {
        const string uri = "file:///C:/work/Worker.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Public Event Saved(",
            "Public Sub Run()",
            "    RaiseEvent Saved(1)",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var definition = index.ResolveSourceDefinition(
            uri,
            4,
            "    RaiseEvent ".Length);

        Assert.Equal(VbaSourceDefinitionKind.Event, definition?.Kind);
        Assert.Equal(
            VbaEventRecoveryReason.MissingOrInvalidSignature,
            definition?.EventRecoveryReasons);
        Assert.Null(definition?.Signature);
        Assert.Null(index.GetSignatureHelp(
            uri,
            4,
            "    RaiseEvent Saved(".Length));
    }

    [Fact]
    public void RaiseEventDoesNotFallBackToASameNamedNonEvent()
    {
        const string uri = "file:///C:/work/Worker.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private Saved As Variant",
            "Public Sub Run()",
            "    RaiseEvent Saved",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        Assert.Null(index.ResolveSourceDefinition(
            uri,
            4,
            "    RaiseEvent ".Length));
        Assert.Empty(index.FindReferences(
            uri,
            4,
            "    RaiseEvent ".Length));
    }

    [Fact]
    public void RaiseEventDoesNotFallBackToAnotherClassEvent()
    {
        const string workerUri = "file:///C:/work/Worker.cls";
        const string otherUri = "file:///C:/work/Other.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    RaiseEvent Saved",
            "End Sub"
        ]);
        var otherText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Other\"",
            "Public Event Saved()"
        ]);
        var index = BuildIndex(new Dictionary<string, string>
        {
            [workerUri] = workerText,
            [otherUri] = otherText
        });

        Assert.Null(index.ResolveSourceDefinition(
            workerUri,
            3,
            "    RaiseEvent ".Length));
        Assert.Empty(index.FindReferences(
            workerUri,
            3,
            "    RaiseEvent ".Length));
    }

    [Fact]
    public void RaiseEventDoesNotFallBackToATypeLibraryEvent()
    {
        const string uri = "file:///C:/work/Worker.cls";
        const string referenceName = "Generated Event Library";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    RaiseEvent Saved",
            "End Sub"
        ]);
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference(referenceName)]);
        var catalogs = VbaProjectReferenceCatalogSet.Empty.WithCatalog(
            new VbaProjectReferenceCatalog(
                referenceName,
                ["GeneratedEvents"],
                [
                    new VbaProjectReferenceDefinition(
                        referenceName,
                        "Saved",
                        VbaSourceDefinitionKind.Event,
                        Signature: new VbaCallableSignature(
                            "Event Saved()",
                            [],
                            CallableKind: VbaCallableKind.Event),
                        GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal)
                ]));
        var index = VbaSemanticInventoryFixture.Create(
            new Dictionary<string, string> { [uri] = text },
            selection,
            catalogs);

        Assert.Null(index.ResolveSourceDefinition(
            uri,
            3,
            "    RaiseEvent ".Length));
        Assert.Empty(index.FindReferences(
            uri,
            3,
            "    RaiseEvent ".Length));
    }

    [Fact]
    public void RaiseEventDoesNotFallBackToAnIntrinsicHostEvent()
    {
        const string uri = "file:///C:/work/Worker.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    RaiseEvent Saved",
            "End Sub"
        ]);
        var hostProjection = new VbaHostClassProjectionSnapshot(
            Revision: 1,
            new VbaHostClassProjectionContext("Book1", "Worker", "Worker.xlsm"),
            ClassEnumerationComplete: true,
            [
                new VbaCurrentHostClassProjectionEntry(
                    new VbaHostClassIdentity("Worker", VbaHostClassKind.Document),
                    new VbaHostClassProjection(
                        "Worksheet",
                        [
                            new VbaHostEventSignature(
                                "Saved",
                                [],
                                Documentation: null,
                                AuthoringAvailable: true,
                                ExistingHandlerRecognizable: true)
                        ]))
            ]);
        var index = VbaSemanticInventory.Create(
            VbaSemanticInventoryFixture.ProjectSourceDocuments(
                new Dictionary<string, string> { [uri] = text }),
            referenceSelection: null,
            VbaProjectReferenceCatalogSet.Empty,
            hostProjection);

        Assert.Null(index.ResolveSourceDefinition(
            uri,
            3,
            "    RaiseEvent ".Length));
        Assert.Empty(index.FindReferences(
            uri,
            3,
            "    RaiseEvent ".Length));
    }

    [Fact]
    public void RaiseEventNavigationIncludesAConditionalRecoveredEventFamily()
    {
        const string uri = "file:///C:/work/Worker.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "#If VBA7 Then",
            "Public Event Saved(ByVal Value As Long)",
            "#Else",
            "Private Event Saved(ByVal Value As Long)",
            "#End If",
            "Public Sub Run()",
            "    RaiseEvent Saved(1)",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var definition = index.ResolveSourceDefinition(
            uri,
            8,
            "    RaiseEvent ".Length);
        var references = index.FindReferences(
            uri,
            8,
            "    RaiseEvent ".Length);
        var definitions = index.ResolveDefinitions(
            uri,
            8,
            "    RaiseEvent ".Length);

        Assert.Equal(VbaSourceDefinitionKind.Event, definition?.Kind);
        Assert.Equal(
            [3, 5],
            definitions.Select(location => location.Range.Start.Line).ToArray());
        Assert.Equal(
            [3, 5, 8],
            references.Select(reference => reference.Range.Start.Line).ToArray());
    }

    [Theory]
    [InlineData("file:///C:/work/StandardEvents.bas", true)]
    [InlineData("file:///C:/work/Worker.cls", false)]
    public void PlacementInvalidRaiseEventDoesNotCreateAnEventReference(
        string uri,
        bool placeInProcedure)
    {
        var text = placeInProcedure
            ? string.Join('\n', [
                "Attribute VB_Name = \"StandardEvents\"",
                "Public Event Saved()",
                "Public Sub Run()",
                "    RaiseEvent Saved",
                "End Sub"
            ])
            : string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Public Event Saved()",
                "RaiseEvent Saved"
            ]);
        const int line = 3;
        var character = placeInProcedure
            ? "    RaiseEvent ".Length
            : "RaiseEvent ".Length;
        var index = BuildIndex(uri, text);

        Assert.Null(index.ResolveSourceDefinition(uri, line, character));
        Assert.Empty(index.FindReferences(uri, line, character));
    }

    [Theory]
    [InlineData("Type")]
    [InlineData("Enum")]
    public void EventInsideATypeDeclarationIsDiagnosedAndRetainedForRaiseEvent(
        string blockKind)
    {
        const string uri = "file:///C:/work/Worker.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            $"Private {blockKind} Container",
            "    Event Saved()",
            $"End {blockKind}",
            "Public Sub Run()",
            "    RaiseEvent Saved",
            "End Sub"
        ]);

        var diagnostic = Assert.Single(
            VbaSyntaxDiagnostics.Collect(text, uri),
            candidate => candidate.Code
                == "syntax.eventDeclarationNotAllowedInModule");
        Assert.Equal(
            new VbaRange(
                new VbaPosition(3, 4),
                new VbaPosition(3, 9)),
            diagnostic.Range);

        var definition = BuildIndex(uri, text).ResolveSourceDefinition(
            uri,
            6,
            "    RaiseEvent ".Length);
        Assert.Equal(VbaSourceDefinitionKind.Event, definition?.Kind);
        Assert.Equal(3, definition?.Range.Start.Line);
        Assert.Equal(
            VbaEventRecoveryReason.InvalidPlacement,
            definition?.EventRecoveryReasons);
    }

    [Fact]
    public void WithEventsHandlersResolveSourceAndReferenceEvents()
    {
        const string workerUri = "file:///C:/work/Worker.cls";
        const string publisherUri = "file:///C:/work/Publisher.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private WithEvents app As Excel.Application",
            "Private Sub publisher_Changed()",
            "End Sub",
            "Private Sub app_WorkbookOpen(ByVal Wb As Excel.Workbook)",
            "End Sub"
        ]);
        var publisherText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Publisher\"",
            "Public Event Changed()"
        ]);
        var index = BuildIndex(new Dictionary<string, string>
        {
            [workerUri] = workerText,
            [publisherUri] = publisherText
        });

        var sourceEvent = index.ResolveSourceDefinition(
            workerUri,
            4,
            "Private Sub publisher_".Length);
        var referenceEvent = index.ResolveSourceDefinition(
            workerUri,
            6,
            "Private Sub app_".Length);

        Assert.Equal("Changed", sourceEvent?.Name);
        Assert.Equal(VbaSourceDefinitionKind.Event, sourceEvent?.Kind);
        Assert.Equal(publisherUri, sourceEvent?.Uri);
        Assert.Equal("WorkbookOpen", referenceEvent?.Name);
        Assert.Equal(VbaSourceDefinitionKind.Event, referenceEvent?.Kind);
        Assert.Equal("Application", referenceEvent?.ParentTypeName);
        Assert.Equal("Microsoft Excel 16.0 Object Library", referenceEvent?.ModuleName);
    }

    [Fact]
    public void Bundled_partial_TypeLib_surface_keeps_unknown_handler_indeterminate()
    {
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents app As Excel.Application",
            "Private Sub app_SheetChange()",
            "End Sub"
        ]);
        var index = VbaSemanticInventoryFixture.Create(
            new Dictionary<string, string> { [workerUri] = workerText },
            VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [new VbaProjectReference("Microsoft Excel 16.0 Object Library")]),
            VbaProjectReferenceCatalogSet.CreateBundled());

        var prefixDefinition = index.ResolveSourceDefinition(
            workerUri,
            3,
            "Private Sub ".Length);

        Assert.Equal("app", prefixDefinition?.Name);
        Assert.Equal(VbaSourceDefinitionKind.Variable, prefixDefinition?.Kind);
        Assert.Equal(2, prefixDefinition?.Range.Start.Line);
    }

    [Fact]
    public void WithEventsHandlerIdentifierProjectsVariablePrefixAndEventSuffix()
    {
        const string workerUri = "file:///C:/work/Worker.cls";
        const string publisherUri = "file:///C:/work/Publisher.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Changed()",
            "End Sub"
        ]);
        var publisherText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Publisher\"",
            "Public Event Changed()"
        ]);
        var index = BuildIndex(new Dictionary<string, string>
        {
            [workerUri] = workerText,
            [publisherUri] = publisherText
        });

        var variable = index.ResolveSourceDefinition(
            workerUri,
            3,
            "Private Sub ".Length);
        var handler = index.ResolveSourceDefinition(
            workerUri,
            3,
            "Private Sub publisher".Length);
        var eventDefinition = index.ResolveSourceDefinition(
            workerUri,
            3,
            "Private Sub publisher_".Length);

        Assert.Equal(VbaSourceDefinitionKind.Variable, variable?.Kind);
        Assert.Equal("publisher", variable?.Name);
        Assert.Equal(2, variable?.Range.Start.Line);
        Assert.Equal(VbaSourceDefinitionKind.Procedure, handler?.Kind);
        Assert.Equal("publisher_Changed", handler?.Name);
        Assert.Equal(VbaSourceDefinitionKind.Event, eventDefinition?.Kind);
        Assert.Equal("Changed", eventDefinition?.Name);
        Assert.Equal(publisherUri, eventDefinition?.Uri);
    }

    [Fact]
    public void WithEventsHandlerReferencesRetainDistinctPrefixAndSuffixRanges()
    {
        const string workerUri = "file:///C:/work/Worker.cls";
        const string publisherUri = "file:///C:/work/Publisher.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Changed()",
            "End Sub"
        ]);
        var publisherText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Publisher\"",
            "Public Event Changed()"
        ]);
        var index = BuildIndex(new Dictionary<string, string>
        {
            [workerUri] = workerText,
            [publisherUri] = publisherText
        });

        var variableReferences = index.FindReferences(
            workerUri,
            2,
            "Private WithEvents ".Length);
        var eventReferences = index.FindReferences(
            publisherUri,
            2,
            "Public Event ".Length);

        Assert.Contains(variableReferences, reference =>
            reference.Uri == workerUri
            && reference.Range.Start.Line == 3
            && reference.Range.Start.Character == "Private Sub ".Length
            && reference.Range.End.Character == "Private Sub publisher".Length);
        Assert.Contains(eventReferences, reference =>
            reference.Uri == workerUri
            && reference.Range.Start.Line == 3
            && reference.Range.Start.Character == "Private Sub publisher_".Length
            && reference.Range.End.Character == "Private Sub publisher_Changed".Length);
        Assert.DoesNotContain(variableReferences, reference =>
            reference.Uri == workerUri
            && reference.Range.Start.Line == 3
            && reference.Range.End.Character == "Private Sub publisher_Changed".Length);
    }

    [Fact]
    public void WithEventsHandlerDefinitionUnionsEventsFromEveryConditionalVariableVariant()
    {
        const string workerUri = "file:///C:/work/Worker.cls";
        const string firstPublisherUri = "file:///C:/work/FirstPublisher.cls";
        const string secondPublisherUri = "file:///C:/work/SecondPublisher.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "#If UseFirst Then",
            "Private WithEvents publisher As FirstPublisher",
            "#Else",
            "Private WithEvents publisher As SecondPublisher",
            "#End If",
            "Private Sub publisher_Changed()",
            "End Sub"
        ]);
        var firstPublisherText = string.Join('\n', [
            "Attribute VB_Name = \"FirstPublisher\"",
            "Public Event Changed()"
        ]);
        var secondPublisherText = string.Join('\n', [
            "Attribute VB_Name = \"SecondPublisher\"",
            "Public Event Changed()"
        ]);
        var index = BuildIndex(new Dictionary<string, string>
        {
            [workerUri] = workerText,
            [firstPublisherUri] = firstPublisherText,
            [secondPublisherUri] = secondPublisherText
        });

        var definitions = index.ResolveDefinitions(
            workerUri,
            7,
            "Private Sub publisher_".Length);

        Assert.Equal(
            [firstPublisherUri, secondPublisherUri],
            definitions.Select(definition => definition.Uri).ToArray());
    }

    [Fact]
    public void WithEventsHandlerNavigationFiltersOnlyTheExternalPartOfAMixedUnion()
    {
        const string workerUri = "file:///C:/work/Worker.cls";
        const string publisherUri = "file:///C:/work/Publisher.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "#If UseSource Then",
            "Private WithEvents target As Publisher",
            "#Else",
            "Private WithEvents target As Excel.Application",
            "#End If",
            "Private Sub target_WorkbookOpen(ByVal Wb As Excel.Workbook)",
            "End Sub"
        ]);
        var publisherText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Publisher\"",
            "Public Event WorkbookOpen(ByVal Wb As Excel.Workbook)"
        ]);
        var index = BuildIndexWithCompleteExcelTypeLib(new Dictionary<string, string>
        {
            [workerUri] = workerText,
            [publisherUri] = publisherText
        });

        var definitions = index.ResolveDefinitions(
            workerUri,
            7,
            "Private Sub target_".Length);
        var references = index.FindReferences(
            workerUri,
            7,
            "Private Sub target_".Length);

        Assert.Equal(
            [publisherUri],
            definitions.Select(definition => definition.Uri).ToArray());
        Assert.Equal(
            [publisherUri, workerUri],
            references.Select(reference => reference.Uri).ToArray());
        Assert.DoesNotContain(
            VbaProjectReferenceCatalogSet.ExternalDefinitionUriPrefix,
            string.Join('\n', definitions.Select(definition => definition.Uri)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void WithEventsHandlerReferencesRetainEveryConditionalEventAssociation()
    {
        const string workerUri = "file:///C:/work/Worker.cls";
        const string firstPublisherUri = "file:///C:/work/FirstPublisher.cls";
        const string secondPublisherUri = "file:///C:/work/SecondPublisher.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "#If UseFirst Then",
            "Private WithEvents publisher As FirstPublisher",
            "#Else",
            "Private WithEvents publisher As SecondPublisher",
            "#End If",
            "Private Sub publisher_Changed()",
            "End Sub"
        ]);
        var firstPublisherText = string.Join('\n', [
            "Attribute VB_Name = \"FirstPublisher\"",
            "Public Event Changed()"
        ]);
        var secondPublisherText = string.Join('\n', [
            "Attribute VB_Name = \"SecondPublisher\"",
            "Public Event Changed()"
        ]);
        var index = BuildIndex(new Dictionary<string, string>
        {
            [workerUri] = workerText,
            [firstPublisherUri] = firstPublisherText,
            [secondPublisherUri] = secondPublisherText
        });

        var references = index.FindReferences(
            workerUri,
            7,
            "Private Sub publisher_".Length);

        Assert.Equal(
            [firstPublisherUri, secondPublisherUri, workerUri],
            references.Select(reference => reference.Uri).ToArray());
        Assert.Equal(3, references.Count);
        Assert.Contains(references, reference =>
            reference.Uri == workerUri
            && reference.Range.Start.Line == 7
            && reference.Range.Start.Character == "Private Sub publisher_".Length
            && reference.Range.End.Character == "Private Sub publisher_Changed".Length);
    }

    [Fact]
    public void WithEventsHandlerSuffixesUseFinalUnderscoreAndFailClosedForRecoveredOnlySurfaces()
    {
        const string workerUri = "file:///C:/work/Worker.cls";
        const string publisherUri = "file:///C:/work/Publisher.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Hidden()",
            "End Sub",
            "Private Sub publisher_Bad_Name()",
            "End Sub",
            "Private Sub publisher_OptionalEvent()",
            "End Sub"
        ]);
        var publisherText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Publisher\"",
            "Private Event Hidden()",
            "Public Event Bad_Name()",
            "Public Event OptionalEvent(Optional ByVal Value As Long)"
        ]);
        var index = BuildIndex(new Dictionary<string, string>
        {
            [workerUri] = workerText,
            [publisherUri] = publisherText
        });

        Assert.Null(index.ResolveSourceDefinition(
            workerUri,
            3,
            "Private Sub publisher_".Length));
        var finalUnderscoreProcedure = index.ResolveSourceDefinition(
            workerUri,
            5,
            "Private Sub publisher_Bad_".Length);
        Assert.Equal("publisher_Bad_Name", finalUnderscoreProcedure?.Name);
        Assert.Equal(VbaSourceDefinitionKind.Procedure, finalUnderscoreProcedure?.Kind);
        Assert.Null(index.ResolveSourceDefinition(
            workerUri,
            7,
            "Private Sub publisher_".Length));
    }

    [Fact]
    public void WithEventsHandlerSuffixesExcludePlacementRecoveredEvents()
    {
        const string workerUri = "file:///C:/work/Worker.cls";
        const string publisherUri = "file:///C:/work/Publisher.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Nested()",
            "End Sub"
        ]);
        var publisherText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Publisher\"",
            "Public Sub Broken()",
            "    Event Nested()",
            "End Sub"
        ]);
        var index = BuildIndex(new Dictionary<string, string>
        {
            [workerUri] = workerText,
            [publisherUri] = publisherText
        });

        Assert.Null(index.ResolveSourceDefinition(
            workerUri,
            3,
            "Private Sub publisher_".Length));
    }

    [Fact]
    public void WithEventsHandlersFailClosedForMissingOrAmbiguousEventMetadata()
    {
        const string workerUri = "file:///C:/work/Worker.cls";
        const string duplicateAUri = "file:///C:/work/DuplicateA.cls";
        const string duplicateBUri = "file:///C:/work/DuplicateB.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents missing As MissingPublisher",
            "Private WithEvents duplicate As DuplicatePublisher",
            "Private Sub missing_Changed()",
            "End Sub",
            "Private Sub duplicate_Changed()",
            "End Sub"
        ]);
        var duplicateText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"DuplicatePublisher\"",
            "Public Event Changed()"
        ]);
        var index = BuildIndex(new Dictionary<string, string>
        {
            [workerUri] = workerText,
            [duplicateAUri] = duplicateText,
            [duplicateBUri] = duplicateText
        });

        Assert.Null(index.ResolveSourceDefinition(
            workerUri,
            4,
            "Private Sub missing_".Length));
        Assert.Null(index.ResolveSourceDefinition(
            workerUri,
            6,
            "Private Sub duplicate_".Length));
    }

    [Fact]
    public void DuplicateUnconditionalWithEventsVariablesDoNotCreateAHandlerBinding()
    {
        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string workerUri = "file:///C:/work/Worker.cls";
        var index = BuildIndex(new Dictionary<string, string>
        {
            [publisherUri] = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Publisher\"",
                "Public Event Changed()"
            ]),
            [workerUri] = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Private WithEvents publisher As Publisher",
                "Private WithEvents publisher As Publisher",
                "Private Function publisher_Changed() As Boolean",
                "End Function"
            ])
        });

        var definition = Assert.IsType<VbaSourceDefinition>(index.ResolveSourceDefinition(
            workerUri,
            4,
            "Private Function publisher_".Length));
        Assert.Equal("publisher_Changed", definition.Name);
        Assert.Equal(VbaSourceDefinitionKind.Procedure, definition.Kind);
        Assert.Equal(4, definition.Range.Start.Line);
        Assert.DoesNotContain(
            index.GetProjectValidationDiagnostics(workerUri),
            diagnostic => diagnostic.Code is "validation.eventHandlerMustBeSub"
                or "validation.incompatibleEventHandlerSignature");
    }

    [Fact]
    public void ExistingHandlerDefinitionRetainsAHiddenTypeLibEventAssociation()
    {
        const string workerUri = "file:///C:/work/Worker.cls";
        var index = BuildHiddenTypeLibIndex(string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Hidden()",
            "End Sub"
        ]));

        var target = Assert.IsType<VbaSourceDefinition>(index.ResolveSourceDefinition(
            workerUri,
            3,
            "Private Sub publisher_".Length));
        Assert.Equal(VbaDefinitionOrigin.ProjectReference, target.Identity.Origin);
        Assert.Equal("Hidden", target.Name);

        Assert.Empty(index.ResolveDefinitions(
            workerUri,
            3,
            "Private Sub publisher_".Length));

        var reference = Assert.Single(index.FindReferences(
            workerUri,
            3,
            "Private Sub publisher_".Length));
        Assert.Equal(workerUri, reference.Uri);
        Assert.Equal(3, reference.Range.Start.Line);
        Assert.Equal("Private Sub publisher_".Length, reference.Range.Start.Character);
        Assert.Equal(3, reference.Range.End.Line);
        Assert.Equal(
            "Private Sub publisher_Hidden".Length,
            reference.Range.End.Character);
        Assert.DoesNotContain(
            index.GetProjectValidationDiagnostics(workerUri),
            diagnostic => diagnostic.Code
                == "validation.incompatibleEventHandlerSignature");
    }

    [Fact]
    public void HiddenTypeLibEventGuidanceDoesNotAuthorizeDiagnosticsOrAuthoringCompletion()
    {
        const string workerUri = "file:///C:/work/Worker.cls";
        var index = BuildHiddenTypeLibIndex(string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Function publisher_Hidden() As Boolean",
            "End Function",
            "Private Sub Inspect()",
            "    publisher.",
            "End Sub"
        ]));

        var hover = Assert.IsType<VbaHoverResult>(index.ResolveHover(
            workerUri,
            3,
            "Private Function publisher_".Length));
        var hoverDefinition = Assert.Single(hover.Definitions);
        Assert.Equal("Hidden", hoverDefinition.Name);
        Assert.False(hoverDefinition.IsAuthoringAvailable);

        var signatureHelp = Assert.IsType<VbaSignatureHelp>(index.GetSignatureHelp(
            workerUri,
            3,
            "Private Function publisher_Hidden(".Length));
        Assert.Equal(
            "Event Hidden(Value As Long)",
            Assert.Single(signatureHelp.Signatures).Signature.Label);

        Assert.DoesNotContain(
            index.GetCompletionResult(workerUri, 6, "    publisher.".Length).Definitions,
            definition => definition.Name == "Hidden");
        Assert.DoesNotContain(
            index.GetProjectValidationDiagnostics(workerUri),
            diagnostic => diagnostic.Code is "validation.eventHandlerMustBeSub"
                or "validation.incompatibleEventHandlerSignature");
    }

    [Theory]
    [InlineData((int)VbaEventHandlerValidationAuthority.SourceDeclared, true)]
    [InlineData((int)VbaEventHandlerValidationAuthority.CurrentHostProjected, true)]
    [InlineData((int)VbaEventHandlerValidationAuthority.ExternalTypeLibAdvisory, false)]
    [InlineData((int)VbaEventHandlerValidationAuthority.LastKnownGoodHostAdvisory, false)]
    public void ValidationAuthorityControlsCompileStyleHandlerDiagnostics(
        int authorityValue,
        bool expectedAuthoritative)
    {
        var authority = (VbaEventHandlerValidationAuthority)authorityValue;
        const string uri = "file:///C:/work/Worker.cls";
        var range = new VbaRange(
            new VbaPosition(1, 19),
            new VbaPosition(1, 28));
        var variable = new VbaSourceDefinition(
            VbaDefinitionIdentity.ForSource(uri, "publisher", range),
            new VbaDefinitionLocation(uri, range),
            "publisher",
            VbaSourceDefinitionKind.Variable,
            VbaSourceDefinitionVisibility.Private,
            "Worker",
            IsWithEvents: true);
        var variableTarget = new VbaDefinitionNameTarget(variable);
        var eventContract = new VbaResolvedEventContract(
            new VbaProjectedEventContractIdentity(
                "test",
                "Worker.publisher.Changed"),
            "Changed",
            new VbaCallableSignature(
                "Event Changed(Value As Long)",
                [
                    new VbaCallableParameter(
                        "Value",
                        TypeReference: new VbaTypeReference("Long"),
                        IsByRef: true)
                ],
                CallableKind: VbaCallableKind.Event),
            "Projected Event contract.",
            authority,
            IsConditionalContract: false);
        var bindingSet = new VbaWithEventsEventBindingSet(
            variableTarget,
            [
                new VbaWithEventsEventBindingEntry(
                    variable,
                    VbaWithEventsEventBindingStatus.Resolved,
                    EventContracts: [eventContract])
            ]);

        Assert.Equal(
            expectedAuthoritative,
            bindingSet.IsFullyDiagnosticAuthoritative);
        var signatureSet = Assert.IsType<VbaResolvedEventSignatureSet>(
            bindingSet.ResolvedEventSignatures);
        var retainedContract = Assert.Single(signatureSet.Contracts);
        Assert.Same(eventContract, retainedContract);
        Assert.Null(retainedContract.Definition);
        Assert.Null(retainedContract.NavigableLocation);
    }

    [Fact]
    public void Projected_event_contract_uses_retained_canonical_parameter_evidence()
    {
        var contract = new VbaResolvedEventContract(
            new VbaProjectedEventContractIdentity(
                "host",
                "Worker.publisher.Changed"),
            "Changed",
            new VbaCallableSignature(
                "Event Changed(ByRef Value As ProjectedLong)",
                [
                    new VbaCallableParameter(
                        "Value",
                        TypeReference: new VbaTypeReference("ProjectedLong"),
                        IsByRef: true)
                ],
                CallableKind: VbaCallableKind.Event),
            "Projected Event contract.",
            VbaEventHandlerValidationAuthority.CurrentHostProjected,
            IsConditionalContract: false,
            IsAuthoringAvailable: false,
            ParameterTypeEvidence:
            [
                new VbaResolvedEventParameterTypeEvidence(
                    "Long",
                    ReferenceQualifiedDisplayName: null,
                    new VbaIntrinsicParameterTypeIdentity("Long"))
            ]);
        var compatibility = AnalyzeProjectedEventContract(
            "Private Sub publisher_Changed(ByRef Value As Long)",
            contract);

        Assert.Equal(
            VbaEventHandlerCompatibilityState.Compatible,
            Assert.Single(compatibility.Signatures).State);
        Assert.False(contract.IsAuthoringAvailable);
    }

    [Fact]
    public void Projected_event_contract_without_canonical_type_evidence_is_indeterminate()
    {
        var contract = new VbaResolvedEventContract(
            new VbaProjectedEventContractIdentity(
                "host",
                "Worker.publisher.Changed"),
            "Changed",
            new VbaCallableSignature(
                "Event Changed(ByRef Value As ProjectedLong)",
                [
                    new VbaCallableParameter(
                        "Value",
                        TypeReference: new VbaTypeReference("ProjectedLong"),
                        IsByRef: true)
                ],
                CallableKind: VbaCallableKind.Event),
            Documentation: null,
            VbaEventHandlerValidationAuthority.CurrentHostProjected,
            IsConditionalContract: false,
            ParameterTypeEvidence: [null]);

        var compatibility = AnalyzeProjectedEventContract(
            "Private Sub publisher_Changed(ByRef Value As Long)",
            contract);

        Assert.Equal(
            VbaEventHandlerCompatibilityState.Indeterminate,
            Assert.Single(compatibility.Signatures).State);
        Assert.False(compatibility.ShouldReportDiagnostic);
    }

    [Fact]
    public void Projected_event_contract_compares_ParamArray_role()
    {
        var contract = new VbaResolvedEventContract(
            new VbaProjectedEventContractIdentity(
                "host",
                "Worker.publisher.Changed"),
            "Changed",
            new VbaCallableSignature(
                "Event Changed(ParamArray Values() As Long)",
                [
                    new VbaCallableParameter(
                        "Values",
                        TypeReference: new VbaTypeReference("Long"),
                        IsByRef: true,
                        IsParamArray: true)
                ],
                CallableKind: VbaCallableKind.Event),
            Documentation: null,
            VbaEventHandlerValidationAuthority.CurrentHostProjected,
            IsConditionalContract: false,
            ParameterTypeEvidence:
            [
                new VbaResolvedEventParameterTypeEvidence(
                    "Long",
                    ReferenceQualifiedDisplayName: null,
                    new VbaIntrinsicParameterTypeIdentity("Long"))
            ]);

        var compatibility = AnalyzeProjectedEventContract(
            "Private Sub publisher_Changed(ByRef Values As Long)",
            contract);
        var signature = Assert.Single(compatibility.Signatures);

        Assert.Equal(VbaEventHandlerCompatibilityState.Incompatible, signature.State);
        Assert.Equal(
            ["parameter 1 role: expected ParamArray, found required"],
            signature.MismatchReasons);
    }

    [Fact]
    public void Projected_event_contract_identity_is_case_insensitive()
    {
        Assert.Equal(
            new VbaProjectedEventContractIdentity(
                "HOST",
                "Worker.publisher.Changed"),
            new VbaProjectedEventContractIdentity(
                "host",
                "worker.PUBLISHER.changed"));
    }

    [Fact]
    public void Resolved_event_signatures_retain_mixed_authority_provenance()
    {
        const string uri = "file:///C:/work/Worker.cls";
        var range = new VbaRange(
            new VbaPosition(1, 19),
            new VbaPosition(1, 28));
        var variable = new VbaSourceDefinition(
            VbaDefinitionIdentity.ForSource(uri, "publisher", range),
            new VbaDefinitionLocation(uri, range),
            "publisher",
            VbaSourceDefinitionKind.Variable,
            VbaSourceDefinitionVisibility.Private,
            "Worker",
            IsWithEvents: true);
        var identity = new VbaProjectedEventContractIdentity(
            "host",
            "Worker.publisher.Changed");
        var currentContract = new VbaResolvedEventContract(
            identity,
            "Changed",
            new VbaCallableSignature(
                "Event Changed()",
                [],
                CallableKind: VbaCallableKind.Event),
            Documentation: null,
            VbaEventHandlerValidationAuthority.CurrentHostProjected,
            IsConditionalContract: false);
        var staleContract = currentContract with
        {
            ValidationAuthority =
                VbaEventHandlerValidationAuthority.LastKnownGoodHostAdvisory,
            IsConditionalContract = true
        };
        var bindingSet = new VbaWithEventsEventBindingSet(
            new VbaDefinitionNameTarget(variable),
            [
                new VbaWithEventsEventBindingEntry(
                    variable,
                    VbaWithEventsEventBindingStatus.Resolved,
                    EventContracts: [currentContract, staleContract])
            ]);

        Assert.False(bindingSet.IsFullyDiagnosticAuthoritative);
        Assert.Equal(2, bindingSet.ResolvedEventSignatures?.Contracts.Count);
    }

    private static VbaEventHandlerCompatibility AnalyzeProjectedEventContract(
        string handlerDeclaration,
        VbaResolvedEventContract contract)
    {
        const string uri = "file:///C:/work/Worker.cls";
        var documents = VbaSemanticInventoryFixture.ProjectSourceDocuments(
            new Dictionary<string, string>
            {
                [uri] = string.Join('\n', [
                    "VERSION 1.0 CLASS",
                    "Attribute VB_Name = \"Worker\"",
                    "Private WithEvents publisher As Publisher",
                    handlerDeclaration,
                    "End Sub"
                ])
            });
        var document = documents[uri];
        var variable = Assert.Single(document.Definitions, definition =>
            definition.Name == "publisher");
        var handler = Assert.Single(document.Definitions, definition =>
            definition.Name == "publisher_Changed");
        var analysis = new VbaWithEventsHandlerAnalysis(
            handler,
            new VbaWithEventsHandlerNameDecomposition("publisher", "Changed"),
            new VbaWithEventsEventBindingSet(
                new VbaDefinitionNameTarget(variable),
                [
                    new VbaWithEventsEventBindingEntry(
                        variable,
                        VbaWithEventsEventBindingStatus.Resolved,
                        EventContracts: [contract])
                ]),
            VbaWithEventsHandlerRecognition.ResolvedHandler,
            EventTarget: null);
        var semanticModel = new VbaWithEventsSemanticModel(
            new VbaNameResolutionService(
                documents.Values.ToArray(),
                referenceSelection: null,
                VbaProjectReferenceCatalogSet.Empty));
        return semanticModel.AnalyzeHandlerCompatibility(document, analysis);
    }

    private static VbaSemanticInventory BuildHiddenTypeLibIndex(string workerText)
    {
        const string referenceName = "Generated Library";
        const string workerUri = "file:///C:/work/Worker.cls";
        var hiddenEvent = new TypeLibCatalogMember(
            "Hidden",
            VbaSourceDefinitionKind.Event,
            "Hidden Event contract.",
            new VbaCallableSignature(
                "Event Hidden(Value As Long)",
                [
                    new VbaCallableParameter(
                        "Value",
                        TypeReference: new VbaTypeReference("Long"),
                        IsByRef: false)
                ],
                CallableKind: VbaCallableKind.Event),
            Metadata: new TypeLibCatalogCallableMetadata(
                MemberId: 1,
                FunctionFlags: 0x40));
        var catalog = TypeLibReferenceCatalogBuilder.Build(
            referenceName,
            new TypeLibCatalogMetadata(
                "Generated",
                [
                    new TypeLibCatalogType(
                        "Publisher",
                        VbaSourceDefinitionKind.Class,
                        Documentation: null,
                        Members: [hiddenEvent],
                        IsCreatable: true,
                        IsBrowsable: false,
                        Metadata: new TypeLibCatalogTypeMetadata(
                            TypeLibCatalogRawTypeKind.CoClass,
                            TypeFlags: 0x10,
                            ImplementedInterfaces:
                            [
                                new TypeLibCatalogImplementedInterface(
                                    "_PublisherEvents",
                                    TypeFlags: 0,
                                    ImplementationFlags: 0x1 | 0x2,
                                    CallableMembers: [hiddenEvent],
                                    RawTypeKind:
                                        TypeLibCatalogRawTypeKind.Dispatch)
                            ]))
                ]));
        return VbaSemanticInventoryFixture.Create(
            new Dictionary<string, string> { [workerUri] = workerText },
            VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [new VbaProjectReference(referenceName)]),
            VbaProjectReferenceCatalogSet.CreateBundled().WithCatalog(catalog));
    }

    private static VbaSemanticInventory BuildIndexWithCompleteExcelTypeLib(
        IReadOnlyDictionary<string, string> sourceDocuments)
    {
        const string referenceName = "Microsoft Excel 16.0 Object Library";
        var workbookOpen = new TypeLibCatalogMember(
            "WorkbookOpen",
            VbaSourceDefinitionKind.Event,
            "Occurs when a workbook is opened.",
            new VbaCallableSignature(
                "Event WorkbookOpen(Wb As Excel.Workbook)",
                [
                    new VbaCallableParameter(
                        "Wb",
                        TypeReference: new VbaTypeReference("Workbook", "Excel"),
                        IsByRef: false)
                ],
                CallableKind: VbaCallableKind.Event),
            Metadata: new TypeLibCatalogCallableMetadata(
                MemberId: 1,
                FunctionFlags: 0));
        var catalog = TypeLibReferenceCatalogBuilder.Build(
            referenceName,
            new TypeLibCatalogMetadata(
                "Excel",
                [
                    new TypeLibCatalogType(
                        "Application",
                        VbaSourceDefinitionKind.Class,
                        "Represents the Microsoft Excel application.",
                        Members: [workbookOpen],
                        IsCreatable: true,
                        IsApplicationObject: true,
                        Metadata: new TypeLibCatalogTypeMetadata(
                            TypeLibCatalogRawTypeKind.CoClass,
                            TypeFlags: 0,
                            ImplementedInterfaces:
                            [
                                new TypeLibCatalogImplementedInterface(
                                    "AppEvents",
                                    TypeFlags: 0,
                                    ImplementationFlags: 0x1 | 0x2,
                                    CallableMembers: [workbookOpen],
                                    RawTypeKind:
                                        TypeLibCatalogRawTypeKind.Dispatch)
                            ])),
                    new TypeLibCatalogType(
                        "Workbook",
                        VbaSourceDefinitionKind.Class,
                        "Represents a Microsoft Excel workbook.",
                        Members: [])
                ]));

        return VbaSemanticInventoryFixture.Create(
            sourceDocuments,
            VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [new VbaProjectReference(referenceName)]),
            VbaProjectReferenceCatalogSet.CreateBundled().WithCatalog(catalog));
    }

    private static VbaSemanticInventory BuildIndex(string uri, string text)
        => BuildIndex(new Dictionary<string, string> { [uri] = text });

    private static VbaSemanticInventory BuildIndex(IReadOnlyDictionary<string, string> sourceDocuments)
        => VbaSemanticInventoryFixture.Create(
            sourceDocuments,
            VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [new VbaProjectReference("Microsoft Excel 16.0 Object Library")]),
            VbaProjectReferenceCatalogSet.CreateBundled());
}
