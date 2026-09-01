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
    public void ParserReportsMalformedModuleIdentityMetadataOnTheRepairablePayload()
    {
        const string source = "Attribute VB_Name = \"123Bad\"";

        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            source);

        var diagnostic = Assert.Single(
            tree.Diagnostics,
            candidate => candidate.Code
                == "syntax.moduleIdentityMetadataMalformed");
        Assert.Equal(RangeOf(source, "123Bad"), diagnostic.Range);
        Assert.Contains(
            "re-export or repair",
            diagnostic.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParserReportsOnlyTheMalformedModuleIdentityRepairCandidate()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"GoodIdentity\"",
            "Attribute VB_Name.\"BadIdentity\""
        ]);

        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            source);

        var diagnostic = Assert.Single(
            tree.Diagnostics,
            candidate => candidate.Code
                == "syntax.moduleIdentityMetadataMalformed");
        Assert.Equal(RangeOf(source, "VB_Name", occurrence: 1), diagnostic.Range);
    }

    [Fact]
    public void ParserReportsEveryDuplicateStandardModuleIdentityPayload()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"FirstIdentity\"",
            "Attribute VB_Name = \"SecondIdentity\""
        ]);

        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            source);

        var diagnostics = tree.Diagnostics
            .Where(candidate => candidate.Code
                == "syntax.moduleIdentityMetadataDuplicate")
            .ToArray();
        Assert.Collection(
            diagnostics,
            first => Assert.Equal(RangeOf(source, "FirstIdentity"), first.Range),
            second => Assert.Equal(RangeOf(source, "SecondIdentity"), second.Range));
    }

    [Fact]
    public void ParserUsesTheLastRepeatedClassIdentityAsAuthority()
    {
        var source = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "BEGIN",
            "  MultiUse = -1",
            "END",
            "Attribute VB_Name = \"ShadowedIdentity\"",
            "Attribute VB_Name = \"CurrentIdentity\"",
            "Attribute VB_GlobalNameSpace = False",
            "Attribute VB_Creatable = False",
            "Attribute VB_PredeclaredId = False",
            "Attribute VB_Exposed = True"
        ]);

        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.cls",
            source);

        Assert.Equal("CurrentIdentity", tree.Module.Identity.Name);
        Assert.Equal(RangeOf(source, "CurrentIdentity"), tree.Module.Identity.Range);
        Assert.DoesNotContain(
            tree.Diagnostics,
            candidate => candidate.Code.StartsWith(
                "syntax.moduleIdentityMetadata",
                StringComparison.Ordinal));
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
    public void ParserUsesVbaIdentifierAuthorityForFormDesignerIdentity()
    {
        var source = string.Join('\n', [
            "VERSION 5.00",
            "Begin VB.UserForm 集計",
            "  Picture = \"集計.frx\":0010",
            "End",
            "Attribute VB_Name = \"集計\""
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/集計.frm", source);

        var designer = Assert.IsType<VbaFormDesignerBlock>(
            tree.Module.FormDesignerBlock);
        var root = Assert.IsType<VbaFormDesignerRoot>(designer.Root);
        Assert.Equal("集計", root.Name);
        Assert.Equal(RangeOf(source, "集計"), root.NameRange);
        var resource = Assert.Single(designer.ResourceReferences);
        Assert.Equal("集計.frx", resource.FileName);
        Assert.Equal("0010", resource.Offset);
        Assert.Empty(designer.EvidenceProblems);
    }

    [Fact]
    public void ParserRecognizesIndexedFormDesignerResourceProperties()
    {
        var source = string.Join('\n', [
            "VERSION 5.00",
            "Begin VB.UserForm Dialog",
            "  TabPicture(0) = \"Dialog.frx\":0010",
            "End",
            "Attribute VB_Name = \"Dialog\""
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Dialog.frm", source);

        var designer = Assert.IsType<VbaFormDesignerBlock>(
            tree.Module.FormDesignerBlock);
        var resource = Assert.Single(designer.ResourceReferences);
        Assert.Equal("TabPicture(0)", resource.PropertyName);
        Assert.Equal("Dialog.frx", resource.FileName);
        Assert.Equal("0010", resource.Offset);
        Assert.Empty(designer.EvidenceProblems);
    }

    [Fact]
    public void ParserDistinguishesDotPrefixedSidecarBasenamesFromParentTraversal()
    {
        var source = string.Join('\n', [
            "VERSION 5.00",
            "Begin VB.UserForm Dialog",
            "  Picture = \"..Dialog.frx\":0010",
            "  MouseIcon = \"..\\Dialog.frx\":0020",
            "End",
            "Attribute VB_Name = \"Dialog\""
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Dialog.frm", source);

        var designer = Assert.IsType<VbaFormDesignerBlock>(
            tree.Module.FormDesignerBlock);
        var reference = Assert.Single(designer.ResourceReferences);
        Assert.Equal("..Dialog.frx", reference.FileName);
        var problem = Assert.Single(designer.EvidenceProblems);
        Assert.Equal(
            VbaFormDesignerEvidenceProblemKind.ResourceReferenceUnsafe,
            problem.Kind);
        Assert.Equal("..\\Dialog.frx", problem.Value);
    }

    [Theory]
    [InlineData("Begin VB.UserForm Dialog\n   Begin VB.CommandButton\nEnd")]
    [InlineData("Begin VB.UserForm Dialog\nEnd\nPicture = \"Dialog.frx\":0000")]
    [InlineData("Begin VB.UserForm Dialog\nEnd\nCaption = \"Outside root\"")]
    [InlineData("Begin VB.UserForm Dialog\nEnd\nTabCaption(0) = \"Outside root\"")]
    [InlineData("BeginProperty Font\nEndProperty\nBegin VB.UserForm Dialog\nEnd")]
    [InlineData("Begin VB.UserForm Dialog\nBeginProperty\nEndProperty\nEnd")]
    public void ParserReportsMalformedFormDesignerStructure(string designerBody)
    {
        var source = string.Join('\n', [
            "VERSION 5.00",
            designerBody,
            "Attribute VB_Name = \"Dialog\""
        ]);

        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Dialog.frm", source);

        var designer = Assert.IsType<VbaFormDesignerBlock>(
            tree.Module.FormDesignerBlock);
        Assert.Contains(
            designer.EvidenceProblems,
            problem => problem.Kind
                == VbaFormDesignerEvidenceProblemKind.StructureMalformed);
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

    private static VbaSyntaxRange RangeOf(
        string source,
        string value,
        int occurrence = 0)
    {
        var startOffset = -1;
        for (var index = 0; index <= occurrence; index++)
        {
            startOffset = source.IndexOf(
                value,
                startOffset + 1,
                StringComparison.Ordinal);
        }

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
