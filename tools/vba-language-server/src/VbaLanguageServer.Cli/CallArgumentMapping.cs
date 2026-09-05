using VbaTools.Syntax;

namespace VbaLanguageServer.SourceModel;

internal enum VbaCallContext
{
    StatementInvocation,
    ValueRead,
    PropertyLetAssignment,
    PropertySetAssignment,
    RaiseEvent,
    Indeterminate
}

internal enum VbaCallContextCompatibility
{
    Compatible,
    Incompatible,
    Indeterminate
}

internal enum VbaCallCompatibilityState
{
    Applicable,
    Inapplicable,
    Indeterminate
}

/// <summary>
/// Retains one source argument's structural mapping to a callable parameter.
/// </summary>
internal sealed record VbaMappedCallArgument(
    int SourceIndex,
    int? ParameterIndex,
    bool IsParamArrayElement);

internal enum VbaCallMappingMismatchKind
{
    RequiredArgumentOmitted,
    DuplicateParameterAssignment,
    NamedArgumentsNotAccepted,
    UnknownNamedParameter,
    ExcessPositionalArgument
}

internal sealed record VbaCallMappingMismatch(
    VbaCallMappingMismatchKind Kind,
    int SourceIndex,
    int? ParameterIndex);

/// <summary>
/// Retains the shared structural result used by call-site editor features.
/// </summary>
internal sealed record VbaCallArgumentMapping(
    IReadOnlyList<VbaMappedCallArgument> Arguments,
    int? ActiveParameter,
    bool AllowsPositionalExpression,
    IReadOnlyList<VbaCallableParameter> RemainingNamedParameters,
    IReadOnlyList<VbaCallMappingMismatch> Mismatches,
    bool HasValidSignatureShape,
    bool HasStructuralMismatch,
    bool HasIndeterminateMapping,
    VbaCallContextCompatibility ContextCompatibility);

internal sealed record VbaCompleteCallArgumentMapping(
    VbaCallArgumentMapping Mapping,
    IReadOnlyList<int> MissingRequiredParameterIndexes,
    VbaCallCompatibilityState State,
    IReadOnlyList<string> TypeMismatchReasons);

internal sealed record VbaCallVariantCompatibility(
    VbaSourceDefinition Definition,
    VbaCallableSignature? Signature,
    VbaCallableSignature? InvocationSignature,
    VbaCompleteCallArgumentMapping? Mapping,
    VbaCallCompatibilityState State);

internal sealed record VbaConditionalCallCompatibility(
    VbaResolvedNameTarget Target,
    VbaCallContext Context,
    IReadOnlyList<VbaCallVariantCompatibility> Variants);

internal static class VbaCallDiagnosticText
{
    public static string GetParameterSubject(
        VbaCallableParameter parameter,
        int parameterIndex)
        => string.IsNullOrWhiteSpace(parameter.Name)
            ? $"parameter {parameterIndex + 1}"
            : $"parameter '{parameter.Name}'";
}

/// <summary>
/// Maps positional, named, and omitted call arguments without selecting a callable variant.
/// </summary>
internal static class VbaCallArgumentMapper
{
    private static readonly VbaCallSiteSyntax EmptyCompleteCallSite = CreateEmptyCompleteCallSite();

    public static VbaCompleteCallArgumentMapping MapCompleteZeroArgument(
        VbaCallableSignature signature,
        VbaCallContextCompatibility contextCompatibility)
        => MapComplete(
            signature,
            EmptyCompleteCallSite,
            allowNamedArguments: false,
            contextCompatibility);

    public static VbaCallContextCompatibility GetContextCompatibility(
        VbaSourceDefinition definition,
        VbaCallableSignature signature,
        VbaCallContext context)
    {
        var compatible = context switch
        {
            VbaCallContext.StatementInvocation =>
                signature.CallableKind is VbaCallableKind.Sub or VbaCallableKind.Function,
            VbaCallContext.ValueRead =>
                signature.CallableKind == VbaCallableKind.Function
                || (signature.CallableKind == VbaCallableKind.Property
                    && definition.PropertyAccess.HasFlag(VbaPropertyAccess.Readable)),
            VbaCallContext.PropertyLetAssignment =>
                definition.PropertyAccessorKind == VbaPropertyAccessorKind.Let,
            VbaCallContext.PropertySetAssignment =>
                definition.PropertyAccessorKind == VbaPropertyAccessorKind.Set,
            VbaCallContext.RaiseEvent => signature.CallableKind == VbaCallableKind.Event,
            _ => false
        };
        var hasUnknownPropertyAccessorForContext =
            signature.CallableKind == VbaCallableKind.Property
            && definition.PropertyAccessorKind is null
            && (context == VbaCallContext.ValueRead
                    && definition.PropertyAccess == VbaPropertyAccess.Unknown
                || context is VbaCallContext.PropertyLetAssignment
                        or VbaCallContext.PropertySetAssignment
                    && (definition.PropertyAccess == VbaPropertyAccess.Unknown
                        || definition.PropertyAccess.HasFlag(VbaPropertyAccess.Writable)));
        if (context == VbaCallContext.Indeterminate
            || signature.CallableKind is null
            || hasUnknownPropertyAccessorForContext)
        {
            return VbaCallContextCompatibility.Indeterminate;
        }

        return compatible
            ? VbaCallContextCompatibility.Compatible
            : VbaCallContextCompatibility.Incompatible;
    }

    public static VbaCallArgumentMapping MapInProgress(
        VbaCallableSignature signature,
        VbaCallSiteSyntax callSite,
        bool allowNamedArguments,
        VbaCallContextCompatibility contextCompatibility =
            VbaCallContextCompatibility.Indeterminate)
    {
        var parameters = signature.Parameters;
        if (!HasValidParameterShape(parameters))
        {
            return Invalid();
        }

        var mappedArguments = new List<VbaMappedCallArgument>();
        var mismatches = new List<VbaCallMappingMismatch>();
        var consumedParameters = new bool[parameters.Count];
        var suppliedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nextPositionalParameter = 0;
        var hasNamedArgument = false;
        var hasStructuralMismatch = false;
        var hasParamArray = parameters.LastOrDefault()?.IsParamArray == true;
        var namedArgumentsAccepted = allowNamedArguments
            && signature.SupportsNamedArguments == true
            && !hasParamArray;
        var namedArgumentSupportUnknown = allowNamedArguments
            && signature.SupportsNamedArguments is null
            && !hasParamArray;
        var hasIndeterminateMapping = false;
        var priorArgumentCount = Math.Clamp(
            callSite.ActiveArgumentIndex,
            0,
            callSite.Arguments.Count);

        foreach (var argument in callSite.Arguments.Take(priorArgumentCount))
        {
            if (argument.Name is not null)
            {
                hasNamedArgument = true;
                if (namedArgumentSupportUnknown)
                {
                    hasIndeterminateMapping = true;
                    mappedArguments.Add(new VbaMappedCallArgument(
                        argument.Index,
                        null,
                        IsParamArrayElement: false));
                    continue;
                }

                var namedParameterIndex = FindParameter(parameters, argument.Name);
                var isDuplicateName = !suppliedNames.Add(argument.Name);
                var isDuplicateMapping = namedParameterIndex >= 0
                    && consumedParameters[namedParameterIndex];
                var mayMatchUnavailableParameterName = namedArgumentsAccepted
                    && namedParameterIndex < 0
                    && parameters
                        .Where((parameter, index) => !consumedParameters[index])
                        .Any(parameter => string.IsNullOrWhiteSpace(parameter.Name));
                if (mayMatchUnavailableParameterName)
                {
                    hasIndeterminateMapping = true;
                    mappedArguments.Add(new VbaMappedCallArgument(
                        argument.Index,
                        null,
                        IsParamArrayElement: false));
                    continue;
                }

                if (!namedArgumentsAccepted
                    || isDuplicateName
                    || namedParameterIndex < 0
                    || isDuplicateMapping
                    || parameters[namedParameterIndex].IsParamArray)
                {
                    hasStructuralMismatch = true;
                }

                if (!namedArgumentsAccepted)
                {
                    mismatches.Add(new VbaCallMappingMismatch(
                        VbaCallMappingMismatchKind.NamedArgumentsNotAccepted,
                        argument.Index,
                        null));
                }
                else if (namedParameterIndex < 0)
                {
                    mismatches.Add(new VbaCallMappingMismatch(
                        VbaCallMappingMismatchKind.UnknownNamedParameter,
                        argument.Index,
                        null));
                }
                else if (namedParameterIndex >= 0
                    && (isDuplicateName || isDuplicateMapping))
                {
                    mismatches.Add(new VbaCallMappingMismatch(
                        VbaCallMappingMismatchKind.DuplicateParameterAssignment,
                        argument.Index,
                        namedParameterIndex));
                }

                if (namedArgumentsAccepted
                    && namedParameterIndex >= 0
                    && !isDuplicateName
                    && !isDuplicateMapping)
                {
                    consumedParameters[namedParameterIndex] = true;
                }

                mappedArguments.Add(new VbaMappedCallArgument(
                    argument.Index,
                    namedArgumentsAccepted
                        && namedParameterIndex >= 0
                        && !isDuplicateName
                        && !isDuplicateMapping
                        ? namedParameterIndex
                        : null,
                    namedParameterIndex >= 0 && parameters[namedParameterIndex].IsParamArray));
                continue;
            }

            if (hasNamedArgument)
            {
                hasStructuralMismatch = true;
            }

            var positionalParameter = GetPositionalParameter(
                parameters,
                nextPositionalParameter);
            if (positionalParameter is null)
            {
                hasStructuralMismatch = true;
                mismatches.Add(new VbaCallMappingMismatch(
                    VbaCallMappingMismatchKind.ExcessPositionalArgument,
                    argument.Index,
                    null));
                mappedArguments.Add(new VbaMappedCallArgument(argument.Index, null, false));
                continue;
            }

            var (parameter, parameterIndex) = positionalParameter.Value;
            mappedArguments.Add(new VbaMappedCallArgument(
                argument.Index,
                parameterIndex,
                parameter.IsParamArray));
            if (argument.IsOmitted && !parameter.IsOptional && !parameter.IsParamArray)
            {
                hasStructuralMismatch = true;
                mismatches.Add(new VbaCallMappingMismatch(
                    VbaCallMappingMismatchKind.RequiredArgumentOmitted,
                    argument.Index,
                    parameterIndex));
            }

            consumedParameters[parameterIndex] = true;
            if (!parameter.IsParamArray)
            {
                nextPositionalParameter++;
            }
        }

        foreach (var argument in callSite.TrailingArguments ?? [])
        {
            if (argument.Name is null)
            {
                continue;
            }

            var parameterIndex = FindParameter(parameters, argument.Name);
            if (parameterIndex >= 0 && !parameters[parameterIndex].IsParamArray)
            {
                consumedParameters[parameterIndex] = true;
            }
        }

        var activeParameter = MapActiveParameter(
            parameters,
            callSite,
            consumedParameters,
            nextPositionalParameter,
            hasNamedArgument,
            namedArgumentsAccepted);
        hasIndeterminateMapping |= namedArgumentSupportUnknown
            && callSite.ActiveNamedArgument is not null;
        var allowsPositionalExpression = !hasStructuralMismatch
            && !hasNamedArgument
            && GetPositionalParameter(parameters, nextPositionalParameter) is not null;
        var hasTrailingPositionalArgument = callSite.TrailingArguments?.Any(argument =>
            argument.Name is null) == true;
        var canOfferNamedArguments = !hasStructuralMismatch
            && namedArgumentsAccepted
            && !hasTrailingPositionalArgument;
        var remainingNamedParameters = canOfferNamedArguments
            ? parameters
                .Where((parameter, index) => !consumedParameters[index]
                    && !string.IsNullOrWhiteSpace(parameter.Name))
                .ToArray()
            : [];
        return new VbaCallArgumentMapping(
            Array.AsReadOnly(mappedArguments.ToArray()),
            activeParameter,
            allowsPositionalExpression,
            Array.AsReadOnly(remainingNamedParameters),
            Array.AsReadOnly(mismatches.ToArray()),
            true,
            hasStructuralMismatch,
            hasIndeterminateMapping,
            contextCompatibility);
    }

    public static VbaCompleteCallArgumentMapping MapComplete(
        VbaCallableSignature signature,
        VbaCallSiteSyntax callSite,
        bool allowNamedArguments,
        VbaCallContextCompatibility contextCompatibility)
    {
        var mapping = MapInProgress(
            signature,
            callSite,
            allowNamedArguments,
            contextCompatibility);
        var mappedParameters = mapping.Arguments
            .Where(argument => argument.ParameterIndex is not null)
            .Select(argument => argument.ParameterIndex!.Value)
            .ToHashSet();
        if (mapping.HasIndeterminateMapping)
        {
            foreach (var argument in callSite.Arguments.Where(argument => argument.Name is not null))
            {
                var parameterIndex = FindParameter(signature.Parameters, argument.Name!);
                if (parameterIndex >= 0)
                {
                    mappedParameters.Add(parameterIndex);
                    continue;
                }

                foreach (var blankParameterIndex in signature.Parameters
                    .Select((parameter, index) => new { parameter, index })
                    .Where(item => string.IsNullOrWhiteSpace(item.parameter.Name))
                    .Select(item => item.index))
                {
                    mappedParameters.Add(blankParameterIndex);
                }
            }
        }

        var missingRequiredParameters = signature.Parameters
            .Select((parameter, index) => new { parameter, index })
            .Where(item => !item.parameter.IsOptional
                && !item.parameter.IsParamArray
                && !mappedParameters.Contains(item.index))
            .Select(item => item.index)
            .ToArray();
        var requiredParameterCount = signature.Parameters.Count(parameter =>
            !parameter.IsOptional && !parameter.IsParamArray);
        var suppliedValueCount = callSite.Arguments.Count(argument => !argument.IsOmitted);
        if (mapping.HasStructuralMismatch
            && suppliedValueCount >= requiredParameterCount)
        {
            missingRequiredParameters = [];
        }
        var state = !mapping.HasValidSignatureShape
            ? VbaCallCompatibilityState.Indeterminate
            : callSite.IsIncomplete
                && contextCompatibility != VbaCallContextCompatibility.Incompatible
                ? VbaCallCompatibilityState.Indeterminate
            : contextCompatibility == VbaCallContextCompatibility.Incompatible
            || mapping.HasStructuralMismatch
            || missingRequiredParameters.Length > 0
                ? VbaCallCompatibilityState.Inapplicable
                : mapping.HasIndeterminateMapping
                    || contextCompatibility == VbaCallContextCompatibility.Indeterminate
                    ? VbaCallCompatibilityState.Indeterminate
                    : callSite.Arguments.Count == 0
                        ? VbaCallCompatibilityState.Applicable
                        : VbaCallCompatibilityState.Indeterminate;
        return new VbaCompleteCallArgumentMapping(
            mapping,
            Array.AsReadOnly(missingRequiredParameters),
            state,
            []);
    }

    private static int? MapActiveParameter(
        IReadOnlyList<VbaCallableParameter> parameters,
        VbaCallSiteSyntax callSite,
        IReadOnlyList<bool> consumedParameters,
        int nextPositionalParameter,
        bool hasNamedArgument,
        bool namedArgumentsAccepted)
    {
        if (callSite.ActiveNamedArgument is not null)
        {
            if (!namedArgumentsAccepted)
            {
                return null;
            }

            var parameterIndex = FindParameter(parameters, callSite.ActiveNamedArgument);
            return parameterIndex >= 0
                && !consumedParameters[parameterIndex]
                && !parameters[parameterIndex].IsParamArray
                    ? parameterIndex
                    : null;
        }

        if (hasNamedArgument)
        {
            return null;
        }

        return GetPositionalParameter(parameters, nextPositionalParameter)?.Index;
    }

    private static (VbaCallableParameter Parameter, int Index)? GetPositionalParameter(
        IReadOnlyList<VbaCallableParameter> parameters,
        int nextPositionalParameter)
        => nextPositionalParameter >= 0 && nextPositionalParameter < parameters.Count
            ? (parameters[nextPositionalParameter], nextPositionalParameter)
            : null;

    private static bool HasValidParameterShape(IReadOnlyList<VbaCallableParameter> parameters)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index];
            if (!string.IsNullOrWhiteSpace(parameter.Name)
                    && !names.Add(parameter.Name)
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
        if (string.IsNullOrWhiteSpace(name))
        {
            return -1;
        }

        for (var index = 0; index < parameters.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(parameters[index].Name)
                && parameters[index].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static VbaCallArgumentMapping Invalid()
        => new(
            [],
            null,
            false,
            [],
            [],
            false,
            true,
            true,
            VbaCallContextCompatibility.Indeterminate);

    private static VbaCallSiteSyntax CreateEmptyCompleteCallSite()
    {
        var position = new VbaSyntaxPosition(0, 0, 0);
        var range = new VbaSyntaxRange(position, position);
        var identifier = new VbaPositionIdentifierSyntax("", range, IsKeyword: false);
        return new VbaCallSiteSyntax(
            VbaCallSyntaxForm.BareValueRead,
            new VbaMemberAccessSyntax(
                [identifier],
                TargetSegmentIndex: 0,
                IsLeadingDot: false,
                IsIncomplete: false,
                HasTrailingWhitespace: false,
                range),
            [],
            ActiveArgumentIndex: 0,
            ActiveNamedArgument: null,
            IsIncomplete: false);
    }
}
