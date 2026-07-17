namespace VbaLanguageServer.Syntax;

/// <summary>
/// Validates the finite argument contract of a known VBA Standard Library
/// member without resolving project or reference-catalog state.
/// </summary>
internal static class VbaStandardLibraryInvocationSyntax
{
    public static bool IsCompatible(
        IReadOnlyList<VbaToken> tokens,
        int memberTokenIndex,
        int end,
        VbaStandardLibraryPotentialReceiverMemberSyntaxFact member)
    {
        var invocationStart = memberTokenIndex + 1;
        if (invocationStart >= end || !Matches(tokens[invocationStart], "("))
        {
            var canOmitArguments =
                member.Kind == VbaStandardLibraryPotentialReceiverMemberKind.Property
                || member.Parameters.All(parameter => parameter.IsOptional || parameter.IsParamArray);
            return canOmitArguments
                && (member.DeclaredTypeCategory !=
                        VbaStandardLibraryPotentialReceiverDeclaredTypeCategory.NamedObject
                    || invocationStart == end);
        }

        if (!TryReadArguments(
                tokens,
                invocationStart,
                end,
                out var arguments,
                out var invocationEnd))
        {
            return false;
        }

        return ArgumentsMatchParameters(tokens, arguments, member.Parameters)
            && (member.DeclaredTypeCategory !=
                    VbaStandardLibraryPotentialReceiverDeclaredTypeCategory.NamedObject
                || invocationEnd == end);
    }

    private static bool TryReadArguments(
        IReadOnlyList<VbaToken> tokens,
        int invocationStart,
        int end,
        out IReadOnlyList<ArgumentRange> arguments,
        out int invocationEnd)
    {
        var result = new List<ArgumentRange>();
        var argumentStart = invocationStart + 1;
        var depth = 1;
        for (var index = argumentStart; index < end; index++)
        {
            if (Matches(tokens[index], "("))
            {
                depth++;
                continue;
            }

            if (Matches(tokens[index], ")"))
            {
                depth--;
                if (depth != 0)
                {
                    continue;
                }

                if (argumentStart != index || result.Count > 0)
                {
                    result.Add(new ArgumentRange(argumentStart, index));
                }

                arguments = result;
                invocationEnd = index + 1;
                return true;
            }

            if (depth == 1 && Matches(tokens[index], ","))
            {
                result.Add(new ArgumentRange(argumentStart, index));
                argumentStart = index + 1;
            }
        }

        arguments = [];
        invocationEnd = invocationStart;
        return false;
    }

    private static bool ArgumentsMatchParameters(
        IReadOnlyList<VbaToken> tokens,
        IReadOnlyList<ArgumentRange> arguments,
        IReadOnlyList<VbaStandardLibraryParameterSyntaxFact> parameters)
    {
        var supplied = new bool[parameters.Count];
        var positionalIndex = 0;
        var namedArgumentSeen = false;
        var paramArrayIndex = FindParamArrayIndex(parameters);

        foreach (var argument in arguments)
        {
            if (argument.Start == argument.End)
            {
                if (namedArgumentSeen
                    || positionalIndex >= parameters.Count
                    || !parameters[positionalIndex].IsOptional
                        && !parameters[positionalIndex].IsParamArray)
                {
                    return false;
                }

                supplied[positionalIndex] = true;
                if (!parameters[positionalIndex].IsParamArray)
                {
                    positionalIndex++;
                }

                continue;
            }

            if (TryGetNamedArgument(tokens, argument, out var argumentName))
            {
                if (paramArrayIndex >= 0)
                {
                    return false;
                }

                namedArgumentSeen = true;
                var parameterIndex = FindParameterIndex(parameters, argumentName);
                if (parameterIndex < 0
                    || parameters[parameterIndex].IsParamArray
                    || supplied[parameterIndex])
                {
                    return false;
                }

                supplied[parameterIndex] = true;
                continue;
            }

            if (namedArgumentSeen)
            {
                return false;
            }

            if (positionalIndex < parameters.Count)
            {
                if (parameters[positionalIndex].IsParamArray)
                {
                    supplied[positionalIndex] = true;
                }
                else
                {
                    supplied[positionalIndex] = true;
                    positionalIndex++;
                }

                continue;
            }

            if (paramArrayIndex < 0)
            {
                return false;
            }

            supplied[paramArrayIndex] = true;
        }

        for (var index = 0; index < parameters.Count; index++)
        {
            if (!parameters[index].IsOptional
                && !parameters[index].IsParamArray
                && !supplied[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetNamedArgument(
        IReadOnlyList<VbaToken> tokens,
        ArgumentRange argument,
        out string name)
    {
        if (argument.End - argument.Start >= 3
            && tokens[argument.Start].Kind is VbaTokenKind.Identifier or VbaTokenKind.Keyword
            && Matches(tokens[argument.Start + 1], ":="))
        {
            name = tokens[argument.Start].Text;
            return true;
        }

        name = string.Empty;
        return false;
    }

    private static int FindParameterIndex(
        IReadOnlyList<VbaStandardLibraryParameterSyntaxFact> parameters,
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

    private static int FindParamArrayIndex(
        IReadOnlyList<VbaStandardLibraryParameterSyntaxFact> parameters)
    {
        for (var index = 0; index < parameters.Count; index++)
        {
            if (parameters[index].IsParamArray)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool Matches(VbaToken token, string text)
        => token.Text.Equals(text, StringComparison.OrdinalIgnoreCase);

    private readonly record struct ArgumentRange(int Start, int End);
}
