using VbaDev.App.HostClasses;
using VbaLanguageServer.Syntax;

namespace VbaDev.Infrastructure.Workbooks;

internal static class HostClassGeneratedSignatureParser
{
    private static readonly IReadOnlyDictionary<string, string> IntrinsicTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Boolean"] = "Boolean",
            ["Byte"] = "Byte",
            ["Currency"] = "Currency",
            ["Date"] = "Date",
            ["Double"] = "Double",
            ["Integer"] = "Integer",
            ["Long"] = "Long",
            ["LongLong"] = "LongLong",
            ["LongPtr"] = "LongPtr",
            ["Object"] = "Object",
            ["Single"] = "Single",
            ["String"] = "String",
            ["Variant"] = "Variant"
        };

    public static HostEventSignature Parse(
        string eventName,
        string procedureName,
        string generatedSource,
        bool authoringAvailable,
        bool existingHandlerRecognizable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentException.ThrowIfNullOrWhiteSpace(procedureName);
        ArgumentNullException.ThrowIfNull(generatedSource);
        var tree = VbaSyntaxTree.ParseModule(
            $"vba-dev://host-class/{Uri.EscapeDataString(procedureName)}",
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
            .Select(parameter => new HostEventParameter(
                parameter.Name,
                CreateTypeReference(parameter.TypeReference),
                parameter.IsByRef
                    ? HostEventPassingMechanism.ByRef
                    : HostEventPassingMechanism.ByVal,
                parameter.IsArray
                    ? HostEventArrayShape.Array
                    : HostEventArrayShape.Scalar,
                parameter.IsOptional,
                parameter.IsParamArray))
            .ToArray();
        return new HostEventSignature(
            eventName,
            parameters,
            declaration.Documentation,
            authoringAvailable,
            existingHandlerRecognizable);
    }

    private static HostEventTypeReference CreateTypeReference(
        VbaTypeReferenceSyntax? typeReference)
    {
        if (typeReference is null)
        {
            return new IntrinsicHostEventTypeReference("Variant");
        }

        if (typeReference.Qualifier is null &&
            IntrinsicTypes.TryGetValue(typeReference.Name, out var canonicalName))
        {
            return new IntrinsicHostEventTypeReference(canonicalName);
        }

        var displayName = string.IsNullOrWhiteSpace(typeReference.Qualifier)
            ? typeReference.Name
            : $"{typeReference.Qualifier}.{typeReference.Name}";
        return new UnresolvedHostEventTypeReference(displayName);
    }
}
