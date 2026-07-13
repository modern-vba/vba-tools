namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Identifies why a member-chain resolution attempt stopped.
/// </summary>
internal enum VbaMemberChainResolutionStopReason
{
    /// <summary>
    /// The member chain resolved to a type or member.
    /// </summary>
    Resolved,

    /// <summary>
    /// The receiver expression could not be resolved.
    /// </summary>
    UnresolvedReceiver,

    /// <summary>
    /// A member segment could not be resolved from the receiver type.
    /// </summary>
    UnresolvedMember
}

/// <summary>
/// Represents the result of resolving a receiver/member chain.
/// </summary>
/// <param name="ReceiverType">The resolved receiver type when available.</param>
/// <param name="Member">The final resolved member when the chain targets a member.</param>
/// <param name="ResultType">The resolved type produced by the expression when available.</param>
/// <param name="StopReason">The reason resolution stopped.</param>
/// <param name="Segments">The normalized member expression segments.</param>
internal sealed record VbaMemberChainResolutionResult(
    VbaResolvedType? ReceiverType,
    VbaSourceDefinition? Member,
    VbaResolvedType? ResultType,
    VbaMemberChainResolutionStopReason StopReason,
    IReadOnlyList<string> Segments);

/// <summary>
/// Resolves member chains while keeping receiver/member traversal separate from name and type lookup adapters.
/// </summary>
internal sealed class VbaMemberChainResolution
{
    private readonly VbaTypeResolution typeResolution;

    /// <summary>
    /// Creates a member-chain resolver.
    /// </summary>
    /// <param name="typeResolution">The type resolver used as the lookup adapter.</param>
    public VbaMemberChainResolution(VbaTypeResolution typeResolution)
    {
        this.typeResolution = typeResolution;
    }

    /// <summary>
    /// Resolves the type produced by a member expression.
    /// </summary>
    /// <param name="currentDocument">The document that owns the expression.</param>
    /// <param name="line">The zero-based line where resolution is requested.</param>
    /// <param name="character">The zero-based character where resolution is requested.</param>
    /// <param name="expression">The receiver or member expression.</param>
    /// <param name="resolvedType">The resolved result type.</param>
    /// <returns>True when the expression resolves to a type.</returns>
    public bool TryResolveExpressionType(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        string expression,
        out VbaResolvedType resolvedType)
        => typeResolution.TryResolveExpressionType(currentDocument, line, character, expression, out resolvedType);

    /// <summary>
    /// Resolves a receiver expression and returns its public member candidates.
    /// </summary>
    /// <param name="currentDocument">The document that owns the receiver expression.</param>
    /// <param name="line">The zero-based line where resolution is requested.</param>
    /// <param name="character">The zero-based character where resolution is requested.</param>
    /// <param name="receiverExpression">The receiver expression before the final dot.</param>
    /// <returns>The member candidates or an empty set when the receiver is unresolved.</returns>
    public IReadOnlyList<VbaSourceDefinition> GetMemberCompletions(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        string receiverExpression)
    {
        return TryResolveExpressionType(currentDocument, line, character, receiverExpression, out var receiverType)
            ? GetMembersOfType(receiverType)
            : [];
    }

    /// <summary>
    /// Resolves a member-chain completion context and returns its public member candidates.
    /// </summary>
    /// <param name="currentDocument">The document that owns the receiver expression.</param>
    /// <param name="line">The zero-based line where resolution is requested.</param>
    /// <param name="character">The zero-based character where resolution is requested.</param>
    /// <param name="context">The parsed member-chain context.</param>
    /// <returns>The member candidates or an empty set when the receiver is unresolved.</returns>
    public IReadOnlyList<VbaSourceDefinition> GetMemberCompletions(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaMemberChainContext context)
        => GetMemberCompletions(currentDocument, line, character, context.ReceiverExpression);

    /// <summary>
    /// Gets visible members for a resolved receiver type.
    /// </summary>
    /// <param name="resolvedType">The resolved receiver type.</param>
    /// <returns>The visible member definitions.</returns>
    public IReadOnlyList<VbaSourceDefinition> GetMembersOfType(VbaResolvedType resolvedType)
        => typeResolution.GetMembersOfType(resolvedType);

    /// <summary>
    /// Resolves a named member from a receiver type.
    /// </summary>
    /// <param name="resolvedType">The resolved receiver type.</param>
    /// <param name="memberName">The member name to resolve.</param>
    /// <returns>The resolved member, or null when unresolved or ambiguous.</returns>
    public VbaSourceDefinition? ResolveMember(VbaResolvedType resolvedType, string memberName)
        => typeResolution.ResolveMember(resolvedType, memberName);

    /// <summary>
    /// Resolves a named event from a receiver type.
    /// </summary>
    /// <param name="resolvedType">The resolved receiver type.</param>
    /// <param name="eventName">The event name to resolve.</param>
    /// <returns>The resolved event, or null when unresolved or ambiguous.</returns>
    public VbaSourceDefinition? ResolveEvent(VbaResolvedType resolvedType, string eventName)
        => typeResolution.ResolveEvent(resolvedType, eventName);

    /// <summary>
    /// Resolves the member at the end of a receiver expression and member name pair.
    /// </summary>
    /// <param name="currentDocument">The document that owns the member chain.</param>
    /// <param name="line">The zero-based line where resolution is requested.</param>
    /// <param name="character">The zero-based character where resolution is requested.</param>
    /// <param name="receiverExpression">The receiver expression before the final member.</param>
    /// <param name="memberName">The final member name.</param>
    /// <returns>The member-chain resolution result.</returns>
    public VbaMemberChainResolutionResult ResolveMemberChain(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        string receiverExpression,
        string memberName)
    {
        var segments = VbaMemberExpressionSyntax
            .NormalizeMemberExpression($"{receiverExpression}.{memberName}")
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!TryResolveExpressionType(currentDocument, line, character, receiverExpression, out var receiverType))
        {
            return new VbaMemberChainResolutionResult(
                null,
                null,
                null,
                VbaMemberChainResolutionStopReason.UnresolvedReceiver,
                segments);
        }

        var member = ResolveMember(receiverType, memberName);
        return new VbaMemberChainResolutionResult(
            receiverType,
            member,
            member?.TypeReference is not null
                && typeResolution.TryResolveTypeReference(currentDocument, member.TypeReference, out var resultType)
                    ? resultType
                    : null,
            member is null
                ? VbaMemberChainResolutionStopReason.UnresolvedMember
                : VbaMemberChainResolutionStopReason.Resolved,
            segments);
    }

    /// <summary>
    /// Resolves the member at the end of a parsed member-chain context.
    /// </summary>
    /// <param name="currentDocument">The document that owns the member chain.</param>
    /// <param name="line">The zero-based line where resolution is requested.</param>
    /// <param name="character">The zero-based character where resolution is requested.</param>
    /// <param name="context">The parsed member-chain context.</param>
    /// <returns>The member-chain resolution result.</returns>
    public VbaMemberChainResolutionResult ResolveMemberChain(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaMemberChainContext context)
    {
        if (context.MemberName is null)
        {
            return new VbaMemberChainResolutionResult(
                null,
                null,
                null,
                VbaMemberChainResolutionStopReason.UnresolvedMember,
                context.Segments);
        }

        return ResolveMemberChain(
            currentDocument,
            line,
            character,
            context.ReceiverExpression,
            context.MemberName);
    }
}
