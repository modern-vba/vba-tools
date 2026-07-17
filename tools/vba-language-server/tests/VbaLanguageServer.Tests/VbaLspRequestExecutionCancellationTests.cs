using System.Text.Json.Nodes;
using VbaLanguageServer.Lsp;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaLspRequestExecutionCancellationTests
{
    [Fact]
    public async Task Executor_returns_request_cancelled_for_a_request_cancelled_before_execution()
    {
        await using var output = new MemoryStream();
        var transport = new LspMessageTransport(Stream.Null, output);
        var catalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.CreateBundled());
        var executor = new VbaLspRequestExecution(
            transport,
            new VbaLanguageWorkspace(catalogCache));
        using var requestCancellation = new CancellationTokenSource();
        requestCancellation.Cancel();
        var request = JsonNode.Parse(
            """
            {
              "jsonrpc": "2.0",
              "id": 7,
              "method": "test/unknown"
            }
            """)!.AsObject();

        await executor.ExecuteAsync(
            request,
            requestCancellation.Token,
            CancellationToken.None);

        output.Position = 0;
        var responseReader = new LspMessageTransport(output, Stream.Null);
        var response = Assert.IsType<JsonObject>(
            await responseReader.ReadMessageAsync(CancellationToken.None));
        Assert.Equal(-32800, response["error"]!["code"]!.GetValue<int>());
    }
}
