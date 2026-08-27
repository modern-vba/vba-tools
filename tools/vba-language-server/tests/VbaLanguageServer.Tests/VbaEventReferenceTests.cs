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

        var sourceEvent = index.ResolveSourceDefinition(workerUri, 4, "Private Sub ".Length);
        var referenceEvent = index.ResolveSourceDefinition(workerUri, 6, "Private Sub ".Length);

        Assert.Equal("Changed", sourceEvent?.Name);
        Assert.Equal(VbaSourceDefinitionKind.Event, sourceEvent?.Kind);
        Assert.Equal(publisherUri, sourceEvent?.Uri);
        Assert.Equal("WorkbookOpen", referenceEvent?.Name);
        Assert.Equal(VbaSourceDefinitionKind.Event, referenceEvent?.Kind);
        Assert.Equal("Application", referenceEvent?.ParentTypeName);
        Assert.Equal("Microsoft Excel 16.0 Object Library", referenceEvent?.ModuleName);
    }

    [Fact]
    public void WithEventsHandlerSuffixesExcludeNameAndVisibilityRecoveredEvents()
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
            "Private Sub ".Length));
        Assert.Null(index.ResolveSourceDefinition(
            workerUri,
            5,
            "Private Sub ".Length));
        var optionalEvent = index.ResolveSourceDefinition(
            workerUri,
            7,
            "Private Sub ".Length);
        Assert.Equal("OptionalEvent", optionalEvent?.Name);
        Assert.True(optionalEvent?.IsRecoveredEventDeclaration);
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
            "Private Sub ".Length));
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

        Assert.Null(index.ResolveSourceDefinition(workerUri, 4, "Private Sub ".Length));
        Assert.Null(index.ResolveSourceDefinition(workerUri, 6, "Private Sub ".Length));
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
