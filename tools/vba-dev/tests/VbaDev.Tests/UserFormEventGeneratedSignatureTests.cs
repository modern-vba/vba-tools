using VbaDev.Infrastructure.Workbooks;
using System.Runtime.InteropServices;
using Xunit;

namespace VbaDev.Tests;

public sealed class UserFormEventGeneratedSignatureTests
{
    [Fact]
    public void UnsignedFourByteComTypeRemainsOpaqueEvidence()
    {
        var type = UserFormEventTypeLibSurfaceReader.CreatePrimitiveTypeReference(
            VarEnum.VT_UI4);

        var unresolved = Assert.IsType<ObservedUnresolvedHostEventTypeReference>(type);
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
        var type = UserFormEventTypeLibSurfaceReader.CreatePrimitiveTypeReference(
            variableType);

        var unresolved = Assert.IsType<ObservedUnresolvedHostEventTypeReference>(type);
        Assert.Equal(expectedDisplayName, unresolved.DisplayName);
    }

    [Fact]
    public void ParsesTheCanonicalUserFormQueryCloseSignature()
    {
        const string generated =
            "Private Sub UserForm_QueryClose(Cancel As Integer, CloseMode As Integer)\r\n" +
            "End Sub\r\n";

        var signature = UserFormEventGeneratedSignatureParser.Parse(
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
        var signature = UserFormEventGeneratedSignatureParser.Parse(
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
        var signature = UserFormEventGeneratedSignatureParser.Parse(
            "Changed",
            "Host_Changed",
            "Private Sub Host_Changed(ByVal value As \u00A0.Widget)\r\nEnd Sub\r\n",
            authoringAvailable: true,
            existingHandlerRecognizable: true);

        var parameter = Assert.Single(signature.Parameters);
        var type = Assert.IsType<ObservedUnresolvedHostEventTypeReference>(parameter.Type);
        Assert.Equal("\u00A0.Widget", type.DisplayName);
    }

    [Fact]
    public void TypeLibCallableTypeDisagreementRejectsTheCompleteObservation()
    {
        var generated = UserFormEventGeneratedSignatureParser.Parse(
            "BeforeClose",
            "Workbook_BeforeClose",
            "Private Sub Workbook_BeforeClose(Cancel As Boolean)\r\nEnd Sub\r\n",
            authoringAvailable: true,
            existingHandlerRecognizable: true);
        var surface = new UserFormEventTypeLibSurface(
            new UserFormEventBaseTypeProvenance("Workbook", Guid.NewGuid(), 1, 0, 0),
            new Dictionary<string, UserFormTypeLibEvent>(StringComparer.OrdinalIgnoreCase)
            {
                ["BeforeClose"] = new(
                    "BeforeClose",
                    [CreateTypeLibParameter(new ObservedIntrinsicHostEventTypeReference("Long")) with
                    {
                        Passing = ObservedHostEventPassingMechanism.ByRef
                    }],
                    null)
            });

        var error = Assert.Throws<UserFormEventObservationConflictException>(
            () => UserFormEventEvidenceMerger.Merge(generated, surface));

        Assert.Equal(UserFormEventInspectionFailureReason.EventEnumerationFailure, error.Reason);
        Assert.Contains("callable contract", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TypeLibParameterCountDisagreementRejectsTheCompleteObservation()
    {
        var generated = UserFormEventGeneratedSignatureParser.Parse(
            "BeforeClose",
            "Workbook_BeforeClose",
            "Private Sub Workbook_BeforeClose(Cancel As Boolean)\r\nEnd Sub\r\n",
            authoringAvailable: true,
            existingHandlerRecognizable: false);
        var surface = new UserFormEventTypeLibSurface(
            new UserFormEventBaseTypeProvenance("Workbook", Guid.NewGuid(), 1, 0, 0),
            new Dictionary<string, UserFormTypeLibEvent>(StringComparer.OrdinalIgnoreCase)
            {
                ["BeforeClose"] = new("BeforeClose", [], "TypeLib documentation")
            });

        var error = Assert.Throws<UserFormEventObservationConflictException>(
            () => UserFormEventEvidenceMerger.Merge(generated, surface));

        Assert.Equal(UserFormEventInspectionFailureReason.EventEnumerationFailure, error.Reason);
        Assert.Contains("callable contract", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingTypeLibMetadataProducesEmptySupplementalEvidence()
    {
        var found = UserFormEventTypeLibSurfaceReader.TryRead(
            new object(),
            out var surface);

        Assert.False(found);
        Assert.Null(surface.BaseType);
        Assert.Empty(surface.Events);
    }

    [Fact]
    public void NameMismatchedOpaqueAndTypeLibEvidenceRejectsTheCompleteObservation()
    {
        var generated = UserFormEventGeneratedSignatureParser.Parse(
            "Change",
            "Worksheet_Change",
            "Private Sub Worksheet_Change(ByVal Target As MysteryWidget)\r\nEnd Sub\r\n",
            authoringAvailable: true,
            existingHandlerRecognizable: true);
        var surface = new UserFormEventTypeLibSurface(
            null,
            new Dictionary<string, UserFormTypeLibEvent>(StringComparer.OrdinalIgnoreCase)
            {
                ["Change"] = new(
                    "Change",
                    [CreateTypeLibParameter(
                        new ObservedTypeLibHostEventTypeReference(
                            "Range",
                            new Guid("00020813-0000-0000-c000-000000000046"),
                            1,
                            9,
                            0))],
                    null)
            });

        var error = Assert.Throws<UserFormEventObservationConflictException>(
            () => UserFormEventEvidenceMerger.Merge(generated, surface));

        Assert.Equal(UserFormEventInspectionFailureReason.EventEnumerationFailure, error.Reason);
        Assert.Contains("callable contract", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TypeLibDocumentationDoesNotReplaceGeneratedDocumentation()
    {
        var generated = new UserFormEventObservation(
            "Change",
            [],
            "Generated declaration documentation.",
            AuthoringAvailable: true,
            ExistingHandlerRecognizable: true);
        var surface = new UserFormEventTypeLibSurface(
            null,
            new Dictionary<string, UserFormTypeLibEvent>(StringComparer.OrdinalIgnoreCase)
            {
                ["Change"] = new(
                    "Change",
                    [],
                    "Supplemental TypeLib documentation.")
            });

        var merged = UserFormEventEvidenceMerger.Merge(generated, surface);

        Assert.Equal("Generated declaration documentation.", merged.Documentation);
    }

    [Fact]
    public void OneTypeLibParameterMismatchRejectsTheCompleteSupplementalObservation()
    {
        var generated = UserFormEventGeneratedSignatureParser.Parse(
            "Change",
            "Worksheet_Change",
            "Private Sub Worksheet_Change(ByVal Target As Range, ByVal Context As MysteryWidget)\r\n" +
            "End Sub\r\n",
            authoringAvailable: true,
            existingHandlerRecognizable: true);
        var surface = new UserFormEventTypeLibSurface(
            null,
            new Dictionary<string, UserFormTypeLibEvent>(StringComparer.OrdinalIgnoreCase)
            {
                ["Change"] = new(
                    "Change",
                    [
                        CreateTypeLibParameter(
                            new ObservedTypeLibHostEventTypeReference(
                                "Range",
                                new Guid("00020813-0000-0000-c000-000000000046"),
                                1,
                                9,
                                0)),
                        CreateTypeLibParameter(
                            new ObservedTypeLibHostEventTypeReference(
                                "DifferentWidget",
                                Guid.NewGuid(),
                                1,
                                0,
                                0))
                    ],
                    "Supplemental TypeLib documentation.")
            });

        var error = Assert.Throws<UserFormEventObservationConflictException>(
            () => UserFormEventEvidenceMerger.Merge(generated, surface));

        Assert.Equal(UserFormEventInspectionFailureReason.EventEnumerationFailure, error.Reason);
        Assert.Contains("callable contract", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertParameter(ObservedHostEventParameter parameter, string expectedName)
    {
        Assert.Equal(expectedName, parameter.Name);
        var type = Assert.IsType<ObservedIntrinsicHostEventTypeReference>(parameter.Type);
        Assert.Equal("Integer", type.Name);
        Assert.Equal(ObservedHostEventPassingMechanism.ByRef, parameter.Passing);
        Assert.Equal(ObservedHostEventArrayShape.Scalar, parameter.ArrayShape);
        Assert.False(parameter.Optional);
        Assert.False(parameter.ParamArray);
    }

    private static ObservedHostEventParameter CreateTypeLibParameter(
        ObservedHostEventTypeReference type)
        => new(
            "Parameter",
            type,
            ObservedHostEventPassingMechanism.ByVal,
            ObservedHostEventArrayShape.Scalar,
            Optional: false,
            ParamArray: false);
}
