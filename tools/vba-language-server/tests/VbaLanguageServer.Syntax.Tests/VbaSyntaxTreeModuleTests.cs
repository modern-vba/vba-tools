using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaSyntaxTreeModuleTests
{
    [Fact]
    public void ParserEmitsModuleIdentityAttributesAndOptionsWithRanges()
    {
        var standardSource = string.Join('\n', [
            "Attribute VB_Name = \"WorkerModule\"",
            "Option Explicit",
            "Option Private Module"
        ]);
        var classSource = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"CustomerRecord\"",
            "Option Explicit"
        ]);

        var standardTree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", standardSource);
        var classTree = VbaSyntaxTree.ParseModule("file:///C:/work/Customer.cls", classSource);

        Assert.Equal(VbaModuleKind.StandardModule, standardTree.Module.Kind);
        Assert.Equal("WorkerModule", standardTree.Module.Identity.Name);
        Assert.Equal(RangeOf(standardSource, "WorkerModule"), standardTree.Module.Identity.Range);
        Assert.Contains(standardTree.Module.Attributes, attribute =>
            attribute.Name == "VB_Name"
            && attribute.Value == "WorkerModule"
            && attribute.Range == RangeOf(standardSource, "Attribute VB_Name = \"WorkerModule\""));
        Assert.Collection(
            standardTree.Module.Options,
            option => Assert.Equal("Option Explicit", option.Text),
            option => Assert.Equal("Option Private Module", option.Text));

        Assert.Equal(VbaModuleKind.ClassModule, classTree.Module.Kind);
        Assert.Equal("CustomerRecord", classTree.Module.Identity.Name);
        Assert.Equal(RangeOf(classSource, "CustomerRecord"), classTree.Module.Identity.Range);
        Assert.Empty(standardTree.Diagnostics);
        Assert.Empty(classTree.Diagnostics);
    }

    [Theory]
    [InlineData("CDecl")]
    [InlineData("亜ㄱ")]
    [InlineData("Name$")]
    [InlineData("[Name]")]
    public void InvalidVbNameDoesNotBecomeTheAuthoritativeModuleIdentity(string name)
    {
        var source = $"Attribute VB_Name = \"{name}\"";

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", source);

        Assert.Equal("Worker", tree.Module.Identity.Name);
        Assert.Equal(tree.SourceText.StartPosition, tree.Module.Identity.Range.Start);
        Assert.Equal(tree.SourceText.StartPosition, tree.Module.Identity.Range.End);
    }

    [Fact]
    public void VbNameUsesSharedMultilingualIdentifierRecognitionAndThe31RuneLimit()
    {
        var valid = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Attribute VB_Name = \"集計\"");
        var overLength = new string('A', 32);
        var invalid = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            $"Attribute VB_Name = \"{overLength}\"");

        Assert.Equal("集計", valid.Module.Identity.Name);
        Assert.Equal("Worker", invalid.Module.Identity.Name);
    }

    [Fact]
    public void AttributeMetadataUsesExactMsVbalWhitespaceBoundaries()
    {
        var wscTree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Attribute\u0019VB_Name\u0019=\u0019\"集計\"");
        var codePageTree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Attribute\u00A0VB_Name = \"Spoofed\"");

        Assert.Equal("集計", wscTree.Module.Identity.Name);
        Assert.Equal("Worker", codePageTree.Module.Identity.Name);
        Assert.Empty(codePageTree.Module.Attributes);
    }

    [Fact]
    public void ParserUsesSharedIdentifierAuthorityForAttributeNames()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Attribute \u00A0 = \"value\"\nAttribute 亜ㄱ = \"mixed\"");

        var attribute = Assert.Single(tree.Module.Attributes);
        Assert.Equal("\u00A0", attribute.Name);
        Assert.Equal("value", attribute.Value);
    }

    [Fact]
    public void OptionMetadataUsesExactMsVbalWhitespaceBoundaries()
    {
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            "Option\u0019Explicit\nOption\u00A0Private Module");

        var option = Assert.Single(tree.Module.Options);
        Assert.Equal("Option\u0019Explicit", option.Text);
    }

    [Fact]
    public void ParserPreservesFormDesignerBlockAndParsesCodeSectionNormally()
    {
        var source = string.Join('\n', [
            "VERSION 5.00",
            "Begin VB.Form Dialog",
            "  Caption = \"Designer caption\"",
            "End",
            "Attribute VB_Name = \"DialogView\"",
            "Option Explicit"
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Dialog.frm", source);

        Assert.Equal(VbaModuleKind.FormModule, tree.Module.Kind);
        Assert.Equal("DialogView", tree.Module.Identity.Name);
        Assert.Equal(RangeOf(source, "DialogView"), tree.Module.Identity.Range);
        Assert.NotNull(tree.Module.FormDesignerBlock);
        var designerBlock = tree.Module.FormDesignerBlock;
        Assert.Contains("Caption = \"Designer caption\"", designerBlock.RawText);
        Assert.DoesNotContain("Option Explicit", designerBlock.RawText);
        Assert.True(designerBlock.Range.End.Offset <= RangeOf(source, "Attribute VB_Name").Start.Offset);
        var option = Assert.Single(tree.Module.Options);
        Assert.Equal("Option Explicit", option.Text);
        Assert.Empty(tree.Diagnostics);
    }

    [Fact]
    public void ParserReportsRecoverableFormCodeBoundaryFailure()
    {
        var source = string.Join('\n', [
            "VERSION 5.00",
            "Begin VB.Form Dialog",
            "  Caption = \"Designer caption\"",
            "End"
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Dialog.frm", source);

        Assert.Equal(VbaModuleKind.FormModule, tree.Module.Kind);
        Assert.Equal("Dialog", tree.Module.Identity.Name);
        Assert.NotNull(tree.Module.FormDesignerBlock);
        Assert.Empty(tree.Module.Attributes);
        Assert.Empty(tree.Module.Options);
        var diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal("syntax.formCodeSectionBoundaryMissing", diagnostic.Code);
        Assert.Equal("Form module is missing an Attribute VB_Name code-section boundary.", diagnostic.Message);
    }

    private static VbaSyntaxRange RangeOf(string source, string value)
    {
        var startOffset = source.IndexOf(value, StringComparison.Ordinal);
        Assert.True(startOffset >= 0, $"Could not find '{value}' in source.");
        return new VbaSyntaxRange(
            PositionAt(source, startOffset),
            PositionAt(source, startOffset + value.Length));
    }

    private static VbaSyntaxPosition PositionAt(string source, int offset)
    {
        var line = 0;
        var character = 0;
        for (var index = 0; index < offset; index++)
        {
            if (source[index] == '\n')
            {
                line++;
                character = 0;
                continue;
            }

            character++;
        }

        return new VbaSyntaxPosition(line, character, offset);
    }
}
