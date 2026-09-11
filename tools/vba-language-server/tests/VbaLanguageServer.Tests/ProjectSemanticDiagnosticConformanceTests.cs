using System.Text.Json;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.SourceModel;
using VbaTools.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class ProjectSemanticDiagnosticConformanceTests
{
    [Fact]
    public void SharedAnalysisDoesNotUseADefaultMemberAfterAnInvalidParameterizedReceiverCall()
    {
        const string uri = "file:///C:/project-semantic-fixtures/Caller.bas";
        var syntax = VbaSyntaxTree.ParseModule(uri,
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\n    Call Factory().OnlyThere(Unknown:=False)\nEnd Sub\n");
        const string referenceName = "Receiver Library";
        var catalog = new VbaProjectReferenceCatalog(
            referenceName,
            ["ReceiverLib"],
            [
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "Application",
                    VbaSourceDefinitionKind.Class),
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "Factory",
                    VbaSourceDefinitionKind.Procedure,
                    Signature: new VbaCallableSignature(
                        "Function Factory(required As Variant) As Workbooks",
                        [new VbaCallableParameter("required")],
                        CallableKind: VbaCallableKind.Function),
                    ParentTypeName: "Application",
                    TypeReference: new VbaTypeReference("Workbooks", "ReceiverLib"),
                    GlobalExposure: ReferenceDefinitionGlobalExposure.MainHostGlobal),
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "Workbooks",
                    VbaSourceDefinitionKind.Class),
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "HiddenSelector",
                    VbaSourceDefinitionKind.Property,
                    Signature: new VbaCallableSignature(
                        "Property HiddenSelector() As Result",
                        [],
                        CallableKind: VbaCallableKind.Property),
                    ParentTypeName: "Workbooks",
                    TypeReference: new VbaTypeReference("Result", "ReceiverLib"),
                    PropertyAccess: VbaPropertyAccess.Readable,
                    IsAuthoringAvailable: false)
                {
                    IsDefaultMember = true,
                    PropertyAccessorKind = VbaPropertyAccessorKind.Get,
                    CallableKind = VbaCallableKind.Property,
                    IsReturnArray = false
                },
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "Result",
                    VbaSourceDefinitionKind.Class),
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "OnlyThere",
                    VbaSourceDefinitionKind.Procedure,
                    Signature: new VbaCallableSignature(
                        "Sub OnlyThere()",
                        [],
                        CallableKind: VbaCallableKind.Sub),
                    ParentTypeName: "Result")
            ]);
        var inputs = VbaProjectSemanticInputs.Capture(
            VbaReferenceSelection.Capture([referenceName], referenceName),
            VbaProjectReferenceCatalogSet.Empty.WithCatalog(catalog));
        var document = VbaSourceDocumentProjector.Project(uri, syntax);
        var inventory = VbaSemanticInventory.Create(
            new Dictionary<string, VbaSourceDocument>(StringComparer.OrdinalIgnoreCase)
            {
                [uri] = document
            },
            VbaProjectReferenceSelection.Create(
                "excel",
                [new VbaProjectReference(referenceName)]),
            inputs.ReferenceCatalogs);

        var diagnostics = VbaProjectSourceAnalysis.Analyze([syntax], inputs);

        Assert.Null(inventory.ResolveSourceTarget(
            uri,
            2,
            "    Call Factory().".Length));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("validation.incompatibleCallArgumentList", diagnostic.Code);
        Assert.Equal(2, diagnostic.Range.Start.Line);
        Assert.Equal(9, diagnostic.Range.Start.Character);
    }

    [Fact]
    public void SharedAnalysisResolvesImplicitDefaultMemberResultBeforeFollowingMember()
    {
        const string uri = "file:///C:/project-semantic-fixtures/Caller.bas";
        var syntax = VbaSyntaxTree.ParseModule(uri,
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\n    Dim book As Variant\n    Call Workbooks(book).Close(SaveChanges:=False)\n    Call Workbooks.Item(book).Close()\n    Call ActiveWorkbook.Close(SaveChanges:=False)\n    Call Workbooks(Unknown:=book).Close()\n    Call Workbooks(book).Close(Unknown:=False)\n    Call Workbooks.Close(SaveChanges:=False)\nEnd Sub\n");
        var catalog = TypeLibReferenceCatalogBuilder.Build(
            "Microsoft Excel 16.0 Object Library",
            new TypeLibCatalogMetadata(
                "Excel",
                [
                    new TypeLibCatalogType(
                        "Application",
                        VbaSourceDefinitionKind.Class,
                        Documentation: null,
                        Members:
                        [
                            new TypeLibCatalogMember(
                                "Workbooks",
                                VbaSourceDefinitionKind.Property,
                                Documentation: null,
                                Signature: null,
                                new VbaTypeReference("Workbooks", "Excel"),
                                VbaPropertyAccess.Readable,
                                new TypeLibCatalogCallableMetadata(572, 0)
                                {
                                    PropertyAccessorKind = VbaPropertyAccessorKind.Get,
                                    IsReturnArray = false
                                }),
                            new TypeLibCatalogMember(
                                "ActiveWorkbook",
                                VbaSourceDefinitionKind.Property,
                                Documentation: null,
                                new VbaCallableSignature(
                                    "Property ActiveWorkbook() As Workbook",
                                    [],
                                    CallableKind: VbaCallableKind.Property),
                                new VbaTypeReference("Workbook", "Excel"),
                                VbaPropertyAccess.Readable,
                                new TypeLibCatalogCallableMetadata(308, 0)
                                {
                                    PropertyAccessorKind = VbaPropertyAccessorKind.Get,
                                    IsReturnArray = false
                                })
                        ],
                        IsApplicationObject: true),
                    new TypeLibCatalogType(
                        "Workbooks",
                        VbaSourceDefinitionKind.Class,
                        Documentation: null,
                        Members:
                        [
                            new TypeLibCatalogMember(
                                "Item",
                                VbaSourceDefinitionKind.Property,
                                Documentation: null,
                                new VbaCallableSignature(
                                    "Property Item(index As Variant) As Workbook",
                                    [new VbaCallableParameter("index", TypeReference: new("Variant"))],
                                    CallableKind: VbaCallableKind.Property),
                                new VbaTypeReference("VisibleItemResult", "Excel"),
                                VbaPropertyAccess.Readable,
                                new TypeLibCatalogCallableMetadata(170, 0)
                                {
                                    PropertyAccessorKind = VbaPropertyAccessorKind.Get,
                                    IsReturnArray = false
                                }),
                            new TypeLibCatalogMember(
                                "_Default",
                                VbaSourceDefinitionKind.Property,
                                Documentation: null,
                                new VbaCallableSignature(
                                    "Property _Default(index As Variant) As Workbook",
                                    [new VbaCallableParameter("index", TypeReference: new("Variant"))],
                                    CallableKind: VbaCallableKind.Property),
                                new VbaTypeReference("Workbook", "Excel"),
                                VbaPropertyAccess.Readable,
                                new TypeLibCatalogCallableMetadata(0, 1024)
                                {
                                    PropertyAccessorKind = VbaPropertyAccessorKind.Get,
                                    IsReturnArray = false
                                }),
                            new TypeLibCatalogMember(
                                "Close",
                                VbaSourceDefinitionKind.Procedure,
                                Documentation: null,
                                new VbaCallableSignature(
                                    "Sub Close()",
                                    [],
                                    CallableKind: VbaCallableKind.Sub))
                        ]),
                    new TypeLibCatalogType(
                        "Workbook",
                        VbaSourceDefinitionKind.Class,
                        Documentation: null,
                        Members:
                        [
                            new TypeLibCatalogMember(
                                "Close",
                                VbaSourceDefinitionKind.Procedure,
                                Documentation: null,
                                new VbaCallableSignature(
                                    "Sub Close([SaveChanges As Variant])",
                                    [
                                        new VbaCallableParameter(
                                            "SaveChanges",
                                            TypeReference: new("Variant"),
                                            IsOptional: true)
                                    ],
                                    CallableKind: VbaCallableKind.Sub))
                        ]),
                    new TypeLibCatalogType(
                        "VisibleItemResult",
                        VbaSourceDefinitionKind.Class,
                        Documentation: null,
                        Members:
                        [
                            new TypeLibCatalogMember(
                                "Close",
                                VbaSourceDefinitionKind.Procedure,
                                Documentation: null,
                                new VbaCallableSignature(
                                    "Sub Close()",
                                    [],
                                    CallableKind: VbaCallableKind.Sub))
                        ])
                ]));
        var defaultMember = Assert.Single(catalog.Definitions,
            definition => definition.IsDefaultMember);
        Assert.Equal("_Default", defaultMember.Name);
        Assert.False(defaultMember.IsAuthoringAvailable);
        var inputs = VbaProjectSemanticInputs.Capture(
            VbaReferenceSelection.Capture([catalog.ReferenceName], catalog.ReferenceName),
            VbaProjectReferenceCatalogSet.Empty.WithCatalog(catalog));
        const string completionUri =
            "file:///C:/project-semantic-fixtures/CompletionCaller.bas";
        var completionSyntax = VbaSyntaxTree.ParseModule(
            completionUri,
            "Attribute VB_Name = \"CompletionCaller\"\nPublic Sub Run()\n    Dim selected As Object\n    Set selected = Workbooks.\nEnd Sub\n");
        var inventory = VbaSemanticInventory.Create(
            new Dictionary<string, VbaSourceDocument>(StringComparer.OrdinalIgnoreCase)
            {
                [completionUri] = VbaSourceDocumentProjector.Project(
                    completionUri,
                    completionSyntax)
            },
            VbaProjectReferenceSelection.Create(
                "excel",
                [new VbaProjectReference(catalog.ReferenceName)]),
            inputs.ReferenceCatalogs);

        var diagnostics = VbaProjectSourceAnalysis.Analyze([syntax], inputs);
        var completions = inventory.GetCompletionResult(
            completionUri,
            3,
            "    Set selected = Workbooks.".Length).Definitions;

        Assert.All(diagnostics, diagnostic =>
            Assert.Equal("validation.incompatibleCallArgumentList", diagnostic.Code));
        Assert.Equal(new[] { 6, 7, 8 }, diagnostics.Select(diagnostic =>
            diagnostic.Range.Start.Line));
        Assert.Contains(completions, definition => definition.Name == "Item");
        Assert.DoesNotContain(completions, definition => definition.Name == "_Default");
    }

    [Fact]
    public void SharedAnalysisPreservesDownstreamDiagnosticsAfterArrayIndexing()
    {
        const string callerUri = "file:///C:/project-semantic-fixtures/Caller.bas";
        const string itemUri = "file:///C:/project-semantic-fixtures/Item.cls";
        var callerSyntax = VbaSyntaxTree.ParseModule(
            callerUri,
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\n    Dim items(0 To 1) As Item\n    Call items(0).Finish(Unknown:=False)\n    Call ItemsFromFunction(0).Finish(Unknown:=False)\nEnd Sub\nPrivate Function ItemsFromFunction() As Item()\nEnd Function\n");
        var itemSyntax = VbaSyntaxTree.ParseModule(
            itemUri,
            "VERSION 1.0 CLASS\nAttribute VB_Name = \"Item\"\nPublic Sub Finish()\nEnd Sub\n");
        var inputs = VbaProjectSemanticInputs.Capture(
            VbaReferenceSelection.Capture([], null),
            VbaProjectReferenceCatalogSet.Empty);

        var diagnostics = VbaProjectSourceAnalysis.Analyze(
            [callerSyntax, itemSyntax],
            inputs);

        Assert.All(diagnostics, diagnostic =>
            Assert.Equal("validation.incompatibleCallArgumentList", diagnostic.Code));
        Assert.Equal(new[] { 3, 4 }, diagnostics.Select(diagnostic =>
            diagnostic.Range.Start.Line));
    }

    [Fact]
    public void SharedAnalysisPropagatesExternalArrayPropertyElementType()
    {
        const string uri = "file:///C:/project-semantic-fixtures/Caller.bas";
        var syntax = VbaSyntaxTree.ParseModule(
            uri,
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\n    Call Items(0).Finish(Unknown:=False)\nEnd Sub\n");
        const string referenceName = "Array Property Library";
        var catalog = new VbaProjectReferenceCatalog(
            referenceName,
            ["ArrayProperty"],
            [
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "Application",
                    VbaSourceDefinitionKind.Class),
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "Items",
                    VbaSourceDefinitionKind.Property,
                    ParentTypeName: "Application",
                    TypeReference: new VbaTypeReference("Item", "ArrayProperty"),
                    PropertyAccess: VbaPropertyAccess.Readable,
                    GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal,
                    IsCallableMetadataComplete: true)
                {
                    PropertyAccessorKind = VbaPropertyAccessorKind.Get,
                    IsReturnArray = true
                },
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "Item",
                    VbaSourceDefinitionKind.Class),
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "Finish",
                    VbaSourceDefinitionKind.Procedure,
                    Signature: new VbaCallableSignature(
                        "Sub Finish()",
                        [],
                        CallableKind: VbaCallableKind.Sub,
                        SupportsNamedArguments: true),
                    ParentTypeName: "Item",
                    IsCallableMetadataComplete: true)
                {
                    CallableKind = VbaCallableKind.Sub
                }
            ]);
        var inputs = VbaProjectSemanticInputs.Capture(
            VbaReferenceSelection.Capture([referenceName], referenceName),
            VbaProjectReferenceCatalogSet.Empty.WithCatalog(catalog));

        var diagnostic = Assert.Single(
            VbaProjectSourceAnalysis.Analyze([syntax], inputs));

        Assert.Equal("validation.incompatibleCallArgumentList", diagnostic.Code);
        Assert.Equal(2, diagnostic.Range.Start.Line);
    }

    [Fact]
    public void SharedAnalysisDoesNotAssumeADefaultMemberWhenForeignArrayShapeIsUnknown()
    {
        const string uri = "file:///C:/project-semantic-fixtures/Caller.bas";
        var syntax = VbaSyntaxTree.ParseModule(
            uri,
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\n    Call Factory(0).Finish(Unknown:=False)\n    Call KnownFinish(Unknown:=False)\nEnd Sub\nPrivate Sub KnownFinish()\nEnd Sub\n");
        const string referenceName = "Unknown Array Shape Library";
        var catalog = new VbaProjectReferenceCatalog(
            referenceName,
            ["UnknownShape"],
            [
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "Application",
                    VbaSourceDefinitionKind.Class),
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "Factory",
                    VbaSourceDefinitionKind.Procedure,
                    Signature: new VbaCallableSignature(
                        "Function Factory() As Collection",
                        [],
                        CallableKind: VbaCallableKind.Function,
                        SupportsNamedArguments: true),
                    ParentTypeName: "Application",
                    TypeReference: new VbaTypeReference("Collection", "UnknownShape"),
                    GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal,
                    IsCallableMetadataComplete: true)
                {
                    CallableKind = VbaCallableKind.Function,
                    IsReturnArray = null
                },
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "Collection",
                    VbaSourceDefinitionKind.Class),
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "_Default",
                    VbaSourceDefinitionKind.Property,
                    Signature: new VbaCallableSignature(
                        "Property _Default(index As Variant) As Item",
                        [new VbaCallableParameter(
                            "index",
                            TypeReference: new VbaTypeReference("Variant"))],
                        CallableKind: VbaCallableKind.Property,
                        SupportsNamedArguments: true),
                    ParentTypeName: "Collection",
                    TypeReference: new VbaTypeReference("Item", "UnknownShape"),
                    PropertyAccess: VbaPropertyAccess.Readable,
                    IsAuthoringAvailable: false,
                    IsCallableMetadataComplete: true)
                {
                    IsDefaultMember = true,
                    PropertyAccessorKind = VbaPropertyAccessorKind.Get,
                    CallableKind = VbaCallableKind.Property,
                    IsReturnArray = false
                },
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "Item",
                    VbaSourceDefinitionKind.Class),
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "Finish",
                    VbaSourceDefinitionKind.Procedure,
                    Signature: new VbaCallableSignature(
                        "Sub Finish()",
                        [],
                        CallableKind: VbaCallableKind.Sub,
                        SupportsNamedArguments: true),
                    ParentTypeName: "Item",
                    IsCallableMetadataComplete: true)
                {
                    CallableKind = VbaCallableKind.Sub
                }
            ]);
        var inputs = VbaProjectSemanticInputs.Capture(
            VbaReferenceSelection.Capture([referenceName], referenceName),
            VbaProjectReferenceCatalogSet.Empty.WithCatalog(catalog));

        var diagnostics = VbaProjectSourceAnalysis.Analyze([syntax], inputs);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("validation.incompatibleCallArgumentList", diagnostic.Code);
        Assert.Equal(3, diagnostic.Range.Start.Line);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public void SharedAnalysisRequiresScalarDefaultMemberResultBeforeFollowingMember(
        bool? defaultResultIsArray)
    {
        const string uri = "file:///C:/project-semantic-fixtures/Caller.bas";
        var syntax = VbaSyntaxTree.ParseModule(
            uri,
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\n    Call Workbooks(0).Finish(Unknown:=False)\n    Call Workbooks(Unknown:=0).Finish()\n    Call KnownFinish(Unknown:=False)\nEnd Sub\nPrivate Sub KnownFinish()\nEnd Sub\n");
        const string referenceName = "Default Result Shape Library";
        var catalog = new VbaProjectReferenceCatalog(
            referenceName,
            ["DefaultShape"],
            [
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "Application",
                    VbaSourceDefinitionKind.Class),
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "Workbooks",
                    VbaSourceDefinitionKind.Property,
                    ParentTypeName: "Application",
                    TypeReference: new VbaTypeReference("Collection", "DefaultShape"),
                    PropertyAccess: VbaPropertyAccess.Readable,
                    GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal,
                    IsCallableMetadataComplete: true)
                {
                    PropertyAccessorKind = VbaPropertyAccessorKind.Get,
                    IsReturnArray = false
                },
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "Collection",
                    VbaSourceDefinitionKind.Class),
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "_Default",
                    VbaSourceDefinitionKind.Property,
                    Signature: new VbaCallableSignature(
                        "Property _Default(index As Variant) As Item",
                        [new VbaCallableParameter(
                            "index",
                            TypeReference: new VbaTypeReference("Variant"))],
                        CallableKind: VbaCallableKind.Property,
                        SupportsNamedArguments: true),
                    ParentTypeName: "Collection",
                    TypeReference: new VbaTypeReference("Item", "DefaultShape"),
                    PropertyAccess: VbaPropertyAccess.Readable,
                    IsAuthoringAvailable: false,
                    IsCallableMetadataComplete: true)
                {
                    IsDefaultMember = true,
                    PropertyAccessorKind = VbaPropertyAccessorKind.Get,
                    CallableKind = VbaCallableKind.Property,
                    IsReturnArray = defaultResultIsArray
                },
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "Item",
                    VbaSourceDefinitionKind.Class),
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "Finish",
                    VbaSourceDefinitionKind.Procedure,
                    Signature: new VbaCallableSignature(
                        "Sub Finish()",
                        [],
                        CallableKind: VbaCallableKind.Sub,
                        SupportsNamedArguments: true),
                    ParentTypeName: "Item",
                    IsCallableMetadataComplete: true)
                {
                    CallableKind = VbaCallableKind.Sub
                }
            ]);
        var inputs = VbaProjectSemanticInputs.Capture(
            VbaReferenceSelection.Capture([referenceName], referenceName),
            VbaProjectReferenceCatalogSet.Empty.WithCatalog(catalog));

        var diagnostics = VbaProjectSourceAnalysis.Analyze([syntax], inputs);

        Assert.All(diagnostics, diagnostic =>
            Assert.Equal("validation.incompatibleCallArgumentList", diagnostic.Code));
        Assert.Equal(new[] { 3, 4 }, diagnostics.Select(diagnostic =>
            diagnostic.Range.Start.Line));
    }

    public static IEnumerable<object[]> ConformanceCases()
        => ReadCases().Select(fixture => new object[] { fixture.GetProperty("id").GetString()! });

    [Theory]
    [MemberData(nameof(ConformanceCases))]
    public void CapturedSourceSemanticDiagnosticsMatchTheNeutralLiteralCorpus(string caseId)
    {
        var fixture = ReadCases().Single(item => item.GetProperty("id").GetString() == caseId);
        var sources = fixture.GetProperty("sources").EnumerateArray().Select(source =>
        {
            var fileName = source.GetProperty("fileName").GetString()!;
            var uri = "file:///C:/project-semantic-fixtures/" + fileName;
            var text = source.GetProperty("source").GetString()!;
            var tree = VbaSyntaxTree.ParseModule(uri, text);
            Assert.Empty(VbaDiagnosticPipeline.CollectDocument(tree, uri).Diagnostics);
            return (FileName: fileName, Uri: uri, Document: VbaSourceDocumentProjector.Project(uri, tree));
        }).ToArray();
        var documents = sources.ToDictionary(source => source.Uri, source => source.Document,
            StringComparer.OrdinalIgnoreCase);
        var catalogs = VbaProjectReferenceCatalogSet.Empty;
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var names = new List<VbaProjectReference>();
        if (fixture.TryGetProperty("referenceCatalogs", out var catalogData))
        {
            foreach (var entry in catalogData.EnumerateArray())
            {
                var catalog = entry.Deserialize<VbaProjectReferenceCatalog>(jsonOptions)!;
                catalogs = catalogs.WithCatalog(catalog);
                names.Add(new(catalog.ReferenceName));
            }
        }
        var host = fixture.TryGetProperty("hostEvents", out var hostData)
            ? hostData.Deserialize<VbaIntrinsicHostEventCatalog>(jsonOptions) : null;
        var inventory = VbaSemanticInventory.Create(documents,
            referenceSelection: names.Count == 0 ? null : VbaProjectReferenceSelection.Create("word", names),
            referenceCatalogs: catalogs, intrinsicHostEventCatalog: host);
        if (caseId is "callbyname-paramarray-accepted" or "callbyname-paramarray-invalid")
        {
            var target = Assert.IsAssignableFrom<VbaResolvedNameTarget>(
                inventory.ResolveSourceTarget(
                    sources[0].Uri,
                    caseId == "callbyname-paramarray-accepted" ? 6 : 5,
                    "    value = ".Length));
            Assert.Equal("CallByName", target.CanonicalName);
            Assert.Equal(
                VbaDefinitionOrigin.ProjectReference,
                target.SelectedDefinition.Identity.Origin);
            var signature = Assert.IsType<VbaCallableSignature>(
                target.SelectedDefinition.Signature);
            var parameter = Assert.IsType<VbaCallableParameter>(signature.Parameters[^1]);
            Assert.Equal("Args", parameter.Name);
            Assert.True(parameter.IsParamArray);
            Assert.True(parameter.IsArray);
        }
        if (caseId.StartsWith("external-", StringComparison.Ordinal))
        {
            var target = Assert.IsAssignableFrom<VbaResolvedNameTarget>(inventory.ResolveSourceTarget(sources[0].Uri, 4, 11));
            Assert.Equal("Exists", target.CanonicalName);
            Assert.Equal(VbaDefinitionOrigin.ProjectReference, target.SelectedDefinition.Identity.Origin);
        }
        var expected = fixture.GetProperty("diagnostics").EnumerateArray().Select(diagnostic =>
        {
            var range = diagnostic.GetProperty("range");
            var start = range.GetProperty("start");
            var end = range.GetProperty("end");
            return new DiagnosticSnapshot(
                diagnostic.GetProperty("fileName").GetString()!,
                diagnostic.GetProperty("code").GetString()!,
                diagnostic.GetProperty("message").GetString()!,
                diagnostic.GetProperty("severity").GetString()!,
                start.GetProperty("line").GetInt32(), start.GetProperty("character").GetInt32(),
                end.GetProperty("line").GetInt32(), end.GetProperty("character").GetInt32());
        }).ToArray();

        var findings = sources.SelectMany(source => inventory.GetProjectValidationDiagnostics(source.Uri)
            .Select(diagnostic => (source.FileName, Diagnostic: diagnostic))).ToArray();
        var actual = findings
            .Select(diagnostic =>
            {
                var value = diagnostic.Diagnostic;
                Assert.Equal("vba-language-server", value.Source);
                return new DiagnosticSnapshot(diagnostic.FileName, value.Code, value.Message,
                    value.Severity, value.Range.Start.Line, value.Range.Start.Character,
                    value.Range.End.Line, value.Range.End.Character);
            }).ToArray();

        Assert.Equal(expected, actual);
        var expectedFindings = fixture.GetProperty("diagnostics").EnumerateArray().ToArray();
        for (var index = 0; index < findings.Length; index++)
        {
            var details = expectedFindings[index].GetProperty("details");
            if (details.ValueKind == JsonValueKind.Null)
            {
                Assert.Null(findings[index].Diagnostic.Details);
                continue;
            }
            var actualDetails = findings[index].Diagnostic.Details;
            Assert.NotNull(actualDetails);
            Assert.Equal(details.EnumerateArray().Select(ReadDetail).ToArray(),
                actualDetails.Select(detail => new DetailSnapshot(
                    detail.Location?.Uri,
                    detail.Location?.Range.Start.Line, detail.Location?.Range.Start.Character,
                    detail.Location?.Range.End.Line, detail.Location?.Range.End.Character,
                    detail.RelatedMessage, detail.FallbackText)).ToArray());
        }
    }

    private static DetailSnapshot ReadDetail(JsonElement detail)
    {
        var location = detail.GetProperty("location");
        var relatedMessage = detail.GetProperty("relatedMessage").GetString()!;
        var fallbackText = detail.GetProperty("fallbackText").GetString()!;
        if (location.ValueKind == JsonValueKind.Null)
        {
            return new(null, null, null, null, null, relatedMessage, fallbackText);
        }
        var range = location.GetProperty("range");
        return new("file:///C:/project-semantic-fixtures/" + location.GetProperty("fileName").GetString(),
            range.GetProperty("start").GetProperty("line").GetInt32(),
            range.GetProperty("start").GetProperty("character").GetInt32(),
            range.GetProperty("end").GetProperty("line").GetInt32(),
            range.GetProperty("end").GetProperty("character").GetInt32(), relatedMessage, fallbackText);
    }

    private sealed record DetailSnapshot(string? Uri, int? StartLine, int? StartCharacter,
        int? EndLine, int? EndCharacter, string RelatedMessage, string FallbackText);

    private static JsonElement[] ReadCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "fixtures", "project-semantic-diagnostics", "cases.json")));
        Assert.Equal("1.0", document.RootElement.GetProperty("schemaVersion").GetString());
        return document.RootElement.GetProperty("cases").EnumerateArray()
            .Select(fixture => fixture.Clone()).ToArray();
    }

    private sealed record DiagnosticSnapshot(
        string FileName, string Code, string Message, string Severity,
        int StartLine, int StartCharacter, int EndLine, int EndCharacter);
}
