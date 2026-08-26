using VbaDev.App.HostClasses;
using VbaDev.Infrastructure.Workbooks;
using System.Runtime.InteropServices;
using Xunit;

namespace VbaDev.Tests;

public sealed class HostClassGeneratedSignatureTests
{
    [Fact]
    public void UnsignedFourByteComTypeRemainsOpaqueEvidence()
    {
        var type = HostClassTypeLibEventSurfaceReader.CreatePrimitiveTypeReference(
            VarEnum.VT_UI4);

        var unresolved = Assert.IsType<UnresolvedHostEventTypeReference>(type);
        Assert.Equal("VT_UI4", unresolved.DisplayName);
    }

    [Theory]
    [InlineData(VarEnum.VT_I1, "VT_I1")]
    [InlineData(VarEnum.VT_UI2, "VT_UI2")]
    [InlineData(VarEnum.VT_UINT, "VT_UINT")]
    [InlineData(VarEnum.VT_UI8, "VT_UI8")]
    public void ComIntegerTypesWithoutAnExactVbaCanonicalTypeRemainOpaque(
        VarEnum variableType,
        string expectedDisplayName)
    {
        var type = HostClassTypeLibEventSurfaceReader.CreatePrimitiveTypeReference(
            variableType);

        var unresolved = Assert.IsType<UnresolvedHostEventTypeReference>(type);
        Assert.Equal(expectedDisplayName, unresolved.DisplayName);
    }

    [Fact]
    public void ParsesTheCanonicalUserFormQueryCloseSignature()
    {
        const string generated =
            "Private Sub UserForm_QueryClose(Cancel As Integer, CloseMode As Integer)\r\n" +
            "End Sub\r\n";

        var signature = HostClassGeneratedSignatureParser.Parse(
            "QueryClose",
            "UserForm_QueryClose",
            generated,
            authoringAvailable: true,
            existingHandlerRecognizable: true);

        Assert.Equal("QueryClose", signature.Name);
        Assert.Null(signature.Documentation);
        Assert.True(signature.AuthoringAvailable);
        Assert.True(signature.ExistingHandlerRecognizable);
        Assert.Equal(2, signature.Parameters.Count);
        Assert.Collection(
            signature.Parameters,
            cancel => AssertParameter(cancel, "Cancel"),
            closeMode => AssertParameter(closeMode, "CloseMode"));
    }

    [Fact]
    public void PreservesAnExactCodePageEventName()
    {
        var signature = HostClassGeneratedSignatureParser.Parse(
            "\u00A0",
            "Host_\u00A0",
            "Private Sub Host_\u00A0()\r\nEnd Sub\r\n",
            authoringAvailable: true,
            existingHandlerRecognizable: true);

        Assert.Equal("\u00A0", signature.Name);
    }

    [Fact]
    public void PreservesAnExactCodePageTypeQualifier()
    {
        var signature = HostClassGeneratedSignatureParser.Parse(
            "Changed",
            "Host_Changed",
            "Private Sub Host_Changed(ByVal value As \u00A0.Widget)\r\nEnd Sub\r\n",
            authoringAvailable: true,
            existingHandlerRecognizable: true);

        var parameter = Assert.Single(signature.Parameters);
        var type = Assert.IsType<UnresolvedHostEventTypeReference>(parameter.Type);
        Assert.Equal("\u00A0.Widget", type.DisplayName);
    }

    [Fact]
    public void TypeLibCallableTypeDisagreementRejectsTheCompleteObservation()
    {
        var generated = HostClassGeneratedSignatureParser.Parse(
            "BeforeClose",
            "Workbook_BeforeClose",
            "Private Sub Workbook_BeforeClose(Cancel As Boolean)\r\nEnd Sub\r\n",
            authoringAvailable: true,
            existingHandlerRecognizable: true);
        var surface = new HostClassTypeLibEventSurface(
            new HostClassBaseTypeProvenance("Workbook", Guid.NewGuid(), 1, 0, 0),
            new Dictionary<string, HostClassTypeLibEvent>(StringComparer.OrdinalIgnoreCase)
            {
                ["BeforeClose"] = new(
                    "BeforeClose",
                    [CreateTypeLibParameter(new IntrinsicHostEventTypeReference("Long")) with
                    {
                        Passing = HostEventPassingMechanism.ByRef
                    }],
                    null)
            });

        var error = Assert.Throws<HostClassEventObservationConflictException>(
            () => HostClassTypeLibEventEvidenceMerger.Merge(generated, surface));

        Assert.Equal(HostClassInspectionFailureReason.EventEnumerationFailure, error.Reason);
        Assert.Contains("callable contract", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TypeLibParameterCountDisagreementRejectsTheCompleteObservation()
    {
        var generated = HostClassGeneratedSignatureParser.Parse(
            "BeforeClose",
            "Workbook_BeforeClose",
            "Private Sub Workbook_BeforeClose(Cancel As Boolean)\r\nEnd Sub\r\n",
            authoringAvailable: true,
            existingHandlerRecognizable: false);
        var surface = new HostClassTypeLibEventSurface(
            new HostClassBaseTypeProvenance("Workbook", Guid.NewGuid(), 1, 0, 0),
            new Dictionary<string, HostClassTypeLibEvent>(StringComparer.OrdinalIgnoreCase)
            {
                ["BeforeClose"] = new("BeforeClose", [], "TypeLib documentation")
            });

        var error = Assert.Throws<HostClassEventObservationConflictException>(
            () => HostClassTypeLibEventEvidenceMerger.Merge(generated, surface));

        Assert.Equal(HostClassInspectionFailureReason.EventEnumerationFailure, error.Reason);
        Assert.Contains("callable contract", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingTypeLibMetadataProducesEmptySupplementalEvidence()
    {
        var found = HostClassTypeLibEventSurfaceReader.TryRead(
            new object(),
            out var surface);

        Assert.False(found);
        Assert.Null(surface.BaseType);
        Assert.Empty(surface.Events);
    }

    [Fact]
    public void NameMismatchedOpaqueAndTypeLibEvidenceRejectsTheCompleteObservation()
    {
        var generated = HostClassGeneratedSignatureParser.Parse(
            "Change",
            "Worksheet_Change",
            "Private Sub Worksheet_Change(ByVal Target As MysteryWidget)\r\nEnd Sub\r\n",
            authoringAvailable: true,
            existingHandlerRecognizable: true);
        var surface = new HostClassTypeLibEventSurface(
            null,
            new Dictionary<string, HostClassTypeLibEvent>(StringComparer.OrdinalIgnoreCase)
            {
                ["Change"] = new(
                    "Change",
                    [CreateTypeLibParameter(
                        new TypeLibHostEventTypeReference(
                            "Range",
                            new Guid("00020813-0000-0000-c000-000000000046"),
                            1,
                            9,
                            0))],
                    null)
            });

        var error = Assert.Throws<HostClassEventObservationConflictException>(
            () => HostClassTypeLibEventEvidenceMerger.Merge(generated, surface));

        Assert.Equal(HostClassInspectionFailureReason.EventEnumerationFailure, error.Reason);
        Assert.Contains("callable contract", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TypeLibDocumentationDoesNotReplaceGeneratedDocumentation()
    {
        var generated = new HostEventSignature(
            "Change",
            [],
            "Generated declaration documentation.",
            AuthoringAvailable: true,
            ExistingHandlerRecognizable: true);
        var surface = new HostClassTypeLibEventSurface(
            null,
            new Dictionary<string, HostClassTypeLibEvent>(StringComparer.OrdinalIgnoreCase)
            {
                ["Change"] = new(
                    "Change",
                    [],
                    "Supplemental TypeLib documentation.")
            });

        var merged = HostClassTypeLibEventEvidenceMerger.Merge(generated, surface);

        Assert.Equal("Generated declaration documentation.", merged.Documentation);
    }

    [Fact]
    public void OneTypeLibParameterMismatchRejectsTheCompleteSupplementalObservation()
    {
        var generated = HostClassGeneratedSignatureParser.Parse(
            "Change",
            "Worksheet_Change",
            "Private Sub Worksheet_Change(ByVal Target As Range, ByVal Context As MysteryWidget)\r\n" +
            "End Sub\r\n",
            authoringAvailable: true,
            existingHandlerRecognizable: true);
        var surface = new HostClassTypeLibEventSurface(
            null,
            new Dictionary<string, HostClassTypeLibEvent>(StringComparer.OrdinalIgnoreCase)
            {
                ["Change"] = new(
                    "Change",
                    [
                        CreateTypeLibParameter(
                            new TypeLibHostEventTypeReference(
                                "Range",
                                new Guid("00020813-0000-0000-c000-000000000046"),
                                1,
                                9,
                                0)),
                        CreateTypeLibParameter(
                            new TypeLibHostEventTypeReference(
                                "DifferentWidget",
                                Guid.NewGuid(),
                                1,
                                0,
                                0))
                    ],
                    "Supplemental TypeLib documentation.")
            });

        var error = Assert.Throws<HostClassEventObservationConflictException>(
            () => HostClassTypeLibEventEvidenceMerger.Merge(generated, surface));

        Assert.Equal(HostClassInspectionFailureReason.EventEnumerationFailure, error.Reason);
        Assert.Contains("callable contract", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertParameter(HostEventParameter parameter, string expectedName)
    {
        Assert.Equal(expectedName, parameter.Name);
        var type = Assert.IsType<IntrinsicHostEventTypeReference>(parameter.Type);
        Assert.Equal("Integer", type.Name);
        Assert.Equal(HostEventPassingMechanism.ByRef, parameter.Passing);
        Assert.Equal(HostEventArrayShape.Scalar, parameter.ArrayShape);
        Assert.False(parameter.Optional);
        Assert.False(parameter.ParamArray);
    }

    private static HostEventParameter CreateTypeLibParameter(
        HostEventTypeReference type)
        => new(
            "Parameter",
            type,
            HostEventPassingMechanism.ByVal,
            HostEventArrayShape.Scalar,
            Optional: false,
            ParamArray: false);
}
