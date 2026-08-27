using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

internal enum VbaContractCompletionDomain
{
    HostEvents,
    WithEvents,
    Interface
}

internal sealed record VbaContractMemberCompletionOrigin(
    string Name,
    VbaContractCompletionDomain Domain,
    bool IsConditionalContract,
    VbaCallableSignature? Signature = null,
    string? Documentation = null,
    object? Identity = null);

internal sealed record VbaContractPrefixCompletionOrigin(
    string Prefix,
    VbaContractCompletionDomain Domain,
    bool IsConditionalPrefix,
    IReadOnlyList<VbaContractMemberCompletionOrigin> Members);

internal static class VbaContractDeclarationNameCompletion
{
    public static IEnumerable<VbaCompletionCandidate> CreateCandidates(
        VbaCallableDeclarationNameSyntax declarationName,
        IEnumerable<VbaContractPrefixCompletionOrigin> candidateOrigins,
        VbaProspectiveDeclaration prospectiveDeclaration,
        IReadOnlyList<VbaSourceDefinition> declarations)
    {
        var originGroups = candidateOrigins
            .Select(origin => origin with
            {
                Members = origin.Members
                    .Where(member =>
                        VbaDeclarationRelationshipPolicy
                            .IsProspectiveDeclarationAvailable(
                                prospectiveDeclaration,
                                origin.Prefix + member.Name,
                                declarations))
                    .ToArray()
            })
            .Where(origin => origin.Members.Count > 0)
            .GroupBy(origin => origin.Prefix, StringComparer.OrdinalIgnoreCase)
            .Select(group => new OriginGroup(
                SelectCanonicalSpelling(group.Select(origin => origin.Prefix)),
                group.ToArray()))
            .ToArray();
        var memberGroup = originGroups
            .Where(group => declarationName.Fragment.StartsWith(
                group.Prefix,
                StringComparison.OrdinalIgnoreCase))
            .Where(group => HasMatchingMember(declarationName, group))
            .OrderByDescending(group => group.Prefix.Equals(
                declarationName.Fragment,
                StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(group => group.Prefix.Length)
            .ThenBy(group => group.Prefix, StringComparer.Ordinal)
            .FirstOrDefault();
        if (memberGroup is not null)
        {
            return CreateMemberCandidates(declarationName, memberGroup);
        }

        return originGroups
            .Where(group => group.Prefix.StartsWith(
                declarationName.Fragment,
                StringComparison.OrdinalIgnoreCase))
            .Select(CreatePrefixCandidate);
    }

    private static bool HasMatchingMember(
        VbaCallableDeclarationNameSyntax declarationName,
        OriginGroup group)
    {
        var suffixFragment = declarationName.Fragment[group.Prefix.Length..];
        return group.Origins
            .SelectMany(origin => origin.Members)
            .Any(member => member.Name.StartsWith(
                suffixFragment,
                StringComparison.OrdinalIgnoreCase));
    }

    private static VbaCompletionCandidate CreatePrefixCandidate(
        OriginGroup group)
    {
        var domains = group.Origins
            .Select(origin => origin.Domain)
            .Distinct()
            .ToArray();
        var detail = domains.Length == 1
            ? domains[0] switch
            {
                VbaContractCompletionDomain.HostEvents => "Host Events",
                VbaContractCompletionDomain.WithEvents => "WithEvents",
                VbaContractCompletionDomain.Interface => "Interface",
                _ => throw new InvalidOperationException("Unsupported contract domain.")
            }
            : "Multiple Contracts";
        if (group.Origins.All(origin => origin.IsConditionalPrefix))
        {
            detail += " [#If]";
        }

        return new VbaCompletionCandidate(
            group.Prefix,
            VbaCompletionCandidateKind.ContractPrefix,
            FilterText: group.Prefix)
        {
            Detail = detail,
            RetriggerCompletion = true
        };
    }

    private static IEnumerable<VbaCompletionCandidate> CreateMemberCandidates(
        VbaCallableDeclarationNameSyntax declarationName,
        OriginGroup group)
    {
        var writtenPrefix = declarationName.Fragment[..group.Prefix.Length];
        var suffixFragment = declarationName.Fragment[group.Prefix.Length..];
        var suffixStart = new VbaPosition(
            declarationName.FragmentRange.Start.Line,
            declarationName.FragmentRange.Start.Character + group.Prefix.Length);
        var suffixRange = new VbaRange(
            suffixStart,
            new VbaPosition(
                declarationName.FragmentRange.End.Line,
                declarationName.FragmentRange.End.Character));
        return group.Origins
            .SelectMany(origin => origin.Members)
            .Where(member => member.Name.StartsWith(
                suffixFragment,
                StringComparison.OrdinalIgnoreCase))
            .GroupBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
            .Select(members =>
            {
                var memberOrigins = members.ToArray();
                var memberName = SelectCanonicalSpelling(
                    memberOrigins.Select(member => member.Name));
                var domains = memberOrigins
                    .Select(member => member.Domain)
                    .Distinct()
                    .ToArray();
                var detail = domains.All(domain => domain is
                        VbaContractCompletionDomain.HostEvents
                            or VbaContractCompletionDomain.WithEvents)
                    ? "Event"
                    : domains.All(domain => domain
                        == VbaContractCompletionDomain.Interface)
                        ? "Interface Member"
                        : "Multiple Contracts";
                if (memberOrigins.Any(member => member.IsConditionalContract))
                {
                    detail += " [#If]";
                }

                return new VbaCompletionCandidate(
                    writtenPrefix + memberName,
                    VbaCompletionCandidateKind.ContractMemberName,
                    FilterText: memberName,
                    TextEdit: new VbaTextEdit(suffixRange, memberName))
                {
                    Detail = detail,
                    SignaturePresentations =
                        CreateSignaturePresentations(memberOrigins)
                };
            });
    }

    private static IReadOnlyList<VbaCompletionSignaturePresentation>
        CreateSignaturePresentations(
            IReadOnlyList<VbaContractMemberCompletionOrigin> origins)
        => origins
            .Where(origin => origin.Signature is not null)
            .GroupBy(origin => new SignaturePresentationIdentity(
                origin.Signature!.Label,
                origin.IsConditionalContract))
            .Select(group => new VbaCompletionSignaturePresentation(
                group.Key.Label,
                group.Key.IsConditional,
                group
                    .Select(origin => origin.Documentation
                        ?? origin.Signature?.Documentation)
                    .Where(documentation =>
                        !string.IsNullOrWhiteSpace(documentation))
                    .Select(documentation => documentation!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();

    private static string SelectCanonicalSpelling(IEnumerable<string> spellings)
        => spellings
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .First();

    private sealed record OriginGroup(
        string Prefix,
        IReadOnlyList<VbaContractPrefixCompletionOrigin> Origins);

    private sealed record SignaturePresentationIdentity(
        string Label,
        bool IsConditional);
}
