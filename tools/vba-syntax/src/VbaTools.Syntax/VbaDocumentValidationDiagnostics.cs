namespace VbaTools.Syntax;

/// <summary>
/// Represents parsed-source validity evidence that requires no project semantic state.
/// </summary>
/// <param name="Code">The stable diagnostic code.</param>
/// <param name="Message">The diagnostic message.</param>
/// <param name="Range">The original source range.</param>
/// <param name="Severity">The diagnostic severity value.</param>
public sealed record VbaDocumentValidationDiagnostic(
    string Code,
    string Message,
    VbaSyntaxRange Range,
    string Severity = "error");

/// <summary>
/// Collects document-local validation diagnostics from an already parsed source tree.
/// </summary>
public static class VbaDocumentValidationDiagnostics
{
    /// <summary>
    /// Collects parsed-source validity diagnostics without parsing or reading source again.
    /// </summary>
    /// <param name="tree">The captured syntax tree.</param>
    /// <returns>The ordered document-local validation diagnostics.</returns>
    public static IReadOnlyList<VbaDocumentValidationDiagnostic> Collect(VbaSyntaxTree tree)
    {
        var diagnostics = new List<VbaDocumentValidationDiagnostic>();
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
            var parameters = declaration.Signature?.Parameters ?? [];
            if (parameters.Count < 2)
            {
                continue;
            }

            AddDuplicateCallableParameterDiagnostics(
                diagnostics,
                parameters
                    .Where(parameter => parameter.Range is not null)
                    .Select(parameter => new NamedSyntax(parameter.Name, parameter.Range!)));
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

        return diagnostics.AsReadOnly();
    }

    private static void AddDuplicateCallableParameterDiagnostics(
        ICollection<VbaDocumentValidationDiagnostic> diagnostics,
        IEnumerable<NamedSyntax> parameters)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters)
        {
            if (seen.Add(parameter.Name))
            {
                continue;
            }

            diagnostics.Add(new VbaDocumentValidationDiagnostic(
                "validation.duplicateCallableParameterName",
                $"Duplicate callable parameter name '{parameter.Name}'.",
                parameter.Range));
        }
    }

    private static void AddDuplicateNamedCallArgumentDiagnostics(
        ICollection<VbaDocumentValidationDiagnostic> diagnostics,
        IEnumerable<NamedSyntax> arguments)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in arguments)
        {
            if (seen.Add(argument.Name))
            {
                continue;
            }

            diagnostics.Add(new VbaDocumentValidationDiagnostic(
                "validation.duplicateNamedCallArgument",
                $"Duplicate named call argument '{argument.Name}'.",
                argument.Range));
        }
    }

    private static void AddPositionalAfterNamedCallArgumentDiagnostics(
        ICollection<VbaDocumentValidationDiagnostic> diagnostics,
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

            diagnostics.Add(new VbaDocumentValidationDiagnostic(
                "validation.positionalCallArgumentAfterNamed",
                "Positional call argument cannot appear after a named argument.",
                argument.ValueRange ?? argument.Range));
        }
    }

    private sealed record NamedSyntax(string Name, VbaSyntaxRange Range);
}
