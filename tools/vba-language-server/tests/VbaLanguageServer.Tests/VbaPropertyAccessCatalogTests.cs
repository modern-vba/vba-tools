using System.Runtime.InteropServices.ComTypes;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaPropertyAccessCatalogTests
{
    [Theory]
    [InlineData(INVOKEKIND.INVOKE_FUNC, VbaPropertyAccess.Unknown)]
    [InlineData(INVOKEKIND.INVOKE_PROPERTYGET, VbaPropertyAccess.Readable)]
    [InlineData(INVOKEKIND.INVOKE_PROPERTYPUT, VbaPropertyAccess.Writable)]
    [InlineData(INVOKEKIND.INVOKE_PROPERTYPUTREF, VbaPropertyAccess.Writable)]
    public void TypeLibInvokeKindMapsToPropertyAccess(
        INVOKEKIND invokeKind,
        VbaPropertyAccess expectedAccess)
    {
        Assert.Equal(expectedAccess, ComTypeLibCatalogMetadataReader.GetPropertyAccess(invokeKind));
    }

    [Theory]
    [InlineData(INVOKEKIND.INVOKE_FUNC, null)]
    [InlineData(INVOKEKIND.INVOKE_PROPERTYGET, VbaPropertyAccessorKind.Get)]
    [InlineData(INVOKEKIND.INVOKE_PROPERTYPUT, VbaPropertyAccessorKind.Let)]
    [InlineData(INVOKEKIND.INVOKE_PROPERTYPUTREF, VbaPropertyAccessorKind.Set)]
    public void TypeLibInvokeKindMapsToPhysicalPropertyAccessor(
        INVOKEKIND invokeKind,
        VbaPropertyAccessorKind? expectedAccessor)
    {
        Assert.Equal(
            expectedAccessor,
            ComTypeLibCatalogMetadataReader.GetPropertyAccessorKind(invokeKind));
    }

    [Fact]
    public void TypeLibCatalogRetainsPhysicalPropertyInvokeKinds()
    {
        var catalog = TypeLibReferenceCatalogBuilder.Build(
            "Generated Library",
            new TypeLibCatalogMetadata(
                "Generated",
                [
                    new TypeLibCatalogType(
                        "GeneratedType",
                        VbaSourceDefinitionKind.Class,
                        null,
                        [
                            new TypeLibCatalogMember(
                                "Value",
                                VbaSourceDefinitionKind.Property,
                                "Returns the value.",
                                new VbaCallableSignature(
                                    "Property Get Value() As String",
                                    [],
                                    CallableKind: VbaCallableKind.Property),
                                TypeReference: new VbaTypeReference("String"),
                                PropertyAccess: VbaPropertyAccess.Readable,
                                Metadata: new TypeLibCatalogCallableMetadata(
                                    1,
                                    0)
                                {
                                    PropertyAccessorKind = VbaPropertyAccessorKind.Get
                                }),
                            new TypeLibCatalogMember(
                                "Value",
                                VbaSourceDefinitionKind.Property,
                                "Assigns the value.",
                                new VbaCallableSignature(
                                    "Property Let Value(ByVal AssignedValue As String)",
                                    [new VbaCallableParameter("AssignedValue")],
                                    CallableKind: VbaCallableKind.Property),
                                PropertyAccess: VbaPropertyAccess.Writable,
                                Metadata: new TypeLibCatalogCallableMetadata(
                                    1,
                                    0)
                                {
                                    PropertyAccessorKind = VbaPropertyAccessorKind.Let
                                }),
                            new TypeLibCatalogMember(
                                "Value",
                                VbaSourceDefinitionKind.Property,
                                "Assigns the object reference.",
                                new VbaCallableSignature(
                                    "Property Set Value(ByVal AssignedValue As Object)",
                                    [new VbaCallableParameter("AssignedValue")],
                                    CallableKind: VbaCallableKind.Property),
                                PropertyAccess: VbaPropertyAccess.Writable,
                                Metadata: new TypeLibCatalogCallableMetadata(
                                    1,
                                    0)
                                {
                                    PropertyAccessorKind = VbaPropertyAccessorKind.Set
                                })
                        ])
                ]));

        var properties = catalog.Definitions.Where(
            definition => definition.Name == "Value"
                && definition.ParentTypeName == "GeneratedType")
            .ToArray();
        Assert.Equal(
            [
                VbaPropertyAccessorKind.Get,
                VbaPropertyAccessorKind.Let,
                VbaPropertyAccessorKind.Set
            ],
            properties.Select(property => property.PropertyAccessorKind));

        var activeProperties = VbaProjectReferenceCatalogSet.Empty
            .WithCatalog(catalog)
            .GetActiveDefinitions(VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [new VbaProjectReference("Generated Library")]))
            .Where(definition => definition.Name == "Value"
                && definition.ParentTypeName == "GeneratedType")
            .ToArray();
        Assert.Equal(
            [
                VbaPropertyAccessorKind.Get,
                VbaPropertyAccessorKind.Let,
                VbaPropertyAccessorKind.Set
            ],
            activeProperties.Select(property => property.PropertyAccessorKind));
        Assert.Equal(3, activeProperties.Select(property => property.Identity).Distinct().Count());
    }

    [Fact]
    public void BundledPropertyDefinitionsDeclareKnownAccessInsteadOfUsingLegacyFallback()
    {
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference("Microsoft Excel 16.0 Object Library")]);
        var properties = VbaProjectReferenceCatalogSet.CreateBundled()
            .GetActiveDefinitions(selection)
            .Where(definition => definition.Kind == VbaSourceDefinitionKind.Property)
            .ToArray();

        Assert.NotEmpty(properties);
        Assert.All(properties, definition => Assert.NotEqual(VbaPropertyAccess.Unknown, definition.PropertyAccess));
    }
}
