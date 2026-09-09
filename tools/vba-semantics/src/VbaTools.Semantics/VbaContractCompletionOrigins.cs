namespace VbaTools.Semantics;

internal enum VbaContractCompletionDomain
{
    HostEvents,
    WithEvents,
    Interface,
    ClassLifecycle
}

internal sealed record VbaContractMemberCompletionOrigin(
    string Name,
    VbaContractCompletionDomain Domain,
    bool IsConditionalContract,
    VbaCallableSignature? Signature = null,
    string? Documentation = null,
    object? Identity = null,
    string? CanonicalLabel = null);

internal sealed record VbaContractPrefixCompletionOrigin(
    string Prefix,
    VbaContractCompletionDomain Domain,
    bool IsConditionalPrefix,
    IReadOnlyList<VbaContractMemberCompletionOrigin> Members);
