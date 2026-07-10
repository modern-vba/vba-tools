using VbaLanguageServer.Diagnostics;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class SyntaxDiagnosticsTests
{
    [Fact]
    public void Diagnostics_match_existing_typescript_invalid_trailing_comment_continuation_case()
    {
        const string invalidLine = "        \"needle\", _ ' comment";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "",
            "Public Sub Run()",
            "    ReadValue( _",
            invalidLine,
            "End Sub"
        ]);

        var diagnostic = Assert.Single(VbaSyntaxDiagnostics.Collect(source, "Worker.bas"));

        Assert.Equal("syntax.invalidTrailingCommentContinuation", diagnostic.Code);
        Assert.Equal("Code line-continuation marker cannot be followed by a comment.", diagnostic.Message);
        Assert.Equal("error", diagnostic.Severity);
        Assert.Equal("vba-language-server", diagnostic.Source);
        Assert.Equal(new VbaRange(new VbaPosition(5, invalidLine.IndexOf('_')), new VbaPosition(5, invalidLine.Length)), diagnostic.Range);
    }

    [Fact]
    public void Diagnostics_match_existing_typescript_unterminated_string_case()
    {
        const string invalidLine = "    value = \"unterminated";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "",
            "Public Sub Run()",
            invalidLine,
            "End Sub"
        ]);

        var diagnostic = Assert.Single(VbaSyntaxDiagnostics.Collect(source, "Worker.bas"));

        Assert.Equal("syntax.unterminatedStringLiteral", diagnostic.Code);
        Assert.Equal("String literal is missing a closing double quote.", diagnostic.Message);
        Assert.Equal("error", diagnostic.Severity);
        Assert.Equal("vba-language-server", diagnostic.Source);
        Assert.Equal(new VbaRange(new VbaPosition(4, invalidLine.IndexOf('"')), new VbaPosition(4, invalidLine.Length)), diagnostic.Range);
    }

    [Fact]
    public void Diagnostics_cover_cls_and_frm_code_while_ignoring_frm_designer_text()
    {
        const string classInvalidLine = "    value = \"unterminated";
        const string formInvalidLine = "    value = \"unterminated";
        var classSource = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "",
            "Public Sub Run()",
            classInvalidLine,
            "End Sub"
        ]);
        var formSource = string.Join('\n', [
            "VERSION 5.00",
            "Begin VB.Form Dialog",
            "  Caption = \"designer text is not code",
            "End",
            "Attribute VB_Name = \"Dialog\"",
            "Option Explicit",
            "",
            "Public Sub Run()",
            formInvalidLine,
            "End Sub"
        ]);

        var classDiagnostic = Assert.Single(VbaSyntaxDiagnostics.Collect(classSource, "Worker.cls"));
        var formDiagnostic = Assert.Single(VbaSyntaxDiagnostics.Collect(formSource, "Dialog.frm"));

        Assert.Equal(new VbaRange(new VbaPosition(5, classInvalidLine.IndexOf('"')), new VbaPosition(5, classInvalidLine.Length)), classDiagnostic.Range);
        Assert.Equal(new VbaRange(new VbaPosition(8, formInvalidLine.IndexOf('"')), new VbaPosition(8, formInvalidLine.Length)), formDiagnostic.Range);
    }

    [Fact]
    public void Diagnostics_reject_parenthesis_free_raise_event_arguments()
    {
        const string invalidLine = "    RaiseEvent Saved \"ok\"";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Event Saved(ByVal message As String)",
            "Public Sub Run()",
            invalidLine,
            "End Sub"
        ]);

        var diagnostic = Assert.Single(VbaSyntaxDiagnostics.Collect(source, "Worker.bas"));

        Assert.Equal("syntax.raiseEventArgumentListRequiresParentheses", diagnostic.Code);
        Assert.Equal("RaiseEvent arguments must be enclosed in parentheses.", diagnostic.Message);
        Assert.Equal("error", diagnostic.Severity);
        Assert.Equal("vba-language-server", diagnostic.Source);
        Assert.Equal(
            new VbaRange(
                new VbaPosition(3, invalidLine.IndexOf("\"ok\"", StringComparison.Ordinal)),
                new VbaPosition(3, invalidLine.Length)),
            diagnostic.Range);
    }

    [Fact]
    public void Diagnostics_reject_parenthesis_free_raise_event_multiple_arguments()
    {
        const string invalidLine = "    RaiseEvent Saved \"ok\", 1";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Event Saved(ByVal message As String, ByVal count As Long)",
            "Public Sub Run()",
            invalidLine,
            "End Sub"
        ]);

        var diagnostic = Assert.Single(VbaSyntaxDiagnostics.Collect(source, "Worker.bas"));

        Assert.Equal("syntax.raiseEventArgumentListRequiresParentheses", diagnostic.Code);
        Assert.Equal(
            new VbaRange(
                new VbaPosition(3, invalidLine.IndexOf("\"ok\", 1", StringComparison.Ordinal)),
                new VbaPosition(3, invalidLine.Length)),
            diagnostic.Range);
    }

    [Theory]
    [InlineData("    RaiseEvent Saved")]
    [InlineData("    RaiseEvent Saved()")]
    [InlineData("    RaiseEvent Saved(\"ok\")")]
    [InlineData("    Example \"ok\", 1")]
    public void Diagnostics_do_not_reject_valid_raise_event_or_ordinary_call_shapes(string statementLine)
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Event Saved(ByVal message As String)",
            "Public Sub Run()",
            statementLine,
            "End Sub"
        ]);

        Assert.DoesNotContain(
            VbaSyntaxDiagnostics.Collect(source, "Worker.bas"),
            diagnostic => diagnostic.Code == "syntax.raiseEventArgumentListRequiresParentheses");
    }

    [Fact]
    public void Valid_representative_sources_return_no_syntax_diagnostics()
    {
        var basSource = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "",
            "Public Sub Run()",
            "    Dim ordinary_identifier As String",
            "    ordinary_identifier = \"a \"\"quoted\"\" value\"",
            "    value = 1 ' #not-a-date# \"unterminated `",
            "    Rem #not-a-date# \"unterminated `",
            "End Sub"
        ]);
        var clsSource = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "",
            "Public Sub Run()",
            "    Debug.Print \"ok\"",
            "End Sub"
        ]);
        var frmSource = string.Join('\n', [
            "VERSION 5.00",
            "Begin VB.Form Dialog",
            "  Caption = \"designer text is not code",
            "End",
            "Attribute VB_Name = \"Dialog\"",
            "Option Explicit",
            "",
            "Public Sub Run()",
            "    Debug.Print \"ok\"",
            "End Sub"
        ]);

        Assert.Empty(VbaSyntaxDiagnostics.Collect(basSource, "Worker.bas"));
        Assert.Empty(VbaSyntaxDiagnostics.Collect(clsSource, "Worker.cls"));
        Assert.Empty(VbaSyntaxDiagnostics.Collect(frmSource, "Dialog.frm"));
    }

    [Fact]
    public void Diagnostics_include_parser_recovery_without_semantic_name_checks()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function () As String",
            "    value = \"unterminated",
            "    ReadValue _ ' bad continuation",
            "    @",
            "Public Sub Run()",
            "    If ready Then",
            "        MissingIdentifier"
        ]);

        var diagnostics = VbaSyntaxDiagnostics.Collect(source, "Worker.bas");

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "syntax.malformedDeclarationHeader");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "syntax.unterminatedStringLiteral");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "syntax.invalidTrailingCommentContinuation");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "syntax.unexpectedStatementBoundaryToken");
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Code == "syntax.missingBlockTerminator"
            && diagnostic.Message.Contains("End If", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Code == "syntax.missingBlockTerminator"
            && diagnostic.Message.Contains("End Sub", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code.Contains("unresolved", StringComparison.OrdinalIgnoreCase));
    }
}
