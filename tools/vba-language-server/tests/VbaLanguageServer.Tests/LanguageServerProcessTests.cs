using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using VbaLanguageServer.SourceModel;
using VbaTools.TypeLibRegistry;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class LanguageServerProcessTests
{
    [Fact]
    public async Task Server_without_a_supplied_vba_dev_warns_after_initialized_and_keeps_running()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();

        await server.InitializeAsync();

        var warning = await server.WaitForLogMessageAsync(
            "did not receive one absolute --vba-dev executable path");
        Assert.Equal(2, warning.GetProperty("params").GetProperty("type").GetInt32());

        var afterWarning = server.TranscriptCheckpoint;
        await server.SendNotificationAsync("initialized", new { });
        await Assert.ThrowsAsync<TimeoutException>(() => server.WaitForMessageAsync(
            afterWarning,
            message => message.TryGetProperty("method", out var method)
                && method.GetString() == "window/logMessage"
                && message.GetProperty("params").GetProperty("message").GetString()
                    ?.Contains("--vba-dev", StringComparison.Ordinal) == true,
            TimeSpan.FromMilliseconds(250)));

        var completion = await server.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = "file:///C:/work/Module1.bas" },
                position = new { line = 0, character = 0 }
            });
        Assert.Equal(JsonValueKind.Array, completion.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task Server_does_not_search_for_or_normalize_a_relative_vba_dev_path()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync(
            serverArguments: ["--vba-dev", "vba-dev.exe"]);

        await server.InitializeAsync();

        var warning = await server.WaitForLogMessageAsync(
            "did not receive one absolute --vba-dev executable path");
        Assert.Contains(
            "registry-only discovery remains available",
            warning.GetProperty("params").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Server_survives_a_supplied_vba_dev_probe_failure_without_corrupting_stdio()
    {
        var missingExecutablePath = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            $"missing-vba-dev-{Guid.NewGuid():N}.exe"));
        await using var server = await LanguageServerProcessHarness.StartAsync(
            serverArguments: ["--vba-dev", missingExecutablePath]);

        var initialize = await server.InitializeAsync();

        Assert.Equal(1, initialize.GetProperty("id").GetInt32());
        var warning = await server.WaitForLogMessageAsync(missingExecutablePath);
        Assert.Contains(
            "could not be validated",
            warning.GetProperty("params").GetProperty("message").GetString());

        var completion = await server.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = "file:///C:/work/Module1.bas" },
                position = new { line = 0, character = 0 }
            });
        Assert.Equal(JsonValueKind.Array, completion.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task Server_handles_initialize_text_sync_completion_and_shutdown()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        var initialize = await server.InitializeAsync();

        Assert.Equal(1, initialize.GetProperty("id").GetInt32());
        var capabilities = initialize
            .GetProperty("result")
            .GetProperty("capabilities");
        Assert.Equal(1, capabilities.GetProperty("textDocumentSync").GetInt32());
        var completionTriggers = capabilities
            .GetProperty("completionProvider")
            .GetProperty("triggerCharacters")
            .EnumerateArray()
            .Select(trigger => trigger.GetString()!)
            .ToArray();
        Assert.Equal(
            [
                ".", "_", " ", "(", ",", ":", ";", "+", "-", "*", "/", "\\", "^", "&", "=", "<", ">"
            ],
            completionTriggers);
        Assert.DoesNotContain("!", completionTriggers);
        Assert.True(capabilities.GetProperty("referencesProvider").GetBoolean());
        Assert.True(capabilities.GetProperty("workspaceSymbolProvider").GetBoolean());
        Assert.True(capabilities.GetProperty("documentFormattingProvider").GetBoolean());
        var signatureProvider = capabilities.GetProperty("signatureHelpProvider");
        var signatureTriggers = signatureProvider
            .GetProperty("triggerCharacters")
            .EnumerateArray()
            .Select(trigger => trigger.GetString())
            .ToArray();
        Assert.Contains(" ", signatureTriggers);
        var signatureRetriggers = signatureProvider
            .GetProperty("retriggerCharacters")
            .EnumerateArray()
            .Select(trigger => trigger.GetString()!)
            .ToArray();
        Assert.Equal(["="], signatureRetriggers);
        Assert.False(capabilities.TryGetProperty("documentRangeFormattingProvider", out _));
        Assert.False(capabilities.TryGetProperty("documentOnTypeFormattingProvider", out _));

        await server.SendNotificationAsync(
            "textDocument/didOpen",
            new
            {
                textDocument = new
                {
                    uri = "file:///C:/work/Module1.bas",
                    languageId = "vba",
                    version = 1,
                    text = "Public Sub Hello()\nEnd Sub\n"
                }
            });
        await server.SendNotificationAsync(
            "textDocument/didChange",
            new
            {
                textDocument = new
                {
                    uri = "file:///C:/work/Module1.bas",
                    version = 2
                },
                contentChanges = new[]
                {
                    new
                    {
                        text = "Public Sub Hello()\n    \nDebug.Print \"hi\"\nEnd Sub\n"
                    }
                }
            });

        var completion = await server.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = "file:///C:/work/Module1.bas" },
                position = new { line = 1, character = 4 }
            });

        var completionItems = completion.GetProperty("result").EnumerateArray().ToArray();
        var completionLabels = completionItems.Select(item => item.GetProperty("label").GetString()).ToArray();
        Assert.Contains("Hello", completionLabels);
        Assert.Contains("If", completionLabels);

        var workspaceSymbols = await server.SendRequestAsync(
            3,
            "workspace/symbol",
            new
            {
                query = "hello"
            });
        var workspaceSymbol = Assert.Single(workspaceSymbols.GetProperty("result").EnumerateArray());
        Assert.Equal("Hello", workspaceSymbol.GetProperty("name").GetString());

        var references = await SendPositionRequestAsync(
            server,
            4,
            "textDocument/references",
            "file:///C:/work/Module1.bas",
            "Public Sub Hello()\nDebug.Print \"hi\"\nEnd Sub\n",
            "Hello");
        var reference = Assert.Single(references.GetProperty("result").EnumerateArray());
        Assert.Equal("file:///C:/work/Module1.bas", reference.GetProperty("uri").GetString());

        var shutdown = await server.ShutdownAsync(5);
        Assert.Equal(JsonValueKind.Null, shutdown.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task Completion_invocation_context_does_not_change_candidates_for_the_same_position()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        const string uri = "file:///C:/work/CompletionContext.bas";
        var text = string.Join('\n',
        [
            "Public Sub Run()",
            "    Dim result As Long",
            "    result = 1 + ",
            "End Sub"
        ]);
        await server.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

        object?[] invocationContexts =
        [
            null,
            new { context = new { triggerKind = 1 } },
            new { context = new { triggerKind = 2, triggerCharacter = "+" } },
            new { context = new { triggerKind = 3 } }
        ];
        var responses = new List<JsonElement>();
        for (var index = 0; index < invocationContexts.Length; index++)
        {
            responses.Add(await server.SendRequestAsync(
                index + 2,
                "textDocument/completion",
                MergePositionParameters(
                    uri,
                    2,
                    "    result = 1 + ".Length,
                    invocationContexts[index])));
        }

        var expectedResult = responses[0].GetProperty("result");
        Assert.NotEmpty(expectedResult.EnumerateArray());
        Assert.All(
            responses.Skip(1),
            response => Assert.Equal(
                expectedResult.GetRawText(),
                response.GetProperty("result").GetRawText()));

        await server.ShutdownAsync(6);
    }

    [Fact]
    public async Task Underscore_trigger_returns_no_ordinary_candidates_outside_a_contract_name_slot()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        const string uri = "file:///C:/work/UnderscoreCompletion.bas";
        var text = string.Join('\n',
        [
            "Public Sub Run()",
            "    Dim value_ As Long",
            "    value_",
            "End Sub"
        ]);
        await server.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

        var explicitCompletion = await server.SendRequestAsync(
            2,
            "textDocument/completion",
            MergePositionParameters(uri, 2, "    value_".Length, null));
        Assert.NotEmpty(explicitCompletion.GetProperty("result").EnumerateArray());

        var triggeredCompletion = await server.SendRequestAsync(
            3,
            "textDocument/completion",
            MergePositionParameters(
                uri,
                2,
                "    value_".Length,
                new { context = new { triggerKind = 2, triggerCharacter = "_" } }));
        Assert.Empty(triggeredCompletion.GetProperty("result").EnumerateArray());

        await server.ShutdownAsync(4);
    }

    [Fact]
    public async Task Space_trigger_preserves_ordinary_completion_outside_a_contract_name_slot()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        const string uri = "file:///C:/work/SpaceCompletion.bas";
        var text = string.Join('\n',
        [
            "Public Sub Run()",
            "    Dim result As Long",
            "    result = 1 + ",
            "End Sub"
        ]);
        await server.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

        var explicitCompletion = await server.SendRequestAsync(
            2,
            "textDocument/completion",
            MergePositionParameters(uri, 2, "    result = 1 + ".Length, null));
        Assert.NotEmpty(explicitCompletion.GetProperty("result").EnumerateArray());

        var triggeredCompletion = await server.SendRequestAsync(
            3,
            "textDocument/completion",
            MergePositionParameters(
                uri,
                2,
                "    result = 1 + ".Length,
                new { context = new { triggerKind = 2, triggerCharacter = " " } }));
        Assert.Equal(
            explicitCompletion.GetProperty("result").GetRawText(),
            triggeredCompletion.GetProperty("result").GetRawText());

        await server.ShutdownAsync(4);
    }

    [Fact]
    public async Task Space_trigger_preserves_Property_accessor_keyword_completion()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        const string uri = "file:///C:/work/PropertyAccessorCompletion.cls";
        const string text = "Private Property ";
        await server.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var explicitCompletion = await server.SendRequestAsync(
            2,
            "textDocument/completion",
            MergePositionParameters(uri, 0, text.Length, null));
        var triggeredCompletion = await server.SendRequestAsync(
            3,
            "textDocument/completion",
            MergePositionParameters(
                uri,
                0,
                text.Length,
                new { context = new { triggerKind = 2, triggerCharacter = " " } }));
        Assert.Equal(
            ["Get", "Let", "Set"],
            triggeredCompletion
                .GetProperty("result")
                .EnumerateArray()
                .Select(item => item.GetProperty("label").GetString()));
        Assert.Equal(
            explicitCompletion.GetProperty("result").GetRawText(),
            triggeredCompletion.GetProperty("result").GetRawText());

        await server.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_projects_only_candidates_admitted_by_the_active_completion_context()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();
        const string uri = "file:///C:/work/ContextualCompletion.bas";
        var lines = new[]
        {
            "Attribute VB_Name = \"ContextualCompletion\"",
            "Option Explicit",
            "Public Function ExampleFunc(ByVal Arg1 As Long, Optional ByVal Arg2 As Boolean = False, Optional ByVal Arg3 As Boolean = False) As String",
            "End Function",
            "Public Sub Main()",
            "    Dim result As Variant",
            "    Dim LocalValue As Long",
            "    result = ExampleFunc(1, Arg3:=True) ",
            "    result = ExampleFunc(1, ",
            "    result = LocalValue +",
            "    result = LocalValue + ",
            "    If True Then",
            "        End ",
            "    End If",
            "End Sub"
        };
        await server.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, string.Join('\n', lines)));
        var requestId = 2;

        async Task<JsonElement> CompleteAsync(int line, int character)
            => (await server.SendRequestAsync(
                requestId++,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri },
                    position = new { line, character }
                })).GetProperty("result");

        var completedLength = lines[7].Length - 1;
        Assert.Empty((await CompleteAsync(7, completedLength)).EnumerateArray());
        Assert.Empty((await CompleteAsync(7, lines[7].Length)).EnumerateArray());

        var arguments = (await CompleteAsync(8, lines[8].Length))
            .EnumerateArray()
            .ToArray();
        var namedArguments = arguments
            .Where(item => item.GetProperty("kind").GetInt32() == 5)
            .ToDictionary(item => item.GetProperty("label").GetString()!);
        Assert.Equal(["Arg2", "Arg3"], namedArguments.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal("Arg2:=", namedArguments["Arg2"].GetProperty("insertText").GetString());
        Assert.Equal("Arg2", namedArguments["Arg2"].GetProperty("filterText").GetString());
        Assert.False(namedArguments["Arg2"].TryGetProperty("documentation", out _));

        var afterOperator = await CompleteAsync(9, lines[9].Length);
        var afterOperatorSpace = await CompleteAsync(10, lines[10].Length);
        Assert.NotEmpty(afterOperator.EnumerateArray());
        Assert.Equal(afterOperator.GetRawText(), afterOperatorSpace.GetRawText());

        var endItem = Assert.Single((await CompleteAsync(12, lines[12].Length)).EnumerateArray());
        Assert.Equal("End If", endItem.GetProperty("label").GetString());
        Assert.Equal("End If", endItem.GetProperty("textEdit").GetProperty("newText").GetString());

        await server.ShutdownAsync(requestId);
    }

    [Fact]
    public async Task Explicit_end_statement_completion_remains_available_inside_a_conditional_branch()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalCompletion.bas";
        var lines = new[]
        {
            "Attribute VB_Name = \"ConditionalCompletion\"",
            "Public Sub Main()",
            "#If VBA7 Then",
            "#ElseIf Win64 Then",
            "    If True Then",
            "        End ",
            "    End If",
            "#Else",
            "#End If",
            "End Sub"
        };
        await server.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, string.Join('\n', lines)));

        var response = await server.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new { line = 5, character = lines[5].Length },
                context = new { triggerKind = 1 }
            });

        var item = Assert.Single(response
            .GetProperty("result")
            .EnumerateArray());
        Assert.Equal("End If", item.GetProperty("label").GetString());
        Assert.Equal(
            "End If",
            item.GetProperty("textEdit").GetProperty("newText").GetString());

        await server.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_preserves_candidates_across_operator_and_call_separator_whitespace()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();
        const string uri = "file:///C:/work/TriggerParity.bas";
        var lines = new List<string>
        {
            "Attribute VB_Name = \"TriggerParity\"",
            "Option Explicit",
            "Public Function ExampleFunc(ByVal Arg1 As Long, Optional ByVal Arg2 As Boolean = False) As String",
            "End Function",
            "Public Sub Main()",
            "    Dim result As Variant",
            "    Dim LocalValue As Long"
        };
        var operatorPairs = new List<(int OperatorLine, int SpaceLine)>();
        foreach (var operation in new[] { "+", "-", "*", "/", "\\", "^", "&", "=", "<", ">" })
        {
            var operatorLine = lines.Count;
            lines.Add($"    result = LocalValue {operation}");
            var spaceLine = lines.Count;
            lines.Add($"    result = LocalValue {operation} ");
            operatorPairs.Add((operatorLine, spaceLine));
        }

        var wordOperatorPairs = new List<(int OperatorLine, int SpaceLine)>();
        foreach (var operation in new[] { "And", "Or", "Xor", "Eqv", "Imp", "Mod", "Like", "Is", "Not" })
        {
            var operatorLine = lines.Count;
            lines.Add(operation == "Not"
                ? "    result = Not"
                : $"    result = LocalValue {operation}");
            var spaceLine = lines.Count;
            lines.Add(lines[operatorLine] + " ");
            wordOperatorPairs.Add((operatorLine, spaceLine));
        }

        var commaLine = lines.Count;
        lines.Add("    result = ExampleFunc(1,");
        var commaSpaceLine = lines.Count;
        lines.Add("    result = ExampleFunc(1, ");
        lines.Add("End Sub");
        await server.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, string.Join('\n', lines)));
        var requestId = 2;

        async Task<JsonElement> CompleteAsync(int line)
            => (await server.SendRequestAsync(
                requestId++,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri },
                    position = new { line, character = lines[line].Length }
                })).GetProperty("result");

        foreach (var pair in operatorPairs)
        {
            var afterOperator = await CompleteAsync(pair.OperatorLine);
            var afterSpace = await CompleteAsync(pair.SpaceLine);
            Assert.NotEmpty(afterOperator.EnumerateArray());
            Assert.Equal(afterOperator.GetRawText(), afterSpace.GetRawText());
        }

        foreach (var pair in wordOperatorPairs)
        {
            var beforeSeparator = await CompleteAsync(pair.OperatorLine);
            var afterSeparator = await CompleteAsync(pair.SpaceLine);
            Assert.Empty(beforeSeparator.EnumerateArray());
            Assert.NotEmpty(afterSeparator.EnumerateArray());
        }

        var afterComma = await CompleteAsync(commaLine);
        var afterCommaSpace = await CompleteAsync(commaSpaceLine);
        Assert.NotEmpty(afterComma.EnumerateArray());
        Assert.Equal(afterComma.GetRawText(), afterCommaSpace.GetRawText());

        await server.ShutdownAsync(requestId);
    }

    [Fact]
    public async Task Harness_retains_interleaved_notifications_and_correlates_concurrent_responses()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        const string uri = "file:///C:/work/Concurrent.bas";
        var text = "Public Sub Run()\n    \nEnd Sub\n";
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

        var responseCheckpoint = process.TranscriptCheckpoint;
        var completionTask = process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new { line = 1, character = 4 }
            });
        var symbolsTask = process.SendRequestAsync(
            3,
            "textDocument/documentSymbol",
            new
            {
                textDocument = new { uri }
            });

        var responses = await Task.WhenAll(completionTask, symbolsTask);
        var completion = responses[0];
        var symbols = responses[1];
        Assert.Equal(2, completion.GetProperty("id").GetInt32());
        Assert.Equal(3, symbols.GetProperty("id").GetInt32());
        Assert.Contains(
            completion.GetProperty("result").EnumerateArray(),
            item => item.GetProperty("label").GetString() == "Run");
        Assert.Contains(
            symbols.GetProperty("result").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "Run");

        static bool IsConcurrentResponse(JsonElement message)
            => message.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.Number
                && id.GetInt32() is 2 or 3;

        var firstWireResponse = await process.WaitForMessageAsync(
            responseCheckpoint,
            IsConcurrentResponse);
        var secondWireResponse = await process.WaitForMessageAsync(
            responseCheckpoint,
            IsConcurrentResponse);
        Assert.NotEqual(
            firstWireResponse.GetProperty("id").GetInt32(),
            secondWireResponse.GetProperty("id").GetInt32());

        var diagnostics = await process.WaitForDiagnosticsAsync(uri);
        Assert.Empty(diagnostics.GetProperty("params").GetProperty("diagnostics").EnumerateArray());
        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Harness_disposal_releases_waiters_and_is_idempotent()
    {
        var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        var unmatchedNotification = process.WaitForNotificationAsync(
            "test/never-arrives",
            TimeSpan.FromSeconds(30));

        var firstDisposal = process.DisposeAsync().AsTask();
        var secondDisposal = process.DisposeAsync().AsTask();
        Assert.Same(firstDisposal, secondDisposal);
        await Task.WhenAll(firstDisposal, secondDisposal);

        var waiterFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await unmatchedNotification);
        Assert.Contains("session failed", waiterFailure.Message, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => process.SendRequestAsync(2, "shutdown", parameters: null));
    }

    [Fact]
    public async Task Server_advertises_semantic_tokens_and_updates_after_document_change()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        var initialize = await process.SendRequestAsync(1,
            "initialize",
            new
            {
                processId = Environment.ProcessId,
                rootUri = (string?)null,
                capabilities = new { }
            });
        var semanticProvider = initialize
            .GetProperty("result")
            .GetProperty("capabilities")
            .GetProperty("semanticTokensProvider");
        Assert.True(semanticProvider.GetProperty("full").GetBoolean());
        Assert.Contains(
            "function",
            semanticProvider.GetProperty("legend").GetProperty("tokenTypes").EnumerateArray().Select(item => item.GetString()));

        await process.SendNotificationAsync("initialized", new { });
        const string uri = "file:///C:/work/Module1.bas";
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, string.Join('\n', [
            "Attribute VB_Name = \"Module1\"",
            "Option Explicit",
            "Public Sub Run()",
            "End Sub"
        ])));
        var before = await process.SendRequestAsync(2,
            "textDocument/semanticTokens/full",
            new
            {
                textDocument = new { uri }
            });
        var beforeLength = before.GetProperty("result").GetProperty("data").GetArrayLength();

        await process.SendNotificationAsync("textDocument/didChange",
            new
            {
                textDocument = new
                {
                    uri,
                    version = 2
                },
                contentChanges = new[]
                {
                    new
                    {
                        text = string.Join('\n', [
                            "Attribute VB_Name = \"Module1\"",
                            "Option Explicit",
                            "Public Function BuildValue() As String",
                            "End Function",
                            "Public Sub Run()",
                            "    BuildValue",
                            "End Sub"
                        ])
                    }
                }
            });
        await process.WaitForNotificationAsync("textDocument/publishDiagnostics");
        var after = await process.SendRequestAsync(3,
            "textDocument/semanticTokens/full",
            new
            {
                textDocument = new { uri }
            });
        var afterLength = after.GetProperty("result").GetProperty("data").GetArrayLength();
        Assert.True(afterLength > beforeLength);

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_returns_project_symbol_semantic_tokens_for_range_bounds_scenario()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string rangeBoundsUri = "file:///C:/work/WorksheetRangeBounds.cls";
        var rangeBoundsText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"WorksheetRangeBounds\"",
            "Private pColumn As Long",
            "Public Property Get Column() As Long",
            "    Column = pColumn",
            "End Property"
        ]);
        const string workerUri = "file:///C:/work/Worker.bas";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Private Function TestFunction() As String",
            "    Dim range_obj As WorksheetRangeBounds",
            "    aaaa = range_obj.Column",
            "End Function"
        ]);
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(rangeBoundsUri, rangeBoundsText));
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(workerUri, workerText));

        var response = await process.SendRequestAsync(2,
            "textDocument/semanticTokens/full",
            new
            {
                textDocument = new { uri = workerUri }
            });
        var tokens = DecodeSemanticTokens(response, workerText);

        Assert.Contains(tokens, token =>
            token.Text == "WorksheetRangeBounds"
            && token.TokenType == "class"
            && !token.TokenModifiers.Contains("declaration"));
        Assert.Contains(tokens, token =>
            token.Text == "range_obj"
            && token.TokenType == "variable"
            && token.TokenModifiers.Contains("declaration"));
        Assert.Contains(tokens, token =>
            token.Text == "range_obj"
            && token.TokenType == "variable"
            && !token.TokenModifiers.Contains("declaration"));
        Assert.Contains(tokens, token =>
            token.Text == "Column"
            && token.TokenType == "property"
            && !token.TokenModifiers.Contains("declaration"));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_orders_completion_items_by_source_proximity_before_catalogs()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string workerUri = "file:///C:/work/CompletionOrderingWorker.bas";
        const string projectUri = "file:///C:/work/CompletionOrderingProject.bas";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"CompletionOrderingWorker\"",
            "Option Explicit",
            "Private YankeeCurrent As String",
            "Public Sub Run()",
            "    Dim ZuluLocal As String",
            "    value = ",
            "End Sub"
        ]);
        var projectText = string.Join('\n', [
            "Attribute VB_Name = \"CompletionOrderingProject\"",
            "Option Explicit",
            "Public XrayProject As String"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(projectUri, projectText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var completion = await process.SendRequestAsync(2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 5, character = "    value = ".Length }
            });
        var items = completion
            .GetProperty("result")
            .EnumerateArray()
            .ToArray();

        var localSortText = Assert.Single(items, item =>
                item.GetProperty("label").GetString() == "ZuluLocal")
            .GetProperty("sortText")
            .GetString();
        var currentModuleSortText = Assert.Single(items, item =>
                item.GetProperty("label").GetString() == "YankeeCurrent")
            .GetProperty("sortText")
            .GetString();
        var projectSortText = Assert.Single(items, item =>
                item.GetProperty("label").GetString() == "XrayProject")
            .GetProperty("sortText")
            .GetString();
        var catalogSortText = Assert.Single(items, item =>
                item.GetProperty("label").GetString() == "vbCrLf")
            .GetProperty("sortText")
            .GetString();

        Assert.True(StringComparer.Ordinal.Compare(localSortText, currentModuleSortText) < 0);
        Assert.True(StringComparer.Ordinal.Compare(currentModuleSortText, projectSortText) < 0);
        Assert.True(StringComparer.Ordinal.Compare(projectSortText, catalogSortText) < 0);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_orders_current_module_qualifier_before_project_and_reference_qualifiers()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string workerUri = "file:///C:/work/ZuluCurrent.bas";
        const string projectUri = "file:///C:/work/YankeeProject.bas";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"ZuluCurrent\"",
            "Option Explicit",
            "Public Function CurrentValue() As String",
            "End Function",
            "Public Sub Run()",
            "    value = ",
            "End Sub"
        ]);
        var projectText = string.Join('\n', [
            "Attribute VB_Name = \"YankeeProject\"",
            "Option Explicit",
            "Public Function ProjectValue() As String",
            "End Function"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(projectUri, projectText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var completion = await process.SendRequestAsync(2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 5, character = "    value = ".Length }
            });
        var items = completion
            .GetProperty("result")
            .EnumerateArray()
            .ToArray();
        var currentSortText = Assert.Single(items, item =>
                item.GetProperty("label").GetString() == "ZuluCurrent"
                && item.GetProperty("kind").GetInt32() == 9)
            .GetProperty("sortText")
            .GetString();
        var projectSortText = Assert.Single(items, item =>
                item.GetProperty("label").GetString() == "YankeeProject"
                && item.GetProperty("kind").GetInt32() == 9)
            .GetProperty("sortText")
            .GetString();
        var referenceSortText = Assert.Single(items, item =>
                item.GetProperty("label").GetString() == "VBA"
                && item.GetProperty("kind").GetInt32() == 9)
            .GetProperty("sortText")
            .GetString();

        Assert.True(StringComparer.Ordinal.Compare(currentSortText, projectSortText) < 0);
        Assert.True(StringComparer.Ordinal.Compare(projectSortText, referenceSortText) < 0);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_distinguishes_same_label_callable_and_source_qualifier_items()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string workerUri = "file:///C:/work/SameLabelWorker.bas";
        const string builderUri = "file:///C:/work/Builder.bas";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"SameLabelWorker\"",
            "Option Explicit",
            "Public Function Builder() As String",
            "End Function",
            "Public Sub Run()",
            "    value = ",
            "End Sub"
        ]);
        var builderText = string.Join('\n', [
            "Attribute VB_Name = \"Builder\"",
            "Option Explicit",
            "Public Function CreateValue() As String",
            "End Function"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(builderUri, builderText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var completion = await process.SendRequestAsync(2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 5, character = "    value = ".Length }
            });
        var builderItems = completion
            .GetProperty("result")
            .EnumerateArray()
            .Where(item => item.GetProperty("label").GetString() == "Builder")
            .ToArray();

        Assert.Equal(2, builderItems.Length);
        var callable = Assert.Single(builderItems, item =>
            item.GetProperty("kind").GetInt32() == 3);
        var qualifier = Assert.Single(builderItems, item =>
            item.GetProperty("kind").GetInt32() == 9);
        Assert.Equal(
            "Function Builder() As String",
            callable.GetProperty("detail").GetString());
        Assert.Equal(
            "Module qualifier",
            qualifier.GetProperty("detail").GetString());
        Assert.Equal("Builder.", qualifier.GetProperty("insertText").GetString());
        Assert.NotEqual(
            callable.GetProperty("sortText").GetString(),
            qualifier.GetProperty("sortText").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_orders_unqualified_type_completion_before_catalog_types()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-type-ordering-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(
                projectRoot,
                "Microsoft Excel 16.0 Object Library");

            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            var workerUri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "TypeOrderingWorker.bas"));
            var projectUri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "TypeOrderingProject.bas"));
            var workerText = string.Join('\n', [
                "Attribute VB_Name = \"TypeOrderingWorker\"",
                "Option Explicit",
                "Public Type ZuluCurrentType",
                "    Value As Long",
                "End Type",
                "Public Sub Run()",
                "    Dim value As ",
                "End Sub"
            ]);
            var projectText = string.Join('\n', [
                "Attribute VB_Name = \"TypeOrderingProject\"",
                "Option Explicit",
                "Public Type YankeeProjectType",
                "    Value As Long",
                "End Type"
            ]);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(projectUri, projectText));
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(workerUri, workerText));

            var completion = await process.SendRequestAsync(2,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri = workerUri },
                    position = new { line = 6, character = "    Dim value As ".Length }
                });
            var items = completion
                .GetProperty("result")
                .EnumerateArray()
                .ToArray();
            var currentModuleSortText = Assert.Single(items, item =>
                    item.GetProperty("label").GetString() == "ZuluCurrentType")
                .GetProperty("sortText")
                .GetString();
            var projectSortText = Assert.Single(items, item =>
                    item.GetProperty("label").GetString() == "YankeeProjectType")
                .GetProperty("sortText")
                .GetString();
            var catalogSortText = Assert.Single(items, item =>
                    item.GetProperty("label").GetString() == "Application")
                .GetProperty("sortText")
                .GetString();

            Assert.True(StringComparer.Ordinal.Compare(currentModuleSortText, projectSortText) < 0);
            Assert.True(StringComparer.Ordinal.Compare(projectSortText, catalogSortText) < 0);

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_replaces_partial_source_qualifier_with_a_trailing_dot()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string workerUri = "file:///C:/work/PartialQualifierWorker.bas";
        const string builderUri = "file:///C:/work/Builder.bas";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"PartialQualifierWorker\"",
            "Option Explicit",
            "Public Sub Run()",
            "    value = Bui",
            "End Sub"
        ]);
        var builderText = string.Join('\n', [
            "Attribute VB_Name = \"Builder\"",
            "Option Explicit",
            "Public Function CreateValue() As String",
            "End Function"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(builderUri, builderText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var completion = await process.SendRequestAsync(2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 3, character = "    value = Bui".Length }
            });
        var item = Assert.Single(
            completion.GetProperty("result").EnumerateArray(),
            candidate => candidate.GetProperty("label").GetString() == "Builder");
        var textEdit = item.GetProperty("textEdit");

        Assert.Equal("Builder", item.GetProperty("label").GetString());
        Assert.Equal("Builder", item.GetProperty("filterText").GetString());
        Assert.Equal("Builder.", textEdit.GetProperty("newText").GetString());
        Assert.Equal(
            "    value = ".Length,
            textEdit.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(
            "    value = Bui".Length,
            textEdit.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());
        Assert.False(item.TryGetProperty("insertText", out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_exposes_standard_library_constants_in_ad_hoc_projects()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/StandardLibrary.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"StandardLibrary\"",
            "Option Explicit",
            "Public Sub Run()",
            "    value = ",
            "    value = VBA.",
            "    value = vbCrLf",
            "End Sub"
        ]);
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

        var rootCompletion = await process.SendRequestAsync(2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new { line = 3, character = "    value = ".Length }
            });
        var rootItems = rootCompletion.GetProperty("result").EnumerateArray().ToArray();
        Assert.Contains(rootItems, item =>
            item.GetProperty("label").GetString() == "vbCrLf"
            && item.GetProperty("kind").GetInt32() == 21);
        var qualifierItem = Assert.Single(rootItems, item =>
            item.GetProperty("label").GetString() == "VBA");
        Assert.Equal("VBA.", qualifierItem.GetProperty("insertText").GetString());

        var qualifiedCompletion = await process.SendRequestAsync(3,
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new { line = 4, character = "    value = VBA.".Length }
            });
        Assert.Contains(qualifiedCompletion.GetProperty("result").EnumerateArray(), item =>
            item.GetProperty("label").GetString() == "vbCrLf");

        var hover = await SendPositionRequestAsync(process, 4, "textDocument/hover", uri, text, "vbCrLf");
        var hoverValue = hover.GetProperty("result").GetProperty("contents").GetProperty("value").GetString();
        Assert.Contains("Carriage return-linefeed character combination.", hoverValue, StringComparison.Ordinal);
        Assert.Contains("```vba\nConst vbCrLf As String\n```", hoverValue, StringComparison.Ordinal);

        var semanticTokensResponse = await process.SendRequestAsync(5,
            "textDocument/semanticTokens/full",
            new
            {
                textDocument = new { uri }
            });
        var semanticTokens = DecodeSemanticTokens(semanticTokensResponse, text);
        Assert.Contains(semanticTokens, token =>
            token.Text == "vbCrLf"
            && token.TokenType == "field"
            && token.TokenModifiers.Contains("readonly")
            && token.TokenModifiers.Contains("defaultLibrary"));

        var prepareRename = await SendPositionRequestAsync(
            process,
            6,
            "textDocument/prepareRename",
            uri,
            text,
            "vbCrLf");
        var prepareError = prepareRename.GetProperty("error");
        Assert.Equal(-32803, prepareError.GetProperty("code").GetInt32());
        Assert.Equal(
            "notRenameTarget",
            prepareError
                .GetProperty("data")
                .GetProperty("reason")
                .GetString());

        var definition = await SendPositionRequestAsync(
            process,
            7,
            "textDocument/definition",
            uri,
            text,
            "vbCrLf");
        Assert.Equal(JsonValueKind.Null, definition.GetProperty("result").ValueKind);
        Assert.DoesNotContain(
            VbaProjectReferenceCatalogSet.ExternalDefinitionUriPrefix,
            definition.GetRawText(),
            StringComparison.Ordinal);

        var rename = await SendPositionRequestAsync(
            process,
            8,
            "textDocument/rename",
            uri,
            text,
            "vbCrLf",
            0,
            new { newName = "lineBreak" });
        var renameError = rename.GetProperty("error");
        Assert.Equal(-32803, renameError.GetProperty("code").GetInt32());
        Assert.Equal(
            "notRenameTarget",
            renameError
                .GetProperty("data")
                .GetProperty("reason")
                .GetString());

        await process.ShutdownAsync(9);
    }

    [Fact]
    public async Task Server_allows_definition_and_rename_for_a_source_shadow_of_a_catalog_name()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/SourceShadow.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"SourceShadow\"",
            "Option Explicit",
            "Public Sub Run()",
            "    Dim vbCrLf As String",
            "    vbcrlf = \"shadow\"",
            "End Sub"
        ]);
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

        var definition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            uri,
            text,
            "vbcrlf");
        var location = definition.GetProperty("result");
        Assert.Equal(uri, location.GetProperty("uri").GetString());
        Assert.Equal(
            3,
            location.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());

        var prepareRename = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/prepareRename",
            uri,
            text,
            "vbcrlf");
        var prepareResult = prepareRename.GetProperty("result");
        Assert.Equal(
            "vbCrLf",
            prepareResult.GetProperty("placeholder").GetString());
        Assert.Equal(
            4,
            prepareResult
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());
        Assert.Equal(
            "    ".Length,
            prepareResult
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("character")
                .GetInt32());
        Assert.Equal(
            "    vbCrLf".Length,
            prepareResult
                .GetProperty("range")
                .GetProperty("end")
                .GetProperty("character")
                .GetInt32());

        var rename = await SendPositionRequestAsync(
            process,
            4,
            "textDocument/rename",
            uri,
            text,
            "vbcrlf",
            0,
            new { newName = "sourceLineBreak" });
        var edits = rename
            .GetProperty("result")
            .GetProperty("changes")
            .GetProperty(uri)
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, edits.Length);
        Assert.All(edits, edit =>
            Assert.Equal("sourceLineBreak", edit.GetProperty("newText").GetString()));

        await process.ShutdownAsync(5);
    }

    [Fact]
    public async Task Server_publishes_diagnostics_after_open_and_change_notifications()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.SendRequestAsync(1,
            "initialize",
            new
            {
                processId = Environment.ProcessId,
                rootUri = (string?)null,
                capabilities = new { }
            });
        await process.SendNotificationAsync("initialized", new { });

        const string invalidLine = "        \"needle\", _ ' comment";
        await process.SendNotificationAsync("textDocument/didOpen",
            new
            {
                textDocument = new
                {
                    uri = "file:///C:/work/Module1.bas",
                    languageId = "vba",
                    version = 1,
                    text = string.Join('\n', [
                        "Attribute VB_Name = \"Module1\"",
                        "Option Explicit",
                        "",
                        "Public Sub Run()",
                        "    ReadValue( _",
                        invalidLine,
                        "End Sub"
                    ])
                }
            });

        var invalidDiagnostics = await process.WaitForNotificationAsync("textDocument/publishDiagnostics");
        var firstDiagnostic = invalidDiagnostics
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .Single();
        Assert.Equal("syntax.invalidTrailingCommentContinuation", firstDiagnostic.GetProperty("code").GetString());
        Assert.Equal("Code line-continuation marker cannot be followed by a comment.", firstDiagnostic.GetProperty("message").GetString());
        Assert.Equal("vba-language-server", firstDiagnostic.GetProperty("source").GetString());
        Assert.Equal(1, firstDiagnostic.GetProperty("severity").GetInt32());
        Assert.Equal(5, firstDiagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(invalidLine.IndexOf('_'), firstDiagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(5, firstDiagnostic.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(invalidLine.Length, firstDiagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());

        await process.SendNotificationAsync("textDocument/didChange",
            new
            {
                textDocument = new
                {
                    uri = "file:///C:/work/Module1.bas",
                    version = 2
                },
                contentChanges = new[]
                {
                    new
                    {
                        text = string.Join('\n', [
                            "Attribute VB_Name = \"Module1\"",
                            "Option Explicit",
                            "",
                            "Public Sub Run()",
                            "    ReadValue( _",
                            "        \"needle\")",
                            "End Sub"
                        ])
                    }
                }
            });

        var validDiagnostics = await process.WaitForNotificationAsync("textDocument/publishDiagnostics");
        Assert.Empty(validDiagnostics.GetProperty("params").GetProperty("diagnostics").EnumerateArray());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_returns_document_symbols_for_representative_source_definitions()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        await process.SendNotificationAsync("textDocument/didOpen",
            new
            {
                textDocument = new
                {
                    uri = "file:///C:/work/Worker.bas",
                    languageId = "vba",
                    version = 1,
                    text = string.Join('\n', [
                        "Attribute VB_Name = \"WorkerModule\"",
                        "Option Explicit",
                        "Public Const PublicLimit As Long = 1",
                        "Private moduleValue As String",
                        "Public Event Saved(ByVal name As String)",
                        "Public Enum Status",
                        "    StatusReady = 1",
                        "    StatusDone",
                        "End Enum",
                        "Public Type CustomerRecord",
                        "    Id As Long",
                        "    Name As String",
                        "End Type",
                        "Public Function BuildValue(ByVal inputText As String) As String",
                        "    Dim localCount As Long",
                        "    BuildValue = inputText",
                        "End Function",
                        "Public Property Get DisplayName() As String",
                        "End Property"
                    ])
                }
            });

        var response = await process.SendRequestAsync(2,
            "textDocument/documentSymbol",
            new
            {
                textDocument = new { uri = "file:///C:/work/Worker.bas" }
            });

        var symbolNames = response
            .GetProperty("result")
            .EnumerateArray()
            .Select(symbol => symbol.GetProperty("name").GetString())
            .ToArray();
        Assert.Contains("WorkerModule", symbolNames);
        Assert.Contains("PublicLimit", symbolNames);
        Assert.Contains("moduleValue", symbolNames);
        Assert.Contains("Saved", symbolNames);
        Assert.Contains("Status", symbolNames);
        Assert.Contains("StatusReady", symbolNames);
        Assert.Contains("CustomerRecord", symbolNames);
        Assert.Contains("Id", symbolNames);
        Assert.Contains("BuildValue", symbolNames);
        Assert.Contains("inputText", symbolNames);
        Assert.Contains("localCount", symbolNames);
        Assert.Contains("DisplayName", symbolNames);

        const string classUri = "file:///C:/work/Customer.cls";
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(classUri, string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Customer\"",
            "Option Explicit",
            "Public Event Changed()",
            "Public Property Get DisplayName() As String",
            "End Property"
        ])));
        var classSymbols = await process.SendRequestAsync(3,
            "textDocument/documentSymbol",
            new
            {
                textDocument = new { uri = classUri }
            });
        var classSymbolNames = classSymbols
            .GetProperty("result")
            .EnumerateArray()
            .Select(symbol => symbol.GetProperty("name").GetString())
            .ToArray();
        Assert.Contains("Customer", classSymbolNames);
        Assert.Contains("Changed", classSymbolNames);
        Assert.Contains("DisplayName", classSymbolNames);

        const string formUri = "file:///C:/work/Dialog.frm";
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(formUri, string.Join('\n', [
            "VERSION 5.00",
            "Begin VB.Form Dialog",
            "  Caption = \"Designer caption\"",
            "End",
            "Attribute VB_Name = \"Dialog\"",
            "Option Explicit",
            "Private Sub CommandButton1_Click()",
            "End Sub"
        ])));
        var formSymbols = await process.SendRequestAsync(4,
            "textDocument/documentSymbol",
            new
            {
                textDocument = new { uri = formUri }
            });
        var formSymbolNames = formSymbols
            .GetProperty("result")
            .EnumerateArray()
            .Select(symbol => symbol.GetProperty("name").GetString())
            .ToArray();
        Assert.Contains("Dialog", formSymbolNames);
        Assert.Contains("CommandButton1_Click", formSymbolNames);
        Assert.DoesNotContain("Caption", formSymbolNames);

        await process.ShutdownAsync(5);
    }

    [Fact]
    public async Task Server_resolves_representative_source_definitions_and_ambiguity()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string helperUri = "file:///C:/work/Helpers.bas";
        const string workerUri = "file:///C:/work/Worker.bas";
        const string duplicateAUri = "file:///C:/work/DuplicateA.bas";
        const string duplicateBUri = "file:///C:/work/DuplicateB.bas";
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(helperUri, string.Join('\n', [
            "Attribute VB_Name = \"Helpers\"",
            "Option Explicit",
            "",
            "Public Function BuildValue() As String",
            "End Function",
            "Private Function HiddenValue() As String",
            "End Function"
        ])));
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(duplicateAUri, string.Join('\n', [
            "Attribute VB_Name = \"DuplicateA\"",
            "Public Function DuplicateValue() As String",
            "End Function"
        ])));
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(duplicateBUri, string.Join('\n', [
            "Attribute VB_Name = \"DuplicateB\"",
            "Public Function DuplicateValue() As String",
            "End Function"
        ])));
        var workerText = string.Join('\n', [
            "Option Explicit",
            "Public Sub Run()",
            "    Dim localValue As String",
            "    localValue = BuildValue()",
            "    localValue = Helpers.BuildValue()",
            "    localValue = HiddenValue()",
            "    localValue = DuplicateValue()",
            "End Sub"
        ]);
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(workerUri, workerText));

        var unqualified = await RequestDefinitionAsync(process, 2, workerUri, workerText, "BuildValue()");
        Assert.Equal(helperUri, unqualified.GetProperty("uri").GetString());
        Assert.Equal(3, unqualified.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal("Public Function ".Length, unqualified.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());

        var qualified = await RequestDefinitionAsync(process, 3, workerUri, workerText, "Helpers.BuildValue()", "Helpers.".Length);
        Assert.Equal(helperUri, qualified.GetProperty("uri").GetString());

        var privateResult = await SendDefinitionRequestAsync(process, 4, workerUri, workerText, "HiddenValue()");
        Assert.Equal(JsonValueKind.Null, privateResult.ValueKind);

        var ambiguousResult = await SendDefinitionRequestAsync(process, 5, workerUri, workerText, "DuplicateValue()");
        Assert.Equal(JsonValueKind.Null, ambiguousResult.ValueKind);

        var localDefinition = await RequestDefinitionAsync(process, 6, workerUri, workerText, "localValue = BuildValue()");
        Assert.Equal(workerUri, localDefinition.GetProperty("uri").GetString());
        Assert.Equal(2, localDefinition.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal("    Dim ".Length, localDefinition.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());

        var fallbackSymbols = await process.SendRequestAsync(7,
            "textDocument/documentSymbol",
            new
            {
                textDocument = new { uri = workerUri }
            });
        Assert.Equal("Worker", fallbackSymbols.GetProperty("result").EnumerateArray().First().GetProperty("name").GetString());

        await process.ShutdownAsync(8);
    }

    [Fact]
    public async Task Server_defines_a_use_as_every_conditional_declaration_variant()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalFunctions.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalFunctions\"",
            "Option Explicit",
            "#If VBA7 Then",
            "Public Function BuildValue() As String",
            "End Function",
            "#Else",
            "Public Function buildvalue() As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Debug.Print BuildValue()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var definition = await SendDefinitionRequestAsync(
            process,
            2,
            uri,
            text,
            "BuildValue()",
            text.LastIndexOf("BuildValue()", StringComparison.Ordinal)
                - text.IndexOf("BuildValue()", StringComparison.Ordinal));

        Assert.Equal(JsonValueKind.Array, definition.ValueKind);
        var locations = definition.EnumerateArray().ToArray();
        Assert.Equal(2, locations.Length);
        Assert.Equal(3, locations[0].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(6, locations[1].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_forms_one_family_from_nested_conditional_branches()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/NestedConditionalFamily.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"NestedConditionalFamily\"",
            "#If OUTER_CONFIGURATION Then",
            "#If INNER_CONFIGURATION Then",
            "Public Function BuildValue() As String",
            "End Function",
            "#Else",
            "Public Function buildvalue() As String",
            "End Function",
            "#End If",
            "#End If",
            "Public Sub Run()",
            "    Debug.Print BuildValue()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var definition = await SendDefinitionRequestAsync(
            process,
            2,
            uri,
            text,
            "BuildValue()",
            text.LastIndexOf("BuildValue()", StringComparison.Ordinal)
                - text.IndexOf("BuildValue()", StringComparison.Ordinal));
        Assert.Equal(
            [3, 6],
            definition
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_marks_a_single_conditional_completion_without_exposing_its_condition()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalCompletion.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalCompletion\"",
            "Option Explicit",
            "#If VBA7 Then",
            "Public Function BuildValue() As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Bui",
            "    Debug.Print BuildValue()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new { line = 7, character = "    Bui".Length }
            });
        var item = completion
            .GetProperty("result")
            .EnumerateArray()
            .Single(candidate => candidate.GetProperty("label").GetString() == "BuildValue");

        Assert.EndsWith(" [#If]", item.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("VBA7", item.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[#If]", item.GetProperty("label").GetString(), StringComparison.Ordinal);

        var hover = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/hover",
            uri,
            text,
            "Debug.Print BuildValue()",
            "Debug.Print ".Length);
        var hoverValue = hover
            .GetProperty("result")
            .GetProperty("contents")
            .GetProperty("value")
            .GetString();
        Assert.Contains(
            "**BuildValue [#If]**",
            hoverValue,
            StringComparison.Ordinal);
        Assert.Contains(
            "Function BuildValue() As String [#If]",
            hoverValue,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "VBA7",
            hoverValue,
            StringComparison.OrdinalIgnoreCase);

        var references = await SendPositionRequestAsync(
            process,
            4,
            "textDocument/references",
            uri,
            text,
            "Debug.Print BuildValue()",
            "Debug.Print ".Length);
        Assert.Equal(
            [3, 8],
            references
                .GetProperty("result")
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        await process.ShutdownAsync(5);
    }

    [Fact]
    public async Task Server_projects_one_completion_row_for_an_all_guarded_mixed_kind_family()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalMixedKindCompletion.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalMixedKindCompletion\"",
            "#If FUNCTION_CONFIGURATION Then",
            "Public Function BuildValue() As String",
            "End Function",
            "#End If",
            "#If PROPERTY_CONFIGURATION Then",
            "Public Property Get buildvalue() As String",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    buil",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new { line = 10, character = "    buil".Length }
            });
        var item = Assert.Single(
            completion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "BuildValue",
                StringComparison.OrdinalIgnoreCase));

        Assert.Equal("BuildValue", item.GetProperty("label").GetString());
        Assert.EndsWith(
            " [#If]",
            item.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FUNCTION_CONFIGURATION",
            item.GetRawText(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "PROPERTY_CONFIGURATION",
            item.GetRawText(),
            StringComparison.OrdinalIgnoreCase);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_completes_a_module_qualified_mixed_kind_conditional_family_once()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string helperUri = "file:///C:/work/Helper.bas";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(helperUri, string.Join('\n', [
                "Attribute VB_Name = \"Helper\"",
                "#If FUNCTION_CONFIGURATION Then",
                "Public Function BuildValue() As String",
                "End Function",
                "#End If",
                "#If PROPERTY_CONFIGURATION Then",
                "Public Property Get buildvalue() As String",
                "End Property",
                "#End If"
            ])));
        const string callerUri = "file:///C:/work/ConditionalMixedKindCaller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalMixedKindCaller\"",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = Helper.",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = callerUri },
                position = new
                {
                    line = 3,
                    character = "    result = Helper.".Length
                }
            });
        var item = Assert.Single(
            completion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "BuildValue",
                StringComparison.OrdinalIgnoreCase));

        Assert.Equal("BuildValue", item.GetProperty("label").GetString());
        Assert.EndsWith(
            " [#If]",
            item.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FUNCTION_CONFIGURATION",
            item.GetRawText(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "PROPERTY_CONFIGURATION",
            item.GetRawText(),
            StringComparison.OrdinalIgnoreCase);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_completes_one_logical_type_for_conditional_type_variants()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalTypeCompletion.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalTypeCompletion\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Enum RunMode",
            "    FirstMode = 1",
            "End Enum",
            "#End If",
            "#If SECOND_CONFIGURATION Then",
            "Private Enum runmode",
            "    SecondMode = 2",
            "End Enum",
            "#End If",
            "Public Sub Run()",
            "    Dim current As run",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new { line = 12, character = "    Dim current As run".Length }
            });
        var item = Assert.Single(
            completion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "RunMode",
                StringComparison.OrdinalIgnoreCase));

        Assert.Equal("RunMode", item.GetProperty("label").GetString());
        Assert.EndsWith(
            " [#If]",
            item.GetProperty("detail").GetString(),
            StringComparison.Ordinal);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_defines_every_project_visible_friend_type_variant()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string firstUri = "file:///C:/work/FriendTypeA.bas";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(firstUri, string.Join('\n', [
                "Attribute VB_Name = \"FriendTypeA\"",
                "#If FIRST_CONFIGURATION Then",
                "Friend Enum RunMode",
                "    FirstMode = 1",
                "End Enum",
                "#End If"
            ])));
        const string secondUri = "file:///C:/work/FriendTypeB.bas";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(secondUri, string.Join('\n', [
                "Attribute VB_Name = \"FriendTypeB\"",
                "#If SECOND_CONFIGURATION Then",
                "Friend Enum runmode",
                "    SecondMode = 2",
                "End Enum",
                "#End If"
            ])));
        const string callerUri = "file:///C:/work/FriendTypeCaller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"FriendTypeCaller\"",
            "Public Sub Run()",
            "    Dim current As RunMode",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var definition = await SendDefinitionRequestAsync(
            process,
            2,
            callerUri,
            callerText,
            "RunMode");

        Assert.Equal(
            [(firstUri, 2), (secondUri, 2)],
            definition
                .EnumerateArray()
                .Select(location => (
                    location.GetProperty("uri").GetString(),
                    location
                        .GetProperty("range")
                        .GetProperty("start")
                        .GetProperty("line")
                        .GetInt32())));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_completes_one_logical_member_for_conditional_member_variants()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string workerUri = "file:///C:/work/ConditionalWorker.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"ConditionalWorker\"",
                "#If FIRST_CONFIGURATION Then",
                "Public Function BuildValue() As String",
                "End Function",
                "#End If",
                "#If SECOND_CONFIGURATION Then",
                "Public Function buildvalue() As String",
                "End Function",
                "#End If"
            ])));
        const string callerUri = "file:///C:/work/ConditionalMemberCaller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalMemberCaller\"",
            "Private worker As ConditionalWorker",
            "Public Sub Run()",
            "    worker.buil",
            "    Debug.Print worker.BuildValue()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = callerUri },
                position = new { line = 3, character = "    worker.buil".Length }
            });
        var item = Assert.Single(
            completion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "BuildValue",
                StringComparison.OrdinalIgnoreCase));

        Assert.Equal("BuildValue", item.GetProperty("label").GetString());
        Assert.EndsWith(
            " [#If]",
            item.GetProperty("detail").GetString(),
            StringComparison.Ordinal);

        var definition = await SendDefinitionRequestAsync(
            process,
            3,
            callerUri,
            callerText,
            "worker.BuildValue()",
            "worker.".Length);
        Assert.Equal(
            [workerUri, workerUri],
            definition
                .EnumerateArray()
                .Select(location => location.GetProperty("uri").GetString()));
        Assert.Equal(
            [3, 7],
            definition
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_completes_a_conditional_member_when_only_a_later_variant_is_eligible()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string workerUri = "file:///C:/work/ConditionalEligibleWorker.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"ConditionalEligibleWorker\"",
                "#If SUB_CONFIGURATION Then",
                "Public Sub BuildValue()",
                "End Sub",
                "#End If",
                "#If FUNCTION_CONFIGURATION Then",
                "Public Function buildvalue() As String",
                "End Function",
                "#End If"
            ])));
        const string callerUri = "file:///C:/work/ConditionalEligibleCaller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalEligibleCaller\"",
            "Private worker As ConditionalEligibleWorker",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = worker.buil",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = callerUri },
                position = new
                {
                    line = 4,
                    character = "    result = worker.buil".Length
                }
            });
        var item = Assert.Single(
            completion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "BuildValue",
                StringComparison.OrdinalIgnoreCase));

        Assert.Equal("BuildValue", item.GetProperty("label").GetString());
        Assert.Equal(3, item.GetProperty("kind").GetInt32());
        Assert.StartsWith(
            "Function buildvalue() As String",
            item.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.EndsWith(
            " [#If]",
            item.GetProperty("detail").GetString(),
            StringComparison.Ordinal);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_does_not_complete_a_conditional_member_from_an_invisible_later_variant()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string workerUri = "file:///C:/work/ConditionalVisibilityWorker.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"ConditionalVisibilityWorker\"",
                "#If WRITE_CONFIGURATION Then",
                "Public Property Let Value(ByVal assigned As Long)",
                "End Property",
                "#End If",
                "#If READ_CONFIGURATION Then",
                "Private Property Get value() As Long",
                "End Property",
                "#End If"
            ])));
        const string callerUri = "file:///C:/work/ConditionalVisibilityCaller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalVisibilityCaller\"",
            "Private worker As ConditionalVisibilityWorker",
            "Public Sub Run()",
            "    Dim result As Long",
            "    result = worker.val",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = callerUri },
                position = new
                {
                    line = 4,
                    character = "    result = worker.val".Length
                }
            });

        Assert.DoesNotContain(
            completion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "Value",
                StringComparison.OrdinalIgnoreCase));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_requires_a_visible_accessor_before_binding_a_unified_property_family()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string workerUri = "file:///C:/work/UnifiedPropertyVisibilityWorker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"UnifiedPropertyVisibilityWorker\"",
            "#If READ_CONFIGURATION Then",
            "Public Property Get Value() As Long",
            "End Property",
            "#End If",
            "#If WRITE_CONFIGURATION Then",
            "Private Property Let value(ByVal assigned As Long)",
            "End Property",
            "#End If",
            "Private Sub WriteInside()",
            "    Value = 1",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        const string callerUri = "file:///C:/work/UnifiedPropertyVisibilityCaller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"UnifiedPropertyVisibilityCaller\"",
            "Private worker As UnifiedPropertyVisibilityWorker",
            "Public Sub Run()",
            "    Debug.Print worker.Value",
            "    worker.Value = 1",
            "    worker.val = 1",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var readDefinition = await SendDefinitionRequestAsync(
            process,
            2,
            callerUri,
            callerText,
            "worker.Value",
            "worker.".Length);
        Assert.Equal(
            [3, 7],
            readDefinition
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        var writeDefinition = await SendDefinitionRequestAsync(
            process,
            3,
            callerUri,
            callerText,
            "worker.Value = 1",
            "worker.".Length);
        Assert.Equal(JsonValueKind.Null, writeDefinition.ValueKind);

        var writeHover = await SendPositionRequestAsync(
            process,
            4,
            "textDocument/hover",
            callerUri,
            callerText,
            "worker.Value = 1",
            "worker.".Length);
        Assert.Equal(JsonValueKind.Null, writeHover.GetProperty("result").ValueKind);

        var internalWriteDefinition = await SendDefinitionRequestAsync(
            process,
            5,
            workerUri,
            workerText,
            "    Value = 1",
            "    ".Length);
        Assert.Equal(
            [3, 7],
            internalWriteDefinition
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        var writeCompletion = await process.SendRequestAsync(
            6,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = callerUri },
                position = new
                {
                    line = 5,
                    character = "    worker.val".Length
                }
            });
        Assert.DoesNotContain(
            writeCompletion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "Value",
                StringComparison.OrdinalIgnoreCase));

        await process.ShutdownAsync(7);
    }

    [Fact]
    public async Task Server_completes_an_unqualified_conditional_family_when_only_a_later_variant_is_eligible()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalEligibleUnqualified.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalEligibleUnqualified\"",
            "#If SUB_CONFIGURATION Then",
            "Public Sub BuildValue()",
            "End Sub",
            "#End If",
            "#If FUNCTION_CONFIGURATION Then",
            "Public Function buildvalue() As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = buil",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new
                {
                    line = 11,
                    character = "    result = buil".Length
                }
            });
        var item = Assert.Single(
            completion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "BuildValue",
                StringComparison.OrdinalIgnoreCase));

        Assert.Equal("BuildValue", item.GetProperty("label").GetString());
        Assert.Equal(3, item.GetProperty("kind").GetInt32());
        Assert.StartsWith(
            "Function buildvalue() As String",
            item.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.EndsWith(
            " [#If]",
            item.GetProperty("detail").GetString(),
            StringComparison.Ordinal);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_does_not_complete_an_eligible_declaration_through_an_ordinary_same_name_ambiguity()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/OrdinaryCompletionAmbiguity.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"OrdinaryCompletionAmbiguity\"",
            "Public Sub BuildValue()",
            "End Sub",
            "Public Function buildvalue() As String",
            "End Function",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = buil",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new
                {
                    line = 7,
                    character = "    result = buil".Length
                }
            });

        Assert.DoesNotContain(
            completion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "BuildValue",
                StringComparison.OrdinalIgnoreCase));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_resolves_a_function_and_complementary_property_accessors_as_one_conditional_family()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalFunctionPropertyOverlap.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalFunctionPropertyOverlap\"",
            "#If FUNCTION_CONFIGURATION Then",
            "Public Function Value() As Long",
            "End Function",
            "#End If",
            "#If GET_CONFIGURATION Then",
            "Public Property Get value() As Long",
            "End Property",
            "#End If",
            "#If LET_CONFIGURATION Then",
            "Public Property Let VALUE(ByVal assigned As Long)",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    Dim result As Long",
            "    result = val",
            "    Debug.Print Value",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new
                {
                    line = 16,
                    character = "    result = val".Length
                }
            });
        var item = Assert.Single(
            completion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "Value",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Value", item.GetProperty("label").GetString());
        Assert.EndsWith(
            " [#If]",
            item.GetProperty("detail").GetString(),
            StringComparison.Ordinal);

        var definition = await SendDefinitionRequestAsync(
            process,
            3,
            uri,
            text,
            "Debug.Print Value",
            "Debug.Print ".Length);

        Assert.Equal(
            [3, 7, 11],
            definition
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        var hover = await SendPositionRequestAsync(
            process,
            4,
            "textDocument/hover",
            uri,
            text,
            "Debug.Print Value",
            "Debug.Print ".Length);
        var hoverValue = hover
            .GetProperty("result")
            .GetProperty("contents")
            .GetProperty("value")
            .GetString();
        Assert.Contains("Function Value", hoverValue, StringComparison.Ordinal);
        Assert.Contains(
            "Property value() As Long",
            hoverValue,
            StringComparison.Ordinal);
        Assert.Contains(
            "Property VALUE(assigned As Long)",
            hoverValue,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FUNCTION_CONFIGURATION",
            hoverValue,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "GET_CONFIGURATION",
            hoverValue,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "LET_CONFIGURATION",
            hoverValue,
            StringComparison.OrdinalIgnoreCase);

        await process.ShutdownAsync(5);
    }

    [Fact]
    public async Task Server_does_not_absorb_an_unconditional_property_accessor_through_a_guarded_accessor_family()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/MixedConditionalPropertyCollision.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"MixedConditionalPropertyCollision\"",
            "#If FUNCTION_CONFIGURATION Then",
            "Public Function Value() As Long",
            "End Function",
            "#End If",
            "#If GET_CONFIGURATION Then",
            "Public Property Get value() As Long",
            "End Property",
            "#End If",
            "Public Property Let VALUE(ByVal assigned As Long)",
            "End Property",
            "Public Sub Run()",
            "    Dim result As Long",
            "    result = val",
            "    Debug.Print Value",
            "    Value = 1",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var diagnostics = await process.WaitForDiagnosticsAsync(uri);
        var duplicateLines = diagnostics
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .Where(diagnostic => diagnostic.GetProperty("code").GetString()
                == "validation.duplicateDeclaration")
            .Select(diagnostic => diagnostic
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32())
            .ToArray();
        Assert.Contains(3, duplicateLines);
        Assert.Contains(10, duplicateLines);

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new
                {
                    line = 14,
                    character = "    result = val".Length
                }
            });
        Assert.DoesNotContain(
            completion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "Value",
                StringComparison.OrdinalIgnoreCase));

        var definition = await SendDefinitionRequestAsync(
            process,
            3,
            uri,
            text,
            "Debug.Print Value",
            "Debug.Print ".Length);
        Assert.Equal(JsonValueKind.Null, definition.ValueKind);

        var hover = await SendPositionRequestAsync(
            process,
            4,
            "textDocument/hover",
            uri,
            text,
            "Debug.Print Value",
            "Debug.Print ".Length);
        Assert.Equal(
            JsonValueKind.Null,
            hover.GetProperty("result").ValueKind);

        var assignmentDefinition = await SendDefinitionRequestAsync(
            process,
            5,
            uri,
            text,
            "    Value = 1",
            "    ".Length);
        Assert.Equal(JsonValueKind.Null, assignmentDefinition.ValueKind);

        var assignmentHover = await SendPositionRequestAsync(
            process,
            6,
            "textDocument/hover",
            uri,
            text,
            "    Value = 1",
            "    ".Length);
        Assert.Equal(
            JsonValueKind.Null,
            assignmentHover.GetProperty("result").ValueKind);

        var declarationDefinition = await SendDefinitionRequestAsync(
            process,
            7,
            uri,
            text,
            "Public Function Value",
            "Public Function ".Length);
        Assert.Equal(
            [3, 7],
            declarationDefinition
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        var functionReferences = await SendPositionRequestAsync(
            process,
            8,
            "textDocument/references",
            uri,
            text,
            "Public Function Value",
            "Public Function ".Length);
        Assert.Equal(
            [3, 7],
            functionReferences
                .GetProperty("result")
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        var propertyReferences = await SendPositionRequestAsync(
            process,
            9,
            "textDocument/references",
            uri,
            text,
            "Public Property Let VALUE",
            "Public Property Let ".Length);
        Assert.Equal(
            [7, 10],
            propertyReferences
                .GetProperty("result")
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        await process.ShutdownAsync(10);
    }

    [Fact]
    public async Task Server_does_not_complete_a_source_qualified_write_through_a_mixed_collision()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string targetUri = "file:///C:/work/MixedQualifiedCollision.bas";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(targetUri, string.Join('\n', [
                "Attribute VB_Name = \"MixedQualifiedCollision\"",
                "#If FUNCTION_CONFIGURATION Then",
                "Public Function Value() As Long",
                "End Function",
                "#End If",
                "#If GET_CONFIGURATION Then",
                "Public Property Get value() As Long",
                "End Property",
                "#End If",
                "Public Property Let VALUE(ByVal assigned As Long)",
                "End Property"
            ])));
        const string callerUri = "file:///C:/work/MixedQualifiedCaller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"MixedQualifiedCaller\"",
            "Public Sub Run()",
            "    MixedQualifiedCollision. = 1",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = callerUri },
                position = new
                {
                    line = 2,
                    character = "    MixedQualifiedCollision.".Length
                }
            });
        Assert.DoesNotContain(
            completion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "Value",
                StringComparison.OrdinalIgnoreCase));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_resolves_a_member_function_and_complementary_property_accessors_as_one_conditional_family()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string workerUri = "file:///C:/work/ConditionalMemberPropertyOverlap.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"ConditionalMemberPropertyOverlap\"",
                "#If FUNCTION_CONFIGURATION Then",
                "Public Function Value() As Long",
                "End Function",
                "#End If",
                "#If GET_CONFIGURATION Then",
                "Public Property Get value() As Long",
                "End Property",
                "#End If",
                "#If LET_CONFIGURATION Then",
                "Public Property Let VALUE(ByVal assigned As Long)",
                "End Property",
                "#End If"
            ])));
        const string callerUri = "file:///C:/work/ConditionalMemberPropertyCaller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalMemberPropertyCaller\"",
            "Private item As ConditionalMemberPropertyOverlap",
            "Public Sub Run()",
            "    Debug.Print item.Value",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var definition = await SendDefinitionRequestAsync(
            process,
            2,
            callerUri,
            callerText,
            "item.Value",
            "item.".Length);

        Assert.Equal(
            [(workerUri, 3), (workerUri, 7), (workerUri, 11)],
            definition
                .EnumerateArray()
                .Select(location => (
                    location.GetProperty("uri").GetString(),
                    location
                        .GetProperty("range")
                        .GetProperty("start")
                        .GetProperty("line")
                        .GetInt32())));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_keeps_members_from_every_project_type_variant()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string firstUri = "file:///C:/work/ConditionalPayloadA.bas";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(firstUri, string.Join('\n', [
                "Attribute VB_Name = \"ConditionalPayloadA\"",
                "#If FIRST_CONFIGURATION Then",
                "Public Type Payload",
                "    FirstValue As Long",
                "End Type",
                "#End If"
            ])));
        const string secondUri = "file:///C:/work/ConditionalPayloadB.bas";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(secondUri, string.Join('\n', [
                "Attribute VB_Name = \"ConditionalPayloadB\"",
                "#If SECOND_CONFIGURATION Then",
                "Public Type payload",
                "    SecondValue As Long",
                "End Type",
                "#End If"
            ])));
        const string callerUri = "file:///C:/work/ConditionalPayloadCaller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalPayloadCaller\"",
            "Private item As Payload",
            "Public Sub Run()",
            "    item.",
            "    Debug.Print item.SecondValue",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = callerUri },
                position = new { line = 3, character = "    item.".Length }
            });
        var labels = completion
            .GetProperty("result")
            .EnumerateArray()
            .Select(candidate => candidate.GetProperty("label").GetString())
            .ToArray();
        Assert.Contains("FirstValue", labels);
        Assert.Contains("SecondValue", labels);

        var definition = await SendDefinitionRequestAsync(
            process,
            3,
            callerUri,
            callerText,
            "item.SecondValue",
            "item.".Length);
        Assert.Equal(secondUri, definition.GetProperty("uri").GetString());
        Assert.Equal(
            3,
            definition
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_defines_a_same_named_member_family_across_project_type_variants()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string firstUri = "file:///C:/work/ConditionalPayloadMemberA.bas";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(firstUri, string.Join('\n', [
                "Attribute VB_Name = \"ConditionalPayloadMemberA\"",
                "#If FIRST_CONFIGURATION Then",
                "Public Type Payload",
                "    Value As Long",
                "End Type",
                "#End If"
            ])));
        const string secondUri = "file:///C:/work/ConditionalPayloadMemberB.bas";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(secondUri, string.Join('\n', [
                "Attribute VB_Name = \"ConditionalPayloadMemberB\"",
                "#If SECOND_CONFIGURATION Then",
                "Public Type payload",
                "    value As Long",
                "End Type",
                "#End If"
            ])));
        const string callerUri = "file:///C:/work/ConditionalPayloadMemberCaller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalPayloadMemberCaller\"",
            "Private item As Payload",
            "Public Sub Run()",
            "    item.val",
            "    Debug.Print item.Value",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = callerUri },
                position = new { line = 3, character = "    item.val".Length }
            });
        var item = Assert.Single(
            completion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "Value",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Value", item.GetProperty("label").GetString());
        Assert.EndsWith(
            " [#If]",
            item.GetProperty("detail").GetString(),
            StringComparison.Ordinal);

        var definition = await SendDefinitionRequestAsync(
            process,
            3,
            callerUri,
            callerText,
            "item.Value",
            "item.".Length);

        Assert.Equal(
            [firstUri, secondUri],
            definition
                .EnumerateArray()
                .Select(location => location.GetProperty("uri").GetString()));
        Assert.Equal(
            [3, 3],
            definition
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        var references = await SendPositionRequestAsync(
            process,
            4,
            "textDocument/references",
            callerUri,
            callerText,
            "item.Value",
            "item.".Length);
        Assert.Equal(
            [(firstUri, 3), (secondUri, 3), (callerUri, 4)],
            references
                .GetProperty("result")
                .EnumerateArray()
                .Select(location => (
                    location.GetProperty("uri").GetString(),
                    location
                        .GetProperty("range")
                        .GetProperty("start")
                        .GetProperty("line")
                        .GetInt32())));

        var hover = await SendPositionRequestAsync(
            process,
            5,
            "textDocument/hover",
            callerUri,
            callerText,
            "item.Value",
            "item.".Length);
        var hoverValue = hover
            .GetProperty("result")
            .GetProperty("contents")
            .GetProperty("value")
            .GetString();
        Assert.Contains("**Value [#If]**", hoverValue, StringComparison.Ordinal);
        Assert.Contains("Value As Long [#If]", hoverValue, StringComparison.Ordinal);
        Assert.Contains("value As Long [#If]", hoverValue, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FIRST_CONFIGURATION",
            hoverValue,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "SECOND_CONFIGURATION",
            hoverValue,
            StringComparison.OrdinalIgnoreCase);

        await process.ShutdownAsync(6);
    }

    [Fact]
    public async Task Server_hovers_one_conditional_family_with_every_physical_declaration()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalHover.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalHover\"",
            "Option Explicit",
            "#If VBA7 Then",
            "Public Function BuildValue() As String",
            "End Function",
            "#ElseIf Win64 Then",
            "Public Function buildvalue(ByVal value As Long) As Long",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Debug.Print BuildValue()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var hover = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/hover",
            uri,
            text,
            "Debug.Print BuildValue()",
            "Debug.Print ".Length);
        var value = hover
            .GetProperty("result")
            .GetProperty("contents")
            .GetProperty("value")
            .GetString();
        var range = hover.GetProperty("result").GetProperty("range");

        Assert.Contains("**BuildValue [#If]**", value, StringComparison.Ordinal);
        Assert.Contains("Function BuildValue() As String [#If]", value, StringComparison.Ordinal);
        Assert.Contains(
            "Function buildvalue(value As Long) As Long [#If]",
            value,
            StringComparison.Ordinal);
        Assert.DoesNotContain("VBA7", value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Win64", value, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(16, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(10, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(26, range.GetProperty("end").GetProperty("character").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_hovers_an_ordinary_property_with_its_readable_accessor_when_the_setter_is_first()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/SetterFirstProperty.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"SetterFirstProperty\"",
            "Public Property Let Value(ByVal assigned As Long)",
            "End Property",
            "Public Property Get value() As Long",
            "End Property",
            "Public Sub Run()",
            "    Debug.Print Value",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var hover = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/hover",
            uri,
            text,
            "Debug.Print Value",
            "Debug.Print ".Length);
        var hoverValue = hover
            .GetProperty("result")
            .GetProperty("contents")
            .GetProperty("value")
            .GetString();

        Assert.Contains(
            "Property value() As Long",
            hoverValue,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "assigned As Long",
            hoverValue,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[#If]", hoverValue, StringComparison.Ordinal);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_uses_the_contextual_property_accessor_family_heading()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalPropertyCanonicalName.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalPropertyCanonicalName\"",
            "Public Property Get value() As Long",
            "End Property",
            "#If FIRST_CONFIGURATION Then",
            "Public Property Let Value(ByVal Assigned As Long)",
            "End Property",
            "#Else",
            "Public Property Let VALUE(ByVal Assigned As Long)",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    Debug.Print value",
            "    Value = 1",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var readHover = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/hover",
            uri,
            text,
            "    Debug.Print value",
            "    Debug.Print ".Length);
        var writeHover = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/hover",
            uri,
            text,
            "    Value = 1",
            "    ".Length);
        var readValue = readHover
            .GetProperty("result")
            .GetProperty("contents")
            .GetProperty("value")
            .GetString();
        var writeValue = writeHover
            .GetProperty("result")
            .GetProperty("contents")
            .GetProperty("value")
            .GetString();

        Assert.DoesNotContain("[#If]", readValue, StringComparison.Ordinal);
        Assert.Contains("**Value [#If]**", writeValue, StringComparison.Ordinal);
        Assert.Contains("Property value() As Long", readValue, StringComparison.Ordinal);
        Assert.Contains("Property Value(Assigned As Long) [#If]", writeValue, StringComparison.Ordinal);
        Assert.Contains("Property VALUE(Assigned As Long) [#If]", writeValue, StringComparison.Ordinal);

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_keeps_ordinary_property_definition_single_target()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/OrdinaryPropertyDefinition.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"OrdinaryPropertyDefinition\"",
            "Public Property Let Value(ByVal assigned As Long)",
            "End Property",
            "Public Property Get value() As Long",
            "End Property",
            "Public Sub Run()",
            "    Debug.Print Value",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var definition = await SendDefinitionRequestAsync(
            process,
            2,
            uri,
            text,
            "Debug.Print Value",
            "Debug.Print ".Length);

        Assert.Equal(
            4,
            definition
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_hovers_an_ordinary_setter_declaration_as_the_setter()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/OrdinaryPropertyDeclarationHover.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"OrdinaryPropertyDeclarationHover\"",
            "Public Property Let Value(ByVal assigned As Long)",
            "End Property",
            "Public Property Get value() As Long",
            "End Property"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var hover = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/hover",
            uri,
            text,
            "Public Property Let Value",
            "Public Property Let ".Length);
        var hoverValue = hover
            .GetProperty("result")
            .GetProperty("contents")
            .GetProperty("value")
            .GetString();

        Assert.Contains(
            "Property Value(assigned As Long)",
            hoverValue,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Property value() As Long",
            hoverValue,
            StringComparison.Ordinal);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_keeps_ordinary_property_completion_casing_from_the_readable_accessor_when_setter_is_first()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/OrdinaryPropertyCompletionCasing.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"OrdinaryPropertyCompletionCasing\"",
            "Public Property Let VALUE(ByVal assigned As Long)",
            "End Property",
            "Public Property Get value() As Long",
            "End Property",
            "Public Sub Run()",
            "    Dim result As Long",
            "    result = val",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new
                {
                    line = 8,
                    character = "    result = val".Length
                }
            });
        var item = Assert.Single(
            completion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "value",
                StringComparison.OrdinalIgnoreCase));

        Assert.Equal("value", item.GetProperty("label").GetString());
        Assert.Equal(
            "value",
            item.GetProperty("textEdit").GetProperty("newText").GetString());
        Assert.DoesNotContain(
            "[#If]",
            item.GetProperty("detail").GetString(),
            StringComparison.Ordinal);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_keeps_source_qualified_ordinary_property_write_completion_casing_from_the_setter()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string propertyUri = "file:///C:/work/OrdinaryPropertyWriteModule.bas";
        var propertyText = string.Join('\n', [
            "Attribute VB_Name = \"OrdinaryPropertyWriteModule\"",
            "Public Property Get value() As Long",
            "End Property",
            "Public Property Let VALUE(ByVal assigned As Long)",
            "End Property"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(propertyUri, propertyText));
        const string callerUri = "file:///C:/work/OrdinaryPropertyWriteCaller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"OrdinaryPropertyWriteCaller\"",
            "Public Sub Run()",
            "    OrdinaryPropertyWriteModule. = 1",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = callerUri },
                position = new
                {
                    line = 2,
                    character = "    OrdinaryPropertyWriteModule.".Length
                }
            });
        var item = Assert.Single(
            completion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "VALUE",
                StringComparison.OrdinalIgnoreCase));

        Assert.Equal("VALUE", item.GetProperty("label").GetString());
        Assert.StartsWith(
            "Property VALUE(assigned)",
            item.GetProperty("detail").GetString(),
            StringComparison.Ordinal);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_keeps_unqualified_ordinary_property_write_completion_from_the_coalesced_readable_accessor()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/OrdinaryPropertyWriteCompletion.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"OrdinaryPropertyWriteCompletion\"",
            "Public Property Get value() As Long",
            "End Property",
            "Public Property Let VALUE(ByVal assigned As Long)",
            "End Property",
            "Public Sub Run()",
            "    val = 1",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new
                {
                    line = 7,
                    character = "    val".Length
                }
            });
        var item = Assert.Single(
            completion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "value",
                StringComparison.OrdinalIgnoreCase));

        Assert.Equal("value", item.GetProperty("label").GetString());
        Assert.Equal(
            "value",
            item.GetProperty("textEdit").GetProperty("newText").GetString());
        Assert.Equal(
            "Property value() As Long",
            item.GetProperty("detail").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_references_every_conditional_variant_and_family_use()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalReferences.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalReferences\"",
            "Option Explicit",
            "#If VBA7 Then",
            "Public Function BuildValue() As String",
            "End Function",
            "#Else",
            "Public Function buildvalue() As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Debug.Print BuildValue()",
            "    Debug.Print buildvalue()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var references = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/references",
            uri,
            text,
            "Debug.Print BuildValue()",
            "Debug.Print ".Length);
        var lines = references
            .GetProperty("result")
            .EnumerateArray()
            .Select(location => location
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32())
            .ToArray();

        Assert.Equal([3, 6, 10, 11], lines);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_keeps_conditional_family_casing_independent_of_variant_visibility()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string familyUri = "file:///C:/work/ConditionalVisibility.bas";
        var familyText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalVisibility\"",
            "Option Explicit",
            "#If INTERNAL_BUILD Then",
            "Private Function buildValue() As String",
            "End Function",
            "#Else",
            "Public Function BUILDVALUE() As String",
            "End Function",
            "#End If"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(familyUri, familyText));
        const string callerUri = "file:///C:/work/Caller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"Caller\"",
            "Option Explicit",
            "Public Sub Run()",
            "    buil",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = callerUri },
                position = new { line = 3, character = "    buil".Length }
            });
        var item = Assert.Single(
            completion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "buildValue",
                StringComparison.OrdinalIgnoreCase));

        Assert.Equal("buildValue", item.GetProperty("label").GetString());
        Assert.EndsWith(" [#If]", item.GetProperty("detail").GetString(), StringComparison.Ordinal);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_diagnoses_every_mixed_unconditional_and_conditional_collision()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/MixedConditionalCollision.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"MixedConditionalCollision\"",
            "Option Explicit",
            "Public Function BuildValue() As String",
            "End Function",
            "#If VBA7 Then",
            "Public Function buildvalue() As String",
            "End Function",
            "#End If"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var collisions = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .Where(diagnostic => diagnostic.GetProperty("code").GetString()
                == "validation.duplicateDeclaration")
            .ToArray();

        Assert.Equal(2, collisions.Length);
        Assert.Equal(
            [2, 5],
            collisions.Select(diagnostic => diagnostic
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32()));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_does_not_diagnose_an_all_guarded_family_that_can_coexist()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/CoexistingConditionalFamily.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"CoexistingConditionalFamily\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function BuildValue() As String",
            "End Function",
            "#End If",
            "#If SECOND_CONFIGURATION Then",
            "Public Function buildvalue() As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Debug.Print BuildValue()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            diagnostic => diagnostic.GetProperty("code").GetString()
                == "validation.duplicateDeclaration");

        var definition = await SendDefinitionRequestAsync(
            process,
            2,
            uri,
            text,
            "Debug.Print BuildValue()",
            "Debug.Print ".Length);
        Assert.Equal(
            [2, 6],
            definition
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_keeps_complementary_property_accessors_legal_across_conditionals()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalPropertyAccessors.cls";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalPropertyAccessors\"",
            "Public Property Get Value() As Long",
            "End Property",
            "#If WRITE_ENABLED Then",
            "Public Property Let Value(ByVal RHS As Long)",
            "End Property",
            "#End If"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            diagnostic => diagnostic.GetProperty("code").GetString()
                == "validation.duplicateDeclaration");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_preserves_a_guarded_get_family_when_the_complementary_setter_is_unconditional()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/MixedPropertyAccessorProvenance.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"MixedPropertyAccessorProvenance\"",
            "#If FIRST_READ_CONFIGURATION Then",
            "Public Property Get Value() As Long",
            "End Property",
            "#Else",
            "Public Property Get value() As Long",
            "End Property",
            "#End If",
            "Public Property Let VALUE(ByVal assigned As Long)",
            "End Property",
            "Public Sub Run()",
            "    Dim result As Long",
            "    result = val",
            "    Debug.Print Value",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new
                {
                    line = 13,
                    character = "    result = val".Length
                }
            });
        var item = Assert.Single(
            completion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "Value",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Value", item.GetProperty("label").GetString());
        Assert.EndsWith(
            " [#If]",
            item.GetProperty("detail").GetString(),
            StringComparison.Ordinal);

        var definition = await SendDefinitionRequestAsync(
            process,
            3,
            uri,
            text,
            "Debug.Print Value",
            "Debug.Print ".Length);
        Assert.Equal(
            [3, 6],
            definition
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        var hover = await SendPositionRequestAsync(
            process,
            4,
            "textDocument/hover",
            uri,
            text,
            "Debug.Print Value",
            "Debug.Print ".Length);
        var hoverValue = hover
            .GetProperty("result")
            .GetProperty("contents")
            .GetProperty("value")
            .GetString();
        Assert.Contains(
            "Property Value() As Long [#If]",
            hoverValue,
            StringComparison.Ordinal);
        Assert.Contains(
            "Property value() As Long [#If]",
            hoverValue,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "assigned As Long",
            hoverValue,
            StringComparison.Ordinal);

        await process.ShutdownAsync(5);
    }

    [Fact]
    public async Task Server_preserves_a_guarded_setter_family_when_the_complementary_getter_is_unconditional()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/MixedPropertySetterProvenance.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"MixedPropertySetterProvenance\"",
            "Public Property Get value() As Long",
            "End Property",
            "#If FIRST_WRITE_CONFIGURATION Then",
            "Public Property Let Value(ByVal firstAssigned As Long)",
            "End Property",
            "#Else",
            "Public Property Let VALUE(ByVal secondAssigned As Long)",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    val = 1",
            "    Value = 1",
            "    Debug.Print value",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new
                {
                    line = 12,
                    character = "    val".Length
                }
            });
        var item = Assert.Single(
            completion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "Value",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Value", item.GetProperty("label").GetString());
        Assert.EndsWith(
            " [#If]",
            item.GetProperty("detail").GetString(),
            StringComparison.Ordinal);

        var definition = await SendDefinitionRequestAsync(
            process,
            3,
            uri,
            text,
            "    Value = 1",
            "    ".Length);
        Assert.Equal(
            [5, 8],
            definition
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        var hover = await SendPositionRequestAsync(
            process,
            4,
            "textDocument/hover",
            uri,
            text,
            "    Value = 1",
            "    ".Length);
        var hoverValue = hover
            .GetProperty("result")
            .GetProperty("contents")
            .GetProperty("value")
            .GetString();
        Assert.Contains(
            "Property Value(firstAssigned As Long) [#If]",
            hoverValue,
            StringComparison.Ordinal);
        Assert.Contains(
            "Property VALUE(secondAssigned As Long) [#If]",
            hoverValue,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Property value() As Long",
            hoverValue,
            StringComparison.Ordinal);

        var readDefinition = await SendDefinitionRequestAsync(
            process,
            5,
            uri,
            text,
            "Debug.Print value",
            "Debug.Print ".Length);
        Assert.Equal(JsonValueKind.Object, readDefinition.ValueKind);
        Assert.Equal(
            2,
            readDefinition
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());

        await process.ShutdownAsync(6);
    }

    [Fact]
    public async Task Server_completes_the_ordinary_setter_for_a_mixed_provenance_member_write()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string workerUri = "file:///C:/work/MixedMemberWriteWorker.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"MixedMemberWriteWorker\"",
                "#If FIRST_READ_CONFIGURATION Then",
                "Public Property Get Value() As Long",
                "End Property",
                "#Else",
                "Public Property Get value() As Long",
                "End Property",
                "#End If",
                "Public Property Let VALUE(ByVal assigned As Long)",
                "End Property"
            ])));
        const string callerUri = "file:///C:/work/MixedMemberWriteCaller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"MixedMemberWriteCaller\"",
            "Private worker As MixedMemberWriteWorker",
            "Public Sub Run()",
            "    worker. = 1",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = callerUri },
                position = new
                {
                    line = 3,
                    character = "    worker.".Length
                }
            });
        var item = Assert.Single(
            completion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "VALUE",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal("VALUE", item.GetProperty("label").GetString());
        Assert.StartsWith(
            "Property VALUE(assigned)",
            item.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[#If]",
            item.GetProperty("detail").GetString(),
            StringComparison.Ordinal);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_completes_the_ordinary_let_accessor_for_an_unqualified_mixed_property_write()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/MixedUnqualifiedPropertyWrite.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"MixedUnqualifiedPropertyWrite\"",
            "Public Property Get Value() As Variant",
            "End Property",
            "Public Property Let value(ByVal valueAssigned As Variant)",
            "End Property",
            "#If SET_CONFIGURATION Then",
            "Public Property Set VALUE(ByVal objectAssigned As Object)",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    val = 1",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new { line = 11, character = "    val".Length }
            });
        var item = Assert.Single(
            completion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "value",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal("value", item.GetProperty("label").GetString());
        Assert.StartsWith(
            "Property value(valueAssigned)",
            item.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[#If]",
            item.GetProperty("detail").GetString(),
            StringComparison.Ordinal);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_completes_the_ordinary_set_accessor_for_an_unqualified_mixed_property_write()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/MixedUnqualifiedPropertySet.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"MixedUnqualifiedPropertySet\"",
            "Public Property Get Value() As Object",
            "End Property",
            "Public Property Set value(ByVal objectAssigned As Object)",
            "End Property",
            "#If LET_CONFIGURATION Then",
            "Public Property Let VALUE(ByVal scalarAssigned As Variant)",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    Set val = Nothing",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new { line = 11, character = "    Set val".Length }
            });
        var item = Assert.Single(
            completion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "value",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal("value", item.GetProperty("label").GetString());
        Assert.StartsWith(
            "Property value(objectAssigned)",
            item.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[#If]",
            item.GetProperty("detail").GetString(),
            StringComparison.Ordinal);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_navigates_the_guarded_setter_family_for_a_member_write()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string workerUri = "file:///C:/work/MixedMemberSetterWorker.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"MixedMemberSetterWorker\"",
                "Public Property Get value() As Long",
                "End Property",
                "#If FIRST_WRITE_CONFIGURATION Then",
                "Public Property Let Value(ByVal firstAssigned As Long)",
                "End Property",
                "#Else",
                "Public Property Let VALUE(ByVal secondAssigned As Long)",
                "End Property",
                "#End If"
            ])));
        const string callerUri = "file:///C:/work/MixedMemberSetterCaller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"MixedMemberSetterCaller\"",
            "Private worker As MixedMemberSetterWorker",
            "Public Sub Run()",
            "    worker.Value = 1",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var definition = await SendDefinitionRequestAsync(
            process,
            2,
            callerUri,
            callerText,
            "worker.Value = 1",
            "worker.".Length);
        Assert.Equal(
            [5, 8],
            definition
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        var hover = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/hover",
            callerUri,
            callerText,
            "worker.Value = 1",
            "worker.".Length);
        var hoverValue = hover
            .GetProperty("result")
            .GetProperty("contents")
            .GetProperty("value")
            .GetString();
        Assert.Contains("Property Value(firstAssigned As Long) [#If]", hoverValue);
        Assert.Contains("Property VALUE(secondAssigned As Long) [#If]", hoverValue);
        Assert.DoesNotContain("Property value() As Long", hoverValue);

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_keeps_a_guarded_getter_result_assignment_on_the_getter_family()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/MixedGetterResultAssignment.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"MixedGetterResultAssignment\"",
            "#If FIRST_READ_CONFIGURATION Then",
            "Public Property Get Value() As Long",
            "    Value = 1",
            "End Property",
            "#Else",
            "Public Property Get value() As Long",
            "    value = 2",
            "End Property",
            "#End If",
            "Public Property Let VALUE(ByVal assigned As Long)",
            "End Property"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var definition = await SendDefinitionRequestAsync(
            process,
            2,
            uri,
            text,
            "    Value = 1",
            "    ".Length);
        Assert.Equal(
            [3, 7],
            definition
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        var hover = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/hover",
            uri,
            text,
            "    Value = 1",
            "    ".Length);
        var hoverValue = hover
            .GetProperty("result")
            .GetProperty("contents")
            .GetProperty("value")
            .GetString();
        Assert.Contains(
            "Property Value() As Long [#If]",
            hoverValue,
            StringComparison.Ordinal);
        Assert.Contains(
            "Property value() As Long [#If]",
            hoverValue,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "assigned As Long",
            hoverValue,
            StringComparison.Ordinal);

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_does_not_treat_a_same_name_leading_dot_write_as_the_getter_result()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string workerUri = "file:///C:/work/LeadingDotPropertyWorker.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"LeadingDotPropertyWorker\"",
                "Public Property Get Value() As Long",
                "End Property",
                "#If FIRST_WRITE_CONFIGURATION Then",
                "Public Property Let value(ByVal firstAssigned As Long)",
                "End Property",
                "#Else",
                "Public Property Let VALUE(ByVal secondAssigned As Long)",
                "End Property",
                "#End If"
            ])));
        const string hostUri = "file:///C:/work/LeadingDotPropertyHost.cls";
        var hostText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"LeadingDotPropertyHost\"",
            "Private worker As LeadingDotPropertyWorker",
            "Public Property Get Value() As Long",
            "    With worker",
            "        .val = 1",
            "        .Value = 1",
            "    End With",
            "    Value = 1",
            "End Property"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(hostUri, hostText));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = hostUri },
                position = new
                {
                    line = 5,
                    character = "        .val".Length
                }
            });
        var item = Assert.Single(
            completion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "value",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal("value", item.GetProperty("label").GetString());
        Assert.Contains("firstAssigned", item.GetProperty("detail").GetString());
        Assert.EndsWith(" [#If]", item.GetProperty("detail").GetString());

        var memberDefinition = await SendDefinitionRequestAsync(
            process,
            3,
            hostUri,
            hostText,
            ".Value = 1",
            ".".Length);
        Assert.Equal(
            [5, 8],
            memberDefinition
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        var resultDefinition = await SendDefinitionRequestAsync(
            process,
            4,
            hostUri,
            hostText,
            "    Value = 1",
            "    ".Length);
        Assert.Equal(JsonValueKind.Object, resultDefinition.ValueKind);
        Assert.Equal(
            3,
            resultDefinition
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());

        await process.ShutdownAsync(5);
    }

    [Fact]
    public async Task Server_navigates_every_conditional_property_accessor_variant()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalPropertyFamily.cls";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalPropertyFamily\"",
            "#If FIRST_GET Then",
            "Public Property Get Value() As Long",
            "End Property",
            "#Else",
            "Public Property Get value() As Long",
            "End Property",
            "#End If",
            "#If FIRST_LET Then",
            "Public Property Let VALUE(ByVal RHS As Long)",
            "End Property",
            "#Else",
            "Public Property Let Value(ByVal RHS As Long)",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    Debug.Print Value",
            "    Value = 1",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var definition = await SendDefinitionRequestAsync(
            process,
            2,
            uri,
            text,
            "Debug.Print Value",
            "Debug.Print ".Length);
        Assert.Equal(
            [2, 5, 9, 12],
            definition
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        var references = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/references",
            uri,
            text,
            "Debug.Print Value",
            "Debug.Print ".Length);
        Assert.Equal(
            [2, 5, 9, 12, 16, 17],
            references
                .GetProperty("result")
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_keeps_guarded_get_let_and_set_as_one_property_for_both_write_forms()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalPropertyWriteForms.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalPropertyWriteForms\"",
            "#If READ_CONFIGURATION Then",
            "Public Property Get Value() As Variant",
            "End Property",
            "#End If",
            "#If VALUE_WRITE_CONFIGURATION Then",
            "Public Property Let value(ByVal valueAssigned As Variant)",
            "End Property",
            "#End If",
            "#If OBJECT_WRITE_CONFIGURATION Then",
            "Public Property Set VALUE(ByVal objectAssigned As Object)",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    val = 1",
            "    Set val = Nothing",
            "    Value = 1",
            "    Set Value = Nothing",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var letCompletion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new
                {
                    line = 15,
                    character = "    val".Length
                }
            });
        var letItem = Assert.Single(
            letCompletion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "Value",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Value", letItem.GetProperty("label").GetString());
        Assert.Contains("valueAssigned", letItem.GetProperty("detail").GetString());
        Assert.EndsWith(" [#If]", letItem.GetProperty("detail").GetString());

        var setCompletion = await process.SendRequestAsync(
            3,
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new
                {
                    line = 16,
                    character = "    Set val".Length
                }
            });
        var setItem = Assert.Single(
            setCompletion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "Value",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Value", setItem.GetProperty("label").GetString());
        Assert.Contains("objectAssigned", setItem.GetProperty("detail").GetString());
        Assert.EndsWith(" [#If]", setItem.GetProperty("detail").GetString());

        var letDefinition = await SendDefinitionRequestAsync(
            process,
            4,
            uri,
            text,
            "    Value = 1",
            "    ".Length);
        Assert.Equal(
            [3, 7, 11],
            letDefinition
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        var setDefinition = await SendDefinitionRequestAsync(
            process,
            5,
            uri,
            text,
            "Set Value = Nothing",
            "Set ".Length);
        Assert.Equal(
            [3, 7, 11],
            setDefinition
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        var hover = await SendPositionRequestAsync(
            process,
            6,
            "textDocument/hover",
            uri,
            text,
            "    Value = 1",
            "    ".Length);
        var hoverValue = hover
            .GetProperty("result")
            .GetProperty("contents")
            .GetProperty("value")
            .GetString();
        Assert.Contains("Property Value() As Variant [#If]", hoverValue);
        Assert.Contains("Property value(valueAssigned As Variant) [#If]", hoverValue);
        Assert.Contains("Property VALUE(objectAssigned As Object) [#If]", hoverValue);

        await process.ShutdownAsync(7);
    }

    [Fact]
    public async Task Server_selects_the_guarded_let_or_set_family_for_each_mixed_provenance_write_form()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/MixedPropertyWriteForms.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"MixedPropertyWriteForms\"",
            "Public Property Get Value() As Variant",
            "End Property",
            "#If FIRST_VALUE_WRITE_CONFIGURATION Then",
            "Public Property Let value(ByVal valueAssigned As Variant)",
            "End Property",
            "#Else",
            "Public Property Let VALUE(ByVal valueAssigned As Variant)",
            "End Property",
            "#End If",
            "#If FIRST_OBJECT_WRITE_CONFIGURATION Then",
            "Public Property Set VALUE(ByVal objectAssigned As Object)",
            "End Property",
            "#Else",
            "Public Property Set Value(ByVal objectAssigned As Object)",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    val = 1",
            "    Set val = Nothing",
            "    Value = 1",
            "    Set Value = Nothing",
            "100 Set val = Nothing",
            "110 Set Value = Nothing",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var letCompletion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new
                {
                    line = 19,
                    character = "    val".Length
                }
            });
        var letItem = Assert.Single(
            letCompletion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "value",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal("value", letItem.GetProperty("label").GetString());
        Assert.Contains("valueAssigned", letItem.GetProperty("detail").GetString());
        Assert.EndsWith(" [#If]", letItem.GetProperty("detail").GetString());

        var setCompletion = await process.SendRequestAsync(
            3,
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new
                {
                    line = 20,
                    character = "    Set val".Length
                }
            });
        var setItem = Assert.Single(
            setCompletion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "VALUE",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal("VALUE", setItem.GetProperty("label").GetString());
        Assert.Contains("objectAssigned", setItem.GetProperty("detail").GetString());
        Assert.EndsWith(" [#If]", setItem.GetProperty("detail").GetString());

        var letDefinition = await SendDefinitionRequestAsync(
            process,
            4,
            uri,
            text,
            "    Value = 1",
            "    ".Length);
        Assert.Equal(
            [5, 8],
            letDefinition
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        var setDefinition = await SendDefinitionRequestAsync(
            process,
            5,
            uri,
            text,
            "Set Value = Nothing",
            "Set ".Length);
        Assert.Equal(
            [12, 15],
            setDefinition
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        var numericLabelSetCompletion = await process.SendRequestAsync(
            6,
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new
                {
                    line = 23,
                    character = "100 Set val".Length
                }
            });
        var numericLabelSetItem = Assert.Single(
            numericLabelSetCompletion.GetProperty("result").EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("label").GetString(),
                "VALUE",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            "VALUE",
            numericLabelSetItem.GetProperty("label").GetString());
        Assert.Contains(
            "objectAssigned",
            numericLabelSetItem.GetProperty("detail").GetString());
        Assert.EndsWith(
            " [#If]",
            numericLabelSetItem.GetProperty("detail").GetString());

        var numericLabelSetDefinition = await SendDefinitionRequestAsync(
            process,
            7,
            uri,
            text,
            "110 Set Value = Nothing",
            "110 Set ".Length);
        Assert.Equal(
            [12, 15],
            numericLabelSetDefinition
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        var numericLabelSetHover = await SendPositionRequestAsync(
            process,
            8,
            "textDocument/hover",
            uri,
            text,
            "110 Set Value = Nothing",
            "110 Set ".Length);
        var numericLabelSetHoverValue = numericLabelSetHover
            .GetProperty("result")
            .GetProperty("contents")
            .GetProperty("value")
            .GetString();
        Assert.Contains(
            "Property VALUE(objectAssigned As Object) [#If]",
            numericLabelSetHoverValue,
            StringComparison.Ordinal);
        Assert.Contains(
            "Property Value(objectAssigned As Object) [#If]",
            numericLabelSetHoverValue,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "valueAssigned",
            numericLabelSetHoverValue,
            StringComparison.Ordinal);

        await process.ShutdownAsync(9);
    }

    [Fact]
    public async Task Server_diagnoses_repeated_property_accessors_in_one_guarded_branch()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/RepeatedConditionalProperty.cls";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"RepeatedConditionalProperty\"",
            "#If VBA7 Then",
            "Public Property Get Value() As Long",
            "End Property",
            "Public Property Get value() As Long",
            "End Property",
            "#End If"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var duplicateLines = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .Where(diagnostic => diagnostic.GetProperty("code").GetString()
                == "validation.duplicateDeclaration")
            .Select(diagnostic => diagnostic
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32())
            .ToArray();
        Assert.Equal([2, 4], duplicateLines);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_diagnoses_a_function_and_property_collision_across_conditionals()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalCallableCollision.cls";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalCallableCollision\"",
            "Public Function Value() As Long",
            "End Function",
            "#If PROPERTY_CONFIGURATION Then",
            "Public Property Get value() As Long",
            "End Property",
            "#End If"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var duplicateLines = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .Where(diagnostic => diagnostic.GetProperty("code").GetString()
                == "validation.duplicateDeclaration")
            .Select(diagnostic => diagnostic
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32())
            .ToArray();
        Assert.Equal([1, 4], duplicateLines);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_diagnoses_an_enum_member_and_module_value_collision()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalEnumMemberCollision.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalEnumMemberCollision\"",
            "Public Enum RunState",
            "    Ready = 1",
            "End Enum",
            "#If LEGACY_CONFIGURATION Then",
            "Private Const ready As Long = 2",
            "#End If"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var duplicateLines = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .Where(diagnostic => diagnostic.GetProperty("code").GetString()
                == "validation.duplicateDeclaration")
            .Select(diagnostic => diagnostic
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32())
            .ToArray();
        Assert.Equal([2, 5], duplicateLines);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_forms_a_project_type_family_across_source_modules()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string firstUri = "file:///C:/work/ConditionalTypesA.bas";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(firstUri, string.Join('\n', [
                "Attribute VB_Name = \"ConditionalTypesA\"",
                "#If FIRST_CONFIGURATION Then",
                "Public Enum RunMode",
                "    FirstMode = 1",
                "End Enum",
                "#End If"
            ])));
        const string secondUri = "file:///C:/work/ConditionalTypesB.bas";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(secondUri, string.Join('\n', [
                "Attribute VB_Name = \"ConditionalTypesB\"",
                "#If SECOND_CONFIGURATION Then",
                "Public Enum runmode",
                "    SecondMode = 2",
                "End Enum",
                "#End If"
            ])));
        const string callerUri = "file:///C:/work/ConditionalTypeCaller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalTypeCaller\"",
            "Private currentMode As RunMode"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var definition = await SendDefinitionRequestAsync(
            process,
            2,
            callerUri,
            callerText,
            "RunMode");
        Assert.Equal(JsonValueKind.Array, definition.ValueKind);
        Assert.Equal(
            [firstUri, secondUri],
            definition
                .EnumerateArray()
                .Select(location => location.GetProperty("uri").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_forms_one_type_family_across_conditional_visibility_variants()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalTypeVisibility.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalTypeVisibility\"",
            "#If PUBLIC_CONFIGURATION Then",
            "Public Type Payload",
            "    PublicValue As Long",
            "End Type",
            "#Else",
            "Private Type payload",
            "    PrivateValue As Long",
            "End Type",
            "#End If",
            "Private current As Payload"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var definition = await SendDefinitionRequestAsync(
            process,
            2,
            uri,
            text,
            "Payload",
            text.LastIndexOf("Payload", StringComparison.Ordinal)
                - text.IndexOf("Payload", StringComparison.Ordinal));
        Assert.Equal(JsonValueKind.Array, definition.ValueKind);
        Assert.Equal(
            [2, 6],
            definition
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_rebuilds_a_conditional_family_after_a_directive_only_edit()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/IncrementalConditionalFamily.bas";
        var conditionalText = string.Join('\n', [
            "Attribute VB_Name = \"IncrementalConditionalFamily\"",
            "Option Explicit",
            "Public Sub Run()",
            "#If VBA7 Then",
            "    Dim BuildValue As String",
            "#Else",
            "    Dim buildvalue As String",
            "#End If",
            "    Debug.Print BuildValue",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, conditionalText));
        await process.WaitForDiagnosticsAsync(uri);

        var conditionalDefinition = await SendDefinitionRequestAsync(
            process,
            2,
            uri,
            conditionalText,
            "BuildValue",
            conditionalText.LastIndexOf("BuildValue", StringComparison.Ordinal)
                - conditionalText.IndexOf("BuildValue", StringComparison.Ordinal));
        Assert.Equal(2, conditionalDefinition.GetArrayLength());

        var unconditionalText = string.Join('\n', [
            "Attribute VB_Name = \"IncrementalConditionalFamily\"",
            "Option Explicit",
            "Public Sub Run()",
            "",
            "    Dim BuildValue As String",
            "",
            "    Dim buildvalue As String",
            "",
            "    Debug.Print BuildValue",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didChange",
            new
            {
                textDocument = new { uri, version = 2 },
                contentChanges = new[]
                {
                    new { text = unconditionalText }
                }
            });
        var diagnostics = await process.WaitForDiagnosticsAsync(uri);
        Assert.Equal(
            [4, 6],
            diagnostics
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray()
                .Where(diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.duplicateDeclaration")
                .Select(diagnostic => diagnostic
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        var unconditionalDefinition = await SendDefinitionRequestAsync(
            process,
            3,
            uri,
            unconditionalText,
            "BuildValue",
            unconditionalText.LastIndexOf("BuildValue", StringComparison.Ordinal)
                - unconditionalText.IndexOf("BuildValue", StringComparison.Ordinal));
        Assert.Equal(JsonValueKind.Null, unconditionalDefinition.ValueKind);

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_does_not_invent_a_family_or_collision_from_an_unterminated_conditional()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/UnterminatedConditionalFamily.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"UnterminatedConditionalFamily\"",
            "Public Sub Run()",
            "    Debug.Print BuildValue()",
            "End Sub",
            "#If VBA7 Then",
            "Public Function BuildValue() As String",
            "End Function",
            "#Else",
            "Public Function buildvalue() As String",
            "End Function"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var diagnostics = await process.WaitForDiagnosticsAsync(uri);
        var publishedDiagnostics = diagnostics
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();
        Assert.Contains(
            publishedDiagnostics,
            diagnostic => diagnostic.GetProperty("code").GetString()
                == "syntax.malformedPreprocessorNesting");
        Assert.DoesNotContain(
            publishedDiagnostics,
            diagnostic => diagnostic.GetProperty("code").GetString()
                == "validation.duplicateDeclaration");

        var definition = await SendDefinitionRequestAsync(
            process,
            2,
            uri,
            text,
            "BuildValue()");
        Assert.Equal(JsonValueKind.Null, definition.ValueKind);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_republishes_project_type_collision_diagnostics_for_both_modules()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string firstUri = "file:///C:/work/ProjectTypeA.bas";
        var firstText = string.Join('\n', [
            "Attribute VB_Name = \"ProjectTypeA\"",
            "Public Enum RunMode",
            "    FirstMode = 1",
            "End Enum"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(firstUri, firstText, version: 7));

        var initialFirst = await process.WaitForDiagnosticsAsync(firstUri);
        var initialFirstParameters = initialFirst.GetProperty("params");
        Assert.Equal(7, initialFirstParameters.GetProperty("version").GetInt32());
        Assert.DoesNotContain(
            initialFirstParameters.GetProperty("diagnostics").EnumerateArray(),
            diagnostic => diagnostic.GetProperty("code").GetString()
                == "validation.duplicateDeclaration");

        const string secondUri = "file:///C:/work/ProjectTypeB.bas";
        var secondText = string.Join('\n', [
            "Attribute VB_Name = \"ProjectTypeB\"",
            "#If SECOND_CONFIGURATION Then",
            "Public Enum runmode",
            "    SecondMode = 2",
            "End Enum",
            "#End If"
        ]);
        var firstRepublishedTask = process.WaitForDiagnosticsAsync(firstUri);
        var secondPublishedTask = process.WaitForDiagnosticsAsync(secondUri);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(secondUri, secondText, version: 11));

        await Task.WhenAll(firstRepublishedTask, secondPublishedTask);
        AssertProjectDuplicate(
            await firstRepublishedTask,
            firstUri,
            expectedVersion: 7,
            expectedLine: 1);
        AssertProjectDuplicate(
            await secondPublishedTask,
            secondUri,
            expectedVersion: 11,
            expectedLine: 2);

        await process.ShutdownAsync(2);

        static void AssertProjectDuplicate(
            JsonElement notification,
            string expectedUri,
            int expectedVersion,
            int expectedLine)
        {
            var parameters = notification.GetProperty("params");
            Assert.Equal(expectedUri, parameters.GetProperty("uri").GetString());
            Assert.Equal(expectedVersion, parameters.GetProperty("version").GetInt32());
            var duplicate = Assert.Single(
                parameters
                    .GetProperty("diagnostics")
                    .EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.duplicateDeclaration");
            var range = duplicate.GetProperty("range");
            Assert.Equal(
                expectedLine,
                range.GetProperty("start").GetProperty("line").GetInt32());
            Assert.Equal(
                "Public Enum ".Length,
                range.GetProperty("start").GetProperty("character").GetInt32());
            Assert.Equal(
                "Public Enum ".Length + "RunMode".Length,
                range.GetProperty("end").GetProperty("character").GetInt32());
        }
    }

    [Fact]
    public async Task Server_clears_a_project_collision_from_its_peer_when_a_source_closes()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string firstUri = "file:///C:/work/ClosingTypeA.bas";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(firstUri, string.Join('\n', [
                "Attribute VB_Name = \"ClosingTypeA\"",
                "Public Enum RunMode",
                "    FirstMode = 1",
                "End Enum"
            ]), version: 7));
        await process.WaitForDiagnosticsAsync(firstUri);

        const string secondUri = "file:///C:/work/ClosingTypeB.bas";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(secondUri, string.Join('\n', [
                "Attribute VB_Name = \"ClosingTypeB\"",
                "#If SECOND_CONFIGURATION Then",
                "Public Enum runmode",
                "    SecondMode = 2",
                "End Enum",
                "#End If"
            ]), version: 11));
        var firstCollision = await process.WaitForDiagnosticsAsync(firstUri);
        await process.WaitForDiagnosticsAsync(secondUri);
        Assert.Contains(
            firstCollision
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            diagnostic => diagnostic.GetProperty("code").GetString()
                == "validation.duplicateDeclaration");

        var firstClearedTask = process.WaitForDiagnosticsAsync(firstUri);
        await process.SendNotificationAsync(
            "textDocument/didClose",
            new
            {
                textDocument = new { uri = secondUri }
            });

        var firstCleared = await firstClearedTask;
        var parameters = firstCleared.GetProperty("params");
        Assert.Equal(7, parameters.GetProperty("version").GetInt32());
        Assert.DoesNotContain(
            parameters.GetProperty("diagnostics").EnumerateArray(),
            diagnostic => diagnostic.GetProperty("code").GetString()
                == "validation.duplicateDeclaration");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_clears_a_project_collision_from_its_peer_when_a_watched_source_is_deleted()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-conditional-delete-").FullName;
        try
        {
            var sourceRoot = Directory.CreateDirectory(Path.Combine(
                projectRoot,
                "src",
                "Book1")).FullName;
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                CreateSingleDocumentManifestText("src/Book1"));
            var firstPath = Path.Combine(sourceRoot, "ProjectTypeA.bas");
            var firstText = string.Join('\n', [
                "Attribute VB_Name = \"ProjectTypeA\"",
                "Public Enum RunMode",
                "    FirstMode = 1",
                "End Enum"
            ]);
            File.WriteAllText(firstPath, firstText);
            var secondPath = Path.Combine(sourceRoot, "ProjectTypeB.bas");
            File.WriteAllText(secondPath, string.Join('\n', [
                "Attribute VB_Name = \"ProjectTypeB\"",
                "#If SECOND_CONFIGURATION Then",
                "Public Enum runmode",
                "    SecondMode = 2",
                "End Enum",
                "#End If"
            ]));
            var firstUri = ToFileUri(firstPath);
            var secondUri = ToFileUri(secondPath);

            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();
            var initialFirstTask = process.WaitForDiagnosticsAsync(firstUri);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(firstUri, firstText, version: 7));
            Assert.Contains(
                (await initialFirstTask)
                    .GetProperty("params")
                    .GetProperty("diagnostics")
                    .EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.duplicateDeclaration");

            var clearedFirstTask = process.WaitForDiagnosticsAsync(firstUri);
            var clearedSecondTask = process.WaitForDiagnosticsAsync(secondUri);
            File.Delete(secondPath);
            await SendWatchedFileChangeAsync(process, secondUri, type: 3);
            await Task.WhenAll(clearedFirstTask, clearedSecondTask);

            var firstParameters = (await clearedFirstTask).GetProperty("params");
            Assert.Equal(7, firstParameters.GetProperty("version").GetInt32());
            Assert.DoesNotContain(
                firstParameters.GetProperty("diagnostics").EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.duplicateDeclaration");
            var secondParameters = (await clearedSecondTask).GetProperty("params");
            Assert.False(secondParameters.TryGetProperty("version", out _));
            Assert.Empty(secondParameters.GetProperty("diagnostics").EnumerateArray());

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_refreshes_project_diagnostics_when_a_manifest_splits_the_project_boundary()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-conditional-manifest-boundary-").FullName;
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                CreateSingleDocumentManifestText("src"));
            var firstRoot = Directory.CreateDirectory(Path.Combine(
                projectRoot,
                "src",
                "Book1")).FullName;
            var secondRoot = Directory.CreateDirectory(Path.Combine(
                projectRoot,
                "src",
                "SecondBook")).FullName;
            var firstPath = Path.Combine(firstRoot, "ProjectTypeA.bas");
            var firstText = string.Join('\n', [
                "Attribute VB_Name = \"ProjectTypeA\"",
                "Public Enum RunMode",
                "    FirstMode = 1",
                "End Enum"
            ]);
            File.WriteAllText(firstPath, firstText);
            var secondPath = Path.Combine(secondRoot, "ProjectTypeB.bas");
            var secondText = string.Join('\n', [
                "Attribute VB_Name = \"ProjectTypeB\"",
                "#If SECOND_CONFIGURATION Then",
                "Public Enum runmode",
                "    SecondMode = 2",
                "End Enum",
                "#End If"
            ]);
            File.WriteAllText(secondPath, secondText);
            var firstUri = ToFileUri(firstPath);
            var secondUri = ToFileUri(secondPath);
            var manifestUri = ToFileUri(Path.Combine(
                projectRoot,
                "vba-project.json"));

            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(firstUri, firstText, version: 7));
            await process.WaitForDiagnosticsAsync(firstUri);
            var collidingFirstTask = process.WaitForDiagnosticsAsync(firstUri);
            var collidingSecondTask = process.WaitForDiagnosticsAsync(secondUri);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(secondUri, secondText, version: 11));
            await Task.WhenAll(collidingFirstTask, collidingSecondTask);
            Assert.All(
                new[] { await collidingFirstTask, await collidingSecondTask },
                notification => Assert.Contains(
                    notification
                        .GetProperty("params")
                        .GetProperty("diagnostics")
                        .EnumerateArray(),
                    diagnostic => diagnostic.GetProperty("code").GetString()
                        == "validation.duplicateDeclaration"));

            var clearedFirstTask = process.WaitForDiagnosticsAsync(firstUri);
            var clearedSecondTask = process.WaitForDiagnosticsAsync(secondUri);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(
                    manifestUri,
                    ProjectManifestFixtureText("multi-document.json"),
                    version: 20));
            await Task.WhenAll(clearedFirstTask, clearedSecondTask);

            AssertProjectCollisionCleared(await clearedFirstTask, 7);
            AssertProjectCollisionCleared(await clearedSecondTask, 11);

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }

        static void AssertProjectCollisionCleared(
            JsonElement notification,
            int expectedVersion)
        {
            var parameters = notification.GetProperty("params");
            Assert.Equal(expectedVersion, parameters.GetProperty("version").GetInt32());
            Assert.DoesNotContain(
                parameters.GetProperty("diagnostics").EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.duplicateDeclaration");
        }
    }

    [Fact]
    public async Task Server_refreshes_disk_only_project_diagnostics_when_a_manifest_splits_the_project_boundary()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-conditional-disk-manifest-boundary-").FullName;
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                CreateSingleDocumentManifestText("src"));
            var firstRoot = Directory.CreateDirectory(Path.Combine(
                projectRoot,
                "src",
                "Book1")).FullName;
            var secondRoot = Directory.CreateDirectory(Path.Combine(
                projectRoot,
                "src",
                "SecondBook")).FullName;
            var firstPath = Path.Combine(firstRoot, "ProjectTypeA.bas");
            var firstText = string.Join('\n', [
                "Attribute VB_Name = \"ProjectTypeA\"",
                "Public Enum RunMode",
                "    FirstMode = 1",
                "End Enum"
            ]);
            File.WriteAllText(firstPath, firstText);
            var secondPath = Path.Combine(secondRoot, "ProjectTypeB.bas");
            var secondText = string.Join('\n', [
                "Attribute VB_Name = \"ProjectTypeB\"",
                "#If SECOND_CONFIGURATION Then",
                "Public Enum runmode",
                "    SecondMode = 2",
                "End Enum",
                "#End If"
            ]);
            File.WriteAllText(secondPath, secondText);
            var firstUri = ToFileUri(firstPath);
            var secondUri = ToFileUri(secondPath);
            var manifestUri = ToFileUri(Path.Combine(
                projectRoot,
                "vba-project.json"));

            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();
            var initialFirstTask = process.WaitForDiagnosticsAsync(firstUri);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(firstUri, firstText, version: 7));
            await initialFirstTask;
            var trackedFirstTask = process.WaitForDiagnosticsAsync(firstUri);
            var trackedSecondTask = process.WaitForDiagnosticsAsync(secondUri);
            await SendWatchedFileChangeAsync(process, secondUri, type: 2);
            await Task.WhenAll(trackedFirstTask, trackedSecondTask);
            Assert.All(
                new[] { await trackedFirstTask, await trackedSecondTask },
                notification => Assert.Contains(
                    notification
                        .GetProperty("params")
                        .GetProperty("diagnostics")
                        .EnumerateArray(),
                    diagnostic => diagnostic.GetProperty("code").GetString()
                        == "validation.duplicateDeclaration"));
            Assert.False(
                (await trackedSecondTask)
                    .GetProperty("params")
                    .TryGetProperty("version", out _));

            var clearedFirstTask = process.WaitForDiagnosticsAsync(firstUri);
            var clearedSecondTask = process.WaitForDiagnosticsAsync(secondUri);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(
                    manifestUri,
                    ProjectManifestFixtureText("multi-document.json"),
                    version: 20));
            await Task.WhenAll(clearedFirstTask, clearedSecondTask);

            AssertProjectCollisionCleared(await clearedFirstTask, expectedVersion: 7);
            AssertProjectCollisionCleared(await clearedSecondTask, expectedVersion: null);

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }

        static void AssertProjectCollisionCleared(
            JsonElement notification,
            int? expectedVersion)
        {
            var parameters = notification.GetProperty("params");
            if (expectedVersion is int version)
            {
                Assert.Equal(version, parameters.GetProperty("version").GetInt32());
            }
            else
            {
                Assert.False(parameters.TryGetProperty("version", out _));
            }

            Assert.DoesNotContain(
                parameters.GetProperty("diagnostics").EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.duplicateDeclaration");
        }
    }

    [Fact]
    public async Task Server_keeps_conditional_families_inside_manifest_project_boundaries()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-conditional-boundary-").FullName;
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                ProjectManifestFixtureText("multi-document.json"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "SecondBook"));

            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();

            var firstHelperUri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Helper.bas"));
            var firstCallerUri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Caller.bas"));
            var secondHelperUri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "SecondBook",
                "Helper.bas"));
            var secondCallerUri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "SecondBook",
                "Caller.bas"));
            var firstHelperText = string.Join('\n', [
                "Attribute VB_Name = \"FirstHelper\"",
                "#If FIRST_CONFIGURATION Then",
                "Public Function BuildValue() As String",
                "End Function",
                "#End If"
            ]);
            var secondHelperText = string.Join('\n', [
                "Attribute VB_Name = \"SecondHelper\"",
                "#If SECOND_CONFIGURATION Then",
                "Public Function buildvalue() As Long",
                "End Function",
                "#End If"
            ]);
            var firstCallerText = string.Join('\n', [
                "Attribute VB_Name = \"FirstCaller\"",
                "Public Sub Run()",
                "    Debug.Print BuildValue()",
                "End Sub"
            ]);
            var secondCallerText = string.Join('\n', [
                "Attribute VB_Name = \"SecondCaller\"",
                "Public Sub Run()",
                "    Debug.Print buildvalue()",
                "End Sub"
            ]);

            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(firstHelperUri, firstHelperText, version: 7));
            var firstDiagnostics = await process.WaitForDiagnosticsAsync(firstHelperUri);
            Assert.DoesNotContain(
                firstDiagnostics
                    .GetProperty("params")
                    .GetProperty("diagnostics")
                    .EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.duplicateDeclaration");
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(secondHelperUri, secondHelperText, version: 11));
            var secondDiagnostics = await process.WaitForDiagnosticsAsync(secondHelperUri);
            Assert.DoesNotContain(
                secondDiagnostics
                    .GetProperty("params")
                    .GetProperty("diagnostics")
                    .EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.duplicateDeclaration");
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(firstCallerUri, firstCallerText));
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(secondCallerUri, secondCallerText));

            var firstDefinition = await RequestDefinitionAsync(
                process,
                2,
                firstCallerUri,
                firstCallerText,
                "BuildValue");
            Assert.Equal(JsonValueKind.Object, firstDefinition.ValueKind);
            Assert.Equal(
                firstHelperUri,
                firstDefinition.GetProperty("uri").GetString());
            var secondDefinition = await RequestDefinitionAsync(
                process,
                3,
                secondCallerUri,
                secondCallerText,
                "buildvalue");
            Assert.Equal(JsonValueKind.Object, secondDefinition.ValueKind);
            Assert.Equal(
                secondHelperUri,
                secondDefinition.GetProperty("uri").GetString());

            await process.ShutdownAsync(4);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_returns_source_completion_items_and_language_vocabulary()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument("file:///C:/work/Builder.bas", string.Join('\n', [
            "Attribute VB_Name = \"Builder\"",
            "Option Explicit",
            "",
            "Public WsSrv As IWorksheetService",
            "Private HiddenSrv As IWorksheetService",
            "",
            "Public Function BuildValue() As String",
            "End Function",
            "",
            "Public Enum RunMode",
            "    Automatic = 0",
            "End Enum"
        ])));
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"Caller\"",
            "Option Explicit",
            "",
            "Public Sub Run()",
            "    Dim currentValue As String",
            "    ",
            "    currentValue = ",
            "    Dim typed As ",
            "    WsSr",
            "End Sub"
        ]);
        const string callerUri = "file:///C:/work/Caller.bas";
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(callerUri, callerText));

        var completion = await process.SendRequestAsync(2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = callerUri },
                position = new { line = 5, character = 4 }
            });
        var labels = completion
            .GetProperty("result")
            .EnumerateArray()
            .Select(item => item.GetProperty("label").GetString())
            .ToArray();

        Assert.Contains("BuildValue", labels);
        Assert.Contains("currentValue", labels);
        Assert.Contains("If", labels);
        Assert.DoesNotContain("RunMode", labels);
        Assert.DoesNotContain("Automatic", labels);
        Assert.DoesNotContain("String", labels);

        var expressionCompletion = await process.SendRequestAsync(3,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = callerUri },
                position = new { line = 6, character = "    currentValue = ".Length }
            });
        var expressionLabels = expressionCompletion
            .GetProperty("result")
            .EnumerateArray()
            .Select(item => item.GetProperty("label").GetString())
            .ToArray();
        Assert.Contains("BuildValue", expressionLabels);
        Assert.Contains("Automatic", expressionLabels);
        Assert.Contains("currentValue", expressionLabels);
        Assert.DoesNotContain("RunMode", expressionLabels);
        Assert.DoesNotContain("String", expressionLabels);

        var typeCompletion = await process.SendRequestAsync(4,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = callerUri },
                position = new { line = 7, character = "    Dim typed As ".Length }
            });
        var typeLabels = typeCompletion
            .GetProperty("result")
            .EnumerateArray()
            .Select(item => item.GetProperty("label").GetString())
            .ToArray();
        Assert.Contains("RunMode", typeLabels);
        Assert.Contains("String", typeLabels);
        Assert.DoesNotContain("BuildValue", typeLabels);

        var publicModuleVariableCompletion = await process.SendRequestAsync(5,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = callerUri },
                position = new { line = 8, character = "    WsSr".Length }
            });
        var publicModuleVariableLabels = publicModuleVariableCompletion
            .GetProperty("result")
            .EnumerateArray()
            .Select(item => item.GetProperty("label").GetString())
            .ToArray();
        Assert.Contains("WsSrv", publicModuleVariableLabels);
        Assert.DoesNotContain("HiddenSrv", publicModuleVariableLabels);

        var outsideProcedureCompletion = await process.SendRequestAsync(6,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = callerUri },
                position = new { line = 2, character = 0 }
            });
        var outsideLabels = outsideProcedureCompletion
            .GetProperty("result")
            .EnumerateArray()
            .Select(item => item.GetProperty("label").GetString())
            .ToArray();
        Assert.DoesNotContain("currentValue", outsideLabels);

        await process.ShutdownAsync(7);
    }

    [Fact]
    public async Task Server_returns_member_completion_without_language_vocabulary()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument("file:///C:/work/WorksheetRangeBounds.cls", string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"WorksheetRangeBounds\"",
            "Public Property Get Column() As Long",
            "End Property",
            "Public Property Get ColumnCount() As Long",
            "End Property"
        ])));
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument("file:///C:/work/Helper.bas", string.Join('\n', [
            "Attribute VB_Name = \"Helper\"",
            "Public Function BuildValue() As String",
            "End Function"
        ])));
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Public Sub Run()",
            "    Dim bare As ",
            "    Dim typed As WorksheetRan",
            "    Dim range_obj As WorksheetRangeBounds",
            "    range_obj.",
            "    range_obj.Col",
            "    aaaa = range_obj.Column ",
            "    aaaa = range_obj. ",
            "End Sub"
        ]);
        const string workerUri = "file:///C:/work/Worker.bas";
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(workerUri, workerText));

        var dotCompletion = await process.SendRequestAsync(2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 6, character = "    range_obj.".Length }
            });
        var dotLabels = dotCompletion
            .GetProperty("result")
            .EnumerateArray()
            .Select(item => item.GetProperty("label").GetString())
            .ToArray();
        Assert.Contains("Column", dotLabels);
        Assert.Contains("ColumnCount", dotLabels);
        Assert.DoesNotContain("Alias", dotLabels);
        Assert.DoesNotContain("Dim", dotLabels);
        Assert.DoesNotContain("BuildValue", dotLabels);

        var partialCompletion = await process.SendRequestAsync(3,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 7, character = "    range_obj.Col".Length }
            });
        var partialLabels = partialCompletion
            .GetProperty("result")
            .EnumerateArray()
            .Select(item => item.GetProperty("label").GetString())
            .ToArray();
        Assert.Contains("Column", partialLabels);
        Assert.Contains("ColumnCount", partialLabels);
        Assert.DoesNotContain("Alias", partialLabels);
        Assert.DoesNotContain("Dim", partialLabels);
        Assert.DoesNotContain("BuildValue", partialLabels);

        var bareTypeCompletion = await process.SendRequestAsync(4,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 3, character = "    Dim bare As ".Length }
            });
        var bareTypeLabels = bareTypeCompletion
            .GetProperty("result")
            .EnumerateArray()
            .Select(item => item.GetProperty("label").GetString())
            .ToArray();
        Assert.Contains("WorksheetRangeBounds", bareTypeLabels);
        Assert.Contains("String", bareTypeLabels);
        Assert.DoesNotContain("Alias", bareTypeLabels);
        Assert.DoesNotContain("Sub", bareTypeLabels);
        Assert.DoesNotContain("Then", bareTypeLabels);

        var typeCompletion = await process.SendRequestAsync(5,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 4, character = "    Dim typed As WorksheetRan".Length }
            });
        var typeLabels = typeCompletion
            .GetProperty("result")
            .EnumerateArray()
            .Select(item => item.GetProperty("label").GetString())
            .ToArray();
        Assert.Contains("WorksheetRangeBounds", typeLabels);
        Assert.Contains("String", typeLabels);
        Assert.DoesNotContain("Alias", typeLabels);
        Assert.DoesNotContain("Sub", typeLabels);
        Assert.DoesNotContain("Then", typeLabels);

        var completedMemberCompletion = await process.SendRequestAsync(6,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 8, character = "    aaaa = range_obj.Column ".Length }
            });
        Assert.Empty(completedMemberCompletion.GetProperty("result").EnumerateArray());

        var spacedDotCompletion = await process.SendRequestAsync(7,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 9, character = "    aaaa = range_obj. ".Length }
            });
        Assert.Empty(spacedDotCompletion.GetProperty("result").EnumerateArray());

        await process.ShutdownAsync(8);
    }

    [Fact]
    public async Task Server_returns_every_conditional_callable_signature_without_selecting_a_branch()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalCallableFamily.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalCallableFamily\"",
            "#If STRING_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As String) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Index As Long, Optional ByVal Fallback As String) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = ResolveValue(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    result = ResolveValue(",
            "    result = ResolveValue(".Length);
        var labels = response
            .GetProperty("result")
            .GetProperty("signatures")
            .EnumerateArray()
            .Select(signature => signature.GetProperty("label").GetString() ?? string.Empty)
            .ToArray();
        Assert.Equal(
            [
                "Function ResolveValue(Key As String) As String [#If]",
                "Function resolvevalue(Index As Long, [Fallback As String]) As String [#If]"
            ],
            labels);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_returns_every_conditional_member_callable_signature_without_selecting_a_branch()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string classUri = "file:///C:/work/ConditionalWorker.cls";
        var classText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalWorker\"",
            "#If STRING_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As String) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Index As Long) As String",
            "End Function",
            "#End If"
        ]);
        const string callerUri = "file:///C:/work/ConditionalMemberCaller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalMemberCaller\"",
            "Public Sub Run()",
            "    Dim worker As ConditionalWorker",
            "    Dim result As String",
            "    result = worker.ResolveValue(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(classUri, classText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            callerUri,
            callerText,
            "    result = worker.ResolveValue(",
            "    result = worker.ResolveValue(".Length);
        var labels = response
            .GetProperty("result")
            .GetProperty("signatures")
            .EnumerateArray()
            .Select(signature => signature.GetProperty("label").GetString() ?? string.Empty)
            .ToArray();
        Assert.Equal(
            [
                "Function ResolveValue(Key As String) As String [#If]",
                "Function resolvevalue(Index As Long) As String [#If]"
            ],
            labels);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_excludes_an_invalid_physical_signature_from_conditional_signature_help()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalRecoveredCallable.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalRecoveredCallable\"",
            "#If INVALID_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As String, ByVal key As Long) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Index As Long) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = ResolveValue(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    result = ResolveValue(",
            "    result = ResolveValue(".Length);
        var signatures = response
            .GetProperty("result")
            .GetProperty("signatures")
            .EnumerateArray()
            .ToArray();

        var signature = Assert.Single(signatures);
        Assert.Equal(
            "Function resolvevalue(Index As Long) As String [#If]",
            signature.GetProperty("label").GetString());

        var completion = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/completion",
            uri,
            text,
            "    result = ResolveValue(",
            "    result = ResolveValue(".Length);
        var indexItem = Assert.Single(
            completion.GetProperty("result").EnumerateArray(),
            item => item.GetProperty("label").GetString() == "Index");
        Assert.False(indexItem.TryGetProperty("detail", out _));
        Assert.DoesNotContain(
            completion.GetProperty("result").EnumerateArray(),
            item => item.GetProperty("label").GetString() == "Key");

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_excludes_a_recovered_byval_param_array_signature_from_callable_variants()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalRecoveredParamArray.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalRecoveredParamArray\"",
            "#If INVALID_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal ParamArray Values() As Variant) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Index As Long) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = ResolveValue(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    result = ResolveValue(",
            "    result = ResolveValue(".Length);
        var signatures = response
            .GetProperty("result")
            .GetProperty("signatures")
            .EnumerateArray()
            .ToArray();

        var signature = Assert.Single(signatures);
        Assert.Equal(
            "Function resolvevalue(Index As Long) As String [#If]",
            signature.GetProperty("label").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_excludes_a_private_conditional_callable_variant_at_an_external_use_site()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string declarationsUri = "file:///C:/work/ConditionalVisibilityDeclarations.bas";
        var declarationsText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalVisibilityDeclarations\"",
            "#If PRIVATE_CONFIGURATION Then",
            "Private Function ResolveValue(ByVal PrivateOnly As String) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal PublicOnly As Long) As String",
            "End Function",
            "#End If"
        ]);
        const string callerUri = "file:///C:/work/ConditionalVisibilityCaller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalVisibilityCaller\"",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = ResolveValue(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(declarationsUri, declarationsText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            callerUri,
            callerText,
            "    result = ResolveValue(",
            "    result = ResolveValue(".Length);
        var signatures = response
            .GetProperty("result")
            .GetProperty("signatures")
            .EnumerateArray()
            .ToArray();

        var signature = Assert.Single(signatures);
        Assert.Equal(
            "Function resolvevalue(PublicOnly As Long) As String [#If]",
            signature.GetProperty("label").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_retains_and_ranks_every_conditional_source_declare_signature()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalSourceDeclare.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalSourceDeclare\"",
            "#If ANSI_CONFIGURATION Then",
            "Public Declare Function ResolveValue Lib \"kernel32\" Alias \"ResolveValueA\" (ByVal Text As String) As Long",
            "#Else",
            "Public Declare Function resolvevalue Lib \"kernel32\" Alias \"ResolveValueW\" (ByVal Code As Long) As Long",
            "#End If",
            "Public Sub Run()",
            "    Dim result As Long",
            "    result = ResolveValue(1&",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    result = ResolveValue(1&",
            "    result = ResolveValue(1&".Length);
        var result = response.GetProperty("result");
        Assert.Equal(
            [
                "Declare Function ResolveValue(Text As String) As Long [#If]",
                "Declare Function resolvevalue(Code As Long) As Long [#If]"
            ],
            result
                .GetProperty("signatures")
                .EnumerateArray()
                .Select(signature => signature.GetProperty("label").GetString()));
        Assert.Equal(1, result.GetProperty("activeSignature").GetInt32());

        var definition = await SendDefinitionRequestAsync(
            process,
            3,
            uri,
            text,
            "    result = ResolveValue(1&",
            "    result = ".Length);
        Assert.Equal(
            [2, 4],
            definition
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_retains_an_external_declare_variant_with_an_any_parameter()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalSourceDeclareAny.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalSourceDeclareAny\"",
            "#If DESTINATION_CONFIGURATION Then",
            "Public Declare Sub CopyMemory Lib \"kernel32\" (ByRef Destination As Any)",
            "#Else",
            "Public Declare Sub copymemory Lib \"kernel32\" (ByVal Source As LongPtr)",
            "#End If",
            "Public Sub Run()",
            "    CopyMemory(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var signatureResponse = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    CopyMemory(",
            "    CopyMemory(".Length);
        Assert.Equal(
            [
                "Declare Sub CopyMemory(ByRef Destination As Any) [#If]",
                "Declare Sub copymemory(Source As LongPtr) [#If]"
            ],
            signatureResponse
                .GetProperty("result")
                .GetProperty("signatures")
                .EnumerateArray()
                .Select(signature => signature.GetProperty("label").GetString()));

        var completionResponse = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/completion",
            uri,
            text,
            "    CopyMemory(",
            "    CopyMemory(".Length);
        Assert.Contains(
            completionResponse.GetProperty("result").EnumerateArray(),
            item => item.GetProperty("kind").GetInt32() == 5
                && item.GetProperty("label").GetString() == "Destination");

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_excludes_an_invalid_declare_header_from_callable_variants()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalInvalidDeclareHeader.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalInvalidDeclareHeader\"",
            "#If INVALID_CONFIGURATION Then",
            "Public Declare Sub Work Lib \"kernel32\" (ByVal Key As Long) As Long",
            "#Else",
            "Public Declare Sub work Lib \"kernel32\" (ByVal GoodKey As Long)",
            "#End If",
            "Public Sub Run()",
            "    Work(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var signatureResponse = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    Work(",
            "    Work(".Length);
        var signature = Assert.Single(signatureResponse
            .GetProperty("result")
            .GetProperty("signatures")
            .EnumerateArray());
        Assert.Equal(
            "Declare Sub work(GoodKey As Long) [#If]",
            signature.GetProperty("label").GetString());

        var definitions = await SendDefinitionRequestAsync(
            process,
            3,
            uri,
            text,
            "    Work(",
            "    ".Length);
        Assert.Equal(
            [2, 4],
            definitions
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_requires_private_declare_members_in_an_object_module()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalObjectDeclare.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalObjectDeclare\"",
            "#If INVALID_CONFIGURATION Then",
            "Public Declare Function Work Lib \"kernel32\" (ByVal Key As Long) As Long",
            "#Else",
            "Private Declare Function work Lib \"kernel32\" (ByVal GoodKey As Long) As Long",
            "#End If",
            "Private Sub Run()",
            "    Dim result As Long",
            "    result = Work(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var signatureResponse = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    result = Work(",
            "    result = Work(".Length);
        var signature = Assert.Single(signatureResponse
            .GetProperty("result")
            .GetProperty("signatures")
            .EnumerateArray());
        Assert.Equal(
            "Declare Function work(GoodKey As Long) As Long [#If]",
            signature.GetProperty("label").GetString());

        var definitions = await SendDefinitionRequestAsync(
            process,
            3,
            uri,
            text,
            "    result = Work(",
            "    result = ".Length);
        Assert.Equal(
            [3, 5],
            definitions
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_excludes_a_property_setter_with_a_result_type_character()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalInvalidTypedPropertyLet.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalInvalidTypedPropertyLet\"",
            "#If INVALID_CONFIGURATION Then",
            "Public Property Let Item$(ByVal Assigned As String)",
            "End Property",
            "#Else",
            "Public Property Let item(ByVal GoodAssigned As String)",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    Item() = \"value\"",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var signatureResponse = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    Item() = \"value\"",
            "    Item(".Length);
        var signature = Assert.Single(signatureResponse
            .GetProperty("result")
            .GetProperty("signatures")
            .EnumerateArray());
        Assert.Equal(
            "Property item() [#If]",
            signature.GetProperty("label").GetString());

        var definitions = await SendDefinitionRequestAsync(
            process,
            3,
            uri,
            text,
            "    Item() = \"value\"",
            "    ".Length);
        Assert.Equal(
            [3, 6],
            definitions
                .EnumerateArray()
                .Select(location => location
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_projects_a_parameter_type_declaration_character_into_each_signature()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalParameterTypeCharacter.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalParameterTypeCharacter\"",
            "#If SUFFIX_CONFIGURATION Then",
            "Public Sub Work(ByRef Value&)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef Value As Long)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Work(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    Work(",
            "    Work(".Length);

        Assert.Equal(
            [
                "Sub Work(ByRef Value As Long) [#If]",
                "Sub work(ByRef Value As Long) [#If]"
            ],
            response
                .GetProperty("result")
                .GetProperty("signatures")
                .EnumerateArray()
                .Select(signature => signature.GetProperty("label").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_projects_a_function_result_type_declaration_character_into_each_signature()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalResultTypeCharacter.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalResultTypeCharacter\"",
            "#If SUFFIX_CONFIGURATION Then",
            "Public Function BuildValue&()",
            "End Function",
            "#Else",
            "Public Function buildvalue() As Long",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As Long",
            "    result = BuildValue(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    result = BuildValue(",
            "    result = BuildValue(".Length);

        Assert.Equal(
            [
                "Function BuildValue() As Long [#If]",
                "Function buildvalue() As Long [#If]"
            ],
            response
                .GetProperty("result")
                .GetProperty("signatures")
                .EnumerateArray()
                .Select(signature => signature.GetProperty("label").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_retains_parameters_after_a_function_result_type_character()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalTypedFunctionParameters.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalTypedFunctionParameters\"",
            "#If SUFFIX_CONFIGURATION Then",
            "Public Function BuildValue&(ByRef Key As Long)",
            "End Function",
            "#Else",
            "Public Function buildvalue(ByRef Key As Long) As Long",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As Long",
            "    result = BuildValue(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    result = BuildValue(",
            "    result = BuildValue(".Length);

        Assert.Equal(
            [
                "Function BuildValue(ByRef Key As Long) As Long [#If]",
                "Function buildvalue(ByRef Key As Long) As Long [#If]"
            ],
            response
                .GetProperty("result")
                .GetProperty("signatures")
                .EnumerateArray()
                .Select(signature => signature.GetProperty("label").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_resolves_a_call_written_with_a_function_result_type_character()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalTypedFunctionCall.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalTypedFunctionCall\"",
            "#If SUFFIX_CONFIGURATION Then",
            "Public Function ResolveValue&(ByVal Key As String)",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Key As String) As Long",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As Long",
            "    result = ResolveValue&(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    result = ResolveValue&(",
            "    result = ResolveValue&(".Length);

        Assert.Equal(
            [
                "Function ResolveValue(Key As String) As Long [#If]",
                "Function resolvevalue(Key As String) As Long [#If]"
            ],
            response
                .GetProperty("result")
                .GetProperty("signatures")
                .EnumerateArray()
                .Select(signature => signature.GetProperty("label").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_diagnoses_a_complete_call_written_with_a_function_result_type_character()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalTypedFunctionCompleteCall.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalTypedFunctionCompleteCall\"",
            "#If SUFFIX_CONFIGURATION Then",
            "Public Function ResolveValue&(ByVal Key As String)",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Key As String) As Long",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As Long",
            "    result = ResolveValue&()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .Where(candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList")
            .ToArray();
        var diagnostic = Assert.Single(diagnostics);
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.All(messages, message => Assert.Contains(
            "parameter 'Key': required argument is missing",
            message,
            StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_ranges_a_zero_argument_qualified_call_on_the_callee_identifier()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalQualifiedRange.bas";
        const string callLine = "    result = ConditionalQualifiedRange.ResolveValue()";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalQualifiedRange\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As String) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Key As String) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            callLine,
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var range = diagnostic.GetProperty("range");
        var identifierStart = callLine.IndexOf("ResolveValue", StringComparison.Ordinal);

        Assert.Equal(10, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(identifierStart, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(10, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(
            identifierStart + "ResolveValue".Length,
            range.GetProperty("end").GetProperty("character").GetInt32());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_ranges_a_statement_call_on_its_supplied_arguments()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalStatementArgumentRange.bas";
        const string callLine = "    Work 1";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalStatementArgumentRange\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work()",
            "End Sub",
            "#Else",
            "Public Sub work()",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            callLine,
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var range = diagnostic.GetProperty("range");
        var argumentStart = callLine.IndexOf('1');

        Assert.Equal(9, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(argumentStart, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(9, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(callLine.Length, range.GetProperty("end").GetProperty("character").GetInt32());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_ranks_a_value_producing_conditional_signature_without_filtering_variants()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalValueCallContext.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalValueCallContext\"",
            "#If SUB_CONFIGURATION Then",
            "Public Sub ResolveValue(ByVal Key As String)",
            "End Sub",
            "#Else",
            "Public Function resolvevalue(ByVal Key As String) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = ResolveValue(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    result = ResolveValue(",
            "    result = ResolveValue(".Length);
        var result = response.GetProperty("result");
        Assert.Equal(2, result.GetProperty("signatures").GetArrayLength());
        Assert.Equal(1, result.GetProperty("activeSignature").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_retains_a_context_incompatible_conditional_property_signature()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalPropertyContext.cls";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalPropertyContext\"",
            "#If WRITE_CONFIGURATION Then",
            "Public Property Let Item(ByVal Index As Long, ByVal Assigned As String)",
            "End Property",
            "#Else",
            "Public Property Get item(ByVal Index As Long) As String",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = Item(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    result = Item(",
            "    result = Item(".Length);
        var result = response.GetProperty("result");

        Assert.Equal(2, result.GetProperty("signatures").GetArrayLength());
        Assert.Equal(1, result.GetProperty("activeSignature").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_marks_only_guarded_physical_property_signatures_as_conditional()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/MixedConditionalPropertyContext.cls";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"MixedConditionalPropertyContext\"",
            "Public Property Get Item(ByVal Index As Long) As String",
            "End Property",
            "#If FIRST_WRITE_CONFIGURATION Then",
            "Public Property Let Item(ByVal Index As Long, ByVal Assigned As String)",
            "End Property",
            "#Else",
            "Public Property Let item(ByVal Index As Long, ByVal Value As String)",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = Item(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    result = Item(",
            "    result = Item(".Length);
        var labels = response
            .GetProperty("result")
            .GetProperty("signatures")
            .EnumerateArray()
            .Select(signature => signature.GetProperty("label").GetString() ?? string.Empty)
            .ToArray();
        Assert.Equal(
            [
                "Property Item(Index As Long) As String",
                "Property Item(Index As Long) [#If]",
                "Property item(Index As Long) [#If]"
            ],
            labels);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_retains_every_physical_property_signature_regardless_of_accessor_source_order()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/MixedConditionalPropertySourceOrder.cls";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"MixedConditionalPropertySourceOrder\"",
            "#If FIRST_WRITE_CONFIGURATION Then",
            "Public Property Let Item(ByVal Index As Long, ByVal Assigned As String)",
            "End Property",
            "#Else",
            "Public Property Let item(ByVal Index As Long, ByVal Value As String)",
            "End Property",
            "#End If",
            "Public Property Get Item(ByVal Index As Long) As String",
            "End Property",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = Item(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    result = Item(",
            "    result = Item(".Length);
        var result = response.GetProperty("result");
        var labels = result
            .GetProperty("signatures")
            .EnumerateArray()
            .Select(signature => signature.GetProperty("label").GetString() ?? string.Empty)
            .ToArray();
        Assert.Equal(
            [
                "Property Item(Index As Long) [#If]",
                "Property item(Index As Long) [#If]",
                "Property Item(Index As Long) As String"
            ],
            labels);
        Assert.Equal(2, result.GetProperty("activeSignature").GetInt32());

        var hover = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/hover",
            uri,
            text,
            "    result = Item(",
            "    result = ".Length);
        var hoverValue = hover
            .GetProperty("result")
            .GetProperty("contents")
            .GetProperty("value")
            .GetString();
        Assert.Contains(
            "Property Item(Index As Long, Assigned As String) [#If]",
            hoverValue,
            StringComparison.Ordinal);
        Assert.Contains(
            "Property item(Index As Long, Value As String) [#If]",
            hoverValue,
            StringComparison.Ordinal);
        Assert.Contains(
            "Property Item(Index As Long) As String",
            hoverValue,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Property Item(Index As Long) As String [#If]",
            hoverValue,
            StringComparison.Ordinal);

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_ranks_a_property_setter_while_its_explicit_assignment_is_incomplete()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalIncompletePropertySet.cls";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalIncompletePropertySet\"",
            "#If READ_CONFIGURATION Then",
            "Public Property Get Item(ByVal Index As Long) As String",
            "End Property",
            "#Else",
            "Public Property Set item(ByVal Index As Long, ByVal Assigned As Object)",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    Set Item(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    Set Item(",
            "    Set Item(".Length);
        var result = response.GetProperty("result");
        Assert.Equal(2, result.GetProperty("signatures").GetArrayLength());
        Assert.Equal(1, result.GetProperty("activeSignature").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_keeps_bare_incomplete_call_context_indeterminate()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalIndeterminateCallContext.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalIndeterminateCallContext\"",
            "#If PROPERTY_CONFIGURATION Then",
            "Public Property Let ResolveValue(ByVal Index As Long, ByVal Assigned As Long)",
            "End Property",
            "#Else",
            "Public Sub resolvevalue(ByVal Index As Long)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    ResolveValue(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    ResolveValue(",
            "    ResolveValue(".Length);
        var result = response.GetProperty("result");

        Assert.Equal(2, result.GetProperty("signatures").GetArrayLength());
        Assert.Equal(0, result.GetProperty("activeSignature").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_ranks_an_arity_compatible_signature_above_an_omitted_required_slot()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalOmittedArityRank.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalOmittedArityRank\"",
            "#If REQUIRED_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Required As Long, ByVal Tail As Long) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(Optional ByVal Maybe As Long, Optional ByVal Tail As Long) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = ResolveValue(, ",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    result = ResolveValue(, ",
            "    result = ResolveValue(, ".Length);
        var result = response.GetProperty("result");

        Assert.Equal(2, result.GetProperty("signatures").GetArrayLength());
        Assert.Equal(1, result.GetProperty("activeSignature").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_does_not_offer_a_conditional_sub_family_for_raise_event()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalNonEventFamily.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalNonEventFamily\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Saved(ByVal Code As Long)",
            "End Sub",
            "#Else",
            "Public Sub saved(ByVal Message As String)",
            "End Sub",
            "#End If",
            "Public Sub Fire()",
            "    RaiseEvent Saved(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    RaiseEvent Saved(",
            "    RaiseEvent Saved(".Length);

        Assert.Equal(JsonValueKind.Null, response.GetProperty("result").ValueKind);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_keeps_a_mixed_conditional_RaiseEvent_family_Event_only()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalEventAndSubFamily.cls";
        const string statementLine = "    RaiseEvent Saved(";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalEventAndSubFamily\"",
            "#If EVENT_CONFIGURATION Then",
            "Public Event Saved(ByVal x As Long)",
            "#Else",
            "Public Sub Saved(ByVal y As String)",
            "End Sub",
            "#End If",
            "Public Sub Fire()",
            statementLine,
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            statementLine,
            "    RaiseEvent Saved(".Length);
        var signature = Assert.Single(response
            .GetProperty("result")
            .GetProperty("signatures")
            .EnumerateArray());
        Assert.Equal(
            "Event Saved(x As Long) [#If]",
            signature.GetProperty("label").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_resolves_an_incomplete_RaiseEvent_target_definition()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/IncompleteRaiseEventDefinition.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"IncompleteRaiseEventDefinition\"",
            "Public Event Saved(ByVal Value As Long)",
            "Public Sub Fire()",
            "    RaiseEvent Saved(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var definition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            uri,
            text,
            "RaiseEvent Saved(",
            "RaiseEvent S".Length);
        var location = definition.GetProperty("result");
        Assert.Equal(uri, location.GetProperty("uri").GetString());
        Assert.Equal(
            2,
            location.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());

        var references = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/references",
            uri,
            text,
            "RaiseEvent Saved(",
            "RaiseEvent S".Length);
        Assert.Equal(
            [2, 4],
            references
                .GetProperty("result")
                .EnumerateArray()
                .Select(reference => reference
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        var rename = await SendPositionRequestAsync(
            process,
            4,
            "textDocument/rename",
            uri,
            text,
            "RaiseEvent Saved(",
            "RaiseEvent S".Length,
            new { newName = "Changed" });
        var edits = rename
            .GetProperty("result")
            .GetProperty("changes")
            .GetProperty(uri)
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, edits.Length);
        Assert.All(edits, edit => Assert.Equal("Changed", edit.GetProperty("newText").GetString()));

        await process.ShutdownAsync(5);
    }

    [Fact]
    public async Task Server_reports_an_Event_declaration_in_a_standard_module()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/StandardEvents.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"StandardEvents\"",
            "Event Saved()"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.eventDeclarationNotAllowedInModule");
        Assert.Equal(
            "Event declarations are allowed only at module level in a class module.",
            diagnostic.GetProperty("message").GetString());
        Assert.Equal(1, diagnostic.GetProperty("severity").GetInt32());
        var range = diagnostic.GetProperty("range");
        Assert.Equal(1, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(0, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(1, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal("Event".Length, range.GetProperty("end").GetProperty("character").GetInt32());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_reports_an_Event_declaration_inside_a_class_procedure()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/NestedEvent.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"NestedEvent\"",
            "Public Sub Run()",
            "    Event Saved()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.eventDeclarationNotAllowedInModule");
        var range = diagnostic.GetProperty("range");
        Assert.Equal(3, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(4, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(3, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(4 + "Event".Length, range.GetProperty("end").GetProperty("character").GetInt32());

        await process.ShutdownAsync(2);
    }

    [Theory]
    [InlineData("Private")]
    [InlineData("Friend")]
    public async Task Server_reports_a_nonpublic_Event_visibility(string visibility)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/InvalidEventVisibility.cls";
        var eventLine = $"{visibility} Event Saved()";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"InvalidEventVisibility\"",
            eventLine
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.eventVisibilityNotAllowed");
        Assert.Equal(
            "Event declarations can only be Public.",
            diagnostic.GetProperty("message").GetString());
        var range = diagnostic.GetProperty("range");
        Assert.Equal(2, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(0, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(2, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(visibility.Length, range.GetProperty("end").GetProperty("character").GetInt32());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_reports_an_Event_name_containing_an_underscore()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/InvalidEventName.cls";
        const string eventName = "Saved_Item";
        const string eventLine = "Public Event " + eventName + "()";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"InvalidEventName\"",
            eventLine
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.eventNameCannotContainUnderscore");
        Assert.Equal(
            "Event name cannot contain an underscore.",
            diagnostic.GetProperty("message").GetString());
        var range = diagnostic.GetProperty("range");
        var nameStart = eventLine.IndexOf(eventName, StringComparison.Ordinal);
        Assert.Equal(2, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(nameStart, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(2, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(nameStart + eventName.Length, range.GetProperty("end").GetProperty("character").GetInt32());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_reports_an_optional_Event_parameter()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/OptionalEventParameter.cls";
        const string eventLine = "Public Event Saved(Optional ByVal Message As String)";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"OptionalEventParameter\"",
            eventLine
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.eventOptionalParameterNotAllowed");
        Assert.Equal(
            "Event parameters cannot be Optional.",
            diagnostic.GetProperty("message").GetString());
        var range = diagnostic.GetProperty("range");
        var optionalStart = eventLine.IndexOf("Optional", StringComparison.Ordinal);
        Assert.Equal(2, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(optionalStart, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(2, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(optionalStart + "Optional".Length, range.GetProperty("end").GetProperty("character").GetInt32());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_reports_a_ParamArray_Event_parameter()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ParamArrayEventParameter.cls";
        const string eventLine = "Public Event Saved(ParamArray Values() As Variant)";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ParamArrayEventParameter\"",
            eventLine
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.eventParamArrayParameterNotAllowed");
        Assert.Equal(
            "Event parameters cannot be ParamArray.",
            diagnostic.GetProperty("message").GetString());
        var range = diagnostic.GetProperty("range");
        var paramArrayStart = eventLine.IndexOf("ParamArray", StringComparison.Ordinal);
        Assert.Equal(2, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(paramArrayStart, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(2, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(paramArrayStart + "ParamArray".Length, range.GetProperty("end").GetProperty("character").GetInt32());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_reports_RaiseEvent_inside_a_standard_module_procedure()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/InvalidRaiseEventPlacement.bas";
        const string statementLine = "    RaiseEvent Saved";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"InvalidRaiseEventPlacement\"",
            "Public Sub Run()",
            statementLine,
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();
        var diagnostic = Assert.Single(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.raiseEventStatementNotAllowedHere");
        Assert.Equal(
            "RaiseEvent statements are allowed only inside a procedure in a class module.",
            diagnostic.GetProperty("message").GetString());
        var range = diagnostic.GetProperty("range");
        var keywordStart = statementLine.IndexOf("RaiseEvent", StringComparison.Ordinal);
        Assert.Equal(2, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(keywordStart, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(2, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(keywordStart + "RaiseEvent".Length, range.GetProperty("end").GetProperty("character").GetInt32());
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                is "validation.raiseEventTargetNotDeclaredInEnclosingModule"
                or "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_does_not_treat_RaiseEvent_inside_Rem_comments_as_a_statement()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/CommentedRaiseEvent.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"CommentedRaiseEvent\"",
            "Rem RaiseEvent Saved",
            "Private value As Long: Rem RaiseEvent Saved",
            "Public Sub Run()",
            "    Rem RaiseEvent Saved",
            "    Debug.Print 1: Rem RaiseEvent Saved",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();

        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString() is { } code
                && code.Contains("raiseEvent", StringComparison.OrdinalIgnoreCase));

        await process.ShutdownAsync(2);
    }

    [Theory]
    [InlineData("Rem comment _")]
    [InlineData("Private value As Long: Rem comment _")]
    public async Task Server_does_not_continue_a_Rem_comment_into_the_following_Event(
        string commentLine)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/CommentBeforeEvent.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"CommentBeforeEvent\"",
            commentLine,
            "Public Event Saved()",
            "Public Sub Fire()",
            "    RaiseEvent Saved",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.raiseEventTargetNotDeclaredInEnclosingModule");

        var definition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            uri,
            text,
            "RaiseEvent Saved",
            "RaiseEvent S".Length);
        var location = definition.GetProperty("result");
        Assert.Equal(uri, location.GetProperty("uri").GetString());
        Assert.Equal(
            3,
            location.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_does_not_continue_an_inline_Rem_comment_into_a_following_procedure()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/CommentBeforeProcedure.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"CommentBeforeProcedure\"",
            "Public Event Saved()",
            "Private value As Long: Rem comment _",
            "Public Sub Fire()",
            "    RaiseEvent Saved",
            "    Debug.Print 1: Rem comment _",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                is "syntax.unexpectedStatementBoundaryToken"
                    or "syntax.missingBlockTerminator"
                    or "syntax.raiseEventStatementNotAllowedHere"
                    or "validation.raiseEventTargetNotDeclaredInEnclosingModule");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_treats_Rem_after_a_numeric_line_label_as_a_comment()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/NumericLabelRem.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"NumericLabelRem\"",
            "Public Sub Run()",
            "10 Rem RaiseEvent Saved _",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                is "syntax.raiseEventStatementNotAllowedHere"
                    or "syntax.missingBlockTerminator"
                    or "validation.raiseEventTargetNotDeclaredInEnclosingModule"
                    or "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_excludes_a_numeric_label_Rem_comment_from_Event_editor_features()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/NumericLabelEventComment.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"NumericLabelEventComment\"",
            "Public Event Saved()",
            "Public Sub Fire()",
            "10 Rem RaiseEvent Saved",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var definition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            uri,
            text,
            "10 Rem RaiseEvent Saved",
            "10 Rem RaiseEvent S".Length);
        Assert.Equal(JsonValueKind.Null, definition.GetProperty("result").ValueKind);

        var completion = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/completion",
            uri,
            text,
            "10 Rem RaiseEvent Saved",
            "10 Rem RaiseEvent ".Length);
        Assert.DoesNotContain(
            completion.GetProperty("result").EnumerateArray(),
            candidate => candidate.GetProperty("label").GetString() == "Saved");

        var references = await SendPositionRequestAsync(
            process,
            4,
            "textDocument/references",
            uri,
            text,
            "Public Event Saved",
            "Public Event S".Length);
        Assert.Equal(
            [2],
            references
                .GetProperty("result")
                .EnumerateArray()
                .Select(reference => reference
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32()));

        await process.ShutdownAsync(5);
    }

    [Fact]
    public async Task Server_accepts_a_zero_argument_RaiseEvent_before_a_colon_statement()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ColonAfterRaiseEvent.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ColonAfterRaiseEvent\"",
            "Public Event Saved()",
            "Public Sub Fire()",
            "    RaiseEvent Saved: Debug.Print 1",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.raiseEventArgumentListRequiresParentheses");
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_reports_parenthesis_free_RaiseEvent_arguments_before_a_colon_once()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ColonAfterRaiseEventArgument.cls";
        const string statementLine = "    RaiseEvent Saved 1: Debug.Print 2";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ColonAfterRaiseEventArgument\"",
            "Public Event Saved(ByVal value As Long)",
            "Public Sub Fire()",
            statementLine,
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.raiseEventArgumentListRequiresParentheses");
        var range = diagnostic.GetProperty("range");
        var argumentStart = statementLine.IndexOf("1:", StringComparison.Ordinal);
        Assert.Equal(4, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(argumentStart, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(4, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(argumentStart + 1, range.GetProperty("end").GetProperty("character").GetInt32());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_rejects_RaiseEvent_after_a_procedure_terminator_on_the_same_line()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/RaiseEventAfterTerminator.cls";
        const string terminatorLine = "End Sub: RaiseEvent Saved";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"RaiseEventAfterTerminator\"",
            "Public Event Saved()",
            "Public Sub Fire()",
            terminatorLine
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();
        var diagnostic = Assert.Single(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.raiseEventStatementNotAllowedHere");
        var keywordStart = terminatorLine.IndexOf("RaiseEvent", StringComparison.Ordinal);
        var range = diagnostic.GetProperty("range");
        Assert.Equal(4, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(keywordStart, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(4, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(keywordStart + "RaiseEvent".Length, range.GetProperty("end").GetProperty("character").GetInt32());
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                is "validation.raiseEventTargetNotDeclaredInEnclosingModule"
                or "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_recognizes_a_callable_terminator_after_another_colon_terminator()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/RaiseEventAfterMultipleTerminators.cls";
        const string terminatorLine = "    End If: End Sub: RaiseEvent Saved";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"RaiseEventAfterMultipleTerminators\"",
            "Public Event Saved()",
            "Public Sub Fire()",
            "    If True Then",
            terminatorLine
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();
        var diagnostic = Assert.Single(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.raiseEventStatementNotAllowedHere");
        var keywordStart = terminatorLine.IndexOf("RaiseEvent", StringComparison.Ordinal);
        var range = diagnostic.GetProperty("range");
        Assert.Equal(5, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(keywordStart, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(5, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(keywordStart + "RaiseEvent".Length, range.GetProperty("end").GetProperty("character").GetInt32());
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                is "syntax.missingBlockTerminator"
                    or "validation.raiseEventTargetNotDeclaredInEnclosingModule"
                    or "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_rejects_RaiseEvent_in_an_external_Declare_colon_tail()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/DeclareTailRaiseEvent.cls";
        const string statementLine =
            "Private Declare Sub Native Lib \"x\": RaiseEvent Saved";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"DeclareTailRaiseEvent\"",
            "Public Event Saved()",
            statementLine
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();
        var diagnostic = Assert.Single(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.raiseEventStatementNotAllowedHere");
        var range = diagnostic.GetProperty("range");
        var keywordStart = statementLine.IndexOf("RaiseEvent", StringComparison.Ordinal);
        Assert.Equal(3, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(keywordStart, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(3, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(keywordStart + "RaiseEvent".Length, range.GetProperty("end").GetProperty("character").GetInt32());
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                is "validation.raiseEventTargetNotDeclaredInEnclosingModule"
                or "validation.incompatibleCallArgumentList");

        var definition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            uri,
            text,
            "RaiseEvent Saved",
            "RaiseEvent S".Length);
        Assert.Equal(JsonValueKind.Null, definition.GetProperty("result").ValueKind);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_does_not_extend_a_bare_RaiseEvent_across_an_uncontinued_line()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/BareRaiseEventBoundary.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"BareRaiseEventBoundary\"",
            "Public Sub Run()",
            "    RaiseEvent",
            "    Foo 1",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.raiseEventArgumentListRequiresParentheses"
                && candidate.GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32() == 4);
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                is "validation.raiseEventTargetNotDeclaredInEnclosingModule"
                or "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_reports_a_missing_RaiseEvent_target_in_the_enclosing_class()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/MissingRaiseEventTarget.cls";
        const string statementLine = "    RaiseEvent Missing";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"MissingRaiseEventTarget\"",
            "Public Sub Run()",
            statementLine,
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();
        var diagnostic = Assert.Single(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "validation.raiseEventTargetNotDeclaredInEnclosingModule");
        Assert.Equal(
            "RaiseEvent target must be an Event declared in the enclosing class module.",
            diagnostic.GetProperty("message").GetString());
        var range = diagnostic.GetProperty("range");
        var targetStart = statementLine.IndexOf("Missing", StringComparison.Ordinal);
        Assert.Equal(3, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(targetStart, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(3, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(targetStart + "Missing".Length, range.GetProperty("end").GetProperty("character").GetInt32());
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.raiseEventOmittedArgumentNotAllowed");

        await process.ShutdownAsync(2);
    }

    [Theory]
    [InlineData("()", "syntax.raiseEventEmptyArgumentListNotAllowed")]
    [InlineData("(Name:=1)", "syntax.raiseEventNamedArgumentNotAllowed")]
    [InlineData("(,)", "syntax.raiseEventOmittedArgumentNotAllowed")]
    [InlineData(" 1", "syntax.raiseEventArgumentListRequiresParentheses")]
    public async Task Server_reports_a_missing_RaiseEvent_target_despite_an_invalid_list_shape(
        string suffix,
        string shapeDiagnosticCode)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/MissingRaiseEventTargetWithList.cls";
        var statementLine = $"    RaiseEvent Missing{suffix}";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"MissingRaiseEventTargetWithList\"",
            "Public Sub Run()",
            statementLine,
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();
        Assert.Contains(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString() == shapeDiagnosticCode);
        var targetDiagnostic = Assert.Single(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "validation.raiseEventTargetNotDeclaredInEnclosingModule");
        var targetRange = targetDiagnostic.GetProperty("range");
        var targetStart = statementLine.IndexOf("Missing", StringComparison.Ordinal);
        Assert.Equal(3, targetRange.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(targetStart, targetRange.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(3, targetRange.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(targetStart + "Missing".Length, targetRange.GetProperty("end").GetProperty("character").GetInt32());
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Theory]
    [InlineData("(")]
    [InlineData("(1 +)")]
    [InlineData("( ' TODO")]
    public async Task Server_reports_a_missing_RaiseEvent_target_during_an_incomplete_call(
        string suffix)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/IncompleteMissingRaiseEventTarget.cls";
        var statementLine = $"    RaiseEvent Missing{suffix}";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"IncompleteMissingRaiseEventTarget\"",
            "Public Sub Run()",
            statementLine,
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();
        var diagnostic = Assert.Single(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "validation.raiseEventTargetNotDeclaredInEnclosingModule");
        var range = diagnostic.GetProperty("range");
        var targetStart = statementLine.IndexOf("Missing", StringComparison.Ordinal);
        Assert.Equal(3, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(targetStart, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(3, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(targetStart + "Missing".Length, range.GetProperty("end").GetProperty("character").GetInt32());
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_reports_one_missing_target_for_the_owning_incomplete_RaiseEvent()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/MultipleIncompleteRaiseEventTargets.cls";
        const string statementLine = "    RaiseEvent Saved: RaiseEvent Missing(";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"MultipleIncompleteRaiseEventTargets\"",
            "Public Event Saved()",
            "Public Sub Run()",
            statementLine,
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();
        var diagnostic = Assert.Single(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "validation.raiseEventTargetNotDeclaredInEnclosingModule");
        var range = diagnostic.GetProperty("range");
        var targetStart = statementLine.LastIndexOf("Missing", StringComparison.Ordinal);
        Assert.Equal(4, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(targetStart, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(4, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(targetStart + "Missing".Length, range.GetProperty("end").GetProperty("character").GetInt32());
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_does_not_manufacture_an_Event_from_a_continuation_tail()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ContinuationTailEvent.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ContinuationTailEvent\"",
            "Private value As _",
            "    Event Saved()",
            "Public Sub Run()",
            "    RaiseEvent Saved",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();
        Assert.Contains(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "validation.raiseEventTargetNotDeclaredInEnclosingModule");

        var completion = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/completion",
            uri,
            text,
            "RaiseEvent Saved",
            "RaiseEvent Saved".Length);
        Assert.DoesNotContain(
            completion.GetProperty("result").EnumerateArray(),
            candidate => candidate.GetProperty("label").GetString() == "Saved");

        var definition = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/definition",
            uri,
            text,
            "RaiseEvent Saved",
            "RaiseEvent S".Length);
        Assert.Equal(JsonValueKind.Null, definition.GetProperty("result").ValueKind);

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_excludes_an_invalid_optional_Event_variant_from_RaiseEvent_signature_help()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalRecoveredEventSignature.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalRecoveredEventSignature\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Event Saved(Optional ByVal Message As String)",
            "#Else",
            "Public Event saved(ByVal Message As String)",
            "#End If",
            "Public Sub Fire()",
            "    RaiseEvent Saved(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    RaiseEvent Saved(",
            "    RaiseEvent Saved(".Length);
        var signatures = response
            .GetProperty("result")
            .GetProperty("signatures")
            .EnumerateArray()
            .ToArray();

        var signature = Assert.Single(signatures);
        Assert.Equal(
            "Event saved(Message As String) [#If]",
            signature.GetProperty("label").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_excludes_an_Event_with_a_result_type_from_RaiseEvent_signature_help()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalRecoveredEventResultType.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalRecoveredEventResultType\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Event Saved() As Long",
            "#Else",
            "Public Event saved(ByVal Message As String)",
            "#End If",
            "Public Sub Fire()",
            "    RaiseEvent Saved(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    RaiseEvent Saved(",
            "    RaiseEvent Saved(".Length);
        var signatures = response
            .GetProperty("result")
            .GetProperty("signatures")
            .EnumerateArray()
            .ToArray();

        var signature = Assert.Single(signatures);
        Assert.Equal(
            "Event saved(Message As String) [#If]",
            signature.GetProperty("label").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_ranks_the_conditional_signature_containing_the_active_named_argument()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalNamedSignatureRank.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalNamedSignatureRank\"",
            "#If KEY_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As String) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Index As Long) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = ResolveValue(Index:=",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    result = ResolveValue(Index:=",
            "    result = ResolveValue(Index:=".Length);
        var result = response.GetProperty("result");
        Assert.Equal(2, result.GetProperty("signatures").GetArrayLength());
        Assert.Equal(1, result.GetProperty("activeSignature").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_ranks_the_conditional_signature_containing_every_supplied_named_argument()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalSuppliedNamedArguments.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalSuppliedNamedArguments\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal FirstOnly As Long, ByVal Common As Long) As Long",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal SecondOnly As Long, ByVal Common As Long) As Long",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As Long",
            "    result = ResolveValue(SecondOnly:=unknownValue, Common:=",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    result = ResolveValue(SecondOnly:=unknownValue, Common:=",
            "    result = ResolveValue(SecondOnly:=unknownValue, Common:=".Length);
        var result = response.GetProperty("result");

        Assert.Equal(2, result.GetProperty("signatures").GetArrayLength());
        Assert.Equal(1, result.GetProperty("activeSignature").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_ranks_a_signature_by_a_supplied_name_after_the_active_argument()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalTrailingNamedSignatureRank.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalTrailingNamedSignatureRank\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByVal First As Long, ByVal Common As Long)",
            "End Sub",
            "#Else",
            "Public Sub work(ByVal Second As Long, ByVal Common As Long)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Work , Second:=2&",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    Work , Second:=2&",
            "    Work ".Length);
        var result = response.GetProperty("result");

        Assert.Equal(2, result.GetProperty("signatures").GetArrayLength());
        Assert.Equal(1, result.GetProperty("activeSignature").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_does_not_partially_rank_supplied_named_argument_membership()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalPartialNamedArguments.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalPartialNamedArguments\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal A As Variant, ByVal X As Variant) As Long",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal A As Variant, ByVal B As Variant) As Long",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As Long",
            "    result = ResolveValue(A:=unknownA, B:=unknownB, C:=",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    result = ResolveValue(A:=unknownA, B:=unknownB, C:=",
            "    result = ResolveValue(A:=unknownA, B:=unknownB, C:=".Length);
        var result = response.GetProperty("result");

        Assert.Equal(2, result.GetProperty("signatures").GetArrayLength());
        Assert.Equal(0, result.GetProperty("activeSignature").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_ranks_the_conditional_signature_accepting_the_active_positional_argument()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalAritySignatureRank.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalAritySignatureRank\"",
            "#If ONE_ARGUMENT Then",
            "Public Function ResolveValue(ByVal Key As String) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Key As String, ByVal Fallback As String) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = ResolveValue(\"key\", ",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    result = ResolveValue(\"key\", ",
            "    result = ResolveValue(\"key\", ".Length);
        var result = response.GetProperty("result");
        Assert.Equal(2, result.GetProperty("signatures").GetArrayLength());
        Assert.Equal(1, result.GetProperty("activeSignature").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_ranks_an_exact_known_argument_type_without_selecting_a_variant()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalTypeSignatureRank.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalTypeSignatureRank\"",
            "#If LONG_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Value As Long) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Value As String) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = ResolveValue(\"text\"",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    result = ResolveValue(\"text\"",
            "    result = ResolveValue(\"text\"".Length);
        var result = response.GetProperty("result");
        Assert.Equal(2, result.GetProperty("signatures").GetArrayLength());
        Assert.Equal(1, result.GetProperty("activeSignature").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_ranks_an_exact_declared_argument_type_without_selecting_a_variant()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalDeclaredTypeSignatureRank.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalDeclaredTypeSignatureRank\"",
            "#If LONG_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Value As Long) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Value As String) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim textValue As String",
            "    Dim result As String",
            "    result = ResolveValue(textValue",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    result = ResolveValue(textValue",
            "    result = ResolveValue(textValue".Length);
        var result = response.GetProperty("result");
        Assert.Equal(2, result.GetProperty("signatures").GetArrayLength());
        Assert.Equal(1, result.GetProperty("activeSignature").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_ranks_a_whitespace_separated_parenthesized_statement_argument_as_a_value_temporary()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string otherClassUri = "file:///C:/work/OtherClass.cls";
        var otherClassText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"OtherClass\""
        ]);
        const string actualClassUri = "file:///C:/work/ActualClass.cls";
        var actualClassText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ActualClass\""
        ]);
        const string uri = "file:///C:/work/ConditionalParenthesizedStatementSignatureRank.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalParenthesizedStatementSignatureRank\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByRef Value As OtherClass)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef Value As Object)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Dim value As ActualClass",
            "    Work (value)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(otherClassUri, otherClassText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(actualClassUri, actualClassText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    Work (value)",
            "    Work (value".Length);
        var result = response.GetProperty("result");

        Assert.Equal(2, result.GetProperty("signatures").GetArrayLength());
        Assert.Equal(1, result.GetProperty("activeSignature").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_treats_qualified_and_unqualified_byref_types_as_one_canonical_identity()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-conditional-canonical-call-type-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(
                projectRoot,
                "Microsoft Excel 16.0 Object Library");
            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "ConditionalCanonicalCallType.bas"));
            var text = string.Join('\n', [
                "Attribute VB_Name = \"ConditionalCanonicalCallType\"",
                "#If FIRST_CONFIGURATION Then",
                "Public Sub Work(ByRef Target As Excel.Range)",
                "End Sub",
                "#Else",
                "Public Sub work(ByRef Target As Excel.Range)",
                "End Sub",
                "#End If",
                "Public Sub Run()",
                "    Dim cell As Range",
                "    Work cell",
                "End Sub"
            ]);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));

            var notification = await process.WaitForDiagnosticsAsync(uri);

            Assert.DoesNotContain(
                notification
                    .GetProperty("params")
                    .GetProperty("diagnostics")
                    .EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.incompatibleCallArgumentList");

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_ranks_a_proven_class_to_object_assignment_without_selecting_a_variant()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-conditional-object-assignment-rank-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(
                projectRoot,
                "Microsoft Excel 16.0 Object Library");
            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "ConditionalObjectAssignmentRank.bas"));
            var text = string.Join('\n', [
                "Attribute VB_Name = \"ConditionalObjectAssignmentRank\"",
                "#If STRING_CONFIGURATION Then",
                "Public Function ResolveValue(ByVal Value As String) As Long",
                "End Function",
                "#Else",
                "Public Function resolvevalue(ByVal Value As Object) As Long",
                "End Function",
                "#End If",
                "Public Sub Run()",
                "    Dim cell As Range",
                "    Dim result As Long",
                "    result = ResolveValue(cell",
                "End Sub"
            ]);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));

            var response = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/signatureHelp",
                uri,
                text,
                "    result = ResolveValue(cell",
                "    result = ResolveValue(cell".Length);
            var result = response.GetProperty("result");

            Assert.Equal(2, result.GetProperty("signatures").GetArrayLength());
            Assert.Equal(1, result.GetProperty("activeSignature").GetInt32());

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_retains_a_tied_conditional_signature_on_retrigger()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                signatureHelp = new
                {
                    contextSupport = true,
                    signatureInformation = new
                    {
                        activeParameterSupport = true,
                        noActiveParameterSupport = true
                    }
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalSignatureRetrigger.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalSignatureRetrigger\"",
            "#If KEY_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As String) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Index As Long) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = ResolveValue(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await process.SendRequestAsync(
            2,
            "textDocument/signatureHelp",
            new
            {
                textDocument = new { uri },
                position = new
                {
                    line = 10,
                    character = "    result = ResolveValue(".Length
                },
                context = new
                {
                    triggerKind = 3,
                    isRetrigger = true,
                    activeSignatureHelp = new
                    {
                        signatures = new object[]
                        {
                            new
                            {
                                label = "Function ResolveValue(Key As String) As String [#If]",
                                parameters = new[] { new { label = "Key As String" } }
                            },
                            new
                            {
                                label = "Function resolvevalue(Index As Long) As String [#If]",
                                parameters = new[] { new { label = "Index As Long" } }
                            }
                        },
                        activeSignature = 1,
                        activeParameter = 0
                    }
                }
            });
        var result = response.GetProperty("result");
        Assert.Equal(2, result.GetProperty("signatures").GetArrayLength());
        Assert.Equal(1, result.GetProperty("activeSignature").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_ignores_retrigger_state_without_context_support()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                signatureHelp = new
                {
                    contextSupport = false
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalSignatureNoContext.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalSignatureNoContext\"",
            "#If KEY_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As String) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Index As Long) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = ResolveValue(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await process.SendRequestAsync(
            2,
            "textDocument/signatureHelp",
            new
            {
                textDocument = new { uri },
                position = new
                {
                    line = 10,
                    character = "    result = ResolveValue(".Length
                },
                context = new
                {
                    triggerKind = 3,
                    isRetrigger = true,
                    activeSignatureHelp = new
                    {
                        signatures = new object[]
                        {
                            new
                            {
                                label = "Function ResolveValue(Key As String) As String [#If]",
                                parameters = new[] { new { label = "Key As String" } }
                            },
                            new
                            {
                                label = "Function resolvevalue(Index As Long) As String [#If]",
                                parameters = new[] { new { label = "Index As Long" } }
                            }
                        },
                        activeSignature = 1,
                        activeParameter = 0
                    }
                }
            });
        var result = response.GetProperty("result");
        Assert.Equal(2, result.GetProperty("signatures").GetArrayLength());
        Assert.Equal(0, result.GetProperty("activeSignature").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_excludes_context_incompatible_variants_from_named_argument_completion()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalCompletionContext.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalCompletionContext\"",
            "#If SUB_CONFIGURATION Then",
            "Public Sub ResolveValue(ByVal SubOnly As String)",
            "End Sub",
            "#Else",
            "Public Function resolvevalue(ByVal FunctionOnly As String) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = ResolveValue(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/completion",
            uri,
            text,
            "    result = ResolveValue(",
            "    result = ResolveValue(".Length);
        var labels = response
            .GetProperty("result")
            .EnumerateArray()
            .Select(item => item.GetProperty("label").GetString() ?? string.Empty)
            .ToArray();
        Assert.Contains("FunctionOnly", labels);
        Assert.DoesNotContain("SubOnly", labels);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_projects_active_parameters_for_each_conditional_signature()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                signatureHelp = new
                {
                    signatureInformation = new
                    {
                        activeParameterSupport = true,
                        noActiveParameterSupport = true
                    }
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalSignatureParameters.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalSignatureParameters\"",
            "#If STRING_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As String, Optional ByVal FirstOnly As Variant) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Index As Long, Optional ByVal SecondOnly As Variant) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = ResolveValue(\"value\", ",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    result = ResolveValue(\"value\", ",
            "    result = ResolveValue(\"value\", ".Length);
        var signatures = response
            .GetProperty("result")
            .GetProperty("signatures")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, signatures.Length);
        Assert.Equal(1, signatures[0].GetProperty("activeParameter").GetInt32());
        Assert.Equal(1, signatures[1].GetProperty("activeParameter").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_projects_null_when_an_argument_does_not_map_to_a_conditional_signature()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                signatureHelp = new
                {
                    signatureInformation = new
                    {
                        activeParameterSupport = true,
                        noActiveParameterSupport = true
                    }
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalUnmappedParameter.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalUnmappedParameter\"",
            "#If STRING_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As String, Optional ByVal FirstOnly As Variant) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Index As Long, Optional ByVal SecondOnly As Variant) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = ResolveValue(SecondOnly:=",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    result = ResolveValue(SecondOnly:=",
            "    result = ResolveValue(SecondOnly:=".Length);
        var signatures = response
            .GetProperty("result")
            .GetProperty("signatures")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, signatures.Length);
        Assert.Equal(JsonValueKind.Null, signatures[0].GetProperty("activeParameter").ValueKind);
        Assert.Equal(1, signatures[1].GetProperty("activeParameter").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_omits_an_unmapped_active_parameter_without_null_support()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                signatureHelp = new
                {
                    signatureInformation = new
                    {
                        activeParameterSupport = true
                    }
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalOmittedParameter.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalOmittedParameter\"",
            "#If STRING_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As String, Optional ByVal FirstOnly As Variant) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Index As Long, Optional ByVal SecondOnly As Variant) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = ResolveValue(SecondOnly:=",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    result = ResolveValue(SecondOnly:=",
            "    result = ResolveValue(SecondOnly:=".Length);
        var signatures = response
            .GetProperty("result")
            .GetProperty("signatures")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, signatures.Length);
        Assert.False(signatures[0].TryGetProperty("activeParameter", out _));
        Assert.Equal(1, signatures[1].GetProperty("activeParameter").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_does_not_map_a_named_raise_event_argument_to_any_conditional_signature()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                signatureHelp = new
                {
                    signatureInformation = new
                    {
                        activeParameterSupport = true,
                        noActiveParameterSupport = true
                    }
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalNamedRaiseEvent.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalNamedRaiseEvent\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Event Saved(ByVal Message As String)",
            "#Else",
            "Public Event saved(ByVal Code As Long)",
            "#End If",
            "Public Sub Fire()",
            "    RaiseEvent Saved(Message:=",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    RaiseEvent Saved(Message:=",
            "    RaiseEvent Saved(Message:=".Length);
        var signatures = response
            .GetProperty("result")
            .GetProperty("signatures")
            .EnumerateArray()
            .ToArray();

        Assert.Equal(2, signatures.Length);
        Assert.All(
            signatures,
            signature => Assert.Equal(
                JsonValueKind.Null,
                signature.GetProperty("activeParameter").ValueKind));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_does_not_clamp_an_excess_argument_to_the_last_parameter()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                signatureHelp = new
                {
                    signatureInformation = new
                    {
                        activeParameterSupport = true,
                        noActiveParameterSupport = true
                    }
                }
            }
        });
        const string uri = "file:///C:/work/ExcessSignatureParameter.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ExcessSignatureParameter\"",
            "Public Function ResolveValue(ByVal Key As String) As String",
            "End Function",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = ResolveValue(\"one\", ",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            uri,
            text,
            "    result = ResolveValue(\"one\", ",
            "    result = ResolveValue(\"one\", ".Length);
        var signature = Assert.Single(response
            .GetProperty("result")
            .GetProperty("signatures")
            .EnumerateArray());
        Assert.Equal(JsonValueKind.Null, signature.GetProperty("activeParameter").ValueKind);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_unions_remaining_named_arguments_across_conditional_callable_signatures()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalNamedArguments.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalNamedArguments\"",
            "#If STRING_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As String, Optional ByVal Common As Variant) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Index As Long, Optional ByVal common As Variant, Optional ByVal Fallback As String) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = ResolveValue(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/completion",
            uri,
            text,
            "    result = ResolveValue(",
            "    result = ResolveValue(".Length);
        var expectedNames = new HashSet<string>(
            ["Key", "Common", "Index", "Fallback"],
            StringComparer.OrdinalIgnoreCase);
        var namedItems = response
            .GetProperty("result")
            .EnumerateArray()
            .Where(item => item.GetProperty("kind").GetInt32() == 5)
            .Where(item => expectedNames.Contains(
                item.GetProperty("label").GetString() ?? string.Empty))
            .ToArray();
        Assert.Equal(4, namedItems.Length);
        Assert.Equal(
            expectedNames,
            namedItems
                .Select(item => item.GetProperty("label").GetString() ?? string.Empty)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
        Assert.Single(namedItems, item =>
            (item.GetProperty("label").GetString() ?? string.Empty).Equals(
                "Common",
                StringComparison.OrdinalIgnoreCase));
        foreach (var conditionalName in new[] { "Key", "Index", "Fallback" })
        {
            var item = Assert.Single(namedItems, item =>
                (item.GetProperty("label").GetString() ?? string.Empty).Equals(
                    conditionalName,
                    StringComparison.OrdinalIgnoreCase));
            Assert.Equal("[#If]", item.GetProperty("detail").GetString());
        }

        var commonItem = Assert.Single(namedItems, item =>
            (item.GetProperty("label").GetString() ?? string.Empty).Equals(
                "Common",
                StringComparison.OrdinalIgnoreCase));
        Assert.False(commonItem.TryGetProperty("detail", out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_excludes_a_trailing_supplied_name_from_middle_argument_completion()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/MiddleArgumentNamedCompletion.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"MiddleArgumentNamedCompletion\"",
            "Public Sub Work(ByVal First As Long, ByVal Second As Long)",
            "End Sub",
            "Public Sub Run()",
            "    Work , Second:=2&",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/completion",
            uri,
            text,
            "    Work , Second:=2&",
            "    Work ".Length);
        var namedLabels = response
            .GetProperty("result")
            .EnumerateArray()
            .Where(item => item.GetProperty("kind").GetInt32() == 5)
            .Select(item => item.GetProperty("label").GetString())
            .ToArray();

        Assert.Contains("First", namedLabels);
        Assert.DoesNotContain("Second", namedLabels);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_offers_no_named_arguments_before_a_trailing_positional_argument()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/MiddleArgumentPositionalCompletion.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"MiddleArgumentPositionalCompletion\"",
            "Public Sub Work(ByVal First As Long, ByVal Second As Long)",
            "End Sub",
            "Public Sub Run()",
            "    Work , 2&",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/completion",
            uri,
            text,
            "    Work , 2&",
            "    Work ".Length);

        Assert.DoesNotContain(
            response.GetProperty("result").EnumerateArray(),
            item => item.GetProperty("kind").GetInt32() == 5);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_unions_indexed_property_names_while_the_call_context_is_indeterminate()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalPropertyNamedArguments.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalPropertyNamedArguments\"",
            "#If READ_CONFIGURATION Then",
            "Public Property Get Item(ByVal ReadIndex As Long) As Variant",
            "End Property",
            "#Else",
            "Public Property Let item(ByVal WriteIndex As Long, ByVal AssignedValue As Variant)",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    Item(",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/completion",
            uri,
            text,
            "    Item(",
            "    Item(".Length);
        var expectedNames = new HashSet<string>(
            ["ReadIndex", "WriteIndex"],
            StringComparer.OrdinalIgnoreCase);
        var namedItems = response
            .GetProperty("result")
            .EnumerateArray()
            .Where(item => item.GetProperty("kind").GetInt32() == 5)
            .Where(item => expectedNames.Contains(
                item.GetProperty("label").GetString() ?? string.Empty))
            .ToArray();

        Assert.Equal(expectedNames, namedItems
            .Select(item => item.GetProperty("label").GetString() ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase));
        Assert.All(
            namedItems,
            item => Assert.Equal("[#If]", item.GetProperty("detail").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_offers_no_named_arguments_for_a_paramarray_signature()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ParamArrayNamedArguments.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ParamArrayNamedArguments\"",
            "Public Sub Collect(ByVal Prefix As String, ParamArray Values() As Variant)",
            "End Sub",
            "Public Sub Run()",
            "    Collect ",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/completion",
            uri,
            text,
            "    Collect ",
            "    Collect ".Length);
        Assert.DoesNotContain(
            response.GetProperty("result").EnumerateArray(),
            item => item.GetProperty("kind").GetInt32() == 5);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_accepts_an_omitted_paramarray_slot_before_more_positionals()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ParamArrayOmission.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ParamArrayOmission\"",
            "Public Sub Collect(ByVal Prefix As String, ParamArray Values() As Variant)",
            "End Sub",
            "Public Sub Run()",
            "    Collect \"prefix\", , ",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/completion",
            uri,
            text,
            "    Collect \"prefix\", , ",
            "    Collect \"prefix\", , ".Length);
        Assert.Contains(
            response.GetProperty("result").EnumerateArray(),
            item => item.GetProperty("label").GetString() == "True");

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_diagnoses_a_complete_call_rejected_by_every_conditional_signature()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalRejectedCall.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalRejectedCall\"",
            "#If KEY_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As String) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Index As Long) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = ResolveValue(Unknown:=1)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        Assert.Equal(
            "No available callable signature accepts this argument list.\n"
                + "Candidate signature: Function ResolveValue(Key As String) As String [#If].\n"
                + "Mismatches: argument 1 ('Unknown') mapping: no parameter named 'Unknown'.\n"
                + "Candidate signature: Function resolvevalue(Index As Long) As String [#If].\n"
                + "Mismatches: argument 1 ('Unknown') mapping: no parameter named 'Unknown'.",
            diagnostic.GetProperty("message").GetString());
        Assert.False(diagnostic.TryGetProperty("relatedInformation", out _));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_diagnoses_a_complete_call_rejected_by_an_unconditional_signature()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/UnconditionalRejectedCall.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"UnconditionalRejectedCall\"",
            "Public Sub Work(ByRef Text As String)",
            "End Sub",
            "Public Sub Run()",
            "    Dim number As Long",
            "    Work number",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var detail = Assert.Single(diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray())
            .GetProperty("message")
            .GetString();
        Assert.Equal(
            "Candidate signature: Sub Work(ByRef Text As String). Mismatches: "
                + "argument 1 for parameter 'Text' ByRef type: expected String, found Long.",
            detail);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_diagnoses_a_zero_argument_statement_call_over_its_conditional_callee()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalZeroArgumentStatement.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalZeroArgumentStatement\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByVal Value As Long)",
            "End Sub",
            "#Else",
            "Public Sub work(ByVal Text As String)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Work",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var range = diagnostic.GetProperty("range");

        Assert.Equal(9, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(4, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(9, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(8, range.GetProperty("end").GetProperty("character").GetInt32());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_diagnoses_a_bare_zero_argument_value_read_through_the_shared_mapper()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalZeroArgumentValueRead.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalZeroArgumentValueRead\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As String) As Long",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Index As Long) As Long",
            "End Function",
            "#End If",
            "Public Function Run() As Long",
            "    Run = ResolveValue",
            "End Function"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var range = diagnostic.GetProperty("range");
        Assert.Equal(9, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(10, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(9, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(22, range.GetProperty("end").GetProperty("character").GetInt32());
        Assert.Equal(
            [
                "Candidate signature: Function ResolveValue(Key As String) As Long [#If]. Mismatches: parameter 'Key': required argument is missing.",
                "Candidate signature: Function resolvevalue(Index As Long) As Long [#If]. Mismatches: parameter 'Index': required argument is missing."
            ],
            diagnostic
                .GetProperty("relatedInformation")
                .EnumerateArray()
                .Select(item => item.GetProperty("message").GetString()!)
                .ToArray());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_diagnoses_a_bare_zero_argument_value_read_in_a_Print_statement()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalZeroArgumentPrintValue.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalZeroArgumentPrintValue\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As Long) As Long",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Index As Long) As Long",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Debug.Print ResolveValue",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var range = diagnostic.GetProperty("range");
        Assert.Equal(9, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(16, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(9, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(28, range.GetProperty("end").GetProperty("character").GetInt32());
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();
        Assert.Equal(2, messages.Length);
        Assert.Contains("parameter 'Key': required argument is missing.", messages[0]);
        Assert.Contains("parameter 'Index': required argument is missing.", messages[1]);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_diagnoses_a_bare_zero_argument_value_read_in_an_If_condition()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalBareIfRead.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalBareIfRead\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As String) As Boolean",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Key As String) As Boolean",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    If ResolveValue Then",
            "    End If",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var range = diagnostic.GetProperty("range");
        Assert.Equal(9, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(7, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(9, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(19, range.GetProperty("end").GetProperty("character").GetInt32());
        Assert.All(
            diagnostic
                .GetProperty("relatedInformation")
                .EnumerateArray(),
            item => Assert.Contains(
                "parameter 'Key': required argument is missing.",
                item.GetProperty("message").GetString(),
                StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_diagnoses_a_bare_zero_argument_value_read_inside_a_larger_expression()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalBareExpressionRead.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalBareExpressionRead\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As String) As Boolean",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Key As String) As Boolean",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    If Not ResolveValue Then",
            "    End If",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var range = diagnostic.GetProperty("range");
        Assert.Equal(9, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(11, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(9, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(23, range.GetProperty("end").GetProperty("character").GetInt32());
        Assert.All(
            diagnostic
                .GetProperty("relatedInformation")
                .EnumerateArray(),
            item => Assert.Contains(
                "parameter 'Key': required argument is missing.",
                item.GetProperty("message").GetString(),
                StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_diagnoses_a_bare_zero_argument_value_read_before_a_comparison()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalBareComparisonRead.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalBareComparisonRead\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As String) As Boolean",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Key As String) As Boolean",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    If ResolveValue = True Then",
            "    End If",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var range = diagnostic.GetProperty("range");
        Assert.Equal(9, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(7, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(9, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(19, range.GetProperty("end").GetProperty("character").GetInt32());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_diagnoses_a_bare_zero_argument_callable_receiver_in_a_member_chain()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalBareCallableReceiver.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalBareCallableReceiver\"",
            "Public Type Result",
            "    Member As Long",
            "End Type",
            "#If FIRST_CONFIGURATION Then",
            "Public Function Build(ByVal Key As Long) As Result",
            "End Function",
            "#Else",
            "Public Function build(ByVal Index As Long) As Result",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Debug.Print Build.Member",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_diagnoses_a_bare_zero_argument_value_read_in_a_ReDim_bound()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalBareReDimBoundRead.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalBareReDimBoundRead\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function ResolveBound(ByVal Key As Long) As Long",
            "End Function",
            "#Else",
            "Public Function resolvebound(ByVal Index As Long) As Long",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim values() As Long",
            "    ReDim values(ResolveBound)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        Assert.Equal(
            [
                "Candidate signature: Function ResolveBound(Key As Long) As Long [#If]. Mismatches: parameter 'Key': required argument is missing.",
                "Candidate signature: Function resolvebound(Index As Long) As Long [#If]. Mismatches: parameter 'Index': required argument is missing."
            ],
            diagnostic
                .GetProperty("relatedInformation")
                .EnumerateArray()
                .Select(item => item.GetProperty("message").GetString()!)
                .ToArray());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_diagnoses_a_bare_zero_argument_value_read_in_an_Open_statement()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalBareOpenPathRead.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalBareOpenPathRead\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function ResolvePath(ByVal Key As Long) As String",
            "End Function",
            "#Else",
            "Public Function resolvepath(ByVal Index As Long) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Open ResolvePath For Input As #1",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        Assert.Equal(
            [
                "Candidate signature: Function ResolvePath(Key As Long) As String [#If]. Mismatches: parameter 'Key': required argument is missing.",
                "Candidate signature: Function resolvepath(Index As Long) As String [#If]. Mismatches: parameter 'Index': required argument is missing."
            ],
            diagnostic
                .GetProperty("relatedInformation")
                .EnumerateArray()
                .Select(item => item.GetProperty("message").GetString()!)
                .ToArray());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_does_not_diagnose_a_label_declaration_as_a_bare_value_read()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalCallableNamedLabel.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalCallableNamedLabel\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As Long) As Long",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Index As Long) As Long",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "ResolveValue:",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_does_not_diagnose_a_GoTo_label_reference_as_a_bare_value_read()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalCallableNamedGoToLabel.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalCallableNamedGoToLabel\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As Long) As Long",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Index As Long) As Long",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    GoTo ResolveValue",
            "ResolveValue:",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_does_not_diagnose_an_Enum_member_initializer_as_a_bare_value_read()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalCallableNamedEnumInitializer.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalCallableNamedEnumInitializer\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As Long) As Long",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Index As Long) As Long",
            "End Function",
            "#End If",
            "Public Enum Values",
            "    Item = ResolveValue",
            "End Enum"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_does_not_diagnose_a_DefType_range_as_a_bare_value_read()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalCallableDefTypeRange.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalCallableDefTypeRange\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function A(ByVal Key As Long) As Long",
            "End Function",
            "#Else",
            "Public Function a(ByVal Index As Long) As Long",
            "End Function",
            "#End If",
            "DefLng A-Z"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_does_not_diagnose_an_AddressOf_operand_as_a_bare_value_read()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalCallableAddressOfOperand.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalCallableAddressOfOperand\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function Callback(ByVal Key As Long) As Long",
            "End Function",
            "#Else",
            "Public Function callback(ByVal Index As Long) As Long",
            "End Function",
            "#End If",
            "Private Sub Consume(ByVal Pointer As LongPtr)",
            "End Sub",
            "Public Sub Run()",
            "    Consume AddressOf Callback",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_does_not_diagnose_a_New_type_operand_as_a_bare_value_read()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string callerUri = "file:///C:/work/ConditionalCallableNewTypeOperand.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalCallableNewTypeOperand\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function Work(ByVal Key As Long) As Object",
            "End Function",
            "#Else",
            "Public Function work(ByVal Index As Long) As Object",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim value As Object",
            "    Set value = New Work",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var notification = await process.WaitForDiagnosticsAsync(callerUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_does_not_diagnose_a_named_argument_label_as_a_bare_value_read()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalCallableNamedArgumentLabel.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalCallableNamedArgumentLabel\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function Key(ByVal Value As Long) As Long",
            "End Function",
            "#Else",
            "Public Function key(ByVal Index As Long) As Long",
            "End Function",
            "#End If",
            "Public Sub Work(ByVal Key As Long)",
            "End Sub",
            "Public Sub Run()",
            "    Work Key:=1&",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_maps_a_statement_call_in_a_single_line_If_tail()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalSingleLineIfTailCall.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalSingleLineIfTailCall\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByVal Value As Long)",
            "End Sub",
            "#Else",
            "Public Sub work(ByVal Value As Long)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    If True Then Work 1&",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_maps_a_statement_call_in_a_nested_single_line_If_tail()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalNestedSingleLineIfTailCall.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalNestedSingleLineIfTailCall\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByVal Value As Long)",
            "End Sub",
            "#Else",
            "Public Sub work(ByVal Value As Long)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    If True Then If True Then Work 1&",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_maps_both_nested_and_outer_single_line_If_Else_tails()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalNestedSingleLineIfElseCalls.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalNestedSingleLineIfElseCalls\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByVal Value As Long)",
            "End Sub",
            "#Else",
            "Public Sub work(ByVal Value As Long)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    If True Then If True Then Work 1& Else Work 2& Else Work 3&",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_maps_colon_separated_single_line_If_statement_lists()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalSingleLineIfStatementLists.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalSingleLineIfStatementLists\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByVal Value As Long)",
            "End Sub",
            "#Else",
            "Public Sub work(ByVal Value As Long)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    If True Then Work 1&: Work 2& Else Work 3&",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_maps_an_explicit_Call_in_a_single_line_If_tail()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalSingleLineIfExplicitCall.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalSingleLineIfExplicitCall\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByVal Value As Long)",
            "End Sub",
            "#Else",
            "Public Sub work(ByVal Value As Long)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    If True Then Call Work(1&)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_maps_an_indexed_Property_Set_in_a_single_line_If_tail()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalSingleLineIfIndexedPropertySet.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalSingleLineIfIndexedPropertySet\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Property Set Item(ByVal Index As Long, ByVal assigned As Object)",
            "End Property",
            "#Else",
            "Public Property Set item(ByVal Index As Long, ByVal assigned As Object)",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    If True Then Set Item(1&) = Me",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_maps_an_unindexed_Property_Set_in_a_single_line_If_tail()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalSingleLineIfUnindexedPropertySet.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalSingleLineIfUnindexedPropertySet\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Property Set Value(ByVal assigned As Object)",
            "End Property",
            "#Else",
            "Public Property Set value(ByVal assigned As Object)",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    If True Then Set Value = Me",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_maps_an_indexed_Property_Let_in_a_single_line_If_tail()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalSingleLineIfIndexedPropertyLet.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalSingleLineIfIndexedPropertyLet\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Property Let Item(ByVal Index As Long, ByVal assigned As Long)",
            "End Property",
            "#Else",
            "Public Property Let item(ByVal Index As Long, ByVal assigned As Long)",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    If True Then Let Item(1&) = 2&",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_diagnoses_a_zero_argument_Call_statement_through_the_shared_mapper()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalZeroArgumentCallStatement.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalZeroArgumentCallStatement\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByVal Key As String)",
            "End Sub",
            "#Else",
            "Public Sub work(ByVal Index As Long)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Call Work",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var range = diagnostic.GetProperty("range");
        Assert.Equal(9, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(9, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(9, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(13, range.GetProperty("end").GetProperty("character").GetInt32());
        Assert.Equal(
            [
                "Candidate signature: Sub Work(Key As String) [#If]. Mismatches: parameter 'Key': required argument is missing.",
                "Candidate signature: Sub work(Index As Long) [#If]. Mismatches: parameter 'Index': required argument is missing."
            ],
            diagnostic
                .GetProperty("relatedInformation")
                .EnumerateArray()
                .Select(item => item.GetProperty("message").GetString()!)
                .ToArray());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_reports_an_empty_RaiseEvent_argument_list_without_an_aggregate_call_diagnostic()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalEmptyRaiseEventArgumentList.cls";
        const string statementLine = "    RaiseEvent Saved()";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalEmptyRaiseEventArgumentList\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Event Saved(ByVal Message As String)",
            "#Else",
            "Public Event saved(ByVal Message As String)",
            "#End If",
            "Public Sub Run()",
            statementLine,
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();
        var diagnostic = Assert.Single(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.raiseEventEmptyArgumentListNotAllowed");
        Assert.Equal(
            "RaiseEvent must omit parentheses when no arguments are supplied.",
            diagnostic.GetProperty("message").GetString());
        var range = diagnostic.GetProperty("range");
        var argumentListStart = statementLine.IndexOf("()", StringComparison.Ordinal);
        Assert.Equal(8, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(argumentListStart, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(8, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(argumentListStart + 2, range.GetProperty("end").GetProperty("character").GetInt32());
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_reports_an_empty_RaiseEvent_list_in_a_single_line_If_tail_without_an_aggregate()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalSingleLineIfEmptyRaiseEvent.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalSingleLineIfEmptyRaiseEvent\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Event Saved(ByVal Message As String)",
            "#Else",
            "Public Event saved(ByVal Message As String)",
            "#End If",
            "Public Sub Run()",
            "    If True Then RaiseEvent Saved()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();
        Assert.Single(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.raiseEventEmptyArgumentListNotAllowed");
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_reports_an_empty_continued_RaiseEvent_argument_list_without_an_aggregate()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalContinuedEmptyRaiseEvent.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalContinuedEmptyRaiseEvent\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Event Saved(ByVal Message As String)",
            "#Else",
            "Public Event saved(ByVal Message As String)",
            "#End If",
            "Public Sub Run()",
            "    RaiseEvent Saved( _",
            "    )",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();
        Assert.Single(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.raiseEventEmptyArgumentListNotAllowed");
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_reports_omitted_RaiseEvent_arguments_without_an_aggregate_call_diagnostic()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalOmittedRaiseEventArgument.cls";
        const string statementLine = "    RaiseEvent Saved(,)";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalOmittedRaiseEventArgument\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Event Saved(ByVal Message As String)",
            "#Else",
            "Public Event saved(ByVal Message As String)",
            "#End If",
            "Public Sub Run()",
            statementLine,
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();
        var diagnostic = Assert.Single(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.raiseEventOmittedArgumentNotAllowed");
        Assert.Equal(
            "RaiseEvent arguments cannot be omitted.",
            diagnostic.GetProperty("message").GetString());
        var range = diagnostic.GetProperty("range");
        var argumentListStart = statementLine.IndexOf("(,)", StringComparison.Ordinal);
        Assert.Equal(8, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(argumentListStart, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(8, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(argumentListStart + 3, range.GetProperty("end").GetProperty("character").GetInt32());
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_accepts_a_RaiseEvent_when_any_valid_conditional_signature_matches()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalMixedRaiseEvent.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalMixedRaiseEvent\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Event Saved(ByVal Value As Long)",
            "#ElseIf SECOND_CONFIGURATION Then",
            "Public Event Saved(ByVal Value As Long, ByVal Retry As Long)",
            "#Else",
            "Private Event Saved(ByVal Value As Long)",
            "#End If",
            "Public Sub Run()",
            "    RaiseEvent Saved(1)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();
        Assert.Contains(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.eventVisibilityNotAllowed");
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_does_not_complete_members_from_a_RaiseEvent_result()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/RaiseEventResult.cls";
        const string statementLine = "    RaiseEvent Saved(1).";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"RaiseEventResult\"",
            "Public Event Saved(ByVal Value As Long)",
            "Public Sub Run()",
            statementLine,
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri },
                position = new { line = 4, character = statementLine.Length }
            });

        Assert.Empty(completion.GetProperty("result").EnumerateArray());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_diagnoses_a_zero_argument_RaiseEvent_call_through_the_shared_mapper()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalZeroArgumentRaiseEvent.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalZeroArgumentRaiseEvent\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Event Saved(ByVal Message As String)",
            "#Else",
            "Public Event saved(ByVal Code As Long)",
            "#End If",
            "Public Sub Fire()",
            "    RaiseEvent Saved",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var range = diagnostic.GetProperty("range");
        Assert.Equal(8, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(15, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(8, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(20, range.GetProperty("end").GetProperty("character").GetInt32());
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();
        Assert.Equal(2, messages.Length);
        Assert.Contains("parameter 'Message': required argument is missing.", messages[0]);
        Assert.Contains("parameter 'Code': required argument is missing.", messages[1]);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_diagnoses_an_unindexed_Property_assignment_through_the_shared_mapper()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalUnindexedPropertyAssignment.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalUnindexedPropertyAssignment\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Property Let Value(ByVal Key As String, ByVal assigned As Long)",
            "End Property",
            "#Else",
            "Public Property Let value(ByVal Index As Long, ByVal assigned As Long)",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    Value = 1",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var range = diagnostic.GetProperty("range");
        Assert.Equal(10, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(4, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(10, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(9, range.GetProperty("end").GetProperty("character").GetInt32());
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();
        Assert.Equal(2, messages.Length);
        Assert.Contains("parameter 'Key': required argument is missing.", messages[0]);
        Assert.Contains("parameter 'Index': required argument is missing.", messages[1]);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_does_not_treat_a_Property_Get_result_assignment_as_a_setter_call()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/PropertyGetResultAssignment.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"PropertyGetResultAssignment\"",
            "Public Property Get Value() As Long",
            "    Value = 1&",
            "End Property"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_reports_every_rejected_conditional_signature_as_related_information()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalRejectedCallDetails.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalRejectedCallDetails\"",
            "#If KEY_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As String) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Index As Long) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = ResolveValue(Unknown:=1)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var relatedInformation = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, relatedInformation.Length);
        Assert.Equal(
            [2, 5],
            relatedInformation.Select(item => item
                .GetProperty("location")
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32()));
        Assert.Equal(
            [
                "Candidate signature: Function ResolveValue(Key As String) As String [#If]. Mismatches: argument 1 ('Unknown') mapping: no parameter named 'Unknown'.",
                "Candidate signature: Function resolvevalue(Index As Long) As String [#If]. Mismatches: argument 1 ('Unknown') mapping: no parameter named 'Unknown'."
            ],
            relatedInformation.Select(item => item.GetProperty("message").GetString()));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_reports_a_required_omitted_argument_for_every_conditional_signature()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalRequiredOmission.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalRequiredOmission\"",
            "#If KEY_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As String, Optional ByVal Fallback As String) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Index As Long, Optional ByVal Fallback As String) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = ResolveValue(, \"fallback\")",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Equal(
            [
                "Candidate signature: Function ResolveValue(Key As String, [Fallback As String]) As String [#If]. Mismatches: parameter 'Key': required argument is missing.",
                "Candidate signature: Function resolvevalue(Index As Long, [Fallback As String]) As String [#If]. Mismatches: parameter 'Index': required argument is missing."
            ],
            messages);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_orders_mapping_reasons_before_required_omission_reasons()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalCallMismatchReasonOrder.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalCallMismatchReasonOrder\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function Work(ByVal First As Long, ByVal Second As Long) As Long",
            "End Function",
            "#Else",
            "Public Function work(ByVal First As Long, ByVal Second As Long) As Long",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As Long",
            "    result = Work(, First:=1)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.All(messages, message => Assert.Contains(
            "Mismatches: argument 2 ('First') mapping: parameter 'First' is already supplied; parameter 'First': required argument is missing; parameter 'Second': required argument is missing.",
            message,
            StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_preserves_source_order_between_duplicate_and_unknown_named_arguments()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalNamedMismatchOrder.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalNamedMismatchOrder\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByVal A As Long, ByVal B As Long)",
            "End Sub",
            "#Else",
            "Public Sub work(ByVal A As Long, ByVal B As Long)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Work 1&, A:=2&, Unknown:=3&",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.All(messages, message => Assert.Contains(
            "Mismatches: argument 2 ('A') mapping: parameter 'A' is already supplied; "
                + "argument 3 ('Unknown') mapping: no parameter named 'Unknown'",
            message,
            StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_retains_missing_required_reasons_after_a_mapping_mismatch()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalIndependentRequiredReasons.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalIndependentRequiredReasons\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByVal A As Long, ByVal B As Long, ByVal C As Long)",
            "End Sub",
            "#Else",
            "Public Sub work(ByVal A As Long, ByVal B As Long, ByVal C As Long)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Work 1&, A:=2&",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.All(messages, message => Assert.Contains(
            "Mismatches: argument 2 ('A') mapping: parameter 'A' is already supplied; "
                + "parameter 'B': required argument is missing; "
                + "parameter 'C': required argument is missing",
            message,
            StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_does_not_reject_conditional_signatures_for_an_incomplete_argument_expression()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalIncompleteArgument.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalIncompleteArgument\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByVal Only As Long)",
            "End Sub",
            "#Else",
            "Public Sub work(ByVal Only As Long)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Work 1&, (",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_does_not_reject_conditional_signatures_for_an_empty_named_argument_value()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalIncompleteNamedArgument.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalIncompleteNamedArgument\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByVal Only As Long)",
            "End Sub",
            "#Else",
            "Public Sub work(ByVal Only As Long)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Work Unknown:=",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_does_not_reject_conditional_signatures_for_an_incomplete_member_argument()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalIncompleteMemberArgument.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalIncompleteMemberArgument\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByVal Only As Long)",
            "End Sub",
            "#Else",
            "Public Sub work(ByVal Only As Long)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Work 1&, foo.",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_does_not_reject_a_call_while_an_earlier_argument_expression_is_incomplete()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalEarlierIncompleteMemberArgument.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalEarlierIncompleteMemberArgument\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByVal Only As Long)",
            "End Sub",
            "#Else",
            "Public Sub work(ByVal Only As Long)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Work foo., 1&",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_does_not_reject_an_outer_call_while_a_nested_argument_is_incomplete()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalNestedIncompleteArgument.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalNestedIncompleteArgument\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function Outer(ByVal Only As Long) As Long",
            "End Function",
            "#Else",
            "Public Function outer(ByVal Only As Long) As Long",
            "End Function",
            "#End If",
            "Private Function Inner(ByVal First As Variant, ByVal Second As Long) As Long",
            "End Function",
            "Public Sub Run()",
            "    Dim result As Long",
            "    result = Outer(Inner(foo., 1&), 2&)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_does_not_reject_conditional_signatures_for_an_incomplete_parenthesized_member_argument()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalIncompleteParenthesizedMemberArgument.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalIncompleteParenthesizedMemberArgument\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function Work(ByVal Only As Long) As Long",
            "End Function",
            "#Else",
            "Public Function work(ByVal Only As Long) As Long",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As Long",
            "    result = Work(1&, foo.)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_reports_only_the_first_mapping_reason_for_a_paramarray_named_argument()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalParamArrayMappingReason.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalParamArrayMappingReason\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Collect(ByVal Prefix As String, ParamArray Values() As Variant)",
            "End Sub",
            "#Else",
            "Public Sub collect(ByVal Prefix As String, ParamArray Values() As Variant)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Collect \"a\", Prefix:=\"b\"",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.All(messages, message => Assert.Contains(
            "Mismatches: argument 2 ('Prefix') mapping: named arguments are not accepted.",
            message,
            StringComparison.Ordinal));
        Assert.DoesNotContain(
            messages,
            message => message.Contains("already supplied", StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_reports_a_duplicate_parameter_assignment_for_every_conditional_signature()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalDuplicateAssignment.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalDuplicateAssignment\"",
            "#If KEY_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As String) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Key As Variant) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = ResolveValue(\"first\", Key:=\"second\")",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Equal(
            [
                "Candidate signature: Function ResolveValue(Key As String) As String [#If]. Mismatches: argument 2 ('Key') mapping: parameter 'Key' is already supplied.",
                "Candidate signature: Function resolvevalue(Key As Variant) As String [#If]. Mismatches: argument 2 ('Key') mapping: parameter 'Key' is already supplied."
            ],
            messages);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_reports_independent_mapping_and_byref_mismatches_for_each_conditional_signature()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalIndependentMismatches.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalIndependentMismatches\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function ResolveValue(ByRef Key As String) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByRef Key As String) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    Dim number As Long",
            "    result = ResolveValue(number, Unknown:=1)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.All(
            messages,
            message => Assert.Contains(
                "Mismatches: argument 2 ('Unknown') mapping: no parameter named 'Unknown'; argument 1 for parameter 'Key' ByRef type: expected String, found Long.",
                message,
                StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_orders_byref_reasons_before_ordinary_type_reasons()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalTypeReasonOrder.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalTypeReasonOrder\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal First As Object, ByRef Second As String) As Long",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal First As Object, ByRef Second As String) As Long",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim first As Long",
            "    Dim second As Long",
            "    Dim result As Long",
            "    result = ResolveValue(first, second)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.All(messages, message => Assert.Contains(
            "Mismatches: argument 2 for parameter 'Second' ByRef type: expected String, found Long; "
                + "argument 1 for parameter 'First' type: expected Object, found Long.",
            message,
            StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_reports_call_context_mismatches_for_every_conditional_signature()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalContextMismatch.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalContextMismatch\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub PerformWork()",
            "End Sub",
            "#Else",
            "Public Sub performwork()",
            "End Sub",
            "#End If",
            "Public Function Run() As Long",
            "    Run = PerformWork()",
            "End Function"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString())
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.All(
            messages,
            message => Assert.Contains(
                "call context: expected Function or Property Get, found Sub",
                message,
                StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_reports_every_conditional_property_accessor_rejected_by_the_assignment_context()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalPropertyContextMismatch.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalPropertyContextMismatch\"",
            "#If READ_CONFIGURATION Then",
            "Public Property Get Item(ByVal Index As Long) As Object",
            "End Property",
            "#Else",
            "Public Property Let item(ByVal Index As Long, ByVal Assigned As String)",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    Set Item(0&) = Nothing",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString())
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.Contains(
            messages,
            message => message?.Contains(
                "call context: expected Property Set, found Property Get",
                StringComparison.Ordinal) == true);
        Assert.Contains(
            messages,
            message => message?.Contains(
                "call context: expected Property Set, found Property Let",
                StringComparison.Ordinal) == true);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_maps_property_assignment_names_only_to_index_parameters()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalPropertyValueName.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalPropertyValueName\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Property Let Item(ByVal Index As Long, ByVal Assigned As String)",
            "End Property",
            "#Else",
            "Public Property Let item(ByVal Index As Long, ByVal Value As String)",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    Item(Assigned:=1&) = \"value\"",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString())
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.All(
            messages,
            message => Assert.Contains(
                "argument 1 ('Assigned') mapping: no parameter named 'Assigned'",
                message,
                StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_suppresses_the_aggregate_diagnostic_when_a_conditional_signature_is_invalid()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalInvalidSignature.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalInvalidSignature\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByRef Value As String, ByRef value As Date)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef Value As String)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Dim number As Long",
            "    Work number",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();

        Assert.Contains(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "validation.duplicateCallableParameterName");
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_prefers_the_raise_event_named_argument_syntax_diagnostic()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalRaiseEvent.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalRaiseEvent\"",
            "#If MESSAGE_CONFIGURATION Then",
            "Public Event Saved(ByVal Message As String)",
            "#Else",
            "Public Event saved(ByVal Code As Long)",
            "#End If",
            "Public Sub Fire()",
            "    RaiseEvent Saved(Unknown:=1)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();

        var diagnostic = Assert.Single(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.raiseEventNamedArgumentNotAllowed");
        Assert.Equal(
            "RaiseEvent arguments cannot use named-argument syntax.",
            diagnostic.GetProperty("message").GetString());
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_reports_a_RaiseEvent_named_argument_in_a_single_line_If()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalSingleLineIfRaiseEvent.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalSingleLineIfRaiseEvent\"",
            "#If MESSAGE_CONFIGURATION Then",
            "Public Event Saved(ByVal Message As String)",
            "#Else",
            "Public Event saved(ByVal Code As Long)",
            "#End If",
            "Public Sub Fire()",
            "    If True Then RaiseEvent Saved(Message:=value)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();

        var diagnostic = Assert.Single(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.raiseEventNamedArgumentNotAllowed");
        var range = diagnostic.GetProperty("range");
        Assert.Equal(8, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(34, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(8, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(43, range.GetProperty("end").GetProperty("character").GetInt32());
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_prefers_the_parenthesis_free_RaiseEvent_syntax_diagnostic()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalParenthesisFreeRaiseEvent.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalParenthesisFreeRaiseEvent\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Event Changed(ByVal First As Long, ByVal Second As Long)",
            "#Else",
            "Public Event changed(ByVal First As Long, ByVal Second As Long)",
            "#End If",
            "Public Sub Fire()",
            "    RaiseEvent Changed 1&",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();
        Assert.Contains(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.raiseEventArgumentListRequiresParentheses");
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_allows_named_arguments_inside_a_nested_raise_event_argument_call()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalNestedRaiseEvent.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalNestedRaiseEvent\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Event Saved(ByVal Message As String)",
            "#Else",
            "Public Event saved(ByVal Message As String)",
            "#End If",
            "Private Function Build(ByVal x As Long) As String",
            "End Function",
            "Public Sub Fire()",
            "    RaiseEvent Saved(Build(x:=1))",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.raiseEventNamedArgumentNotAllowed");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_analyzes_a_conditional_raise_event_family_despite_a_same_named_local()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalShadowedRaiseEvent.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalShadowedRaiseEvent\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Event Saved(ByRef Message As String)",
            "#Else",
            "Public Event saved(ByRef Text As String)",
            "#End If",
            "Public Sub Fire()",
            "    Dim Saved As Variant",
            "    Dim number As Long",
            "    RaiseEvent Saved(number)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.Contains(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_accepts_scalar_elements_for_every_conditional_paramarray_signature()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalParamArrayCall.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalParamArrayCall\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Collect(ParamArray Values() As Variant)",
            "End Sub",
            "#Else",
            "Public Sub collect(ParamArray Items() As Variant)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Collect 1&, 2&",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_accepts_direct_storage_as_a_conditional_paramarray_element()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalParamArrayStorage.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalParamArrayStorage\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Collect(ParamArray Values() As Variant)",
            "End Sub",
            "#Else",
            "Public Sub collect(ParamArray Items() As Variant)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Dim item As String",
            "    Collect item",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_diagnoses_proven_byref_storage_type_mismatches_for_every_variant()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalByRefMismatch.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalByRefMismatch\"",
            "#If STRING_CONFIGURATION Then",
            "Public Function ResolveValue(ByRef Value As String) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByRef Value As Date) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim value As Long",
            "    Dim result As String",
            "    result = ResolveValue(value)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.Contains(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_diagnoses_array_shape_independently_of_unresolved_types()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalIndependentArrayShape.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalIndependentArrayShape\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByRef Values() As MissingType)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef Values() As MissingType)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Dim value As MissingType",
            "    Work value",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.All(messages, message => Assert.Contains(
            "argument 1 for parameter 'Values' ByRef array shape: expected array, found scalar.",
            message,
            StringComparison.Ordinal));
        Assert.DoesNotContain(
            messages,
            message => message.Contains(" type:", StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_treats_a_member_argument_as_direct_storage()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string holderUri = "file:///C:/work/ConditionalStorageHolder.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(holderUri, string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"ConditionalStorageHolder\"",
                "Public Value As Long"
            ])));
        const string callerUri = "file:///C:/work/ConditionalMemberStorage.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalMemberStorage\"",
            "#If STRING_CONFIGURATION Then",
            "Public Sub Work(ByRef InputValue As String)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef InputValue As Date)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Dim holder As ConditionalStorageHolder",
            "    Work holder.Value",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var memberCompletion = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/completion",
            callerUri,
            callerText,
            "    Work holder.Value",
            "    Work holder.".Length);
        Assert.Contains(
            memberCompletion.GetProperty("result").EnumerateArray(),
            item => item.GetProperty("label").GetString() == "Value");

        var notification = await process.WaitForDiagnosticsAsync(callerUri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.Contains(
            "argument 1 for parameter 'InputValue' ByRef type: expected String, found Long.",
            messages[0],
            StringComparison.Ordinal);
        Assert.Contains(
            "argument 1 for parameter 'InputValue' ByRef type: expected Date, found Long.",
            messages[1],
            StringComparison.Ordinal);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_treats_a_module_qualified_indexed_array_element_as_direct_storage()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string dataUri = "file:///C:/work/Data.bas";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(dataUri, string.Join('\n', [
                "Attribute VB_Name = \"Data\"",
                "Public Values() As Long"
            ])));
        const string callerUri = "file:///C:/work/ConditionalQualifiedIndexedStorage.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalQualifiedIndexedStorage\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByRef Value As String)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef Value As Date)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Work Data.Values(0)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var notification = await process.WaitForDiagnosticsAsync(callerUri);
        Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_treats_an_indexed_array_element_as_direct_storage()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalIndexedStorage.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalIndexedStorage\"",
            "#If STRING_CONFIGURATION Then",
            "Public Sub Work(ByRef InputValue As String)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef InputValue As Date)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Dim values() As Long",
            "    Work values(0)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.Contains(
            "argument 1 for parameter 'InputValue' ByRef type: expected String, found Long.",
            messages[0],
            StringComparison.Ordinal);
        Assert.Contains(
            "argument 1 for parameter 'InputValue' ByRef type: expected Date, found Long.",
            messages[1],
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            messages,
            message => message.Contains("array shape", StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_treats_a_multidimensional_array_element_as_direct_storage()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalMultidimensionalIndexedStorage.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalMultidimensionalIndexedStorage\"",
            "#If STRING_CONFIGURATION Then",
            "Public Sub Work(ByRef InputValue As String)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef InputValue As Date)",
            "End Sub",
            "#End If",
            "Private Values(0 To 1, 0 To 1) As Long",
            "Public Sub Run()",
            "    Work Values(0, 0)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_treats_a_leading_dot_With_member_as_direct_storage()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string holderUri = "file:///C:/work/Holder.cls";
        var holderText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Holder\"",
            "Public Value As Long"
        ]);
        const string callerUri = "file:///C:/work/ConditionalWithMemberStorage.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalWithMemberStorage\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByRef Value As String)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef Value As String)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Dim holder As Holder",
            "    With holder",
            "        Work .Value",
            "    End With",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(holderUri, holderText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var notification = await process.WaitForDiagnosticsAsync(callerUri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.All(messages, message => Assert.Contains(
            "argument 1 for parameter 'Value' ByRef type: expected String, found Long.",
            message,
            StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_resolves_an_explicit_Call_to_a_leading_dot_With_member()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string holderUri = "file:///C:/work/ConditionalCallHolder.cls";
        var holderText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalCallHolder\"",
            "#If STRING_CONFIGURATION Then",
            "Public Sub Work(ByVal Key As String)",
            "End Sub",
            "#Else",
            "Public Sub work(ByVal Key As Long)",
            "End Sub",
            "#End If"
        ]);
        const string callerUri = "file:///C:/work/ConditionalLeadingDotCall.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalLeadingDotCall\"",
            "Public Sub Run()",
            "    Dim holder As ConditionalCallHolder",
            "    With holder",
            "        Call .Work(",
            "    End With",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(holderUri, holderText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            callerUri,
            callerText,
            "        Call .Work(",
            "        Call .Work(".Length);

        Assert.Equal(
            [
                "Sub Work(Key As String) [#If]",
                "Sub work(Key As Long) [#If]"
            ],
            response
                .GetProperty("result")
                .GetProperty("signatures")
                .EnumerateArray()
                .Select(signature => signature.GetProperty("label").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_retains_the_written_name_in_named_argument_type_reasons()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalNamedByRefMismatch.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalNamedByRefMismatch\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function ResolveValue(ByRef Key As String) As Long",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByRef Key As Date) As Long",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim number As Long",
            "    Dim result As Long",
            "    result = ResolveValue(Key:=number)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Contains(
            "argument 1 ('Key') for parameter 'Key' ByRef type: expected String, found Long.",
            messages[0],
            StringComparison.Ordinal);
        Assert.Contains(
            "argument 1 ('Key') for parameter 'Key' ByRef type: expected Date, found Long.",
            messages[1],
            StringComparison.Ordinal);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_treats_a_callable_result_argument_as_a_value_temporary()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalCallableArgumentResult.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalCallableArgumentResult\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByRef Value As Object)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef Value As Object)",
            "End Sub",
            "#End If",
            "Public Function BuildNumber() As Long",
            "End Function",
            "Public Sub Run()",
            "    Work BuildNumber",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.All(messages, message => Assert.Contains(
            "argument 1 for parameter 'Value' type: expected Object, found Long.",
            message,
            StringComparison.Ordinal));
        Assert.DoesNotContain(
            messages,
            message => message.Contains("ByRef type", StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_uses_only_readable_property_accessors_for_callable_result_evidence()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalPropertyResultArgument.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalPropertyResultArgument\"",
            "Public Property Get SourceValue() As Long",
            "End Property",
            "Public Property Let SourceValue(ByVal assigned As Long)",
            "End Property",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByRef Value As Object)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef Value As Object)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Work SourceValue",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.All(messages, message => Assert.Contains(
            "argument 1 for parameter 'Value' type: expected Object, found Long.",
            message,
            StringComparison.Ordinal));
        Assert.DoesNotContain(
            messages,
            message => message.Contains("ByRef type", StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Theory]
    [InlineData("factory.BuildNumber")]
    [InlineData("factory.BuildNumber()")]
    public async Task Server_treats_a_member_callable_result_as_a_value_temporary(
        string argumentExpression)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string factoryUri = "file:///C:/work/Factory.cls";
        var factoryText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Factory\"",
            "Public Function BuildNumber() As Long",
            "End Function"
        ]);
        const string callerUri = "file:///C:/work/ConditionalMemberCallableResult.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalMemberCallableResult\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByRef Value As Object)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef Value As Object)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Dim factory As Factory",
            $"    Work {argumentExpression}",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(factoryUri, factoryText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var notification = await process.WaitForDiagnosticsAsync(callerUri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.All(messages, message => Assert.Contains(
            "argument 1 for parameter 'Value' type: expected Object, found Long.",
            message,
            StringComparison.Ordinal));
        Assert.DoesNotContain(
            messages,
            message => message.Contains("ByRef type", StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_treats_a_qualified_type_character_callable_result_as_a_value_temporary()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string factoryUri = "file:///C:/work/TypeCharacterFactory.cls";
        var factoryText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"TypeCharacterFactory\"",
            "Public Function BuildNumber&()",
            "End Function"
        ]);
        const string callerUri = "file:///C:/work/ConditionalQualifiedTypeCharacterCallableResult.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalQualifiedTypeCharacterCallableResult\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByRef Value As Object)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef Value As Object)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Dim factory As TypeCharacterFactory",
            "    Work factory.BuildNumber&",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(factoryUri, factoryText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var notification = await process.WaitForDiagnosticsAsync(callerUri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.All(messages, message => Assert.Contains(
            "argument 1 for parameter 'Value' type: expected Object, found Long.",
            message,
            StringComparison.Ordinal));
        Assert.DoesNotContain(
            messages,
            message => message.Contains("ByRef type", StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_treats_a_parameterized_member_callable_result_as_a_value_temporary()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string factoryUri = "file:///C:/work/ParameterizedFactory.cls";
        var factoryText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ParameterizedFactory\"",
            "Public Function BuildNumber(ByVal Key As Long) As Long",
            "End Function"
        ]);
        const string callerUri = "file:///C:/work/ConditionalParameterizedMemberCallableResult.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalParameterizedMemberCallableResult\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByRef Value As Object)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef Value As Object)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Dim factory As ParameterizedFactory",
            "    Work factory.BuildNumber(1&)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(factoryUri, factoryText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var notification = await process.WaitForDiagnosticsAsync(callerUri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.All(messages, message => Assert.Contains(
            "argument 1 for parameter 'Value' type: expected Object, found Long.",
            message,
            StringComparison.Ordinal));
        Assert.DoesNotContain(
            messages,
            message => message.Contains("ByRef type", StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_uses_only_a_parameterized_property_get_for_callable_result_evidence()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string catalogUri = "file:///C:/work/ParameterizedCatalog.cls";
        var catalogText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ParameterizedCatalog\"",
            "Public Property Get Item(ByVal Key As Long) As Long",
            "End Property",
            "Public Property Let Item(ByVal Key As Long, ByVal AssignedValue As Long)",
            "End Property"
        ]);
        const string callerUri = "file:///C:/work/ConditionalParameterizedPropertyResult.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalParameterizedPropertyResult\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByRef Value As Object)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef Value As Object)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Dim catalog As ParameterizedCatalog",
            "    Work catalog.Item(1&)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(catalogUri, catalogText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var notification = await process.WaitForDiagnosticsAsync(callerUri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.All(messages, message => Assert.Contains(
            "argument 1 for parameter 'Value' type: expected Object, found Long.",
            message,
            StringComparison.Ordinal));
        Assert.DoesNotContain(
            messages,
            message => message.Contains("ByRef type", StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_does_not_select_a_parameterized_conditional_member_result_for_argument_evidence()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string factoryUri = "file:///C:/work/ConditionalParameterizedFactory.cls";
        var factoryText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalParameterizedFactory\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function Build(ByVal Key As Long) As Long",
            "End Function",
            "#Else",
            "Public Function build(ByVal Key As Long) As String",
            "End Function",
            "#End If"
        ]);
        const string callerUri = "file:///C:/work/ConditionalParameterizedMemberResultEvidence.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalParameterizedMemberResultEvidence\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByRef Value As Object)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef Value As Object)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Dim factory As ConditionalParameterizedFactory",
            "    Work factory.Build(1&)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(factoryUri, factoryText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var notification = await process.WaitForDiagnosticsAsync(callerUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_does_not_publish_result_evidence_from_a_visible_callable_subset()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string factoryUri = "file:///C:/work/ConditionalVisibilityFactory.cls";
        var factoryText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalVisibilityFactory\"",
            "#If PUBLIC_CONFIGURATION Then",
            "Public Function Build(ByVal Key As Long) As Long",
            "End Function",
            "#Else",
            "Private Function build(ByVal Key As Long) As String",
            "End Function",
            "#End If"
        ]);
        const string callerUri = "file:///C:/work/ConditionalVisibleSubsetResultEvidence.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalVisibleSubsetResultEvidence\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByRef Value As Object)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef Value As Object)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Dim factory As ConditionalVisibilityFactory",
            "    Work factory.Build(1&)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(factoryUri, factoryText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var notification = await process.WaitForDiagnosticsAsync(callerUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("()")]
    public async Task Server_does_not_publish_zero_argument_result_evidence_from_a_visible_callable_subset(
        string callSuffix)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string factoryUri = "file:///C:/work/ConditionalZeroArgumentVisibilityFactory.cls";
        var factoryText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalZeroArgumentVisibilityFactory\"",
            "#If PUBLIC_CONFIGURATION Then",
            "Public Function BuildNumber() As Long",
            "End Function",
            "#Else",
            "Private Function buildnumber() As String",
            "End Function",
            "#End If"
        ]);
        const string callerUri = "file:///C:/work/ConditionalZeroArgumentVisibleSubsetResultEvidence.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalZeroArgumentVisibleSubsetResultEvidence\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByRef Value As Object)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef Value As Object)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Dim factory As ConditionalZeroArgumentVisibilityFactory",
            $"    Work factory.BuildNumber{callSuffix}",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(factoryUri, factoryText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var notification = await process.WaitForDiagnosticsAsync(callerUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_treats_a_type_character_argument_as_direct_storage()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string uri = "file:///C:/work/ConditionalTypeCharacterStorage.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalTypeCharacterStorage\"",
            "#If STRING_CONFIGURATION Then",
            "Public Sub Work(ByRef Value As String)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef Value As Date)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Dim value&",
            "    Work value&",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.Contains(
            "Candidate signature: Sub Work(ByRef Value As String) [#If]. Mismatches: "
                + "argument 1 for parameter 'Value' ByRef type: expected String, found Long.",
            messages);
        Assert.Contains(
            "Candidate signature: Sub work(ByRef Value As Date) [#If]. Mismatches: "
                + "argument 1 for parameter 'Value' ByRef type: expected Date, found Long.",
            messages);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_resolves_source_parameter_types_in_the_declaration_document()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string payloadUri = "file:///C:/work/Payload.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(payloadUri, string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Payload\""
            ])));
        const string contractsUri = "file:///C:/work/Contracts.bas";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(contractsUri, string.Join('\n', [
                "Attribute VB_Name = \"Contracts\"",
                "#If FIRST_CONFIGURATION Then",
                "Public Sub Work(ByRef Value As Payload)",
                "End Sub",
                "#Else",
                "Public Sub work(ByRef Value As Payload)",
                "End Sub",
                "#End If"
            ])));
        const string callerUri = "file:///C:/work/Caller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"Caller\"",
            "Private Type Payload",
            "    Member As Long",
            "End Type",
            "Public Sub Run()",
            "    Dim value As Payload",
            "    Work value",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var notification = await process.WaitForDiagnosticsAsync(callerUri);
        Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_preserves_an_array_marker_after_a_type_declaration_character()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalTypeCharacterArrayStorage.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalTypeCharacterArrayStorage\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByRef Values() As Long)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef Values() As Long)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Dim values&()",
            "    Work values&",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_does_not_infer_a_literal_type_from_an_expression_suffix()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalExpressionSuffix.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalExpressionSuffix\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByVal Value As Object)",
            "End Sub",
            "#Else",
            "Public Sub work(ByVal Value As Object)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Dim unknown As Variant",
            "    Work unknown + 1&",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_uses_value_compatibility_for_byval_and_parenthesized_byref_arguments()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string payloadUri = "file:///C:/work/Payload.cls";
        var payloadText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Payload\""
        ]);
        const string callerUri = "file:///C:/work/ConditionalValueCompatibility.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalValueCompatibility\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByVal First As Payload, ByRef Second As Payload)",
            "End Sub",
            "#Else",
            "Public Sub work(ByVal First As Payload, ByRef Second As Payload)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Dim text As String",
            "    Work text, (text)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(payloadUri, payloadText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var notification = await process.WaitForDiagnosticsAsync(callerUri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.All(messages, message => Assert.Contains(
            "Mismatches: argument 1 for parameter 'First' type: expected Payload, found String; "
                + "argument 2 for parameter 'Second' type: expected Payload, found String.",
            message,
            StringComparison.Ordinal));
        Assert.DoesNotContain(
            messages,
            message => message.Contains("ByRef type", StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_treats_a_whitespace_separated_parenthesized_statement_argument_as_a_value_temporary()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string payloadUri = "file:///C:/work/Payload.cls";
        var payloadText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Payload\""
        ]);
        const string callerUri = "file:///C:/work/ConditionalWhitespaceParenthesizedArgument.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalWhitespaceParenthesizedArgument\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByRef Value As Payload)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef Value As Payload)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Dim text As String",
            "    Work (text)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(payloadUri, payloadText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var notification = await process.WaitForDiagnosticsAsync(callerUri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.All(messages, message => Assert.Contains(
            "argument 1 for parameter 'Value' type: expected Payload, found String.",
            message,
            StringComparison.Ordinal));
        Assert.DoesNotContain(
            messages,
            message => message.Contains("ByRef type", StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_treats_parentheses_after_an_explicit_Call_as_argument_list_delimiters()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string payloadUri = "file:///C:/work/Payload.cls";
        var payloadText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Payload\""
        ]);
        const string callerUri = "file:///C:/work/ConditionalExplicitCallParentheses.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalExplicitCallParentheses\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByRef Value As Payload)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef Value As Payload)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Dim text As String",
            "    Call Work (text)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(payloadUri, payloadText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var notification = await process.WaitForDiagnosticsAsync(callerUri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.All(messages, message => Assert.Contains(
            "argument 1 for parameter 'Value' ByRef type: expected Payload, found String.",
            message,
            StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_peels_nested_parentheses_from_a_statement_value_temporary()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });
        const string payloadUri = "file:///C:/work/Payload.cls";
        var payloadText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Payload\""
        ]);
        const string callerUri = "file:///C:/work/ConditionalNestedParenthesizedArgument.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalNestedParenthesizedArgument\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByRef Value As Payload)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef Value As Payload)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Dim text As String",
            "    Work ((text))",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(payloadUri, payloadText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var notification = await process.WaitForDiagnosticsAsync(callerUri);
        var diagnostic = Assert.Single(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");
        var messages = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .Select(item => item.GetProperty("message").GetString()!)
            .ToArray();

        Assert.Equal(2, messages.Length);
        Assert.All(messages, message => Assert.Contains(
            "argument 1 for parameter 'Value' type: expected Payload, found String.",
            message,
            StringComparison.Ordinal));
        Assert.DoesNotContain(
            messages,
            message => message.Contains("ByRef type", StringComparison.Ordinal));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Server_does_not_select_a_conditional_argument_storage_type_for_call_compatibility()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalArgumentStorageType.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalArgumentStorageType\"",
            "#If LONG_VALUE_CONFIGURATION Then",
            "Private value As Long",
            "#Else",
            "Private value As String",
            "#End If",
            "#If STRING_CALL_CONFIGURATION Then",
            "Public Sub Work(ByRef inputValue As String)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef inputValue As Date)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Work value",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_keeps_unresolved_call_parameter_types_indeterminate()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalUnresolvedParameterType.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalUnresolvedParameterType\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Sub Work(ByRef inputValue As MissingFirstType)",
            "End Sub",
            "#Else",
            "Public Sub work(ByRef inputValue As MissingSecondType)",
            "End Sub",
            "#End If",
            "Public Sub Run()",
            "    Dim value As Long",
            "    Work value",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_stops_member_completion_when_conditional_result_types_diverge()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalDivergentResult.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalDivergentResult\"",
            "Public Type FirstResult",
            "    FirstOnly As String",
            "End Type",
            "Public Type SecondResult",
            "    SecondOnly As String",
            "End Type",
            "#If FIRST_CONFIGURATION Then",
            "Public Function BuildValue() As FirstResult",
            "End Function",
            "#Else",
            "Public Function buildvalue() As SecondResult",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Debug.Print BuildValue.",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/completion",
            uri,
            text,
            "    Debug.Print BuildValue.",
            "    Debug.Print BuildValue.".Length);
        var labels = response
            .GetProperty("result")
            .EnumerateArray()
            .Select(item => item.GetProperty("label").GetString())
            .ToArray();
        Assert.DoesNotContain("FirstOnly", labels);
        Assert.DoesNotContain("SecondOnly", labels);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_stops_zero_argument_result_completion_when_a_variant_is_private_at_the_use_site()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string providerUri = "file:///C:/work/ConditionalZeroArgumentProvider.bas";
        var providerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalZeroArgumentProvider\"",
            "Public Type SharedResult",
            "    SharedMember As String",
            "End Type",
            "#If FIRST_CONFIGURATION Then",
            "Private Function BuildValue() As SharedResult",
            "End Function",
            "#Else",
            "Public Function buildvalue() As SharedResult",
            "End Function",
            "#End If"
        ]);
        const string callerUri = "file:///C:/work/ConditionalZeroArgumentCaller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalZeroArgumentCaller\"",
            "Public Sub Run()",
            "    Debug.Print BuildValue.",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(providerUri, providerText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/completion",
            callerUri,
            callerText,
            "    Debug.Print BuildValue.",
            "    Debug.Print BuildValue.".Length);
        Assert.DoesNotContain(
            response.GetProperty("result").EnumerateArray(),
            item => item.GetProperty("label").GetString() == "SharedMember");

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_completes_members_for_an_applicable_nonzero_conditional_call_with_convergent_results()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalConvergentCallResult.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalConvergentCallResult\"",
            "Public Type SharedResult",
            "    SharedMember As String",
            "End Type",
            "#If FIRST_CONFIGURATION Then",
            "Public Function BuildValue(ByVal Key As Long) As SharedResult",
            "End Function",
            "#Else",
            "Public Function buildvalue(ByVal Key As Long) As SharedResult",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Debug.Print BuildValue(1&).",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/completion",
            uri,
            text,
            "    Debug.Print BuildValue(1&).",
            "    Debug.Print BuildValue(1&).".Length);
        Assert.Contains(
            response.GetProperty("result").EnumerateArray(),
            item => item.GetProperty("label").GetString() == "SharedMember");

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_resolves_conditional_result_types_in_the_declaration_document()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string payloadUri = "file:///C:/work/ResultPayload.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(payloadUri, string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"ResultPayload\"",
                "Public Name As String"
            ])));
        const string contractsUri = "file:///C:/work/ResultContracts.bas";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(contractsUri, string.Join('\n', [
                "Attribute VB_Name = \"ResultContracts\"",
                "#If FIRST_CONFIGURATION Then",
                "Public Function Build() As ResultPayload",
                "End Function",
                "#Else",
                "Public Function build() As ResultPayload",
                "End Function",
                "#End If"
            ])));
        const string callerUri = "file:///C:/work/ResultCaller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ResultCaller\"",
            "Private Type ResultPayload",
            "    LocalOnly As Long",
            "End Type",
            "Public Sub Run()",
            "    Debug.Print Build().",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/completion",
            callerUri,
            callerText,
            "    Debug.Print Build().",
            "    Debug.Print Build().".Length);
        var labels = response
            .GetProperty("result")
            .EnumerateArray()
            .Select(item => item.GetProperty("label").GetString())
            .ToArray();

        Assert.Contains("Name", labels);
        Assert.DoesNotContain("LocalOnly", labels);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_completes_members_from_parameterized_conditional_Property_Get_despite_complementary_setters()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string resultUri = "file:///C:/work/ResultRecord.cls";
        var resultText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ResultRecord\"",
            "Public SharedMember As String"
        ]);
        const string catalogUri = "file:///C:/work/ConditionalCatalog.cls";
        var catalogText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalCatalog\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Property Get Item(ByVal Key As Long) As ResultRecord",
            "End Property",
            "Public Property Set Item(ByVal Key As Long, ByVal Assigned As ResultRecord)",
            "End Property",
            "#Else",
            "Public Property Get item(ByVal Key As Long) As ResultRecord",
            "End Property",
            "Public Property Set item(ByVal Key As Long, ByVal Assigned As ResultRecord)",
            "End Property",
            "#End If"
        ]);
        const string callerUri = "file:///C:/work/ConditionalPropertyCompletionCaller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalPropertyCompletionCaller\"",
            "Public Sub Run()",
            "    Dim catalog As ConditionalCatalog",
            "    Debug.Print catalog.Item(1&).",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(resultUri, resultText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(catalogUri, catalogText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/completion",
            callerUri,
            callerText,
            "    Debug.Print catalog.Item(1&).",
            "    Debug.Print catalog.Item(1&).".Length);
        Assert.Contains(
            response.GetProperty("result").EnumerateArray(),
            item => item.GetProperty("label").GetString() == "SharedMember");

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_completes_members_from_zero_argument_conditional_Property_Get_despite_complementary_setters()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string resultUri = "file:///C:/work/ZeroArgumentResultRecord.cls";
        var resultText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ZeroArgumentResultRecord\"",
            "Public SharedMember As String"
        ]);
        const string catalogUri = "file:///C:/work/ConditionalZeroArgumentCatalog.cls";
        var catalogText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalZeroArgumentCatalog\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Property Get Item() As ZeroArgumentResultRecord",
            "End Property",
            "Public Property Set Item(ByVal Assigned As ZeroArgumentResultRecord)",
            "End Property",
            "#Else",
            "Public Property Get item() As ZeroArgumentResultRecord",
            "End Property",
            "Public Property Set item(ByVal Assigned As ZeroArgumentResultRecord)",
            "End Property",
            "#End If"
        ]);
        const string callerUri = "file:///C:/work/ConditionalZeroArgumentPropertyCompletionCaller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalZeroArgumentPropertyCompletionCaller\"",
            "Public Sub Run()",
            "    Dim catalog As ConditionalZeroArgumentCatalog",
            "    Debug.Print catalog.Item.",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(resultUri, resultText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(catalogUri, catalogText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/completion",
            callerUri,
            callerText,
            "    Debug.Print catalog.Item.",
            "    Debug.Print catalog.Item.".Length);
        Assert.Contains(
            response.GetProperty("result").EnumerateArray(),
            item => item.GetProperty("label").GetString() == "SharedMember");

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_completes_members_after_modeled_byval_variant_coercion()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalVariantCallResult.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalVariantCallResult\"",
            "Public Type SharedResult",
            "    SharedMember As String",
            "End Type",
            "#If FIRST_CONFIGURATION Then",
            "Public Function BuildValue(ByVal Key As Variant) As SharedResult",
            "End Function",
            "#Else",
            "Public Function buildvalue(ByVal Key As Variant) As SharedResult",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Debug.Print BuildValue(1&).",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/completion",
            uri,
            text,
            "    Debug.Print BuildValue(1&).",
            "    Debug.Print BuildValue(1&).".Length);
        Assert.Contains(
            response.GetProperty("result").EnumerateArray(),
            item => item.GetProperty("label").GetString() == "SharedMember");

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_treats_an_omitted_source_parameter_type_as_variant()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalImplicitVariantCallResult.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalImplicitVariantCallResult\"",
            "Public Type SharedResult",
            "    SharedMember As String",
            "End Type",
            "#If FIRST_CONFIGURATION Then",
            "Public Function BuildValue(ByVal Key) As SharedResult",
            "End Function",
            "#Else",
            "Public Function buildvalue(ByVal Key) As SharedResult",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Debug.Print BuildValue(1&).",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/completion",
            uri,
            text,
            "    Debug.Print BuildValue(1&).",
            "    Debug.Print BuildValue(1&).".Length);
        Assert.Contains(
            response.GetProperty("result").EnumerateArray(),
            item => item.GetProperty("label").GetString() == "SharedMember");

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_completes_members_after_a_proven_numeric_widening()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalNumericWideningResult.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalNumericWideningResult\"",
            "Public Type SharedResult",
            "    SharedMember As String",
            "End Type",
            "#If FIRST_CONFIGURATION Then",
            "Public Function BuildValue(ByVal Key As Long) As SharedResult",
            "End Function",
            "#Else",
            "Public Function buildvalue(ByVal Key As Long) As SharedResult",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim key As Integer",
            "    Debug.Print BuildValue(key).",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/completion",
            uri,
            text,
            "    Debug.Print BuildValue(key).",
            "    Debug.Print BuildValue(key).".Length);
        Assert.Contains(
            response.GetProperty("result").EnumerateArray(),
            item => item.GetProperty("label").GetString() == "SharedMember");

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_keeps_an_unmodeled_scalar_coercion_indeterminate()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalUnmodeledCoercionResult.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalUnmodeledCoercionResult\"",
            "Public Type SharedResult",
            "    SharedMember As String",
            "End Type",
            "#If FIRST_CONFIGURATION Then",
            "Public Function BuildValue(ByVal Key As Long) As SharedResult",
            "End Function",
            "#Else",
            "Public Function buildvalue(ByVal Key As Long) As SharedResult",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim key As String",
            "    Debug.Print BuildValue(key).",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var notification = await process.WaitForDiagnosticsAsync(uri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleCallArgumentList");

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/completion",
            uri,
            text,
            "    Debug.Print BuildValue(key).",
            "    Debug.Print BuildValue(key).".Length);
        Assert.DoesNotContain(
            response.GetProperty("result").EnumerateArray(),
            item => item.GetProperty("label").GetString() == "SharedMember");

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_stops_result_member_completion_when_one_nonzero_conditional_call_variant_is_inapplicable()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalInapplicableCallResult.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalInapplicableCallResult\"",
            "Public Type SharedResult",
            "    SharedMember As String",
            "End Type",
            "#If FIRST_CONFIGURATION Then",
            "Public Function BuildValue(ByRef Key As Long) As SharedResult",
            "End Function",
            "#Else",
            "Public Function buildvalue(ByRef Key As String) As SharedResult",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim keyValue As Long",
            "    Debug.Print BuildValue(keyValue).",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/completion",
            uri,
            text,
            "    Debug.Print BuildValue(keyValue).",
            "    Debug.Print BuildValue(keyValue).".Length);
        Assert.DoesNotContain(
            response.GetProperty("result").EnumerateArray(),
            item => item.GetProperty("label").GetString() == "SharedMember");

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_stops_result_member_completion_when_nonzero_conditional_results_diverge()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalDivergentNonzeroCallResult.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalDivergentNonzeroCallResult\"",
            "Public Type FirstResult",
            "    FirstOnly As String",
            "End Type",
            "Public Type SecondResult",
            "    SecondOnly As String",
            "End Type",
            "#If FIRST_CONFIGURATION Then",
            "Public Function BuildValue(ByVal Key As Long) As FirstResult",
            "End Function",
            "#Else",
            "Public Function buildvalue(ByVal Key As Long) As SecondResult",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Debug.Print BuildValue(1&).",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/completion",
            uri,
            text,
            "    Debug.Print BuildValue(1&).",
            "    Debug.Print BuildValue(1&).".Length);
        var labels = response
            .GetProperty("result")
            .EnumerateArray()
            .Select(item => item.GetProperty("label").GetString())
            .ToArray();
        Assert.DoesNotContain("FirstOnly", labels);
        Assert.DoesNotContain("SecondOnly", labels);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_does_not_select_a_conditional_member_callable_result_type()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string typesUri = "file:///C:/work/ConditionalMemberResultTypes.bas";
        var typesText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalMemberResultTypes\"",
            "Public Type FirstResult",
            "    FirstOnly As String",
            "End Type",
            "Public Type SecondResult",
            "    SecondOnly As String",
            "End Type"
        ]);
        const string classUri = "file:///C:/work/ConditionalResultWorker.cls";
        var classText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalResultWorker\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function BuildValue() As FirstResult",
            "End Function",
            "#Else",
            "Public Function buildvalue() As SecondResult",
            "End Function",
            "#End If"
        ]);
        const string callerUri = "file:///C:/work/ConditionalMemberResultCaller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalMemberResultCaller\"",
            "Public Sub Run()",
            "    Dim worker As ConditionalResultWorker",
            "    Debug.Print worker.BuildValue.",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(typesUri, typesText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(classUri, classText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/completion",
            callerUri,
            callerText,
            "    Debug.Print worker.BuildValue.",
            "    Debug.Print worker.BuildValue.".Length);
        var labels = response
            .GetProperty("result")
            .EnumerateArray()
            .Select(item => item.GetProperty("label").GetString())
            .ToArray();
        Assert.DoesNotContain("FirstOnly", labels);
        Assert.DoesNotContain("SecondOnly", labels);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_returns_hover_and_signature_help_for_source_callables()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "",
            "'* @brief Reads a value.",
            "'* @param Key Key to read.",
            "'* @param Fallback Value used when the key is missing.",
            "'* @return The configured value.",
            "Public Function ReadValue(ByVal Key As String, Optional ByVal Fallback As String) As String",
            "End Function",
            "Public Sub PlainSub(ByVal Arg1 As String)",
            "End Sub",
            "",
            "Public Sub Run()",
            "    ReadValue(\"id\", ",
            "    ReadValue ",
            "    ReadValue \"id\", ",
            "    PlainSub(",
            "End Sub"
        ]);
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

        var hover = await SendPositionRequestAsync(process, 2, "textDocument/hover", uri, text, "ReadValue(\"id\"");
        var hoverValue = hover
            .GetProperty("result")
            .GetProperty("contents")
            .GetProperty("value")
            .GetString();
        Assert.Contains("Reads a value.", hoverValue);
        Assert.EndsWith(
            "---\n\n```vba\nFunction ReadValue(Key As String, [Fallback As String]) As String\n```",
            hoverValue,
            StringComparison.Ordinal);

        var signature = await SendPositionRequestAsync(process, 3, "textDocument/signatureHelp", uri, text, "ReadValue(\"id\", ", "ReadValue(\"id\", ".Length);
        var result = signature.GetProperty("result");
        Assert.Equal(1, result.GetProperty("activeParameter").GetInt32());
        var firstSignature = result.GetProperty("signatures").EnumerateArray().Single();
        Assert.Equal(
            "Function ReadValue(Key As String, [Fallback As String]) As String",
            firstSignature.GetProperty("label").GetString());
        Assert.False(firstSignature.TryGetProperty("documentation", out _));
        var parameters = firstSignature.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal("Key As String", parameters[0].GetProperty("label").GetString());
        Assert.Contains("Key to read.", parameters[0].GetProperty("documentation").GetProperty("value").GetString());
        Assert.Equal("[Fallback As String]", parameters[1].GetProperty("label").GetString());
        Assert.Contains("Value used when the key is missing.", parameters[1].GetProperty("documentation").GetProperty("value").GetString());

        var statementSignature = await SendPositionRequestAsync(process, 4, "textDocument/signatureHelp", uri, text, "ReadValue ", "ReadValue ".Length);
        var statementResult = statementSignature.GetProperty("result");
        Assert.Equal(0, statementResult.GetProperty("activeParameter").GetInt32());
        Assert.Equal(
            "Function ReadValue(Key As String, [Fallback As String]) As String",
            statementResult.GetProperty("signatures").EnumerateArray().Single().GetProperty("label").GetString());

        var statementSecondParameter = await SendPositionRequestAsync(process, 5, "textDocument/signatureHelp", uri, text, "ReadValue \"id\", ", "ReadValue \"id\", ".Length);
        Assert.Equal(1, statementSecondParameter.GetProperty("result").GetProperty("activeParameter").GetInt32());

        var undocumentedSignature = await SendPositionRequestAsync(
            process,
            6,
            "textDocument/signatureHelp",
            uri,
            text,
            "    PlainSub(",
            "    PlainSub(".Length);
        var undocumentedFirstSignature = undocumentedSignature
            .GetProperty("result")
            .GetProperty("signatures")
            .EnumerateArray()
            .Single();
        Assert.False(undocumentedFirstSignature.TryGetProperty("documentation", out _));
        Assert.DoesNotContain(
            undocumentedFirstSignature.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.TryGetProperty("documentation", out _));

        await process.ShutdownAsync(7);
    }

    [Fact]
    public async Task Server_preserves_rich_declaration_metadata_for_documented_source_callables()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        var uri = ToFileUri(Path.Combine(Path.GetTempPath(), $"vba-ls-rich-hover-{Guid.NewGuid():N}", "Mod_Example.bas"));
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Mod_Example\"",
            "Option Explicit",
            "",
            "Public Sub ExampleSub()",
            "    Dim example_var As String",
            "    example_var = ExampleFunc(Arg2:=True)",
            "End Sub",
            "",
            "'* Example of a function.",
            "'*",
            "'* @param Arg1 Example of a required argument.",
            "'* @param Arg2 Example of an optional argument.",
            "'* @returns Example of a return value.",
            "'*",
            "'* @details",
            "'* This is an example of a function that has a required argument and an optional argument.",
            "Public Function ExampleFunc(ByRef Arg1 As Long, Optional Arg2 As Boolean = False) As String",
            "End Function"
        ]);
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

        var hover = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/hover",
            uri,
            text,
            "ExampleFunc(Arg2:=True");
        var hoverValue = hover
            .GetProperty("result")
            .GetProperty("contents")
            .GetProperty("value")
            .GetString();
        Assert.Contains("Example of a function.", hoverValue, StringComparison.Ordinal);
        Assert.EndsWith(
            "---\n\n```vba\nFunction ExampleFunc(ByRef Arg1 As Long, [ByRef Arg2 As Boolean]) As String\n```",
            hoverValue,
            StringComparison.Ordinal);

        var signature = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/signatureHelp",
            uri,
            text,
            "ExampleFunc(Arg2:=True",
            "ExampleFunc(Arg2:=True".Length);
        var firstSignature = signature
            .GetProperty("result")
            .GetProperty("signatures")
            .EnumerateArray()
            .Single();
        Assert.Equal(
            "Function ExampleFunc(ByRef Arg1 As Long, [ByRef Arg2 As Boolean]) As String",
            firstSignature.GetProperty("label").GetString());
        Assert.False(firstSignature.TryGetProperty("documentation", out _));
        var parameters = firstSignature.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal("ByRef Arg1 As Long", parameters[0].GetProperty("label").GetString());
        Assert.Equal("[ByRef Arg2 As Boolean]", parameters[1].GetProperty("label").GetString());
        Assert.Contains(
            "Example of a required argument.",
            parameters[0].GetProperty("documentation").GetProperty("value").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "Example of an optional argument.",
            parameters[1].GetProperty("documentation").GetProperty("value").GetString(),
            StringComparison.Ordinal);

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_preserves_rich_declarations_for_function_and_statement_sub_calls()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        var uri = ToFileUri(Path.Combine(Path.GetTempPath(), $"vba-ls-rich-calls-{Guid.NewGuid():N}", "Mod_Example.bas"));
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Mod_Example\"",
            "Option Explicit",
            "",
            "Public Sub Main()",
            "    Dim example_var As String",
            "    example_var = ExampleFunc(1, Arg3:=True)",
            "    ExampleSub 1, Arg3:=True",
            "End Sub",
            "",
            "'* Example of a function.",
            "'*",
            "'* @param Arg1 Example of a required argument.",
            "'* @param Arg2 Example of an optional argument.",
            "'* @param Arg3 Example of another optional argument.",
            "'* @return Example of a return value.",
            "'*",
            "'* @details",
            "'* This is an example of a function that has a required argument and an optional argument.",
            "Public Function ExampleFunc(ByVal Arg1 As Long, Optional ByVal Arg2 As Boolean = False, Optional ByVal Arg3 As Boolean = False) As String",
            "End Function",
            "",
            "'* Example of a subroutine.",
            "'*",
            "'* @param[out] Arg1 Example of a required argument.",
            "'* @param[in] Arg2 Example of an optional argument.",
            "'* @param[in] Arg3 Example of another optional argument.",
            "'*",
            "'* @details",
            "'* This is an example of a subroutine that has a required argument and an optional argument.",
            "Public Sub ExampleSub(ByRef Arg1 As Long, Optional ByVal Arg2 As Boolean = False, Optional ByVal Arg3 As Boolean = False)",
            "End Sub"
        ]);
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

        const string functionLabel =
            "Function ExampleFunc(Arg1 As Long, [Arg2 As Boolean], [Arg3 As Boolean]) As String";
        var functionHover = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/hover",
            uri,
            text,
            "ExampleFunc(1, Arg3:=True");
        var functionHoverValue = functionHover
            .GetProperty("result")
            .GetProperty("contents")
            .GetProperty("value")
            .GetString();
        Assert.Contains("Example of a function.", functionHoverValue, StringComparison.Ordinal);
        Assert.EndsWith(
            $"---\n\n```vba\n{functionLabel}\n```",
            functionHoverValue,
            StringComparison.Ordinal);

        var functionSignature = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/signatureHelp",
            uri,
            text,
            "ExampleFunc(1, Arg3:=True",
            "ExampleFunc(1, Arg3:=True".Length);
        var firstFunctionSignature = functionSignature
            .GetProperty("result")
            .GetProperty("signatures")
            .EnumerateArray()
            .Single();
        Assert.Equal(2, functionSignature.GetProperty("result").GetProperty("activeParameter").GetInt32());
        Assert.Equal(functionLabel, firstFunctionSignature.GetProperty("label").GetString());
        Assert.False(firstFunctionSignature.TryGetProperty("documentation", out _));
        var functionParameters = firstFunctionSignature.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal(
            ["Arg1 As Long", "[Arg2 As Boolean]", "[Arg3 As Boolean]"],
            functionParameters.Select(parameter => parameter.GetProperty("label").GetString() ?? "").ToArray());
        Assert.Equal(
            "Example of another optional argument.",
            functionParameters[2].GetProperty("documentation").GetProperty("value").GetString());

        const string subLabel =
            "Sub ExampleSub(ByRef Arg1 As Long, [Arg2 As Boolean], [Arg3 As Boolean])";
        var subHover = await SendPositionRequestAsync(
            process,
            4,
            "textDocument/hover",
            uri,
            text,
            "ExampleSub 1, Arg3:=True");
        var subHoverValue = subHover
            .GetProperty("result")
            .GetProperty("contents")
            .GetProperty("value")
            .GetString();
        Assert.Contains("Example of a subroutine.", subHoverValue, StringComparison.Ordinal);
        Assert.Contains(
            "@param[out] Arg1 Example of a required argument.",
            subHoverValue,
            StringComparison.Ordinal);
        Assert.Contains(
            "@param[in] Arg3 Example of another optional argument.",
            subHoverValue,
            StringComparison.Ordinal);
        Assert.EndsWith(
            $"---\n\n```vba\n{subLabel}\n```",
            subHoverValue,
            StringComparison.Ordinal);

        var subSignature = await SendPositionRequestAsync(
            process,
            5,
            "textDocument/signatureHelp",
            uri,
            text,
            "ExampleSub 1, Arg3:=True",
            "ExampleSub 1, Arg3:=True".Length);
        var firstSubSignature = subSignature
            .GetProperty("result")
            .GetProperty("signatures")
            .EnumerateArray()
            .Single();
        Assert.Equal(2, subSignature.GetProperty("result").GetProperty("activeParameter").GetInt32());
        Assert.Equal(subLabel, firstSubSignature.GetProperty("label").GetString());
        Assert.False(firstSubSignature.TryGetProperty("documentation", out _));
        var subParameters = firstSubSignature.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal(
            ["ByRef Arg1 As Long", "[Arg2 As Boolean]", "[Arg3 As Boolean]"],
            subParameters.Select(parameter => parameter.GetProperty("label").GetString() ?? "").ToArray());
        Assert.Equal(
            "Example of a required argument.",
            subParameters[0].GetProperty("documentation").GetProperty("value").GetString());
        Assert.Equal(
            "Example of another optional argument.",
            subParameters[2].GetProperty("documentation").GetProperty("value").GetString());

        var commaBoundarySignature = await SendPositionRequestAsync(
            process,
            6,
            "textDocument/signatureHelp",
            uri,
            text,
            "ExampleSub 1, Arg3:=True",
            "ExampleSub 1,".Length);
        Assert.Equal(
            2,
            commaBoundarySignature.GetProperty("result").GetProperty("activeParameter").GetInt32());

        await process.ShutdownAsync(7);
    }

    [Fact]
    public async Task Server_returns_rich_hover_declarations_for_source_definition_kinds()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        var uri = ToFileUri(Path.Combine(Path.GetTempPath(), $"vba-ls-rich-kinds-{Guid.NewGuid():N}", "Worker.cls"));
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Private WithEvents App As Excel.Application",
            "Private Const MaxCount As Long = 10",
            "Public Enum Status",
            "    StatusReady = 1",
            "End Enum",
            "Public Type CustomerRecord",
            "    Id As Long",
            "End Type",
            "Public Event Saved(ByVal Name As String, ByRef RetryCount As Long)",
            "Public Declare PtrSafe Function GetTickCount Lib \"kernel32\" () As Long",
            "Public Property Get DisplayName(Optional Fallback As String) As String",
            "End Property",
            "Public Sub Run(ByRef ExplicitByRef As String, ByVal ExplicitByVal As Long, ParamArray Values() As Variant)",
            "    Static localCount As Long",
            "    Dim header_names() As String",
            "End Sub"
        ]);
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

        var requestId = 2;
        async Task<string> HoverValueAsync(string needle, int offset = 0)
        {
            var hover = await SendPositionRequestAsync(
                process,
                requestId++,
                "textDocument/hover",
                uri,
                text,
                needle,
                offset);
            return hover.GetProperty("result").GetProperty("contents").GetProperty("value").GetString() ?? "";
        }

        Assert.Equal("```vba\nWithEvents App As Application\n```", await HoverValueAsync("App As"));
        Assert.Equal("```vba\nConst MaxCount As Long\n```", await HoverValueAsync("MaxCount"));
        Assert.Equal("```vba\nEnum Status\n```", await HoverValueAsync("Enum Status", "Enum ".Length));
        Assert.Equal("```vba\nStatusReady\n```", await HoverValueAsync("StatusReady"));
        Assert.Equal("```vba\nType CustomerRecord\n```", await HoverValueAsync("CustomerRecord"));
        Assert.Equal("```vba\nId As Long\n```", await HoverValueAsync("Id As"));
        Assert.Equal(
            "```vba\nEvent Saved(Name As String, ByRef RetryCount As Long)\n```",
            await HoverValueAsync("Saved("));
        Assert.Equal(
            "```vba\nDeclare Function GetTickCount() As Long\n```",
            await HoverValueAsync("GetTickCount"));
        Assert.Equal(
            "```vba\nProperty DisplayName([ByRef Fallback As String]) As String\n```",
            await HoverValueAsync("DisplayName"));
        Assert.Equal(
            "```vba\nSub Run(ByRef ExplicitByRef As String, ExplicitByVal As Long, ParamArray Values() As Variant)\n```",
            await HoverValueAsync("Run(ByRef"));
        Assert.Equal("```vba\nByRef ExplicitByRef As String\n```", await HoverValueAsync("ExplicitByRef"));
        Assert.Equal("```vba\nExplicitByVal As Long\n```", await HoverValueAsync("ExplicitByVal"));
        Assert.Equal("```vba\nParamArray Values() As Variant\n```", await HoverValueAsync("Values()"));
        Assert.Equal("```vba\nStatic localCount As Long\n```", await HoverValueAsync("localCount"));
        Assert.Equal("```vba\nheader_names() As String\n```", await HoverValueAsync("header_names"));

        await process.ShutdownAsync(requestId);
    }

    [Fact]
    public async Task Server_uses_active_reference_catalog_for_completion_hover_and_signature_help()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-catalog-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(
                projectRoot,
                "Microsoft Excel 16.0 Object Library",
                "Microsoft Scripting Runtime");

            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Option Explicit",
                "",
                "Public Sub Run()",
                "    Dim target As ",
                "    Excel.Application",
                "    Scripting.Dictionary",
                "    Excel.Run(",
                "End Sub"
            ]);
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

            var completion = await process.SendRequestAsync(2,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri },
                    position = new { line = 4, character = "    Dim target As ".Length }
                });
            var completionLabels = completion
                .GetProperty("result")
                .EnumerateArray()
                .Select(item => item.GetProperty("label").GetString())
                .ToArray();
            Assert.Contains("Application", completionLabels);
            Assert.Contains("Dictionary", completionLabels);

            var applicationHover = await SendPositionRequestAsync(process, 3, "textDocument/hover", uri, text, "Application");
            Assert.Contains(
                "Microsoft Excel application",
                applicationHover.GetProperty("result").GetProperty("contents").GetProperty("value").GetString(),
                StringComparison.Ordinal);

            var dictionaryHover = await SendPositionRequestAsync(process, 4, "textDocument/hover", uri, text, "Dictionary");
            Assert.Contains(
                "Microsoft Scripting Runtime",
                dictionaryHover.GetProperty("result").GetProperty("contents").GetProperty("value").GetString(),
                StringComparison.Ordinal);

            var signature = await SendPositionRequestAsync(process, 5, "textDocument/signatureHelp", uri, text, "Excel.Run(", "Excel.Run(".Length);
            var firstSignature = signature
                .GetProperty("result")
                .GetProperty("signatures")
                .EnumerateArray()
                .Single();
            Assert.Equal("Function Run(Macro, [Arg1])", firstSignature.GetProperty("label").GetString());
            Assert.Contains(
                "The macro or function to run.",
                firstSignature.GetProperty("parameters").EnumerateArray().First().GetProperty("documentation").GetProperty("value").GetString(),
                StringComparison.Ordinal);

            await process.ShutdownAsync(6);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_preserves_rich_declaration_metadata_from_reference_catalogs()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-catalog-rich-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory("vba-ls-catalog-rich-cache-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot, "Generated Library");
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity("Generated Library"),
                    new VbaProjectReferenceCatalog(
                        "Generated Library",
                        ["Generated"],
                        [
                            new VbaProjectReferenceDefinition(
                                "Generated Library",
                                "GeneratedType",
                                VbaSourceDefinitionKind.Class),
                            new VbaProjectReferenceDefinition(
                                "Generated Library",
                                "GeneratedValue",
                                VbaSourceDefinitionKind.Property,
                                "Returns a generated value.",
                                TypeReference: new VbaTypeReference("Variant"),
                                GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal),
                            new VbaProjectReferenceDefinition(
                                "Generated Library",
                                "RichMethod",
                                VbaSourceDefinitionKind.Procedure,
                                Documentation: "Runs a generated rich method.",
                                Signature: new VbaCallableSignature(
                                    "RichMethod(Required, OptionalValue)",
                                    [
                                        new VbaCallableParameter(
                                            "Required",
                                            "Required argument documentation.",
                                            TypeReference: new VbaTypeReference("Variant"),
                                            IsByRef: true),
                                        new VbaCallableParameter(
                                            "OptionalValue",
                                            IsOptional: true,
                                            TypeReference: new VbaTypeReference("String"),
                                            IsByRef: false)
                                    ],
                                    CallableKind: VbaCallableKind.Function),
                                ParentTypeName: "GeneratedType",
                                TypeReference: new VbaTypeReference("String"))
                        ])));

            await using var process = await LanguageServerProcessHarness.StartAsync(referenceCatalogCacheRoot: cacheRoot);

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "    Generated.GeneratedValue",
                "    Dim generatedObject As GeneratedType",
                "    generatedObject.RichMethod(",
                "End Sub"
            ]);
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");

            var valueHover = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/hover",
                uri,
                text,
                "Generated.GeneratedValue",
                "Generated.".Length);
            Assert.EndsWith(
                "---\n\n```vba\nProperty GeneratedValue As Variant\n```",
                valueHover.GetProperty("result").GetProperty("contents").GetProperty("value").GetString(),
                StringComparison.Ordinal);

            var methodHover = await SendPositionRequestAsync(process, 3, "textDocument/hover", uri, text, "RichMethod(");
            var methodHoverValue = methodHover.GetProperty("result").GetProperty("contents").GetProperty("value").GetString();
            Assert.Contains("Runs a generated rich method.", methodHoverValue, StringComparison.Ordinal);
            Assert.EndsWith(
                "---\n\n```vba\nFunction RichMethod(ByRef Required As Variant, [OptionalValue As String]) As String\n```",
                methodHoverValue,
                StringComparison.Ordinal);
            Assert.DoesNotContain("Required argument documentation.", methodHoverValue, StringComparison.Ordinal);

            var signatureHelp = await SendPositionRequestAsync(
                process,
                4,
                "textDocument/signatureHelp",
                uri,
                text,
                "RichMethod(",
                "RichMethod(".Length);
            var signature = signatureHelp
                .GetProperty("result")
                .GetProperty("signatures")
                .EnumerateArray()
                .Single();
            Assert.Equal(
                "Function RichMethod(ByRef Required As Variant, [OptionalValue As String]) As String",
                signature.GetProperty("label").GetString());
            Assert.Equal(
                ["ByRef Required As Variant", "[OptionalValue As String]"],
                signature
                    .GetProperty("parameters")
                    .EnumerateArray()
                    .Select(parameter => parameter.GetProperty("label").GetString() ?? "")
                    .ToArray());

            await process.ShutdownAsync(5);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_keeps_unknown_catalog_named_argument_support_indeterminate()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-catalog-unknown-named-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-catalog-unknown-named-cache-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot, "Generated Library");
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity("Generated Library"),
                    new VbaProjectReferenceCatalog(
                        "Generated Library",
                        ["Generated"],
                        [
                            new VbaProjectReferenceDefinition(
                                "Generated Library",
                                "GeneratedType",
                                VbaSourceDefinitionKind.Class),
                            new VbaProjectReferenceDefinition(
                                "Generated Library",
                                "FallbackMethod",
                                VbaSourceDefinitionKind.Procedure,
                                Signature: new VbaCallableSignature(
                                    "FallbackMethod(Value)",
                                    [
                                        new VbaCallableParameter(
                                            "Value",
                                            TypeReference: new VbaTypeReference("Long"),
                                            IsByRef: false)
                                    ],
                                    CallableKind: VbaCallableKind.Sub),
                                ParentTypeName: "GeneratedType")
                        ])));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.bas"));
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "    Dim generatedObject As GeneratedType",
                "    generatedObject.FallbackMethod(Value:=1)",
                "End Sub"
            ]);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");

            var checkpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            var notification = await process.WaitForMessageAsync(
                checkpoint,
                message => message.TryGetProperty("method", out var method)
                    && method.GetString() == "textDocument/publishDiagnostics"
                    && message.GetProperty("params").GetProperty("uri").GetString() == uri);
            Assert.DoesNotContain(
                notification
                    .GetProperty("params")
                    .GetProperty("diagnostics")
                    .EnumerateArray(),
                candidate => candidate.GetProperty("code").GetString()
                    == "validation.incompatibleCallArgumentList");

            var completion = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/completion",
                uri,
                text,
                "    generatedObject.FallbackMethod(Value:=1)",
                "    generatedObject.FallbackMethod(".Length);
            Assert.DoesNotContain(
                completion.GetProperty("result").EnumerateArray(),
                item => item.GetProperty("kind").GetInt32() == 5
                    && item.GetProperty("label").GetString() == "Value");

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_preserves_a_conclusively_missing_required_parameter_when_named_argument_support_is_unknown()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-catalog-unknown-named-missing-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-catalog-unknown-named-missing-cache-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot, "Generated Library");
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity("Generated Library"),
                    new VbaProjectReferenceCatalog(
                        "Generated Library",
                        ["Generated"],
                        [
                            new VbaProjectReferenceDefinition(
                                "Generated Library",
                                "Work",
                                VbaSourceDefinitionKind.Procedure,
                                Signature: new VbaCallableSignature(
                                    "Sub Work(ByVal First As Long, ByVal Second As Long)",
                                    [
                                        new VbaCallableParameter(
                                            "First",
                                            TypeReference: new VbaTypeReference("Long"),
                                            IsByRef: false),
                                        new VbaCallableParameter(
                                            "Second",
                                            TypeReference: new VbaTypeReference("Long"),
                                            IsByRef: false)
                                    ],
                                    CallableKind: VbaCallableKind.Sub),
                                GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal)
                        ])));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync(new
            {
                textDocument = new
                {
                    publishDiagnostics = new
                    {
                        relatedInformation = true
                    }
                }
            });
            var uri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.bas"));
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "    Work First:=1&",
                "End Sub"
            ]);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");

            var checkpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            var notification = await process.WaitForMessageAsync(
                checkpoint,
                message => message.TryGetProperty("method", out var method)
                    && method.GetString() == "textDocument/publishDiagnostics"
                    && message.GetProperty("params").GetProperty("uri").GetString() == uri);
            var diagnostic = Assert.Single(notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
                candidate => candidate.GetProperty("code").GetString()
                    == "validation.incompatibleCallArgumentList");

            Assert.Contains(
                "parameter 'Second': required argument is missing.",
                diagnostic.GetProperty("message").GetString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "parameter 'First': required argument is missing.",
                diagnostic.GetProperty("message").GetString(),
                StringComparison.Ordinal);

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_preserves_a_context_mismatch_when_named_argument_support_is_unknown()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-catalog-unknown-named-context-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-catalog-unknown-named-context-cache-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot, "Generated Library");
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity("Generated Library"),
                    new VbaProjectReferenceCatalog(
                        "Generated Library",
                        ["Generated"],
                        [
                            new VbaProjectReferenceDefinition(
                                "Generated Library",
                                "Work",
                                VbaSourceDefinitionKind.Procedure,
                                Signature: new VbaCallableSignature(
                                    "Work(Key)",
                                    [
                                        new VbaCallableParameter(
                                            "Key",
                                            TypeReference: new VbaTypeReference("Long"),
                                            IsByRef: false)
                                    ],
                                    CallableKind: VbaCallableKind.Sub),
                                GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal)
                        ])));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.bas"));
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "    Dim result As Long",
                "    result = Work(Key:=1&)",
                "End Sub"
            ]);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");

            var checkpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            var notification = await process.WaitForMessageAsync(
                checkpoint,
                message => message.TryGetProperty("method", out var method)
                    && method.GetString() == "textDocument/publishDiagnostics"
                    && message.GetProperty("params").GetProperty("uri").GetString() == uri);
            var diagnostic = Assert.Single(notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
                candidate => candidate.GetProperty("code").GetString()
                    == "validation.incompatibleCallArgumentList");

            Assert.Contains(
                "call context: expected Function or Property Get, found Sub.",
                diagnostic.GetProperty("message").GetString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "named arguments are not accepted",
                diagnostic.GetProperty("message").GetString(),
                StringComparison.Ordinal);

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_keeps_a_writable_catalog_property_accessor_kind_indeterminate()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-catalog-property-accessor-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-catalog-property-accessor-cache-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot, "Generated Library");
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity("Generated Library"),
                    new VbaProjectReferenceCatalog(
                        "Generated Library",
                        ["Generated"],
                        [
                            new VbaProjectReferenceDefinition(
                                "Generated Library",
                                "GeneratedType",
                                VbaSourceDefinitionKind.Class),
                            new VbaProjectReferenceDefinition(
                                "Generated Library",
                                "Item",
                                VbaSourceDefinitionKind.Property,
                                Signature: new VbaCallableSignature(
                                    "Property Item(Index As Long)",
                                    [
                                        new VbaCallableParameter(
                                            "Index",
                                            TypeReference: new VbaTypeReference("Long"),
                                            IsByRef: false)
                                    ],
                                    CallableKind: VbaCallableKind.Property,
                                    SupportsNamedArguments: true),
                                ParentTypeName: "GeneratedType",
                                TypeReference: new VbaTypeReference("Object"),
                                PropertyAccess: VbaPropertyAccess.Writable)
                        ])));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.bas"));
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "    Dim target As GeneratedType",
                "    Dim other As Object",
                "    Set target.Item(1&) = other",
                "End Sub"
            ]);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");

            var checkpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            var notification = await process.WaitForMessageAsync(
                checkpoint,
                message => message.TryGetProperty("method", out var method)
                    && method.GetString() == "textDocument/publishDiagnostics"
                    && message.GetProperty("params").GetProperty("uri").GetString() == uri);

            Assert.DoesNotContain(
                notification
                    .GetProperty("params")
                    .GetProperty("diagnostics")
                    .EnumerateArray(),
                candidate => candidate.GetProperty("code").GetString()
                    == "validation.incompatibleCallArgumentList");

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_uses_a_parameter_ordinal_when_catalog_name_metadata_is_empty()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-catalog-empty-parameter-name-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-catalog-empty-parameter-name-cache-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot, "Generated Library");
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity("Generated Library"),
                    new VbaProjectReferenceCatalog(
                        "Generated Library",
                        ["Generated"],
                        [
                            new VbaProjectReferenceDefinition(
                                "Generated Library",
                                "Work",
                                VbaSourceDefinitionKind.Procedure,
                                Signature: new VbaCallableSignature(
                                    "Sub Work(ByVal Arg1 As Long)",
                                    [
                                        new VbaCallableParameter(
                                            "",
                                            DisplayLabel: "ByVal Arg1 As Long",
                                            TypeReference: new VbaTypeReference("Long"),
                                            IsByRef: false)
                                    ],
                                    CallableKind: VbaCallableKind.Sub,
                                    SupportsNamedArguments: true),
                                GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal)
                        ])));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.bas"));
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "    Work",
                "End Sub"
            ]);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");

            var checkpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            var notification = await process.WaitForMessageAsync(
                checkpoint,
                message => message.TryGetProperty("method", out var method)
                    && method.GetString() == "textDocument/publishDiagnostics"
                    && message.GetProperty("params").GetProperty("uri").GetString() == uri);
            var diagnostic = Assert.Single(notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
                candidate => candidate.GetProperty("code").GetString()
                    == "validation.incompatibleCallArgumentList");

            Assert.Contains(
                "Mismatches: parameter 1: required argument is missing.",
                diagnostic.GetProperty("message").GetString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "parameter ''",
                diagnostic.GetProperty("message").GetString(),
                StringComparison.Ordinal);

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_keeps_a_named_call_indeterminate_when_catalog_parameter_name_metadata_is_empty()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-catalog-unknown-parameter-name-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-catalog-unknown-parameter-name-cache-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot, "Generated Library");
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity("Generated Library"),
                    new VbaProjectReferenceCatalog(
                        "Generated Library",
                        ["Generated"],
                        [
                            new VbaProjectReferenceDefinition(
                                "Generated Library",
                                "Work",
                                VbaSourceDefinitionKind.Procedure,
                                Signature: new VbaCallableSignature(
                                    "Sub Work(ByVal Arg1 As Long)",
                                    [
                                        new VbaCallableParameter(
                                            "",
                                            DisplayLabel: "ByVal Arg1 As Long",
                                            TypeReference: new VbaTypeReference("Long"),
                                            IsByRef: false)
                                    ],
                                    CallableKind: VbaCallableKind.Sub,
                                    SupportsNamedArguments: true),
                                GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal)
                        ])));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.bas"));
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "    Work Value:=1&",
                "End Sub"
            ]);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");

            var checkpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            var notification = await process.WaitForMessageAsync(
                checkpoint,
                message => message.TryGetProperty("method", out var method)
                    && method.GetString() == "textDocument/publishDiagnostics"
                    && message.GetProperty("params").GetProperty("uri").GetString() == uri);

            Assert.DoesNotContain(
                notification
                    .GetProperty("params")
                    .GetProperty("diagnostics")
                    .EnumerateArray(),
                candidate => candidate.GetProperty("code").GetString()
                    == "validation.incompatibleCallArgumentList");

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_does_not_offer_an_empty_catalog_parameter_name()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-catalog-empty-parameter-completion-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-catalog-empty-parameter-completion-cache-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot, "Generated Library");
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity("Generated Library"),
                    new VbaProjectReferenceCatalog(
                        "Generated Library",
                        ["Generated"],
                        [
                            new VbaProjectReferenceDefinition(
                                "Generated Library",
                                "Work",
                                VbaSourceDefinitionKind.Procedure,
                                Signature: new VbaCallableSignature(
                                    "Sub Work(ByVal Arg1 As Long)",
                                    [
                                        new VbaCallableParameter(
                                            "",
                                            DisplayLabel: "ByVal Arg1 As Long",
                                            TypeReference: new VbaTypeReference("Long"),
                                            IsByRef: false)
                                    ],
                                    CallableKind: VbaCallableKind.Sub,
                                    SupportsNamedArguments: true),
                                GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal)
                        ])));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.bas"));
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "    Work ",
                "End Sub"
            ]);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");

            var completion = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/completion",
                uri,
                text,
                "    Work ",
                "    Work ".Length);
            Assert.DoesNotContain(
                completion.GetProperty("result").EnumerateArray(),
                item => item.GetProperty("kind").GetInt32() == 5
                    && string.IsNullOrWhiteSpace(
                        item.GetProperty("label").GetString()));

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_preserves_the_owner_of_a_catalog_parameter_type()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-catalog-parameter-type-owner-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-catalog-parameter-type-owner-cache-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot, "Generated Library");
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity("Generated Library"),
                    new VbaProjectReferenceCatalog(
                        "Generated Library",
                        ["Generated"],
                        [
                            new VbaProjectReferenceDefinition(
                                "Generated Library",
                                "Payload",
                                VbaSourceDefinitionKind.Class),
                            new VbaProjectReferenceDefinition(
                                "Generated Library",
                                "Work",
                                VbaSourceDefinitionKind.Procedure,
                                Signature: new VbaCallableSignature(
                                    "Sub Work(ByRef Value As Payload)",
                                    [
                                        new VbaCallableParameter(
                                            "Value",
                                            TypeReference: new VbaTypeReference("Payload"),
                                            IsByRef: true)
                                    ],
                                    CallableKind: VbaCallableKind.Sub,
                                    SupportsNamedArguments: true),
                                GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal)
                        ])));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();
            var payloadUri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Payload.cls"));
            var payloadText = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Payload\""
            ]);
            var callerUri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.bas"));
            var callerText = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "    Dim value As Payload",
                "    Work value",
                "End Sub"
            ]);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(payloadUri, payloadText));
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(callerUri, callerText));
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");

            var checkpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri = callerUri, version = 2 },
                    contentChanges = new[] { new { text = callerText } }
                });
            var notification = await process.WaitForMessageAsync(
                checkpoint,
                message => message.TryGetProperty("method", out var method)
                    && method.GetString() == "textDocument/publishDiagnostics"
                    && message.GetProperty("params").GetProperty("uri").GetString()
                        == callerUri);
            var diagnostic = Assert.Single(notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
                candidate => candidate.GetProperty("code").GetString()
                    == "validation.incompatibleCallArgumentList");

            Assert.Contains(
                "argument 1 for parameter 'Value' ByRef type: expected Generated.Payload, found Payload.",
                diagnostic.GetProperty("message").GetString(),
                StringComparison.Ordinal);

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_preserves_the_owner_of_a_qualified_catalog_parameter_type()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-qualified-catalog-type-owner-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-qualified-catalog-type-owner-cache-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(
                projectRoot,
                "Second Library",
                "First Library");
            var store = new VbaProjectReferenceCatalogPersistentStore(cacheRoot);
            store.Save(new VbaProjectReferenceCatalogPersistentEntry(
                CreateGeneratedReferenceCatalogIdentity("Second Library"),
                new VbaProjectReferenceCatalog(
                    "Second Library",
                    ["Second", "Shared"],
                    [
                        new VbaProjectReferenceDefinition(
                            "Second Library",
                            "Payload",
                            VbaSourceDefinitionKind.Class)
                    ])));
            store.Save(new VbaProjectReferenceCatalogPersistentEntry(
                CreateGeneratedReferenceCatalogIdentity("First Library"),
                new VbaProjectReferenceCatalog(
                    "First Library",
                    ["First", "Shared"],
                    [
                        new VbaProjectReferenceDefinition(
                            "First Library",
                            "Payload",
                            VbaSourceDefinitionKind.Class),
                        new VbaProjectReferenceDefinition(
                            "First Library",
                            "Work",
                            VbaSourceDefinitionKind.Procedure,
                            Signature: new VbaCallableSignature(
                                "Sub Work(ByRef Value As Shared.Payload)",
                                [
                                    new VbaCallableParameter(
                                        "Value",
                                        TypeReference: new VbaTypeReference(
                                            "Payload",
                                            "Shared"),
                                        IsByRef: true)
                                ],
                                CallableKind: VbaCallableKind.Sub,
                                SupportsNamedArguments: true),
                            GlobalExposure:
                                ReferenceDefinitionGlobalExposure.LibraryGlobal)
                    ])));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync(new
            {
                textDocument = new
                {
                    publishDiagnostics = new
                    {
                        relatedInformation = true
                    }
                }
            });
            var uri = ToFileUri(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.bas"));
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "    Dim value As Second.Payload",
                "    Work value",
                "End Sub"
            ]);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForLogTextAsync(
                "reference 'First Library' source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");

            var checkpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            var notification = await process.WaitForMessageAsync(
                checkpoint,
                message => message.TryGetProperty("method", out var method)
                    && method.GetString() == "textDocument/publishDiagnostics"
                    && message.GetProperty("params").GetProperty("uri").GetString() == uri);
            var diagnostic = Assert.Single(notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
                candidate => candidate.GetProperty("code").GetString()
                    == "validation.incompatibleCallArgumentList");

            Assert.Contains(
                "argument 1 for parameter 'Value' ByRef type: expected First.Payload, found Second.Payload.",
                diagnostic.GetProperty("message").GetString(),
                StringComparison.Ordinal);

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_logs_skipped_reference_catalog_refresh_for_valid_persisted_cache()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-persisted-catalog-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory("vba-ls-persisted-catalog-cache-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot, "Generated Library");
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    new VbaProjectReferenceCatalogIdentity(
                        "Generated Library",
                        "{33333333-3333-3333-3333-333333333333}",
                        1,
                        0,
                        0,
                        @"C:\TypeLibs\Generated.tlb"),
                    new VbaProjectReferenceCatalog(
                        "Generated Library",
                        ["Generated"],
                        [
                            new VbaProjectReferenceDefinition(
                                "Generated Library",
                                "GeneratedType",
                                VbaSourceDefinitionKind.Class)
                        ])));

            await using var process = await LanguageServerProcessHarness.StartAsync(referenceCatalogCacheRoot: cacheRoot);

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "End Sub"
            ])));

            var logMessage = await process.WaitForLogTextAsync("source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");

            Assert.Contains("Generated Library", logMessage);
            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_logs_bundled_reference_catalog_availability()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-bundled-catalog-log-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot, "Microsoft Excel 16.0 Object Library");
            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "End Sub"
            ])));

            var logMessage = await process.WaitForLogTextAsync("source=bundled outcome=available");

            Assert.Contains("Microsoft Excel 16.0 Object Library", logMessage);
            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_uses_current_persisted_excel_catalog_after_background_preload_without_discovery()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-current-startup-catalog-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory("vba-ls-current-startup-cache-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot, "Microsoft Excel 16.0 Object Library");
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity("Microsoft Excel 16.0 Object Library"),
                    CreateGeneratedExcelReferenceCatalog()));
            var discoveryStartedFile = Path.Combine(projectRoot, "discovery-started.txt");
            var discoveryReleaseFile = Path.Combine(projectRoot, "discovery-release.txt");

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot,
                environment: new Dictionary<string, string>
                {
                    ["VBA_TOOLS_REFERENCE_CATALOG_DISCOVERY_STARTED_FILE"] = discoveryStartedFile,
                    ["VBA_TOOLS_REFERENCE_CATALOG_DISCOVERY_RELEASE_FILE"] = discoveryReleaseFile
                });

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var text = CreateExcelStartupCatalogWorkerText();
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");

            var completion = await process.SendRequestAsync(2,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri },
                    position = new
                    {
                        line = 7,
                        character = "    Set target_sheet = target_book.W".Length
                    }
                });
            var completionLabels = completion
                .GetProperty("result")
                .EnumerateArray()
                .Select(item => item.GetProperty("label").GetString())
                .ToArray();
            var discoveryStarted = await TryWaitForFileAsync(
                discoveryStartedFile,
                TimeSpan.FromMilliseconds(300));

            Assert.Contains("Worksheets", completionLabels);
            Assert.False(discoveryStarted);

            File.WriteAllText(discoveryReleaseFile, "release");
            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_returns_no_uncommitted_catalog_root_surfaces_while_discovery_is_blocked()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-uncommitted-catalog-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot, "Generated Library");
            var discoveryStartedFile = Path.Combine(projectRoot, "discovery-started.txt");
            var discoveryReleaseFile = Path.Combine(projectRoot, "discovery-release.txt");
            await using var process = await LanguageServerProcessHarness.StartAsync(
                environment: new Dictionary<string, string>
                {
                    ["VBA_TOOLS_REFERENCE_CATALOG_DISCOVERY_STARTED_FILE"] = discoveryStartedFile,
                    ["VBA_TOOLS_REFERENCE_CATALOG_DISCOVERY_RELEASE_FILE"] = discoveryReleaseFile
                });

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Option Explicit",
                "",
                "Public Sub Run()",
                "    value = Gen",
                "    value = Generated.",
                "    value = GeneratedValue",
                "    CatalogRun(",
                "End Sub"
            ]);
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));
            var diagnostics = await process.WaitForDiagnosticsAsync(uri);
            await WaitForFileAsync(discoveryStartedFile, TimeSpan.FromSeconds(5));

            var rootCompletion = await process.SendRequestAsync(2,
                    "textDocument/completion",
                    new
                    {
                        textDocument = new { uri },
                        position = new
                        {
                            line = 4,
                            character = "    value = Gen".Length
                        }
                    })
                .WaitAsync(TimeSpan.FromSeconds(1));
            var rootLabels = rootCompletion
                .GetProperty("result")
                .EnumerateArray()
                .Select(item => item.GetProperty("label").GetString())
                .ToArray();
            var qualifierCompletion = await process.SendRequestAsync(3,
                    "textDocument/completion",
                    new
                    {
                        textDocument = new { uri },
                        position = new
                        {
                            line = 5,
                            character = "    value = Generated.".Length
                        }
                    })
                .WaitAsync(TimeSpan.FromSeconds(1));
            var hover = await SendPositionRequestAsync(
                    process,
                    4,
                    "textDocument/hover",
                    uri,
                    text,
                    "GeneratedValue")
                .WaitAsync(TimeSpan.FromSeconds(1));
            var signatureHelp = await SendPositionRequestAsync(
                    process,
                    5,
                    "textDocument/signatureHelp",
                    uri,
                    text,
                    "CatalogRun(",
                    "CatalogRun(".Length)
                .WaitAsync(TimeSpan.FromSeconds(1));

            Assert.DoesNotContain("Generated", rootLabels);
            Assert.DoesNotContain("GeneratedValue", rootLabels);
            Assert.Empty(qualifierCompletion.GetProperty("result").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, hover.GetProperty("result").ValueKind);
            Assert.Equal(JsonValueKind.Null, signatureHelp.GetProperty("result").ValueKind);
            Assert.Empty(
                diagnostics
                    .GetProperty("params")
                    .GetProperty("diagnostics")
                    .EnumerateArray());

            File.WriteAllText(discoveryReleaseFile, "release");
            await process.ShutdownAsync(6);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_uses_stale_persisted_excel_catalog_for_editor_features_while_refresh_is_blocked()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-stale-startup-catalog-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory("vba-ls-stale-startup-cache-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot, "Microsoft Excel 16.0 Object Library");
            var store = new VbaProjectReferenceCatalogPersistentStore(cacheRoot);
            store.Save(new VbaProjectReferenceCatalogPersistentEntry(
                CreateGeneratedReferenceCatalogIdentity("Microsoft Excel 16.0 Object Library"),
                CreateGeneratedExcelReferenceCatalog()));
            MarkReferenceCatalogIndexAsStale(store, "Microsoft Excel 16.0 Object Library");
            var discoveryStartedFile = Path.Combine(projectRoot, "discovery-started.txt");
            var discoveryReleaseFile = Path.Combine(projectRoot, "discovery-release.txt");

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot,
                environment: new Dictionary<string, string>
                {
                    ["VBA_TOOLS_REFERENCE_CATALOG_DISCOVERY_STARTED_FILE"] = discoveryStartedFile,
                    ["VBA_TOOLS_REFERENCE_CATALOG_DISCOVERY_RELEASE_FILE"] = discoveryReleaseFile
                });

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var text = CreateExcelStartupCatalogWorkerText();
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));
            var diagnostics = await process.WaitForDiagnosticsAsync(uri);
            await WaitForFileAsync(discoveryStartedFile, TimeSpan.FromSeconds(5));
            var staleMessage = await process.WaitForLogTextAsync(
                    "source=stale-persisted outcome=stale phase=persistent-load expensiveMetadata=false")
                .WaitAsync(TimeSpan.FromSeconds(1));

            var completion = await process.SendRequestAsync(2,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri },
                    position = new
                    {
                        line = 7,
                        character = "    Set target_sheet = target_book.W".Length
                    }
                }).WaitAsync(TimeSpan.FromSeconds(1));
            var completionLabels = completion
                .GetProperty("result")
                .EnumerateArray()
                .Select(item => item.GetProperty("label").GetString())
                .ToArray();
            var signatureHelp = await SendPositionRequestAsync(process, 3,
                "textDocument/signatureHelp",
                uri,
                text,
                "Range(",
                "Range(".Length).WaitAsync(TimeSpan.FromSeconds(1));
            var semanticTokensResponse = await process.SendRequestAsync(4,
                "textDocument/semanticTokens/full",
                new
                {
                    textDocument = new { uri }
                }).WaitAsync(TimeSpan.FromSeconds(1));
            var semanticTokens = DecodeSemanticTokens(semanticTokensResponse, text);
            var hostGlobalCompletion = await process.SendRequestAsync(5,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri },
                    position = new
                    {
                        line = 9,
                        character = "    value = App".Length
                    }
                }).WaitAsync(TimeSpan.FromSeconds(1));
            var hostGlobalCompletionLabels = hostGlobalCompletion
                .GetProperty("result")
                .EnumerateArray()
                .Select(item => item.GetProperty("label").GetString())
                .ToArray();
            var libraryGlobalCompletion = await process.SendRequestAsync(6,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri },
                    position = new
                    {
                        line = 10,
                        character = "    value = xlC".Length
                    }
                }).WaitAsync(TimeSpan.FromSeconds(1));
            var libraryGlobalCompletionLabels = libraryGlobalCompletion
                .GetProperty("result")
                .EnumerateArray()
                .Select(item => item.GetProperty("label").GetString())
                .ToArray();
            var qualifierCompletion = await process.SendRequestAsync(7,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri },
                    position = new
                    {
                        line = 11,
                        character = "    value = Excel.".Length
                    }
                }).WaitAsync(TimeSpan.FromSeconds(1));
            var qualifierCompletionLabels = qualifierCompletion
                .GetProperty("result")
                .EnumerateArray()
                .Select(item => item.GetProperty("label").GetString())
                .ToArray();
            var catalogSignatureHelp = await SendPositionRequestAsync(process, 8,
                "textDocument/signatureHelp",
                uri,
                text,
                "Excel.CatalogRun(",
                "Excel.CatalogRun(".Length).WaitAsync(TimeSpan.FromSeconds(1));
            var hostGlobalHover = await SendPositionRequestAsync(process, 9,
                "textDocument/hover",
                uri,
                text,
                "Application").WaitAsync(TimeSpan.FromSeconds(1));
            var definition = await SendPositionRequestAsync(process, 10,
                "textDocument/definition",
                uri,
                text,
                "target_book.W").WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Contains("Worksheets", completionLabels);
            Assert.Contains("Microsoft Excel 16.0 Object Library", staleMessage);
            Assert.Empty(
                diagnostics
                    .GetProperty("params")
                    .GetProperty("diagnostics")
                    .EnumerateArray());
            Assert.Contains("Application", hostGlobalCompletionLabels);
            Assert.Contains("xlCenter", libraryGlobalCompletionLabels);
            Assert.Contains("xlCenter", qualifierCompletionLabels);
            var catalogSignature = catalogSignatureHelp
                .GetProperty("result")
                .GetProperty("signatures")
                .EnumerateArray()
                .Single();
            Assert.Equal(
                "Sub CatalogRun(Value As Long)",
                catalogSignature.GetProperty("label").GetString());
            Assert.Contains(
                "Application As Application",
                hostGlobalHover
                    .GetProperty("result")
                    .GetProperty("contents")
                    .GetProperty("value")
                    .GetString(),
                StringComparison.Ordinal);
            var signature = signatureHelp
                .GetProperty("result")
                .GetProperty("signatures")
                .EnumerateArray()
                .Single();
            Assert.Equal("Property Range(Cell1, Cell2) As Range", signature.GetProperty("label").GetString());
            Assert.Contains(semanticTokens, token =>
                token.Text == "Workbook"
                && token.TokenType == "class"
                && !token.TokenModifiers.Contains("declaration"));
            Assert.Contains(semanticTokens, token =>
                token.Text == "Range"
                && token.TokenType == "property"
                && token.Line == 8);
            Assert.Equal(JsonValueKind.Object, definition.GetProperty("result").ValueKind);

            File.WriteAllText(discoveryReleaseFile, "release");
            await process.ShutdownAsync(11);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_shutdown_cancels_blocked_reference_catalog_lifecycle()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-catalog-shutdown-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot, "Generated Library");
            var discoveryStartedFile = Path.Combine(projectRoot, "discovery-started.txt");
            var discoveryReleaseFile = Path.Combine(projectRoot, "discovery-release.txt");
            await using var process = await LanguageServerProcessHarness.StartAsync(
                environment: new Dictionary<string, string>
                {
                    ["VBA_TOOLS_REFERENCE_CATALOG_DISCOVERY_STARTED_FILE"] = discoveryStartedFile,
                    ["VBA_TOOLS_REFERENCE_CATALOG_DISCOVERY_RELEASE_FILE"] = discoveryReleaseFile
                });

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(
                    uri,
                    "Attribute VB_Name = \"Worker\"\nPublic Sub Run()\nEnd Sub"));
            await WaitForFileAsync(discoveryStartedFile, TimeSpan.FromSeconds(5));

            await process.ShutdownAsync(2);

            Assert.False(File.Exists(discoveryReleaseFile));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_logs_stale_and_failed_reference_catalog_diagnostics_once()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-stale-catalog-log-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory("vba-ls-stale-catalog-cache-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot, "Generated Library");
            var store = new VbaProjectReferenceCatalogPersistentStore(cacheRoot);
            store.Save(new VbaProjectReferenceCatalogPersistentEntry(
                CreateGeneratedReferenceCatalogIdentity("Generated Library"),
                CreateGeneratedReferenceCatalog("Generated Library")));
            MarkReferenceCatalogIndexAsStale(store, "Generated Library");

            await using var process = await LanguageServerProcessHarness.StartAsync(referenceCatalogCacheRoot: cacheRoot);

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "End Sub"
            ]);
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

            var staleMessage = await process.WaitForLogTextAsync("source=stale-persisted outcome=stale phase=persistent-load expensiveMetadata=false");
            var failedMessage = await process.WaitForLogTextAsync("source=stale-persisted outcome=failed phase=typelib-discovery expensiveMetadata=true");

            Assert.Contains("Generated Library", staleMessage);
            Assert.Contains("warning=", staleMessage);
            Assert.Contains("No matching TypeLib registry entry", failedMessage);

            await process.SendNotificationAsync("textDocument/didChange",
                new
                {
                    textDocument = new
                    {
                        uri,
                        version = 2
                    },
                    contentChanges = new[]
                    {
                        new
                        {
                            text
                        }
                    }
                });

            var duplicateFailure = await process.TryWaitForLogMessageAsync("source=stale-persisted outcome=failed phase=typelib-discovery expensiveMetadata=true",
                TimeSpan.FromMilliseconds(500));

            Assert.Null(duplicateFailure);

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_logs_corrupt_reference_catalog_cache_as_non_fatal()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-corrupt-catalog-log-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory("vba-ls-corrupt-catalog-cache-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot, "Generated Library");
            var store = new VbaProjectReferenceCatalogPersistentStore(cacheRoot);
            store.Save(new VbaProjectReferenceCatalogPersistentEntry(
                CreateGeneratedReferenceCatalogIdentity("Generated Library"),
                CreateGeneratedReferenceCatalog("Generated Library")));
            File.WriteAllText(
                store.GetReferenceIndexPath("Generated Library"),
                "{ this is not valid json");

            await using var process = await LanguageServerProcessHarness.StartAsync(referenceCatalogCacheRoot: cacheRoot);

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "End Sub"
            ])));

            var corruptMessage = await process.WaitForLogTextAsync("source=unavailable outcome=cache-read-warning phase=persistent-load expensiveMetadata=false");
            var failedMessage = await process.WaitForLogTextAsync("source=unavailable outcome=failed phase=typelib-discovery expensiveMetadata=true");

            Assert.Contains("non-fatal", corruptMessage);
            Assert.Contains("could not be read", corruptMessage);
            Assert.Contains("No matching TypeLib registry entry", failedMessage);

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_returns_generated_excel_workbook_member_completion_after_catalog_refresh()
    {
        if (!HasRegisteredTypeLib("Microsoft Excel 16.0 Object Library"))
        {
            return;
        }

        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-excel-catalog-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(
                projectRoot,
                "Microsoft Excel 16.0 Object Library");

            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Option Explicit",
                "",
                "Public Sub Run()",
                "    Dim target_book As Workbook",
                "    Dim target_sheet As Worksheet",
                "    Set target_sheet = target_book.W",
                "End Sub"
            ]);
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

            var refresh = await process.TryWaitForLogMessageAsync("Reference catalog refresh: document 'Book1' reference 'Microsoft Excel 16.0 Object Library' cached",
                TimeSpan.FromSeconds(20));
            Assert.NotNull(refresh);

            var completion = await process.SendRequestAsync(2,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri },
                    position = new { line = 6, character = "    Set target_sheet = target_book.W".Length }
                });
            var completionItems = completion
                .GetProperty("result")
                .EnumerateArray()
                .ToArray();
            var completionLabels = completionItems
                .Select(item => item.GetProperty("label").GetString())
                .ToArray();
            var worksheetsCompletion = completionItems.Single(item =>
                item.GetProperty("label").GetString() == "Worksheets");

            Assert.Contains("Worksheets", completionLabels);
            Assert.Equal(10, worksheetsCompletion.GetProperty("kind").GetInt32());

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_returns_generated_excel_parameterized_property_signature_help_after_catalog_refresh()
    {
        if (!HasRegisteredTypeLib("Microsoft Excel 16.0 Object Library"))
        {
            return;
        }

        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-excel-catalog-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(
                projectRoot,
                "Microsoft Excel 16.0 Object Library");

            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Option Explicit",
                "",
                "Public Sub Run()",
                "    Dim target_sheet As Worksheet",
                "    Dim target_range As Range",
                "    Dim first_cell As Range",
                "    Set target_range = target_sheet.Range(",
                "    Set target_range = target_sheet.Range(first_cell, ",
                "End Sub"
            ]);
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

            var refresh = await process.TryWaitForLogMessageAsync("Reference catalog refresh: document 'Book1' reference 'Microsoft Excel 16.0 Object Library' cached",
                TimeSpan.FromSeconds(20));
            Assert.NotNull(refresh);

            var firstParameterHelp = await SendPositionRequestAsync(process, 2,
                "textDocument/signatureHelp",
                uri,
                text,
                "Range(",
                "Range(".Length);
            var firstParameterResult = firstParameterHelp.GetProperty("result");
            Assert.Equal(0, firstParameterResult.GetProperty("activeParameter").GetInt32());
            var firstSignature = firstParameterResult
                .GetProperty("signatures")
                .EnumerateArray()
                .Single();
            Assert.StartsWith("Property Range(", firstSignature.GetProperty("label").GetString());
            Assert.EndsWith(") As Range", firstSignature.GetProperty("label").GetString());
            var parameterLabels = firstSignature
                .GetProperty("parameters")
                .EnumerateArray()
                .Select(parameter => parameter.GetProperty("label").GetString() ?? "")
                .ToArray();
            Assert.Equal(2, parameterLabels.Length);
            Assert.Contains("Cell1", parameterLabels[0], StringComparison.Ordinal);
            Assert.Contains("Cell2", parameterLabels[1], StringComparison.Ordinal);

            var secondParameterHelp = await SendPositionRequestAsync(process, 3,
                "textDocument/signatureHelp",
                uri,
                text,
                "Range(first_cell, ",
                "Range(first_cell, ".Length);
            var secondParameterResult = secondParameterHelp.GetProperty("result");
            Assert.Equal(1, secondParameterResult.GetProperty("activeParameter").GetInt32());
            var secondSignature = secondParameterResult
                .GetProperty("signatures")
                .EnumerateArray()
                .Single();
            Assert.StartsWith("Property Range(", secondSignature.GetProperty("label").GetString());
            Assert.EndsWith(") As Range", secondSignature.GetProperty("label").GetString());

            await process.ShutdownAsync(4);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_prefers_source_definitions_over_reference_catalogs()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-source-precedence-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot, "Microsoft Scripting Runtime");

            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Option Explicit",
                "",
                "'* @brief Source dictionary wins.",
                "Public Function Dictionary() As String",
                "End Function",
                "",
                "Public Sub Run()",
                "    Dictionary",
                "End Sub"
            ]);
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

            var callOffset = text.LastIndexOf("Dictionary", StringComparison.Ordinal)
                - text.IndexOf("Dictionary", StringComparison.Ordinal);
            var hover = await SendPositionRequestAsync(process, 2, "textDocument/hover", uri, text, "Dictionary", callOffset);
            var hoverValue = hover.GetProperty("result").GetProperty("contents").GetProperty("value").GetString();
            Assert.Contains("Source dictionary wins.", hoverValue, StringComparison.Ordinal);
            Assert.DoesNotContain("Microsoft Scripting Runtime", hoverValue, StringComparison.Ordinal);

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_projects_excel_host_globals_as_external_values()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-host-globals-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(
                projectRoot,
                "Microsoft Excel 16.0 Object Library");

            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Option Explicit",
                "Public Sub Run()",
                "    value = ",
                "    value = ActiveCell",
                "    value = Application",
                "    value = xlCenter",
                "    Dim app As Application",
                "End Sub"
            ]);
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

            var rootCompletion = await process.SendRequestAsync(2,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri },
                    position = new { line = 3, character = "    value = ".Length }
                });
            var rootItems = rootCompletion
                .GetProperty("result")
                .EnumerateArray()
                .ToArray();
            var typeCompletion = await process.SendRequestAsync(3,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri },
                    position = new { line = 7, character = "    Dim app As ".Length }
                });
            var typeItems = typeCompletion
                .GetProperty("result")
                .EnumerateArray()
                .ToArray();
            var hover = await SendPositionRequestAsync(
                process,
                4,
                "textDocument/hover",
                uri,
                text,
                "ActiveCell");
            var hoverValue = hover
                .GetProperty("result")
                .GetProperty("contents")
                .GetProperty("value")
                .GetString();
            var semanticTokensResponse = await process.SendRequestAsync(5,
                "textDocument/semanticTokens/full",
                new
                {
                    textDocument = new { uri }
                });
            var semanticTokens = DecodeSemanticTokens(semanticTokensResponse, text);

            AssertCatalogCompletionItem(
                rootItems,
                "vbCrLf",
                expectedKind: 21,
                expectedDetail: "Const vbCrLf As String");
            AssertCatalogCompletionItem(
                rootItems,
                "xlCenter",
                expectedKind: 20,
                expectedDetail: "xlCenter As Long");
            AssertCatalogCompletionItem(
                rootItems,
                "ActiveCell",
                expectedKind: 10,
                expectedDetail: "ActiveCell As Range");
            AssertCatalogCompletionItem(
                rootItems,
                "Application",
                expectedKind: 10,
                expectedDetail: "Application As Application");
            AssertCatalogCompletionItem(
                typeItems,
                "Application",
                expectedKind: 7,
                expectedDetail: "Class Application");
            var excelQualifier = Assert.Single(rootItems, item =>
                item.GetProperty("label").GetString() == "Excel"
                && item.GetProperty("kind").GetInt32() == 9);
            Assert.Equal(
                "Reference qualifier",
                excelQualifier.GetProperty("detail").GetString());
            Assert.Equal("Excel.", excelQualifier.GetProperty("insertText").GetString());
            Assert.False(string.IsNullOrWhiteSpace(
                excelQualifier.GetProperty("sortText").GetString()));

            Assert.Contains("```vba\nActiveCell As Range\n```", hoverValue, StringComparison.Ordinal);
            Assert.DoesNotContain("Property ActiveCell", hoverValue, StringComparison.Ordinal);
            Assert.Contains(semanticTokens, token =>
                token.Text == "ActiveCell"
                && token.TokenType == "property"
                && token.TokenModifiers.Contains("defaultLibrary")
                && token.Line == 4);
            Assert.Contains(semanticTokens, token =>
                token.Text == "Application"
                && token.TokenType == "property"
                && token.TokenModifiers.Contains("defaultLibrary")
                && token.Line == 5);
            Assert.Contains(semanticTokens, token =>
                token.Text == "xlCenter"
                && token.TokenType == "enumMember"
                && token.TokenModifiers.Contains("defaultLibrary")
                && token.Line == 6);
            Assert.Contains(semanticTokens, token =>
                token.Text == "Application"
                && token.TokenType == "class"
                && token.TokenModifiers.Contains("defaultLibrary")
                && token.Line == 7);

            await process.ShutdownAsync(6);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_prefers_main_reference_over_other_reference_matches_for_unqualified_names()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-main-catalog-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(
                projectRoot,
                "Microsoft Excel 16.0 Object Library",
                "Microsoft Office 16.0 Object Library");

            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Option Explicit",
                "",
                "Public Sub Run()",
                "    Application",
                "End Sub"
            ]);
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

            var hover = await SendPositionRequestAsync(process, 2, "textDocument/hover", uri, text, "Application");
            var hoverValue = hover.GetProperty("result").GetProperty("contents").GetProperty("value").GetString();
            Assert.Contains("Microsoft Excel application", hoverValue, StringComparison.Ordinal);
            Assert.DoesNotContain("Microsoft Office application", hoverValue, StringComparison.Ordinal);

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_keeps_equal_rank_reference_matches_ambiguous_and_ignores_inactive_references()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-catalog-ambiguity-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(
                projectRoot,
                "Microsoft Office 16.0 Object Library",
                "Microsoft Outlook 16.0 Object Library");

            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Option Explicit",
                "",
                "Public Sub Run()",
                "    Application",
                "    Scripting.Dictionary",
                "End Sub"
            ]);
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

            var ambiguousHover = await SendPositionRequestAsync(process, 2, "textDocument/hover", uri, text, "Application");
            Assert.Equal(JsonValueKind.Null, ambiguousHover.GetProperty("result").ValueKind);

            var inactiveHover = await SendPositionRequestAsync(process, 3, "textDocument/hover", uri, text, "Dictionary");
            Assert.Equal(JsonValueKind.Null, inactiveHover.GetProperty("result").ValueKind);

            var completion = await process.SendRequestAsync(4,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri },
                    position = new { line = 4, character = 4 }
                });
            var completionLabels = completion
                .GetProperty("result")
                .EnumerateArray()
                .Select(item => item.GetProperty("label").GetString())
                .ToArray();
            Assert.DoesNotContain("Application", completionLabels);
            Assert.DoesNotContain("Dictionary", completionLabels);

            await process.ShutdownAsync(5);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_reports_missing_catalog_availability_without_source_diagnostics()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-missing-catalog-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(
                projectRoot,
                "Microsoft Excel 16.0 Object Library",
                "Uncataloged Reference Library");

            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Option Explicit",
                "",
                "Public Sub Run()",
                "    UncatalogedType",
                "End Sub"
            ]);
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

            var diagnostics = await process.WaitForNotificationAsync("textDocument/publishDiagnostics");
            Assert.Empty(diagnostics.GetProperty("params").GetProperty("diagnostics").EnumerateArray());

            var selection = await process.WaitForLogMessageAsync("VbaProjectReferenceSelection document=Book1");
            Assert.Contains(
                "Uncataloged Reference Library",
                selection.GetProperty("params").GetProperty("message").GetString(),
                StringComparison.Ordinal);
            var availability = await process.WaitForLogMessageAsync("Reference catalog availability");
            Assert.Equal(3, availability.GetProperty("params").GetProperty("type").GetInt32());
            var availabilityMessage = availability.GetProperty("params").GetProperty("message").GetString();
            Assert.Contains("Uncataloged Reference Library", availabilityMessage, StringComparison.Ordinal);
            Assert.Contains("editor metadata is not currently available", availabilityMessage, StringComparison.Ordinal);
            Assert.Contains("reference remains active for workbook build/test", availabilityMessage, StringComparison.Ordinal);
            Assert.Contains("external editor definitions are unavailable", availabilityMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("warning", availabilityMessage, StringComparison.OrdinalIgnoreCase);

            var discoveryFailure = await process.WaitForLogMessageAsync("source=unavailable outcome=failed phase=typelib-discovery expensiveMetadata=true");
            Assert.Equal(2, discoveryFailure.GetProperty("params").GetProperty("type").GetInt32());
            var discoveryFailureMessage = discoveryFailure.GetProperty("params").GetProperty("message").GetString();
            Assert.Contains("Uncataloged Reference Library", discoveryFailureMessage, StringComparison.Ordinal);
            Assert.Contains("No matching TypeLib registry entry was found.", discoveryFailureMessage, StringComparison.Ordinal);

            var hover = await SendPositionRequestAsync(process, 2, "textDocument/hover", uri, text, "UncatalogedType");
            Assert.Equal(JsonValueKind.Null, hover.GetProperty("result").ValueKind);

            var completion = await process.SendRequestAsync(3,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri },
                    position = new { line = 4, character = 4 }
                });
            var completionLabels = completion
                .GetProperty("result")
                .EnumerateArray()
                .Select(item => item.GetProperty("label").GetString())
                .ToArray();
            Assert.DoesNotContain("UncatalogedType", completionLabels);

            await process.ShutdownAsync(4);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_prepares_the_authoritative_module_identity_payload_for_rename()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/Orders.bas";
        const string text = "Attribute VB_Name = \"InvoiceModule\"\nOption Explicit";
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

        var prepare = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/prepareRename",
            uri,
            text,
            "InvoiceModule");

        var result = prepare.GetProperty("result");
        Assert.Equal("InvoiceModule", result.GetProperty("placeholder").GetString());
        Assert.Equal(0, result.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(21, result.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(0, result.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(34, result.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_keeps_managed_module_identity_owned_while_member_rename_remains_available()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-managed-module-rename-").FullName;
        try
        {
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            Directory.CreateDirectory(sourceRoot);
            var sourcePath = Path.Combine(sourceRoot, "ManagedHelper.bas");
            var consumerPath = Path.Combine(sourceRoot, "Consumer.bas");
            var text = string.Join('\n', [
                "Attribute VB_Name = \"ManagedHelper\"",
                "Public Sub Run()",
                "End Sub"
            ]);
            var consumerText = string.Join('\n', [
                "Attribute VB_Name = \"Consumer\"",
                "Public Sub Execute()",
                "    ManagedHelper.Run",
                "End Sub"
            ]);
            File.WriteAllText(sourcePath, text);
            File.WriteAllText(consumerPath, consumerText);
            WriteModuleRenameProjectManifest(
                projectRoot,
                ("ManagedHelper", "ManagedHelper.bas"));
            var manifestPath = Path.Combine(projectRoot, "vba-project.json");
            var originalManifestBytes = File.ReadAllBytes(manifestPath);
            var originalSourceBytes = File.ReadAllBytes(sourcePath);
            var uri = ToFileUri(sourcePath);
            var consumerUri = ToFileUri(consumerPath);
            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(consumerUri, consumerText));

            var prepare = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/prepareRename",
                uri,
                text,
                "ManagedHelper");

            Assert.False(prepare.TryGetProperty("result", out _));
            var error = prepare.GetProperty("error");
            Assert.Equal(-32803, error.GetProperty("code").GetInt32());
            var data = error.GetProperty("data");
            Assert.Equal(
                "managedModuleIdentity",
                data.GetProperty("reason").GetString());
            Assert.Equal(sourcePath, data.GetProperty("path").GetString(), ignoreCase: true);
            Assert.Contains(
                "CommonModules",
                data.GetProperty("guidance").GetString(),
                StringComparison.Ordinal);

            var qualifierPrepare = await SendPositionRequestAsync(
                process,
                3,
                "textDocument/prepareRename",
                consumerUri,
                consumerText,
                "ManagedHelper.Run");
            Assert.Equal(
                "managedModuleIdentity",
                qualifierPrepare
                    .GetProperty("error")
                    .GetProperty("data")
                    .GetProperty("reason")
                    .GetString());

            var memberRename = await SendPositionRequestAsync(
                process,
                4,
                "textDocument/rename",
                consumerUri,
                consumerText,
                "ManagedHelper.Run",
                "ManagedHelper.".Length,
                new { newName = "RunManaged" });
            var changes = memberRename.GetProperty("result").GetProperty("changes");
            Assert.Equal(
                "RunManaged",
                Assert.Single(changes.GetProperty(uri).EnumerateArray())
                    .GetProperty("newText")
                    .GetString());
            Assert.Equal(
                "RunManaged",
                Assert.Single(changes.GetProperty(consumerUri).EnumerateArray())
                    .GetProperty("newText")
                    .GetString());
            Assert.Equal(originalManifestBytes, File.ReadAllBytes(manifestPath));
            Assert.Equal(originalSourceBytes, File.ReadAllBytes(sourcePath));

            await process.ShutdownAsync(5);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_reports_managed_module_ownership_before_file_capability_gating()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-managed-module-capability-").FullName;
        try
        {
            var sourcePath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "ManagedHelper.bas");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            const string text = "Attribute VB_Name = \"ManagedHelper\"";
            File.WriteAllText(sourcePath, text);
            WriteModuleRenameProjectManifest(
                projectRoot,
                ("ManagedHelper", "ManagedHelper.bas"));
            var uri = ToFileUri(sourcePath);
            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));

            var rename = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/rename",
                uri,
                text,
                "ManagedHelper",
                0,
                new { newName = "RenamedHelper" });

            Assert.False(rename.TryGetProperty("result", out _));
            Assert.Equal(
                "managedModuleIdentity",
                rename
                    .GetProperty("error")
                    .GetProperty("data")
                    .GetProperty("reason")
                    .GetString());

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_rejects_manifest_module_rename_without_current_containing_project_name_authority()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-module-project-authority-").FullName;
        try
        {
            WriteModuleRenameProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var sourcePath = Path.Combine(sourceRoot, "SourceUnit.bas");
            var consumerPath = Path.Combine(sourceRoot, "Consumer.bas");
            var templatePath = Path.Combine(sourceRoot, "Book1.xlsm");
            var text = string.Join('\n', [
                "Attribute VB_Name = \"InvoiceModule\"",
                "Public Function BuildValue() As Long",
                "    BuildValue = 1",
                "End Function"
            ]);
            var consumerText = string.Join('\n', [
                "Attribute VB_Name = \"Consumer\"",
                "Public Sub Run()",
                "    Debug.Print ",
                "End Sub"
            ]);
            File.WriteAllText(sourcePath, text);
            File.WriteAllText(consumerPath, consumerText);
            File.WriteAllBytes(templatePath, [0x01, 0x02, 0x03]);
            var uri = ToFileUri(sourcePath);
            var consumerUri = ToFileUri(consumerPath);
            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(consumerUri, consumerText));

            var completion = await process.SendRequestAsync(
                2,
                "textDocument/completion",
                MergePositionParameters(
                    consumerUri,
                    2,
                    "    Debug.Print ".Length,
                    null));
            Assert.Contains(
                completion.GetProperty("result").EnumerateArray(),
                item => item.GetProperty("label").GetString() == "BuildValue");

            var hover = await SendPositionRequestAsync(
                process,
                3,
                "textDocument/hover",
                uri,
                text,
                "BuildValue");
            Assert.Contains(
                "BuildValue",
                hover.GetProperty("result").GetRawText(),
                StringComparison.Ordinal);

            var memberRename = await SendPositionRequestAsync(
                process,
                4,
                "textDocument/rename",
                uri,
                text,
                "BuildValue",
                0,
                new { newName = "CreateValue" });
            Assert.Equal(
                2,
                memberRename
                    .GetProperty("result")
                    .GetProperty("changes")
                    .GetProperty(uri)
                    .GetArrayLength());

            var rename = await SendPositionRequestAsync(
                process,
                5,
                "textDocument/rename",
                uri,
                text,
                "InvoiceModule",
                0,
                new { newName = "BillingModule" });

            Assert.False(rename.TryGetProperty("result", out _));
            var error = rename.GetProperty("error");
            Assert.Equal(-32803, error.GetProperty("code").GetInt32());
            Assert.Equal(
                "analysisIncomplete",
                error.GetProperty("data").GetProperty("reason").GetString());
            Assert.Contains(
                "VBProject.Name",
                error.GetProperty("message").GetString(),
                StringComparison.Ordinal);

            await process.ShutdownAsync(6);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_rejects_module_rename_with_a_stale_source_template_fingerprint()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-module-stale-template-authority-").FullName;
        try
        {
            WriteModuleRenameProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var sourcePath = Path.Combine(sourceRoot, "SourceUnit.bas");
            var templatePath = Path.Combine(sourceRoot, "Book1.xlsm");
            const string text = "Attribute VB_Name = \"InvoiceModule\"";
            var inspectedTemplateBytes = new byte[] { 0x10, 0x20, 0x30, 0x40 };
            File.WriteAllText(sourcePath, text);
            File.WriteAllBytes(templatePath, inspectedTemplateBytes);
            var uri = ToFileUri(sourcePath);
            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.SendNotificationAsync(
                "vba/hostClassProjectionSnapshot",
                new
                {
                    schemaVersion = 2,
                    revision = 1,
                    project = Path.GetFullPath(projectRoot),
                    document = "Book1",
                    sourceTemplate = Path.GetFullPath(templatePath),
                    state = "present",
                    vbaProjectName = "ContainingProject",
                    sourceTemplateFingerprint = Convert.ToHexString(
                        SHA256.HashData(inspectedTemplateBytes)),
                    classEnumerationComplete = true,
                    classes = Array.Empty<object>()
                });
            File.WriteAllBytes(
                templatePath,
                [0x50, 0x60, 0x70, 0x80]);

            var rename = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/rename",
                uri,
                text,
                "InvoiceModule",
                0,
                new { newName = "BillingModule" });

            Assert.False(rename.TryGetProperty("result", out _));
            var error = rename.GetProperty("error");
            Assert.Equal(-32803, error.GetProperty("code").GetInt32());
            var data = error.GetProperty("data");
            Assert.Equal("analysisIncomplete", data.GetProperty("reason").GetString());
            Assert.Equal(
                "containingProjectNameUnavailable",
                data.GetProperty("condition").GetString());
            Assert.Equal(
                templatePath,
                data.GetProperty("path").GetString(),
                ignoreCase: true);

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_reports_an_existing_authoritative_referenced_project_name_conflict_without_blocking_repairing_rename()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-existing-module-reference-collision-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(
                projectRoot,
                "Microsoft Excel 16.0 Object Library");
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var sourcePath = Path.Combine(sourceRoot, "SourceUnit.bas");
            var consumerPath = Path.Combine(sourceRoot, "Consumer.bas");
            var templatePath = Path.Combine(sourceRoot, "Book1.xlsm");
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Excel\"",
                "Public Sub Run()",
                "End Sub"
            ]);
            var consumerText = string.Join('\n', [
                "Attribute VB_Name = \"Consumer\"",
                "Public Sub Execute()",
                "    Excel.Run",
                "End Sub"
            ]);
            var templateBytes = new byte[] { 0x42, 0x24, 0x18, 0x81 };
            File.WriteAllText(sourcePath, text);
            File.WriteAllText(consumerPath, consumerText);
            File.WriteAllBytes(templatePath, templateBytes);
            var uri = ToFileUri(sourcePath);
            var consumerUri = ToFileUri(consumerPath);
            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(consumerUri, consumerText));
            await process.WaitForDiagnosticsAsync(uri);
            await process.SendNotificationAsync(
                "vba/hostClassProjectionSnapshot",
                new
                {
                    schemaVersion = 2,
                    revision = 1,
                    project = Path.GetFullPath(projectRoot),
                    document = "Book1",
                    sourceTemplate = Path.GetFullPath(templatePath),
                    state = "present",
                    vbaProjectName = "ContainingProject",
                    sourceTemplateFingerprint = Convert.ToHexString(
                        SHA256.HashData(templateBytes)),
                    classEnumerationComplete = true,
                    classes = Array.Empty<object>()
                });
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });

            var notification = await process.WaitForDiagnosticsAsync(uri);
            var diagnostic = Assert.Single(
                notification
                    .GetProperty("params")
                    .GetProperty("diagnostics")
                    .EnumerateArray());
            Assert.Equal(
                "validation.moduleIdentityNameConflict",
                diagnostic.GetProperty("code").GetString());
            Assert.Equal(
                "Module name 'Excel' conflicts with referenced project or object library 'Excel'.",
                diagnostic.GetProperty("message").GetString());
            var range = diagnostic.GetProperty("range");
            Assert.Equal(0, range.GetProperty("start").GetProperty("line").GetInt32());
            Assert.Equal(21, range.GetProperty("start").GetProperty("character").GetInt32());
            Assert.Equal(0, range.GetProperty("end").GetProperty("line").GetInt32());
            Assert.Equal(26, range.GetProperty("end").GetProperty("character").GetInt32());
            var conflict = Assert.Single(
                diagnostic
                    .GetProperty("data")
                    .GetProperty("conflicts")
                    .EnumerateArray());
            Assert.Equal(
                "referencedProject",
                conflict.GetProperty("collisionKind").GetString());
            Assert.Equal("Excel", conflict.GetProperty("name").GetString());
            Assert.Equal(
                "Microsoft Excel 16.0 Object Library",
                conflict.GetProperty("referenceName").GetString());
            Assert.False(conflict.TryGetProperty("uri", out _));
            Assert.False(conflict.TryGetProperty("range", out _));
            Assert.False(diagnostic.TryGetProperty("relatedInformation", out _));

            var definition = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/definition",
                consumerUri,
                consumerText,
                "Excel.Run");
            var definitionLocation = definition.GetProperty("result");
            Assert.Equal(uri, definitionLocation.GetProperty("uri").GetString());
            Assert.Equal(
                0,
                definitionLocation
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32());

            var references = await SendPositionRequestAsync(
                process,
                3,
                "textDocument/references",
                consumerUri,
                consumerText,
                "Excel.Run");
            var referenceLocations = references
                .GetProperty("result")
                .EnumerateArray()
                .ToArray();
            Assert.Contains(referenceLocations, location =>
                location.GetProperty("uri").GetString() == uri
                && location.GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32() == 0);
            Assert.Contains(referenceLocations, location =>
                location.GetProperty("uri").GetString() == consumerUri
                && location.GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32() == 2);

            var rename = await SendPositionRequestAsync(
                process,
                4,
                "textDocument/rename",
                uri,
                text,
                "Excel",
                0,
                new { newName = "BillingModule" });

            Assert.True(rename.TryGetProperty("result", out var repair));
            Assert.Equal(JsonValueKind.Object, repair.ValueKind);

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_reports_a_current_containing_vba_project_name_collision()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-module-project-collision-").FullName;
        try
        {
            WriteModuleRenameProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var sourcePath = Path.Combine(sourceRoot, "SourceUnit.bas");
            var templatePath = Path.Combine(sourceRoot, "Book1.xlsm");
            const string text = "Attribute VB_Name = \"InvoiceModule\"";
            var templateBytes = new byte[] { 0x10, 0x20, 0x30, 0x40 };
            File.WriteAllText(sourcePath, text);
            File.WriteAllBytes(templatePath, templateBytes);
            var uri = ToFileUri(sourcePath);
            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.SendNotificationAsync(
                "vba/hostClassProjectionSnapshot",
                new
                {
                    schemaVersion = 2,
                    revision = 1,
                    project = Path.GetFullPath(projectRoot),
                    document = "Book1",
                    sourceTemplate = Path.GetFullPath(templatePath),
                    state = "present",
                    vbaProjectName = "BillingModule",
                    sourceTemplateFingerprint = Convert.ToHexString(
                        SHA256.HashData(templateBytes)),
                    classEnumerationComplete = true,
                    classes = Array.Empty<object>()
                });
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            await process.WaitForDiagnosticsAsync(uri);

            var rename = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/rename",
                uri,
                text,
                "InvoiceModule",
                0,
                new { newName = "BillingModule" });

            Assert.False(rename.TryGetProperty("result", out _));
            var error = rename.GetProperty("error");
            Assert.Equal(-32803, error.GetProperty("code").GetInt32());
            var data = error.GetProperty("data");
            Assert.Equal(
                "sameScopeCollision",
                data.GetProperty("reason").GetString());
            var conflict = Assert.Single(
                data.GetProperty("conflicts").EnumerateArray());
            Assert.Equal(
                "containingProject",
                conflict.GetProperty("collisionKind").GetString());
            Assert.Equal(
                "BillingModule",
                conflict.GetProperty("name").GetString());
            Assert.Equal(
                ToFileUri(templatePath),
                conflict.GetProperty("uri").GetString());

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_reports_an_authoritative_bundled_referenced_project_name_collision()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-module-reference-collision-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(
                projectRoot,
                "Microsoft Excel 16.0 Object Library");
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var sourcePath = Path.Combine(sourceRoot, "SourceUnit.bas");
            var templatePath = Path.Combine(sourceRoot, "Book1.xlsm");
            const string text = "Attribute VB_Name = \"InvoiceModule\"";
            var templateBytes = new byte[] { 0x11, 0x22, 0x33, 0x44 };
            File.WriteAllText(sourcePath, text);
            File.WriteAllBytes(templatePath, templateBytes);
            var uri = ToFileUri(sourcePath);
            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.SendNotificationAsync(
                "vba/hostClassProjectionSnapshot",
                new
                {
                    schemaVersion = 2,
                    revision = 1,
                    project = Path.GetFullPath(projectRoot),
                    document = "Book1",
                    sourceTemplate = Path.GetFullPath(templatePath),
                    state = "present",
                    vbaProjectName = "ContainingProject",
                    sourceTemplateFingerprint = Convert.ToHexString(
                        SHA256.HashData(templateBytes)),
                    classEnumerationComplete = true,
                    classes = Array.Empty<object>()
                });
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            await process.WaitForDiagnosticsAsync(uri);

            var rename = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/rename",
                uri,
                text,
                "InvoiceModule",
                0,
                new { newName = "Excel" });

            Assert.False(rename.TryGetProperty("result", out _));
            var error = rename.GetProperty("error");
            Assert.Equal(-32803, error.GetProperty("code").GetInt32());
            Assert.Equal(
                "Module name 'Excel' conflicts with referenced project or object library 'Excel'.",
                error.GetProperty("message").GetString());
            var data = error.GetProperty("data");
            Assert.Equal(
                "sameScopeCollision",
                data.GetProperty("reason").GetString());
            var conflict = Assert.Single(
                data.GetProperty("conflicts").EnumerateArray());
            Assert.Equal(
                "referencedProject",
                conflict.GetProperty("collisionKind").GetString());
            Assert.Equal("Excel", conflict.GetProperty("name").GetString());
            Assert.Equal(
                "Microsoft Excel 16.0 Object Library",
                conflict.GetProperty("referenceName").GetString());
            Assert.False(conflict.TryGetProperty("uri", out _));
            Assert.False(conflict.TryGetProperty("range", out _));

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_uses_only_the_current_concrete_referenced_project_name_for_module_collisions()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-module-generated-reference-collision-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-module-generated-reference-cache-").FullName;
        try
        {
            const string referenceName = "FriendlyLibrary";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            var catalog = new VbaProjectReferenceCatalog(
                referenceName,
                ["DisplayAlias"],
                [])
            {
                ReferencedVbaProjectName = "ActualReferenceProject"
            };
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    catalog));
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var sourcePath = Path.Combine(sourceRoot, "SourceUnit.bas");
            var templatePath = Path.Combine(sourceRoot, "Book1.xlsm");
            const string text = "Attribute VB_Name = \"InvoiceModule\"";
            var templateBytes = new byte[] { 0x12, 0x24, 0x36, 0x48 };
            File.WriteAllText(sourcePath, text);
            File.WriteAllBytes(templatePath, templateBytes);
            var uri = ToFileUri(sourcePath);
            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);

            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
            await process.SendNotificationAsync(
                "vba/hostClassProjectionSnapshot",
                new
                {
                    schemaVersion = 2,
                    revision = 1,
                    project = Path.GetFullPath(projectRoot),
                    document = "Book1",
                    sourceTemplate = Path.GetFullPath(templatePath),
                    state = "present",
                    vbaProjectName = "ContainingProject",
                    sourceTemplateFingerprint = Convert.ToHexString(
                        SHA256.HashData(templateBytes)),
                    classEnumerationComplete = true,
                    classes = Array.Empty<object>()
                });
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            await process.WaitForDiagnosticsAsync(uri);

            var displayNameRename = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/rename",
                uri,
                text,
                "InvoiceModule",
                0,
                new { newName = referenceName });
            Assert.True(displayNameRename.TryGetProperty("result", out _));

            var aliasRename = await SendPositionRequestAsync(
                process,
                3,
                "textDocument/rename",
                uri,
                text,
                "InvoiceModule",
                0,
                new { newName = "DisplayAlias" });
            Assert.True(aliasRename.TryGetProperty("result", out _));

            var rename = await SendPositionRequestAsync(
                process,
                4,
                "textDocument/rename",
                uri,
                text,
                "InvoiceModule",
                0,
                new { newName = "ActualReferenceProject" });

            var data = rename.GetProperty("error").GetProperty("data");
            Assert.Equal("sameScopeCollision", data.GetProperty("reason").GetString());
            var conflict = Assert.Single(
                data.GetProperty("conflicts").EnumerateArray());
            Assert.Equal(
                "referencedProject",
                conflict.GetProperty("collisionKind").GetString());
            Assert.Equal(
                "ActualReferenceProject",
                conflict.GetProperty("name").GetString());
            Assert.Equal(
                referenceName,
                conflict.GetProperty("referenceName").GetString());

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_orders_complete_module_conflicts_from_source_to_project_to_reference()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-module-complete-collision-order-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-module-complete-collision-cache-").FullName;
        try
        {
            const string referenceName = "FriendlyLibrary";
            const string collisionName = "CollisionName";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            var catalog = new VbaProjectReferenceCatalog(
                referenceName,
                ["DisplayAlias"],
                [])
            {
                ReferencedVbaProjectName = collisionName
            };
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    catalog));
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var targetPath = Path.Combine(sourceRoot, "SourceUnit.bas");
            var conflictPath = Path.Combine(sourceRoot, "CollisionName.bas");
            var templatePath = Path.Combine(sourceRoot, "Book1.xlsm");
            const string targetText = "Attribute VB_Name = \"InvoiceModule\"";
            const string conflictText = "Attribute VB_Name = \"CollisionName\"";
            var templateBytes = new byte[] { 0x14, 0x28, 0x42, 0x56 };
            File.WriteAllText(targetPath, targetText);
            File.WriteAllText(conflictPath, conflictText);
            File.WriteAllBytes(templatePath, templateBytes);
            var targetUri = ToFileUri(targetPath);
            var conflictUri = ToFileUri(conflictPath);
            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);

            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(targetUri, targetText));
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(conflictUri, conflictText));
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
            await process.SendNotificationAsync(
                "vba/hostClassProjectionSnapshot",
                new
                {
                    schemaVersion = 2,
                    revision = 1,
                    project = Path.GetFullPath(projectRoot),
                    document = "Book1",
                    sourceTemplate = Path.GetFullPath(templatePath),
                    state = "present",
                    vbaProjectName = collisionName,
                    sourceTemplateFingerprint = Convert.ToHexString(
                        SHA256.HashData(templateBytes)),
                    classEnumerationComplete = true,
                    classes = Array.Empty<object>()
                });
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri = targetUri, version = 2 },
                    contentChanges = new[] { new { text = targetText } }
                });
            await process.WaitForDiagnosticsAsync(targetUri);

            var rename = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/rename",
                targetUri,
                targetText,
                "InvoiceModule",
                0,
                new { newName = collisionName });

            Assert.False(rename.TryGetProperty("result", out _));
            var error = rename.GetProperty("error");
            Assert.Equal(-32803, error.GetProperty("code").GetInt32());
            var data = error.GetProperty("data");
            Assert.Equal("sameScopeCollision", data.GetProperty("reason").GetString());
            var conflicts = data.GetProperty("conflicts").EnumerateArray().ToArray();
            Assert.Equal(
                ["sourceDeclaration", "containingProject", "referencedProject"],
                conflicts.Select(conflict => conflict
                    .GetProperty("collisionKind")
                    .GetString()));
            Assert.All(conflicts, conflict => Assert.Equal(
                collisionName,
                conflict.GetProperty("name").GetString()));
            Assert.Equal(conflictUri, conflicts[0].GetProperty("uri").GetString());
            Assert.Equal(
                0,
                conflicts[0]
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32());
            Assert.Equal(
                ToFileUri(templatePath),
                conflicts[1].GetProperty("uri").GetString());
            Assert.Equal(
                referenceName,
                conflicts[2].GetProperty("referenceName").GetString());

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_rejects_module_rename_when_an_active_reference_project_name_is_unavailable()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-module-reference-authority-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(
                projectRoot,
                "Unavailable Project Name Authority Library");
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var sourcePath = Path.Combine(sourceRoot, "SourceUnit.bas");
            var templatePath = Path.Combine(sourceRoot, "Book1.xlsm");
            const string text = "Attribute VB_Name = \"InvoiceModule\"";
            var templateBytes = new byte[] { 0x21, 0x32, 0x43, 0x54 };
            File.WriteAllText(sourcePath, text);
            File.WriteAllBytes(templatePath, templateBytes);
            var uri = ToFileUri(sourcePath);
            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.SendNotificationAsync(
                "vba/hostClassProjectionSnapshot",
                new
                {
                    schemaVersion = 2,
                    revision = 1,
                    project = Path.GetFullPath(projectRoot),
                    document = "Book1",
                    sourceTemplate = Path.GetFullPath(templatePath),
                    state = "present",
                    vbaProjectName = "ContainingProject",
                    sourceTemplateFingerprint = Convert.ToHexString(
                        SHA256.HashData(templateBytes)),
                    classEnumerationComplete = true,
                    classes = Array.Empty<object>()
                });
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            var unavailableAuthorityDiagnostics =
                await process.WaitForDiagnosticsAsync(uri);
            Assert.DoesNotContain(
                unavailableAuthorityDiagnostics
                    .GetProperty("params")
                    .GetProperty("diagnostics")
                    .EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.moduleIdentityNameConflict");

            var rename = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/rename",
                uri,
                text,
                "InvoiceModule",
                0,
                new { newName = "BillingModule" });

            Assert.False(rename.TryGetProperty("result", out _));
            var data = rename.GetProperty("error").GetProperty("data");
            Assert.Equal("analysisIncomplete", data.GetProperty("reason").GetString());
            Assert.Equal(
                "referenceProjectNameUnavailable",
                data.GetProperty("condition").GetString());
            Assert.Contains(
                "Unavailable Project Name Authority Library",
                rename.GetProperty("error").GetProperty("message").GetString(),
                StringComparison.Ordinal);

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_prepares_only_the_last_valid_class_module_identity_record()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/Customer.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"LegacyCustomer\"",
            "Attribute VB_Name = \"CustomerRecord\"",
            "Option Explicit"
        ]);
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

        var shadowed = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/prepareRename",
            uri,
            text,
            "LegacyCustomer");
        Assert.Equal(JsonValueKind.Null, shadowed.GetProperty("result").ValueKind);

        var authoritative = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/prepareRename",
            uri,
            text,
            "CustomerRecord");
        var result = authoritative.GetProperty("result");
        Assert.Equal("CustomerRecord", result.GetProperty("placeholder").GetString());
        Assert.Equal(2, result.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(21, result.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(2, result.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(35, result.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_prepares_only_the_last_valid_form_module_identity_record()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/Customer.frm";
        var text = string.Join('\n', [
            "VERSION 5.00",
            "Begin VB.Form Customer",
            "End",
            "Attribute VB_Name = \"LegacyCustomer\"",
            "Attribute VB_Name = \"CustomerDialog\"",
            "Option Explicit"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var shadowed = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/prepareRename",
            uri,
            text,
            "LegacyCustomer");
        Assert.Equal(JsonValueKind.Null, shadowed.GetProperty("result").ValueKind);

        var authoritative = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/prepareRename",
            uri,
            text,
            "CustomerDialog");
        var result = authoritative.GetProperty("result");
        Assert.Equal("CustomerDialog", result.GetProperty("placeholder").GetString());
        Assert.Equal(
            4,
            result.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(
            21,
            result.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_renames_an_explicit_module_identity_and_qualifier_without_renaming_a_deliberately_different_source_path()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string moduleUri = "file:///C:/work/Orders.bas";
        const string consumerUri = "file:///C:/work/Consumer.bas";
        var moduleText = string.Join('\n', [
            "Attribute VB_Name = \"InvoiceModule\"",
            "Public Sub Run()",
            "End Sub"
        ]);
        var consumerText = string.Join('\n', [
            "Attribute VB_Name = \"Consumer\"",
            "Public Sub Execute()",
            "    InvoiceModule.Run",
            "    Debug.Print \"InvoiceModule\"",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(moduleUri, moduleText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(consumerUri, consumerText));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            consumerUri,
            consumerText,
            "InvoiceModule",
            0,
            new { newName = "BillingModule" });

        var result = rename.GetProperty("result");
        Assert.False(result.TryGetProperty("documentChanges", out _));
        var changes = result.GetProperty("changes");
        var declarationEdit = Assert.Single(changes.GetProperty(moduleUri).EnumerateArray());
        Assert.Equal("BillingModule", declarationEdit.GetProperty("newText").GetString());
        Assert.Equal(21, declarationEdit.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        var qualifierEdit = Assert.Single(changes.GetProperty(consumerUri).EnumerateArray());
        Assert.Equal("BillingModule", qualifierEdit.GetProperty("newText").GetString());
        Assert.Equal(4, qualifierEdit.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_enforces_the_31_code_point_module_identity_rename_boundary()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/Orders.bas";
        const string text = "Attribute VB_Name = \"InvoiceModule\"";
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

        var boundaryName = "A" + new string('界', 30);
        var accepted = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "InvoiceModule",
            0,
            new { newName = boundaryName });
        Assert.Equal(
            boundaryName,
            Assert.Single(
                    accepted
                        .GetProperty("result")
                        .GetProperty("changes")
                        .GetProperty(uri)
                        .EnumerateArray())
                .GetProperty("newText")
                .GetString());

        var overLengthName = "A" + new string('界', 31);
        var rejected = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/rename",
            uri,
            text,
            "InvoiceModule",
            0,
            new { newName = overLengthName });
        var error = rejected.GetProperty("error");
        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        Assert.Equal("invalidName", error.GetProperty("data").GetProperty("reason").GetString());
        Assert.Contains("31 Unicode code points", error.GetProperty("message").GetString(), StringComparison.Ordinal);

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_rejects_a_file_following_module_rename_without_ordered_resource_operation_capabilities()
    {
        var sourceRoot = Directory.CreateTempSubdirectory("vba-ls-module-rename-capability-").FullName;
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "InvoiceModule.bas");
            const string text = "Attribute VB_Name = \"InvoiceModule\"";
            File.WriteAllText(sourcePath, text);
            var uri = ToFileUri(sourcePath);
            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));

            var rename = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/rename",
                uri,
                text,
                "InvoiceModule",
                0,
                new { newName = "BillingModule" });

            var error = rename.GetProperty("error");
            Assert.Equal(-32803, error.GetProperty("code").GetInt32());
            Assert.Equal(
                "clientCapabilityMissing",
                error.GetProperty("data").GetProperty("reason").GetString());
            Assert.Contains("documentChanges", error.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Contains("rename resource operation", error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_rejects_an_incapable_file_following_client_before_semantic_collision_planning()
    {
        var sourceRoot = Directory.CreateTempSubdirectory(
            "vba-ls-module-rename-entry-capability-").FullName;
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "InvoiceModule.bas");
            var conflictPath = Path.Combine(sourceRoot, "Existing.bas");
            const string sourceText =
                "Attribute VB_Name = \"InvoiceModule\"";
            const string conflictText =
                "Attribute VB_Name = \"BillingModule\"";
            File.WriteAllText(sourcePath, sourceText);
            File.WriteAllText(conflictPath, conflictText);
            var sourceUri = ToFileUri(sourcePath);
            var conflictUri = ToFileUri(conflictPath);
            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(sourceUri, sourceText));
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(conflictUri, conflictText));

            var rename = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/rename",
                sourceUri,
                sourceText,
                "InvoiceModule",
                0,
                new { newName = "BillingModule" });

            var error = rename.GetProperty("error");
            Assert.Equal(-32803, error.GetProperty("code").GetInt32());
            Assert.Equal(
                "clientCapabilityMissing",
                error.GetProperty("data").GetProperty("reason").GetString());
            Assert.Contains(
                "documentChanges",
                error.GetProperty("message").GetString(),
                StringComparison.Ordinal);
            Assert.False(rename.TryGetProperty("result", out _));

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_returns_ordered_text_and_rename_file_document_changes_for_a_file_following_module_rename()
    {
        var sourceRoot = Directory.CreateTempSubdirectory("vba-ls-module-rename-plan-").FullName;
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "InvoiceModule.bas");
            var destinationPath = Path.Combine(sourceRoot, "BillingModule.bas");
            const string text = "Attribute VB_Name = \"InvoiceModule\"";
            File.WriteAllText(sourcePath, text);
            var uri = ToFileUri(sourcePath);
            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync(new
            {
                workspace = new
                {
                    workspaceEdit = new
                    {
                        documentChanges = true,
                        resourceOperations = new[] { "rename" }
                    }
                }
            });
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));

            var rename = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/rename",
                uri,
                text,
                "InvoiceModule",
                0,
                new { newName = "BillingModule" });

            var result = rename.GetProperty("result");
            Assert.False(result.TryGetProperty("changes", out _));
            var documentChanges = result.GetProperty("documentChanges").EnumerateArray().ToArray();
            Assert.Equal(2, documentChanges.Length);
            var textDocumentEdit = documentChanges[0];
            Assert.Equal(uri, textDocumentEdit.GetProperty("textDocument").GetProperty("uri").GetString());
            Assert.Equal(JsonValueKind.Null, textDocumentEdit.GetProperty("textDocument").GetProperty("version").ValueKind);
            var textEdit = Assert.Single(textDocumentEdit.GetProperty("edits").EnumerateArray());
            Assert.Equal("BillingModule", textEdit.GetProperty("newText").GetString());
            var renameFile = documentChanges[1];
            Assert.Equal("rename", renameFile.GetProperty("kind").GetString());
            Assert.Equal(uri, renameFile.GetProperty("oldUri").GetString());
            Assert.Equal(ToFileUri(destinationPath), renameFile.GetProperty("newUri").GetString());
            Assert.False(renameFile.GetProperty("options").GetProperty("overwrite").GetBoolean());
            Assert.False(renameFile.GetProperty("options").GetProperty("ignoreIfExists").GetBoolean());

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_preserves_an_intentional_case_only_module_and_source_basename_rename()
    {
        var sourceRoot = Directory.CreateTempSubdirectory(
            "vba-ls-module-case-rename-").FullName;
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "invoiceModule.bas");
            var destinationPath = Path.Combine(sourceRoot, "INVOICEMODULE.bas");
            const string text = "Attribute VB_Name = \"InvoiceModule\"";
            File.WriteAllText(sourcePath, text);
            var uri = ToFileUri(sourcePath);
            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync(new
            {
                workspace = new
                {
                    workspaceEdit = new
                    {
                        documentChanges = true,
                        resourceOperations = new[] { "rename" }
                    }
                }
            });
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));

            var rename = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/rename",
                uri,
                text,
                "InvoiceModule",
                0,
                new { newName = "INVOICEMODULE" });

            Assert.False(
                rename.TryGetProperty("error", out var renameError),
                renameError.ToString());
            var documentChanges = rename
                .GetProperty("result")
                .GetProperty("documentChanges")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(2, documentChanges.Length);
            Assert.Equal(
                "INVOICEMODULE",
                Assert.Single(documentChanges[0].GetProperty("edits").EnumerateArray())
                    .GetProperty("newText")
                    .GetString());
            Assert.Equal(uri, documentChanges[1].GetProperty("oldUri").GetString());
            Assert.Equal(
                ToFileUri(destinationPath),
                documentChanges[1].GetProperty("newUri").GetString());
            Assert.True(
                documentChanges[1]
                    .GetProperty("options")
                    .GetProperty("overwrite")
                    .GetBoolean());

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_returns_the_matching_form_sidecar_as_part_of_the_ordered_rename_plan()
    {
        var sourceRoot = Directory.CreateTempSubdirectory(
            "vba-ls-form-rename-plan-").FullName;
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "Dialog.frm");
            var sidecarPath = Path.Combine(sourceRoot, "Dialog.frx");
            var destinationPath = Path.Combine(sourceRoot, "DialogView.frm");
            var sidecarDestinationPath = Path.Combine(sourceRoot, "DialogView.frx");
            var text = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "End",
                "Attribute VB_Name = \"Dialog\""
            ]);
            File.WriteAllText(sourcePath, text);
            File.WriteAllBytes(sidecarPath, [0x01, 0x02, 0x03]);
            var uri = ToFileUri(sourcePath);
            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync(new
            {
                workspace = new
                {
                    workspaceEdit = new
                    {
                        documentChanges = true,
                        resourceOperations = new[] { "rename" }
                    }
                }
            });
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));

            var rename = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/rename",
                uri,
                text,
                "Dialog\"",
                0,
                new { newName = "DialogView" });

            Assert.False(
                rename.TryGetProperty("error", out var renameError),
                renameError.ToString());
            var documentChanges = rename
                .GetProperty("result")
                .GetProperty("documentChanges")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(3, documentChanges.Length);
            Assert.True(documentChanges[0].TryGetProperty("textDocument", out _));
            Assert.Equal("rename", documentChanges[1].GetProperty("kind").GetString());
            Assert.Equal(uri, documentChanges[1].GetProperty("oldUri").GetString());
            Assert.Equal(ToFileUri(destinationPath), documentChanges[1].GetProperty("newUri").GetString());
            Assert.Equal("rename", documentChanges[2].GetProperty("kind").GetString());
            Assert.Equal(ToFileUri(sidecarPath), documentChanges[2].GetProperty("oldUri").GetString());
            Assert.Equal(ToFileUri(sidecarDestinationPath), documentChanges[2].GetProperty("newUri").GetString());

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_rejects_a_form_rename_when_the_sidecar_destination_exists()
    {
        var sourceRoot = Directory.CreateTempSubdirectory(
            "vba-ls-form-sidecar-destination-").FullName;
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "Dialog.frm");
            var sidecarPath = Path.Combine(sourceRoot, "Dialog.frx");
            var sidecarDestinationPath = Path.Combine(sourceRoot, "dialogview.FRX");
            var text = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Dialog",
                "End",
                "Attribute VB_Name = \"Dialog\""
            ]);
            File.WriteAllText(sourcePath, text);
            File.WriteAllBytes(sidecarPath, [0x01, 0x02, 0x03]);
            File.WriteAllBytes(sidecarDestinationPath, [0x09, 0x08, 0x07]);
            var uri = ToFileUri(sourcePath);
            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync(new
            {
                workspace = new
                {
                    workspaceEdit = new
                    {
                        documentChanges = true,
                        resourceOperations = new[] { "rename" }
                    }
                }
            });
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));

            var rename = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/rename",
                uri,
                text,
                "Dialog\"",
                0,
                new { newName = "DialogView" });

            Assert.False(rename.TryGetProperty("result", out _));
            var error = rename.GetProperty("error");
            Assert.Equal(-32803, error.GetProperty("code").GetInt32());
            var data = error.GetProperty("data");
            Assert.Equal(
                "resourceOperationConflict",
                data.GetProperty("reason").GetString());
            Assert.Equal("sidecarConflict", data.GetProperty("condition").GetString());
            Assert.Equal(
                sidecarDestinationPath,
                data.GetProperty("path").GetString(),
                ignoreCase: true);
            Assert.Contains(
                "remove",
                data.GetProperty("guidance").GetString(),
                StringComparison.OrdinalIgnoreCase);

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_rejects_a_file_following_module_rename_when_the_destination_exists()
    {
        var sourceRoot = Directory.CreateTempSubdirectory("vba-ls-module-rename-conflict-").FullName;
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "InvoiceModule.bas");
            var destinationPath = Path.Combine(sourceRoot, "billingmodule.BAS");
            const string text = "Attribute VB_Name = \"InvoiceModule\"";
            File.WriteAllText(sourcePath, text);
            File.WriteAllText(destinationPath, "Attribute VB_Name = \"Existing\"");
            var uri = ToFileUri(sourcePath);
            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync(new
            {
                workspace = new
                {
                    workspaceEdit = new
                    {
                        documentChanges = true,
                        resourceOperations = new[] { "rename" }
                    }
                }
            });
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));

            var rename = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/rename",
                uri,
                text,
                "InvoiceModule",
                0,
                new { newName = "BillingModule" });

            var error = rename.GetProperty("error");
            Assert.Equal(-32803, error.GetProperty("code").GetInt32());
            var data = error.GetProperty("data");
            Assert.Equal("resourceOperationConflict", data.GetProperty("reason").GetString());
            Assert.Equal("destinationExists", data.GetProperty("condition").GetString());
            Assert.Equal(destinationPath, data.GetProperty("path").GetString(), ignoreCase: true);
            Assert.Contains("choose another module name", data.GetProperty("guidance").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.False(rename.TryGetProperty("result", out _));

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_rejects_a_file_following_module_rename_when_the_source_is_missing()
    {
        var sourceRoot = Directory.CreateTempSubdirectory("vba-ls-module-rename-missing-").FullName;
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "InvoiceModule.bas");
            const string text = "Attribute VB_Name = \"InvoiceModule\"";
            File.WriteAllText(sourcePath, text);
            var uri = ToFileUri(sourcePath);
            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync(new
            {
                workspace = new
                {
                    workspaceEdit = new
                    {
                        documentChanges = true,
                        resourceOperations = new[] { "rename" }
                    }
                }
            });
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            File.Delete(sourcePath);

            var rename = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/rename",
                uri,
                text,
                "InvoiceModule",
                0,
                new { newName = "BillingModule" });

            var error = rename.GetProperty("error");
            Assert.Equal(-32803, error.GetProperty("code").GetInt32());
            var data = error.GetProperty("data");
            Assert.Equal("resourceOperationConflict", data.GetProperty("reason").GetString());
            Assert.Equal("sourceMissing", data.GetProperty("condition").GetString());
            Assert.Equal(sourcePath, data.GetProperty("path").GetString(), ignoreCase: true);
            Assert.Contains("restore or reload", data.GetProperty("guidance").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.False(rename.TryGetProperty("result", out _));

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_rejects_rename_of_a_filename_fallback_module_identity_without_returning_an_edit()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string moduleUri = "file:///C:/work/Orders.bas";
        const string consumerUri = "file:///C:/work/Consumer.bas";
        const string moduleText = "Public Sub Run()\nEnd Sub";
        var consumerText = string.Join('\n', [
            "Attribute VB_Name = \"Consumer\"",
            "Public Sub Execute()",
            "    Orders.Run",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(moduleUri, moduleText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(consumerUri, consumerText));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            consumerUri,
            consumerText,
            "Orders",
            0,
            new { newName = "Billing" });

        var error = rename.GetProperty("error");
        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        Assert.Equal(
            "moduleIdentityNotExplicit",
            error.GetProperty("data").GetProperty("reason").GetString());
        Assert.Contains("Attribute VB_Name", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(rename.TryGetProperty("result", out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_allows_an_exact_module_identity_no_op_before_explicit_metadata_authority()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string moduleUri = "file:///C:/work/Orders.bas";
        const string consumerUri = "file:///C:/work/Consumer.bas";
        const string moduleText = "Public Sub Run()\nEnd Sub";
        var consumerText = string.Join('\n', [
            "Attribute VB_Name = \"Consumer\"",
            "Public Sub Execute()",
            "    Orders.Run",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(moduleUri, moduleText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(consumerUri, consumerText));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            consumerUri,
            consumerText,
            "Orders.Run",
            0,
            new { newName = "Orders" });

        Assert.Equal(JsonValueKind.Null, rename.GetProperty("result").ValueKind);
        Assert.False(rename.TryGetProperty("error", out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_rejects_prepare_rename_from_a_filename_fallback_module_qualifier()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string moduleUri = "file:///C:/work/Orders.bas";
        const string consumerUri = "file:///C:/work/Consumer.bas";
        const string moduleText = "Public Sub Run()\nEnd Sub";
        var consumerText = string.Join('\n', [
            "Attribute VB_Name = \"Consumer\"",
            "Public Sub Execute()",
            "    Orders.Run",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(moduleUri, moduleText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(consumerUri, consumerText));

        var prepare = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/prepareRename",
            consumerUri,
            consumerText,
            "Orders");

        var error = prepare.GetProperty("error");
        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        Assert.Equal(
            "moduleIdentityNotExplicit",
            error.GetProperty("data").GetProperty("reason").GetString());
        Assert.Contains("Attribute VB_Name", error.GetProperty("message").GetString(), StringComparison.Ordinal);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_rejects_prepare_rename_for_duplicate_procedural_module_identity_metadata()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string moduleUri = "file:///C:/work/Orders.bas";
        const string consumerUri = "file:///C:/work/Consumer.bas";
        var moduleText = string.Join('\n', [
            "Attribute VB_Name = \"InvoiceModule\"",
            "Attribute VB_Name = \"DuplicateModule\"",
            "Public Sub Run()",
            "End Sub"
        ]);
        var consumerText = string.Join('\n', [
            "Attribute VB_Name = \"Consumer\"",
            "Public Sub Execute()",
            "    Orders.Run",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(moduleUri, moduleText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(consumerUri, consumerText));

        var prepare = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/prepareRename",
            consumerUri,
            consumerText,
            "Orders");

        var error = prepare.GetProperty("error");
        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        var data = error.GetProperty("data");
        Assert.Equal("moduleIdentityInvalid", data.GetProperty("reason").GetString());
        Assert.Equal("duplicate", data.GetProperty("condition").GetString());
        Assert.Contains("re-export or repair", error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_rejects_rename_from_a_qualifier_when_module_identity_metadata_is_invalid()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string moduleUri = "file:///C:/work/Orders.bas";
        const string consumerUri = "file:///C:/work/Consumer.bas";
        var moduleText = string.Join('\n', [
            "Attribute VB_Name = \"InvoiceModule\"",
            "Attribute VB_Name = \"DuplicateModule\"",
            "Public Sub Run()",
            "End Sub"
        ]);
        var consumerText = string.Join('\n', [
            "Attribute VB_Name = \"Consumer\"",
            "Public Sub Execute()",
            "    Orders.Run",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(moduleUri, moduleText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(consumerUri, consumerText));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            consumerUri,
            consumerText,
            "Orders",
            0,
            new { newName = "BillingModule" });

        var error = rename.GetProperty("error");
        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        var data = error.GetProperty("data");
        Assert.Equal("moduleIdentityInvalid", data.GetProperty("reason").GetString());
        Assert.Equal("duplicate", data.GetProperty("condition").GetString());
        Assert.False(rename.TryGetProperty("result", out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_rejects_prepare_rename_for_a_valid_but_misplaced_module_identity_record()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/MisplacedModule.bas";
        var text = string.Join('\n', [
            "Option Explicit",
            "Attribute VB_Name = \"MisplacedModule\"",
            "Public Sub Run()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var prepare = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/prepareRename",
            uri,
            text,
            "MisplacedModule");

        var error = prepare.GetProperty("error");
        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        var data = error.GetProperty("data");
        Assert.Equal("moduleIdentityInvalid", data.GetProperty("reason").GetString());
        Assert.Equal("malformed", data.GetProperty("condition").GetString());
        Assert.Contains(
            "re-export or repair",
            error.GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(prepare.TryGetProperty("result", out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_rejects_prepare_rename_directly_on_malformed_module_identity_metadata()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/BadModule.bas";
        const string text = "Attribute VB_Name = \"123Bad\"";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var prepare = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/prepareRename",
            uri,
            text,
            "123Bad");

        var error = prepare.GetProperty("error");
        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        var data = error.GetProperty("data");
        Assert.Equal("moduleIdentityInvalid", data.GetProperty("reason").GetString());
        Assert.Equal("malformed", data.GetProperty("condition").GetString());
        Assert.Contains("re-export or repair", error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(prepare.TryGetProperty("result", out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_rejects_prepare_rename_on_the_exact_malformed_module_identity_repair_candidate()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/BadModule.bas";
        const string text = "Attribute VB_Name.\"BadIdentity\"";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var prepare = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/prepareRename",
            uri,
            text,
            "VB_Name");

        var error = prepare.GetProperty("error");
        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        var data = error.GetProperty("data");
        Assert.Equal("moduleIdentityInvalid", data.GetProperty("reason").GetString());
        Assert.Equal("malformed", data.GetProperty("condition").GetString());
        Assert.False(prepare.TryGetProperty("result", out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_rejects_rename_directly_on_malformed_module_identity_metadata()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/BadModule.bas";
        const string text = "Attribute VB_Name = \"123Bad\"";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "123Bad",
            0,
            new { newName = "RepairedModule" });

        var error = rename.GetProperty("error");
        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        var data = error.GetProperty("data");
        Assert.Equal("moduleIdentityInvalid", data.GetProperty("reason").GetString());
        Assert.Equal("malformed", data.GetProperty("condition").GetString());
        Assert.False(rename.TryGetProperty("result", out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_renames_class_type_occurrences_without_manufacturing_an_object_receiver_occurrence()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string classUri = "file:///C:/work/WorkerSource.cls";
        const string consumerUri = "file:///C:/work/Consumer.bas";
        var classText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Attribute VB_PredeclaredId = False",
            "Public Sub Run()",
            "End Sub"
        ]);
        var consumerText = string.Join('\n', [
            "Attribute VB_Name = \"Consumer\"",
            "Public Sub Execute()",
            "    Dim worker As Worker",
            "    Set worker = New Worker",
            "    worker.Run",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(classUri, classText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(consumerUri, consumerText));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            classUri,
            classText,
            "Worker",
            0,
            new { newName = "Employee" });

        var changes = rename.GetProperty("result").GetProperty("changes");
        Assert.Single(changes.GetProperty(classUri).EnumerateArray());
        var consumerEdits = changes.GetProperty(consumerUri).EnumerateArray().ToArray();
        Assert.Equal(2, consumerEdits.Length);
        Assert.Contains(
            consumerEdits,
            edit => edit.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32() == 2
                && edit.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32() == 18);
        Assert.Contains(
            consumerEdits,
            edit => edit.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32() == 3
                && edit.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32() == 21);
        Assert.DoesNotContain(
            consumerEdits,
            edit => edit.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32() == 4);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_renames_a_conclusively_resolved_source_interface_declaration_prefix()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string interfaceUri = "file:///C:/work/Contract.cls";
        const string implementationUri = "file:///C:/work/Worker.cls";
        var interfaceText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"I_Worker\"",
            "Public Sub Run()",
            "End Sub"
        ]);
        var implementationText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Implements I_Worker",
            "Private Sub I_Worker_Run()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            interfaceUri,
            interfaceText,
            "I_Worker",
            0,
            new { newName = "I_Service" });

        Assert.False(
            rename.TryGetProperty("error", out var renameError),
            renameError.ToString());
        var changes = rename.GetProperty("result").GetProperty("changes");
        Assert.Single(changes.GetProperty(interfaceUri).EnumerateArray());
        var implementationEdits = changes
            .GetProperty(implementationUri)
            .EnumerateArray()
            .OrderBy(edit => edit
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32())
            .ToArray();
        Assert.Equal(2, implementationEdits.Length);
        Assert.Equal(2, implementationEdits[0].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(3, implementationEdits[1].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(12, implementationEdits[1].GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(20, implementationEdits[1].GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());
        Assert.All(implementationEdits, edit =>
            Assert.Equal("I_Service", edit.GetProperty("newText").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_prepares_only_a_conclusively_resolved_source_interface_declaration_prefix()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string interfaceUri = "file:///C:/work/Contract.cls";
        const string implementationUri = "file:///C:/work/Worker.cls";
        var interfaceText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"I_Worker\"",
            "Public Sub Run()",
            "End Sub"
        ]);
        var implementationText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Implements I_Worker",
            "Private Sub I_Worker_Run()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var prepare = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/prepareRename",
            implementationUri,
            implementationText,
            "I_Worker_Run");

        var result = prepare.GetProperty("result");
        Assert.Equal("I_Worker", result.GetProperty("placeholder").GetString());
        var range = result.GetProperty("range");
        Assert.Equal(3, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(12, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(3, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(20, range.GetProperty("end").GetProperty("character").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_does_not_prepare_an_indeterminate_source_interface_declaration_prefix()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string interfaceUri = "file:///C:/work/Contract.cls";
        const string implementationUri = "file:///C:/work/Worker.cls";
        var interfaceText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"I_Worker\"",
            "Public Sub Run()",
            "End Sub"
        ]);
        var implementationText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "#If VBA7 Then",
            "Implements I_Worker",
            "Private Sub I_Worker_Run()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var prepare = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/prepareRename",
            implementationUri,
            implementationText,
            "I_Worker_Run");

        var error = prepare.GetProperty("error");
        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        Assert.Equal(
            "analysisIncomplete",
            error.GetProperty("data").GetProperty("reason").GetString());
        Assert.False(prepare.TryGetProperty("result", out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_does_not_rename_a_local_receiver_that_shadows_a_predeclared_class()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string classUri = "file:///C:/work/WorkerSource.cls";
        const string consumerUri = "file:///C:/work/Consumer.bas";
        var classText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Attribute VB_PredeclaredId = True",
            "Public Sub Run()",
            "End Sub"
        ]);
        var consumerText = string.Join('\n', [
            "Attribute VB_Name = \"Consumer\"",
            "Public Sub Execute()",
            "    Dim Worker As Worker",
            "    Set Worker = New Worker",
            "    Worker.Run",
            "End Sub",
            "Public Sub ExecuteDefault()",
            "    Worker.Run",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(classUri, classText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(consumerUri, consumerText));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            classUri,
            classText,
            "Worker",
            0,
            new { newName = "Employee" });

        Assert.False(
            rename.TryGetProperty("error", out var renameError),
            renameError.ToString());
        var edits = rename
            .GetProperty("result")
            .GetProperty("changes")
            .GetProperty(consumerUri)
            .EnumerateArray()
            .ToArray();
        Assert.Equal(3, edits.Length);
        Assert.Contains(edits, edit =>
            edit.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32() == 2
            && edit.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32() == 18);
        Assert.Contains(edits, edit =>
            edit.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32() == 3
            && edit.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32() == 21);
        Assert.Contains(edits, edit =>
            edit.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32() == 7
            && edit.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32() == 4);
        Assert.DoesNotContain(edits, edit =>
            edit.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32() == 4);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_renames_source_targets_and_rejects_non_renameable_inputs()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "",
            "Public Function BuildValue() As String",
            "End Function",
            "",
            "Public Sub Run()",
            "    BuildValue",
            "    Debug.Print \"BuildValue\"",
            "' BuildValue remains a comment.",
            "    Rem BuildValue remains a comment.",
            "'* @details BuildValue remains documentation.",
            "End Sub"
        ]);
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(process, 2,
            "textDocument/rename",
            uri,
            text,
            "BuildValue",
            0,
            new { newName = "CreateValue" });
        var edits = rename
            .GetProperty("result")
            .GetProperty("changes")
            .GetProperty(uri)
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, edits.Length);
        Assert.All(edits, edit => Assert.Equal("CreateValue", edit.GetProperty("newText").GetString()));
        Assert.Contains(edits, edit => edit.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32() == 3);
        Assert.Contains(edits, edit => edit.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32() == 7);

        var cp2Rename = await SendPositionRequestAsync(process, 3,
            "textDocument/rename",
            uri,
            text,
            "BuildValue",
            0,
            new { newName = "\u00a0" });
        var cp2Edits = cp2Rename
            .GetProperty("result")
            .GetProperty("changes")
            .GetProperty(uri)
            .EnumerateArray()
            .ToArray();
        Assert.All(cp2Edits, edit => Assert.Equal("\u00a0", edit.GetProperty("newText").GetString()));

        var invalidRename = await SendPositionRequestAsync(process, 4,
            "textDocument/rename",
            uri,
            text,
            "BuildValue",
            0,
            new { newName = " BuildValue" });
        var invalidError = invalidRename.GetProperty("error");
        Assert.Equal(-32803, invalidError.GetProperty("code").GetInt32());
        Assert.Contains(
            "valid VBA identifier",
            invalidError.GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "invalidName",
            invalidError
                .GetProperty("data")
                .GetProperty("reason")
                .GetString());

        var unchangedRename = await SendPositionRequestAsync(process, 5,
            "textDocument/rename",
            uri,
            text,
            "BuildValue",
            0,
            new { newName = "BuildValue" });
        Assert.Equal(
            JsonValueKind.Null,
            unchangedRename.GetProperty("result").ValueKind);

        var caseOnlyRename = await SendPositionRequestAsync(process, 6,
            "textDocument/rename",
            uri,
            text,
            "BuildValue",
            0,
            new { newName = "buildvalue" });
        var caseOnlyEdits = caseOnlyRename
            .GetProperty("result")
            .GetProperty("changes")
            .GetProperty(uri)
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, caseOnlyEdits.Length);
        Assert.All(
            caseOnlyEdits,
            edit => Assert.Equal(
                "buildvalue",
                edit.GetProperty("newText").GetString()));

        var stringRename = await SendPositionRequestAsync(process, 7,
            "textDocument/rename",
            uri,
            text,
            "\"BuildValue\"",
            1,
            new { newName = "IgnoredValue" });
        var stringError = stringRename.GetProperty("error");
        Assert.Equal(-32803, stringError.GetProperty("code").GetInt32());
        Assert.Contains(
            "rename target",
            stringError.GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "notRenameTarget",
            stringError
                .GetProperty("data")
                .GetProperty("reason")
                .GetString());

        var stringPrepare = await SendPositionRequestAsync(process, 8,
            "textDocument/prepareRename",
            uri,
            text,
            "\"BuildValue\"",
            1);
        Assert.Equal(
            JsonValueKind.Null,
            stringPrepare.GetProperty("result").ValueKind);

        var malformedRename = await process.SendRequestAsync(9,
            "textDocument/rename",
            new
            {
                textDocument = new { uri },
                position = new { line = 3, character = "Public Function ".Length },
                newName = 42
            });
        Assert.Equal(
            -32602,
            malformedRename
                .GetProperty("error")
                .GetProperty("code")
                .GetInt32());

        await process.ShutdownAsync(10);
    }

    [Fact]
    public async Task Server_renames_every_physical_conditional_callable_declaration_and_reference()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalRename.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalRename\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function BuildValue(ByRef key As Long) As String",
            "End Function",
            "#Else",
            "Public Function buildvalue(ByRef key As Long, Optional ByVal fallback As Long = 0) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim keyValue As Long",
            "    Debug.Print BuildValue(keyValue, keyValue)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var prepare = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/prepareRename",
            uri,
            text,
            "    Debug.Print BuildValue(keyValue, keyValue)",
            "    Debug.Print ".Length);
        var prepareResult = prepare.GetProperty("result");
        Assert.Equal(
            "BuildValue",
            prepareResult.GetProperty("placeholder").GetString());
        Assert.Equal(
            10,
            prepareResult
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());
        Assert.Equal(
            "    Debug.Print ".Length,
            prepareResult
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("character")
                .GetInt32());
        Assert.Equal(
            "    Debug.Print BuildValue".Length,
            prepareResult
                .GetProperty("range")
                .GetProperty("end")
                .GetProperty("character")
                .GetInt32());

        var rename = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/rename",
            uri,
            text,
            "    Debug.Print BuildValue(keyValue, keyValue)",
            "    Debug.Print ".Length,
            new { newName = "CreateValue" });
        Assert.True(
            rename.TryGetProperty("result", out var result),
            rename.ToString());
        var edits = result
            .GetProperty("changes")
            .GetProperty(uri)
            .EnumerateArray()
            .ToArray();

        Assert.Equal(
            [2, 5, 10],
            edits.Select(edit => edit
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32()));
        Assert.All(
            edits,
            edit => Assert.Equal(
                "CreateValue",
                edit.GetProperty("newText").GetString()));

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_renames_visibility_variants_from_separate_conditional_blocks_through_an_external_use()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string workerUri = "file:///C:/work/VisibilityWorker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"VisibilityWorker\"",
            "#If PRIVATE_CONFIGURATION Then",
            "Private Function buildValue() As Long",
            "End Function",
            "#End If",
            "#If FRIEND_CONFIGURATION Then",
            "Friend Function BuildValue() As Long",
            "End Function",
            "#End If",
            "#If PUBLIC_CONFIGURATION Then",
            "Public Function BUILDVALUE() As Long",
            "End Function",
            "#End If"
        ]);
        const string callerUri = "file:///C:/work/VisibilityCaller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"VisibilityCaller\"",
            "Public Sub Run()",
            "    Dim worker As VisibilityWorker",
            "    Set worker = New VisibilityWorker",
            "    Debug.Print worker.BUILDVALUE()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var prepare = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/prepareRename",
            callerUri,
            callerText,
            "    Debug.Print worker.BUILDVALUE()",
            "    Debug.Print worker.".Length);
        Assert.Equal(
            "buildValue",
            prepare
                .GetProperty("result")
                .GetProperty("placeholder")
                .GetString());

        var rename = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/rename",
            callerUri,
            callerText,
            "    Debug.Print worker.BUILDVALUE()",
            "    Debug.Print worker.".Length,
            new { newName = "CreateValue" });
        Assert.True(
            rename.TryGetProperty("result", out var renameResult),
            rename.ToString());
        var changes = renameResult.GetProperty("changes");
        var workerEdits = changes
            .GetProperty(workerUri)
            .EnumerateArray()
            .ToArray();
        var callerEdit = Assert.Single(
            changes.GetProperty(callerUri).EnumerateArray());

        Assert.Equal(
            [3, 7, 11],
            workerEdits.Select(edit => edit
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32()));
        Assert.Equal(
            4,
            callerEdit
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());
        Assert.All(
            workerEdits.Append(callerEdit),
            edit => Assert.Equal(
                "CreateValue",
                edit.GetProperty("newText").GetString()));

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_renames_a_one_variant_conditional_family()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/SingleVariantRename.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"SingleVariantRename\"",
            "#If ENABLED Then",
            "Public ResultValue As Long",
            "#End If",
            "Public Sub Run()",
            "    Debug.Print ResultValue",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "    Debug.Print ResultValue",
            "    Debug.Print ".Length,
            new { newName = "CurrentValue" });
        var edits = rename
            .GetProperty("result")
            .GetProperty("changes")
            .GetProperty(uri)
            .EnumerateArray()
            .ToArray();

        Assert.Equal(
            [2, 5],
            edits.Select(edit => edit
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32()));
        Assert.All(
            edits,
            edit => Assert.Equal(
                "CurrentValue",
                edit.GetProperty("newText").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_preserves_nested_branch_paths_while_renaming_the_family()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/NestedConditionalRename.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"NestedConditionalRename\"",
            "#If OUTER_CONFIGURATION Then",
            "#If INNER_CONFIGURATION Then",
            "Public ResultValue As Long",
            "#Else",
            "Public resultvalue As Long",
            "#End If",
            "#Else",
            "Public RESULTVALUE As Long",
            "#End If",
            "Public Sub Run()",
            "    Debug.Print ResultValue",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "    Debug.Print ResultValue",
            "    Debug.Print ".Length,
            new { newName = "CurrentValue" });
        var edits = rename
            .GetProperty("result")
            .GetProperty("changes")
            .GetProperty(uri)
            .EnumerateArray()
            .ToArray();

        Assert.Equal(
            [3, 5, 8, 11],
            edits.Select(edit => edit
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32()));
        Assert.All(
            edits,
            edit => Assert.Equal(
                "CurrentValue",
                edit.GetProperty("newText").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_rejects_a_rename_when_conditional_call_compatibility_is_indeterminate()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalRecoveredRename.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalRecoveredRename\"",
            "#If INVALID_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As String, ByVal key As Long) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Index As Long) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Dim result As String",
            "    result = ResolveValue(1)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "    result = ResolveValue(1)",
            "    result = ".Length,
            new { newName = "ComputeValue" });

        Assert.False(rename.TryGetProperty("result", out _));
        var error = rename.GetProperty("error");
        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        Assert.Equal(
            "analysisIncomplete",
            error.GetProperty("data").GetProperty("reason").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_proves_conditional_call_compatibility_for_a_qualified_family_use()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string workerUri = "file:///C:/work/ConditionalWorker.bas";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalWorker\"",
            "#If INVALID_CONFIGURATION Then",
            "Public Function ResolveValue(ByVal Key As String, ByVal key As Long) As String",
            "End Function",
            "#Else",
            "Public Function resolvevalue(ByVal Index As Long) As String",
            "End Function",
            "#End If"
        ]);
        const string callerUri = "file:///C:/work/ConditionalCaller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalCaller\"",
            "Public Sub Run()",
            "    Debug.Print ConditionalWorker.ResolveValue(1)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            callerUri,
            callerText,
            "    Debug.Print ConditionalWorker.ResolveValue(1)",
            "    Debug.Print ConditionalWorker.".Length,
            new { newName = "ComputeValue" });

        Assert.False(rename.TryGetProperty("result", out _));
        var error = rename.GetProperty("error");
        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        Assert.Equal(
            "analysisIncomplete",
            error.GetProperty("data").GetProperty("reason").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_prioritizes_a_conclusive_binding_change_over_incomplete_conditional_call_evidence()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalRenameCapture.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalRenameCapture\"",
            "#If INVALID_CONFIGURATION Then",
            "Public Function BuildValue(ByVal Key As String, ByVal key As Long) As String",
            "End Function",
            "#Else",
            "Public Function buildvalue(ByVal Index As Long) As String",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Debug.Print BuildValue(1)",
            "    Debug.Print Captured",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "    Debug.Print BuildValue(1)",
            "    Debug.Print ".Length,
            new { newName = "Captured" });

        Assert.False(rename.TryGetProperty("result", out _));
        var error = rename.GetProperty("error");
        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        Assert.Equal(
            "resolutionChanged",
            error.GetProperty("data").GetProperty("reason").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_renames_conditional_function_result_assignments_as_non_call_occurrences()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalFunctionResultRename.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalFunctionResultRename\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function BuildValue() As Long",
            "    BuildValue = 1",
            "End Function",
            "#Else",
            "Public Function buildvalue() As Long",
            "    buildvalue = 2",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Debug.Print BuildValue()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "    Debug.Print BuildValue()",
            "    Debug.Print ".Length,
            new { newName = "CreateValue" });
        var edits = rename
            .GetProperty("result")
            .GetProperty("changes")
            .GetProperty(uri)
            .EnumerateArray()
            .ToArray();

        Assert.Equal(
            [2, 3, 6, 7, 11],
            edits.Select(edit => edit
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32()));
        Assert.All(
            edits,
            edit => Assert.Equal(
                "CreateValue",
                edit.GetProperty("newText").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_renames_conditional_array_families_without_treating_indexing_as_a_call()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalArrayRename.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalArrayRename\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Values(0 To 1) As Long",
            "#Else",
            "Public values(0 To 2) As Long",
            "#End If",
            "Public Sub Run()",
            "    Debug.Print Values(0)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "    Debug.Print Values(0)",
            "    Debug.Print ".Length,
            new { newName = "Items" });
        var edits = rename
            .GetProperty("result")
            .GetProperty("changes")
            .GetProperty(uri)
            .EnumerateArray()
            .ToArray();

        Assert.Equal(
            [2, 4, 7],
            edits.Select(edit => edit
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32()));
        Assert.All(
            edits,
            edit => Assert.Equal(
                "Items",
                edit.GetProperty("newText").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_rejects_a_rename_from_an_incompletely_recovered_conditional_family()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/MalformedConditionalRename.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"MalformedConditionalRename\"",
            "#If VBA7 Then",
            "Public Function BuildValue() As String",
            "End Function",
            "#Else",
            "Public Function buildvalue() As String",
            "End Function"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var prepare = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/prepareRename",
            uri,
            text,
            "Public Function BuildValue",
            "Public Function ".Length);
        Assert.False(prepare.TryGetProperty("result", out _));
        Assert.Equal(
            "analysisIncomplete",
            prepare
                .GetProperty("error")
                .GetProperty("data")
                .GetProperty("reason")
                .GetString());

        var rename = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/rename",
            uri,
            text,
            "Public Function BuildValue",
            "Public Function ".Length,
            new { newName = "CreateValue" });
        Assert.False(rename.TryGetProperty("result", out _));
        var error = rename.GetProperty("error");
        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        Assert.Equal(
            "analysisIncomplete",
            error.GetProperty("data").GetProperty("reason").GetString());

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_rejects_a_rename_when_a_conditional_family_sibling_is_incompletely_recovered()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/PartiallyRecoveredFamily.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"PartiallyRecoveredFamily\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function BuildValue() As Long",
            "End Function",
            "#End If",
            "#If SECOND_CONFIGURATION Then",
            "Public Function buildvalue() As Long",
            "End Function"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "Public Function BuildValue",
            "Public Function ".Length,
            new { newName = "CreateValue" });
        Assert.False(rename.TryGetProperty("result", out _));
        Assert.Equal(
            "analysisIncomplete",
            rename
                .GetProperty("error")
                .GetProperty("data")
                .GetProperty("reason")
                .GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_does_not_reject_a_sound_family_for_an_unrelated_malformed_conditional()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/UnrelatedMalformedConditional.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"UnrelatedMalformedConditional\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Function BuildValue() As Long",
            "End Function",
            "#Else",
            "Public Function buildvalue() As Long",
            "End Function",
            "#End If",
            "Public Sub Run()",
            "    Debug.Print BuildValue()",
            "End Sub",
            "#If UNRELATED_CONFIGURATION Then",
            "Public OtherValue As Long"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "    Debug.Print BuildValue()",
            "    Debug.Print ".Length,
            new { newName = "CreateValue" });
        Assert.True(
            rename.TryGetProperty("result", out var result),
            rename.ToString());
        var edits = result
            .GetProperty("changes")
            .GetProperty(uri)
            .EnumerateArray()
            .ToArray();

        Assert.Equal(
            [2, 5, 9],
            edits.Select(edit => edit
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_reports_a_collision_from_any_conditional_parent_family_variant()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string firstUri = "file:///C:/work/ConditionalPayloadA.bas";
        var firstText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalPayloadA\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Type Payload",
            "    Value As Long",
            "    Taken As Long",
            "End Type",
            "#End If"
        ]);
        const string secondUri = "file:///C:/work/ConditionalPayloadB.bas";
        var secondText = string.Join('\n', [
            "Attribute VB_Name = \"ConditionalPayloadB\"",
            "#If SECOND_CONFIGURATION Then",
            "Public Type payload",
            "    value As Long",
            "    Taken As Long",
            "End Type",
            "#End If"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(firstUri, firstText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(secondUri, secondText));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            firstUri,
            firstText,
            "    Value As Long",
            "    ".Length,
            new { newName = "Taken" });
        Assert.False(rename.TryGetProperty("result", out _));
        var error = rename.GetProperty("error");
        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        var data = error.GetProperty("data");
        Assert.Equal(
            "sameScopeCollision",
            data.GetProperty("reason").GetString());
        var conflicts = data
            .GetProperty("conflicts")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, conflicts.Length);
        Assert.Equal(
            [(firstUri, 4), (secondUri, 4)],
            conflicts.Select(conflict => (
                conflict.GetProperty("uri").GetString(),
                conflict
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32())));
        Assert.All(
            conflicts,
            conflict => Assert.Equal(
                "Taken",
                conflict.GetProperty("name").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_renames_every_physical_conditional_property_accessor_variant()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalPropertyRename.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalPropertyRename\"",
            "#If FIRST_CONFIGURATION Then",
            "Public Property Get Value() As Long",
            "End Property",
            "#Else",
            "Public Property Get VALUE() As Long",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    Debug.Print Value",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "Public Property Get Value",
            "Public Property Get ".Length,
            new { newName = "ResultValue" });
        Assert.True(
            rename.TryGetProperty("result", out var result),
            rename.ToString());
        var edits = result
            .GetProperty("changes")
            .GetProperty(uri)
            .EnumerateArray()
            .ToArray();

        Assert.Equal(
            [3, 6, 10],
            edits.Select(edit => edit
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32()));
        Assert.All(
            edits,
            edit => Assert.Equal(
                "ResultValue",
                edit.GetProperty("newText").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_case_renames_a_mixed_conditional_property_family_from_a_noncanonical_variant()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalRenameProperty.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalRenameProperty\"",
            "#If FUNCTION_CONFIGURATION Then",
            "Public Function Value() As Long",
            "End Function",
            "#End If",
            "#If GET_CONFIGURATION Then",
            "Public Property Get value() As Long",
            "End Property",
            "#End If",
            "#If LET_CONFIGURATION Then",
            "Public Property Let VALUE(ByVal assigned As Long)",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    Debug.Print Value",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var prepare = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/prepareRename",
            uri,
            text,
            "Public Property Get value",
            "Public Property Get ".Length);
        var prepareResult = prepare.GetProperty("result");
        Assert.Equal("Value", prepareResult.GetProperty("placeholder").GetString());
        Assert.Equal(
            7,
            prepareResult
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());
        Assert.Equal(
            "Public Property Get ".Length,
            prepareResult
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("character")
                .GetInt32());
        Assert.Equal(
            "Public Property Get value".Length,
            prepareResult
                .GetProperty("range")
                .GetProperty("end")
                .GetProperty("character")
                .GetInt32());

        var unchangedRename = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/rename",
            uri,
            text,
            "Public Property Get value",
            "Public Property Get ".Length,
            new { newName = "Value" });
        Assert.Equal(
            JsonValueKind.Null,
            unchangedRename.GetProperty("result").ValueKind);

        var rename = await SendPositionRequestAsync(
            process,
            4,
            "textDocument/rename",
            uri,
            text,
            "Public Property Get value",
            "Public Property Get ".Length,
            new { newName = "value" });
        var edits = rename
            .GetProperty("result")
            .GetProperty("changes")
            .GetProperty(uri)
            .EnumerateArray()
            .ToArray();

        Assert.Equal(
            [3, 7, 11, 15],
            edits.Select(edit => edit
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32()));
        Assert.All(
            edits,
            edit => Assert.Equal(
                "value",
                edit.GetProperty("newText").GetString()));

        await process.ShutdownAsync(5);
    }

    [Fact]
    public async Task Server_renames_the_complete_property_from_a_conditional_setter_use()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/MixedPropertyRename.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"MixedPropertyRename\"",
            "Public Property Get value() As Long",
            "End Property",
            "#If FIRST_WRITE_CONFIGURATION Then",
            "Public Property Let Value(ByVal firstAssigned As Long)",
            "End Property",
            "#Else",
            "Public Property Let VALUE(ByVal secondAssigned As Long)",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    Value = 1",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var prepare = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/prepareRename",
            uri,
            text,
            "    Value = 1",
            "    ".Length);
        var prepareResult = prepare.GetProperty("result");
        Assert.Equal("value", prepareResult.GetProperty("placeholder").GetString());
        Assert.Equal(
            12,
            prepareResult
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());

        var rename = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/rename",
            uri,
            text,
            "    Value = 1",
            "    ".Length,
            new { newName = "Amount" });
        var edits = rename
            .GetProperty("result")
            .GetProperty("changes")
            .GetProperty(uri)
            .EnumerateArray()
            .ToArray();

        Assert.Equal(
            [2, 5, 8, 12],
            edits.Select(edit => edit
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32()));
        Assert.All(
            edits,
            edit => Assert.Equal(
                "Amount",
                edit.GetProperty("newText").GetString()));

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_renames_get_let_and_every_conditional_set_variant_from_a_set_use()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ConditionalObjectPropertyRename.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ConditionalObjectPropertyRename\"",
            "Public Property Get Value() As Variant",
            "End Property",
            "Public Property Let Value(ByVal assigned As Variant)",
            "End Property",
            "#If FIRST_OBJECT_CONFIGURATION Then",
            "Public Property Set value(ByVal assigned As Object)",
            "End Property",
            "#Else",
            "Public Property Set VALUE(ByVal assigned As Object)",
            "End Property",
            "#End If",
            "Public Sub Run()",
            "    Dim assigned As Object",
            "    Set Value = assigned",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "    Set Value = assigned",
            "    Set ".Length,
            new { newName = "CurrentValue" });
        Assert.True(
            rename.TryGetProperty("result", out var result),
            rename.ToString());
        var edits = result
            .GetProperty("changes")
            .GetProperty(uri)
            .EnumerateArray()
            .ToArray();

        Assert.Equal(
            [2, 4, 7, 10, 15],
            edits.Select(edit => edit
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32()));
        Assert.All(
            edits,
            edit => Assert.Equal(
                "CurrentValue",
                edit.GetProperty("newText").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_renames_complementary_property_accessors_atomically()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/Properties.cls";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Properties\"",
            "Public Property Get Value() As Variant",
            "End Property",
            "Public Property Let Value(ByVal assigned As Variant)",
            "End Property",
            "Public Property Set Value(ByVal assigned As Object)",
            "End Property",
            "Public Sub Run()",
            "    Dim current As Variant",
            "    current = Value",
            "    Value = current",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "Value",
            0,
            new { newName = "ResultValue" });
        var edits = rename
            .GetProperty("result")
            .GetProperty("changes")
            .GetProperty(uri)
            .EnumerateArray()
            .ToArray();
        var editedLines = edits
            .Select(edit => edit
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32())
            .ToArray();

        Assert.Equal([1, 3, 5, 9, 10], editedLines);
        Assert.All(
            edits,
            edit => Assert.Equal(
                "ResultValue",
                edit.GetProperty("newText").GetString()));

        var setRename = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/rename",
            uri,
            text,
            "Value",
            2,
            new { newName = "OtherValue" });
        var setEditedLines = setRename
            .GetProperty("result")
            .GetProperty("changes")
            .GetProperty(uri)
            .EnumerateArray()
            .Select(edit => edit
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32())
            .ToArray();
        Assert.Equal([1, 3, 5, 9, 10], setEditedLines);

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_uses_canonical_property_family_casing_from_a_setter()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/PropertyCasing.cls";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"PropertyCasing\"",
            "Public Property Get Value() As Variant",
            "End Property",
            "Public Property Let VALUE(ByVal assigned As Variant)",
            "End Property",
            "Public Property Set VALUE(ByVal assigned As Object)",
            "End Property",
            "Public Sub Run()",
            "    Dim current As Variant",
            "    current = Value",
            "    Value = current",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var prepare = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/prepareRename",
            uri,
            text,
            "Property Set VALUE",
            "Property Set ".Length);
        var prepareResult = prepare.GetProperty("result");
        Assert.Equal(
            5,
            prepareResult
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());
        Assert.Equal(
            "Value",
            prepareResult.GetProperty("placeholder").GetString());

        var rename = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/rename",
            uri,
            text,
            "Property Set VALUE",
            "Property Set ".Length,
            new { newName = "VALUE" });
        Assert.True(
            rename.TryGetProperty("result", out var result),
            rename.ToString());
        var edits = result
            .GetProperty("changes")
            .GetProperty(uri)
            .EnumerateArray()
            .ToArray();

        Assert.Equal(
            [1, 3, 5, 9, 10],
            edits.Select(edit => edit
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32()));
        Assert.All(edits, edit => Assert.Equal(
            "VALUE",
            edit.GetProperty("newText").GetString()));

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_rejects_duplicate_property_accessors_as_a_collision()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/DuplicateProperty.cls";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"DuplicateProperty\"",
            "Public Property Get Value() As Variant",
            "End Property",
            "Public Property Get Value() As Variant",
            "End Property",
            "Public Property Let Value(ByVal assigned As Variant)",
            "End Property",
            "Public Property Set Value(ByVal assigned As Object)",
            "End Property"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "Value",
            0,
            new { newName = "Renamed" });
        var error = rename.GetProperty("error");
        var conflict = Assert.Single(error
            .GetProperty("data")
            .GetProperty("conflicts")
            .EnumerateArray());

        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        Assert.Equal(
            "sameScopeCollision",
            error.GetProperty("data").GetProperty("reason").GetString());
        Assert.Equal(
            3,
            conflict.GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());
        Assert.False(rename.TryGetProperty("result", out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_rejects_an_underscore_in_an_event_rename_name()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/EventNames.cls";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"EventNames\"",
            "Public Event Saved()",
            "Public Sub Fire()",
            "    RaiseEvent Saved",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "Saved",
            0,
            new { newName = "Bad_Name" });
        var error = rename.GetProperty("error");

        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        Assert.Equal(
            "invalidName",
            error.GetProperty("data").GetProperty("reason").GetString());
        Assert.False(rename.TryGetProperty("result", out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_rejects_an_unchanged_underscore_event_name()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/RecoveredEventName.cls";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"RecoveredEventName\"",
            "Public Event Bad_Name()",
            "Public Sub Fire()",
            "    RaiseEvent Bad_Name",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "Bad_Name",
            0,
            new { newName = "Bad_Name" });
        var error = rename.GetProperty("error");

        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        Assert.Equal(
            "invalidName",
            error.GetProperty("data").GetProperty("reason").GetString());
        Assert.False(rename.TryGetProperty("result", out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_rejects_a_rename_that_would_capture_a_target_reference()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string libraryUri = "file:///C:/work/Library.bas";
        var libraryText = string.Join('\n', [
            "Attribute VB_Name = \"Library\"",
            "Public Function BuildValue() As Long",
            "    BuildValue = 1",
            "End Function"
        ]);
        const string callerUri = "file:///C:/work/Caller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"Caller\"",
            "Public Sub Run()",
            "    Dim CreateValue As Long",
            "    Debug.Print BuildValue",
            "    Debug.Print CreateValue",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(libraryUri, libraryText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            callerUri,
            callerText,
            "BuildValue",
            0,
            new { newName = "CreateValue" });
        var error = rename.GetProperty("error");

        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        Assert.Equal(
            "resolutionChanged",
            error.GetProperty("data").GetProperty("reason").GetString());
        Assert.Contains(
            "binding",
            error.GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(rename.TryGetProperty("result", out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_reports_every_same_scope_rename_collision_in_source_order()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/Collisions.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Collisions\"",
            "Public Sub Run(ByVal Taken As Long)",
            "    Dim Original As Long",
            "    Dim Taken As Long",
            "    Debug.Print Original",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "Original",
            0,
            new { newName = "Taken" });
        var error = rename.GetProperty("error");
        var conflicts = error
            .GetProperty("data")
            .GetProperty("conflicts")
            .EnumerateArray()
            .ToArray();

        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        Assert.Equal(
            "sameScopeCollision",
            error.GetProperty("data").GetProperty("reason").GetString());
        Assert.Equal(2, conflicts.Length);
        Assert.Equal(
            [1, 3],
            conflicts.Select(conflict => conflict
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32()));
        Assert.All(conflicts, conflict =>
        {
            Assert.Equal(
                "sourceDeclaration",
                conflict.GetProperty("collisionKind").GetString());
            Assert.Equal("Taken", conflict.GetProperty("name").GetString());
            Assert.Equal(uri, conflict.GetProperty("uri").GetString());
        });
        Assert.False(rename.TryGetProperty("result", out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_rejects_a_procedure_rename_to_its_local_name()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ProcedureLocalCollision.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ProcedureLocalCollision\"",
            "Public Function BuildValue() As Long",
            "    Dim Taken As Long",
            "    BuildValue = Taken",
            "End Function"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "BuildValue",
            0,
            new { newName = "Taken" });
        var error = rename.GetProperty("error");
        var conflict = Assert.Single(error
            .GetProperty("data")
            .GetProperty("conflicts")
            .EnumerateArray());

        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        Assert.Equal(
            "sameScopeCollision",
            error.GetProperty("data").GetProperty("reason").GetString());
        Assert.Equal(
            2,
            conflict.GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());
        Assert.False(rename.TryGetProperty("result", out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_rejects_a_procedure_rename_to_a_module_enum_member()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/ProcedureEnumCollision.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"ProcedureEnumCollision\"",
            "Public Enum Choice",
            "    Taken",
            "End Enum",
            "Public Function BuildValue() As Long",
            "    BuildValue = 1",
            "End Function"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "BuildValue",
            0,
            new { newName = "Taken" });
        var error = rename.GetProperty("error");
        var conflict = Assert.Single(error
            .GetProperty("data")
            .GetProperty("conflicts")
            .EnumerateArray());

        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        Assert.Equal(
            "sameScopeCollision",
            error.GetProperty("data").GetProperty("reason").GetString());
        Assert.Equal(
            2,
            conflict.GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());
        Assert.False(rename.TryGetProperty("result", out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_rejects_a_public_enum_rename_to_a_project_public_type()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string enumUri = "file:///C:/work/ProjectEnum.bas";
        var enumText = string.Join('\n', [
            "Attribute VB_Name = \"ProjectEnum\"",
            "Public Enum Original",
            "    FirstValue",
            "End Enum"
        ]);
        const string typeUri = "file:///C:/work/ProjectType.bas";
        var typeText = string.Join('\n', [
            "Attribute VB_Name = \"ProjectType\"",
            "Public Type Taken",
            "    Value As Long",
            "End Type"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(enumUri, enumText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(typeUri, typeText));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            enumUri,
            enumText,
            "Original",
            0,
            new { newName = "Taken" });
        var error = rename.GetProperty("error");
        var conflict = Assert.Single(error
            .GetProperty("data")
            .GetProperty("conflicts")
            .EnumerateArray());

        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        Assert.Equal(
            "sameScopeCollision",
            error.GetProperty("data").GetProperty("reason").GetString());
        Assert.Equal(typeUri, conflict.GetProperty("uri").GetString());
        Assert.Equal(
            1,
            conflict.GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());
        Assert.False(rename.TryGetProperty("result", out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_allows_a_sub_rename_to_a_local_name_in_its_body()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/SubLocalNonCollision.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"SubLocalNonCollision\"",
            "Public Sub Original()",
            "    Dim Taken As Long",
            "    Debug.Print Taken",
            "End Sub",
            "Public Sub Invoke()",
            "    Original",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "Original",
            0,
            new { newName = "Taken" });
        var edits = rename
            .GetProperty("result")
            .GetProperty("changes")
            .GetProperty(uri)
            .EnumerateArray()
            .ToArray();

        Assert.Equal(
            [1, 6],
            edits.Select(edit => edit
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32()));
        Assert.All(edits, edit => Assert.Equal(
            "Taken",
            edit.GetProperty("newText").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_allows_a_function_rename_to_an_enum_type_name()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/FunctionEnumTypeNonCollision.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"FunctionEnumTypeNonCollision\"",
            "Public Enum Taken",
            "    FirstValue",
            "End Enum",
            "Public Function Original() As Long",
            "    Original = 1",
            "End Function",
            "Public Sub Invoke()",
            "    Debug.Print Original",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "Original",
            0,
            new { newName = "Taken" });
        Assert.True(
            rename.TryGetProperty("result", out var result),
            rename.ToString());
        var edits = result
            .GetProperty("changes")
            .GetProperty(uri)
            .EnumerateArray()
            .ToArray();

        Assert.Equal(
            [4, 5, 8],
            edits.Select(edit => edit
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32()));
        Assert.All(edits, edit => Assert.Equal(
            "Taken",
            edit.GetProperty("newText").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_allows_the_same_member_name_in_different_udts()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/UdtMemberScopes.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"UdtMemberScopes\"",
            "Private Type UFirst",
            "    Original As Long",
            "End Type",
            "Private Type USecond",
            "    Taken As Long",
            "End Type",
            "Public Sub Invoke()",
            "    Dim recordValue As UFirst",
            "    Dim sink As Long",
            "    sink = recordValue.Original",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "Original",
            0,
            new { newName = "Taken" });
        Assert.True(
            rename.TryGetProperty("result", out var result),
            rename.ToString());
        var edits = result
            .GetProperty("changes")
            .GetProperty(uri)
            .EnumerateArray()
            .ToArray();

        Assert.Equal(
            [2, 10],
            edits.Select(edit => edit
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32()));
        Assert.All(edits, edit => Assert.Equal(
            "Taken",
            edit.GetProperty("newText").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_rejects_the_same_member_name_in_one_udt()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/UdtMemberCollision.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"UdtMemberCollision\"",
            "Private Type Record",
            "    Original As Long",
            "    Taken As Long",
            "End Type"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "Original",
            0,
            new { newName = "Taken" });
        var error = rename.GetProperty("error");
        var conflict = Assert.Single(error
            .GetProperty("data")
            .GetProperty("conflicts")
            .EnumerateArray());

        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        Assert.Equal(
            "sameScopeCollision",
            error.GetProperty("data").GetProperty("reason").GetString());
        Assert.Equal(uri, conflict.GetProperty("uri").GetString());
        Assert.Equal(
            3,
            conflict.GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());
        Assert.False(rename.TryGetProperty("result", out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_does_not_rename_an_unqualified_udt_member_name()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/UdtMemberQualification.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"UdtMemberQualification\"",
            "Private Type Record",
            "    Original As Long",
            "End Type",
            "Public Sub Run()",
            "    Dim sink As Long",
            "    Original = 1",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            uri,
            text,
            "Original",
            0,
            new { newName = "Renamed" });
        Assert.True(
            rename.TryGetProperty("result", out var result),
            rename.ToString());
        var edit = Assert.Single(result
            .GetProperty("changes")
            .GetProperty(uri)
            .EnumerateArray());

        Assert.Equal(
            2,
            edit.GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());
        Assert.Equal("Renamed", edit.GetProperty("newText").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_keeps_udt_members_bound_when_a_class_has_the_same_name()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string classUri = "file:///C:/work/Customer.cls";
        var classText = string.Join('\n', [
            "Attribute VB_Name = \"Customer\"",
            "Public Function ClassMethod() As Long",
            "    ClassMethod = 1",
            "End Function"
        ]);
        const string moduleUri = "file:///C:/work/UdtOwner.bas";
        var moduleText = string.Join('\n', [
            "Attribute VB_Name = \"UdtOwner\"",
            "Private Type Customer",
            "    Field As Long",
            "End Type",
            "Public Sub Run()",
            "    Dim item As Customer",
            "    Dim sink As Long",
            "    sink = item.Field",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(classUri, classText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(moduleUri, moduleText));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            moduleUri,
            moduleText,
            "Field",
            0,
            new { newName = "ClassMethod" });
        Assert.True(
            rename.TryGetProperty("result", out var result),
            rename.ToString());
        var edits = result
            .GetProperty("changes")
            .GetProperty(moduleUri)
            .EnumerateArray()
            .ToArray();

        Assert.Equal(
            [2, 7],
            edits.Select(edit => edit
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32()));
        Assert.All(edits, edit => Assert.Equal(
            "ClassMethod",
            edit.GetProperty("newText").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_preserves_preexisting_unresolved_occurrences_during_rename()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string libraryUri = "file:///C:/work/Library.bas";
        var libraryText = string.Join('\n', [
            "Attribute VB_Name = \"Library\"",
            "Public Function BuildValue() As Long",
            "    BuildValue = 1",
            "End Function"
        ]);
        const string callerUri = "file:///C:/work/Caller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"Caller\"",
            "Public Sub Run()",
            "    Debug.Print Library.BuildValue",
            "    Debug.Print CreateValue",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(libraryUri, libraryText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            callerUri,
            callerText,
            "BuildValue",
            0,
            new { newName = "CreateValue" });
        var error = rename.GetProperty("error");

        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        Assert.Equal(
            "resolutionChanged",
            error.GetProperty("data").GetProperty("reason").GetString());
        Assert.Contains(
            "classification",
            error.GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(rename.TryGetProperty("result", out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_allows_qualified_rename_with_a_same_named_other_module_member()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string firstUri = "file:///C:/work/First.bas";
        var firstText = string.Join('\n', [
            "Attribute VB_Name = \"First\"",
            "Public Function BuildValue() As Long",
            "    BuildValue = 1",
            "End Function"
        ]);
        const string secondUri = "file:///C:/work/Second.bas";
        var secondText = string.Join('\n', [
            "Attribute VB_Name = \"Second\"",
            "Public Function CreateValue() As Long",
            "    CreateValue = 2",
            "End Function"
        ]);
        const string callerUri = "file:///C:/work/Caller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"Caller\"",
            "Public Sub Run()",
            "    Debug.Print First.BuildValue",
            "    Debug.Print Second.CreateValue",
            "    Debug.Print Unknown.BuildValue",
            "End Sub"
        ]);
        const string brokenUri = "file:///C:/work/Broken.bas";
        var brokenText = string.Join('\n', [
            "Attribute VB_Name = \"Broken\"",
            "Public Sub Broken(",
            "    MissingName"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(firstUri, firstText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(secondUri, secondText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(brokenUri, brokenText));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            callerUri,
            callerText,
            "BuildValue",
            0,
            new { newName = "CreateValue" });
        var changes = rename.GetProperty("result").GetProperty("changes");

        Assert.True(changes.TryGetProperty(firstUri, out var firstEdits));
        Assert.True(changes.TryGetProperty(callerUri, out var callerEdits));
        Assert.False(changes.TryGetProperty(secondUri, out _));
        Assert.False(changes.TryGetProperty(brokenUri, out _));
        Assert.Equal(2, firstEdits.GetArrayLength());
        Assert.Single(callerEdits.EnumerateArray());
        Assert.All(
            firstEdits.EnumerateArray().Concat(callerEdits.EnumerateArray()),
            edit => Assert.Equal(
                "CreateValue",
                edit.GetProperty("newText").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_preserves_an_affected_preexisting_ambiguous_classification()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string targetUri = "file:///C:/work/Target.bas";
        var targetText = string.Join('\n', [
            "Attribute VB_Name = \"Target\"",
            "Public Function BuildValue() As Long",
            "    BuildValue = 1",
            "End Function"
        ]);
        const string firstOtherUri = "file:///C:/work/FirstOther.bas";
        var firstOtherText = string.Join('\n', [
            "Attribute VB_Name = \"FirstOther\"",
            "Public Function CreateValue() As Long",
            "End Function"
        ]);
        const string secondOtherUri = "file:///C:/work/SecondOther.bas";
        var secondOtherText = string.Join('\n', [
            "Attribute VB_Name = \"SecondOther\"",
            "Public Function CreateValue() As Long",
            "End Function"
        ]);
        const string callerUri = "file:///C:/work/Caller.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"Caller\"",
            "Public Sub Run()",
            "    Debug.Print Target.BuildValue",
            "    Debug.Print CreateValue",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(targetUri, targetText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(firstOtherUri, firstOtherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(secondOtherUri, secondOtherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var rename = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/rename",
            callerUri,
            callerText,
            "BuildValue",
            0,
            new { newName = "CreateValue" });
        var changes = rename.GetProperty("result").GetProperty("changes");

        Assert.True(changes.TryGetProperty(targetUri, out _));
        Assert.True(changes.TryGetProperty(callerUri, out _));
        Assert.False(changes.TryGetProperty(firstOtherUri, out _));
        Assert.False(changes.TryGetProperty(secondOtherUri, out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_reports_analysis_incomplete_for_an_ambiguous_rename_target()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string firstUri = "file:///C:/work/First.bas";
        const string firstText =
            "Attribute VB_Name = \"First\"\n"
            + "Public Function SharedValue() As Long\n"
            + "End Function";
        const string secondUri = "file:///C:/work/Second.bas";
        const string secondText =
            "Attribute VB_Name = \"Second\"\n"
            + "Public Function SharedValue() As Long\n"
            + "End Function";
        const string callerUri = "file:///C:/work/Caller.bas";
        const string callerText =
            "Attribute VB_Name = \"Caller\"\n"
            + "Public Sub Run()\n"
            + "    Debug.Print SharedValue\n"
            + "End Sub";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(firstUri, firstText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(secondUri, secondText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(callerUri, callerText));

        var prepare = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/prepareRename",
            callerUri,
            callerText,
            "SharedValue");
        var prepareError = prepare.GetProperty("error");
        Assert.Equal(-32803, prepareError.GetProperty("code").GetInt32());
        Assert.Equal(
            "analysisIncomplete",
            prepareError
                .GetProperty("data")
                .GetProperty("reason")
                .GetString());

        var rename = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/rename",
            callerUri,
            callerText,
            "SharedValue",
            0,
            new { newName = "RenamedValue" });
        var renameError = rename.GetProperty("error");
        Assert.Equal(-32803, renameError.GetProperty("code").GetInt32());
        Assert.Equal(
            "analysisIncomplete",
            renameError
                .GetProperty("data")
                .GetProperty("reason")
                .GetString());

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_formats_source_casing_and_indentation()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string builderUri = "file:///C:/work/Builder.bas";
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(builderUri, string.Join('\n', [
            "Attribute VB_Name = \"Builder\"",
            "Option Explicit",
            "",
            "Public Function BuildValue() As String",
            "End Function"
        ])));
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument("file:///C:/work/First.bas", string.Join('\n', [
            "Attribute VB_Name = \"First\"",
            "Public Sub DuplicateValue()",
            "End Sub"
        ])));
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument("file:///C:/work/Second.bas", string.Join('\n', [
            "Attribute VB_Name = \"Second\"",
            "Public Sub DuplicateValue()",
            "End Sub"
        ])));
        const string callerUri = "file:///C:/work/Caller.bas";
        const string lineEnding = "\r\n";
        string[] callerLines = [
            "Attribute vb_name = \"Caller\"",
            "option explicit",
            "",
            "public sub Run()",
            "dim localValue as string",
            "localvalue = buildvalue",
            "duplicatevalue",
            "unresolvedname",
            "text = \"buildvalue public sub\"",
            "if true then",
            "'* @brief buildvalue remains prose.",
            "else",
            "' buildvalue remains an ordinary comment.",
            "end if",
            "End Sub"
        ];
        var text = string.Join(lineEnding, callerLines);
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(callerUri, text));

        var formatting = await process.SendRequestAsync(2,
            "textDocument/formatting",
            new
            {
                textDocument = new { uri = callerUri },
                options = new { tabSize = 4, insertSpaces = true }
            });
        var edit = formatting.GetProperty("result").EnumerateArray().Single();
        var range = edit.GetProperty("range");
        Assert.Equal(0, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(0, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(callerLines.Length - 1, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(callerLines[^1].Length, range.GetProperty("end").GetProperty("character").GetInt32());
        Assert.Equal(string.Join(lineEnding, [
            "Attribute VB_Name = \"Caller\"",
            "Option Explicit",
            "",
            "Public Sub Run()",
            "    Dim localValue As String",
            "    localValue = BuildValue",
            "    duplicatevalue",
            "    unresolvedname",
            "    text = \"buildvalue public sub\"",
            "    If True Then",
            "        '* @brief buildvalue remains prose.",
            "    Else",
            "        ' buildvalue remains an ordinary comment.",
            "    End If",
            "End Sub"
        ]), edit.GetProperty("newText").GetString());

        const string formattedUri = "file:///C:/work/Formatted.bas";
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(formattedUri, string.Join(lineEnding, [
            "Attribute VB_Name = \"Formatted\"",
            "Option Explicit",
            "",
            "Public Sub Run()",
            "    If True Then",
            "    End If",
            "End Sub"
        ])));
        var noFormatting = await process.SendRequestAsync(3,
            "textDocument/formatting",
            new
            {
                textDocument = new { uri = formattedUri },
                options = new { tabSize = 4, insertSpaces = true }
            });
        Assert.Empty(noFormatting.GetProperty("result").EnumerateArray());

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Server_uses_resolved_indent_size_for_space_formatting()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/IndentSize.bas";
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, string.Join('\n', [
            "Public Sub Run()",
            "value = 1",
            "End Sub"
        ])));

        var formatting = await process.SendRequestAsync(2,
            "textDocument/formatting",
            new
            {
                textDocument = new { uri },
                options = new { tabSize = 4, insertSpaces = true, indentSize = 2 }
            });

        var edit = formatting.GetProperty("result").EnumerateArray().Single();
        Assert.Equal(string.Join('\n', [
            "Public Sub Run()",
            "  value = 1",
            "End Sub"
        ]), edit.GetProperty("newText").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_uses_tabs_when_space_indentation_is_disabled()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/TabIndentation.bas";
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, string.Join('\n', [
            "Public Sub Run()",
            "value = 1",
            "End Sub"
        ])));

        var formatting = await process.SendRequestAsync(2,
            "textDocument/formatting",
            new
            {
                textDocument = new { uri },
                options = new { tabSize = 4, insertSpaces = false, indentSize = 2 }
            });

        var edit = formatting.GetProperty("result").EnumerateArray().Single();
        Assert.Equal(string.Join('\n', [
            "Public Sub Run()",
            "\tvalue = 1",
            "End Sub"
        ]), edit.GetProperty("newText").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_falls_back_to_tab_size_when_indent_size_is_omitted()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();

        await process.InitializeAsync();
        const string uri = "file:///C:/work/TabSizeFallback.bas";
        await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, string.Join('\n', [
            "Public Sub Run()",
            "value = 1",
            "End Sub"
        ])));

        var formatting = await process.SendRequestAsync(2,
            "textDocument/formatting",
            new
            {
                textDocument = new { uri },
                options = new { tabSize = 4, insertSpaces = true }
            });

        var edit = formatting.GetProperty("result").EnumerateArray().Single();
        Assert.Equal(string.Join('\n', [
            "Public Sub Run()",
            "    value = 1",
            "End Sub"
        ]), edit.GetProperty("newText").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Server_scopes_source_definitions_to_the_manifest_document_source_set()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-manifest-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "vba-project.json"), ProjectManifestFixtureText("multi-document.json"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "SecondBook"));

            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            var book1CallerUri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            var book1HelperUri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Helper.bas"));
            var secondCallerUri = ToFileUri(Path.Combine(projectRoot, "src", "SecondBook", "Caller.bas"));
            var secondHelperUri = ToFileUri(Path.Combine(projectRoot, "src", "SecondBook", "Helper.bas"));
            var book1CallerText = string.Join('\n', [
                "Attribute VB_Name = \"Caller\"",
                "Public Sub Run()",
                "    BuildValue",
                "End Sub"
            ]);
            var secondCallerText = book1CallerText;

            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(book1HelperUri, string.Join('\n', [
                "Attribute VB_Name = \"Book1Helper\"",
                "Public Function BuildValue() As String",
                "End Function"
            ])));
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(secondHelperUri, string.Join('\n', [
                "Attribute VB_Name = \"SecondHelper\"",
                "Public Function BuildValue() As String",
                "End Function",
                "Public Function SecondOnly() As String",
                "End Function"
            ])));
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(book1CallerUri, book1CallerText));
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(secondCallerUri, secondCallerText));

            var book1Definition = await RequestDefinitionAsync(process, 2, book1CallerUri, book1CallerText, "BuildValue");
            Assert.Equal(book1HelperUri, book1Definition.GetProperty("uri").GetString());

            var secondDefinition = await RequestDefinitionAsync(process, 3, secondCallerUri, secondCallerText, "BuildValue");
            Assert.Equal(secondHelperUri, secondDefinition.GetProperty("uri").GetString());

            var book1Completion = await process.SendRequestAsync(4,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri = book1CallerUri },
                    position = new { line = 2, character = 4 }
                });
            var book1Labels = book1Completion
                .GetProperty("result")
                .EnumerateArray()
                .Select(item => item.GetProperty("label").GetString())
                .ToArray();
            Assert.Contains("BuildValue", book1Labels);
            Assert.DoesNotContain("SecondOnly", book1Labels);

            await process.ShutdownAsync(5);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_reports_manifest_reference_selection_and_missing_main_reference()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-references-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "vba-project.json"), ProjectManifestFixtureText("references.json"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));

            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "End Sub"
            ])));

            var selection = await process.WaitForLogMessageAsync("VbaProjectReferenceSelection document=Book1");
            var selectionMessage = selection.GetProperty("params").GetProperty("message").GetString();
            Assert.Contains("Microsoft Scripting Runtime", selectionMessage, StringComparison.Ordinal);
            Assert.Contains("OLE Automation", selectionMessage, StringComparison.Ordinal);
            Assert.Contains("main=<none>", selectionMessage, StringComparison.Ordinal);

            var warning = await process.WaitForLogMessageAsync("missing expected main reference 'Microsoft Excel 16.0 Object Library'");
            Assert.Equal(2, warning.GetProperty("params").GetProperty("type").GetInt32());

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_marks_main_reference_only_when_manifest_contains_it_per_document()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-main-reference-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "vba-project.json"), """
                {
                  "schemaVersion": 1,
                  "projectName": "MainReferenceProject",
                  "primaryDocument": "Book1",
                  "documents": {
                    "Book1": {
                      "kind": "excel",
                      "sourcePath": "src/Book1",
                      "templatePath": "src/Book1/Book1.xlsm",
                      "binPath": "bin/Book1/Book1.xlsm",
                      "publishPath": "publish/Book1/Book1.xlsm",
                      "commonModules": [],
                      "references": [
                        {
                          "name": "Microsoft Excel 16.0 Object Library",
                          "requested": true
                        },
                        {
                          "name": "Microsoft Scripting Runtime",
                          "requested": true
                        }
                      ]
                    },
                    "SecondBook": {
                      "kind": "excel",
                      "sourcePath": "src/SecondBook",
                      "templatePath": "src/SecondBook/SecondBook.xlsm",
                      "binPath": "bin/SecondBook/SecondBook.xlsm",
                      "publishPath": "publish/SecondBook/SecondBook.xlsm",
                      "commonModules": [],
                      "references": [
                        {
                          "name": "Microsoft Scripting Runtime",
                          "requested": true
                        }
                      ]
                    }
                  }
                }
                """);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "SecondBook"));

            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            var book1Uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(book1Uri, string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "End Sub"
            ])));

            var book1Selection = await process.WaitForLogMessageAsync("VbaProjectReferenceSelection document=Book1");
            Assert.Contains(
                "main=Microsoft Excel 16.0 Object Library",
                book1Selection.GetProperty("params").GetProperty("message").GetString(),
                StringComparison.Ordinal);

            var secondBookUri = ToFileUri(Path.Combine(projectRoot, "src", "SecondBook", "Worker.bas"));
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(secondBookUri, string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "End Sub"
            ])));

            var secondBookSelection = await process.WaitForLogMessageAsync("VbaProjectReferenceSelection document=SecondBook");
            var secondBookMessage = secondBookSelection.GetProperty("params").GetProperty("message").GetString();
            Assert.Contains("Microsoft Scripting Runtime", secondBookMessage, StringComparison.Ordinal);
            Assert.Contains("main=<none>", secondBookMessage, StringComparison.Ordinal);
            await process.WaitForLogMessageAsync("document 'SecondBook' kind 'excel' is missing expected main reference");

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_does_not_emit_reference_selection_for_ad_hoc_projects()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-adhoc-references-").FullName;
        try
        {
            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "Worker.bas"));
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "End Sub"
            ])));

            var selection = await process.TryWaitForLogMessageAsync("VbaProjectReferenceSelection", TimeSpan.FromMilliseconds(500));
            Assert.Null(selection);

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_keeps_source_templates_out_of_manifest_source_scope_and_preserves_ad_hoc_projects()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-template-").FullName;
        var looseRoot = Directory.CreateTempSubdirectory("vba-ls-adhoc-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "vba-project.json"), ProjectManifestFixtureText("source-template.json"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "templates"));
            Directory.CreateDirectory(Path.Combine(looseRoot, "same"));
            Directory.CreateDirectory(Path.Combine(looseRoot, "other"));

            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            var manifestHelperUri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Helper.bas"));
            var templateCallerUri = ToFileUri(Path.Combine(projectRoot, "templates", "TemplateModule.bas"));
            var templateCallerText = string.Join('\n', [
                "Attribute VB_Name = \"TemplateModule\"",
                "Public Sub Run()",
                "    BuildValue",
                "End Sub"
            ]);
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(manifestHelperUri, string.Join('\n', [
                "Attribute VB_Name = \"ManifestHelper\"",
                "Public Function BuildValue() As String",
                "End Function"
            ])));
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(templateCallerUri, templateCallerText));

            var templateDefinition = await SendDefinitionRequestAsync(process, 2, templateCallerUri, templateCallerText, "BuildValue");
            Assert.Equal(JsonValueKind.Null, templateDefinition.ValueKind);

            var looseCallerUri = ToFileUri(Path.Combine(looseRoot, "same", "Caller.bas"));
            var looseHelperUri = ToFileUri(Path.Combine(looseRoot, "same", "Helper.bas"));
            var otherHelperUri = ToFileUri(Path.Combine(looseRoot, "other", "Helper.bas"));
            var looseCallerText = string.Join('\n', [
                "Attribute VB_Name = \"Caller\"",
                "Public Sub Run()",
                "    BuildValue",
                "End Sub"
            ]);
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(looseHelperUri, string.Join('\n', [
                "Attribute VB_Name = \"LooseHelper\"",
                "Public Function BuildValue() As String",
                "End Function"
            ])));
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(otherHelperUri, string.Join('\n', [
                "Attribute VB_Name = \"OtherHelper\"",
                "Public Function BuildValue() As String",
                "End Function"
            ])));
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(looseCallerUri, looseCallerText));

            var looseDefinition = await RequestDefinitionAsync(process, 3, looseCallerUri, looseCallerText, "BuildValue");
            Assert.Equal(looseHelperUri, looseDefinition.GetProperty("uri").GetString());

            var looseCompletion = await process.SendRequestAsync(4,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri = looseCallerUri },
                    position = new { line = 2, character = 4 }
                });
            var looseLabels = looseCompletion
                .GetProperty("result")
                .EnumerateArray()
                .Select(item => item.GetProperty("label").GetString())
                .ToArray();
            Assert.Contains("BuildValue", looseLabels);
            Assert.DoesNotContain("String", looseLabels);

            await process.ShutdownAsync(5);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(looseRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_invalidates_source_files_from_workspace_watched_file_events()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-file-events-").FullName;
        try
        {
            var callerPath = Path.Combine(projectRoot, "Caller.bas");
            var helperPath = Path.Combine(projectRoot, "Helper.bas");
            var renamedHelperPath = Path.Combine(projectRoot, "RenamedHelper.bas");
            var callerUri = ToFileUri(callerPath);
            var helperUri = ToFileUri(helperPath);
            var renamedHelperUri = ToFileUri(renamedHelperPath);
            const string documentation = "\u65e5\u672c\u8a9e\u306e\u8aac\u660e";
            var callerText = string.Join('\n', [
                "Attribute VB_Name = \"Caller\"",
                "Public Sub Run()",
                "    BuildValue",
                "End Sub"
            ]);
            var helperText = string.Join('\n', [
                "Attribute VB_Name = \"Helper\"",
                "Public Function BuildValue() As String",
                "End Function"
            ]);
            var renamedHelperText = string.Join('\n', [
                "Attribute VB_Name = \"Helper\"",
                $"'* @brief {documentation}",
                "Public Function BuildValue() As String",
                "End Function"
            ]);
            File.WriteAllText(callerPath, callerText);
            File.WriteAllText(helperPath, helperText);

            await using var process = await LanguageServerProcessHarness.StartAsync();

            await process.InitializeAsync();
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(callerUri, callerText));

            var initialDefinition = await RequestDefinitionAsync(process, 2, callerUri, callerText, "BuildValue");
            Assert.Equal(helperUri, initialDefinition.GetProperty("uri").GetString());

            await process.SendNotificationAsync("workspace/didChangeWatchedFiles",
                new
                {
                    changes = new[]
                    {
                        new { uri = helperUri, type = 3 }
                    }
                });
            var removedDefinition = await SendDefinitionRequestAsync(process, 3, callerUri, callerText, "BuildValue");
            Assert.Equal(JsonValueKind.Null, removedDefinition.ValueKind);

            File.WriteAllText(renamedHelperPath, renamedHelperText);
            await process.SendNotificationAsync("workspace/didChangeWatchedFiles",
                new
                {
                    changes = new[]
                    {
                        new { uri = renamedHelperUri, type = 1 }
                    }
                });
            var renamedDefinition = await RequestDefinitionAsync(process, 4, callerUri, callerText, "BuildValue");
            Assert.Equal(renamedHelperUri, renamedDefinition.GetProperty("uri").GetString());
            var hover = await SendPositionRequestAsync(process, 5, "textDocument/hover", callerUri, callerText, "BuildValue");
            Assert.Contains(
                documentation,
                hover.GetProperty("result").GetProperty("contents").GetProperty("value").GetString());

            await process.ShutdownAsync(6);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_publishes_diagnostic_for_invalid_closed_source_encoding()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-invalid-source-encoding-").FullName;
        try
        {
            var sourcePath = Path.Combine(projectRoot, "Worker.bas");
            var sourceUri = ToFileUri(sourcePath);
            File.WriteAllBytes(sourcePath, [0xFF, 0xFE, 0x00, 0xD8]);
            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();

            await process.SendNotificationAsync(
                "workspace/didChangeWatchedFiles",
                new
                {
                    changes = new[]
                    {
                        new { uri = sourceUri, type = 1 }
                    }
                });

            var notification = await process.WaitForNotificationAsync(
                "textDocument/publishDiagnostics");
            var parameters = notification.GetProperty("params");
            Assert.Equal(
                sourceUri,
                parameters.GetProperty("uri").GetString());
            var diagnostic = Assert.Single(
                parameters.GetProperty("diagnostics").EnumerateArray());
            Assert.Equal(
                "invalid-disk-source-encoding",
                diagnostic.GetProperty("code").GetString());
            Assert.Contains(
                sourcePath,
                diagnostic.GetProperty("message").GetString());

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_publishes_invalid_closed_source_encoding_with_a_tracked_project_peer()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-invalid-source-peer-").FullName;
        try
        {
            var callerPath = Path.Combine(projectRoot, "Caller.bas");
            var callerUri = ToFileUri(callerPath);
            var callerText = "Attribute VB_Name = \"Caller\"\n"
                + "Public Sub Run()\n"
                + "End Sub\n";
            File.WriteAllText(callerPath, callerText);
            var workerPath = Path.Combine(projectRoot, "Worker.bas");
            var workerUri = ToFileUri(workerPath);
            File.WriteAllText(
                workerPath,
                "Attribute VB_Name = \"Worker\"\n"
                    + "Public Sub Work()\n"
                    + "End Sub\n");
            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(callerUri, callerText));
            await process.WaitForDiagnosticsAsync(callerUri);
            await process.SendNotificationAsync(
                "workspace/didChangeWatchedFiles",
                new
                {
                    changes = new[]
                    {
                        new { uri = workerUri, type = 1 }
                    }
                });
            await process.WaitForDiagnosticsAsync(workerUri);

            File.WriteAllBytes(workerPath, [0xFF, 0xFE, 0x00, 0xD8]);
            await process.SendNotificationAsync(
                "workspace/didChangeWatchedFiles",
                new
                {
                    changes = new[]
                    {
                        new { uri = workerUri, type = 2 }
                    }
                });

            var notification = await process.WaitForDiagnosticsAsync(workerUri);
            var parameters = notification.GetProperty("params");
            var diagnostic = Assert.Single(
                parameters.GetProperty("diagnostics").EnumerateArray());
            Assert.Equal(
                "invalid-disk-source-encoding",
                diagnostic.GetProperty("code").GetString());
            Assert.Contains(
                workerPath,
                diagnostic.GetProperty("message").GetString());

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_preserves_versioned_open_buffers_across_watcher_events_and_uses_disk_after_close()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-open-authority-").FullName;
        try
        {
            var sourcePath = Path.Combine(projectRoot, "Worker.bas");
            var canonicalUri = ToFileUri(sourcePath);
            var encodedUri = ToEncodedDriveFileUri(sourcePath);
            File.WriteAllText(sourcePath, "Public Sub InitialDisk()\nEnd Sub\n");
            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();

            await process.SendNotificationAsync("textDocument/didOpen",
                new
                {
                    textDocument = new
                    {
                        uri = 42,
                        version = 1,
                        text = "ignored"
                    }
                });
            const string unsavedText = "Public Sub UnsavedBuffer()\nEnd Sub\n";
            await process.SendNotificationAsync("textDocument/didOpen",
                CreateOpenDocument(encodedUri, unsavedText, version: 5));
            await process.SendNotificationAsync("textDocument/didChange",
                new
                {
                    textDocument = new
                    {
                        uri = encodedUri,
                        version = 4
                    },
                    contentChanges = new[]
                    {
                        new
                        {
                            text = "Public Sub StaleBuffer()\nEnd Sub\n"
                        }
                    }
                });

            var afterStaleChange = await process.SendRequestAsync(2,
                "textDocument/documentSymbol",
                new
                {
                    textDocument = new { uri = encodedUri }
                });
            var afterStaleNames = afterStaleChange
                .GetProperty("result")
                .EnumerateArray()
                .Select(symbol => symbol.GetProperty("name").GetString())
                .ToArray();
            Assert.Contains("UnsavedBuffer", afterStaleNames);
            Assert.DoesNotContain("StaleBuffer", afterStaleNames);

            const string latestDiskText = "Public Sub LatestDisk()\nEnd Sub\n";
            File.WriteAllText(sourcePath, latestDiskText);
            await process.SendNotificationAsync("workspace/didChangeWatchedFiles",
                new
                {
                    changes = new[]
                    {
                        new { uri = canonicalUri, type = 2 }
                    }
                });
            var afterWatcher = await process.SendRequestAsync(3,
                "textDocument/documentSymbol",
                new
                {
                    textDocument = new { uri = encodedUri }
                });
            var afterWatcherNames = afterWatcher
                .GetProperty("result")
                .EnumerateArray()
                .Select(symbol => symbol.GetProperty("name").GetString())
                .ToArray();
            Assert.Contains("UnsavedBuffer", afterWatcherNames);
            Assert.DoesNotContain("LatestDisk", afterWatcherNames);

            await process.SendNotificationAsync("textDocument/didClose",
                new
                {
                    textDocument = new { uri = encodedUri }
                });
            var afterClose = await process.SendRequestAsync(4,
                "textDocument/documentSymbol",
                new
                {
                    textDocument = new { uri = canonicalUri }
                });
            var afterCloseNames = afterClose
                .GetProperty("result")
                .EnumerateArray()
                .Select(symbol => symbol.GetProperty("name").GetString())
                .ToArray();
            Assert.Contains("LatestDisk", afterCloseNames);
            Assert.DoesNotContain("UnsavedBuffer", afterCloseNames);

            const string openAfterDeleteText = "Public Sub OpenAfterDelete()\nEnd Sub\n";
            await process.SendNotificationAsync("textDocument/didOpen",
                CreateOpenDocument(encodedUri, openAfterDeleteText, version: 6));
            await process.SendNotificationAsync("workspace/didChangeWatchedFiles",
                new
                {
                    changes = new[]
                    {
                        new { uri = canonicalUri, type = 3 }
                    }
                });
            var whileDeletedAndOpen = await process.SendRequestAsync(5,
                "textDocument/documentSymbol",
                new
                {
                    textDocument = new { uri = encodedUri }
                });
            Assert.Contains(
                whileDeletedAndOpen.GetProperty("result").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "OpenAfterDelete");

            await process.SendNotificationAsync("textDocument/didClose",
                new
                {
                    textDocument = new { uri = canonicalUri }
                });
            var afterDeletedBufferClose = await process.SendRequestAsync(6,
                "textDocument/documentSymbol",
                new
                {
                    textDocument = new { uri = canonicalUri }
                });
            Assert.Empty(afterDeletedBufferClose.GetProperty("result").EnumerateArray());

            await process.ShutdownAsync(7);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_uses_unsaved_manifest_overlay_ignores_stale_versions_and_restores_disk_on_close()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-manifest-overlay-").FullName;
        try
        {
            var manifestPath = Path.Combine(projectRoot, "vba-project.json");
            var callerPath = Path.Combine(projectRoot, "src", "live", "caller", "Caller.bas");
            var helperPath = Path.Combine(projectRoot, "src", "live", "lib", "Helper.bas");
            Directory.CreateDirectory(Path.GetDirectoryName(callerPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(helperPath)!);
            var callerText = "Public Sub Run()\n    BuildValue\nEnd Sub\n";
            var helperText = "Public Function BuildValue() As String\nEnd Function\n";
            File.WriteAllText(callerPath, callerText);
            File.WriteAllText(helperPath, helperText);
            var diskManifestText = CreateSingleDocumentManifestText("src/disk");
            var overlayManifestText = CreateSingleDocumentManifestText("src/live");
            File.WriteAllText(manifestPath, diskManifestText);
            var callerUri = ToFileUri(callerPath);
            var helperUri = ToFileUri(helperPath);
            var manifestUri = ToFileUri(manifestPath);
            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(callerUri, callerText));

            var beforeOverlay = await SendDefinitionRequestAsync(process, 2,
                callerUri,
                callerText,
                "BuildValue");
            Assert.Equal(JsonValueKind.Null, beforeOverlay.ValueKind);

            await process.SendNotificationAsync("textDocument/didOpen",
                CreateOpenDocument(manifestUri, overlayManifestText, version: 5));
            var overlayTrace = await process.WaitForLogMessageAsync("VbaProjectReferenceSelection document=Book1");
            Assert.Contains(
                "Microsoft Excel 16.0 Object Library",
                overlayTrace.GetProperty("params").GetProperty("message").GetString(),
                StringComparison.Ordinal);
            var overlayDefinition = await RequestDefinitionAsync(process, 3,
                callerUri,
                callerText,
                "BuildValue");
            Assert.Equal(helperUri, overlayDefinition.GetProperty("uri").GetString());

            await process.SendNotificationAsync("textDocument/didChange",
                new
                {
                    textDocument = new
                    {
                        uri = manifestUri,
                        version = 4
                    },
                    contentChanges = new[]
                    {
                        new { text = diskManifestText }
                    }
                });
            var afterStaleManifestChange = await RequestDefinitionAsync(process, 4,
                callerUri,
                callerText,
                "BuildValue");
            Assert.Equal(helperUri, afterStaleManifestChange.GetProperty("uri").GetString());

            await process.SendNotificationAsync("textDocument/didClose",
                new
                {
                    textDocument = new { uri = manifestUri }
                });
            var afterManifestClose = await SendDefinitionRequestAsync(process, 5,
                callerUri,
                callerText,
                "BuildValue");
            Assert.Equal(JsonValueKind.Null, afterManifestClose.ValueKind);

            await process.ShutdownAsync(6);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_applies_manifest_watcher_events_without_overwriting_open_overlay()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-manifest-watcher-").FullName;
        try
        {
            var manifestPath = Path.Combine(projectRoot, "vba-project.json");
            var callerPath = Path.Combine(projectRoot, "src", "live", "caller", "Caller.bas");
            var helperPath = Path.Combine(projectRoot, "src", "live", "lib", "Helper.bas");
            Directory.CreateDirectory(Path.GetDirectoryName(callerPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(helperPath)!);
            var callerText = "Public Sub Run()\n    BuildValue\nEnd Sub\n";
            File.WriteAllText(callerPath, callerText);
            File.WriteAllText(helperPath, "Public Function BuildValue() As String\nEnd Function\n");
            var liveManifestText = CreateSingleDocumentManifestText("src/live");
            var otherManifestText = CreateSingleDocumentManifestText("src/other");
            var callerUri = ToFileUri(callerPath);
            var helperUri = ToFileUri(helperPath);
            var manifestUri = ToFileUri(manifestPath);
            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();

            var withoutManifest = await SendDefinitionRequestAsync(process, 2,
                callerUri,
                callerText,
                "BuildValue");
            Assert.Equal(JsonValueKind.Null, withoutManifest.ValueKind);

            File.WriteAllText(manifestPath, liveManifestText);
            await SendWatchedFileChangeAsync(process, manifestUri, type: 1);
            var afterCreate = await RequestDefinitionAsync(process, 3,
                callerUri,
                callerText,
                "BuildValue");
            Assert.Equal(helperUri, afterCreate.GetProperty("uri").GetString());

            File.WriteAllText(manifestPath, otherManifestText);
            await SendWatchedFileChangeAsync(process, manifestUri, type: 2);
            var afterChange = await SendDefinitionRequestAsync(process, 4,
                callerUri,
                callerText,
                "BuildValue");
            Assert.Equal(JsonValueKind.Null, afterChange.ValueKind);

            File.WriteAllText(manifestPath, liveManifestText);
            await SendWatchedFileChangeAsync(process, manifestUri, type: 2);
            var afterLiveChange = await RequestDefinitionAsync(process, 5,
                callerUri,
                callerText,
                "BuildValue");
            Assert.Equal(helperUri, afterLiveChange.GetProperty("uri").GetString());

            File.Delete(manifestPath);
            await SendWatchedFileChangeAsync(process, manifestUri, type: 3);
            var afterDelete = await SendDefinitionRequestAsync(process, 6,
                callerUri,
                callerText,
                "BuildValue");
            Assert.Equal(JsonValueKind.Null, afterDelete.ValueKind);

            File.WriteAllText(manifestPath, otherManifestText);
            await SendWatchedFileChangeAsync(process, manifestUri, type: 1);
            await process.SendNotificationAsync("textDocument/didOpen",
                CreateOpenDocument(manifestUri, liveManifestText, version: 5));
            var withOpenOverlay = await RequestDefinitionAsync(process, 7,
                callerUri,
                callerText,
                "BuildValue");
            Assert.Equal(helperUri, withOpenOverlay.GetProperty("uri").GetString());

            File.WriteAllText(manifestPath, otherManifestText);
            await SendWatchedFileChangeAsync(process, manifestUri, type: 2);
            var afterWatcherUnderOverlay = await RequestDefinitionAsync(process, 8,
                callerUri,
                callerText,
                "BuildValue");
            Assert.Equal(helperUri, afterWatcherUnderOverlay.GetProperty("uri").GetString());

            await process.SendNotificationAsync("textDocument/didClose",
                new
                {
                    textDocument = new { uri = manifestUri }
                });
            var afterOverlayClose = await SendDefinitionRequestAsync(process, 9,
                callerUri,
                callerText,
                "BuildValue");
            Assert.Equal(JsonValueKind.Null, afterOverlayClose.ValueKind);

            await process.SendNotificationAsync("textDocument/didOpen",
                CreateOpenDocument(manifestUri, liveManifestText, version: 6));
            File.Delete(manifestPath);
            await SendWatchedFileChangeAsync(process, manifestUri, type: 3);
            var afterDeleteUnderOverlay = await RequestDefinitionAsync(process, 10,
                callerUri,
                callerText,
                "BuildValue");
            Assert.Equal(helperUri, afterDeleteUnderOverlay.GetProperty("uri").GetString());

            await process.SendNotificationAsync("textDocument/didClose",
                new
                {
                    textDocument = new { uri = manifestUri }
                });
            var afterDeletedOverlayClose = await SendDefinitionRequestAsync(process, 11,
                callerUri,
                callerText,
                "BuildValue");
            Assert.Equal(JsonValueKind.Null, afterDeletedOverlayClose.ValueKind);

            await process.ShutdownAsync(12);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_retains_last_valid_manifest_and_continues_after_invalid_overlay_changes()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-invalid-manifest-overlay-").FullName;
        try
        {
            var manifestPath = Path.Combine(projectRoot, "vba-project.json");
            var callerPath = Path.Combine(projectRoot, "src", "live", "caller", "Caller.bas");
            var helperPath = Path.Combine(projectRoot, "src", "live", "lib", "Helper.bas");
            Directory.CreateDirectory(Path.GetDirectoryName(callerPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(helperPath)!);
            var callerText = "Public Sub Run()\n    BuildValue\nEnd Sub\n";
            File.WriteAllText(callerPath, callerText);
            File.WriteAllText(helperPath, "Public Function BuildValue() As String\nEnd Function\n");
            var liveManifestText = CreateSingleDocumentManifestText("src/live");
            var otherManifestText = CreateSingleDocumentManifestText("src/other");
            var invalidSourcePathManifestText = CreateSingleDocumentManifestText("\0");
            var overlappingManifestText = CreateOverlappingManifestText("src/live");
            File.WriteAllText(manifestPath, liveManifestText);
            var callerUri = ToFileUri(callerPath);
            var helperUri = ToFileUri(helperPath);
            var manifestUri = ToFileUri(manifestPath);
            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(callerUri, callerText));

            await process.SendNotificationAsync("textDocument/didOpen",
                CreateOpenDocument(manifestUri, "{", version: 1));
            var invalidManifestDiagnostics = await process.WaitForDiagnosticsAsync(manifestUri);
            var invalidManifestDiagnostic = Assert.Single(
                invalidManifestDiagnostics.GetProperty("params").GetProperty("diagnostics").EnumerateArray());
            Assert.Equal(
                "invalid-project-manifest",
                invalidManifestDiagnostic.GetProperty("code").GetString());
            var initialInvalidFallback = await RequestDefinitionAsync(process, 2,
                callerUri,
                callerText,
                "BuildValue");
            Assert.Equal(helperUri, initialInvalidFallback.GetProperty("uri").GetString());

            File.WriteAllText(manifestPath, otherManifestText);
            await process.SendNotificationAsync("textDocument/didClose",
                new
                {
                    textDocument = new { uri = manifestUri }
                });
            var diskFallbackAfterClose = await SendDefinitionRequestAsync(process, 3,
                callerUri,
                callerText,
                "BuildValue");
            Assert.Equal(JsonValueKind.Null, diskFallbackAfterClose.ValueKind);

            await process.SendNotificationAsync("textDocument/didOpen",
                CreateOpenDocument(manifestUri, liveManifestText, version: 10));
            var validManifestDiagnostics = await process.WaitForDiagnosticsAsync(manifestUri);
            Assert.Empty(
                validManifestDiagnostics.GetProperty("params").GetProperty("diagnostics").EnumerateArray());
            var validOverlayDefinition = await RequestDefinitionAsync(process, 4,
                callerUri,
                callerText,
                "BuildValue");
            Assert.Equal(helperUri, validOverlayDefinition.GetProperty("uri").GetString());

            await process.SendNotificationAsync("textDocument/didChange",
                new
                {
                    textDocument = new
                    {
                        uri = manifestUri,
                        version = 11
                    },
                    contentChanges = new[]
                    {
                        new { text = "{" }
                    }
                });
            var afterInvalidJson = await RequestDefinitionAsync(process, 5,
                callerUri,
                callerText,
                "BuildValue");
            Assert.Equal(helperUri, afterInvalidJson.GetProperty("uri").GetString());

            await process.SendNotificationAsync("textDocument/didChange",
                new
                {
                    textDocument = new
                    {
                        uri = manifestUri,
                        version = 12
                    },
                    contentChanges = new[]
                    {
                        new { text = otherManifestText }
                    }
                });
            var afterNewerValidManifest = await SendDefinitionRequestAsync(process, 6,
                callerUri,
                callerText,
                "BuildValue");
            Assert.Equal(JsonValueKind.Null, afterNewerValidManifest.ValueKind);

            await process.SendNotificationAsync("textDocument/didChange",
                new
                {
                    textDocument = new
                    {
                        uri = manifestUri,
                        version = 13
                    },
                    contentChanges = new[]
                    {
                        new { text = invalidSourcePathManifestText }
                    }
                });
            var afterInvalidSourcePath = await SendDefinitionRequestAsync(process, 7,
                callerUri,
                callerText,
                "BuildValue");
            Assert.Equal(JsonValueKind.Null, afterInvalidSourcePath.ValueKind);

            await process.SendNotificationAsync("textDocument/didChange",
                new
                {
                    textDocument = new
                    {
                        uri = manifestUri,
                        version = 14
                    },
                    contentChanges = new[]
                    {
                        new { text = liveManifestText }
                    }
                });
            var recoveredAfterInvalidSourcePath = await RequestDefinitionAsync(process, 8,
                callerUri,
                callerText,
                "BuildValue");
            Assert.Equal(helperUri, recoveredAfterInvalidSourcePath.GetProperty("uri").GetString());
            var recoveredSourcePathDiagnostics = await process.WaitForDiagnosticsAsync(manifestUri);
            Assert.Empty(
                recoveredSourcePathDiagnostics.GetProperty("params").GetProperty("diagnostics").EnumerateArray());

            var overlapCheckpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync("textDocument/didChange",
                new
                {
                    textDocument = new
                    {
                        uri = manifestUri,
                        version = 15
                    },
                    contentChanges = new[]
                    {
                        new { text = overlappingManifestText }
                    }
                });
            var overlapDiagnostics = await process.WaitForMessageAsync(
                overlapCheckpoint,
                message => message.TryGetProperty("method", out var method)
                    && method.GetString() == "textDocument/publishDiagnostics"
                    && message.GetProperty("params").GetProperty("uri").GetString() == manifestUri
                    && message.GetProperty("params").GetProperty("diagnostics").EnumerateArray()
                        .Any(diagnostic => (diagnostic.GetProperty("message").GetString() ?? "")
                            .Contains("document source roots overlap", StringComparison.OrdinalIgnoreCase)));
            var overlapDiagnostic = Assert.Single(
                overlapDiagnostics.GetProperty("params").GetProperty("diagnostics").EnumerateArray());
            Assert.Equal(
                "invalid-project-manifest",
                overlapDiagnostic.GetProperty("code").GetString());
            var overlapMessage = overlapDiagnostic.GetProperty("message").GetString() ?? "";
            Assert.Contains("Book1", overlapMessage, StringComparison.Ordinal);
            Assert.Contains("Book2", overlapMessage, StringComparison.Ordinal);
            Assert.Equal(2, overlapMessage.Split("sourcePath 'src/live'", StringSplitOptions.None).Length - 1);
            var retainedAfterOverlap = await RequestDefinitionAsync(process, 9,
                callerUri,
                callerText,
                "BuildValue");
            Assert.Equal(helperUri, retainedAfterOverlap.GetProperty("uri").GetString());

            var recoveryCheckpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync("textDocument/didChange",
                new
                {
                    textDocument = new
                    {
                        uri = manifestUri,
                        version = 16
                    },
                    contentChanges = new[]
                    {
                        new { text = liveManifestText }
                    }
                });
            var recoveredDiagnostics = await process.WaitForMessageAsync(
                recoveryCheckpoint,
                message => message.TryGetProperty("method", out var method)
                    && method.GetString() == "textDocument/publishDiagnostics"
                    && message.GetProperty("params").GetProperty("uri").GetString() == manifestUri
                    && !message.GetProperty("params").GetProperty("diagnostics").EnumerateArray().Any());
            Assert.Empty(
                recoveredDiagnostics.GetProperty("params").GetProperty("diagnostics").EnumerateArray());
            var recoveredAfterOverlap = await RequestDefinitionAsync(process, 10,
                callerUri,
                callerText,
                "BuildValue");
            Assert.Equal(helperUri, recoveredAfterOverlap.GetProperty("uri").GetString());

            await process.ShutdownAsync(11);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_returns_strict_json_rpc_errors_and_continues_processing_requests()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-request-errors-").FullName;
        try
        {
            var sourcePath = Path.Combine(projectRoot, "Module1.bas");
            var manifestPath = Path.Combine(projectRoot, "vba-project.json");
            var sourceText = "Public Sub Recover()\nEnd Sub\n";
            File.WriteAllText(sourcePath, sourceText);
            File.WriteAllText(manifestPath, "{");
            var sourceUri = new Uri(sourcePath).AbsoluteUri;
            await using var server = await LanguageServerProcessHarness.StartAsync();
            await server.InitializeAsync();

            var checkpoint = server.TranscriptCheckpoint;
            await server.SendRawMessageAsync(
                new
                {
                    jsonrpc = "2.0",
                    id = new { invalid = true },
                    method = "shutdown",
                    @params = (object?)null
                });
            var invalidRequest = await server.WaitForMessageAsync(
                checkpoint,
                message => message.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.Null
                    && message.TryGetProperty("error", out var error)
                    && error.GetProperty("code").GetInt32() == -32600);
            Assert.Equal(JsonValueKind.Null, invalidRequest.GetProperty("id").ValueKind);
            AssertJsonRpcError(invalidRequest, -32600, "Invalid Request");

            var methodNotFound = await server.SendRequestAsync(2, "unknown/method", new { });
            Assert.Equal(2, methodNotFound.GetProperty("id").GetInt32());
            AssertJsonRpcError(methodNotFound, -32601, "Method not found");

            var invalidParams = await server.SendRequestAsync(
                3,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri = sourceUri },
                    position = new { line = -1, character = 0 }
                });
            AssertJsonRpcError(invalidParams, -32602, "Invalid params");

            var internalError = await server.SendRequestAsync(
                4,
                "textDocument/documentSymbol",
                new
                {
                    textDocument = new { uri = sourceUri }
                });
            AssertJsonRpcError(internalError, -32603, "Internal error");

            File.Delete(manifestPath);
            var recovered = await server.SendRequestAsync(
                5,
                "textDocument/documentSymbol",
                new
                {
                    textDocument = new { uri = sourceUri }
                });
            Assert.Contains(
                recovered.GetProperty("result").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "Recover");

            await server.ShutdownAsync(6);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    private static void AssertCatalogCompletionItem(
        IReadOnlyList<JsonElement> items,
        string label,
        int expectedKind,
        string expectedDetail)
    {
        var item = Assert.Single(items, candidate =>
            candidate.GetProperty("label").GetString() == label);
        Assert.Equal(expectedKind, item.GetProperty("kind").GetInt32());
        Assert.Equal(expectedDetail, item.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("sortText").GetString()));
    }

    private static void AssertJsonRpcError(JsonElement response, int code, string message)
    {
        Assert.Equal("2.0", response.GetProperty("jsonrpc").GetString());
        var error = response.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetInt32());
        Assert.Equal(message, error.GetProperty("message").GetString());
        Assert.False(response.TryGetProperty("result", out _));
    }

    private static Task SendWatchedFileChangeAsync(LanguageServerProcessHarness process, string uri, int type)
        => process.SendNotificationAsync(
            "workspace/didChangeWatchedFiles",
            new
            {
                changes = new[]
                {
                    new { uri, type }
                }
            });

    private static object CreateOpenDocument(string uri, string text, int version = 1)
    {
        return new
        {
            textDocument = new
            {
                uri,
                languageId = "vba",
                version,
                text
            }
        };
    }

    private static async Task<JsonElement> RequestDefinitionAsync(
        LanguageServerProcessHarness process,
        int id,
        string uri,
        string text,
        string needle,
        int offset = 0)
    {
        var result = await SendDefinitionRequestAsync(process, id, uri, text, needle, offset);
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        return result;
    }

    private static async Task<JsonElement> SendDefinitionRequestAsync(
        LanguageServerProcessHarness process,
        int id,
        string uri,
        string text,
        string needle,
        int offset = 0)
    {
        var characterOffset = text.IndexOf(needle, StringComparison.Ordinal) + offset;
        Assert.True(characterOffset >= offset);
        var prefix = text[..characterOffset];
        var line = prefix.Count(character => character == '\n');
        var lineStart = prefix.LastIndexOf('\n');
        var character = lineStart < 0 ? characterOffset : characterOffset - lineStart - 1;

        var response = await process.SendRequestAsync(id,
            "textDocument/definition",
            new
            {
                textDocument = new { uri },
                position = new { line, character }
            });
        return response.GetProperty("result");
    }

    private static Task<JsonElement> SendPositionRequestAsync(
        LanguageServerProcessHarness server,
        int id,
        string method,
        string uri,
        string text,
        string needle,
        int offset = 0,
        object? additionalParameters = null)
    {
        var position = FindPosition(text, needle, offset);
        var parameters = MergePositionParameters(uri, position.Line, position.Character, additionalParameters);
        return server.SendRequestAsync(id, method, parameters);
    }

    private static object MergePositionParameters(
        string uri,
        int line,
        int character,
        object? additionalParameters)
    {
        var json = JsonSerializer.SerializeToNode(additionalParameters ?? new { })!.AsObject();
        json["textDocument"] = JsonSerializer.SerializeToNode(new { uri });
        json["position"] = JsonSerializer.SerializeToNode(new { line, character });
        return json;
    }

    private static (int Line, int Character) FindPosition(string text, string needle, int offset = 0)
    {
        var characterOffset = text.IndexOf(needle, StringComparison.Ordinal) + offset;
        Assert.True(characterOffset >= offset);
        var prefix = text[..characterOffset];
        var line = prefix.Count(character => character == '\n');
        var lineStart = prefix.LastIndexOf('\n');
        var character = lineStart < 0 ? characterOffset : characterOffset - lineStart - 1;
        return (line, character);
    }

    private static IReadOnlyList<DecodedSemanticToken> DecodeSemanticTokens(JsonElement response, string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var data = response
            .GetProperty("result")
            .GetProperty("data")
            .EnumerateArray()
            .Select(item => item.GetInt32())
            .ToArray();
        var tokens = new List<DecodedSemanticToken>();
        var line = 0;
        var character = 0;
        for (var index = 0; index < data.Length; index += 5)
        {
            var deltaLine = data[index];
            var deltaStart = data[index + 1];
            var length = data[index + 2];
            var tokenTypeIndex = data[index + 3];
            var modifierBits = data[index + 4];
            line += deltaLine;
            character = deltaLine == 0 ? character + deltaStart : deltaStart;
            var tokenText = lines[line].Substring(character, length);
            tokens.Add(new DecodedSemanticToken(
                tokenText,
                VbaSemanticTokenLegend.Types[tokenTypeIndex],
                DecodeSemanticTokenModifiers(modifierBits),
                line,
                character,
                length));
        }

        return tokens;
    }

    private static IReadOnlyList<string> DecodeSemanticTokenModifiers(int modifierBits)
        => VbaSemanticTokenLegend.Modifiers
            .Where((_, index) => (modifierBits & (1 << index)) != 0)
            .ToArray();

    private static string ToFileUri(string path)
        => new Uri(path).AbsoluteUri;

    private static string ToEncodedDriveFileUri(string path)
    {
        var fullPath = Path.GetFullPath(path).Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Length >= 2 && fullPath[1] == Path.VolumeSeparatorChar
            ? $"file:///{char.ToLowerInvariant(fullPath[0])}%3A{fullPath[2..]}"
            : new Uri(path).AbsoluteUri;
    }

    private sealed record DecodedSemanticToken(
        string Text,
        string TokenType,
        IReadOnlyList<string> TokenModifiers,
        int Line,
        int Character,
        int Length);

    private static string ProjectManifestFixtureText(string fixtureName)
        => File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "fixtures",
            "project-manifest",
            fixtureName)));

    private static string CreateSingleDocumentManifestText(string sourcePath)
    {
        var manifest = new
        {
            schemaVersion = 1,
            projectName = "ManifestOverlayProject",
            primaryDocument = "Book1",
            documents = new Dictionary<string, object>
            {
                ["Book1"] = new
                {
                    kind = "excel",
                    sourcePath,
                    templatePath = "src/Book1/Book1.xlsm",
                    binPath = "bin/Book1/Book1.xlsm",
                    publishPath = "publish/Book1/Book1.xlsm",
                    commonModules = Array.Empty<object>(),
                    references = new[]
                    {
                        new
                        {
                            name = "Microsoft Excel 16.0 Object Library",
                            requested = true
                        }
                    }
                }
            }
        };
        return JsonSerializer.Serialize(
            manifest,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });
    }

    private static string CreateOverlappingManifestText(string sourcePath)
    {
        static object CreateDocument(string documentName, string sourcePath)
            => new
            {
                kind = "excel",
                sourcePath,
                templatePath = $"src/{documentName}/{documentName}.xlsm",
                binPath = $"bin/{documentName}.xlsm",
                publishPath = $"publish/{documentName}.xlsm",
                commonModules = Array.Empty<object>(),
                references = Array.Empty<object>()
            };

        var manifest = new
        {
            schemaVersion = 1,
            projectName = "OverlappingManifestProject",
            primaryDocument = "Book1",
            documents = new Dictionary<string, object>
            {
                ["Book1"] = CreateDocument("Book1", sourcePath),
                ["Book2"] = CreateDocument("Book2", sourcePath)
            }
        };
        return JsonSerializer.Serialize(
            manifest,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });
    }

    private static bool HasRegisteredTypeLib(string referenceName)
    {
        var catalog = new RegistryTypeLibRegistryCatalogReader().Read();
        return catalog.Complete
            && catalog.Find(referenceName)?.Lineages.Count > 0;
    }

    private static void WriteReferenceCatalogProjectManifest(string projectRoot, params string[] referenceNames)
    {
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
        var references = referenceNames
            .Select(referenceName => new { name = referenceName, requested = true })
            .ToArray();
        var manifest = new
        {
            schemaVersion = 1,
            projectName = "ReferenceCatalogProject",
            primaryDocument = "Book1",
            documents = new Dictionary<string, object>
            {
                ["Book1"] = new
                {
                    kind = "excel",
                    sourcePath = "src/Book1",
                    templatePath = "src/Book1/Book1.xlsm",
                    binPath = "bin/Book1/Book1.xlsm",
                    publishPath = "publish/Book1/Book1.xlsm",
                    commonModules = Array.Empty<object>(),
                    references
                }
            }
        };
        File.WriteAllText(
            Path.Combine(projectRoot, "vba-project.json"),
            JsonSerializer.Serialize(
                manifest,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
    }

    private static void WriteModuleRenameProjectManifest(
        string projectRoot,
        params (string Name, string ModuleFile)[] commonModules)
    {
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
        var manifest = new
        {
            schemaVersion = 1,
            projectName = "ModuleRenameProject",
            primaryDocument = "Book1",
            documents = new Dictionary<string, object>
            {
                ["Book1"] = new
                {
                    kind = "excel",
                    sourcePath = "src/Book1",
                    templatePath = "src/Book1/Book1.xlsm",
                    binPath = "bin/Book1.xlsm",
                    publishPath = "publish/Book1.xlsm",
                    commonModules = commonModules.Select(module => new
                    {
                        name = module.Name,
                        moduleFile = module.ModuleFile,
                        requested = true,
                        testOnly = false
                    }),
                    references = Array.Empty<object>()
                }
            }
        };
        File.WriteAllText(
            Path.Combine(projectRoot, "vba-project.json"),
            JsonSerializer.Serialize(manifest));
    }

    private static VbaProjectReferenceCatalogIdentity CreateGeneratedReferenceCatalogIdentity(string referenceName)
        => new(
            referenceName,
            "{33333333-3333-3333-3333-333333333333}",
            1,
            0,
            0,
            @"C:\TypeLibs\Generated.tlb");

    private static VbaProjectReferenceCatalog CreateGeneratedReferenceCatalog(string referenceName)
        => new(
            referenceName,
            ["Generated"],
            [
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "GeneratedType",
                    VbaSourceDefinitionKind.Class)
            ]);

    private static VbaProjectReferenceCatalog CreateGeneratedExcelReferenceCatalog()
        => new(
            "Microsoft Excel 16.0 Object Library",
            ["Excel"],
            [
                new VbaProjectReferenceDefinition(
                    "Microsoft Excel 16.0 Object Library",
                    "Application",
                    VbaSourceDefinitionKind.Class,
                    "Represents the Microsoft Excel application."),
                new VbaProjectReferenceDefinition(
                    "Microsoft Excel 16.0 Object Library",
                    "Application",
                    VbaSourceDefinitionKind.Property,
                    "Returns the Microsoft Excel application.",
                    ParentTypeName: "Application",
                    TypeReference: new VbaTypeReference("Application", "Excel"),
                    PropertyAccess: VbaPropertyAccess.Readable,
                    GlobalExposure: ReferenceDefinitionGlobalExposure.MainHostGlobal),
                new VbaProjectReferenceDefinition(
                    "Microsoft Excel 16.0 Object Library",
                    "XlHAlign",
                    VbaSourceDefinitionKind.Enum,
                    "Specifies horizontal alignment."),
                new VbaProjectReferenceDefinition(
                    "Microsoft Excel 16.0 Object Library",
                    "xlCenter",
                    VbaSourceDefinitionKind.EnumMember,
                    "Centers content horizontally.",
                    ParentTypeName: "XlHAlign",
                    TypeReference: new VbaTypeReference("Long"),
                    GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal),
                new VbaProjectReferenceDefinition(
                    "Microsoft Excel 16.0 Object Library",
                    "CatalogRun",
                    VbaSourceDefinitionKind.Procedure,
                    "Runs a catalog-backed operation.",
                    new VbaCallableSignature(
                        "CatalogRun(Value)",
                        [
                            new VbaCallableParameter(
                                "Value",
                                TypeReference: new VbaTypeReference("Long"))
                        ],
                        CallableKind: VbaCallableKind.Sub),
                    GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal),
                new VbaProjectReferenceDefinition(
                    "Microsoft Excel 16.0 Object Library",
                    "Workbook",
                    VbaSourceDefinitionKind.Class,
                    "Represents a Microsoft Excel workbook."),
                new VbaProjectReferenceDefinition(
                    "Microsoft Excel 16.0 Object Library",
                    "Worksheets",
                    VbaSourceDefinitionKind.Class,
                    "Represents the worksheets in a workbook."),
                new VbaProjectReferenceDefinition(
                    "Microsoft Excel 16.0 Object Library",
                    "Worksheet",
                    VbaSourceDefinitionKind.Class,
                    "Represents a Microsoft Excel worksheet."),
                new VbaProjectReferenceDefinition(
                    "Microsoft Excel 16.0 Object Library",
                    "Range",
                    VbaSourceDefinitionKind.Class,
                    "Represents a cell or range of cells."),
                new VbaProjectReferenceDefinition(
                    "Microsoft Excel 16.0 Object Library",
                    "Worksheets",
                    VbaSourceDefinitionKind.Property,
                    "Returns the workbook worksheets.",
                    ParentTypeName: "Workbook",
                    TypeReference: new VbaTypeReference("Worksheets", "Excel"),
                    PropertyAccess: VbaPropertyAccess.Readable),
                new VbaProjectReferenceDefinition(
                    "Microsoft Excel 16.0 Object Library",
                    "Range",
                    VbaSourceDefinitionKind.Property,
                    "Returns a Range object.",
                    new VbaCallableSignature(
                        "Range(Cell1, Cell2) As Range",
                        [
                            new VbaCallableParameter("Cell1", "The first cell."),
                            new VbaCallableParameter("Cell2", "The second cell.")
                        ],
                        "Returns a Range object."),
                    ParentTypeName: "Worksheet",
                    TypeReference: new VbaTypeReference("Range", "Excel"),
                    PropertyAccess: VbaPropertyAccess.Readable)
            ]);

    private static string CreateExcelStartupCatalogWorkerText()
        => string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "",
            "Public Sub Run()",
            "    Dim target_book As Workbook",
            "    Dim target_sheet As Worksheet",
            "    Dim target_range As Range",
            "    Set target_sheet = target_book.W",
            "    Set target_range = target_sheet.Range(",
            "    value = App",
            "    value = xlC",
            "    value = Excel.",
            "    Excel.CatalogRun(",
            "    value = Application",
            "End Sub"
        ]);

    private static void MarkReferenceCatalogIndexAsStale(
        VbaProjectReferenceCatalogPersistentStore store,
        string referenceName)
    {
        var indexPath = store.GetReferenceIndexPath(referenceName);
        var json = File.ReadAllText(indexPath);
        File.WriteAllText(
            indexPath,
            json.Replace(
                VbaProjectReferenceCatalogPersistentStore.CurrentGeneratorVersion,
                "old-generator",
                StringComparison.Ordinal));
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        if (!await TryWaitForFileAsync(path, timeout))
        {
            throw new TimeoutException($"Timed out waiting for file: {path}");
        }
    }

    private static async Task<bool> TryWaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        return File.Exists(path);
    }

}
