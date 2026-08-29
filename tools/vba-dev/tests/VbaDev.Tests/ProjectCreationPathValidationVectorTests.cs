using System.Text;
using System.Text.Json;
using VbaDev.Domain;
using Xunit;

namespace VbaDev.Tests;

public sealed class ProjectCreationPathValidationVectorTests
{
    [Fact]
    public void SharedProjectNameVectorsMatchTheLexicalContract()
    {
        var fixtureSet = ReadFixtureSet();

        Assert.Equal("1.0", fixtureSet.SchemaVersion);
        foreach (var testCase in fixtureSet.ProjectNameCases)
        {
            var result = ProjectNameLexicalContract.Validate(Materialize(testCase));

            Assert.Equal(testCase.ExpectedReason is null, result.IsValid);
            Assert.Equal(testCase.ExpectedReason, result.Reason);
        }
    }

    [Fact]
    public void SharedExcelWorkbookPathVectorsMatchTheExcelContract()
    {
        var fixtureSet = ReadFixtureSet();

        Assert.Equal("1.0", fixtureSet.SchemaVersion);
        foreach (var testCase in fixtureSet.ExcelWorkbookPathCases)
        {
            var candidate = Materialize(testCase);
            var result = ExcelWorkbookPathContract.Validate(candidate);

            Assert.Equal(testCase.ExpectedUtf16CodeUnitLength, candidate.Length);
            Assert.Equal(testCase.ExpectedReason is null, result.IsValid);
            Assert.Equal(testCase.ExpectedReason, result.Reason);
        }
    }

    private static ProjectCreationPathValidationFixtureSet ReadFixtureSet()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "fixtures",
            "project-creation-path-validation",
            "v1",
            "fixture-set.json"));

        return JsonSerializer.Deserialize<ProjectCreationPathValidationFixtureSet>(
            File.ReadAllText(fixturePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static string Materialize(ProjectCreationPathValidationFixtureCase testCase)
    {
        if (testCase.Utf16CodeUnits is not null)
        {
            return new string(testCase.Utf16CodeUnits.Select(codeUnit => (char)codeUnit).ToArray());
        }

        var value = new StringBuilder(testCase.Value);
        if (testCase.RepeatCodeUnit is not null)
        {
            value.Append((char)testCase.RepeatCodeUnit.Value, testCase.RepeatCount ?? 0);
        }

        value.Append(testCase.Suffix);
        return value.ToString();
    }

    private sealed record ProjectCreationPathValidationFixtureSet(
        string SchemaVersion,
        ProjectCreationPathValidationFixtureCase[] ProjectNameCases,
        ProjectCreationPathValidationFixtureCase[] ExcelWorkbookPathCases);

    private sealed record ProjectCreationPathValidationFixtureCase(
        string Id,
        string? Value,
        int[]? Utf16CodeUnits,
        int? RepeatCodeUnit,
        int? RepeatCount,
        string? Suffix,
        string? ExpectedReason,
        int? ExpectedUtf16CodeUnitLength);
}
