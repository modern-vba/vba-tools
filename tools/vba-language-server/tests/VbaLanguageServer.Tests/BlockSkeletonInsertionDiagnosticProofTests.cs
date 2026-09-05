using VbaLanguageServer.BlockSkeletonInsertion;
using VbaLanguageServer.Diagnostics;
using VbaTools.Syntax;
using Xunit;
using PublishedSyntaxDiagnostic = VbaLanguageServer.Diagnostics.VbaSyntaxDiagnostic;

namespace VbaLanguageServer.Tests;

public sealed class BlockSkeletonInsertionDiagnosticProofTests
{
    [Fact]
    public void Proof_ignores_order_while_preserving_duplicate_errors()
    {
        var first = Error(0, 2);
        var second = Error(4, 6) with { Code = "other" };
        var original = Evidence("abcdefgh", [first, second, first]);
        var prospective = Evidence("abcdefgh", [first, first, second]);

        Assert.True(BlockSkeletonInsertionDiagnosticProof.IsSafe(new(
            original,
            prospective,
            Evidence("abcdefgh"),
            new(0, 0, 0))));
    }

    [Fact]
    public void Proof_removes_only_the_selected_count_of_an_exact_error()
    {
        var removed = Error(0, 2);
        var retained = removed with { Source = "another-source" };

        Assert.True(BlockSkeletonInsertionDiagnosticProof.IsSafe(new(
            Evidence("abcdefgh", [removed, retained, removed]),
            Evidence("abcdefgh", [retained, removed]),
            Evidence("abcdefgh", [removed]),
            new(0, 0, 0))));
    }

    [Fact]
    public void Proof_uses_control_as_expected_errors_when_its_removals_are_allowed()
    {
        var removed = Error(0, 2);
        var retained = Error(4, 6);
        var unusedCascade = Error(2, 3);

        Assert.True(BlockSkeletonInsertionDiagnosticProof.IsSafe(new(
            Evidence("abcdefgh", [removed, retained]),
            Evidence("abcdefgh", [retained]),
            Evidence("abcdefgh", [removed, unusedCascade]),
            new(0, 0, 0),
            Evidence("  cdefgh", [retained]))));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("category")]
    [InlineData("source")]
    [InlineData("severity")]
    [InlineData("code")]
    [InlineData("message")]
    [InlineData("range")]
    public void Proof_rejects_a_different_error_identity_or_count(string difference)
    {
        var original = Error(0, 2);
        var changed = difference switch
        {
            "source" => original with { Source = "another-source" },
            "severity" => original with { Severity = "ERROR" },
            "code" => original with { Code = "another-code" },
            "message" => original with { Message = "Another message." },
            "range" => Error(0, 3),
            _ => original
        };
        var prospective = difference switch
        {
            "count" => Evidence("abcdefgh", [original, original]),
            "category" => Evidence("abcdefgh", validation:
                [new(original.Code, original.Message, original.Range)]),
            _ => Evidence("abcdefgh", [changed])
        };

        Assert.False(BlockSkeletonInsertionDiagnosticProof.IsSafe(new(
            Evidence("abcdefgh", [original]),
            prospective,
            Evidence("abcdefgh"),
            new(0, 0, 0))));
    }

    [Fact]
    public void Proof_rejects_removing_more_exact_errors_than_original_contains()
    {
        var error = Error(0, 2);

        Assert.False(BlockSkeletonInsertionDiagnosticProof.IsSafe(new(
            Evidence("abcdefgh", [error]),
            Evidence("abcdefgh"),
            Evidence("abcdefgh", [error, error]),
            new(0, 0, 0))));
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("")]
    public void Proof_reverse_maps_errors_before_and_after_a_replacement(string replacement)
    {
        var after = 2 + replacement.Length;
        var original = Evidence("abXXefgh", [Error(0, 2), Error(4, 8)],
            [new("validation.error", "Existing validation error.", new(new(0, 4), new(0, 6)))]);
        var prospective = Evidence("ab" + replacement + "efgh", [Error(0, 2), Error(after, after + 4)],
            [new("validation.error", "Existing validation error.", new(new(0, after), new(0, after + 2)))]);

        Assert.True(BlockSkeletonInsertionDiagnosticProof.IsSafe(new(
            original,
            prospective,
            Evidence("abXXefgh"),
            new(2, 4, after))));
    }

    [Theory]
    [InlineData("added")]
    [InlineData("duplicate")]
    [InlineData("unallowed-removal")]
    [InlineData("different-prospective")]
    public void Proof_rejects_a_control_mismatch(string mismatch)
    {
        var removed = Error(0, 2);
        var retained = Error(4, 6);
        var control = mismatch switch
        {
            "added" => new[] { retained, Error(6, 8) },
            "duplicate" => new[] { retained, retained },
            _ => new[] { retained }
        };

        Assert.False(BlockSkeletonInsertionDiagnosticProof.IsSafe(new(
            Evidence("abcdefgh", [removed, retained]),
            Evidence("abcdefgh", mismatch == "different-prospective" ? [removed] : [retained]),
            Evidence("abcdefgh", mismatch == "unallowed-removal" ? [] : [removed]),
            new(0, 0, 0),
            Evidence("  cdefgh", control))));
    }

    [Theory]
    [InlineData(-1, 0, 0, 1)]
    [InlineData(2, 0, 2, 0)]
    [InlineData(0, -1, 0, 1)]
    [InlineData(0, 0, 0, 3)]
    [InlineData(0, 2, 0, 1)]
    [InlineData(1, 0, 0, 1)]
    [InlineData(1, 0, 1, int.MaxValue)]
    [InlineData(int.MaxValue, 0, int.MaxValue, 0)]
    public void Proof_rejects_malformed_ranges_even_when_selected_for_removal(
        int startLine, int startCharacter, int endLine, int endCharacter)
    {
        var malformed = Error(0, 1) with
        {
            Range = new(new(startLine, startCharacter), new(endLine, endCharacter))
        };

        Assert.False(BlockSkeletonInsertionDiagnosticProof.IsSafe(new(
            Evidence("ab\r\ncd", [malformed]),
            Evidence("ab\r\ncd"),
            Evidence("ab\r\ncd", [malformed]),
            new(0, 0, 0))));
    }

    [Theory]
    [InlineData("original")]
    [InlineData("prospective")]
    [InlineData("allowance")]
    [InlineData("control")]
    public void Proof_rejects_coordinates_that_alias_a_different_physical_line(string role)
    {
        var valid = Error(0, 1) with { Range = new(new(1, 0), new(1, 1)) };
        var malformed = valid with { Range = new(new(0, 4), new(0, 5)) };

        Assert.False(BlockSkeletonInsertionDiagnosticProof.IsSafe(new(
            Evidence("ab\r\ncd", role == "original" ? [malformed] : [valid]),
            Evidence("ab\r\ncd", role == "allowance" ? [] : role == "prospective" ? [malformed] : [valid]),
            Evidence("ab\r\ncd", role == "allowance" ? [malformed] : []),
            new(0, 0, 0),
            role == "control" ? Evidence("ab\r\ncd", [malformed]) : null)));
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 5)]
    [InlineData(3, 3)]
    [InlineData(4, 6)]
    public void Proof_rejects_prospective_ranges_overlapping_the_replacement(int start, int end)
    {
        Assert.False(BlockSkeletonInsertionDiagnosticProof.IsSafe(new(
            Evidence("abXXefgh", [Error(start, end)]),
            Evidence("ab123efgh", [Error(start, end)]),
            Evidence("abXXefgh"),
            new(2, 4, 5))));
    }

    [Fact]
    public void Proof_preserves_zero_width_ranges_adjacent_to_the_replacement()
    {
        Assert.True(BlockSkeletonInsertionDiagnosticProof.IsSafe(new(
            Evidence("abXXefgh", [Error(2, 2), Error(4, 4)]),
            Evidence("ab123efgh", [Error(2, 2), Error(5, 5)]),
            Evidence("abXXefgh"),
            new(2, 4, 5))));
    }

    [Theory]
    [InlineData("zb123efgh")]
    [InlineData("ab123efgx")]
    [InlineData("ab123efgh!")]
    public void Proof_rejects_prospective_source_that_does_not_match_the_replacement(string prospective)
    {
        Assert.False(BlockSkeletonInsertionDiagnosticProof.IsSafe(new(
            Evidence("abXXefgh"),
            Evidence(prospective),
            Evidence("abXXefgh"),
            new(2, 4, 5))));
    }

    [Fact]
    public void Proof_rejects_removal_evidence_from_different_source_text()
    {
        Assert.False(BlockSkeletonInsertionDiagnosticProof.IsSafe(new(
            Evidence("abcdefgh"),
            Evidence("abcdefgh"),
            Evidence("abcdEfgh"),
            new(0, 0, 0))));
    }

    [Theory]
    [InlineData("a\r\nbcd")]
    [InlineData("abcdef")]
    [InlineData("ab\ncd")]
    [InlineData("ab\n\rcd")]
    public void Proof_rejects_control_source_with_a_different_coordinate_layout(string control)
    {
        Assert.False(BlockSkeletonInsertionDiagnosticProof.IsSafe(new(
            Evidence("ab\r\ncd"),
            Evidence("ab\r\ncd"),
            Evidence("ab\r\ncd"),
            new(0, 0, 0),
            Evidence(control))));
    }

    [Theory]
    [InlineData(-1, 4, 5)]
    [InlineData(4, 2, 5)]
    [InlineData(2, 9, 5)]
    [InlineData(2, 4, 1)]
    [InlineData(2, 4, 10)]
    [InlineData(int.MinValue, int.MaxValue, int.MaxValue)]
    [InlineData(0, 0, int.MaxValue)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue)]
    public void Proof_rejects_invalid_or_overflowing_replacement_coordinates(
        int start, int end, int prospectiveEnd)
    {
        Assert.False(BlockSkeletonInsertionDiagnosticProof.IsSafe(new(
            Evidence("abXXefgh"),
            Evidence("ab123efgh"),
            Evidence("abXXefgh"),
            new(start, end, prospectiveEnd))));
    }

    [Fact]
    public void Proof_ignores_warnings_information_and_project_errors_including_their_ranges()
    {
        var invalidRange = new VbaRange(new(-1, -1), new(int.MaxValue, int.MaxValue));
        var original = new BlockSkeletonInsertionDiagnosticEvidence(VbaSourceText.From("abcdefgh"), new(
            [Error(0, 1) with { Severity = "warning", Range = invalidRange },
                Error(0, 1) with { Severity = "information", Range = invalidRange }],
            [new("validation.warning", "Warning.", invalidRange, "warning")],
            [new("project.error", "Project error.", invalidRange)]));
        var prospective = new BlockSkeletonInsertionDiagnosticEvidence(VbaSourceText.From("abcdefgh"), new(
            [],
            [new("validation.information", "Information.", invalidRange, "information")],
            [new("different.project.error", "Another project error.", invalidRange)]));

        Assert.True(BlockSkeletonInsertionDiagnosticProof.IsSafe(new(
            original, prospective, Evidence("abcdefgh"), new(0, 0, 0))));
    }

    [Fact]
    public void Proof_retains_captured_syntax_and_validation_evidence_after_caller_mutation()
    {
        var syntax = new[] { Error(0, 2) };
        var validation = new List<VbaValidationDiagnostic>
        {
            new("validation.error", "Existing validation error.", new(new(0, 4), new(0, 6)))
        };
        var original = Evidence("abcdefgh", syntax, validation);
        var prospective = Evidence("abcdefgh", [syntax[0]], [validation[0]]);
        var proofCase = new BlockSkeletonInsertionDiagnosticProofCase(
            original, prospective, Evidence("abcdefgh"), new(0, 0, 0));

        syntax[0] = syntax[0] with { Source = "changed-after-capture" };
        validation.Clear();

        Assert.True(BlockSkeletonInsertionDiagnosticProof.IsSafe(proofCase));
    }

    [Fact]
    public void Proof_reverse_maps_multiline_errors_across_a_crlf_replacement()
    {
        var original = Error(0, 1) with { Range = new(new(1, 4), new(2, 2)) };
        var prospective = original with { Range = new(new(2, 1), new(3, 2)) };

        Assert.True(BlockSkeletonInsertionDiagnosticProof.IsSafe(new(
            Evidence("ab\r\ncdXXef\r\ngh", [original]),
            Evidence("ab\r\ncd1\r\n2ef\r\ngh", [prospective]),
            Evidence("ab\r\ncdXXef\r\ngh"),
            new(6, 8, 10))));
    }

    [Fact]
    public void Proof_removes_exact_validation_counts_without_removing_the_matching_syntax_error()
    {
        var syntax = Error(0, 2);
        var validation = new VbaValidationDiagnostic(syntax.Code, syntax.Message, syntax.Range);

        Assert.True(BlockSkeletonInsertionDiagnosticProof.IsSafe(new(
            Evidence("abcdefgh", [syntax], [validation, validation]),
            Evidence("abcdefgh", [syntax], [validation]),
            Evidence("abcdefgh", validation: [validation]),
            new(0, 0, 0))));
    }

    private static PublishedSyntaxDiagnostic Error(int start, int end)
        => new("syntax.error", "Existing error.", new(new(0, start), new(0, end)));

    private static BlockSkeletonInsertionDiagnosticEvidence Evidence(
        string text,
        IReadOnlyList<PublishedSyntaxDiagnostic>? syntax = null,
        IReadOnlyList<VbaValidationDiagnostic>? validation = null)
        => new(VbaSourceText.From(text), new(syntax ?? [], validation ?? [], []));
}
