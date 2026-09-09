using System.Text.Json;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using VbaTools.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class ModuleNamespaceDiagnosticConformanceTests
{
    public static IEnumerable<object[]> Cases() => ReadCases().Select(item => new object[] { item.GetProperty("id").GetString()! });

    [Theory]
    [MemberData(nameof(Cases))]
    public void ModuleNamespaceDiagnosticsMatchTheNeutralLiteralCorpus(string id)
    {
        var fixture = ReadCases().Single(item => item.GetProperty("id").GetString() == id);
        var uri = "file:///C:/module-namespace-fixtures/" + fixture.GetProperty("fileName").GetString();
        var tree = VbaSyntaxTree.ParseModule(uri, fixture.GetProperty("source").GetString()!);
        var references = fixture.GetProperty("references").EnumerateArray().ToArray();
        var selection = VbaProjectReferenceSelection.Create("excel", references.Select(reference =>
            new VbaProjectReference(reference.GetProperty("referenceName").GetString()!)).ToArray());
        var inventory = VbaSemanticInventory.Create(new Dictionary<string, VbaSourceDocument>
        {
            [uri] = VbaSourceDocumentProjector.Project(uri, tree)
        }, selection, VbaProjectReferenceCatalogSet.Empty,
            projectResolution: new(fixture.GetProperty("kind").GetString() == "manifest"
                ? VbaProjectResolutionKind.ManifestDocument : VbaProjectResolutionKind.AdHoc,
                "C:/module-namespace-fixtures"),
            authoritativeReferencedProjectNames: references.ToDictionary(
                reference => reference.GetProperty("referenceName").GetString()!,
                reference => reference.GetProperty("name").GetString()!));
        var name = fixture.GetProperty("containingProjectName").GetString();
        var identity = name is null ? null : VbaProjectIdentityReadResult.Success(new(name, 1252,
            VbaSourceTemplateContentIdentity.FromBytes([1, 2, 3])));

        var actual = inventory.GetProjectValidationDiagnostics(uri, identity);

        var expected = fixture.GetProperty("diagnostics").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, actual.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            var diagnostic = actual[index];
            Assert.Equal(expected[index].GetProperty("code").GetString(), diagnostic.Code);
            Assert.Equal(expected[index].GetProperty("message").GetString(), diagnostic.Message);
            Assert.Equal(expected[index].GetProperty("severity").GetString(), diagnostic.Severity);
            Assert.Equal(ReadRange(expected[index].GetProperty("range")), diagnostic.Range);
            Assert.Equal("vba-language-server", diagnostic.Source);
            Assert.Null(diagnostic.Details);
            using var data = JsonDocument.Parse(JsonSerializer.Serialize(diagnostic.Data));
            Assert.True(JsonElement.DeepEquals(expected[index].GetProperty("data"), data.RootElement));
        }
    }

    private static VbaRange ReadRange(JsonElement range)
        => new(new(range.GetProperty("start").GetProperty("line").GetInt32(), range.GetProperty("start").GetProperty("character").GetInt32()),
            new(range.GetProperty("end").GetProperty("line").GetInt32(), range.GetProperty("end").GetProperty("character").GetInt32()));

    private static JsonElement[] ReadCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "fixtures", "project-semantic-diagnostics", "namespace-cases.json")));
        Assert.Equal("1.0", document.RootElement.GetProperty("schemaVersion").GetString());
        return document.RootElement.GetProperty("cases").EnumerateArray().Select(item => item.Clone()).ToArray();
    }
}
