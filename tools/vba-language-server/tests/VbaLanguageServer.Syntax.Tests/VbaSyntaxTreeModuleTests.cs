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
