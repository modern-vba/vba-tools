using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

internal enum VbaCallableContractParameterRole
{
    Required,
    Optional,
    ParamArray
}

internal sealed record VbaCallableContractType(
    string Name,
    object? Identity,
    string? ReferenceQualifiedName = null,
    bool IsPortableTypeLibraryIdentity = false,
    bool IsUnmappedProjectReferenceIdentity = false);

internal enum VbaCallableContractDefaultState
{
    Absent,
    Evaluated,
    Indeterminate
}

internal sealed record VbaCallableContractDefault(
    VbaCallableContractDefaultState State,
    VbaConstantValue? Value = null)
{
    public static VbaCallableContractDefault Absent { get; } = new(
        VbaCallableContractDefaultState.Absent);

    public static VbaCallableContractDefault Indeterminate { get; } = new(
        VbaCallableContractDefaultState.Indeterminate);

    public static VbaCallableContractDefault FromExpression(string expression)
    {
        var evaluation = VbaConstantExpressionEvaluator.Evaluate(expression);
        return evaluation.Succeeded
            ? new VbaCallableContractDefault(
                VbaCallableContractDefaultState.Evaluated,
                evaluation.Value)
            : Indeterminate;
    }

    public string Presentation => State == VbaCallableContractDefaultState.Absent
        ? "no default"
        : Value?.Presentation ?? "unknown default";
}

internal sealed record VbaCallableContractParameter(
    VbaCallableContractType? Type,
    bool? IsArray,
    bool? IsByRef,
    VbaCallableContractParameterRole? Role,
    VbaCallableContractDefault Default);

internal sealed record VbaCallableContractResult(
    VbaCallableContractType? Type,
    bool? IsArray);

internal sealed record VbaCallableContract(
    IReadOnlyList<VbaCallableContractParameter> Parameters,
    VbaCallableContractParameter? PropertyValueParameter = null,
    VbaCallableContractResult? Result = null);

internal enum VbaCallableContractComparisonParticipation
{
    Compare,
    NotCompared
}

internal sealed record VbaCallableContractComparisonPolicy(
    VbaCallableContractComparisonParticipation PropertyValueContract,
    VbaCallableContractComparisonParticipation ResultContract)
{
    public static VbaCallableContractComparisonPolicy EventHandler { get; } = new(
        VbaCallableContractComparisonParticipation.NotCompared,
        VbaCallableContractComparisonParticipation.NotCompared);

    public static VbaCallableContractComparisonPolicy InterfaceFulfillment { get; } = new(
        VbaCallableContractComparisonParticipation.Compare,
        VbaCallableContractComparisonParticipation.Compare);
}

internal enum VbaCallableContractComparisonState
{
    Compatible,
    Incompatible,
    Indeterminate
}

internal enum VbaCallableContractComparisonFactOutcome
{
    Mismatch,
    Indeterminate
}

internal enum VbaCallableContractComparisonSubject
{
    Contract,
    ParameterList,
    Parameter,
    PropertyValue,
    Result
}

internal enum VbaCallableContractComparisonDimension
{
    Availability,
    Count,
    Presence,
    CanonicalType,
    ArrayShape,
    PassingMechanism,
    Role,
    Default
}

internal sealed record VbaCallableContractComparisonFact(
    VbaCallableContractComparisonFactOutcome Outcome,
    VbaCallableContractComparisonSubject Subject,
    VbaCallableContractComparisonDimension Dimension,
    int? ParameterOrdinal,
    object? Expected,
    object? Found);

internal sealed record VbaCallableContractComparisonResult(
    IReadOnlyList<VbaCallableContractComparisonFact> Facts)
{
    public static VbaCallableContractComparisonResult UnavailableContractEvidence()
        => new([
            new VbaCallableContractComparisonFact(
                VbaCallableContractComparisonFactOutcome.Indeterminate,
                VbaCallableContractComparisonSubject.Contract,
                VbaCallableContractComparisonDimension.Availability,
                ParameterOrdinal: null,
                Expected: null,
                Found: null)
        ]);

    public VbaCallableContractComparisonState State
        => Facts.Any(fact =>
                fact.Outcome == VbaCallableContractComparisonFactOutcome.Mismatch)
            ? VbaCallableContractComparisonState.Incompatible
            : HasIndeterminateEvidence
                ? VbaCallableContractComparisonState.Indeterminate
                : VbaCallableContractComparisonState.Compatible;

    public bool HasIndeterminateEvidence
        => Facts.Any(fact =>
            fact.Outcome == VbaCallableContractComparisonFactOutcome.Indeterminate);

    public IReadOnlyList<VbaCallableContractComparisonFact> MismatchFacts
        => Facts.Where(fact =>
                fact.Outcome == VbaCallableContractComparisonFactOutcome.Mismatch)
            .ToArray();
}

internal static class VbaCallableContractComparison
{
    public static VbaCallableContractComparisonResult Compare(
        VbaCallableContract expected,
        VbaCallableContract found,
        VbaCallableContractComparisonPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(found);
        ArgumentNullException.ThrowIfNull(policy);

        var facts = new List<VbaCallableContractComparisonFact>();
        var comparesPropertyValue = policy.PropertyValueContract
            == VbaCallableContractComparisonParticipation.Compare;
        var expectedParameterCount = expected.Parameters.Count
            + (comparesPropertyValue && expected.PropertyValueParameter is not null
                ? 1
                : 0);
        var foundParameterCount = found.Parameters.Count
            + (comparesPropertyValue && found.PropertyValueParameter is not null
                ? 1
                : 0);
        if (expectedParameterCount != foundParameterCount)
        {
            facts.Add(new VbaCallableContractComparisonFact(
                VbaCallableContractComparisonFactOutcome.Mismatch,
                VbaCallableContractComparisonSubject.ParameterList,
                VbaCallableContractComparisonDimension.Count,
                ParameterOrdinal: null,
                expectedParameterCount,
                foundParameterCount));
        }

        var commonParameterCount = Math.Min(
            expected.Parameters.Count,
            found.Parameters.Count);
        for (var index = 0; index < commonParameterCount; index++)
        {
            CompareParameter(
                expected.Parameters[index],
                found.Parameters[index],
                VbaCallableContractComparisonSubject.Parameter,
                index + 1,
                facts);
        }
        for (var index = commonParameterCount;
             index < expected.Parameters.Count;
             index++)
        {
            RetainUnmappedParameterIndeterminateEvidence(
                expected.Parameters[index],
                VbaCallableContractComparisonSubject.Parameter,
                index + 1,
                isExpected: true,
                facts);
        }
        for (var index = commonParameterCount;
             index < found.Parameters.Count;
             index++)
        {
            RetainUnmappedParameterIndeterminateEvidence(
                found.Parameters[index],
                VbaCallableContractComparisonSubject.Parameter,
                index + 1,
                isExpected: false,
                facts);
        }

        if (comparesPropertyValue)
        {
            if (expected.PropertyValueParameter is not null
                && found.PropertyValueParameter is not null)
            {
                CompareParameter(
                    expected.PropertyValueParameter,
                    found.PropertyValueParameter,
                    VbaCallableContractComparisonSubject.PropertyValue,
                    parameterOrdinal: null,
                    facts);
            }
            else if (expected.PropertyValueParameter is not null
                || found.PropertyValueParameter is not null)
            {
                AddPresenceMismatch(
                    VbaCallableContractComparisonSubject.PropertyValue,
                    expected.PropertyValueParameter is not null,
                    found.PropertyValueParameter is not null,
                    facts);
                RetainUnmappedParameterIndeterminateEvidence(
                    expected.PropertyValueParameter
                        ?? found.PropertyValueParameter!,
                    VbaCallableContractComparisonSubject.PropertyValue,
                    parameterOrdinal: null,
                    isExpected: expected.PropertyValueParameter is not null,
                    facts);
            }
        }

        if (policy.ResultContract
            == VbaCallableContractComparisonParticipation.Compare)
        {
            if (expected.Result is not null && found.Result is not null)
            {
                CompareResult(expected.Result, found.Result, facts);
            }
            else if (expected.Result is not null || found.Result is not null)
            {
                AddPresenceMismatch(
                    VbaCallableContractComparisonSubject.Result,
                    expected.Result is not null,
                    found.Result is not null,
                    facts);
                RetainUnmappedResultIndeterminateEvidence(
                    expected.Result ?? found.Result!,
                    isExpected: expected.Result is not null,
                    facts);
            }
        }

        return new VbaCallableContractComparisonResult(facts.ToArray());
    }

    private static void CompareParameter(
        VbaCallableContractParameter? expected,
        VbaCallableContractParameter? found,
        VbaCallableContractComparisonSubject subject,
        int? parameterOrdinal,
        ICollection<VbaCallableContractComparisonFact> facts)
    {
        CompareType(
            expected?.Type,
            found?.Type,
            subject,
            parameterOrdinal,
            facts);
        CompareKnownValue(
            expected?.IsArray,
            found?.IsArray,
            subject,
            VbaCallableContractComparisonDimension.ArrayShape,
            parameterOrdinal,
            facts);
        CompareKnownValue(
            expected?.IsByRef,
            found?.IsByRef,
            subject,
            VbaCallableContractComparisonDimension.PassingMechanism,
            parameterOrdinal,
            facts);
        CompareKnownValue(
            expected?.Role,
            found?.Role,
            subject,
            VbaCallableContractComparisonDimension.Role,
            parameterOrdinal,
            facts);
        CompareDefault(
            expected?.Default,
            found?.Default,
            subject,
            parameterOrdinal,
            facts);
    }

    private static void CompareResult(
        VbaCallableContractResult? expected,
        VbaCallableContractResult? found,
        ICollection<VbaCallableContractComparisonFact> facts)
    {
        CompareType(
            expected?.Type,
            found?.Type,
            VbaCallableContractComparisonSubject.Result,
            parameterOrdinal: null,
            facts);
        CompareKnownValue(
            expected?.IsArray,
            found?.IsArray,
            VbaCallableContractComparisonSubject.Result,
            VbaCallableContractComparisonDimension.ArrayShape,
            parameterOrdinal: null,
            facts);
    }

    private static void CompareType(
        VbaCallableContractType? expected,
        VbaCallableContractType? found,
        VbaCallableContractComparisonSubject subject,
        int? parameterOrdinal,
        ICollection<VbaCallableContractComparisonFact> facts)
    {
        if (expected?.Identity is null || found?.Identity is null)
        {
            AddIndeterminate(
                subject,
                VbaCallableContractComparisonDimension.CanonicalType,
                parameterOrdinal,
                expected,
                found,
                facts);
            return;
        }

        if (HasIncompletePortableTypeComparison(expected, found))
        {
            AddIndeterminate(
                subject,
                VbaCallableContractComparisonDimension.CanonicalType,
                parameterOrdinal,
                expected,
                found,
                facts);
            return;
        }

        if (HaveSameCanonicalTypeIdentity(expected.Identity, found.Identity))
        {
            return;
        }

        facts.Add(new VbaCallableContractComparisonFact(
            VbaCallableContractComparisonFactOutcome.Mismatch,
            subject,
            VbaCallableContractComparisonDimension.CanonicalType,
            parameterOrdinal,
            expected,
            found));
    }

    private static bool HaveSameCanonicalTypeIdentity(
        object expected,
        object found)
    {
        var expectedIntrinsic = expected as string;
        var foundIntrinsic = found as string;
        if (expectedIntrinsic is not null || foundIntrinsic is not null)
        {
            return expectedIntrinsic is not null
                && foundIntrinsic is not null
                && expectedIntrinsic.Equals(
                    foundIntrinsic,
                    StringComparison.OrdinalIgnoreCase);
        }

        return expected.Equals(found);
    }

    private static bool HasIncompletePortableTypeComparison(
        VbaCallableContractType expected,
        VbaCallableContractType found)
        => expected.IsPortableTypeLibraryIdentity
                && found.IsUnmappedProjectReferenceIdentity
            || found.IsPortableTypeLibraryIdentity
                && expected.IsUnmappedProjectReferenceIdentity;

    private static void CompareKnownValue<T>(
        T? expected,
        T? found,
        VbaCallableContractComparisonSubject subject,
        VbaCallableContractComparisonDimension dimension,
        int? parameterOrdinal,
        ICollection<VbaCallableContractComparisonFact> facts)
        where T : struct
    {
        if (expected is null || found is null)
        {
            AddIndeterminate(
                subject,
                dimension,
                parameterOrdinal,
                expected,
                found,
                facts);
            return;
        }

        if (!EqualityComparer<T>.Default.Equals(expected.Value, found.Value))
        {
            facts.Add(new VbaCallableContractComparisonFact(
                VbaCallableContractComparisonFactOutcome.Mismatch,
                subject,
                dimension,
                parameterOrdinal,
                expected.Value,
                found.Value));
        }
    }

    private static void CompareDefault(
        VbaCallableContractDefault? expected,
        VbaCallableContractDefault? found,
        VbaCallableContractComparisonSubject subject,
        int? parameterOrdinal,
        ICollection<VbaCallableContractComparisonFact> facts)
    {
        if (expected is null
            || found is null
            || expected.State == VbaCallableContractDefaultState.Indeterminate
            || found.State == VbaCallableContractDefaultState.Indeterminate
            || expected.State == VbaCallableContractDefaultState.Evaluated
                && expected.Value is null
            || found.State == VbaCallableContractDefaultState.Evaluated
                && found.Value is null)
        {
            AddIndeterminate(
                subject,
                VbaCallableContractComparisonDimension.Default,
                parameterOrdinal,
                expected,
                found,
                facts);
            return;
        }

        var equivalent = expected.State == found.State
            && (expected.State != VbaCallableContractDefaultState.Evaluated
                || expected.Value!.Value.HasSameEvaluatedValue(found.Value!.Value));
        if (!equivalent)
        {
            facts.Add(new VbaCallableContractComparisonFact(
                VbaCallableContractComparisonFactOutcome.Mismatch,
                subject,
                VbaCallableContractComparisonDimension.Default,
                parameterOrdinal,
                expected,
                found));
        }
    }

    private static void AddIndeterminate(
        VbaCallableContractComparisonSubject subject,
        VbaCallableContractComparisonDimension dimension,
        int? parameterOrdinal,
        object? expected,
        object? found,
        ICollection<VbaCallableContractComparisonFact> facts)
        => facts.Add(new VbaCallableContractComparisonFact(
            VbaCallableContractComparisonFactOutcome.Indeterminate,
            subject,
            dimension,
            parameterOrdinal,
            expected,
            found));

    private static void RetainUnmappedParameterIndeterminateEvidence(
        VbaCallableContractParameter parameter,
        VbaCallableContractComparisonSubject subject,
        int? parameterOrdinal,
        bool isExpected,
        ICollection<VbaCallableContractComparisonFact> facts)
    {
        if (parameter.Type?.Identity is null)
        {
            AddIndeterminate(
                subject,
                VbaCallableContractComparisonDimension.CanonicalType,
                parameterOrdinal,
                isExpected ? parameter.Type : null,
                isExpected ? null : parameter.Type,
                facts);
        }
        if (parameter.IsArray is null)
        {
            AddIndeterminate(
                subject,
                VbaCallableContractComparisonDimension.ArrayShape,
                parameterOrdinal,
                expected: null,
                found: null,
                facts: facts);
        }
        if (parameter.IsByRef is null)
        {
            AddIndeterminate(
                subject,
                VbaCallableContractComparisonDimension.PassingMechanism,
                parameterOrdinal,
                expected: null,
                found: null,
                facts: facts);
        }
        if (parameter.Role is null)
        {
            AddIndeterminate(
                subject,
                VbaCallableContractComparisonDimension.Role,
                parameterOrdinal,
                expected: null,
                found: null,
                facts: facts);
        }
        if (HasIndeterminateDefaultEvidence(parameter.Default))
        {
            AddIndeterminate(
                subject,
                VbaCallableContractComparisonDimension.Default,
                parameterOrdinal,
                isExpected ? parameter.Default : null,
                isExpected ? null : parameter.Default,
                facts);
        }
    }

    private static void RetainUnmappedResultIndeterminateEvidence(
        VbaCallableContractResult result,
        bool isExpected,
        ICollection<VbaCallableContractComparisonFact> facts)
    {
        if (result.Type?.Identity is null)
        {
            AddIndeterminate(
                VbaCallableContractComparisonSubject.Result,
                VbaCallableContractComparisonDimension.CanonicalType,
                parameterOrdinal: null,
                isExpected ? result.Type : null,
                isExpected ? null : result.Type,
                facts);
        }
        if (result.IsArray is null)
        {
            AddIndeterminate(
                VbaCallableContractComparisonSubject.Result,
                VbaCallableContractComparisonDimension.ArrayShape,
                parameterOrdinal: null,
                expected: null,
                found: null,
                facts: facts);
        }
    }

    private static bool HasIndeterminateDefaultEvidence(
        VbaCallableContractDefault? value)
        => value is null
            || value.State == VbaCallableContractDefaultState.Indeterminate
            || value.State == VbaCallableContractDefaultState.Evaluated
                && value.Value is null;

    private static void AddPresenceMismatch(
        VbaCallableContractComparisonSubject subject,
        bool expectedIsPresent,
        bool foundIsPresent,
        ICollection<VbaCallableContractComparisonFact> facts)
        => facts.Add(new VbaCallableContractComparisonFact(
            VbaCallableContractComparisonFactOutcome.Mismatch,
            subject,
            VbaCallableContractComparisonDimension.Presence,
            ParameterOrdinal: null,
            expectedIsPresent,
            foundIsPresent));
}

internal static class VbaCallableContractComparisonFormatter
{
    public static IReadOnlyList<string> FormatMismatchReasons(
        VbaCallableContractComparisonResult comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        return comparison.MismatchFacts.Select(FormatMismatch).ToArray();
    }

    private static string FormatMismatch(VbaCallableContractComparisonFact fact)
    {
        if (fact.Outcome != VbaCallableContractComparisonFactOutcome.Mismatch)
        {
            throw new ArgumentException(
                "Only conclusive mismatch facts can be formatted.",
                nameof(fact));
        }

        var subject = GetSubject(fact);
        var (expected, found) = GetPresentations(fact);
        return $"{subject}: expected {expected}, found {found}";
    }

    private static string GetSubject(VbaCallableContractComparisonFact fact)
        => fact.Subject switch
        {
            VbaCallableContractComparisonSubject.ParameterList
                when fact.Dimension == VbaCallableContractComparisonDimension.Count
                => "parameter count",
            VbaCallableContractComparisonSubject.Parameter
                => $"parameter {fact.ParameterOrdinal} {GetDimension(fact.Dimension)}",
            VbaCallableContractComparisonSubject.PropertyValue
                => $"value parameter {GetDimension(fact.Dimension)}",
            VbaCallableContractComparisonSubject.Result
                when fact.Dimension
                    == VbaCallableContractComparisonDimension.Presence
                => "return contract presence",
            VbaCallableContractComparisonSubject.Result
                when fact.Dimension
                    == VbaCallableContractComparisonDimension.CanonicalType
                => "return type",
            VbaCallableContractComparisonSubject.Result
                when fact.Dimension
                    == VbaCallableContractComparisonDimension.ArrayShape
                => "return array shape",
            _ => throw new InvalidOperationException(
                "Unsupported callable-contract mismatch fact.")
        };

    private static string GetDimension(
        VbaCallableContractComparisonDimension dimension)
        => dimension switch
        {
            VbaCallableContractComparisonDimension.CanonicalType => "type",
            VbaCallableContractComparisonDimension.Presence => "presence",
            VbaCallableContractComparisonDimension.ArrayShape => "array shape",
            VbaCallableContractComparisonDimension.PassingMechanism => "passing",
            VbaCallableContractComparisonDimension.Role => "role",
            VbaCallableContractComparisonDimension.Default => "default",
            _ => throw new InvalidOperationException(
                "Unsupported callable-contract comparison dimension.")
        };

    private static (string Expected, string Found) GetPresentations(
        VbaCallableContractComparisonFact fact)
    {
        if (fact.Dimension == VbaCallableContractComparisonDimension.CanonicalType
            && fact.Expected is VbaCallableContractType expectedType
            && fact.Found is VbaCallableContractType foundType)
        {
            return GetTypePresentations(expectedType, foundType);
        }

        return (
            GetPresentation(fact.Dimension, fact.Expected),
            GetPresentation(fact.Dimension, fact.Found));
    }

    private static (string Expected, string Found) GetTypePresentations(
        VbaCallableContractType expected,
        VbaCallableContractType found)
    {
        if (!expected.Name.Equals(found.Name, StringComparison.OrdinalIgnoreCase))
        {
            return (expected.Name, found.Name);
        }

        var expectedPresentation = expected.ReferenceQualifiedName
            ?? expected.Name;
        var foundPresentation = found.ReferenceQualifiedName
            ?? found.Name;
        return expectedPresentation.Equals(
                foundPresentation,
                StringComparison.OrdinalIgnoreCase)
            ? (
                expectedPresentation,
                $"a distinct canonical identity named {foundPresentation}")
            : (expectedPresentation, foundPresentation);
    }

    private static string GetPresentation(
        VbaCallableContractComparisonDimension dimension,
        object? value)
        => dimension switch
        {
            VbaCallableContractComparisonDimension.Count
                when value is int count => count.ToString(),
            VbaCallableContractComparisonDimension.Presence
                when value is bool isPresent => isPresent ? "present" : "absent",
            VbaCallableContractComparisonDimension.ArrayShape
                when value is bool isArray => isArray ? "array" : "scalar",
            VbaCallableContractComparisonDimension.PassingMechanism
                when value is bool isByRef => isByRef ? "ByRef" : "ByVal",
            VbaCallableContractComparisonDimension.Role
                when value is VbaCallableContractParameterRole role
                => role switch
                {
                    VbaCallableContractParameterRole.Required => "required",
                    VbaCallableContractParameterRole.Optional => "Optional",
                    VbaCallableContractParameterRole.ParamArray => "ParamArray",
                    _ => throw new InvalidOperationException(
                        "Unknown callable-contract parameter role.")
                },
            VbaCallableContractComparisonDimension.Default
                when value is VbaCallableContractDefault defaultValue
                => defaultValue.Presentation,
            _ => throw new InvalidOperationException(
                "Unsupported callable-contract mismatch value.")
        };
}
