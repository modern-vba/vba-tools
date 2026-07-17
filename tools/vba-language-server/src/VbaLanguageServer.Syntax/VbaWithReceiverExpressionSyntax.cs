namespace VbaLanguageServer.Syntax;

/// <summary>
/// Validates a complete expression whose visible root is safe for a With receiver.
/// </summary>
internal static class VbaWithReceiverExpressionSyntax
{
    public static bool IsComplete(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end,
        VbaModuleKind moduleKind,
        bool allowLeadingMemberAccess)
        => VbaPotentialReferenceExpressionSyntax.IsComplete(
            tokens,
            start,
            end,
            moduleKind,
            allowLeadingMemberAccess);
}
