using System.Text.RegularExpressions;

namespace VbaLanguageServer.Syntax;

/// <summary>
/// Centralizes statement-boundary and formatting block facts for parsed VBA source.
/// </summary>
internal static class VbaBlockSyntaxFacts
{
    public static string? GetFormattingOpenTerminator(string trimmedLine)
        => GetProcedureOpenTerminator(trimmedLine)
            ?? GetStructuredStatementOpenTerminator(trimmedLine)
            ?? GetFormattingDeclarationOpenTerminator(trimmedLine)
            ?? GetWhileOpenTerminator(trimmedLine);

    public static string? GetFormattingCloseTerminator(string trimmedLine)
        => GetStatementCloseTerminator(trimmedLine)
            ?? GetFormattingDeclarationCloseTerminator(trimmedLine)
            ?? GetWhileCloseTerminator(trimmedLine);

    public static string? GetFormattingBranchTerminator(string trimmedLine)
    {
        if (IsMatch(trimmedLine, "^(Else|ElseIf)\\b"))
        {
            return "End If";
        }

        return IsMatch(trimmedLine, "^Case\\b") ? "End Select" : null;
    }

    public static string? GetStatementCloseTerminator(string trimmedLine)
        => IsMatch(trimmedLine, "^End\\s+Sub\\b") ? "End Sub"
            : IsMatch(trimmedLine, "^End\\s+Function\\b") ? "End Function"
            : IsMatch(trimmedLine, "^End\\s+Property\\b") ? "End Property"
            : IsMatch(trimmedLine, "^End\\s+If\\b") ? "End If"
            : IsMatch(trimmedLine, "^End\\s+With\\b") ? "End With"
            : IsMatch(trimmedLine, "^End\\s+Select\\b") ? "End Select"
            : IsMatch(trimmedLine, "^Next\\b") ? "Next"
            : IsMatch(trimmedLine, "^Loop\\b") ? "Loop"
            : null;

    public static VbaStatementKind ClassifyStatement(string trimmedLine, bool isProcedureHeader)
    {
        if (isProcedureHeader)
        {
            return VbaStatementKind.ProcedureBody;
        }

        if (IsMatch(trimmedLine, "^If\\b.*\\bThen\\s*$"))
        {
            return VbaStatementKind.IfBlock;
        }

        if (IsMatch(trimmedLine, "^With\\b"))
        {
            return VbaStatementKind.WithBlock;
        }

        if (IsMatch(trimmedLine, "^Select\\s+Case\\b"))
        {
            return VbaStatementKind.SelectBlock;
        }

        if (IsMatch(trimmedLine, "^For\\b"))
        {
            return VbaStatementKind.ForBlock;
        }

        if (IsMatch(trimmedLine, "^Do\\b"))
        {
            return VbaStatementKind.DoLoopBlock;
        }

        if (trimmedLine.StartsWith("@", StringComparison.Ordinal))
        {
            return VbaStatementKind.Malformed;
        }

        if (Regex.IsMatch(trimmedLine, "^[A-Za-z_][A-Za-z0-9_]*\\s*=", RegexOptions.CultureInvariant))
        {
            return VbaStatementKind.Assignment;
        }

        if (IsMatch(trimmedLine, "^(Call\\s+)?[A-Za-z_][A-Za-z0-9_]*(?:\\.|\\b)")
            || trimmedLine.StartsWith(".", StringComparison.Ordinal))
        {
            return VbaStatementKind.Call;
        }

        return VbaStatementKind.Unknown;
    }

    public static string? GetExpectedStatementTerminator(string trimmedLine, VbaStatementKind statementKind)
        => statementKind switch
        {
            VbaStatementKind.ProcedureBody => GetProcedureOpenTerminator(trimmedLine),
            VbaStatementKind.IfBlock => "End If",
            VbaStatementKind.WithBlock => "End With",
            VbaStatementKind.SelectBlock => "End Select",
            VbaStatementKind.ForBlock => "Next",
            VbaStatementKind.DoLoopBlock => "Loop",
            _ => null
        };

    private static string? GetProcedureOpenTerminator(string trimmedLine)
        => IsMatch(trimmedLine, "^((Public|Private|Friend|Global)\\s+)?(Static\\s+)?Sub\\b") ? "End Sub"
            : IsMatch(trimmedLine, "^((Public|Private|Friend|Global)\\s+)?(Static\\s+)?Function\\b") ? "End Function"
            : IsMatch(trimmedLine, "^((Public|Private|Friend|Global)\\s+)?(Static\\s+)?Property\\b") ? "End Property"
            : null;

    private static string? GetStructuredStatementOpenTerminator(string trimmedLine)
        => IsMatch(trimmedLine, "^If\\b.*\\bThen\\s*$") ? "End If"
            : IsMatch(trimmedLine, "^Select\\s+Case\\b") ? "End Select"
            : IsMatch(trimmedLine, "^With\\b") ? "End With"
            : IsMatch(trimmedLine, "^For\\b") && !IsMatch(trimmedLine, ":\\s*Next\\b") ? "Next"
            : IsMatch(trimmedLine, "^Do\\b") && !IsMatch(trimmedLine, ":\\s*Loop\\b") ? "Loop"
            : null;

    private static string? GetFormattingDeclarationOpenTerminator(string trimmedLine)
        => IsMatch(trimmedLine, "^((Public|Private|Friend)\\s+)?Enum\\b") ? "End Enum"
            : IsMatch(trimmedLine, "^((Public|Private|Friend)\\s+)?Type\\b") ? "End Type"
            : null;

    private static string? GetFormattingDeclarationCloseTerminator(string trimmedLine)
        => IsMatch(trimmedLine, "^End\\s+Enum\\b") ? "End Enum"
            : IsMatch(trimmedLine, "^End\\s+Type\\b") ? "End Type"
            : null;

    private static string? GetWhileOpenTerminator(string trimmedLine)
        => IsMatch(trimmedLine, "^While\\b") ? "Wend" : null;

    private static string? GetWhileCloseTerminator(string trimmedLine)
        => IsMatch(trimmedLine, "^Wend\\b") ? "Wend" : null;

    private static bool IsMatch(string text, string pattern)
        => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
