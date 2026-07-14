using System.Text.RegularExpressions;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Resolves callable targets and active arguments for signature-help call sites.
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
        VbaSyntaxTree syntaxTree)
    {
        if (!TryResolveCallableTarget(
                currentDocument,
                line,
                character,
                syntaxTree,
                out var definition,
                out var arguments)
            || definition?.Signature is null)
        {
            return null;
        }

        return new VbaSignatureHelp(
            definition.Signature,
            GetActiveSignatureParameter(definition.Signature, arguments));
    }

    private bool TryResolveCallableTarget(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaSyntaxTree syntaxTree,
        out VbaSourceDefinition? definition,
        out string arguments)
    {
        if (syntaxTree.TryGetCallExpressionContext(line, character, out var context))
        {
            return TryResolveCallExpression(
                currentDocument,
                line,
                character,
                context,
                out definition,
                out arguments);
        }

        definition = null;
        arguments = "";
        return false;
    }

    private bool TryResolveCallExpression(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaCallExpressionContext context,
        out VbaSourceDefinition? definition,
        out string arguments)
    {
        arguments = context.Arguments;
        if (context.MemberChain is not null
            && memberChainResolution.TryResolveMemberChainDefinition(
                currentDocument,
                line,
                character,
                context.MemberChain,
                out definition))
        {
            return true;
        }

        definition = nameResolution.Resolve(
            currentDocument.Uri,
            new VbaPosition(line, character),
            context.Qualifier,
            context.UnqualifiedName);
        return true;
    }

    private static int GetActiveSignatureParameter(VbaCallableSignature signature, string arguments)
    {
        var fallbackParameter = Math.Min(
            arguments.Count(characterValue => characterValue == ','),
            Math.Max(0, signature.Parameters.Count - 1));
        var currentArgumentStart = arguments.LastIndexOf(',') + 1;
        var currentArgument = arguments[currentArgumentStart..];
        var namedArgumentMatch = Regex.Match(
            currentArgument,
            "^\\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\\s*:=",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!namedArgumentMatch.Success)
        {
            return fallbackParameter;
        }

        var parameterIndex = signature.Parameters
            .Select((parameter, index) => new { parameter, index })
            .FirstOrDefault(item => item.parameter.Name.Equals(
                namedArgumentMatch.Groups["name"].Value,
                StringComparison.OrdinalIgnoreCase))
            ?.index;
        return parameterIndex ?? fallbackParameter;
    }
}
