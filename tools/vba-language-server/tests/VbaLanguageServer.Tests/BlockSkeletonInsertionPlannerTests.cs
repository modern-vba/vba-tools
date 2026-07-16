using VbaLanguageServer.BlockSkeletonInsertion;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class BlockSkeletonInsertionPlannerTests
{
    [Theory]
    [InlineData(
        "  Public Sub Run()\n    ",
        0,
        18,
        true,
        2,
        "\n    ",
        "\n  End Sub")]
    [InlineData(
        "\tPublic Sub Run()\r\n\t",
        0,
        17,
        false,
        8,
        "\r\n\t\t",
        "\r\n\tEnd Sub")]
    [InlineData(
        "    Public Sub Run( _\r\n        )\r\n        ",
        1,
        9,
        true,
        2,
        "\r\n      ",
        "\r\n    End Sub")]
    public void Planner_preserves_line_endings_and_resolved_first_line_indentation(
        string text,
        int line,
        int character,
        bool insertSpaces,
        int indentSize,
        string expectedBeforeCursor,
        string expectedAfterCursor)
    {
        const string uri = "file:///C:/work/Planner.bas";
        var snapshot = CreateSnapshot(uri, version: 5, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(line, character),
            VbaIndentationStyle.FromEditorOptions(insertSpaces, indentSize));

        Assert.NotNull(plan);
        Assert.Equal(5, plan.DocumentVersion);
        Assert.Equal(new BlockSkeletonInsertionPosition(line, character), plan.Position);
        Assert.Equal(expectedBeforeCursor, plan.TextBeforeCursor);
        Assert.Equal(expectedAfterCursor, plan.TextAfterCursor);
    }

    [Fact]
    public void Planner_rejects_a_validation_error_overlapping_the_header()
    {
        const string uri = "file:///C:/work/Invalid.bas";
        const string header = "Public Sub Run(value As Long, value As Long)";
        var snapshot = CreateSnapshot(uri, version: 5, text: $"{header}\n    ");

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Contains(
            snapshot.Diagnostics.DocumentValidationDiagnostics,
            diagnostic => diagnostic.Code == "validation.duplicateCallableParameterName");
        Assert.Null(plan);
    }

    [Fact]
    public void Planner_rejects_a_snapshot_whose_module_kind_does_not_match_its_tree()
    {
        const string uri = "file:///C:/work/Inconsistent.bas";
        const string header = "Public Sub Run()";
        var snapshot = CreateSnapshot(uri, version: 5, text: $"{header}\n    ") with
        {
            ModuleKind = VbaModuleKind.ClassModule
        };

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    [Theory]
    [InlineData("file:///C:/work/GlobalModule.bas", true)]
    [InlineData("file:///C:/work/GlobalClass.cls", false)]
    public void Planner_allows_global_sub_only_in_a_standard_module(
        string uri,
        bool expectedPlan)
    {
        const string header = "Global Sub Run()";
        var snapshot = CreateSnapshot(uri, version: 5, text: $"{header}\n    ");

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Equal(expectedPlan, plan is not null);
    }

    [Theory]
    [InlineData("Public Sub Run()")]
    [InlineData("Public Sub Run()\n    \n")]
    [InlineData("Public Sub Run()\n    Debug.Print 1")]
    [InlineData("Public Sub Run()\n    ' existing comment")]
    [InlineData("Public Sub Run()\nEnd Sub")]
    public void Planner_rejects_every_post_header_context_outside_the_eof_slice(string text)
    {
        const string uri = "file:///C:/work/NotEof.bas";
        const string header = "Public Sub Run()";
        var snapshot = CreateSnapshot(uri, version: 5, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

    private static VbaVersionedDocumentSnapshot CreateSnapshot(
        string uri,
        int version,
        string text)
    {
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.OpenDocument(uri, version, text);
        return Assert.IsType<VbaVersionedDocumentSnapshot>(
            workspace.GetDocumentSnapshot(uri, version));
    }
}
