using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaCallArgumentAvailabilityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PositionalArgumentConsumesItsOrdinalAndLeavesLaterNames(bool parenthesized)
    {
        var availability = ResolveSourceCall(parenthesized, "1, ");

        Assert.NotNull(availability.CallableDefinition);
        Assert.NotNull(availability.Signature);
        Assert.True(availability.Signature.SupportsNamedArguments);
        Assert.True(availability.AllowsPositionalExpression);
        Assert.Equal(["Arg2", "Arg3"], RemainingNames(availability));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OptionalOmissionConsumesItsOrdinal(bool parenthesized)
    {
        var availability = ResolveSourceCall(parenthesized, "1, , ");

        Assert.True(availability.AllowsPositionalExpression);
        Assert.Equal(["Arg3"], RemainingNames(availability));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RequiredOmissionInvalidatesRemainingArguments(bool parenthesized)
    {
        var availability = ResolveSourceCall(parenthesized, ", ");

        AssertInvalid(availability);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NamedArgumentMatchingIsCaseInsensitiveAndDisablesLaterPositionals(bool parenthesized)
    {
        var availability = ResolveSourceCall(parenthesized, "aRg2:=True, ");

        Assert.False(availability.AllowsPositionalExpression);
        Assert.Equal(["Arg1", "Arg3"], RemainingNames(availability));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnknownNamedArgumentInvalidatesTheSequence(bool parenthesized)
    {
        var availability = ResolveSourceCall(parenthesized, "Missing:=True, ");

        AssertInvalid(availability);
    }

    [Theory]
    [InlineData(true, "Arg2:=True, arg2:=False, ")]
    [InlineData(false, "Arg2:=True, arg2:=False, ")]
    [InlineData(true, "1, Arg1:=2, ")]
    [InlineData(false, "1, Arg1:=2, ")]
    public void DuplicateNamedOrPositionalAndNamedArgumentsInvalidateTheSequence(
        bool parenthesized,
        string arguments)
    {
        var availability = ResolveSourceCall(parenthesized, arguments);

        AssertInvalid(availability);
    }

    [Theory]
    [InlineData(true, "Arg2:=True, 1, ")]
    [InlineData(false, "Arg2:=True, 1, ")]
    [InlineData(true, "Arg2:=True, , ")]
    [InlineData(false, "Arg2:=True, , ")]
    public void PositionalOrOmittedArgumentAfterNamedArgumentInvalidatesTheSequence(
        bool parenthesized,
        string arguments)
    {
        var availability = ResolveSourceCall(parenthesized, arguments);

        AssertInvalid(availability);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExhaustedArityDisablesPositionalAndNamedArguments(bool parenthesized)
    {
        var availability = ResolveSourceCall(parenthesized, "1, True, False, ");

        Assert.False(availability.AllowsPositionalExpression);
        Assert.Empty(availability.RemainingNamedParameters);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ParamArrayAbsorbsFurtherPositionalsAndIsNeverNamed(bool parenthesized)
    {
        var initial = ResolveCall(CreateCallLine(parenthesized, "Collect", ""));
        var availability = ResolveCall(CreateCallLine(parenthesized, "Collect", "\"prefix\", 1, 2, "));

        Assert.Equal(["Prefix"], RemainingNames(initial));
        Assert.True(availability.AllowsPositionalExpression);
        Assert.Empty(availability.RemainingNamedParameters);
    }

    [Theory]
    [InlineData(true, "\"prefix\", , ")]
    [InlineData(false, "\"prefix\", , ")]
    [InlineData(true, "Values:=1, ")]
    [InlineData(false, "Values:=1, ")]
    public void OmittedOrNamedParamArrayInvalidatesTheSequence(
        bool parenthesized,
        string arguments)
    {
        var availability = ResolveCall(CreateCallLine(parenthesized, "Collect", arguments));

        AssertInvalid(availability);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnresolvedCallableAllowsExpressionsOnlyBeforeANamedArgument(bool parenthesized)
    {
        var initial = ResolveCall(CreateCallLine(parenthesized, "Missing", ""));
        var afterNamed = ResolveCall(CreateCallLine(parenthesized, "Missing", "Arg1:=1, "));

        Assert.Null(initial.CallableDefinition);
        Assert.Null(initial.Signature);
        Assert.True(initial.AllowsPositionalExpression);
        Assert.Empty(initial.RemainingNamedParameters);
        AssertInvalid(afterNamed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActiveNamedArgumentIsNotConsumedBecauseNamedValueCompletionUsesExpressions(
        bool parenthesized)
    {
        var availability = ResolveSourceCall(parenthesized, "Arg3:=");

        Assert.True(availability.AllowsPositionalExpression);
        Assert.Equal(["Arg1", "Arg2", "Arg3"], RemainingNames(availability));
    }

    [Fact]
    public void ResolvedDefinitionWithoutSignatureFailsClosed()
    {
        var availability = ResolveCall("    result = Data(");

        Assert.Equal("Data", availability.CallableDefinition?.Name);
        Assert.Null(availability.Signature);
        AssertInvalid(availability);
    }

    [Fact]
    public void RaiseEventAllowsValuesButNeverOffersNamedArguments()
    {
        var availability = ResolveCall("    RaiseEvent Saved(1, ");

        Assert.Equal(VbaSourceDefinitionKind.Event, availability.CallableDefinition?.Kind);
        Assert.True(availability.Signature?.SupportsNamedArguments);
        Assert.True(availability.AllowsPositionalExpression);
        Assert.Empty(availability.RemainingNamedParameters);
    }

    [Fact]
    public void ReferenceCallableNeverOffersNamedArgumentsWhenSupportIsUnknown()
    {
        const string referenceName = "Generated Library";
        var selection = VbaProjectReferenceSelection.Create(
            "word",
            [new VbaProjectReference(referenceName)]);
        var catalogs = VbaProjectReferenceCatalogSet.Empty.WithCatalog(
            new VbaProjectReferenceCatalog(
                referenceName,
                ["Generated"],
                [
                    new VbaProjectReferenceDefinition(
                        referenceName,
                        "GeneratedMethod",
                        VbaSourceDefinitionKind.Procedure,
                        Signature: new VbaCallableSignature(
                            "GeneratedMethod(Arg1, Arg2)",
                            [
                                new VbaCallableParameter("Arg1"),
                                new VbaCallableParameter("Arg2")
                            ],
                            CallableKind: VbaCallableKind.Function))
                ]));

        var availability = ResolveCall(
            "    result = GeneratedMethod(",
            selection,
            catalogs);

        Assert.Equal(VbaDefinitionOrigin.ProjectReference, availability.CallableDefinition?.Identity.Origin);
        Assert.True(availability.AllowsPositionalExpression);
        Assert.Empty(availability.RemainingNamedParameters);
    }

    [Fact]
    public void ReferenceCallableOffersNamedArgumentsWhenCatalogCapabilityIsKnown()
    {
        const string referenceName = "Generated Library";
        var selection = VbaProjectReferenceSelection.Create(
            "word",
            [new VbaProjectReference(referenceName)]);
        var catalogs = VbaProjectReferenceCatalogSet.Empty.WithCatalog(
            new VbaProjectReferenceCatalog(
                referenceName,
                ["Generated"],
                [
                    new VbaProjectReferenceDefinition(
                        referenceName,
                        "GeneratedMethod",
                        VbaSourceDefinitionKind.Procedure,
                        Signature: new VbaCallableSignature(
                            "GeneratedMethod(Arg1, Arg2)",
                            [
                                new VbaCallableParameter("Arg1"),
                                new VbaCallableParameter("Arg2")
                            ],
                            CallableKind: VbaCallableKind.Function,
                            SupportsNamedArguments: true))
                ]));

        var availability = ResolveCall(
            "    result = GeneratedMethod(1, ",
            selection,
            catalogs);

        Assert.Equal(VbaDefinitionOrigin.ProjectReference, availability.CallableDefinition?.Identity.Origin);
        Assert.True(availability.AllowsPositionalExpression);
        Assert.Equal(["Arg2"], RemainingNames(availability));
    }

    [Fact]
    public void BundledReferenceCallableOffersNamedArguments()
    {
        const string referenceName = "Visual Basic For Applications";
        var availability = ResolveCall(
            "    result = MsgBox(",
            VbaProjectReferenceSelection.Create(
                "word",
                [new VbaProjectReference(referenceName)]),
            VbaProjectReferenceCatalogSet.CreateBundled());

        Assert.True(availability.Signature?.SupportsNamedArguments);
        Assert.Equal(["Prompt", "Buttons", "Title"], RemainingNames(availability));
    }

    private static VbaCallArgumentAvailability ResolveSourceCall(
        bool parenthesized,
        string arguments)
        => ResolveCall(CreateCallLine(
            parenthesized,
            parenthesized ? "ExampleFunc" : "ExampleSub",
            arguments));

    private static string CreateCallLine(
        bool parenthesized,
        string callable,
        string arguments)
        => parenthesized
            ? $"    result = {callable}({arguments}"
            : $"    {callable} {arguments}";

    private static VbaCallArgumentAvailability ResolveCall(
        string callLine,
        VbaProjectReferenceSelection? referenceSelection = null,
        VbaProjectReferenceCatalogSet? referenceCatalogs = null)
    {
        const string uri = "file:///C:/work/Worker.cls";
        var lines = new[]
        {
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Public Function ExampleFunc(ByVal Arg1 As Long, Optional ByVal Arg2 As Boolean = False, Optional ByVal Arg3 As Boolean = False) As String",
            "End Function",
            "Public Sub ExampleSub(ByVal Arg1 As Long, Optional ByVal Arg2 As Boolean = False, Optional ByVal Arg3 As Boolean = False)",
            "End Sub",
            "Public Sub Collect(Optional ByVal Prefix As String, ParamArray Values() As Variant)",
            "End Sub",
            "Public Event Saved(ByVal Arg1 As Long, Optional ByVal Arg2 As Boolean)",
            "Private Data As Variant",
            "Public Sub Main()",
            callLine,
            "End Sub"
        };
        var text = string.Join('\n', lines);
        var callLineIndex = Array.IndexOf(lines, callLine);
        var syntaxTree = VbaSyntaxTree.ParseModule(uri, text);
        var document = VbaSourceIndex.CreateDocument(uri, syntaxTree);
        referenceCatalogs ??= VbaProjectReferenceCatalogSet.Empty;
        var nameResolution = new VbaNameResolutionService(
            [document],
            referenceSelection,
            referenceCatalogs);
        var typeResolution = new VbaTypeResolution(nameResolution);
        var callSiteResolution = new VbaCallSiteResolution(
            nameResolution,
            new VbaMemberChainResolution(typeResolution));
        var positionSyntax = syntaxTree.GetPositionSyntax(callLineIndex, callLine.Length);

        Assert.NotNull(positionSyntax.CallSite);
        return callSiteResolution.GetCallArgumentAvailability(
            document,
            callLineIndex,
            callLine.Length,
            positionSyntax);
    }

    private static string[] RemainingNames(VbaCallArgumentAvailability availability)
        => availability.RemainingNamedParameters
            .Select(parameter => parameter.Name)
            .ToArray();

    private static void AssertInvalid(VbaCallArgumentAvailability availability)
    {
        Assert.False(availability.AllowsPositionalExpression);
        Assert.Empty(availability.RemainingNamedParameters);
    }
}
