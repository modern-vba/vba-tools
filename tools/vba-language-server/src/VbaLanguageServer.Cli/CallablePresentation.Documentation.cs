namespace VbaLanguageServer.SourceModel;

internal static partial class VbaCallablePresentation
{
    /// <summary>
    /// Composes one declaration with its complete, ordered documentation group.
    /// Signature Help uses native parameter documentation instead of this block.
    /// </summary>
    public static string ComposeDocumentation(
        string? declaration,
        IEnumerable<string?> documentationVariants)
    {
        var documents = documentationVariants
            .Where(document => !string.IsNullOrWhiteSpace(document))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var documentation = documents.Length switch
        {
            0 => string.Empty,
            1 => documents[0]!,
            _ => "**Documentation variants**\n\n"
                + string.Join("\n\n", documents.Select(
                    (document, index) => $"{index + 1}. {document}"))
        };
        if (string.IsNullOrWhiteSpace(declaration))
        {
            return documentation;
        }

        var block = $"```vba\n{declaration}\n```";
        return documentation.Length == 0
            ? block
            : $"{documentation}\n\n---\n\n{block}";
    }
}
