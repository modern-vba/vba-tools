using System.Runtime.InteropServices.ComTypes;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaCreatableTypeTests
{
    [Fact]
    public void SourceClassAndFormModulesAreCreatableButOtherSourceTypesAreNot()
    {
        const string moduleUri = "file:///C:/work/Types.bas";
        const string classUri = "file:///C:/work/Worker.cls";
        const string formUri = "file:///C:/work/Dialog.frm";
        var index = VbaSemanticInventoryFixture.Create(new Dictionary<string, string>
        {
            [moduleUri] = string.Join('\n', [
                "Attribute VB_Name = \"Types\"",
                "Public Type Point",
                "    X As Long",
                "End Type",
                "Public Enum State",
                "    Ready",
                "End Enum"
            ]),
            [classUri] = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Option Explicit"
            ]),
            [formUri] = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "End",
                "Attribute VB_Name = \"Dialog\"",
                "Option Explicit"
            ])
        });

        Assert.True(Assert.Single(
            index.GetDocumentDefinitions(classUri),
            definition => definition.Kind == VbaSourceDefinitionKind.Class).IsCreatable);
        Assert.True(Assert.Single(
            index.GetDocumentDefinitions(formUri),
            definition => definition.Kind == VbaSourceDefinitionKind.Form).IsCreatable);
        Assert.All(
            index.GetDocumentDefinitions(moduleUri),
            definition => Assert.False(definition.IsCreatable));
    }

    [Theory]
    [InlineData(TYPEKIND.TKIND_COCLASS, true)]
    [InlineData(TYPEKIND.TKIND_DISPATCH, false)]
    [InlineData(TYPEKIND.TKIND_INTERFACE, false)]
    [InlineData(TYPEKIND.TKIND_RECORD, false)]
    [InlineData(TYPEKIND.TKIND_ENUM, false)]
    public void OnlyTypeLibCoClassesAreCreatable(TYPEKIND typeKind, bool expected)
    {
        Assert.Equal(expected, ComTypeLibCatalogMetadataReader.IsCreatableTypeKind(typeKind));
    }

    [Fact]
    public void TypeLibCatalogPreservesCreatableCapabilityAcrossDuplicateTypes()
    {
        var catalog = TypeLibReferenceCatalogBuilder.Build(
            "Generated Library",
            new TypeLibCatalogMetadata(
                "Generated",
                [
                    new TypeLibCatalogType(
                        "Widget",
                        VbaSourceDefinitionKind.Class,
                        null,
                        [],
                        IsCreatable: false),
                    new TypeLibCatalogType(
                        "Widget",
                        VbaSourceDefinitionKind.Class,
                        null,
                        [],
                        IsCreatable: true),
                    new TypeLibCatalogType(
                        "IWidget",
                        VbaSourceDefinitionKind.Class,
                        null,
                        [],
                        IsCreatable: false)
                ]));

        Assert.True(Assert.Single(
            catalog.Definitions,
            definition => definition.Name == "Widget").IsCreatable);
        Assert.False(Assert.Single(
            catalog.Definitions,
            definition => definition.Name == "IWidget").IsCreatable);

        var definitions = VbaProjectReferenceCatalogSet.Empty
            .WithCatalog(catalog)
            .GetActiveDefinitions(VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [new VbaProjectReference("Generated Library")]));
        Assert.True(Assert.Single(
            definitions,
            definition => definition.Name == "Widget").IsCreatable);
        Assert.False(Assert.Single(
            definitions,
            definition => definition.Name == "IWidget").IsCreatable);
    }

    [Fact]
    public void BundledCatalogMarksOnlyKnownCreatableTypes()
    {
        var catalogs = VbaProjectReferenceCatalogSet.CreateBundled();
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [
                new VbaProjectReference("Visual Basic For Applications"),
                new VbaProjectReference("Microsoft Excel 16.0 Object Library"),
                new VbaProjectReference("Microsoft Scripting Runtime"),
                new VbaProjectReference("Microsoft Office 16.0 Object Library"),
                new VbaProjectReference("Microsoft Outlook 16.0 Object Library")
            ]);
        var definitions = catalogs.GetActiveDefinitions(selection);

        Assert.True(Assert.Single(
            definitions,
            definition => definition.ModuleName == "Visual Basic For Applications"
                && definition.Name == "Collection").IsCreatable);
        Assert.True(Assert.Single(
            definitions,
            definition => definition.ModuleName == "Microsoft Scripting Runtime"
                && definition.Name == "Dictionary").IsCreatable);
        Assert.False(Assert.Single(
            definitions,
            definition => definition.ModuleName == "Microsoft Office 16.0 Object Library"
                && definition.Name == "Application").IsCreatable);
    }
}
