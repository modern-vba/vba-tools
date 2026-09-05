using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace VbaLanguageServer.Tests;

public sealed class ClosedSourceEncodingLanguageServerProcessTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    public async Task Server_uses_supported_BOM_authority_for_closed_source_hover(string encodingName)
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-closed-source-bom-").FullName;
        try
        {
            const string documentation = "Unicode 日本語 café 🙂";
            var sourcePath = Path.Combine(projectRoot, "Helper.bas");
            var encoding = encodingName switch
            {
                "utf8" => (Encoding)new UTF8Encoding(true, true),
                "utf16le" => new UnicodeEncoding(false, true, true),
                "utf16be" => new UnicodeEncoding(true, true, true),
                _ => throw new ArgumentOutOfRangeException(nameof(encodingName))
            };
            File.WriteAllText(sourcePath, CreateHelperSource(documentation), encoding);
            var callerUri = new Uri(Path.Combine(projectRoot, "Caller.bas")).AbsoluteUri;
            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen", CreateOpenDocument(callerUri, CallerSource));

            var hover = await RequestHelperHoverAsync(process, 2, callerUri);

            Assert.Contains(documentation, GetHoverText(hover));
            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_uses_actual_host_authority_for_ambiguous_BOMless_source()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-closed-source-host-acp-").FullName;
        try
        {
            const string utf8Documentation = "café";
            var sourcePath = Path.Combine(projectRoot, "Helper.bas");
            var sourceUri = new Uri(sourcePath).AbsoluteUri;
            var utf8 = new UTF8Encoding(false, true);
            File.WriteAllBytes(sourcePath, utf8.GetBytes(CreateHelperSource(utf8Documentation)));
            var callerUri = new Uri(Path.Combine(projectRoot, "Caller.bas")).AbsoluteUri;
            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();
            var checkpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync(
                "textDocument/didOpen", CreateOpenDocument(callerUri, CallerSource));

            var hover = await RequestHelperHoverAsync(process, 2, callerUri);

            if (OperatingSystem.IsWindows())
            {
                var activeCodePage = checked((int)GetACP());
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var authority = Encoding.GetEncoding(
                    activeCodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                var documentationBytes = utf8.GetBytes(utf8Documentation);
                var expectedDocumentation = authority.GetString(documentationBytes);
                Assert.Equal(documentationBytes, authority.GetBytes(expectedDocumentation));
                Assert.Contains(expectedDocumentation, GetHoverText(hover));
                if (activeCodePage != 65001)
                {
                    Assert.NotEqual(utf8Documentation, expectedDocumentation);
                    Assert.DoesNotContain(utf8Documentation, GetHoverText(hover));
                }

                output.WriteLine(
                    $"Native language-server process: actual GetACP {activeCodePage}; "
                    + $"BOM-less UTF-8 bytes use ACP text '{expectedDocumentation}'.");
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, hover.GetProperty("result").ValueKind);
                await process.WaitForDiagnosticsMatchingAsync(
                    sourceUri,
                    HasInvalidDiskSourceDiagnostic,
                    "BOM-less source rejected without Windows ACP authority",
                    afterCheckpoint: checkpoint);
                output.WriteLine("Native non-Windows language-server process: no BOM-less source authority.");
            }

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_preserves_open_Unicode_and_recovers_closed_source_diagnostics_after_reload_and_delete()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-closed-source-lifecycle-").FullName;
        try
        {
            var sourcePath = Path.Combine(projectRoot, "Helper.bas");
            var sourceUri = new Uri(sourcePath).AbsoluteUri;
            var callerUri = new Uri(Path.Combine(projectRoot, "Caller.bas")).AbsoluteUri;
            var utf8 = new UTF8Encoding(true, true);
            var invalidBytes = utf8.GetPreamble()
                .Concat(utf8.GetBytes(CreateHelperSource("Invalid disk text must not become semantic source")))
                .Append((byte)0xc3).ToArray();
            File.WriteAllBytes(sourcePath, invalidBytes);
            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();
            var checkpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync(
                "textDocument/didOpen", CreateOpenDocument(callerUri, CallerSource));

            var invalidHover = await RequestHelperHoverAsync(process, 2, callerUri);

            Assert.Equal(JsonValueKind.Null, invalidHover.GetProperty("result").ValueKind);
            var invalidDiagnostics = await process.WaitForDiagnosticsMatchingAsync(
                sourceUri, HasInvalidDiskSourceDiagnostic, "invalid cold source", afterCheckpoint: checkpoint);
            var diagnostic = Assert.Single(
                invalidDiagnostics.GetProperty("params").GetProperty("diagnostics").EnumerateArray());
            Assert.Contains(sourcePath, diagnostic.GetProperty("message").GetString());

            const string editorDocumentation = "Open Unicode 日本語 🙂";
            checkpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync(
                "textDocument/didOpen", CreateOpenDocument(sourceUri, CreateHelperSource(editorDocumentation)));

            var editorHover = await RequestHelperHoverAsync(process, 3, callerUri);

            Assert.Contains(editorDocumentation, GetHoverText(editorHover));
            await process.WaitForDiagnosticsMatchingAsync(
                sourceUri, diagnostics => !HasInvalidDiskSourceDiagnostic(diagnostics),
                "open Unicode overrides invalid disk bytes", afterCheckpoint: checkpoint);
            Assert.Equal(invalidBytes, File.ReadAllBytes(sourcePath));

            checkpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync(
                "textDocument/didClose", new { textDocument = new { uri = sourceUri } });

            var closedHover = await RequestHelperHoverAsync(process, 4, callerUri);

            Assert.Equal(JsonValueKind.Null, closedHover.GetProperty("result").ValueKind);
            await process.WaitForDiagnosticsMatchingAsync(
                sourceUri, HasInvalidDiskSourceDiagnostic, "close restores disk authority", afterCheckpoint: checkpoint);

            const string recoveredDocumentation = "Recovered disk 日本語 🙂";
            File.WriteAllText(sourcePath, CreateHelperSource(recoveredDocumentation), new UnicodeEncoding(true, true, true));
            checkpoint = process.TranscriptCheckpoint;
            await NotifySourceChangeAsync(process, sourceUri, changeType: 2);

            var recoveredHover = await RequestHelperHoverAsync(process, 5, callerUri);

            Assert.Contains(recoveredDocumentation, GetHoverText(recoveredHover));
            Assert.DoesNotContain(editorDocumentation, GetHoverText(recoveredHover));
            await process.WaitForDiagnosticsMatchingAsync(
                sourceUri, diagnostics => !HasInvalidDiskSourceDiagnostic(diagnostics),
                "valid watched reload clears invalid encoding", afterCheckpoint: checkpoint);

            File.WriteAllBytes(sourcePath, invalidBytes);
            checkpoint = process.TranscriptCheckpoint;
            await NotifySourceChangeAsync(process, sourceUri, changeType: 2);

            var invalidAgainHover = await RequestHelperHoverAsync(process, 6, callerUri);

            Assert.Equal(JsonValueKind.Null, invalidAgainHover.GetProperty("result").ValueKind);
            await process.WaitForDiagnosticsMatchingAsync(
                sourceUri, HasInvalidDiskSourceDiagnostic, "invalid reload removes last-known-good source", afterCheckpoint: checkpoint);

            File.Delete(sourcePath);
            checkpoint = process.TranscriptCheckpoint;
            await NotifySourceChangeAsync(process, sourceUri, changeType: 3);

            var deletedHover = await RequestHelperHoverAsync(process, 7, callerUri);

            Assert.Equal(JsonValueKind.Null, deletedHover.GetProperty("result").ValueKind);
            await process.WaitForDiagnosticsMatchingAsync(
                sourceUri, diagnostics => diagnostics.GetArrayLength() == 0,
                "deletion clears invalid encoding", afterCheckpoint: checkpoint);
            output.WriteLine(
                "Native language-server process: malformed whole-file bytes remain syntax-free; "
                + "open Unicode wins, close restores disk, watched recovery replaces text, and deletion clears diagnostics.");
            await process.ShutdownAsync(8);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    private const string CallerSource =
        "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\n    GetValue\nEnd Sub\n";

    private static string CreateHelperSource(string documentation)
        => $"Attribute VB_Name = \"Helper\"\n'* @brief {documentation}\n"
            + "Public Function GetValue() As String\nEnd Function\n";

    private static object CreateOpenDocument(string uri, string text)
        => new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text }
        };

    private static Task<JsonElement> RequestHelperHoverAsync(
        LanguageServerProcessHarness process,
        int requestId,
        string callerUri)
        => process.SendRequestAsync(
            requestId,
            "textDocument/hover",
            new
            {
                textDocument = new { uri = callerUri },
                position = new { line = 2, character = 4 }
            });

    private static string GetHoverText(JsonElement hover)
        => hover.GetProperty("result").GetProperty("contents").GetProperty("value").GetString()!;

    private static bool HasInvalidDiskSourceDiagnostic(JsonElement diagnostics)
        => diagnostics.EnumerateArray().Any(diagnostic =>
            diagnostic.GetProperty("code").GetString() == "invalid-disk-source-encoding");

    private static Task NotifySourceChangeAsync(LanguageServerProcessHarness process, string uri, int changeType)
        => process.SendNotificationAsync(
            "workspace/didChangeWatchedFiles",
            new { changes = new[] { new { uri, type = changeType } } });

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
