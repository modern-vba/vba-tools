using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Describes which call arguments remain valid before editor projection.
/// </summary>
/// <param name="CallableDefinition">The resolved callable definition, when available.</param>
/// <param name="Signature">The resolved callable signature, when available.</param>
/// <param name="AllowsPositionalExpression">Whether the active argument can be a positional expression.</param>
/// <param name="RemainingNamedParameters">
/// The source parameters that can still be supplied by name for a CallArgument expectation.
/// A NamedArgumentValue expectation uses expression candidates instead and does not consume its active argument.
/// </param>
internal sealed record VbaCallArgumentAvailability(
    VbaSourceDefinition? CallableDefinition,
    VbaCallableSignature? Signature,
    bool AllowsPositionalExpression,
    IReadOnlyList<VbaCallableParameter> RemainingNamedParameters)
{
    public static VbaCallArgumentAvailability None { get; } = new(null, null, false, []);
}

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

    /// <summary>
    /// Gets the positional and named argument forms still available at the active argument.
    /// </summary>
    public VbaCallArgumentAvailability GetCallArgumentAvailability(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaPositionSyntax positionSyntax)
    {
        var callSite = positionSyntax.CallSite;
        if (callSite is null)
        {
            return VbaCallArgumentAvailability.None;
        }

        if (!TryResolveCallableTarget(
                currentDocument,
                line,
                character,
                callSite,
                positionSyntax.EnclosingWithScopes,
                out var definition)
            || definition is null)
        {
            var hasPriorNamedArgument = GetPriorArguments(callSite)
                .Any(argument => argument.Name is not null);
            return new VbaCallArgumentAvailability(
                null,
                null,
                !hasPriorNamedArgument,
                []);
        }

        var signature = definition.Signature;
        if (signature is null)
        {
            // A resolved non-callable, array/default-member ambiguity, or callable without
            // signature metadata must not inherit the permissive unresolved-name behavior.
            return new VbaCallArgumentAvailability(definition, null, false, []);
        }

        return AnalyzeResolvedArguments(definition, signature, callSite);
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

    private static VbaCallArgumentAvailability AnalyzeResolvedArguments(
        VbaSourceDefinition definition,
        VbaCallableSignature signature,
        VbaCallSiteSyntax callSite)
    {
        var parameters = signature.Parameters;
        var consumedParameters = new bool[parameters.Count];
        var suppliedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nextPositionalParameter = 0;
        var hasNamedArgument = false;

        if (!HasValidParameterShape(parameters))
        {
            return Invalid(definition, signature);
        }

        foreach (var argument in GetPriorArguments(callSite))
        {
            if (argument.Name is not null)
            {
                hasNamedArgument = true;
                if (!suppliedNames.Add(argument.Name))
                {
                    return Invalid(definition, signature);
                }

                var parameterIndex = FindParameter(parameters, argument.Name);
                if (parameterIndex < 0
                    || parameters[parameterIndex].IsParamArray
                    || consumedParameters[parameterIndex])
                {
                    return Invalid(definition, signature);
                }

                consumedParameters[parameterIndex] = true;
                continue;
            }

            if (hasNamedArgument || nextPositionalParameter >= parameters.Count)
            {
                return Invalid(definition, signature);
            }

            var parameter = parameters[nextPositionalParameter];
            if (argument.IsOmitted && (!parameter.IsOptional || parameter.IsParamArray))
            {
                return Invalid(definition, signature);
            }

            consumedParameters[nextPositionalParameter] = true;
            if (!parameter.IsParamArray)
            {
                nextPositionalParameter++;
            }
        }

        var allowsPositionalExpression = !hasNamedArgument
            && nextPositionalParameter < parameters.Count;
        var canOfferNamedArguments = definition.Identity.Origin == VbaDefinitionOrigin.Source
            && definition.Kind != VbaSourceDefinitionKind.Event
            && signature.CallableKind != VbaCallableKind.Event;
        var remainingNamedParameters = canOfferNamedArguments
            ? parameters
                .Where((parameter, index) => !parameter.IsParamArray && !consumedParameters[index])
                .ToArray()
            : [];
        return new VbaCallArgumentAvailability(
            definition,
            signature,
            allowsPositionalExpression,
            remainingNamedParameters);
    }

    private static IReadOnlyList<VbaCallArgumentSyntax> GetPriorArguments(
        VbaCallSiteSyntax callSite)
    {
        var count = Math.Clamp(callSite.ActiveArgumentIndex, 0, callSite.Arguments.Count);
        return callSite.Arguments.Take(count).ToArray();
    }

    private static bool HasValidParameterShape(IReadOnlyList<VbaCallableParameter> parameters)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index];
            if (!names.Add(parameter.Name)
                || (parameter.IsParamArray && index != parameters.Count - 1))
            {
                return false;
            }
        }

        return true;
    }

    private static int FindParameter(
        IReadOnlyList<VbaCallableParameter> parameters,
        string name)
    {
        for (var index = 0; index < parameters.Count; index++)
        {
            if (parameters[index].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static VbaCallArgumentAvailability Invalid(
        VbaSourceDefinition definition,
        VbaCallableSignature signature)
        => new(definition, signature, false, []);
}
