using VbaLanguageServer.SourceModel;
using VbaTools.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class CallablePresentationCompatibilityTests
{
    [Fact]
    public void Legacy_setter_keeps_its_structural_property_kind_without_inventing_metadata()
    {
        const string uri = "file:///C:/work/LegacySetter.bas";
        const string reference = "Visual Basic For Applications";
        var signature = new VbaCallableSignature("stale label is not evidence",
            [new("index", TypeReference: new("Long"), IsByRef: false),
             new("value", TypeReference: new("Long"), IsByRef: false)],
            SupportsNamedArguments: true);
        var catalogs = VbaProjectReferenceCatalogSet.Empty.WithCatalog(new(
            reference, ["VBA"],
            [new(reference, "Item", VbaSourceDefinitionKind.Property,
                Signature: signature,
                PropertyAccess: VbaPropertyAccess.Writable,
                GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal)
                { PropertyAccessorKind = VbaPropertyAccessorKind.Let }]));
        var inventory = VbaSemanticInventoryFixture.Create(new Dictionary<string, string>
        {
            [uri] = "Attribute VB_Name = \"LegacySetter\"\nPublic Sub Run()\n    Item(1) = 2\nEnd Sub\n"
        }, referenceCatalogs: catalogs);

        var help = Assert.IsType<VbaSignatureHelp>(inventory.GetSignatureHelp(uri, 2, 9));

        Assert.Equal("Property Item(index As Long)", help.Signature.Label);
        Assert.Equal("index As Long", Assert.Single(help.Signature.Parameters).Label);
        Assert.Null(help.Signature.CallableKind);
        Assert.Null(signature.CallableKind);
        Assert.Equal(0, help.ActiveParameter);
    }
}
