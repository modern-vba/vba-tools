using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Resolves callable targets and active arguments from structured position syntax.
/// </summary>
internal sealed class VbaCallSiteResolution
{
    private readonly VbaNameResolutionService nameResolution;
    private readonly VbaMemberChainResolution memberChainResolution;

    public VbaCallSiteResolution(
        VbaNameResolutionService nameResolution,
        VbaMemberChainResolution memberChainResolution)
    {
        this.nameResolution = nameResolution;
        this.memberChainResolution = memberChainResolution;
    }

    public VbaSignatureHelp? GetSignatureHelp(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaPositionSyntax positionSyntax)
    {
        var callSite = positionSyntax.CallSite;
        if (callSite is null
            || !TryResolveCallableTarget(
                currentDocument,
                line,
                character,
                callSite,
                positionSyntax.EnclosingWithScopes,
                out var definition)
            || definition?.Signature is null)
        {
            return null;
        }

        return new VbaSignatureHelp(
            definition.Signature,
            GetActiveSignatureParameter(definition.Signature, callSite));
    }

    private bool TryResolveCallableTarget(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaCallSiteSyntax callSite,
        IReadOnlyList<VbaWithScopeSyntax> withScopes,
        out VbaSourceDefinition? definition)
    {
        if ((callSite.Callee.IsLeadingDot || callSite.Callee.Segments.Count > 1)
            && memberChainResolution.TryResolveMemberChainDefinition(
                currentDocument,
                line,
                character,
                callSite.Callee,
                withScopes,
                out definition))
        {
            return true;
        }

        var target = callSite.Callee.Target;
        if (target is null)
        {
            definition = null;
            return false;
        }

        var qualifier = callSite.Callee.TargetSegmentIndex > 0
            ? string.Join(
                '.',
                callSite.Callee.Segments
                    .Take(callSite.Callee.TargetSegmentIndex)
                    .Select(segment => segment.Name))
            : null;
        definition = nameResolution.Resolve(
            currentDocument.Uri,
            new VbaPosition(line, character),
            qualifier,
            target.Name);
        return true;
    }

    private static int GetActiveSignatureParameter(
        VbaCallableSignature signature,
        VbaCallSiteSyntax callSite)
    {
        var fallbackParameter = Math.Min(
            callSite.ActiveArgumentIndex,
            Math.Max(0, signature.Parameters.Count - 1));
        if (callSite.ActiveNamedArgument is null)
        {
            return fallbackParameter;
        }

        var parameterIndex = signature.Parameters
            .Select((parameter, index) => new { parameter, index })
            .FirstOrDefault(item => item.parameter.Name.Equals(
                callSite.ActiveNamedArgument,
                StringComparison.OrdinalIgnoreCase))
            ?.index;
        return parameterIndex ?? fallbackParameter;
    }
}
