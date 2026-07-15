using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaDocumentationCommentProjectionTests
{
    [Fact]
    public void ParserPreservesInlineDocumentationCommandsAndHoverOrdering()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "'* @details Uses the configured fallback when the key is missing.",
                "'* @brief Reads a value.",
                "'* @param Key Key to read.",
                "'* @param Fallback Value used when the key is missing.",
                "'* @return The configured value.",
                "Public Function ReadValue(ByVal Key As String, Optional ByVal Fallback As String) As String",
                "End Function"
            ]));

        var function = Assert.Single(
            tree.Module.Declarations,
            declaration => declaration.Name == "ReadValue");
        Assert.Equal(
            "Reads a value.\n\n"
            + "Uses the configured fallback when the key is missing.\n\n"
            + "@param Key Key to read.\n\n"
            + "@param Fallback Value used when the key is missing.\n\n"
            + "@return The configured value.",
            function.Documentation);
        Assert.Equal(
            "Reads a value.\n\n"
            + "Uses the configured fallback when the key is missing.\n\n"
            + "@return The configured value.",
            function.Signature?.Documentation);
    }

    [Fact]
    public void ParserStructuresReturnsAliasAndStandaloneDetailsDocumentation()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Mod_Example.bas",
            string.Join('\n', [
                "Attribute VB_Name = \"Mod_Example\"",
                "Option Explicit",
                "",
                "Public Sub ExampleSub()",
                "    Dim example_var As String",
                "    example_var = ExampleFunc(Arg2:=True)",
                "End Sub",
                "",
                "'* Example of a function.",
                "'*",
                "'* @param Arg1 Example of a required argument.",
                "'* @param Arg2 Example of an optional argument.",
                "'* @returns Example of a return value.",
                "'*",
                "'* @details",
                "'* This is an example of a function that has a required argument and an optional argument.",
                "Public Function ExampleFunc(ByRef Arg1 As Long, Optional Arg2 As Boolean = False) As String",
                "End Function"
            ]));

        var function = Assert.Single(
            tree.Module.Declarations,
            declaration => declaration.Name == "ExampleFunc");
        Assert.Equal(
            "Example of a function.\n\n"
            + "This is an example of a function that has a required argument and an optional argument.\n\n"
            + "@param Arg1 Example of a required argument.\n\n"
            + "@param Arg2 Example of an optional argument.\n\n"
            + "@return Example of a return value.",
            function.Documentation);
        Assert.Equal(
            "Example of a function.\n\n"
            + "This is an example of a function that has a required argument and an optional argument.\n\n"
            + "@return Example of a return value.",
            function.Signature?.Documentation);

        var arg1 = Assert.Single(
            tree.Module.Declarations,
            declaration => declaration.Name == "Arg1");
        Assert.Equal("Example of a required argument.", arg1.Documentation);

        var arg2 = Assert.Single(
            tree.Module.Declarations,
            declaration => declaration.Name == "Arg2");
        Assert.Equal("Example of an optional argument.", arg2.Documentation);
    }
}
