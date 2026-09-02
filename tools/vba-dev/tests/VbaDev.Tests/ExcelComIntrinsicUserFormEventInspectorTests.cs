using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class ExcelComIntrinsicUserFormEventInspectorTests
{
    [Fact]
    public void AcceptsAnExactCodePageIntrinsicSourceName()
    {
        Assert.True(ExcelComIntrinsicUserFormEventInspector.IsIntrinsicSourceName("\u00A0"));
    }

    [Fact]
    public void AcceptsAReservedLexIdentifierFromTheVbeEventList()
    {
        Assert.True(ExcelComIntrinsicUserFormEventInspector.IsAuthoringEventName("CDecl"));
    }

    [Fact]
    public void AuthorsOnlyEventsThatComposeAnExactVbaProcedureIdentifier()
    {
        Assert.True(ExcelComIntrinsicUserFormEventInspector.CanAuthorEvent("Worksheet", "Open"));
        Assert.False(ExcelComIntrinsicUserFormEventInspector.CanAuthorEvent("Worksheet", "Before-Open"));
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
            ExcelComIntrinsicUserFormEventInspector.RenderTypeLibProbeTypeName(name));
    }
}
