using System.Runtime.InteropServices.ComTypes;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
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

    [Fact]
    public void TypeLibCatalogDeduplicationUnionsPropertyAccessorFlags()
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
                                TypeReference: new VbaTypeReference("String"),
                                PropertyAccess: VbaPropertyAccess.Readable),
                            new TypeLibCatalogMember(
                                "Value",
                                VbaSourceDefinitionKind.Property,
                                "Assigns the value.",
                                PropertyAccess: VbaPropertyAccess.Writable)
                        ])
                ]));

        var property = Assert.Single(
            catalog.Definitions,
            definition => definition.Name == "Value"
                && definition.ParentTypeName == "GeneratedType");

        Assert.Equal(VbaPropertyAccess.Readable | VbaPropertyAccess.Writable, property.PropertyAccess);
        Assert.Equal("String", property.TypeReference?.Name);
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
