using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class ExcelComIntrinsicHostClassInspectorTests
{
    [Fact]
    public void AcceptsAnExactCodePageIntrinsicSourceName()
    {
        Assert.True(ExcelComIntrinsicHostClassInspector.IsIntrinsicSourceName("\u00A0"));
    }

    [Fact]
    public void RetainsAnExactCodePageControlName()
    {
        Assert.True(ExcelComIntrinsicHostClassInspector.IsObservedControlName("\u00A0"));
    }

    [Theory]
    [InlineData("CDecl")]
    [InlineData("Run$")]
    [InlineData("\u3000")]
    public void RejectsObservedControlNamesThatAreNotExactVbaIdentifiers(string name)
    {
        Assert.False(ExcelComIntrinsicHostClassInspector.IsObservedControlName(name));
    }

    [Fact]
    public void AcceptsAReservedLexIdentifierFromTheVbeEventList()
    {
        Assert.True(ExcelComIntrinsicHostClassInspector.IsAuthoringEventName("CDecl"));
    }

    [Fact]
    public void AuthorsOnlyEventsThatComposeAnExactVbaProcedureIdentifier()
    {
        Assert.True(ExcelComIntrinsicHostClassInspector.CanAuthorEvent("Worksheet", "Open"));
        Assert.False(ExcelComIntrinsicHostClassInspector.CanAuthorEvent("Worksheet", "Before-Open"));
    }

    [Theory]
    [InlineData("Widget", "Widget")]
    [InlineData("String", "[String]")]
    [InlineData("Widget-2", "[Widget-2]")]
    public void TypeLibraryProbeTypesUseUnrestrictedNameSyntax(
        string name,
        string expected)
    {
        Assert.Equal(
            expected,
            ExcelComIntrinsicHostClassInspector.RenderTypeLibProbeTypeName(name));
    }
}
