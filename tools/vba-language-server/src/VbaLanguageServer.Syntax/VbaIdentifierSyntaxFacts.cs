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
    /// <returns>True when the exact token is an MS-VBAL IDENTIFIER within the VBA name limit.</returns>
    public static bool IsValidDeclaredName(VbaToken token)
        => token.Kind is VbaTokenKind.Identifier or VbaTokenKind.Keyword
            && IsValidDeclaredName(token.Text);

    /// <summary>
    /// Determines whether an exact string can be a declared VBA name.
    /// </summary>
    public static bool IsValidDeclaredName(string value)
        => value.Length is > 0 and <= MaximumDeclaredNameLength
            && VbaIdentifier.IsIdentifier(value);
}
