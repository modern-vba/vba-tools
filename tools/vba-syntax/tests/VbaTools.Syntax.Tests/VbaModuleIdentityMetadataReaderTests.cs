using VbaTools.Syntax;
using Xunit;

namespace VbaTools.Syntax.Tests;

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

    [Fact]
    public void MalformedRecordAfterAValidRecordRetainsTheExactRepairCandidate()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"GoodIdentity\"",
            "Attribute VB_Name.\"BadIdentity\""
        ]);

        var metadata = VbaModuleIdentityMetadataReader.Read(
            source,
            VbaModuleIdentitySourceKind.StandardModule);

        Assert.Equal(VbaModuleIdentityMetadataState.Invalid, metadata.State);
        Assert.Equal(
            VbaModuleIdentityMetadataCondition.Malformed,
            metadata.Condition);
        Assert.Null(metadata.AuthoritativeRecordIndex);
        Assert.Collection(
            metadata.Records,
            valid =>
            {
                Assert.Equal("GoodIdentity", valid.Name);
                Assert.False(valid.IsMalformedOrMisplaced);
                Assert.Equal(0, valid.RepairRange.Start.Line);
                Assert.Equal(21, valid.RepairRange.Start.Character);
                Assert.Equal(33, valid.RepairRange.End.Character);
            },
            malformed =>
            {
                Assert.Null(malformed.Name);
                Assert.True(malformed.IsMalformedOrMisplaced);
                Assert.Equal(1, malformed.RepairRange.Start.Line);
                Assert.Equal(10, malformed.RepairRange.Start.Character);
                Assert.Equal(17, malformed.RepairRange.End.Character);
            });
    }
}
