using System.Text.Json;
using VbaTools.Syntax;
using Xunit;

namespace VbaTools.Syntax.Tests;

public sealed class DocumentDiagnosticConformanceTests
{
    public static IEnumerable<object[]> ConformanceCases()
        => ReadCases().Select(fixture => new object[] { fixture.GetProperty("id").GetString()! });

    [Theory]
    [MemberData(nameof(ConformanceCases))]
    public void NeutralDocumentDiagnosticsMatchTheLiteralCorpus(string caseId)
    {
        var fixture = ReadCases().Single(item => item.GetProperty("id").GetString() == caseId);
        var source = fixture.GetProperty("source").GetString()!;
        var uri = "file:///C:/diagnostic-fixtures/" + fixture.GetProperty("fileName").GetString();
        var tree = VbaSyntaxTree.ParseModule(uri, source);
        var expectedSyntax = ReadDiagnostics(fixture, "syntaxDiagnostics");
        var expectedValidation = ReadDiagnostics(fixture, "documentValidationDiagnostics");
        if (caseId != "valid-control") Assert.NotEmpty(expectedSyntax.Concat(expectedValidation));

        var validation = VbaDocumentValidationDiagnostics.Collect(tree);

        Assert.Equal(expectedSyntax, tree.Diagnostics.Select(diagnostic => Snapshot(
            diagnostic.Code, diagnostic.Message, diagnostic.Severity, diagnostic.Range)).ToArray());
        Assert.Equal(expectedValidation, validation.Select(diagnostic => Snapshot(
            diagnostic.Code, diagnostic.Message, diagnostic.Severity, diagnostic.Range)).ToArray());
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

    private static DiagnosticSnapshot Snapshot(string code, string message, string severity, VbaSyntaxRange range)
        => new(code, message, severity, range.Start.Line, range.Start.Character, range.End.Line, range.End.Character);

    private sealed record DiagnosticSnapshot(
        string Code, string Message, string Severity,
        int StartLine, int StartCharacter, int EndLine, int EndCharacter);
}
