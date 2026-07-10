using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.Diagnostics;

public sealed record VbaPosition(int Line, int Character);

public sealed record VbaRange(VbaPosition Start, VbaPosition End);

public sealed record VbaDiagnostic(
    string Code,
    string Message,
    VbaRange Range,
    string Severity = "error",
    string Source = "vba-language-server");

public sealed record VbaSyntaxDiagnostic(
    string Code,
    string Message,
    VbaRange Range,
    string Severity = "error",
    string Source = "vba-language-server");

public sealed record VbaValidationDiagnostic(
    string Code,
    string Message,
    VbaRange Range,
    string Severity = "error",
    string Source = "vba-language-server");

public static class VbaSyntaxDiagnostics
{
    public static IReadOnlyList<VbaSyntaxDiagnostic> Collect(string source, string fileName)
    {
        var tree = VbaSyntaxTree.ParseModule(fileName, source);
        return VbaSyntaxDiagnosticCollector.Collect(tree, fileName);
    }
}

public static class VbaDocumentDiagnostics
{
    public static IReadOnlyList<VbaDiagnostic> Collect(string source, string uri)
    {
        var tree = VbaSyntaxTree.ParseModule(uri, source);
        return VbaSyntaxDiagnosticCollector.Collect(tree, uri)
            .Select(ToDocumentDiagnostic)
            .Concat(VbaDocumentValidationDiagnosticCollector.Collect(tree, uri).Select(ToDocumentDiagnostic))
            .ToArray();
    }

    private static VbaDiagnostic ToDocumentDiagnostic(VbaSyntaxDiagnostic diagnostic)
        => new(diagnostic.Code, diagnostic.Message, diagnostic.Range, diagnostic.Severity, diagnostic.Source);

    private static VbaDiagnostic ToDocumentDiagnostic(VbaValidationDiagnostic diagnostic)
        => new(diagnostic.Code, diagnostic.Message, diagnostic.Range, diagnostic.Severity, diagnostic.Source);
}

public static class VbaSyntaxDiagnosticCollector
{
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

public static class VbaDocumentValidationDiagnosticCollector
{
    public static IReadOnlyList<VbaValidationDiagnostic> Collect(VbaSyntaxTree tree, string uri)
    {
        var diagnostics = new List<VbaValidationDiagnostic>();
        foreach (var declaration in tree.Module.CallableDeclarations)
        {
            AddDuplicateCallableParameterDiagnostics(
                diagnostics,
                declaration.Parameters.Select(parameter => new NamedSyntax(parameter.Name, parameter.Range)));
        }

        foreach (var declaration in tree.Module.Declarations.Where(declaration => declaration.Kind == VbaDeclarationKind.Event))
        {
            AddDuplicateCallableParameterDiagnostics(
                diagnostics,
                tree.Module.Declarations
                    .Where(parameter => parameter.Kind == VbaDeclarationKind.Parameter
                        && parameter.ParentProcedureName is null
                        && parameter.LineIndex == declaration.LineIndex)
                    .Select(parameter => new NamedSyntax(parameter.Name, parameter.Range)));
        }

        foreach (var argumentList in tree.Module.ArgumentLists)
        {
            AddDuplicateNamedCallArgumentDiagnostics(
                diagnostics,
                argumentList.Arguments
                    .Where(argument => argument.Kind == VbaArgumentKind.Named && argument.Name is not null)
                    .Select(argument => new NamedSyntax(argument.Name!, argument.NameRange ?? argument.Range)));
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

    private static VbaRange ToDiagnosticRange(VbaSyntaxRange range)
        => new(
            new VbaPosition(range.Start.Line, range.Start.Character),
            new VbaPosition(range.End.Line, range.End.Character));

    private sealed record NamedSyntax(string Name, VbaSyntaxRange Range);
}
