using System.Text.Json;
using VbaTools.Semantics;
using VbaTools.Syntax;
using Xunit;

namespace VbaDev.Tests;

public sealed class SemanticNamespaceConformanceTests
{
    public static IEnumerable<object[]> Cases() => ReadCases().Select(item => new object[] { item.GetProperty("id").GetString()! });

    [Theory]
    [MemberData(nameof(Cases))]
    public void SharedNamespaceDiagnosticsMatchTheIndependentLiteralCorpus(string id)
    {
        var fixture = ReadCases().Single(item => item.GetProperty("id").GetString() == id);
        var uri = "file:///C:/module-namespace-fixtures/" + fixture.GetProperty("fileName").GetString();
        var tree = VbaSyntaxTree.ParseModule(uri, fixture.GetProperty("source").GetString()!);
        var containingName = fixture.GetProperty("containingProjectName").GetString();
        var namespaces = fixture.GetProperty("kind").GetString() == "manifest" && containingName is null
            ? null : VbaProjectNamespaceIdentity.Capture(containingName,
                fixture.GetProperty("references").EnumerateArray().Select(reference =>
                    new VbaReferencedProjectNamespace(reference.GetProperty("referenceName").GetString()!,
                        reference.GetProperty("name").GetString()!)));
        var inputs = VbaProjectSemanticInputs.Capture(null, VbaProjectReferenceCatalogSet.Empty,
            projectNamespaces: namespaces);

        var actual = VbaProjectSourceAnalysis.Analyze([tree], inputs);

        var expected = fixture.GetProperty("diagnostics").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, actual.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            var diagnostic = actual[index];
            Assert.Equal(uri, diagnostic.SourceUri);
            Assert.Equal(expected[index].GetProperty("code").GetString(), diagnostic.Code);
            Assert.Equal(expected[index].GetProperty("message").GetString(), diagnostic.Message);
            Assert.Equal(expected[index].GetProperty("severity").GetString(), diagnostic.Severity);
            var range = expected[index].GetProperty("range");
            Assert.Equal(range.GetProperty("start").GetProperty("line").GetInt32(), diagnostic.Range.Start.Line);
            Assert.Equal(range.GetProperty("start").GetProperty("character").GetInt32(), diagnostic.Range.Start.Character);
            Assert.Equal(range.GetProperty("end").GetProperty("line").GetInt32(), diagnostic.Range.End.Line);
            Assert.Equal(range.GetProperty("end").GetProperty("character").GetInt32(), diagnostic.Range.End.Character);
            Assert.Null(diagnostic.Details);
        }
    }

    private static JsonElement[] ReadCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "fixtures", "project-semantic-diagnostics", "namespace-cases.json")));
        Assert.Equal("1.0", document.RootElement.GetProperty("schemaVersion").GetString());
        return document.RootElement.GetProperty("cases").EnumerateArray().Select(item => item.Clone()).ToArray();
    }
}
