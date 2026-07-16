using VbaLanguageServer.BlockSkeletonInsertion;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;
using VbaLanguageServer.Workspace;
using Xunit;
using PublishedSyntaxDiagnostic = VbaLanguageServer.Diagnostics.VbaSyntaxDiagnostic;

namespace VbaLanguageServer.Tests;

public sealed class BlockSkeletonInsertionPlannerTests
{
    [Fact]
    public void Planner_inserts_before_a_same_level_sub_and_preserves_existing_blank_lines()
    {
        const string uri = "file:///C:/work/Boundary.bas";
        const string header = "Public Sub First()";
        const string text = "Public Sub First()\n    \n\nPublic Sub Second()\nEnd Sub";
        var snapshot = CreateSnapshot(uri, version: 5, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Contains(
            snapshot.Diagnostics.SyntaxDiagnostics,
            diagnostic => diagnostic.Code == "syntax.missingBlockTerminator"
                && diagnostic.Message == "Block is missing 'End Sub'.");
        Assert.NotNull(plan);
        Assert.Equal("\n    ", plan.TextBeforeCursor);
        Assert.Equal("\nEnd Sub", plan.TextAfterCursor);
        Assert.Equal(
            "Public Sub First()\n    \nEnd Sub\n\nPublic Sub Second()\nEnd Sub",
            ApplyPlan(text, VbaSourceText.From(text), plan));
    }

    [Theory]
    [InlineData(
        "Public Sub First()\n    \n",
        "Public Sub First()\n    \nEnd Sub\n")]
    [InlineData(
        "Public Sub First()\r\n    \r\n\r\nPublic Sub Second()\r\nEnd Sub",
        "Public Sub First()\r\n    \r\nEnd Sub\r\n\r\nPublic Sub Second()\r\nEnd Sub")]
    [InlineData(
        "Public Sub First()\n    \n\nPublic Sub Second( _\n    )\nEnd Sub",
        "Public Sub First()\n    \nEnd Sub\n\nPublic Sub Second( _\n    )\nEnd Sub")]
    [InlineData(
        "Public Sub First()\n    \n\nPublic Sub Second() ' comment ending in _\nEnd Sub",
        "Public Sub First()\n    \nEnd Sub\n\nPublic Sub Second() ' comment ending in _\nEnd Sub")]
    [InlineData(
        "Public Sub First()\n    \n\nPublic Sub Second()",
        "Public Sub First()\n    \nEnd Sub\n\nPublic Sub Second()")]
    public void Planner_preserves_blank_to_eof_and_complete_same_level_sub_boundaries(
        string text,
        string expectedText)
    {
        const string uri = "file:///C:/work/SafeBoundary.bas";
        const string header = "Public Sub First()";
        var snapshot = CreateSnapshot(uri, version: 5, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.NotNull(plan);
        Assert.Equal(expectedText, ApplyPlan(text, VbaSourceText.From(text), plan));
    }

    [Fact]
    public void Planner_preserves_unrelated_downstream_errors()
    {
        const string uri = "file:///C:/work/UnrelatedErrors.bas";
        const string header = "Public Sub First()";
        const string text = "Public Sub First()\n"
            + "    \n"
            + "\n"
            + "Public Sub Second()\n"
            + "End Sub\n"
            + "\n"
            + "Public Sub Third(value As Long, value As Long)\n"
            + "    value = \"unterminated\n"
            + "End Sub";
        var snapshot = CreateSnapshot(uri, version: 5, text);

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Contains(
            snapshot.Diagnostics.SyntaxDiagnostics,
            diagnostic => diagnostic.Code == "syntax.unterminatedStringLiteral");
        Assert.Contains(
            snapshot.Diagnostics.DocumentValidationDiagnostics,
            diagnostic => diagnostic.Code == "validation.duplicateCallableParameterName");
        Assert.NotNull(plan);
        var speculativeText = ApplyPlan(text, VbaSourceText.From(text), plan);
        var speculativeDiagnostics = VbaDiagnosticPipeline.CollectDocument(
            VbaSyntaxTree.ParseModule(uri, speculativeText),
            uri);
        Assert.DoesNotContain(
            speculativeDiagnostics.SyntaxDiagnostics,
            diagnostic => diagnostic.Code == "syntax.missingBlockTerminator");
        Assert.Contains(
            speculativeDiagnostics.SyntaxDiagnostics,
            diagnostic => diagnostic.Code == "syntax.unterminatedStringLiteral");
        Assert.Contains(
            speculativeDiagnostics.DocumentValidationDiagnostics,
            diagnostic => diagnostic.Code == "validation.duplicateCallableParameterName");
        Assert.Equal(
            CountErrors(snapshot.Diagnostics) - 1,
            CountErrors(speculativeDiagnostics));
    }

    [Fact]
    public void Planner_ignores_overlapping_warning_and_information_diagnostics()
    {
        const string uri = "file:///C:/work/NonErrors.bas";
        const string header = "Public Sub First()";
        const string text = "Public Sub First()\n    \n\nPublic Sub Second()\nEnd Sub";
        var snapshot = CreateSnapshot(uri, version: 5, text);
        var headerRange = new VbaRange(
            new VbaPosition(0, 0),
            new VbaPosition(0, header.Length));
        snapshot = snapshot with
        {
            Diagnostics = snapshot.Diagnostics with
            {
                SyntaxDiagnostics = snapshot.Diagnostics.SyntaxDiagnostics
                    .Append(new PublishedSyntaxDiagnostic(
                        "test.warning",
                        "A warning overlapping the candidate.",
                        headerRange,
                        Severity: "warning"))
                    .ToArray(),
                DocumentValidationDiagnostics = snapshot.Diagnostics.DocumentValidationDiagnostics
                    .Append(new VbaValidationDiagnostic(
                        "test.information",
                        "Information overlapping the candidate.",
                        headerRange,
                        Severity: "information"))
                    .ToArray()
            }
        };

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.NotNull(plan);
    }

    [Fact]
    public void Planner_rejects_an_injected_error_overlapping_the_header()
    {
        const string uri = "file:///C:/work/OverlappingError.bas";
        const string header = "Public Sub First()";
        const string text = "Public Sub First()\n    \n\nPublic Sub Second()\nEnd Sub";
        var snapshot = CreateSnapshot(uri, version: 5, text);
        snapshot = snapshot with
        {
            Diagnostics = snapshot.Diagnostics with
            {
                SyntaxDiagnostics = snapshot.Diagnostics.SyntaxDiagnostics
                    .Append(new PublishedSyntaxDiagnostic(
                        "test.error",
                        "An error overlapping the candidate.",
                        new VbaRange(
                            new VbaPosition(0, 0),
                            new VbaPosition(0, header.Length))))
                    .ToArray()
            }
        };

        var plan = BlockSkeletonInsertionPlanner.CreatePlan(
            snapshot,
            new BlockSkeletonInsertionPosition(0, header.Length),
            VbaIndentationStyle.FromEditorOptions(insertSpaces: true, indentSize: 4));

        Assert.Null(plan);
    }

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
    [InlineData("Public Sub Run()\n    Debug.Print 1")]
    [InlineData("Public Sub Run()\n    ' existing comment")]
    [InlineData("Public Sub Run()\n    Rem existing comment")]
    [InlineData("Public Sub Run()\nEnd Sub")]
    [InlineData("Public Sub Run()\n    \n\nPublic Function NextValue() As Long\nEnd Function")]
    [InlineData("Public Sub Run()\n    \n\n    Public Sub Nested()\n    End Sub")]
    [InlineData("Public Sub Run()\n    \n\nPublic Sub Broken(")]
    [InlineData("Public Sub Run()\n    \n\n#Const Enabled = True")]
    [InlineData("Public Sub Run()\n    \n\nPublic value As Long")]
    [InlineData("  Public Sub Run()\n      \n\n\t\tPublic Sub DifferentWhitespace()\n\t\tEnd Sub")]
    public void Planner_rejects_body_owned_or_unproven_post_header_context(string text)
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

    private static string ApplyPlan(
        string text,
        VbaSourceText source,
        BlockSkeletonInsertionPlan plan)
    {
        var line = source.Lines[plan.Position.Line];
        var startOffset = line.StartOffset + plan.Position.Character;
        var endOffset = startOffset;
        if (text.AsSpan(endOffset).StartsWith("\r\n", StringComparison.Ordinal))
        {
            endOffset += 2;
        }
        else
        {
            endOffset++;
        }

        while (endOffset < text.Length && text[endOffset] is ' ' or '\t')
        {
            endOffset++;
        }

        return text[..startOffset]
            + plan.TextBeforeCursor
            + plan.TextAfterCursor
            + text[endOffset..];
    }

    private static int CountErrors(VbaDiagnosticPipelineResult diagnostics)
        => diagnostics.SyntaxDiagnostics.Count(diagnostic =>
                diagnostic.Severity.Equals("error", StringComparison.OrdinalIgnoreCase))
            + diagnostics.DocumentValidationDiagnostics.Count(diagnostic =>
                diagnostic.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));
}
