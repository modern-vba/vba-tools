using System.Text.Json;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.SourceModel;
using VbaTools.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class ProjectSemanticDiagnosticConformanceTests
{
    public static IEnumerable<object[]> ConformanceCases()
        => ReadCases().Select(fixture => new object[] { fixture.GetProperty("id").GetString()! });

    [Theory]
    [MemberData(nameof(ConformanceCases))]
    public void CapturedSourceSemanticDiagnosticsMatchTheNeutralLiteralCorpus(string caseId)
    {
        var fixture = ReadCases().Single(item => item.GetProperty("id").GetString() == caseId);
        var sources = fixture.GetProperty("sources").EnumerateArray().Select(source =>
        {
            var fileName = source.GetProperty("fileName").GetString()!;
            var uri = "file:///C:/project-semantic-fixtures/" + fileName;
            var text = source.GetProperty("source").GetString()!;
            var tree = VbaSyntaxTree.ParseModule(uri, text);
            Assert.Empty(VbaDiagnosticPipeline.CollectDocument(tree, uri).Diagnostics);
            return (FileName: fileName, Uri: uri, Document: VbaSourceDocumentProjector.Project(uri, tree));
        }).ToArray();
        var documents = sources.ToDictionary(source => source.Uri, source => source.Document,
            StringComparer.OrdinalIgnoreCase);
        var catalogs = VbaProjectReferenceCatalogSet.Empty;
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var names = new List<VbaProjectReference>();
        if (fixture.TryGetProperty("referenceCatalogs", out var catalogData))
        {
            foreach (var entry in catalogData.EnumerateArray())
            {
                var catalog = entry.Deserialize<VbaProjectReferenceCatalog>(jsonOptions)!;
                catalogs = catalogs.WithCatalog(catalog);
                names.Add(new(catalog.ReferenceName));
            }
        }
        var host = fixture.TryGetProperty("hostEvents", out var hostData)
            ? hostData.Deserialize<VbaIntrinsicHostEventCatalog>(jsonOptions) : null;
        var inventory = VbaSemanticInventory.Create(documents,
            referenceSelection: names.Count == 0 ? null : VbaProjectReferenceSelection.Create("word", names),
            referenceCatalogs: catalogs, intrinsicHostEventCatalog: host);
        if (caseId.StartsWith("external-", StringComparison.Ordinal))
        {
            var target = Assert.IsAssignableFrom<VbaResolvedNameTarget>(inventory.ResolveSourceTarget(sources[0].Uri, 4, 11));
            Assert.Equal("Exists", target.CanonicalName);
            Assert.Equal(VbaDefinitionOrigin.ProjectReference, target.SelectedDefinition.Identity.Origin);
        }
        var expected = fixture.GetProperty("diagnostics").EnumerateArray().Select(diagnostic =>
        {
            var range = diagnostic.GetProperty("range");
            var start = range.GetProperty("start");
            var end = range.GetProperty("end");
            return new DiagnosticSnapshot(
                diagnostic.GetProperty("fileName").GetString()!,
                diagnostic.GetProperty("code").GetString()!,
                diagnostic.GetProperty("message").GetString()!,
                diagnostic.GetProperty("severity").GetString()!,
                start.GetProperty("line").GetInt32(), start.GetProperty("character").GetInt32(),
                end.GetProperty("line").GetInt32(), end.GetProperty("character").GetInt32());
        }).ToArray();

        var findings = sources.SelectMany(source => inventory.GetProjectValidationDiagnostics(source.Uri)
            .Select(diagnostic => (source.FileName, Diagnostic: diagnostic))).ToArray();
        var actual = findings
            .Select(diagnostic =>
            {
                var value = diagnostic.Diagnostic;
                Assert.Equal("vba-language-server", value.Source);
                return new DiagnosticSnapshot(diagnostic.FileName, value.Code, value.Message,
                    value.Severity, value.Range.Start.Line, value.Range.Start.Character,
                    value.Range.End.Line, value.Range.End.Character);
            }).ToArray();

        Assert.Equal(expected, actual);
        var expectedFindings = fixture.GetProperty("diagnostics").EnumerateArray().ToArray();
        for (var index = 0; index < findings.Length; index++)
        {
            var details = expectedFindings[index].GetProperty("details");
            if (details.ValueKind == JsonValueKind.Null)
            {
                Assert.Null(findings[index].Diagnostic.Details);
                continue;
            }
            var actualDetails = findings[index].Diagnostic.Details;
            Assert.NotNull(actualDetails);
            Assert.Equal(details.EnumerateArray().Select(ReadDetail).ToArray(),
                actualDetails.Select(detail => new DetailSnapshot(
                    detail.Location?.Uri,
                    detail.Location?.Range.Start.Line, detail.Location?.Range.Start.Character,
                    detail.Location?.Range.End.Line, detail.Location?.Range.End.Character,
                    detail.RelatedMessage, detail.FallbackText)).ToArray());
        }
    }

    private static DetailSnapshot ReadDetail(JsonElement detail)
    {
        var location = detail.GetProperty("location");
        var relatedMessage = detail.GetProperty("relatedMessage").GetString()!;
        var fallbackText = detail.GetProperty("fallbackText").GetString()!;
        if (location.ValueKind == JsonValueKind.Null)
        {
            return new(null, null, null, null, null, relatedMessage, fallbackText);
        }
        var range = location.GetProperty("range");
        return new("file:///C:/project-semantic-fixtures/" + location.GetProperty("fileName").GetString(),
            range.GetProperty("start").GetProperty("line").GetInt32(),
            range.GetProperty("start").GetProperty("character").GetInt32(),
            range.GetProperty("end").GetProperty("line").GetInt32(),
            range.GetProperty("end").GetProperty("character").GetInt32(), relatedMessage, fallbackText);
    }

    private sealed record DetailSnapshot(string? Uri, int? StartLine, int? StartCharacter,
        int? EndLine, int? EndCharacter, string RelatedMessage, string FallbackText);

    private static JsonElement[] ReadCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "fixtures", "project-semantic-diagnostics", "cases.json")));
        Assert.Equal("1.0", document.RootElement.GetProperty("schemaVersion").GetString());
        return document.RootElement.GetProperty("cases").EnumerateArray()
            .Select(fixture => fixture.Clone()).ToArray();
    }

    private sealed record DiagnosticSnapshot(
        string FileName, string Code, string Message, string Severity,
        int StartLine, int StartCharacter, int EndLine, int EndCharacter);
}
