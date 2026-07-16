namespace VbaLanguageServer.Syntax;

/// <summary>
/// Provides strict VBA identifier facts shared by syntax recognizers.
/// </summary>
internal static class VbaIdentifierSyntaxFacts
{
    private const int MaximumDeclaredNameLength = 255;

    /// <summary>
    /// Determines whether a token can be a declared VBA name.
    /// </summary>
    /// <param name="token">The candidate identifier token.</param>
    /// <returns>True when the token begins with a letter and fits the VBA name limit.</returns>
    public static bool IsValidDeclaredName(VbaToken token)
        => token.Kind == VbaTokenKind.Identifier
            && token.Text.Length is > 0 and <= MaximumDeclaredNameLength
            && char.IsLetter(token.Text[0]);
}
