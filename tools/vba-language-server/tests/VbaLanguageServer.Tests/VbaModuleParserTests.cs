using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Parsing;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaModuleParserTests
{
    [Fact]
    public void ParserReadsModuleClassAndFormIdentityFromAttributeOrFileName()
    {
        var standardModule = VbaModuleParser.Parse(
            "file:///C:/work/Worker.bas",
            "Attribute VB_Name = \"WorkerModule\"\nOption Explicit\n");
        var classModule = VbaModuleParser.Parse(
            "file:///C:/work/Customer.cls",
            "VERSION 1.0 CLASS\nAttribute VB_Name = \"CustomerRecord\"\nOption Explicit\n");
        var formModule = VbaModuleParser.Parse(
            "file:///C:/work/Dialog.frm",
            string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "  Caption = \"Designer caption\"",
                "End",
                "Attribute VB_Name = \"DialogView\"",
                "Option Explicit"
            ]));
        var fallback = VbaModuleParser.Parse(
            "file:///C:/work/FallbackName.bas",
            "Option Explicit\n");

        Assert.Equal("WorkerModule", standardModule.Identity.Name);
        Assert.Equal(VbaSourceDefinitionKind.Module, standardModule.Identity.Kind);
        Assert.Equal("CustomerRecord", classModule.Identity.Name);
        Assert.Equal(VbaSourceDefinitionKind.Class, classModule.Identity.Kind);
        Assert.Equal("DialogView", formModule.Identity.Name);
        Assert.Equal(VbaSourceDefinitionKind.Form, formModule.Identity.Kind);
        Assert.Equal("FallbackName", fallback.Identity.Name);
    }

    [Fact]
    public void ParserReadsCallableDeclarationsAndFailsClosedForMalformedHeaders()
    {
        var module = VbaModuleParser.Parse(
            "file:///C:/work/Worker.bas",
            string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "'* Reads a value.",
                "'* @param Key lookup key",
                "Public Function ReadValue(ByVal Key As String) As String",
                "End Function",
                "Private Sub SaveValue()",
                "End Sub",
                "Friend Property Get DisplayName() As String",
                "End Property",
                "Public Function () As String",
                "End Function"
            ]));

        Assert.Collection(
            module.CallableDeclarations,
            readValue =>
            {
                Assert.Equal("ReadValue", readValue.Name);
                Assert.Equal(VbaSourceDefinitionKind.Procedure, readValue.Kind);
                Assert.Equal(VbaSourceDefinitionVisibility.Public, readValue.Visibility);
                Assert.Equal(new VbaRange(new VbaPosition(3, "Public Function ".Length), new VbaPosition(3, "Public Function ReadValue".Length)), readValue.Range);
                Assert.Equal("ReadValue(Key) As String", readValue.Signature.Label);
                var parameter = Assert.Single(readValue.Signature.Parameters);
                Assert.Equal("Key", parameter.Name);
                Assert.Equal("lookup key", parameter.Documentation);
            },
            saveValue =>
            {
                Assert.Equal("SaveValue", saveValue.Name);
                Assert.Equal(VbaSourceDefinitionVisibility.Private, saveValue.Visibility);
            },
            displayName =>
            {
                Assert.Equal("DisplayName", displayName.Name);
                Assert.Equal(VbaSourceDefinitionKind.Property, displayName.Kind);
            });
    }

    [Fact]
    public void ParserReadsClassAndFormCallableDeclarationsAfterExportHeaders()
    {
        var classModule = VbaModuleParser.Parse(
            "file:///C:/work/Customer.cls",
            string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Customer\"",
                "Option Explicit",
                "Public Property Get DisplayName() As String",
                "End Property",
                "Private Sub Class_Initialize()",
                "End Sub"
            ]));
        var formModule = VbaModuleParser.Parse(
            "file:///C:/work/Dialog.frm",
            string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "  Caption = \"Designer caption\"",
                "End",
                "Attribute VB_Name = \"Dialog\"",
                "Option Explicit",
                "Private Sub CommandButton1_Click()",
                "End Sub"
            ]));

        Assert.Collection(
            classModule.CallableDeclarations,
            displayName =>
            {
                Assert.Equal("DisplayName", displayName.Name);
                Assert.Equal(VbaSourceDefinitionKind.Property, displayName.Kind);
                Assert.Equal(VbaSourceDefinitionVisibility.Public, displayName.Visibility);
                Assert.Equal(3, displayName.LineIndex);
            },
            initialize =>
            {
                Assert.Equal("Class_Initialize", initialize.Name);
                Assert.Equal(VbaSourceDefinitionKind.Procedure, initialize.Kind);
                Assert.Equal(VbaSourceDefinitionVisibility.Private, initialize.Visibility);
                Assert.Equal(5, initialize.LineIndex);
            });
        var formCallable = Assert.Single(formModule.CallableDeclarations);
        Assert.Equal("CommandButton1_Click", formCallable.Name);
        Assert.Equal(VbaSourceDefinitionKind.Procedure, formCallable.Kind);
        Assert.Equal(VbaSourceDefinitionVisibility.Private, formCallable.Visibility);
        Assert.Equal(6, formCallable.LineIndex);
    }
}
