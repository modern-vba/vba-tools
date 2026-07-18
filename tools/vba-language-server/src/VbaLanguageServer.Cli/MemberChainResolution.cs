using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Identifies why a member-chain resolution attempt stopped.
/// </summary>
internal enum VbaMemberChainResolutionStopReason
{
    Resolved,
    UnresolvedReceiver,
    UnresolvedMember
}

/// <summary>
/// Represents the result of resolving a receiver/member chain.
/// </summary>
internal sealed record VbaMemberChainResolutionResult(
    VbaResolvedType? ReceiverType,
    VbaSourceDefinition? Member,
    VbaResolvedType? ResultType,
    VbaMemberChainResolutionStopReason StopReason,
    IReadOnlyList<string> Segments);

/// <summary>
/// Resolves structured member chains while keeping syntax recognition outside semantic lookup.
/// </summary>
internal sealed class VbaMemberChainResolution
{
    private readonly VbaTypeResolution typeResolution;

    public VbaMemberChainResolution(VbaTypeResolution typeResolution)
    {
        this.typeResolution = typeResolution;
    }

    public IReadOnlyList<VbaSourceDefinition> GetMemberCompletions(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaMemberAccessSyntax access,
        IReadOnlyList<VbaWithScopeSyntax> withScopes)
    {
        var receiverSegments = access.Segments
            .Take(Math.Clamp(access.TargetSegmentIndex, 0, access.Segments.Count))
            .ToArray();
        return typeResolution.TryResolveExpressionType(
            currentDocument,
            line,
            character,
            receiverSegments,
            access.IsLeadingDot,
            withScopes,
            out var receiverType)
                ? GetMembersOfType(currentDocument, receiverType)
                : [];
    }

    public IReadOnlyList<VbaSourceDefinition> GetMembersOfType(
        VbaSourceDocument currentDocument,
        VbaResolvedType resolvedType)
        => typeResolution.GetMembersOfType(currentDocument, resolvedType);

    public VbaSourceDefinition? ResolveMember(
        VbaSourceDocument currentDocument,
        VbaResolvedType resolvedType,
        string memberName)
        => typeResolution.ResolveMember(currentDocument, resolvedType, memberName);

    public VbaSourceDefinition? ResolveEvent(
        VbaSourceDocument currentDocument,
        VbaResolvedType resolvedType,
        string eventName)
        => typeResolution.ResolveEvent(currentDocument, resolvedType, eventName);

    public VbaMemberChainResolutionResult ResolveMemberChain(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaMemberAccessSyntax access,
        IReadOnlyList<VbaWithScopeSyntax> withScopes)
    {
        var target = access.Target;
        var segments = access.Segments.Select(segment => segment.Name).ToArray();
        if (target is null)
        {
            return new VbaMemberChainResolutionResult(
                null,
                null,
                null,
                VbaMemberChainResolutionStopReason.UnresolvedMember,
                segments);
        }

        var receiverSegments = access.Segments
            .Take(Math.Clamp(access.TargetSegmentIndex, 0, access.Segments.Count))
            .ToArray();
        if (!typeResolution.TryResolveExpressionType(
            currentDocument,
            line,
            character,
            receiverSegments,
            access.IsLeadingDot,
            withScopes,
            out var receiverType))
        {
            return new VbaMemberChainResolutionResult(
                null,
                null,
                null,
                VbaMemberChainResolutionStopReason.UnresolvedReceiver,
                segments);
        }

        var member = ResolveMember(currentDocument, receiverType, target.Name);
        return new VbaMemberChainResolutionResult(
            receiverType,
            member,
            member is not null
                && typeResolution.TryResolveDefinitionTypeReference(
                    currentDocument,
                    member,
                    out var resultType)
                    ? resultType
                    : null,
            member is null
                ? VbaMemberChainResolutionStopReason.UnresolvedMember
                : VbaMemberChainResolutionStopReason.Resolved,
            segments);
    }

    public bool TryResolveMemberChainDefinition(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaMemberAccessSyntax access,
        IReadOnlyList<VbaWithScopeSyntax> withScopes,
        out VbaSourceDefinition? definition)
    {
        definition = null;
        var result = ResolveMemberChain(
            currentDocument,
            line,
            character,
            access,
            withScopes);
        if (result.StopReason == VbaMemberChainResolutionStopReason.UnresolvedReceiver
            && !access.IsLeadingDot)
        {
            return false;
        }

        definition = result.Member;
        return true;
    }

    public bool TryGetCanonicalMemberName(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaMemberAccessSyntax access,
        IReadOnlyList<VbaWithScopeSyntax> withScopes,
        out string? canonicalName)
    {
        canonicalName = null;
        if (!TryResolveMemberChainDefinition(
            currentDocument,
            line,
            character,
            access,
            withScopes,
            out var definition))
        {
            return false;
        }

        canonicalName = definition?.Name;
        return canonicalName is not null;
    }
}
