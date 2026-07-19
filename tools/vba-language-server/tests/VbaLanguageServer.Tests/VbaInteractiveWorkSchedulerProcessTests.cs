using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaInteractiveWorkSchedulerProcessTests
{
    [Fact]
    public async Task Server_accepts_explicit_cancellation_while_a_request_occupies_the_serial_lane()
    {
        var gateRoot = Directory.CreateTempSubdirectory("vba-ls-scheduler-gate-").FullName;
        var startedFile = Path.Combine(gateRoot, "started");
        var cancelledFile = Path.Combine(gateRoot, "cancelled");
        var releaseFile = Path.Combine(gateRoot, "release");
        try
        {
            await using var process = await LanguageServerProcessHarness.StartAsync(
                environment: new Dictionary<string, string>
                {
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_ID"] = "number:2",
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_STARTED_FILE"] = startedFile,
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_CANCELLED_FILE"] = cancelledFile,
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_RELEASE_FILE"] = releaseFile
                });
            try
            {
                await process.InitializeAsync();
                const string uri = "file:///C:/work/SchedulerCancellation.bas";
                await process.SendNotificationAsync(
                    "textDocument/didOpen",
                    CreateOpenDocument(
                        uri,
                        "Attribute VB_Name = \"SchedulerCancellation\"\n"
                        + "Public Sub BeforeCancellation()\n"
                        + "End Sub\n"));
                await process.WaitForDiagnosticsAsync(uri);

                var responseCheckpoint = process.TranscriptCheckpoint;
                var blockedRequest = process.SendRequestAsync(
                    2,
                    "textDocument/documentSymbol",
                    new
                    {
                        textDocument = new { uri }
                    });
                await WaitForFileCreatedAsync(startedFile, TimeSpan.FromSeconds(5));

                await process.SendNotificationAsync(
                    "$/cancelRequest",
                    new
                    {
                        id = 2
                    });
                await WaitForFileCreatedAsync(cancelledFile, TimeSpan.FromSeconds(5));

                var cancelledResponse = await blockedRequest;
                Assert.Equal(
                    -32800,
                    cancelledResponse.GetProperty("error").GetProperty("code").GetInt32());
                var healthyResponse = await process.SendRequestAsync(
                    3,
                    "textDocument/documentSymbol",
                    new
                    {
                        textDocument = new { uri }
                    });
                Assert.Contains(
                    healthyResponse.GetProperty("result").EnumerateArray(),
                    symbol => symbol.GetProperty("name").GetString() == "BeforeCancellation");
                Assert.Equal(1, process.CountResponses(2, responseCheckpoint));

                await process.ShutdownAsync(4);
            }
            finally
            {
                File.WriteAllText(releaseFile, "release");
            }
        }
        finally
        {
            Directory.Delete(gateRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_keeps_one_normal_response_when_completion_wins_before_cancellation()
    {
        var gateRoot = Directory.CreateTempSubdirectory("vba-ls-scheduler-completion-race-").FullName;
        var startedFile = Path.Combine(gateRoot, "started");
        var cancelledFile = Path.Combine(gateRoot, "cancelled");
        var releaseFile = Path.Combine(gateRoot, "release");
        try
        {
            await using var process = await LanguageServerProcessHarness.StartAsync(
                environment: new Dictionary<string, string>
                {
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_ID"] = "number:2",
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_STARTED_FILE"] = startedFile,
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_CANCELLED_FILE"] = cancelledFile,
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_RELEASE_FILE"] = releaseFile
                });
            try
            {
                await process.InitializeAsync();
                const string uri = "file:///C:/work/SchedulerCompletionRace.bas";
                await process.SendNotificationAsync(
                    "textDocument/didOpen",
                    CreateOpenDocument(
                        uri,
                        "Attribute VB_Name = \"SchedulerCompletionRace\"\n"
                        + "Public Sub Completed()\n"
                        + "End Sub\n"));
                await process.WaitForDiagnosticsAsync(uri);
                var responseCheckpoint = process.TranscriptCheckpoint;
                var request = process.SendRequestAsync(
                    2,
                    "textDocument/documentSymbol",
                    new
                    {
                        textDocument = new { uri }
                    });
                await WaitForFileCreatedAsync(startedFile, TimeSpan.FromSeconds(5));

                File.WriteAllText(releaseFile, "release");
                var normalResponse = await request;
                Assert.Contains(
                    normalResponse.GetProperty("result").EnumerateArray(),
                    symbol => symbol.GetProperty("name").GetString() == "Completed");
                await process.SendNotificationAsync(
                    "$/cancelRequest",
                    new
                    {
                        id = 2
                    });
                await process.SendRequestAsync(
                    3,
                    "textDocument/documentSymbol",
                    new
                    {
                        textDocument = new { uri }
                    });

                Assert.Equal(1, process.CountResponses(2, responseCheckpoint));
                Assert.False(File.Exists(cancelledFile));
                await process.ShutdownAsync(4);
            }
            finally
            {
                File.WriteAllText(releaseFile, "release");
            }
        }
        finally
        {
            Directory.Delete(gateRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_preserves_mutation_read_order_without_cancelling_an_earlier_read()
    {
        var gateRoot = Directory.CreateTempSubdirectory("vba-ls-scheduler-order-").FullName;
        var admissionDirectory = Directory.CreateDirectory(
            Path.Combine(gateRoot, "admissions")).FullName;
        var startedFile = Path.Combine(gateRoot, "started");
        var cancelledFile = Path.Combine(gateRoot, "cancelled");
        var releaseFile = Path.Combine(gateRoot, "release");
        try
        {
            await using var process = await LanguageServerProcessHarness.StartAsync(
                environment: new Dictionary<string, string>
                {
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_ID"] = "number:2",
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_STARTED_FILE"] = startedFile,
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_CANCELLED_FILE"] = cancelledFile,
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_RELEASE_FILE"] = releaseFile,
                    ["VBA_TOOLS_INTERACTIVE_ADMISSION_DIRECTORY"] = admissionDirectory
                });
            try
            {
                await process.InitializeAsync();
                const string uri = "file:///C:/work/SchedulerOrdering.bas";
                var oldText = "Attribute VB_Name = \"SchedulerOrdering\"\n"
                    + "Public Sub OldProcedure()\n"
                    + "End Sub\n";
                var newText = "Attribute VB_Name = \"SchedulerOrdering\"\n"
                    + "Public Sub NewProcedure()\n"
                    + "End Sub\n";
                await process.SendNotificationAsync(
                    "textDocument/didOpen",
                    CreateOpenDocument(uri, oldText));
                await process.WaitForDiagnosticsAsync(uri);

                var oldRead = process.SendRequestAsync(
                    2,
                    "textDocument/documentSymbol",
                    new
                    {
                        textDocument = new { uri }
                    });
                await WaitForFileCreatedAsync(startedFile, TimeSpan.FromSeconds(5));
                await process.SendNotificationAsync(
                    "textDocument/didChange",
                    CreateChangedDocument(uri, version: 2, newText));
                var newRead = process.SendRequestAsync(
                    3,
                    "textDocument/documentSymbol",
                    new
                    {
                        textDocument = new { uri }
                    });
                await WaitForMatchingFileCreatedAsync(
                    admissionDirectory,
                    fileName => fileName.EndsWith("-number-3.admitted", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(5));

                Assert.False(File.Exists(cancelledFile));
                File.WriteAllText(releaseFile, "release");
                var oldResponse = await oldRead;
                var newResponse = await newRead;
                Assert.Contains(
                    oldResponse.GetProperty("result").EnumerateArray(),
                    symbol => symbol.GetProperty("name").GetString() == "OldProcedure");
                Assert.Contains(
                    newResponse.GetProperty("result").EnumerateArray(),
                    symbol => symbol.GetProperty("name").GetString() == "NewProcedure");
                Assert.False(File.Exists(cancelledFile));

                await process.ShutdownAsync(4);
            }
            finally
            {
                File.WriteAllText(releaseFile, "release");
            }
        }
        finally
        {
            Directory.Delete(gateRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_coalesces_adjacent_didChange_burst_before_the_next_read()
    {
        var gateRoot = Directory.CreateTempSubdirectory("vba-ls-scheduler-coalesce-").FullName;
        var admissionDirectory = Directory.CreateDirectory(
            Path.Combine(gateRoot, "admissions")).FullName;
        var startedFile = Path.Combine(gateRoot, "started");
        var releaseFile = Path.Combine(gateRoot, "release");
        try
        {
            await using var process = await LanguageServerProcessHarness.StartAsync(
                environment: new Dictionary<string, string>
                {
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_ID"] = "number:2",
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_STARTED_FILE"] = startedFile,
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_RELEASE_FILE"] = releaseFile,
                    ["VBA_TOOLS_INTERACTIVE_ADMISSION_DIRECTORY"] = admissionDirectory
                });
            try
            {
                await process.InitializeAsync();
                const string uri = "file:///C:/work/SchedulerCoalescing.bas";
                var oldText = "Attribute VB_Name = \"SchedulerCoalescing\"\n"
                    + "Public Sub OldProcedure()\n"
                    + "End Sub\n";
                var versionTwoText = "Attribute VB_Name = \"SchedulerCoalescing\"\n"
                    + "Public Sub VersionTwoProcedure()\n"
                    + "End Sub\n";
                var versionThreeText = "Attribute VB_Name = \"SchedulerCoalescing\"\n"
                    + "Public Sub VersionThreeProcedure()\n"
                    + "End Sub\n";
                await process.SendNotificationAsync(
                    "textDocument/didOpen",
                    CreateOpenDocument(uri, oldText));
                await process.WaitForDiagnosticsAsync(uri);

                var oldRead = process.SendRequestAsync(
                    2,
                    "textDocument/documentSymbol",
                    new
                    {
                        textDocument = new { uri }
                    });
                await WaitForFileCreatedAsync(startedFile, TimeSpan.FromSeconds(5));
                await process.SendNotificationAsync(
                    "textDocument/didChange",
                    CreateChangedDocument(uri, version: 2, versionTwoText));
                await process.SendNotificationAsync(
                    "textDocument/didChange",
                    CreateChangedDocument(uri, version: 3, versionThreeText));
                var latestRead = process.SendRequestAsync(
                    3,
                    "textDocument/documentSymbol",
                    new
                    {
                        textDocument = new { uri }
                    });
                await WaitForMatchingFileCreatedAsync(
                    admissionDirectory,
                    fileName => fileName.EndsWith("-number-3.admitted", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(5));

                File.WriteAllText(releaseFile, "release");
                var oldResponse = await oldRead;
                var latestResponse = await latestRead;
                Assert.Contains(
                    oldResponse.GetProperty("result").EnumerateArray(),
                    symbol => symbol.GetProperty("name").GetString() == "OldProcedure");
                Assert.Contains(
                    latestResponse.GetProperty("result").EnumerateArray(),
                    symbol => symbol.GetProperty("name").GetString() == "VersionThreeProcedure");
                Assert.DoesNotContain(
                    latestResponse.GetProperty("result").EnumerateArray(),
                    symbol => symbol.GetProperty("name").GetString() == "VersionTwoProcedure");
                Assert.Equal(
                    2,
                    CountCompletedFiles(
                        admissionDirectory,
                        "-mutation-textDocument_didChange-none.completed"));
                Assert.Contains(
                    Directory.EnumerateFiles(admissionDirectory, "*.completed"),
                    path => path.EndsWith(
                            "-mutation-textDocument_didChange-none.completed",
                            StringComparison.Ordinal)
                        && File.ReadLines(path).Contains(
                            "executionMilliseconds=0.000000"));

                await process.ShutdownAsync(4);
            }
            finally
            {
                File.WriteAllText(releaseFile, "release");
            }
        }
        finally
        {
            Directory.Delete(gateRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_does_not_coalesce_across_exact_version_block_skeleton_request()
    {
        var gateRoot = Directory.CreateTempSubdirectory("vba-ls-scheduler-exact-fence-").FullName;
        var admissionDirectory = Directory.CreateDirectory(
            Path.Combine(gateRoot, "admissions")).FullName;
        var startedFile = Path.Combine(gateRoot, "started");
        var releaseFile = Path.Combine(gateRoot, "release");
        try
        {
            await using var process = await LanguageServerProcessHarness.StartAsync(
                environment: new Dictionary<string, string>
                {
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_ID"] = "number:2",
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_STARTED_FILE"] = startedFile,
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_RELEASE_FILE"] = releaseFile,
                    ["VBA_TOOLS_INTERACTIVE_ADMISSION_DIRECTORY"] = admissionDirectory
                });
            try
            {
                await process.InitializeAsync();
                const string uri = "file:///C:/work/SchedulerExactFence.bas";
                var versionOneText = "Attribute VB_Name = \"SchedulerExactFence\"\n"
                    + "Public Sub Existing()\n"
                    + "End Sub\n";
                var versionTwoHeader = "Public Function Pending() As String";
                var versionTwoText = "Attribute VB_Name = \"SchedulerExactFence\"\n"
                    + versionTwoHeader
                    + "\n";
                var versionThreeText = "Attribute VB_Name = \"SchedulerExactFence\"\n"
                    + "Public Sub Later()\n"
                    + "End Sub\n";
                await process.SendNotificationAsync(
                    "textDocument/didOpen",
                    CreateOpenDocument(uri, versionOneText));
                await process.WaitForDiagnosticsAsync(uri);

                var blockedRead = process.SendRequestAsync(
                    2,
                    "textDocument/documentSymbol",
                    new
                    {
                        textDocument = new { uri }
                    });
                await WaitForFileCreatedAsync(startedFile, TimeSpan.FromSeconds(5));
                await process.SendNotificationAsync(
                    "textDocument/didChange",
                    CreateChangedDocument(uri, version: 2, versionTwoText));
                var exactRequest = process.SendRequestAsync(
                    3,
                    "vba/blockSkeletonInsertion",
                    new
                    {
                        documentUri = uri,
                        documentVersion = 2,
                        position = new
                        {
                            line = 1,
                            character = versionTwoHeader.Length
                        },
                        options = new
                        {
                            insertSpaces = true,
                            indentSize = 4,
                            tabSize = 4
                        }
                    });
                await process.SendNotificationAsync(
                    "textDocument/didChange",
                    CreateChangedDocument(uri, version: 3, versionThreeText));
                var latestRead = process.SendRequestAsync(
                    4,
                    "textDocument/documentSymbol",
                    new
                    {
                        textDocument = new { uri }
                    });
                await WaitForMatchingFileCreatedAsync(
                    admissionDirectory,
                    fileName => fileName.EndsWith("-number-3.admitted", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(5));

                File.WriteAllText(releaseFile, "release");
                await blockedRead;
                var exactResponse = await exactRequest;
                var latestResponse = await latestRead;

                Assert.Equal(
                    2,
                    exactResponse.GetProperty("result").GetProperty("documentVersion").GetInt32());
                Assert.Contains(
                    latestResponse.GetProperty("result").EnumerateArray(),
                    symbol => symbol.GetProperty("name").GetString() == "Later");
                Assert.Equal(
                    2,
                    CountCompletedFiles(
                        admissionDirectory,
                        "-mutation-textDocument_didChange-none.completed"));
                Assert.DoesNotContain(
                    Directory.EnumerateFiles(admissionDirectory, "*.completed")
                        .Where(path => path.EndsWith(
                            "-mutation-textDocument_didChange-none.completed",
                            StringComparison.Ordinal)),
                    path => File.ReadLines(path).Contains("executionMilliseconds=0.000000"));

                await process.ShutdownAsync(5);
            }
            finally
            {
                File.WriteAllText(releaseFile, "release");
            }
        }
        finally
        {
            Directory.Delete(gateRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_sequences_shutdown_before_exit_while_an_earlier_request_is_blocked()
    {
        var gateRoot = Directory.CreateTempSubdirectory("vba-ls-scheduler-exit-").FullName;
        var admissionDirectory = Directory.CreateDirectory(
            Path.Combine(gateRoot, "admissions")).FullName;
        var startedFile = Path.Combine(gateRoot, "started");
        var releaseFile = Path.Combine(gateRoot, "release");
        try
        {
            await using var process = await LanguageServerProcessHarness.StartAsync(
                environment: new Dictionary<string, string>
                {
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_ID"] = "number:2",
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_STARTED_FILE"] = startedFile,
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_RELEASE_FILE"] = releaseFile,
                    ["VBA_TOOLS_INTERACTIVE_ADMISSION_DIRECTORY"] = admissionDirectory
                });
            try
            {
                await process.InitializeAsync();
                const string uri = "file:///C:/work/SchedulerExit.bas";
                await process.SendNotificationAsync(
                    "textDocument/didOpen",
                    CreateOpenDocument(
                        uri,
                        "Attribute VB_Name = \"SchedulerExit\"\n"
                        + "Public Sub Run()\n"
                        + "End Sub\n"));
                await process.WaitForDiagnosticsAsync(uri);
                var responseCheckpoint = process.TranscriptCheckpoint;
                var blockedRequest = process.SendRequestAsync(
                    2,
                    "textDocument/documentSymbol",
                    new
                    {
                        textDocument = new { uri }
                    });
                await WaitForFileCreatedAsync(startedFile, TimeSpan.FromSeconds(5));

                await process.SendRawMessageAsync(new
                {
                    jsonrpc = "2.0",
                    id = 3,
                    method = "shutdown",
                    @params = (object?)null
                });
                await process.SendRawMessageAsync(new
                {
                    jsonrpc = "2.0",
                    method = "exit",
                    @params = (object?)null
                });
                await WaitForMatchingFileCreatedAsync(
                    admissionDirectory,
                    fileName => fileName.EndsWith(
                        "-control-exit-none.admitted",
                        StringComparison.Ordinal),
                    TimeSpan.FromSeconds(5));

                File.WriteAllText(releaseFile, "release");
                var firstResponse = await blockedRequest;
                Assert.True(firstResponse.TryGetProperty("result", out _));
                var shutdownResponse = await process.WaitForMessageAsync(
                    responseCheckpoint,
                    message => message.TryGetProperty("id", out var id)
                        && id.ValueKind == System.Text.Json.JsonValueKind.Number
                        && id.GetInt32() == 3);
                Assert.Equal(
                    System.Text.Json.JsonValueKind.Null,
                    shutdownResponse.GetProperty("result").ValueKind);
                Assert.Equal(
                    0,
                    await process.WaitForProcessExitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(1, process.CountResponses(2, responseCheckpoint));
                Assert.Equal(1, process.CountResponses(3, responseCheckpoint));
            }
            finally
            {
                File.WriteAllText(releaseFile, "release");
            }
        }
        finally
        {
            Directory.Delete(gateRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_exits_with_failure_when_exit_arrives_before_shutdown()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        await process.SendRawMessageAsync(new
        {
            jsonrpc = "2.0",
            method = "exit",
            @params = (object?)null
        });

        Assert.Equal(
            1,
            await process.WaitForProcessExitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Server_aborts_blocked_work_when_exit_arrives_without_shutdown()
    {
        var gateRoot = Directory.CreateTempSubdirectory("vba-ls-scheduler-bad-exit-").FullName;
        var startedFile = Path.Combine(gateRoot, "started");
        var cancelledFile = Path.Combine(gateRoot, "cancelled");
        var releaseFile = Path.Combine(gateRoot, "release");
        try
        {
            await using var process = await LanguageServerProcessHarness.StartAsync(
                environment: new Dictionary<string, string>
                {
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_ID"] = "number:2",
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_STARTED_FILE"] = startedFile,
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_CANCELLED_FILE"] = cancelledFile,
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_RELEASE_FILE"] = releaseFile
                });
            try
            {
                await process.InitializeAsync();
                const string uri = "file:///C:/work/SchedulerBadExit.bas";
                await process.SendNotificationAsync(
                    "textDocument/didOpen",
                    CreateOpenDocument(
                        uri,
                        "Attribute VB_Name = \"SchedulerBadExit\"\n"
                        + "Public Sub Run()\n"
                        + "End Sub\n"));
                await process.WaitForDiagnosticsAsync(uri);
                var responseCheckpoint = process.TranscriptCheckpoint;
                var blockedRequest = process.SendRequestAsync(
                    2,
                    "textDocument/documentSymbol",
                    new
                    {
                        textDocument = new { uri }
                    });
                await WaitForFileCreatedAsync(startedFile, TimeSpan.FromSeconds(5));

                await process.SendRawMessageAsync(new
                {
                    jsonrpc = "2.0",
                    method = "exit",
                    @params = (object?)null
                });

                Assert.Equal(
                    1,
                    await process.WaitForProcessExitAsync(TimeSpan.FromSeconds(5)));
                await WaitForFileCreatedAsync(cancelledFile, TimeSpan.FromSeconds(5));
                await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await blockedRequest);
                Assert.Equal(0, process.CountResponses(2, responseCheckpoint));
            }
            finally
            {
                File.WriteAllText(releaseFile, "release");
            }
        }
        finally
        {
            Directory.Delete(gateRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_aborts_in_flight_work_and_exits_after_input_eof()
    {
        var gateRoot = Directory.CreateTempSubdirectory("vba-ls-scheduler-eof-").FullName;
        var startedFile = Path.Combine(gateRoot, "started");
        var cancelledFile = Path.Combine(gateRoot, "cancelled");
        var releaseFile = Path.Combine(gateRoot, "release");
        try
        {
            await using var process = await LanguageServerProcessHarness.StartAsync(
                environment: new Dictionary<string, string>
                {
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_ID"] = "number:2",
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_STARTED_FILE"] = startedFile,
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_CANCELLED_FILE"] = cancelledFile,
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_RELEASE_FILE"] = releaseFile
                });
            try
            {
                await process.InitializeAsync();
                const string uri = "file:///C:/work/SchedulerEof.bas";
                await process.SendNotificationAsync(
                    "textDocument/didOpen",
                    CreateOpenDocument(
                        uri,
                        "Attribute VB_Name = \"SchedulerEof\"\n"
                        + "Public Sub Run()\n"
                        + "End Sub\n"));
                await process.WaitForDiagnosticsAsync(uri);
                var responseCheckpoint = process.TranscriptCheckpoint;
                var blockedRequest = process.SendRequestAsync(
                    2,
                    "textDocument/documentSymbol",
                    new
                    {
                        textDocument = new { uri }
                    });
                await WaitForFileCreatedAsync(startedFile, TimeSpan.FromSeconds(5));

                process.CompleteInput();

                await WaitForFileCreatedAsync(cancelledFile, TimeSpan.FromSeconds(5));
                Assert.Equal(
                    0,
                    await process.WaitForProcessExitAsync(TimeSpan.FromSeconds(5)));
                await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await blockedRequest);
                Assert.Equal(0, process.CountResponses(2, responseCheckpoint));
            }
            finally
            {
                File.WriteAllText(releaseFile, "release");
            }
        }
        finally
        {
            Directory.Delete(gateRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Server_rejects_an_overlapping_duplicate_request_id_without_losing_its_owner()
    {
        var gateRoot = Directory.CreateTempSubdirectory("vba-ls-scheduler-duplicate-id-").FullName;
        var admissionDirectory = Directory.CreateDirectory(
            Path.Combine(gateRoot, "admissions")).FullName;
        var startedFile = Path.Combine(gateRoot, "started");
        var releaseFile = Path.Combine(gateRoot, "release");
        try
        {
            await using var process = await LanguageServerProcessHarness.StartAsync(
                environment: new Dictionary<string, string>
                {
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_ID"] = "number:2",
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_STARTED_FILE"] = startedFile,
                    ["VBA_TOOLS_INTERACTIVE_REQUEST_RELEASE_FILE"] = releaseFile,
                    ["VBA_TOOLS_INTERACTIVE_ADMISSION_DIRECTORY"] = admissionDirectory
                });
            try
            {
                await process.InitializeAsync();
                const string uri = "file:///C:/work/SchedulerDuplicateId.bas";
                await process.SendNotificationAsync(
                    "textDocument/didOpen",
                    CreateOpenDocument(
                        uri,
                        "Attribute VB_Name = \"SchedulerDuplicateId\"\n"
                        + "Public Sub OriginalOwner()\n"
                        + "End Sub\n"));
                await process.WaitForDiagnosticsAsync(uri);
                var responseCheckpoint = process.TranscriptCheckpoint;
                var originalRequest = process.SendRequestAsync(
                    2,
                    "textDocument/documentSymbol",
                    new
                    {
                        textDocument = new { uri }
                    });
                await WaitForFileCreatedAsync(startedFile, TimeSpan.FromSeconds(5));

                await process.SendRawMessageAsync(new
                {
                    jsonrpc = "2.0",
                    id = 2,
                    method = "test/duplicate",
                    @params = (object?)null
                });
                await WaitForMatchingFileCreatedAsync(
                    admissionDirectory,
                    fileName => fileName.EndsWith(
                        "-control-_duplicate-request_-none.admitted",
                        StringComparison.Ordinal),
                    TimeSpan.FromSeconds(5));

                File.WriteAllText(releaseFile, "release");
                var originalResponse = await originalRequest;
                Assert.Contains(
                    originalResponse.GetProperty("result").EnumerateArray(),
                    symbol => symbol.GetProperty("name").GetString() == "OriginalOwner");
                var duplicateResponse = await process.WaitForMessageAsync(
                    responseCheckpoint,
                    message => message.TryGetProperty("id", out var id)
                        && id.ValueKind == System.Text.Json.JsonValueKind.Number
                        && id.GetInt32() == 2
                        && message.TryGetProperty("error", out var error)
                        && error.GetProperty("code").GetInt32() == -32600);
                Assert.Equal(
                    "Duplicate request id",
                    duplicateResponse.GetProperty("error").GetProperty("message").GetString());
                Assert.Equal(2, process.CountResponses(2, responseCheckpoint));

                var reused = await process.SendRequestAsync(
                    2,
                    "textDocument/documentSymbol",
                    new
                    {
                        textDocument = new { uri }
                    });
                Assert.True(reused.TryGetProperty("result", out _));
                Assert.Equal(3, process.CountResponses(2, responseCheckpoint));
                await process.ShutdownAsync(3);
            }
            finally
            {
                File.WriteAllText(releaseFile, "release");
            }
        }
        finally
        {
            Directory.Delete(gateRoot, recursive: true);
        }
    }

    private static object CreateOpenDocument(string uri, string text)
        => new
        {
            textDocument = new
            {
                uri,
                languageId = "vba",
                version = 1,
                text
            }
        };

    private static object CreateChangedDocument(string uri, int version, string text)
        => new
        {
            textDocument = new
            {
                uri,
                version
            },
            contentChanges = new[]
            {
                new
                {
                    text
                }
            }
        };

    private static async Task WaitForFileCreatedAsync(string path, TimeSpan timeout)
    {
        if (File.Exists(path))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"The gate path has no directory: {path}");
        var fileName = Path.GetFileName(path);
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directory, fileName)
        {
            EnableRaisingEvents = true
        };
        FileSystemEventHandler signalCreated = (_, _) => created.TrySetResult();
        watcher.Created += signalCreated;
        watcher.Renamed += (_, _) => created.TrySetResult();
        if (File.Exists(path))
        {
            created.TrySetResult();
        }

        await created.Task.WaitAsync(timeout);
    }

    private static async Task<string> WaitForMatchingFileCreatedAsync(
        string directory,
        Func<string, bool> predicate,
        TimeSpan timeout)
    {
        var existing = Directory.EnumerateFiles(directory)
            .FirstOrDefault(path => predicate(Path.GetFileName(path)));
        if (existing is not null)
        {
            return existing;
        }

        var created = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directory)
        {
            EnableRaisingEvents = true
        };
        FileSystemEventHandler signalCreated = (_, args) =>
        {
            if (predicate(args.Name ?? ""))
            {
                created.TrySetResult(args.FullPath);
            }
        };
        RenamedEventHandler signalRenamed = (_, args) =>
        {
            if (predicate(args.Name ?? ""))
            {
                created.TrySetResult(args.FullPath);
            }
        };
        watcher.Created += signalCreated;
        watcher.Renamed += signalRenamed;
        existing = Directory.EnumerateFiles(directory)
            .FirstOrDefault(path => predicate(Path.GetFileName(path)));
        if (existing is not null)
        {
            created.TrySetResult(existing);
        }

        return await created.Task.WaitAsync(timeout);
    }

    private static int CountCompletedFiles(string directory, string suffix)
        => Directory.EnumerateFiles(directory, "*.completed")
            .Count(path => path.EndsWith(suffix, StringComparison.Ordinal));
}
