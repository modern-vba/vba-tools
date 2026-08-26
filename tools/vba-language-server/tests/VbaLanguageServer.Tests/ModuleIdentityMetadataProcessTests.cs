using System.Text.Json;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class ModuleIdentityMetadataProcessTests
{
    [Fact]
    public async Task Parser_owned_batch_request_preserves_order_and_shared_identifier_results()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        const string japaneseUri = "file:///C:/work/Sheet1.cls";
        const string reservedUri = "file:///C:/work/CDecl.frm";
        const string missingUri = "file:///C:/work/Sheet2.cls";
        var response = await server.SendRequestAsync(
            2,
            "vba/moduleIdentityMetadata",
            new
            {
                sources = new[]
                {
                    new
                    {
                        sourceUri = japaneseUri,
                        kind = "document",
                        text = string.Join("\r\n", [
                            "VERSION 1.0 CLASS",
                            "BEGIN",
                            "END",
                            "Attribute VB_Name = \"集計\"",
                            "Attribute VB_Exposed = False",
                            string.Empty
                        ])
                    },
                    new
                    {
                        sourceUri = reservedUri,
                        kind = "form",
                        text = "Attribute VB_Name = \"CDecl\"\r\n"
                    },
                    new
                    {
                        sourceUri = missingUri,
                        kind = "document",
                        text = "Attribute VB_Exposed = False\r\n"
                    }
                }
            });

        var results = response
            .GetProperty("result")
            .GetProperty("sources")
            .EnumerateArray()
            .ToArray();
        Assert.Collection(
            results,
            result => AssertSource(
                result,
                japaneseUri,
                "document",
                "authoritative",
                "集計"),
            result => AssertSource(result, reservedUri, "form", "invalid", null),
            result => AssertSource(result, missingUri, "document", "missing", null));

        await server.ShutdownAsync(3);
    }

    [Fact]
    public async Task Batch_request_rejects_a_whitespace_source_uri_and_recovers()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        var invalid = await server.SendRequestAsync(
            2,
            "vba/moduleIdentityMetadata",
            new
            {
                sources = new[]
                {
                    new
                    {
                        sourceUri = " \t",
                        kind = "form",
                        text = "Attribute VB_Name = \"InvoiceForm\"\r\n"
                    }
                }
            });

        Assert.Equal(-32602, invalid.GetProperty("error").GetProperty("code").GetInt32());

        var valid = await server.SendRequestAsync(
            3,
            "vba/moduleIdentityMetadata",
            new { sources = Array.Empty<object>() });
        Assert.Empty(valid.GetProperty("result").GetProperty("sources").EnumerateArray());

        await server.ShutdownAsync(4);
    }

    private static void AssertSource(
        JsonElement result,
        string sourceUri,
        string kind,
        string state,
        string? name)
    {
        Assert.Equal(sourceUri, result.GetProperty("sourceUri").GetString());
        Assert.Equal(kind, result.GetProperty("kind").GetString());
        Assert.Equal(state, result.GetProperty("state").GetString());
        Assert.Equal(name, result.GetProperty("name").GetString());
    }
}
