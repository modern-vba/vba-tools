using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaModuleIdentityMetadataReaderTests
{
    [Fact]
    public void ReservedVbNameIsInvalid()
    {
        var metadata = VbaModuleIdentityMetadataReader.Read(
            "Attribute VB_Name = \"CDecl\"\r\n",
            VbaModuleIdentitySourceKind.ObjectModule);

        Assert.Equal(VbaModuleIdentityMetadataState.Invalid, metadata.State);
        Assert.Null(metadata.Name);
    }
}
