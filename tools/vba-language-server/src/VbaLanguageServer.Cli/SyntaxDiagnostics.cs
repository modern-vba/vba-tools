using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.Diagnostics;

/// <summary>
/// Represents a zero-based LSP-compatible document position.
/// </summary>
/// <param name="Line">The zero-based line.</param>
/// <param name="Character">The zero-based character.</param>
public sealed record VbaPosition(int Line, int Character);

/// <summary>
/// Represents a half-open LSP-compatible source range.
/// </summary>
/// <param name="Start">The inclusive start position.</param>
/// <param name="End">The exclusive end position.</param>
public sealed record VbaRange(VbaPosition Start, VbaPosition End);

/// <summary>
/// Represents a diagnostic published for a document, regardless of diagnostic category.
/// </summary>
/// <param name="Code">The stable diagnostic code.</param>
/// <param name="Message">The diagnostic message.</param>
/// <param name="Range">The diagnostic source range.</param>
/// <param name="Severity">The diagnostic severity value.</param>
/// <param name="Source">The diagnostic source name.</param>
public sealed record VbaDiagnostic(
    string Code,
    string Message,
    VbaRange Range,
    string Severity = "error",
    string Source = "vba-language-server");

/// <summary>
/// Represents a parser recovery or malformed-source diagnostic.
/// </summary>
/// <param name="Code">The stable syntax diagnostic code.</param>
/// <param name="Message">The diagnostic message.</param>
/// <param name="Range">The diagnostic source range.</param>
/// <param name="Severity">The diagnostic severity value.</param>
/// <param name="Source">The diagnostic source name.</param>
public sealed record VbaSyntaxDiagnostic(
    string Code,
    string Message,
    VbaRange Range,
    string Severity = "error",
    string Source = "vba-language-server");

/// <summary>
/// Represents a parsed-source validity diagnostic that is not parser recovery.
/// </summary>
/// <param name="Code">The stable validation diagnostic code.</param>
/// <param name="Message">The diagnostic message.</param>
/// <param name="Range">The diagnostic source range.</param>
/// <param name="Severity">The diagnostic severity value.</param>
/// <param name="Source">The diagnostic source name.</param>
public sealed record VbaValidationDiagnostic(
    string Code,
    string Message,
    VbaRange Range,
    string Severity = "error",
    string Source = "vba-language-server");

/// <summary>
/// Collects syntax diagnostics from raw source text.
/// </summary>
public static class VbaSyntaxDiagnostics
{
    /// <summary>
    /// Parses source text and returns parser syntax diagnostics.
    /// </summary>
    /// <param name="source">The source text to parse.</param>
    /// <param name="fileName">The file name or URI used for parsing context.</param>
    /// <returns>The collected syntax diagnostics.</returns>
    public static IReadOnlyList<VbaSyntaxDiagnostic> Collect(string source, string fileName)
    {
        var tree = VbaSyntaxTree.ParseModule(fileName, source);
        return VbaSyntaxDiagnosticCollector.Collect(tree, fileName);
    }
}

/// <summary>
/// Collects the combined syntax and document-local validation diagnostics for a document.
/// </summary>
public static class VbaDocumentDiagnostics
{
    /// <summary>
    /// Parses source text and returns all publishable document diagnostics.
    /// </summary>
    /// <param name="source">The source text to inspect.</param>
    /// <param name="uri">The document URI.</param>
    /// <returns>The combined diagnostics.</returns>
    public static IReadOnlyList<VbaDiagnostic> Collect(string source, string uri)
        => Collect(VbaSyntaxTree.ParseModule(uri, source), uri);

    /// <summary>
    /// Returns all publishable document diagnostics from a parsed syntax tree.
    /// </summary>
    /// <param name="tree">The parsed syntax tree.</param>
    /// <param name="uri">The document URI.</param>
    /// <returns>The combined syntax and validation diagnostics.</returns>
    public static IReadOnlyList<VbaDiagnostic> Collect(VbaSyntaxTree tree, string uri)
        => VbaDiagnosticPipeline.CollectDocument(tree, uri).Diagnostics;
}

/// <summary>
/// Projects syntax-layer diagnostics into language-server diagnostic DTOs.
/// </summary>
public static class VbaSyntaxDiagnosticCollector
{
    /// <summary>
    /// Collects syntax diagnostics from a parsed syntax tree.
    /// </summary>
    /// <param name="tree">The parsed syntax tree.</param>
    /// <param name="uri">The document URI used for caller context.</param>
    /// <returns>The projected syntax diagnostics.</returns>
    public static IReadOnlyList<VbaSyntaxDiagnostic> Collect(VbaSyntaxTree tree, string uri)
        => tree.Diagnostics
            .Select(diagnostic => new VbaSyntaxDiagnostic(
                diagnostic.Code,
                diagnostic.Message,
                new VbaRange(
                    new VbaPosition(diagnostic.Range.Start.Line, diagnostic.Range.Start.Character),
                    new VbaPosition(diagnostic.Range.End.Line, diagnostic.Range.End.Character)),
                diagnostic.Severity,
                diagnostic.Source))
            .ToArray();
}

/// <summary>
/// Collects document-local validation diagnostics from parsed syntax nodes.
/// </summary>
public static class VbaDocumentValidationDiagnosticCollector
{
    /// <summary>
    /// Collects parsed-source validity diagnostics that do not require project semantic state.
    /// </summary>
    /// <param name="tree">The parsed syntax tree.</param>
    /// <param name="uri">The document URI used for caller context.</param>
    /// <returns>The validation diagnostics.</returns>
    public static IReadOnlyList<VbaValidationDiagnostic> Collect(VbaSyntaxTree tree, string uri)
    {
        var diagnostics = new List<VbaValidationDiagnostic>();
        foreach (var declaration in tree.Module.CallableDeclarations)
        {
            if (declaration.Parameters.Count < 2)
            {
                continue;
            }

            AddDuplicateCallableParameterDiagnostics(
                diagnostics,
                declaration.Parameters.Select(parameter => new NamedSyntax(parameter.Name, parameter.Range)));
        }

        foreach (var declaration in tree.Module.Declarations.Where(declaration => declaration.Kind == VbaDeclarationKind.Event))
        {
            var parameters = tree.Module.Declarations
                .Where(parameter => parameter.Kind == VbaDeclarationKind.Parameter
                    && parameter.ParentProcedureName is null
                    && parameter.LineIndex == declaration.LineIndex)
                .ToArray();
            if (parameters.Length < 2)
            {
                continue;
            }

            AddDuplicateCallableParameterDiagnostics(
                diagnostics,
                parameters.Select(parameter => new NamedSyntax(parameter.Name, parameter.Range)));
        }

        foreach (var argumentList in tree.Module.ArgumentLists)
        {
            if (argumentList.Arguments.Count < 2)
            {
                continue;
            }

            AddDuplicateNamedCallArgumentDiagnostics(
                diagnostics,
                argumentList.Arguments
                    .Where(argument => argument.Kind == VbaArgumentKind.Named && argument.Name is not null)
                    .Select(argument => new NamedSyntax(argument.Name!, argument.NameRange ?? argument.Range)));
            AddPositionalAfterNamedCallArgumentDiagnostics(diagnostics, argumentList.Arguments);
        }

        return diagnostics;
    }

    private static void AddDuplicateCallableParameterDiagnostics(
        ICollection<VbaValidationDiagnostic> diagnostics,
        IEnumerable<NamedSyntax> parameters)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters)
        {
            if (seen.Add(parameter.Name))
            {
                continue;
            }

            diagnostics.Add(new VbaValidationDiagnostic(
                "validation.duplicateCallableParameterName",
                $"Duplicate callable parameter name '{parameter.Name}'.",
                ToDiagnosticRange(parameter.Range)));
        }
    }

    private static void AddDuplicateNamedCallArgumentDiagnostics(
        ICollection<VbaValidationDiagnostic> diagnostics,
        IEnumerable<NamedSyntax> arguments)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in arguments)
        {
            if (seen.Add(argument.Name))
            {
                continue;
            }

            diagnostics.Add(new VbaValidationDiagnostic(
                "validation.duplicateNamedCallArgument",
                $"Duplicate named call argument '{argument.Name}'.",
                ToDiagnosticRange(argument.Range)));
        }
    }

    private static void AddPositionalAfterNamedCallArgumentDiagnostics(
        ICollection<VbaValidationDiagnostic> diagnostics,
        IEnumerable<VbaArgumentSyntax> arguments)
    {
        var hasNamedArgument = false;
        foreach (var argument in arguments)
        {
            if (argument.Kind == VbaArgumentKind.Named)
            {
                hasNamedArgument = true;
                continue;
            }

            if (!hasNamedArgument)
            {
                continue;
            }

            diagnostics.Add(new VbaValidationDiagnostic(
                "validation.positionalCallArgumentAfterNamed",
                "Positional call argument cannot appear after a named argument.",
                ToDiagnosticRange(argument.ValueRange ?? argument.Range)));
        }
    }

    private static VbaRange ToDiagnosticRange(VbaSyntaxRange range)
        => new(
            new VbaPosition(range.Start.Line, range.Start.Character),
            new VbaPosition(range.End.Line, range.End.Character));

    private sealed record NamedSyntax(string Name, VbaSyntaxRange Range);
}
