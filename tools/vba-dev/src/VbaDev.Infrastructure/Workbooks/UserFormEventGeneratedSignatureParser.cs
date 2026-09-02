using VbaLanguageServer.Syntax;

namespace VbaDev.Infrastructure.Workbooks;

internal static class UserFormEventGeneratedSignatureParser
{
    public static UserFormEventObservation Parse(
        string eventName,
        string procedureName,
        string generatedSource,
        bool authoringAvailable,
        bool existingHandlerRecognizable)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        ArgumentNullException.ThrowIfNull(procedureName);
        if (!VbaIdentifier.IsIdentifier(procedureName))
        {
            throw new ArgumentException(
                "The generated procedure name must be an exact VBA identifier.",
                nameof(procedureName));
        }

        ArgumentNullException.ThrowIfNull(generatedSource);
        var tree = VbaSyntaxTree.ParseModule(
            $"vba-dev://host-event/{Uri.EscapeDataString(procedureName)}",
            generatedSource);
        if (tree.Diagnostics.Count != 0)
        {
            throw new InvalidOperationException(
                $"VBE generated an Event declaration with {tree.Diagnostics.Count} syntax diagnostic(s).");
        }

        var declarations = tree.Module.CallableDeclarations
            .Where(declaration => declaration.Name.Equals(
                procedureName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (declarations.Length != 1 ||
            !string.Equals(
                declarations[0].DeclarationKeyword,
                "Sub",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"VBE did not generate exactly one Sub declaration for Event '{eventName}'.");
        }

        var declaration = declarations[0];
        var parameters = declaration.Parameters
            .Select(parameter => new ObservedHostEventParameter(
                parameter.Name,
                CreateTypeReference(parameter.TypeReference),
                parameter.IsByRef
                    ? ObservedHostEventPassingMechanism.ByRef
                    : ObservedHostEventPassingMechanism.ByVal,
                parameter.IsArray
                    ? ObservedHostEventArrayShape.Array
                    : ObservedHostEventArrayShape.Scalar,
                parameter.IsOptional,
                parameter.IsParamArray))
            .ToArray();
        return new UserFormEventObservation(
            eventName,
            parameters,
            declaration.Documentation,
            authoringAvailable,
            existingHandlerRecognizable);
    }

    private static ObservedHostEventTypeReference CreateTypeReference(
        VbaTypeReferenceSyntax? typeReference)
    {
        if (typeReference is null)
        {
            return new ObservedIntrinsicHostEventTypeReference("Variant");
        }

        if (typeReference.Qualifier is null &&
            VbaLanguageVocabulary.TryGetCanonicalTypeName(
                typeReference.Name,
                out var canonicalName))
        {
            return new ObservedIntrinsicHostEventTypeReference(canonicalName);
        }

        var displayName = typeReference.Qualifier is null
            ? typeReference.Name
            : $"{typeReference.Qualifier}.{typeReference.Name}";
        return new ObservedUnresolvedHostEventTypeReference(displayName);
    }
}
