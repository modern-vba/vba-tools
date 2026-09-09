using System.Text.Json;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.SourceModel;
using VbaTools.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class DocumentDiagnosticConformanceTests
{
    public static IEnumerable<object[]> ConformanceCases()
        => ReadCases().Select(fixture => new object[] { fixture.GetProperty("id").GetString()! });

    [Theory]
    [MemberData(nameof(ConformanceCases))]
    public void CapturedDocumentDiagnosticsMatchTheNeutralLiteralCorpus(string caseId)
    {
        var fixture = ReadCases().Single(item => item.GetProperty("id").GetString() == caseId);
        var source = fixture.GetProperty("source").GetString()!;
        var uri = "file:///C:/diagnostic-fixtures/" + fixture.GetProperty("fileName").GetString();
        var tree = VbaSyntaxTree.ParseModule(uri, source);
        var expectedSyntax = ReadDiagnostics(fixture, "syntaxDiagnostics");
        var expectedValidation = ReadDiagnostics(fixture, "documentValidationDiagnostics");
        var expectedCombined = expectedSyntax.Concat(expectedValidation).ToArray();
        if (caseId != "valid-control") Assert.NotEmpty(expectedCombined);

        var syntax = VbaSyntaxDiagnosticCollector.Collect(tree, uri);
        var validation = VbaDocumentValidationDiagnosticCollector.Collect(tree, uri);
        var combined = VbaDocumentDiagnostics.Collect(tree, uri);

        Assert.Equal(expectedSyntax, syntax.Select(diagnostic => Snapshot(
            diagnostic.Code, diagnostic.Message, diagnostic.Severity, diagnostic.Range)).ToArray());
        Assert.Equal(expectedValidation, validation.Select(diagnostic => Snapshot(
            diagnostic.Code, diagnostic.Message, diagnostic.Severity, diagnostic.Range)).ToArray());
        Assert.Equal(expectedCombined, combined.Select(diagnostic => Snapshot(
            diagnostic.Code, diagnostic.Message, diagnostic.Severity, diagnostic.Range)).ToArray());
        Assert.All(syntax, diagnostic => Assert.Equal("vba-language-server", diagnostic.Source));
        Assert.All(validation, diagnostic => Assert.Equal("vba-language-server", diagnostic.Source));
        Assert.All(combined, diagnostic => Assert.Equal("vba-language-server", diagnostic.Source));

        var inventory = VbaSemanticInventory.Create(
            new Dictionary<string, VbaSourceDocument> { [uri] = VbaSourceDocumentProjector.Project(uri, tree) },
            referenceSelection: null, referenceCatalogs: VbaProjectReferenceCatalogSet.Empty);
        Assert.Equal(ReadDiagnostics(fixture, "projectValidationDiagnostics"),
            inventory.GetProjectValidationDiagnostics(uri).Select(diagnostic =>
                Snapshot(diagnostic.Code, diagnostic.Message, diagnostic.Severity, diagnostic.Range)).ToArray());
    }

    private static JsonElement[] ReadCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "fixtures", "document-diagnostics", "cases.json")));
        Assert.Equal("1.0", document.RootElement.GetProperty("schemaVersion").GetString());
        return document.RootElement.GetProperty("cases").EnumerateArray()
            .Select(fixture => fixture.Clone()).ToArray();
    }

    private static DiagnosticSnapshot[] ReadDiagnostics(JsonElement fixture, string category)
        => fixture.GetProperty(category).EnumerateArray().Select(diagnostic =>
        {
            var range = diagnostic.GetProperty("range");
            var start = range.GetProperty("start");
            var end = range.GetProperty("end");
            return new DiagnosticSnapshot(
                diagnostic.GetProperty("code").GetString()!,
                diagnostic.GetProperty("message").GetString()!,
                diagnostic.GetProperty("severity").GetString()!,
                start.GetProperty("line").GetInt32(), start.GetProperty("character").GetInt32(),
                end.GetProperty("line").GetInt32(), end.GetProperty("character").GetInt32());
        }).ToArray();

    private static DiagnosticSnapshot Snapshot(string code, string message, string severity, VbaRange range)
        => new(code, message, severity, range.Start.Line, range.Start.Character, range.End.Line, range.End.Character);

    private sealed record DiagnosticSnapshot(
        string Code, string Message, string Severity,
        int StartLine, int StartCharacter, int EndLine, int EndCharacter);
}
