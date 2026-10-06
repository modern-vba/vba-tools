using System.Text.Json;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaTools.Processes;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaDevReferenceListCrashTests
{
    private const int FatalClrExitCode = unchecked((int)0x80131506);
    private const string ReferenceName = "Library A";

    [Theory]
    [InlineData("complete")]
    [InlineData("empty")]
    [InlineData("partial")]
    public async Task FatalReferenceListExitIsReportedInsteadOfAcceptingOrParsingOutput(
        string outputKind)
    {
        var projectPath = Path.GetFullPath(Path.Combine("projects", "FatalReferenceList"));
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var output = outputKind switch
        {
            "complete" => JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0",
                scope = "project",
                project = projectPath,
                document = "Book1",
                mode = "configured",
                complete = true,
                warnings = Array.Empty<object>(),
                references = new[]
                {
                    new
                    {
                        name = ReferenceName,
                        status = "resolved",
                        identity = new
                        {
                            guid = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                            major = 1,
                            minor = 0
                        }
                    }
                }
            }),
            "empty" => "",
            "partial" => "{\"schemaVersion\":\"1.0\",",
            _ => throw new InvalidOperationException(outputKind)
        };
        var calls = 0;
        var discovery = new VbaDevReferenceListCatalogDiscovery(
            new UnresolvedRegistryDiscovery(),
            new VbaProjectReferenceCatalogRefreshContext(
                projectPath,
                "Book1",
                VbaProjectReferenceSelection.Create(
                    ProjectDocument.ExcelKind,
                    [new VbaProjectReference(ReferenceName)])),
            executablePath,
            (arguments, _) =>
            {
                calls++;
                Assert.Equal(["reference", "list"], arguments.Take(2));
                return Task.FromResult(new ProcessInvocationResult(
                    FatalClrExitCode, output, ""));
            });

        var result = await discovery.DiscoverAsync(ReferenceName);

        Assert.True(result.IsFailure);
        Assert.Equal(1, calls);
        Assert.Contains("role=vba-dev", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("stage=reference-list", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains($"executable={JsonSerializer.Serialize(executablePath)}",
            result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("attempt=1/1", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains($"exitCodeSigned={FatalClrExitCode}", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("exitCodeHex=0x80131506", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("malformed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class UnresolvedRegistryDiscovery : IVbaProjectReferenceCatalogDiscovery
    {
        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VbaProjectReferenceCatalogDiscoveryResult.Ambiguous(
                referenceName, []));
    }
}
