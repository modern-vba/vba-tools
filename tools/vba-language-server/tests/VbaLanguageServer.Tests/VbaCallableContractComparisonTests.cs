using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaCallableContractComparisonTests
{
    [Fact]
    public void Comparison_retains_every_ordered_mismatch_and_indeterminate_fact()
    {
        var expected = new VbaCallableContract(
            [
                new VbaCallableContractParameter(
                    Type("Long", "Long"),
                    IsArray: true,
                    IsByRef: true,
                    VbaCallableContractParameterRole.Required,
                    VbaCallableContractDefault.Absent)
            ],
            Result: new VbaCallableContractResult(
                Type("Long", "Long"),
                IsArray: true));
        var found = new VbaCallableContract(
            [
                new VbaCallableContractParameter(
                    Type: null,
                    IsArray: false,
                    IsByRef: null,
                    VbaCallableContractParameterRole.Optional,
                    EvaluatedDefault("\"\"")),
                new VbaCallableContractParameter(
                    Type("String", "String"),
                    IsArray: false,
                    IsByRef: false,
                    VbaCallableContractParameterRole.Required,
                    VbaCallableContractDefault.Absent)
            ],
            Result: new VbaCallableContractResult(
                Type: null,
                IsArray: false));

        var comparison = VbaCallableContractComparison.Compare(
            expected,
            found,
            VbaCallableContractComparisonPolicy.InterfaceFulfillment);

        Assert.Equal(VbaCallableContractComparisonState.Incompatible, comparison.State);
        Assert.True(comparison.HasIndeterminateEvidence);
        Assert.Equal(
            [
                (
                    VbaCallableContractComparisonFactOutcome.Mismatch,
                    VbaCallableContractComparisonSubject.ParameterList,
                    VbaCallableContractComparisonDimension.Count,
                    (int?)null),
                (
                    VbaCallableContractComparisonFactOutcome.Indeterminate,
                    VbaCallableContractComparisonSubject.Parameter,
                    VbaCallableContractComparisonDimension.CanonicalType,
                    (int?)1),
                (
                    VbaCallableContractComparisonFactOutcome.Mismatch,
                    VbaCallableContractComparisonSubject.Parameter,
                    VbaCallableContractComparisonDimension.ArrayShape,
                    (int?)1),
                (
                    VbaCallableContractComparisonFactOutcome.Indeterminate,
                    VbaCallableContractComparisonSubject.Parameter,
                    VbaCallableContractComparisonDimension.PassingMechanism,
                    (int?)1),
                (
                    VbaCallableContractComparisonFactOutcome.Mismatch,
                    VbaCallableContractComparisonSubject.Parameter,
                    VbaCallableContractComparisonDimension.Role,
                    (int?)1),
                (
                    VbaCallableContractComparisonFactOutcome.Mismatch,
                    VbaCallableContractComparisonSubject.Parameter,
                    VbaCallableContractComparisonDimension.Default,
                    (int?)1),
                (
                    VbaCallableContractComparisonFactOutcome.Indeterminate,
                    VbaCallableContractComparisonSubject.Result,
                    VbaCallableContractComparisonDimension.CanonicalType,
                    (int?)null),
                (
                    VbaCallableContractComparisonFactOutcome.Mismatch,
                    VbaCallableContractComparisonSubject.Result,
                    VbaCallableContractComparisonDimension.ArrayShape,
                    (int?)null)
            ],
            comparison.Facts.Select(fact => (
                fact.Outcome,
                fact.Subject,
                fact.Dimension,
                fact.ParameterOrdinal)));
        Assert.Equal(
            [
                "parameter count: expected 1, found 2",
                "parameter 1 array shape: expected array, found scalar",
                "parameter 1 role: expected required, found Optional",
                "parameter 1 default: expected no default, found \"\"",
                "return array shape: expected array, found scalar"
            ],
            VbaCallableContractComparisonFormatter.FormatMismatchReasons(comparison));
    }

    [Fact]
    public void Event_policy_marks_property_value_and_result_not_compared()
    {
        Assert.Equal(
            VbaCallableContractComparisonParticipation.NotCompared,
            VbaCallableContractComparisonPolicy.EventHandler.PropertyValueContract);
        Assert.Equal(
            VbaCallableContractComparisonParticipation.NotCompared,
            VbaCallableContractComparisonPolicy.EventHandler.ResultContract);
        var expected = new VbaCallableContract(
            [],
            PropertyValueParameter: new VbaCallableContractParameter(
                Type("Long", "expected-value"),
                IsArray: true,
                IsByRef: true,
                VbaCallableContractParameterRole.Optional,
                EvaluatedDefault("1")),
            Result: new VbaCallableContractResult(
                Type("Long", "expected-result"),
                IsArray: true));
        var found = new VbaCallableContract(
            [],
            PropertyValueParameter: new VbaCallableContractParameter(
                Type("String", "found-value"),
                IsArray: false,
                IsByRef: false,
                VbaCallableContractParameterRole.Required,
                VbaCallableContractDefault.Absent),
            Result: new VbaCallableContractResult(
                Type("String", "found-result"),
                IsArray: false));

        var comparison = VbaCallableContractComparison.Compare(
            expected,
            found,
            VbaCallableContractComparisonPolicy.EventHandler);

        Assert.Equal(VbaCallableContractComparisonState.Compatible, comparison.State);
        Assert.Empty(comparison.Facts);
    }

    [Fact]
    public void Comparison_returns_one_fact_for_each_indeterminate_parameter_dimension()
    {
        var expected = new VbaCallableContract([
            new VbaCallableContractParameter(
                Type("Long", "Long"),
                IsArray: false,
                IsByRef: true,
                VbaCallableContractParameterRole.Required,
                VbaCallableContractDefault.Absent)
        ]);
        var found = new VbaCallableContract([
            new VbaCallableContractParameter(
                Type: null,
                IsArray: null,
                IsByRef: null,
                Role: null,
                VbaCallableContractDefault.Indeterminate)
        ]);

        var comparison = VbaCallableContractComparison.Compare(
            expected,
            found,
            VbaCallableContractComparisonPolicy.InterfaceFulfillment);

        Assert.Equal(VbaCallableContractComparisonState.Indeterminate, comparison.State);
        Assert.All(
            comparison.Facts,
            fact => Assert.Equal(
                VbaCallableContractComparisonFactOutcome.Indeterminate,
                fact.Outcome));
        Assert.Equal(
            [
                VbaCallableContractComparisonDimension.CanonicalType,
                VbaCallableContractComparisonDimension.ArrayShape,
                VbaCallableContractComparisonDimension.PassingMechanism,
                VbaCallableContractComparisonDimension.Role,
                VbaCallableContractComparisonDimension.Default
            ],
            comparison.Facts.Select(fact => fact.Dimension));
    }

    [Fact]
    public void Parameter_count_mismatch_does_not_speculate_about_unmapped_slots()
    {
        var expected = new VbaCallableContract([]);
        var found = new VbaCallableContract([
            RequiredParameter("Long", "Long")
        ]);

        var comparison = VbaCallableContractComparison.Compare(
            expected,
            found,
            VbaCallableContractComparisonPolicy.InterfaceFulfillment);

        var fact = Assert.Single(comparison.Facts);
        Assert.Equal(VbaCallableContractComparisonState.Incompatible, comparison.State);
        Assert.False(comparison.HasIndeterminateEvidence);
        Assert.Equal(VbaCallableContractComparisonSubject.ParameterList, fact.Subject);
        Assert.Equal(VbaCallableContractComparisonDimension.Count, fact.Dimension);
    }

    [Fact]
    public void Unmapped_parameter_retains_only_its_intrinsic_indeterminate_evidence()
    {
        var expected = new VbaCallableContract([
            RequiredParameter("Long", "Long")
        ]);
        var found = new VbaCallableContract([
            RequiredParameter("Long", "Long"),
            new VbaCallableContractParameter(
                Type: null,
                IsArray: false,
                IsByRef: false,
                VbaCallableContractParameterRole.Required,
                VbaCallableContractDefault.Absent)
        ]);

        var comparison = VbaCallableContractComparison.Compare(
            expected,
            found,
            VbaCallableContractComparisonPolicy.InterfaceFulfillment);

        Assert.Equal(VbaCallableContractComparisonState.Incompatible, comparison.State);
        Assert.True(comparison.HasIndeterminateEvidence);
        Assert.Equal(
            [
                (
                    VbaCallableContractComparisonFactOutcome.Mismatch,
                    VbaCallableContractComparisonDimension.Count),
                (
                    VbaCallableContractComparisonFactOutcome.Indeterminate,
                    VbaCallableContractComparisonDimension.CanonicalType)
            ],
            comparison.Facts.Select(fact => (fact.Outcome, fact.Dimension)));
        Assert.Equal(2, comparison.Facts[1].ParameterOrdinal);
    }

    [Fact]
    public void Missing_property_value_contract_cannot_be_hidden_by_ordinary_parameter_count()
    {
        var expected = new VbaCallableContract(
            [],
            PropertyValueParameter: RequiredParameter("Long", "expected"));
        var found = new VbaCallableContract([
            RequiredParameter("Long", "found")
        ]);

        var comparison = VbaCallableContractComparison.Compare(
            expected,
            found,
            VbaCallableContractComparisonPolicy.InterfaceFulfillment);

        var fact = Assert.Single(comparison.Facts);
        Assert.Equal(VbaCallableContractComparisonState.Incompatible, comparison.State);
        Assert.False(comparison.HasIndeterminateEvidence);
        Assert.Equal(
            VbaCallableContractComparisonFactOutcome.Mismatch,
            fact.Outcome);
        Assert.Equal(
            VbaCallableContractComparisonSubject.PropertyValue,
            fact.Subject);
        Assert.Equal(VbaCallableContractComparisonDimension.Presence, fact.Dimension);
        Assert.Equal(
            ["value parameter presence: expected present, found absent"],
            VbaCallableContractComparisonFormatter.FormatMismatchReasons(comparison));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void One_sided_result_contract_is_a_structural_mismatch(
        bool expectedHasResult)
    {
        var result = new VbaCallableContractResult(
            Type("Long", "Long"),
            IsArray: false);
        var expected = new VbaCallableContract(
            [],
            Result: expectedHasResult ? result : null);
        var found = new VbaCallableContract(
            [],
            Result: expectedHasResult ? null : result);

        var comparison = VbaCallableContractComparison.Compare(
            expected,
            found,
            VbaCallableContractComparisonPolicy.InterfaceFulfillment);

        var fact = Assert.Single(comparison.Facts);
        Assert.Equal(VbaCallableContractComparisonState.Incompatible, comparison.State);
        Assert.False(comparison.HasIndeterminateEvidence);
        Assert.Equal(
            VbaCallableContractComparisonFactOutcome.Mismatch,
            fact.Outcome);
        Assert.Equal(VbaCallableContractComparisonSubject.Result, fact.Subject);
        Assert.Equal(VbaCallableContractComparisonDimension.Presence, fact.Dimension);
    }

    [Fact]
    public void Unavailable_whole_contract_evidence_is_a_structured_fact()
    {
        var comparison = VbaCallableContractComparisonResult
            .UnavailableContractEvidence();

        var fact = Assert.Single(comparison.Facts);
        Assert.Equal(VbaCallableContractComparisonState.Indeterminate, comparison.State);
        Assert.Equal(
            VbaCallableContractComparisonFactOutcome.Indeterminate,
            fact.Outcome);
        Assert.Equal(VbaCallableContractComparisonSubject.Contract, fact.Subject);
        Assert.Equal(
            VbaCallableContractComparisonDimension.Availability,
            fact.Dimension);
    }

    [Fact]
    public void Interface_policy_formats_property_value_facts_in_stable_order()
    {
        var expected = new VbaCallableContract(
            [],
            PropertyValueParameter: new VbaCallableContractParameter(
                Type("Long", "expected"),
                IsArray: true,
                IsByRef: true,
                VbaCallableContractParameterRole.Optional,
                EvaluatedDefault("1")));
        var found = new VbaCallableContract(
            [],
            PropertyValueParameter: new VbaCallableContractParameter(
                Type("String", "found"),
                IsArray: false,
                IsByRef: false,
                VbaCallableContractParameterRole.Required,
                VbaCallableContractDefault.Absent));

        var comparison = VbaCallableContractComparison.Compare(
            expected,
            found,
            VbaCallableContractComparisonPolicy.InterfaceFulfillment);

        Assert.Equal(
            [
                "value parameter type: expected Long, found String",
                "value parameter array shape: expected array, found scalar",
                "value parameter passing: expected ByRef, found ByVal",
                "value parameter role: expected Optional, found required",
                "value parameter default: expected 1, found no default"
            ],
            VbaCallableContractComparisonFormatter.FormatMismatchReasons(comparison));
    }

    [Fact]
    public void Evaluated_defaults_compare_values_instead_of_source_spelling()
    {
        var expected = ContractWithDefault("1 + 1");
        var found = ContractWithDefault("2");

        var comparison = VbaCallableContractComparison.Compare(
            expected,
            found,
            VbaCallableContractComparisonPolicy.InterfaceFulfillment);

        Assert.Equal(VbaCallableContractComparisonState.Compatible, comparison.State);
        Assert.Empty(comparison.Facts);
    }

    [Fact]
    public void Portable_type_identity_without_matching_catalog_evidence_is_indeterminate()
    {
        var expected = ContractWithType(new VbaCallableContractType(
            "Payload",
            Identity: "same-identity",
            ReferenceQualifiedName: "Library.Payload",
            IsPortableTypeLibraryIdentity: true));
        var found = ContractWithType(new VbaCallableContractType(
            "Payload",
            Identity: "same-identity",
            ReferenceQualifiedName: "Project.Payload",
            IsUnmappedProjectReferenceIdentity: true));

        var comparison = VbaCallableContractComparison.Compare(
            expected,
            found,
            VbaCallableContractComparisonPolicy.EventHandler);

        var fact = Assert.Single(comparison.Facts);
        Assert.Equal(VbaCallableContractComparisonState.Indeterminate, comparison.State);
        Assert.Equal(
            VbaCallableContractComparisonFactOutcome.Indeterminate,
            fact.Outcome);
        Assert.Equal(
            VbaCallableContractComparisonDimension.CanonicalType,
            fact.Dimension);
    }

    [Fact]
    public void Equal_type_labels_use_reference_qualified_mismatch_presentations()
    {
        var expected = ContractWithType(new VbaCallableContractType(
            "Payload",
            Identity: "first",
            ReferenceQualifiedName: "First.Payload"));
        var found = ContractWithType(new VbaCallableContractType(
            "Payload",
            Identity: "second",
            ReferenceQualifiedName: "Second.Payload"));

        var comparison = VbaCallableContractComparison.Compare(
            expected,
            found,
            VbaCallableContractComparisonPolicy.EventHandler);

        Assert.Equal(
            ["parameter 1 type: expected First.Payload, found Second.Payload"],
            VbaCallableContractComparisonFormatter.FormatMismatchReasons(comparison));
    }

    [Fact]
    public void Equal_reference_qualified_labels_still_identify_distinct_canonical_types()
    {
        var expected = ContractWithType(new VbaCallableContractType(
            "Payload",
            Identity: "first",
            ReferenceQualifiedName: "Library.Payload"));
        var found = ContractWithType(new VbaCallableContractType(
            "Payload",
            Identity: "second",
            ReferenceQualifiedName: "Library.Payload"));

        var comparison = VbaCallableContractComparison.Compare(
            expected,
            found,
            VbaCallableContractComparisonPolicy.EventHandler);

        Assert.Equal(
            [
                "parameter 1 type: expected Library.Payload, "
                    + "found a distinct canonical identity named Library.Payload"
            ],
            VbaCallableContractComparisonFormatter.FormatMismatchReasons(comparison));
    }

    private static VbaCallableContract ContractWithDefault(string expression)
        => new([
            new VbaCallableContractParameter(
                Type("Long", "Long"),
                IsArray: false,
                IsByRef: false,
                VbaCallableContractParameterRole.Optional,
                EvaluatedDefault(expression))
        ]);

    private static VbaCallableContract ContractWithType(
        VbaCallableContractType type)
        => new([
            new VbaCallableContractParameter(
                type,
                IsArray: false,
                IsByRef: false,
                VbaCallableContractParameterRole.Required,
                VbaCallableContractDefault.Absent)
        ]);

    private static VbaCallableContractParameter RequiredParameter(
        string typeName,
        object identity)
        => new(
            Type(typeName, identity),
            IsArray: false,
            IsByRef: true,
            VbaCallableContractParameterRole.Required,
            VbaCallableContractDefault.Absent);

    private static VbaCallableContractType Type(string name, object identity)
        => new(name, identity);

    private static VbaCallableContractDefault EvaluatedDefault(string expression)
    {
        var defaultEvidence = VbaCallableContractDefault.FromExpression(expression);
        Assert.Equal(
            VbaCallableContractDefaultState.Evaluated,
            defaultEvidence.State);
        return defaultEvidence;
    }
}
