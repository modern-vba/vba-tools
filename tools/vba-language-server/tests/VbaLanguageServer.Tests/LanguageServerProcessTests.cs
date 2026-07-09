using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class LanguageServerProcessTests
{
    [Fact]
    public async Task Server_handles_initialize_text_sync_completion_and_shutdown()
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

        using var process = StartLanguageServer(serverProjectPath);
        await using var stdin = process.StandardInput.BaseStream;
        using var stdout = process.StandardOutput.BaseStream;

        var initialize = await SendRequestAsync(
            stdin,
            stdout,
            1,
            "initialize",
            new
            {
                processId = Environment.ProcessId,
                rootUri = (string?)null,
                capabilities = new { }
            });

        Assert.Equal(1, initialize.GetProperty("id").GetInt32());
        var capabilities = initialize
            .GetProperty("result")
            .GetProperty("capabilities");
        Assert.Equal(2, capabilities.GetProperty("textDocumentSync").GetInt32());
        Assert.True(capabilities.TryGetProperty("completionProvider", out _));

        await SendNotificationAsync(stdin, "initialized", new { });
        await SendNotificationAsync(
            stdin,
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
        await SendNotificationAsync(
            stdin,
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

        var completion = await SendRequestAsync(
            stdin,
            stdout,
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

        var shutdown = await SendRequestAsync(stdin, stdout, 3, "shutdown", null);
        Assert.Equal(JsonValueKind.Null, shutdown.GetProperty("result").ValueKind);
        await SendNotificationAsync(stdin, "exit", null);

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

        using var process = StartLanguageServer(serverProjectPath);
        await using var stdin = process.StandardInput.BaseStream;
        using var stdout = process.StandardOutput.BaseStream;

        await SendRequestAsync(
            stdin,
            stdout,
            1,
            "initialize",
            new
            {
                processId = Environment.ProcessId,
                rootUri = (string?)null,
                capabilities = new { }
            });
        await SendNotificationAsync(stdin, "initialized", new { });

        const string invalidLine = "        \"needle\", _ ' comment";
        await SendNotificationAsync(
            stdin,
            "textDocument/didOpen",
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

        var invalidDiagnostics = await ReadNotificationAsync(stdout, "textDocument/publishDiagnostics");
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

        await SendNotificationAsync(
            stdin,
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

        var validDiagnostics = await ReadNotificationAsync(stdout, "textDocument/publishDiagnostics");
        Assert.Empty(validDiagnostics.GetProperty("params").GetProperty("diagnostics").EnumerateArray());

        await SendRequestAsync(stdin, stdout, 2, "shutdown", null);
        await SendNotificationAsync(stdin, "exit", null);

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

        using var process = StartLanguageServer(serverProjectPath);
        await using var stdin = process.StandardInput.BaseStream;
        using var stdout = process.StandardOutput.BaseStream;

        await InitializeAsync(stdin, stdout);
        await SendNotificationAsync(
            stdin,
            "textDocument/didOpen",
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

        var response = await SendRequestAsync(
            stdin,
            stdout,
            2,
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
        await SendNotificationAsync(stdin, "textDocument/didOpen", CreateOpenDocument(classUri, string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Customer\"",
            "Option Explicit",
            "Public Event Changed()",
            "Public Property Get DisplayName() As String",
            "End Property"
        ])));
        var classSymbols = await SendRequestAsync(
            stdin,
            stdout,
            3,
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
        await SendNotificationAsync(stdin, "textDocument/didOpen", CreateOpenDocument(formUri, string.Join('\n', [
            "VERSION 5.00",
            "Begin VB.Form Dialog",
            "  Caption = \"Designer caption\"",
            "End",
            "Attribute VB_Name = \"Dialog\"",
            "Option Explicit",
            "Private Sub CommandButton1_Click()",
            "End Sub"
        ])));
        var formSymbols = await SendRequestAsync(
            stdin,
            stdout,
            4,
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

        await SendRequestAsync(stdin, stdout, 5, "shutdown", null);
        await SendNotificationAsync(stdin, "exit", null);
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

        using var process = StartLanguageServer(serverProjectPath);
        await using var stdin = process.StandardInput.BaseStream;
        using var stdout = process.StandardOutput.BaseStream;

        await InitializeAsync(stdin, stdout);
        const string helperUri = "file:///C:/work/Helpers.bas";
        const string workerUri = "file:///C:/work/Worker.bas";
        const string duplicateAUri = "file:///C:/work/DuplicateA.bas";
        const string duplicateBUri = "file:///C:/work/DuplicateB.bas";
        await SendNotificationAsync(stdin, "textDocument/didOpen", CreateOpenDocument(helperUri, string.Join('\n', [
            "Attribute VB_Name = \"Helpers\"",
            "Option Explicit",
            "",
            "Public Function BuildValue() As String",
            "End Function",
            "Private Function HiddenValue() As String",
            "End Function"
        ])));
        await SendNotificationAsync(stdin, "textDocument/didOpen", CreateOpenDocument(duplicateAUri, string.Join('\n', [
            "Attribute VB_Name = \"DuplicateA\"",
            "Public Function DuplicateValue() As String",
            "End Function"
        ])));
        await SendNotificationAsync(stdin, "textDocument/didOpen", CreateOpenDocument(duplicateBUri, string.Join('\n', [
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
        await SendNotificationAsync(stdin, "textDocument/didOpen", CreateOpenDocument(workerUri, workerText));

        var unqualified = await RequestDefinitionAsync(stdin, stdout, 2, workerUri, workerText, "BuildValue()");
        Assert.Equal(helperUri, unqualified.GetProperty("uri").GetString());
        Assert.Equal(3, unqualified.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal("Public Function ".Length, unqualified.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());

        var qualified = await RequestDefinitionAsync(stdin, stdout, 3, workerUri, workerText, "Helpers.BuildValue()", "Helpers.".Length);
        Assert.Equal(helperUri, qualified.GetProperty("uri").GetString());

        var privateResult = await SendDefinitionRequestAsync(stdin, stdout, 4, workerUri, workerText, "HiddenValue()");
        Assert.Equal(JsonValueKind.Null, privateResult.ValueKind);

        var ambiguousResult = await SendDefinitionRequestAsync(stdin, stdout, 5, workerUri, workerText, "DuplicateValue()");
        Assert.Equal(JsonValueKind.Null, ambiguousResult.ValueKind);

        var fallbackSymbols = await SendRequestAsync(
            stdin,
            stdout,
            6,
            "textDocument/documentSymbol",
            new
            {
                textDocument = new { uri = workerUri }
            });
        Assert.Equal("Worker", fallbackSymbols.GetProperty("result").EnumerateArray().First().GetProperty("name").GetString());

        await SendRequestAsync(stdin, stdout, 7, "shutdown", null);
        await SendNotificationAsync(stdin, "exit", null);
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

        using var process = StartLanguageServer(serverProjectPath);
        await using var stdin = process.StandardInput.BaseStream;
        using var stdout = process.StandardOutput.BaseStream;

        await InitializeAsync(stdin, stdout);
        await SendNotificationAsync(stdin, "textDocument/didOpen", CreateOpenDocument("file:///C:/work/Builder.bas", string.Join('\n', [
            "Attribute VB_Name = \"Builder\"",
            "Option Explicit",
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
            "    ",
            "End Sub"
        ]);
        const string callerUri = "file:///C:/work/Caller.bas";
        await SendNotificationAsync(stdin, "textDocument/didOpen", CreateOpenDocument(callerUri, callerText));

        var completion = await SendRequestAsync(
            stdin,
            stdout,
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = callerUri },
                position = new { line = 4, character = 4 }
            });
        var labels = completion
            .GetProperty("result")
            .EnumerateArray()
            .Select(item => item.GetProperty("label").GetString())
            .ToArray();

        Assert.Contains("BuildValue", labels);
        Assert.Contains("RunMode", labels);
        Assert.Contains("Automatic", labels);
        Assert.Contains("If", labels);
        Assert.Contains("String", labels);

        await SendRequestAsync(stdin, stdout, 3, "shutdown", null);
        await SendNotificationAsync(stdin, "exit", null);
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

        using var process = StartLanguageServer(serverProjectPath);
        await using var stdin = process.StandardInput.BaseStream;
        using var stdout = process.StandardOutput.BaseStream;

        await InitializeAsync(stdin, stdout);
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "",
            "'* @brief Reads a value.",
            "'* @param Key Key to read.",
            "'* @param Fallback Value used when the key is missing.",
            "'* @return The configured value.",
            "Public Function ReadValue(ByVal Key As String, ByVal Fallback As String) As String",
            "End Function",
            "",
            "Public Sub Run()",
            "    ReadValue(\"id\", ",
            "End Sub"
        ]);
        await SendNotificationAsync(stdin, "textDocument/didOpen", CreateOpenDocument(uri, text));

        var hover = await SendPositionRequestAsync(stdin, stdout, 2, "textDocument/hover", uri, text, "ReadValue(\"id\"");
        var hoverValue = hover
            .GetProperty("result")
            .GetProperty("contents")
            .GetProperty("value")
            .GetString();
        Assert.Contains("Reads a value.", hoverValue);
        Assert.Contains("ReadValue(Key, Fallback) As String", hoverValue);

        var signature = await SendPositionRequestAsync(stdin, stdout, 3, "textDocument/signatureHelp", uri, text, "ReadValue(\"id\", ", "ReadValue(\"id\", ".Length);
        var result = signature.GetProperty("result");
        Assert.Equal(1, result.GetProperty("activeParameter").GetInt32());
        var firstSignature = result.GetProperty("signatures").EnumerateArray().Single();
        Assert.Equal("ReadValue(Key, Fallback) As String", firstSignature.GetProperty("label").GetString());
        var parameters = firstSignature.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal("Key", parameters[0].GetProperty("label").GetString());
        Assert.Contains("Key to read.", parameters[0].GetProperty("documentation").GetProperty("value").GetString());
        Assert.Equal("Fallback", parameters[1].GetProperty("label").GetString());
        Assert.Contains("Value used when the key is missing.", parameters[1].GetProperty("documentation").GetProperty("value").GetString());

        await SendRequestAsync(stdin, stdout, 4, "shutdown", null);
        await SendNotificationAsync(stdin, "exit", null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(cancellation.Token);
        Assert.Equal(0, process.ExitCode);
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

        using var process = StartLanguageServer(serverProjectPath);
        await using var stdin = process.StandardInput.BaseStream;
        using var stdout = process.StandardOutput.BaseStream;

        await InitializeAsync(stdin, stdout);
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
        await SendNotificationAsync(stdin, "textDocument/didOpen", CreateOpenDocument(uri, text));

        var rename = await SendPositionRequestAsync(
            stdin,
            stdout,
            2,
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

        var stringRename = await SendPositionRequestAsync(
            stdin,
            stdout,
            3,
            "textDocument/rename",
            uri,
            text,
            "\"BuildValue\"",
            1,
            new { newName = "IgnoredValue" });
        Assert.Equal(JsonValueKind.Null, stringRename.GetProperty("result").ValueKind);

        await SendRequestAsync(stdin, stdout, 4, "shutdown", null);
        await SendNotificationAsync(stdin, "exit", null);
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

        using var process = StartLanguageServer(serverProjectPath);
        await using var stdin = process.StandardInput.BaseStream;
        using var stdout = process.StandardOutput.BaseStream;

        await InitializeAsync(stdin, stdout);
        const string builderUri = "file:///C:/work/Builder.bas";
        await SendNotificationAsync(stdin, "textDocument/didOpen", CreateOpenDocument(builderUri, string.Join('\n', [
            "Attribute VB_Name = \"Builder\"",
            "Option Explicit",
            "",
            "Public Function BuildValue() As String",
            "End Function"
        ])));
        const string callerUri = "file:///C:/work/Caller.bas";
        var text = string.Join('\n', [
            "Attribute vb_name = \"Caller\"",
            "option explicit",
            "",
            "public sub Run()",
            "buildvalue",
            "if true then",
            "'* @brief buildvalue remains prose.",
            "else",
            "' buildvalue remains an ordinary comment.",
            "end if",
            "End Sub"
        ]);
        await SendNotificationAsync(stdin, "textDocument/didOpen", CreateOpenDocument(callerUri, text));

        var formatting = await SendRequestAsync(
            stdin,
            stdout,
            2,
            "textDocument/formatting",
            new
            {
                textDocument = new { uri = callerUri },
                options = new { tabSize = 4, insertSpaces = true }
            });
        var edit = formatting.GetProperty("result").EnumerateArray().Single();
        Assert.Equal(string.Join('\n', [
            "Attribute VB_Name = \"Caller\"",
            "Option Explicit",
            "",
            "Public Sub Run()",
            "    BuildValue",
            "    If True Then",
            "        '* @brief buildvalue remains prose.",
            "    Else",
            "        ' buildvalue remains an ordinary comment.",
            "    End If",
            "End Sub"
        ]), edit.GetProperty("newText").GetString());

        await SendRequestAsync(stdin, stdout, 3, "shutdown", null);
        await SendNotificationAsync(stdin, "exit", null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(cancellation.Token);
        Assert.Equal(0, process.ExitCode);
    }

    private static Process StartLanguageServer(string serverProjectPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(serverProjectPath);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the language server process.");

        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                Debug.WriteLine(args.Data);
            }
        };
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task InitializeAsync(Stream stdin, Stream stdout)
    {
        await SendRequestAsync(
            stdin,
            stdout,
            1,
            "initialize",
            new
            {
                processId = Environment.ProcessId,
                rootUri = (string?)null,
                capabilities = new { }
            });
        await SendNotificationAsync(stdin, "initialized", new { });
    }

    private static async Task<JsonElement> SendRequestAsync(
        Stream stdin,
        Stream stdout,
        int id,
        string method,
        object? parameters)
    {
        await WriteMessageAsync(stdin, new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters
        });

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var message = await ReadMessageAsync(stdout, cancellation.Token);
            if (message.TryGetProperty("id", out var responseId)
                && responseId.ValueKind == JsonValueKind.Number
                && responseId.GetInt32() == id)
            {
                return message;
            }
        }
    }

    private static Task SendNotificationAsync(Stream stdin, string method, object? parameters)
    {
        return WriteMessageAsync(stdin, new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters
        });
    }

    private static object CreateOpenDocument(string uri, string text)
    {
        return new
        {
            textDocument = new
            {
                uri,
                languageId = "vba",
                version = 1,
                text
            }
        };
    }

    private static async Task<JsonElement> RequestDefinitionAsync(
        Stream stdin,
        Stream stdout,
        int id,
        string uri,
        string text,
        string needle,
        int offset = 0)
    {
        var result = await SendDefinitionRequestAsync(stdin, stdout, id, uri, text, needle, offset);
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        return result;
    }

    private static async Task<JsonElement> SendDefinitionRequestAsync(
        Stream stdin,
        Stream stdout,
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

        var response = await SendRequestAsync(
            stdin,
            stdout,
            id,
            "textDocument/definition",
            new
            {
                textDocument = new { uri },
                position = new { line, character }
            });
        return response.GetProperty("result");
    }

    private static Task<JsonElement> SendPositionRequestAsync(
        Stream stdin,
        Stream stdout,
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
        return SendRequestAsync(stdin, stdout, id, method, parameters);
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

    private static async Task WriteMessageAsync(Stream stream, object message)
    {
        var json = JsonSerializer.Serialize(
            message,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        var content = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {content.Length}\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(content);
        await stream.FlushAsync();
    }

    private static async Task<JsonElement> ReadNotificationAsync(Stream stdout, string method)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var message = await ReadMessageAsync(stdout, cancellation.Token);
            if (message.TryGetProperty("method", out var methodElement)
                && methodElement.GetString() == method)
            {
                return message;
            }
        }
    }

    private static async Task<JsonElement> ReadMessageAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var headerBytes = new List<byte>();
        var singleByte = new byte[1];
        while (!EndsWithHeaderTerminator(headerBytes))
        {
            var read = await stream.ReadAsync(singleByte.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Language server closed stdout before sending a response.");
            }

            headerBytes.Add(singleByte[0]);
        }

        var headers = Encoding.ASCII.GetString(headerBytes.ToArray());
        var contentLength = headers
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .Where(parts => string.Equals(parts[0], "Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(parts => int.Parse(parts[1].Trim()))
            .Single();

        var content = new byte[contentLength];
        var offset = 0;
        while (offset < content.Length)
        {
            var read = await stream.ReadAsync(content.AsMemory(offset, content.Length - offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Language server closed stdout mid-message.");
            }

            offset += read;
        }

        return JsonDocument.Parse(content).RootElement.Clone();
    }

    private static bool EndsWithHeaderTerminator(List<byte> bytes)
    {
        return bytes.Count >= 4
            && bytes[^4] == '\r'
            && bytes[^3] == '\n'
            && bytes[^2] == '\r'
            && bytes[^1] == '\n';
    }
}
