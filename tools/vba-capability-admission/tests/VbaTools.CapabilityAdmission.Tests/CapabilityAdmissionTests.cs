using System.Text.Json;
using VbaTools.Capabilities;
using Xunit;

namespace VbaTools.Capabilities.Tests;

public sealed class CapabilityAdmissionTests
{
    private static readonly CapabilityRequirements ReferenceList = new(
        commandSchemaVersions: new Dictionary<string, string> { ["reference list"] = "1.0" });
    private static readonly CapabilityRequirements SnapshotBuild = new(
        featureVersions: new Dictionary<string, string>
        {
            ["build.sourceSnapshot"] = "2.0", ["build.sourceSnapshotAnalysis"] = "1.0"
        });

    [Fact]
    public void Admits_the_minimum_reference_list_response_as_consumed_facts()
    {
        var result = VbaDevCapabilityAdmission.Admit(
            """{"commands":{"reference list":{"outputSchemaVersion":"1.0"}}}""", ReferenceList);

        Assert.True(result.IsAccepted);
        Assert.Null(result.Rejection);
        var facts = Assert.IsType<CapabilityFacts>(result.Facts);
        Assert.Equal("1.0", Assert.Single(facts.CommandSchemaVersions).Value);
        Assert.Empty(facts.FeatureVersions);
    }

    [Theory]
    [InlineData("\"extra\":1,\"extra\":1")]
    [InlineData("\"extra\":{\"nested\":1,\"nested\":2}")]
    [InlineData("\"extra\":[{\"nested\":1,\"nested\":1}]")]
    [InlineData("\"extra\":{\"escaped\":1,\"\\u0065scaped\":2}")]
    [InlineData("\"commands\":{\"reference list\":{\"outputSchemaVersion\":\"1.0\"}}")]
    public void A_duplicate_property_anywhere_rejects_the_complete_response(string additionalProperties)
    {
        var result = VbaDevCapabilityAdmission.Admit(
            "{\"commands\":{\"reference list\":{\"outputSchemaVersion\":\"1.0\"}}," + additionalProperties + "}",
            ReferenceList);

        Assert.False(result.IsAccepted);
        Assert.Null(result.Facts);
        Assert.Equal(CapabilityRejectionKind.DuplicateProperty, result.Rejection!.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"commands\":{},}")]
    [InlineData("{} trailing")]
    [InlineData("{/*comment*/}")]
    public void Invalid_json_is_classified_without_throwing(string rawJson)
    {
        var result = VbaDevCapabilityAdmission.Admit(rawJson, ReferenceList);

        Assert.False(result.IsAccepted);
        Assert.Null(result.Facts);
        Assert.Equal(CapabilityRejectionKind.InvalidJson, result.Rejection!.Kind);
    }

    [Theory]
    [InlineData("{}", CapabilityRejectionKind.MissingCapability)]
    [InlineData("{\"commands\":{}}", CapabilityRejectionKind.MissingCapability)]
    [InlineData("{\"commands\":{\"reference list\":{}}}", CapabilityRejectionKind.MissingCapability)]
    [InlineData("[]", CapabilityRejectionKind.InvalidConsumedValue)]
    [InlineData("null", CapabilityRejectionKind.InvalidConsumedValue)]
    [InlineData("{\"commands\":[]}", CapabilityRejectionKind.InvalidConsumedValue)]
    [InlineData("{\"commands\":{\"reference list\":null}}", CapabilityRejectionKind.InvalidConsumedValue)]
    [InlineData("{\"commands\":{\"reference list\":{\"outputSchemaVersion\":1}}}", CapabilityRejectionKind.InvalidConsumedValue)]
    [InlineData("{\"commands\":{\"reference list\":{\"outputSchemaVersion\":\"1.1\"}}}", CapabilityRejectionKind.VersionMismatch)]
    public void Required_command_versions_distinguish_missing_invalid_and_incompatible_values(
        string rawJson, CapabilityRejectionKind expected)
    {
        var result = VbaDevCapabilityAdmission.Admit(rawJson, ReferenceList);

        Assert.False(result.IsAccepted);
        Assert.Null(result.Facts);
        var rejection = Assert.IsType<CapabilityRejection>(result.Rejection);
        Assert.Equal(expected, rejection.Kind);
        Assert.NotEmpty(rejection.Field);
        if (expected == CapabilityRejectionKind.VersionMismatch)
        {
            Assert.Equal("1.0", rejection.Expected);
            Assert.Equal("1.1", rejection.Actual);
        }
    }

    [Fact]
    public void Admits_only_consumed_snapshot_features_without_requiring_commands_or_offer_order()
    {
        var result = VbaDevCapabilityAdmission.Admit("""
            {"featureVersions":{"additional":"99.0","build.sourceSnapshotAnalysis":"1.0","build.sourceSnapshot":"2.0"}}
            """, SnapshotBuild);

        Assert.True(result.IsAccepted);
        var facts = Assert.IsType<CapabilityFacts>(result.Facts);
        Assert.Empty(facts.CommandSchemaVersions);
        Assert.Equal(2, facts.FeatureVersions.Count);
        Assert.Equal("2.0", facts.FeatureVersions["build.sourceSnapshot"]);
        Assert.Equal("1.0", facts.FeatureVersions["build.sourceSnapshotAnalysis"]);
        Assert.False(facts.FeatureVersions.ContainsKey("additional"));
    }

    [Theory]
    [InlineData("{}", CapabilityRejectionKind.MissingCapability)]
    [InlineData("{\"featureVersions\":{}}", CapabilityRejectionKind.MissingCapability)]
    [InlineData("{\"featureVersions\":{\"build.sourceSnapshot\":\"2.0\"}}", CapabilityRejectionKind.MissingCapability)]
    [InlineData("{\"featureVersions\":[]}", CapabilityRejectionKind.InvalidConsumedValue)]
    [InlineData("{\"featureVersions\":{\"build.sourceSnapshot\":null}}", CapabilityRejectionKind.InvalidConsumedValue)]
    [InlineData("{\"featureVersions\":{\"build.sourceSnapshot\":\"2.1\"}}", CapabilityRejectionKind.VersionMismatch)]
    public void Required_features_distinguish_missing_invalid_and_incompatible_values(
        string rawJson, CapabilityRejectionKind expected)
    {
        var result = VbaDevCapabilityAdmission.Admit(rawJson, SnapshotBuild);

        Assert.False(result.IsAccepted);
        Assert.Null(result.Facts);
        Assert.Equal(expected, result.Rejection!.Kind);
        if (expected == CapabilityRejectionKind.VersionMismatch)
        {
            Assert.Equal("2.0", result.Rejection.Expected);
            Assert.Equal("2.1", result.Rejection.Actual);
        }
    }

    [Theory]
    [InlineData("{\"unknown\":{\"\\ud800\":1}}")]
    [InlineData("{\"same\":1,\"same\":1,\"unknown\":[{\"\\udfff\":1}]}")]
    public void An_undecodable_property_name_is_invalid_json_even_after_a_duplicate(string rawJson)
    {
        var result = VbaDevCapabilityAdmission.Admit(rawJson, ReferenceList);

        Assert.Equal(CapabilityRejectionKind.InvalidJson, result.Rejection!.Kind);
        Assert.Null(result.Facts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_undecodable_consumed_version_is_an_invalid_consumed_value(bool feature)
    {
        var rawJson = feature
            ? "{\"featureVersions\":{\"build.sourceSnapshot\":\"\\udfff\"}}"
            : "{\"commands\":{\"reference list\":{\"outputSchemaVersion\":\"\\ud800\"}}}";
        var result = VbaDevCapabilityAdmission.Admit(rawJson, feature ? SnapshotBuild : ReferenceList);

        Assert.Equal(CapabilityRejectionKind.InvalidConsumedValue, result.Rejection!.Kind);
        Assert.Null(result.Facts);
    }

    [Fact]
    public void Raw_incomplete_utf16_is_invalid_json_in_keys_values_and_unknown_properties()
    {
        foreach (var surrogate in new[] { '\ud800', '\udfff' })
        {
            foreach (var rawJson in new[] { "{\"" + surrogate + "\":1}", "{\"unknown\":\"" + surrogate + "\"}" })
            {
                var result = VbaDevCapabilityAdmission.Admit(rawJson, ReferenceList);

                Assert.Equal(CapabilityRejectionKind.InvalidJson, result.Rejection!.Kind);
                Assert.Null(result.Facts);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deep_unknown_values_are_checked_without_an_artificial_nesting_limit(bool duplicate)
    {
        var nestedObject = duplicate ? "{\"key\":1,\"key\":1}" : "{\"key\":1}";
        var rawJson = "{\"commands\":{\"reference list\":{\"outputSchemaVersion\":\"1.0\"}},\"unknown\":"
            + new string('[', 1024) + nestedObject + new string(']', 1024) + "}";

        var result = VbaDevCapabilityAdmission.Admit(rawJson, ReferenceList);

        Assert.Equal(!duplicate, result.IsAccepted);
        if (duplicate) { Assert.Equal(CapabilityRejectionKind.DuplicateProperty, result.Rejection!.Kind); }
    }

    [Theory]
    [MemberData(nameof(SharedCases))]
    public void Shared_raw_json_conforms_to_the_consuming_profile(
        string caseId, string profile, string rawJson, string expected)
    {
        var requirements = profile == "refList" ? ReferenceList : SnapshotBuild;
        var result = VbaDevCapabilityAdmission.Admit(rawJson, requirements);
        var actual = result.IsAccepted ? "Accepted" : result.Rejection!.Kind.ToString();

        Assert.True(expected == actual, $"{caseId}/{profile}: expected {expected}, received {actual}.");
        Assert.Equal(result.IsAccepted, result.Facts is not null);
        Assert.Equal(!result.IsAccepted, result.Rejection is not null);
    }

    public static IEnumerable<object[]> SharedCases()
    {
        var corpusPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "capability-admission", "cases.json");
        using var document = JsonDocument.Parse(File.ReadAllText(corpusPath));
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            foreach (var profile in new[] { "refList", "snapshotBuild" })
            {
                if (item.GetProperty("expectedByProfile").TryGetProperty(profile, out var expected))
                {
                    yield return [item.GetProperty("id").GetString()!, profile,
                        item.GetProperty("rawJson").GetString()!, expected.GetString()!];
                }
            }
        }
    }

    [Fact]
    public void Caller_mutation_and_later_admissions_cannot_change_consumed_facts_or_requirements()
    {
        var commandVersions = new Dictionary<string, string> { ["reference list"] = "1.0" };
        var featureVersions = new Dictionary<string, string> { ["build.sourceSnapshot"] = "2.0" };
        var requirements = new CapabilityRequirements(commandVersions, featureVersions);
        commandVersions["reference list"] = "9.0";
        featureVersions.Clear();

        var accepted = VbaDevCapabilityAdmission.Admit("""
            {"commands":{"reference list":{"outputSchemaVersion":"1.0"}},"featureVersions":{"build.sourceSnapshot":"2.0"}}
            """, requirements);
        var facts = Assert.IsType<CapabilityFacts>(accepted.Facts);
        Assert.Equal("1.0", requirements.CommandSchemaVersions["reference list"]);
        Assert.Equal("2.0", requirements.FeatureVersions["build.sourceSnapshot"]);
        foreach (var map in new[] { facts.CommandSchemaVersions, facts.FeatureVersions,
            requirements.CommandSchemaVersions, requirements.FeatureVersions })
        {
            if (map is IDictionary<string, string> dictionary)
            {
                Assert.Throws<NotSupportedException>(() => dictionary.Clear());
            }
        }
        Assert.False(VbaDevCapabilityAdmission.Admit("{}", requirements).IsAccepted);
        Assert.Equal("1.0", Assert.Single(facts.CommandSchemaVersions).Value);
        Assert.Equal("2.0", Assert.Single(facts.FeatureVersions).Value);
    }

    [Fact]
    public void Null_arguments_are_caller_errors_and_empty_requirements_still_check_the_response()
    {
        Assert.Equal("rawJson", Assert.Throws<ArgumentNullException>(() =>
            VbaDevCapabilityAdmission.Admit(null!, ReferenceList)).ParamName);
        Assert.Equal("requirements", Assert.Throws<ArgumentNullException>(() =>
            VbaDevCapabilityAdmission.Admit("{}", null!)).ParamName);

        var noRequirements = new CapabilityRequirements();
        Assert.True(VbaDevCapabilityAdmission.Admit("{}", noRequirements).IsAccepted);
        Assert.Equal(CapabilityRejectionKind.DuplicateProperty,
            VbaDevCapabilityAdmission.Admit("{\"same\":1,\"same\":1}", noRequirements).Rejection!.Kind);
    }
}
