using System.Text;
using System.Text.Json;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class LanguageServerProcessTests
{
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
        Assert.True(capabilities.TryGetProperty("completionProvider", out _));
        Assert.True(capabilities.GetProperty("referencesProvider").GetBoolean());
        Assert.True(capabilities.GetProperty("workspaceSymbolProvider").GetBoolean());
        Assert.True(capabilities.GetProperty("documentFormattingProvider").GetBoolean());
        var signatureTriggers = capabilities
            .GetProperty("signatureHelpProvider")
            .GetProperty("triggerCharacters")
            .EnumerateArray()
            .Select(trigger => trigger.GetString())
            .ToArray();
        Assert.Contains(" ", signatureTriggers);
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
                        text = "Public Sub Hello()\nDebug.Print \"hi\"\nEnd Sub\n"
                    }
                }
            });

        var completion = await server.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = "file:///C:/work/Module1.bas" },
                position = new { line = 1, character = 5 }
            });

        var completionItems = completion.GetProperty("result").EnumerateArray().ToArray();
        var completionLabels = completionItems.Select(item => item.GetProperty("label").GetString()).ToArray();
        Assert.Contains("Hello", completionLabels);
        Assert.Contains("Sub", completionLabels);

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
    public async Task Server_advertises_semantic_tokens_and_updates_after_document_change()
    {
        var serverProjectPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "VbaLanguageServer.Cli",
                "VbaLanguageServer.Cli.csproj"));

        await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

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

        await process.SendRequestAsync(4, "shutdown", null);
        await process.SendNotificationAsync("exit", null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(cancellation.Token);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Server_returns_project_symbol_semantic_tokens_for_range_bounds_scenario()
    {
        var serverProjectPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "VbaLanguageServer.Cli",
                "VbaLanguageServer.Cli.csproj"));

        await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

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

        await process.SendRequestAsync(3, "shutdown", null);
        await process.SendNotificationAsync("exit", null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(cancellation.Token);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Server_publishes_diagnostics_after_open_and_change_notifications()
    {
        var serverProjectPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "VbaLanguageServer.Cli",
                "VbaLanguageServer.Cli.csproj"));

        await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

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

        await process.SendRequestAsync(2, "shutdown", null);
        await process.SendNotificationAsync("exit", null);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(cancellation.Token);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Server_returns_document_symbols_for_representative_source_definitions()
    {
        var serverProjectPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "VbaLanguageServer.Cli",
                "VbaLanguageServer.Cli.csproj"));

        await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

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

        await process.SendRequestAsync(5, "shutdown", null);
        await process.SendNotificationAsync("exit", null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(cancellation.Token);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Server_resolves_representative_source_definitions_and_ambiguity()
    {
        var serverProjectPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "VbaLanguageServer.Cli",
                "VbaLanguageServer.Cli.csproj"));

        await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

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

        await process.SendRequestAsync(8, "shutdown", null);
        await process.SendNotificationAsync("exit", null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(cancellation.Token);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Server_returns_source_completion_items_and_language_vocabulary()
    {
        var serverProjectPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "VbaLanguageServer.Cli",
                "VbaLanguageServer.Cli.csproj"));

        await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

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
        Assert.Contains("RunMode", labels);
        Assert.Contains("Automatic", labels);
        Assert.Contains("currentValue", labels);
        Assert.Contains("If", labels);
        Assert.Contains("String", labels);

        var publicModuleVariableCompletion = await process.SendRequestAsync(3,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = callerUri },
                position = new { line = 6, character = "    WsSr".Length }
            });
        var publicModuleVariableLabels = publicModuleVariableCompletion
            .GetProperty("result")
            .EnumerateArray()
            .Select(item => item.GetProperty("label").GetString())
            .ToArray();
        Assert.Contains("WsSrv", publicModuleVariableLabels);
        Assert.DoesNotContain("HiddenSrv", publicModuleVariableLabels);

        var outsideProcedureCompletion = await process.SendRequestAsync(4,
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

        await process.SendRequestAsync(5, "shutdown", null);
        await process.SendNotificationAsync("exit", null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(cancellation.Token);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Server_returns_member_completion_without_language_vocabulary()
    {
        var serverProjectPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "VbaLanguageServer.Cli",
                "VbaLanguageServer.Cli.csproj"));

        await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

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

        await process.SendRequestAsync(8, "shutdown", null);
        await process.SendNotificationAsync("exit", null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(cancellation.Token);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Server_returns_hover_and_signature_help_for_source_callables()
    {
        var serverProjectPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "VbaLanguageServer.Cli",
                "VbaLanguageServer.Cli.csproj"));

        await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

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
        Assert.Contains("ReadValue(Key, [Fallback]) As String", hoverValue);

        var signature = await SendPositionRequestAsync(process, 3, "textDocument/signatureHelp", uri, text, "ReadValue(\"id\", ", "ReadValue(\"id\", ".Length);
        var result = signature.GetProperty("result");
        Assert.Equal(1, result.GetProperty("activeParameter").GetInt32());
        var firstSignature = result.GetProperty("signatures").EnumerateArray().Single();
        Assert.Equal("ReadValue(Key, [Fallback]) As String", firstSignature.GetProperty("label").GetString());
        var parameters = firstSignature.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal("Key", parameters[0].GetProperty("label").GetString());
        Assert.Contains("Key to read.", parameters[0].GetProperty("documentation").GetProperty("value").GetString());
        Assert.Equal("Fallback", parameters[1].GetProperty("label").GetString());
        Assert.Contains("Value used when the key is missing.", parameters[1].GetProperty("documentation").GetProperty("value").GetString());

        var statementSignature = await SendPositionRequestAsync(process, 4, "textDocument/signatureHelp", uri, text, "ReadValue ", "ReadValue ".Length);
        var statementResult = statementSignature.GetProperty("result");
        Assert.Equal(0, statementResult.GetProperty("activeParameter").GetInt32());
        Assert.Equal(
            "ReadValue(Key, [Fallback]) As String",
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

        await process.SendRequestAsync(7, "shutdown", null);
        await process.SendNotificationAsync("exit", null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(cancellation.Token);
        Assert.Equal(0, process.ExitCode);
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

            var serverProjectPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "VbaLanguageServer.Cli",
                    "VbaLanguageServer.Cli.csproj"));

            await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Option Explicit",
                "",
                "Public Sub Run()",
                "    ",
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
                    position = new { line = 4, character = 4 }
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
            Assert.Equal("Run(Macro, [Arg1])", firstSignature.GetProperty("label").GetString());
            Assert.Contains(
                "The macro or function to run.",
                firstSignature.GetProperty("parameters").EnumerateArray().First().GetProperty("documentation").GetProperty("value").GetString(),
                StringComparison.Ordinal);

            await process.SendRequestAsync(6, "shutdown", null);
            await process.SendNotificationAsync("exit", null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
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

            var serverProjectPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "VbaLanguageServer.Cli",
                    "VbaLanguageServer.Cli.csproj"));

            await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath, cacheRoot);

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "End Sub"
            ])));

            var logMessage = await process.WaitForLogTextAsync("source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");

            Assert.Contains("Generated Library", logMessage);
            await process.SendRequestAsync(2, "shutdown", null);
            await process.SendNotificationAsync("exit", null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, process.ExitCode);
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
            var serverProjectPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "VbaLanguageServer.Cli",
                    "VbaLanguageServer.Cli.csproj"));

            await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "End Sub"
            ])));

            var logMessage = await process.WaitForLogTextAsync("source=bundled outcome=available");

            Assert.Contains("Microsoft Excel 16.0 Object Library", logMessage);
            await process.SendRequestAsync(2, "shutdown", null);
            await process.SendNotificationAsync("exit", null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_uses_current_persisted_excel_catalog_for_first_completion_without_discovery()
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

            var serverProjectPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "VbaLanguageServer.Cli",
                    "VbaLanguageServer.Cli.csproj"));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                serverProjectPath,
                cacheRoot,
                new Dictionary<string, string>
                {
                    ["VBA_TOOLS_REFERENCE_CATALOG_DISCOVERY_STARTED_FILE"] = discoveryStartedFile,
                    ["VBA_TOOLS_REFERENCE_CATALOG_DISCOVERY_RELEASE_FILE"] = discoveryReleaseFile
                });

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var text = CreateExcelStartupCatalogWorkerText();
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));

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
            await process.SendRequestAsync(3, "shutdown", null);
            await process.SendNotificationAsync("exit", null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
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

            var serverProjectPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "VbaLanguageServer.Cli",
                    "VbaLanguageServer.Cli.csproj"));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                serverProjectPath,
                cacheRoot,
                new Dictionary<string, string>
                {
                    ["VBA_TOOLS_REFERENCE_CATALOG_DISCOVERY_STARTED_FILE"] = discoveryStartedFile,
                    ["VBA_TOOLS_REFERENCE_CATALOG_DISCOVERY_RELEASE_FILE"] = discoveryReleaseFile
                });

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var text = CreateExcelStartupCatalogWorkerText();
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, text));
            await WaitForFileAsync(discoveryStartedFile, TimeSpan.FromSeconds(5));

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
            var signatureHelp = await SendPositionRequestAsync(process, 3,
                "textDocument/signatureHelp",
                uri,
                text,
                "Range(",
                "Range(".Length);
            var semanticTokensResponse = await process.SendRequestAsync(4,
                "textDocument/semanticTokens/full",
                new
                {
                    textDocument = new { uri }
                });
            var semanticTokens = DecodeSemanticTokens(semanticTokensResponse, text);

            Assert.Contains("Worksheets", completionLabels);
            var signature = signatureHelp
                .GetProperty("result")
                .GetProperty("signatures")
                .EnumerateArray()
                .Single();
            Assert.Equal("Range(Cell1, Cell2) As Range", signature.GetProperty("label").GetString());
            Assert.Contains(semanticTokens, token =>
                token.Text == "Workbook"
                && token.TokenType == "class"
                && !token.TokenModifiers.Contains("declaration"));
            Assert.Contains(semanticTokens, token =>
                token.Text == "Range"
                && token.TokenType == "property"
                && token.Line == 8);

            File.WriteAllText(discoveryReleaseFile, "release");
            await process.SendRequestAsync(5, "shutdown", null);
            await process.SendNotificationAsync("exit", null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
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

            var serverProjectPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "VbaLanguageServer.Cli",
                    "VbaLanguageServer.Cli.csproj"));

            await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath, cacheRoot);

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

            await process.SendRequestAsync(2, "shutdown", null);
            await process.SendNotificationAsync("exit", null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, process.ExitCode);
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

            var serverProjectPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "VbaLanguageServer.Cli",
                    "VbaLanguageServer.Cli.csproj"));

            await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath, cacheRoot);

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

            await process.SendRequestAsync(2, "shutdown", null);
            await process.SendNotificationAsync("exit", null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, process.ExitCode);
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

            var serverProjectPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "VbaLanguageServer.Cli",
                    "VbaLanguageServer.Cli.csproj"));

            await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

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

            await process.SendRequestAsync(3, "shutdown", null);
            await process.SendNotificationAsync("exit", null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, process.ExitCode);
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

            var serverProjectPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "VbaLanguageServer.Cli",
                    "VbaLanguageServer.Cli.csproj"));

            await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

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
            Assert.Equal("Range(Cell1, [Cell2]) As Range", firstSignature.GetProperty("label").GetString());
            var parameterLabels = firstSignature
                .GetProperty("parameters")
                .EnumerateArray()
                .Select(parameter => parameter.GetProperty("label").GetString() ?? "")
                .ToArray();
            Assert.Equal(["Cell1", "Cell2"], parameterLabels);

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
            Assert.Equal("Range(Cell1, [Cell2]) As Range", secondSignature.GetProperty("label").GetString());

            await process.SendRequestAsync(4, "shutdown", null);
            await process.SendNotificationAsync("exit", null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, process.ExitCode);
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

            var serverProjectPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "VbaLanguageServer.Cli",
                    "VbaLanguageServer.Cli.csproj"));

            await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

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

            await process.SendRequestAsync(3, "shutdown", null);
            await process.SendNotificationAsync("exit", null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, process.ExitCode);
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

            var serverProjectPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "VbaLanguageServer.Cli",
                    "VbaLanguageServer.Cli.csproj"));

            await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

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

            await process.SendRequestAsync(3, "shutdown", null);
            await process.SendNotificationAsync("exit", null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, process.ExitCode);
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

            var serverProjectPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "VbaLanguageServer.Cli",
                    "VbaLanguageServer.Cli.csproj"));

            await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

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

            await process.SendRequestAsync(5, "shutdown", null);
            await process.SendNotificationAsync("exit", null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, process.ExitCode);
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

            var serverProjectPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "VbaLanguageServer.Cli",
                    "VbaLanguageServer.Cli.csproj"));

            await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

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

            await process.SendRequestAsync(4, "shutdown", null);
            await process.SendNotificationAsync("exit", null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_renames_source_targets_and_rejects_non_renameable_inputs()
    {
        var serverProjectPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "VbaLanguageServer.Cli",
                "VbaLanguageServer.Cli.csproj"));

        await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

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

        var stringRename = await SendPositionRequestAsync(process, 3,
            "textDocument/rename",
            uri,
            text,
            "\"BuildValue\"",
            1,
            new { newName = "IgnoredValue" });
        Assert.Equal(JsonValueKind.Null, stringRename.GetProperty("result").ValueKind);

        await process.SendRequestAsync(4, "shutdown", null);
        await process.SendNotificationAsync("exit", null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(cancellation.Token);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Server_formats_source_casing_and_indentation()
    {
        var serverProjectPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "VbaLanguageServer.Cli",
                "VbaLanguageServer.Cli.csproj"));

        await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

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

        await process.SendRequestAsync(4, "shutdown", null);
        await process.SendNotificationAsync("exit", null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(cancellation.Token);
        Assert.Equal(0, process.ExitCode);
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

            var serverProjectPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "VbaLanguageServer.Cli",
                    "VbaLanguageServer.Cli.csproj"));

            await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

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

            await process.SendRequestAsync(5, "shutdown", null);
            await process.SendNotificationAsync("exit", null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, process.ExitCode);
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

            var serverProjectPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "VbaLanguageServer.Cli",
                    "VbaLanguageServer.Cli.csproj"));

            await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

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

            await process.SendRequestAsync(2, "shutdown", null);
            await process.SendNotificationAsync("exit", null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, process.ExitCode);
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
                      "references": [
                        {
                          "name": "Microsoft Excel 16.0 Object Library"
                        },
                        {
                          "name": "Microsoft Scripting Runtime"
                        }
                      ]
                    },
                    "SecondBook": {
                      "kind": "excel",
                      "sourcePath": "src/SecondBook",
                      "templatePath": "src/SecondBook/SecondBook.xlsm",
                      "binPath": "bin/SecondBook/SecondBook.xlsm",
                      "publishPath": "publish/SecondBook/SecondBook.xlsm",
                      "references": [
                        {
                          "name": "Microsoft Scripting Runtime"
                        }
                      ]
                    }
                  }
                }
                """);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "SecondBook"));

            var serverProjectPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "VbaLanguageServer.Cli",
                    "VbaLanguageServer.Cli.csproj"));

            await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

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

            await process.SendRequestAsync(2, "shutdown", null);
            await process.SendNotificationAsync("exit", null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, process.ExitCode);
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
            var serverProjectPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "VbaLanguageServer.Cli",
                    "VbaLanguageServer.Cli.csproj"));

            await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

            await process.InitializeAsync();
            var uri = ToFileUri(Path.Combine(projectRoot, "Worker.bas"));
            await process.SendNotificationAsync("textDocument/didOpen", CreateOpenDocument(uri, string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "End Sub"
            ])));

            var selection = await process.TryWaitForLogMessageAsync("VbaProjectReferenceSelection", TimeSpan.FromMilliseconds(500));
            Assert.Null(selection);

            await process.SendRequestAsync(2, "shutdown", null);
            await process.SendNotificationAsync("exit", null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, process.ExitCode);
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

            var serverProjectPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "VbaLanguageServer.Cli",
                    "VbaLanguageServer.Cli.csproj"));

            await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

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
            Assert.Contains("String", looseLabels);

            await process.SendRequestAsync(5, "shutdown", null);
            await process.SendNotificationAsync("exit", null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, process.ExitCode);
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

            var serverProjectPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "VbaLanguageServer.Cli",
                    "VbaLanguageServer.Cli.csproj"));

            await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);

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

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            File.WriteAllBytes(renamedHelperPath, Encoding.GetEncoding(932).GetBytes(renamedHelperText));
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

            await process.SendRequestAsync(6, "shutdown", null);
            await process.SendNotificationAsync("exit", null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, process.ExitCode);
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
            var serverProjectPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "VbaLanguageServer.Cli",
                    "VbaLanguageServer.Cli.csproj"));

            await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);
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

            await process.SendRequestAsync(7, "shutdown", null);
            await process.SendNotificationAsync("exit", null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, process.ExitCode);
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
            var serverProjectPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "VbaLanguageServer.Cli",
                    "VbaLanguageServer.Cli.csproj"));

            await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);
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

            await process.SendRequestAsync(6, "shutdown", null);
            await process.SendNotificationAsync("exit", null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, process.ExitCode);
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
            var serverProjectPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "VbaLanguageServer.Cli",
                    "VbaLanguageServer.Cli.csproj"));

            await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);
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

            await process.SendRequestAsync(12, "shutdown", null);
            await process.SendNotificationAsync("exit", null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, process.ExitCode);
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
            File.WriteAllText(manifestPath, liveManifestText);
            var callerUri = ToFileUri(callerPath);
            var helperUri = ToFileUri(helperPath);
            var manifestUri = ToFileUri(manifestPath);
            var serverProjectPath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "VbaLanguageServer.Cli",
                    "VbaLanguageServer.Cli.csproj"));

            await using var process = await LanguageServerProcessHarness.StartAsync(serverProjectPath: serverProjectPath);
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

            await process.SendRequestAsync(9, "shutdown", null);
            await process.SendNotificationAsync("exit", null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cancellation.Token);
            Assert.Equal(0, process.ExitCode);
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
            var invalidRequest = await server.ReadNextMessageAsync(checkpoint);
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
                VbaSourceIndex.SemanticTokenTypes[tokenTypeIndex],
                DecodeSemanticTokenModifiers(modifierBits),
                line,
                character,
                length));
        }

        return tokens;
    }

    private static IReadOnlyList<string> DecodeSemanticTokenModifiers(int modifierBits)
        => VbaSourceIndex.SemanticTokenModifiers
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
                    references = new[]
                    {
                        new { name = "Microsoft Excel 16.0 Object Library" }
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

    private static bool HasRegisteredTypeLib(string referenceName)
        => new RegistryTypeLibRegistryReader()
            .ReadTypeLibraries()
            .Any(entry => entry.ReferenceName.Equals(referenceName, StringComparison.OrdinalIgnoreCase));

    private static void WriteReferenceCatalogProjectManifest(string projectRoot, params string[] referenceNames)
    {
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
        var references = referenceNames
            .Select(referenceName => new { name = referenceName })
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
                    TypeReference: new VbaTypeReference("Worksheets", "Excel")),
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
                    TypeReference: new VbaTypeReference("Range", "Excel"))
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
