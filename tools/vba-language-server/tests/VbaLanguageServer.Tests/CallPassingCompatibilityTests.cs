using VbaLanguageServer.SourceModel;
using VbaTools.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class CallPassingCompatibilityTests
{
    [Fact]
    public void SourceByRefVariantParameterAcceptsDirectStringStorage()
    {
        const string source = """
            Attribute VB_Name = "Caller"
            Public Sub Run()
                Dim text As String
                AcceptValue text
            End Sub
            Public Sub AcceptValue(ByRef value As Variant)
            End Sub
            """;
        AssertSourceCallCompatibility(source);
    }

    [Fact]
    public void SourceByRefObjectParameterAcceptsDirectConcreteClassStorage()
    {
        const string source = """
            Attribute VB_Name = "Caller"
            Public Sub Run()
                Dim item As Payload
                AcceptValue item
            End Sub
            Public Sub AcceptValue(ByRef value As Object)
            End Sub
            """;
        AssertSourceCallCompatibility(source, new Dictionary<string, string>
        {
            ["file:///C:/work/Payload.cls"] = """
                VERSION 1.0 CLASS
                Attribute VB_Name = "Payload"
                """
        });
    }

    [Fact]
    public void SourceByRefInterfaceParameterAcceptsAnImplementingConcreteClass()
    {
        const string source = """
            Attribute VB_Name = "Caller"
            Public Sub Run()
                Dim item As Payload
                AcceptValue item
            End Sub
            Public Sub AcceptValue(ByRef value As IValue)
            End Sub
            """;
        AssertSourceCallCompatibility(source, new Dictionary<string, string>
        {
            ["file:///C:/work/IValue.cls"] = """
                VERSION 1.0 CLASS
                Attribute VB_Name = "IValue"
                Public Sub Work()
                End Sub
                """,
            ["file:///C:/work/Payload.cls"] = """
                VERSION 1.0 CLASS
                Attribute VB_Name = "Payload"
                Implements IValue
                Private Sub IValue_Work()
                End Sub
                """
        });
    }

    [Theory]
    [InlineData("Object")]
    [InlineData("Payload")]
    public void SourceByRefObjectParametersRejectDirectVariantStorage(string parameterType)
    {
        var source = $$"""
            Attribute VB_Name = "Caller"
            Public Sub Run()
                Dim item As Variant
                AcceptValue item
            End Sub
            Public Sub AcceptValue(ByRef value As {{parameterType}})
            End Sub
            """;
        AssertSourceCallCompatibility(source, new Dictionary<string, string>
        {
            ["file:///C:/work/Payload.cls"] = """
                VERSION 1.0 CLASS
                Attribute VB_Name = "Payload"
                """
        }, VbaCallCompatibilityState.Inapplicable);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SourceByRefVariantDistinguishesAWholeArrayFromAnArrayTypedParameter(
        bool parameterIsArray, bool isApplicable)
    {
        var source = $$"""
            Attribute VB_Name = "Caller"
            Public Sub Run()
                Dim items() As Long
                AcceptValue items
            End Sub
            Public Sub AcceptValue(ByRef value{{(parameterIsArray ? "()" : "")}} As Variant)
            End Sub
            """;
        AssertSourceCallCompatibility(source, expectedState: isApplicable
            ? VbaCallCompatibilityState.Applicable : VbaCallCompatibilityState.Inapplicable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceByRefLongRejectsIntegerStorageUnlessTheArgumentIsAValueTemporary(
        bool parenthesized)
    {
        var source = $$"""
            Attribute VB_Name = "Caller"
            Public Sub Run()
                Dim item As Integer
                AcceptValue {{(parenthesized ? "(item)" : "item")}}
            End Sub
            Public Sub AcceptValue(ByRef value As Long)
            End Sub
            """;
        AssertSourceCallCompatibility(source, expectedState: parenthesized
            ? VbaCallCompatibilityState.Applicable : VbaCallCompatibilityState.Inapplicable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceByRefClassDoesNotInferAnUnprovedOrConditionalInterfaceAssignment(
        bool conditionalImplementation)
    {
        const string source = """
            Attribute VB_Name = "Caller"
            Public Sub Run()
                Dim item As Payload
                AcceptValue item
            End Sub
            Public Sub AcceptValue(ByRef value As IValue)
            End Sub
            """;
        AssertSourceCallCompatibility(source, new Dictionary<string, string>
        {
            ["file:///C:/work/IValue.cls"] = """
                VERSION 1.0 CLASS
                Attribute VB_Name = "IValue"
                Public Sub Work()
                End Sub
                """,
            ["file:///C:/work/Payload.cls"] = """
                VERSION 1.0 CLASS
                Attribute VB_Name = "Payload"
                """ + (conditionalImplementation ? """

                #If VBA7 Then
                Implements IValue
                Private Sub IValue_Work()
                End Sub
                #End If
                """ : "")
        }, VbaCallCompatibilityState.Indeterminate);
    }

    private static void AssertSourceCallCompatibility(
        string source,
        IReadOnlyDictionary<string, string>? additionalSources = null,
        VbaCallCompatibilityState expectedState = VbaCallCompatibilityState.Applicable)
    {
        const string uri = "file:///C:/work/Caller.bas";
        var syntaxTree = VbaSyntaxTree.ParseModule(uri, source);
        var document = VbaSourceDocumentProjector.Project(uri, syntaxTree);
        var catalogs = VbaProjectReferenceCatalogSet.Empty;
        var documents = new Dictionary<string, VbaSourceDocument>(StringComparer.OrdinalIgnoreCase)
        {
            [uri] = document
        };
        foreach (var pair in additionalSources ?? new Dictionary<string, string>())
        {
            documents.Add(pair.Key, VbaSourceDocumentProjector.Project(
                pair.Key, VbaSyntaxTree.ParseModule(pair.Key, pair.Value)));
        }
        var inventory = VbaSemanticInventory.Create(documents, referenceSelection: null, catalogs);
        var target = Assert.IsAssignableFrom<VbaResolvedNameTarget>(
            inventory.ResolveSourceTarget(uri, 3, "    ".Length));
        Assert.Equal("AcceptValue", target.CanonicalName);
        Assert.Equal(VbaDefinitionOrigin.Source, target.SelectedDefinition.Identity.Origin);
        var nameResolution = new VbaNameResolutionService(
            documents.Values.ToArray(), referenceSelection: null, catalogs);
        var callResolution = new VbaCallSiteResolution(
            nameResolution,
            new VbaMemberChainResolution(new VbaTypeResolution(nameResolution)),
            new VbaResolutionPolicy());
        var call = Assert.Single(syntaxTree.Module.ArgumentLists, candidate =>
            candidate.CalleeRange?.Start.Line == 3
            && candidate.Form == VbaCallSyntaxForm.Statement);

        var compatibility = callResolution.AnalyzeCompleteCall(document, call, target);

        var variant = Assert.Single(compatibility.Variants);
        Assert.Equal(expectedState, variant.State);
        var mapping = Assert.IsType<VbaCompleteCallArgumentMapping>(variant.Mapping);
        if (expectedState == VbaCallCompatibilityState.Inapplicable)
        {
            Assert.NotEmpty(mapping.TypeMismatchReasons);
            Assert.Contains(inventory.GetProjectValidationDiagnostics(uri), diagnostic =>
                diagnostic.Code == "validation.incompatibleCallArgumentList");
        }
        else
        {
            Assert.Empty(mapping.TypeMismatchReasons);
            Assert.DoesNotContain(inventory.GetProjectValidationDiagnostics(uri), diagnostic =>
                diagnostic.Code == "validation.incompatibleCallArgumentList"
                || diagnostic.Message.Contains("ByRef type", StringComparison.Ordinal));
        }
    }
}
