using System.Text.Json;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class WithEventsLanguageServerProcessTests
{
    [Fact]
    public async Task Empty_Sub_declaration_name_offers_only_the_WithEvents_prefix()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.WaitForDiagnosticsAsync(publisherUri);

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub "
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        await process.WaitForDiagnosticsAsync(workerUri);

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 3, character = "Private Sub ".Length }
            });
        var item = Assert.Single(completion.GetProperty("result").EnumerateArray());
        Assert.Equal("publisher_", item.GetProperty("label").GetString());
        Assert.Equal("WithEvents", item.GetProperty("detail").GetString());
        Assert.Equal(
            "publisher_",
            item.GetProperty("textEdit").GetProperty("newText").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Partial_WithEvents_prefix_filters_case_insensitively_and_replaces_only_the_fragment()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.WaitForDiagnosticsAsync(publisherUri);

        const string workerUri = "file:///C:/work/Worker.cls";
        const string declaration = "Private Sub PuB";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents controller As Publisher",
            "Private WithEvents publisher As Publisher",
            declaration
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        await process.WaitForDiagnosticsAsync(workerUri);

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 4, character = declaration.Length }
            });
        var item = Assert.Single(completion.GetProperty("result").EnumerateArray());
        Assert.Equal("publisher_", item.GetProperty("label").GetString());
        var textEdit = item.GetProperty("textEdit");
        Assert.Equal("publisher_", textEdit.GetProperty("newText").GetString());
        Assert.Equal(
            ("Private Sub ".Length, declaration.Length),
            (
                textEdit.GetProperty("range").GetProperty("start")
                    .GetProperty("character").GetInt32(),
                textEdit.GetProperty("range").GetProperty("end")
                    .GetProperty("character").GetInt32()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Continued_Sub_declaration_replaces_only_the_partial_prefix_line()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.WaitForDiagnosticsAsync(publisherUri);

        const string workerUri = "file:///C:/work/Worker.cls";
        const string fragmentLine = "    PuB";
        var workerText = string.Join("\r\n", [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub _",
            fragmentLine
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        await process.WaitForDiagnosticsAsync(workerUri);

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 4, character = fragmentLine.Length }
            });
        var item = Assert.Single(completion.GetProperty("result").EnumerateArray());
        Assert.Equal("publisher_", item.GetProperty("label").GetString());
        var textEdit = item.GetProperty("textEdit");
        Assert.Equal("publisher_", textEdit.GetProperty("newText").GetString());
        Assert.Equal(
            (4, 4, fragmentLine.Length),
            (
                textEdit.GetProperty("range").GetProperty("start")
                    .GetProperty("line").GetInt32(),
                textEdit.GetProperty("range").GetProperty("start")
                    .GetProperty("character").GetInt32(),
                textEdit.GetProperty("range").GetProperty("end")
                    .GetProperty("character").GetInt32()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Exact_WithEvents_prefix_offers_the_event_name_with_a_suffix_only_edit()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.WaitForDiagnosticsAsync(publisherUri);

        const string workerUri = "file:///C:/work/Worker.cls";
        const string declaration = "Private Sub publisher_";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            declaration
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        await process.WaitForDiagnosticsAsync(workerUri);

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 3, character = declaration.Length }
            });
        var item = Assert.Single(completion.GetProperty("result").EnumerateArray());
        Assert.Equal("publisher_Changed", item.GetProperty("label").GetString());
        Assert.Equal("Event", item.GetProperty("detail").GetString());
        Assert.Equal("Changed", item.GetProperty("filterText").GetString());
        Assert.Equal(
            "Changed",
            item.GetProperty("textEdit").GetProperty("newText").GetString());
        Assert.Equal(
            declaration.Length,
            item.GetProperty("textEdit").GetProperty("range")
                .GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(
            declaration.Length,
            item.GetProperty("textEdit").GetProperty("range")
                .GetProperty("end").GetProperty("character").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Partial_member_suffix_preserves_the_written_prefix_spelling()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.WaitForDiagnosticsAsync(publisherUri);

        const string workerUri = "file:///C:/work/Worker.cls";
        const string writtenPrefix = "PuBLiShEr_";
        const string declaration = "Private Sub PuBLiShEr_ch";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            declaration
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        await process.WaitForDiagnosticsAsync(workerUri);

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 3, character = declaration.Length }
            });
        var item = Assert.Single(completion.GetProperty("result").EnumerateArray());
        Assert.Equal(
            writtenPrefix + "Changed",
            item.GetProperty("label").GetString());
        var textEdit = item.GetProperty("textEdit");
        Assert.Equal("Changed", textEdit.GetProperty("newText").GetString());
        Assert.Equal(
            "Private Sub ".Length + writtenPrefix.Length,
            textEdit.GetProperty("range").GetProperty("start")
                .GetProperty("character").GetInt32());
        Assert.Equal(
            declaration.Length,
            textEdit.GetProperty("range").GetProperty("end")
                .GetProperty("character").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Conditional_prefix_casing_uses_the_ordinal_minimum_in_either_source_order()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string eventSourceUri = "file:///C:/work/EventSource.cls";
        const string eventSourceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "EventSource"
            Public Event Changed()
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(eventSourceUri, eventSourceText));
        await process.WaitForDiagnosticsAsync(eventSourceUri);

        const string workerUri = "file:///C:/work/Worker.cls";
        static string CreateWorkerText(bool uppercaseFirst)
        {
            var first = uppercaseFirst ? "Publisher" : "publisher";
            var second = uppercaseFirst ? "publisher" : "Publisher";
            return string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "#If VBA7 Then",
                $"Private WithEvents {first} As EventSource",
                "#Else",
                $"Private WithEvents {second} As EventSource",
                "#End If",
                "Private Sub "
            ]);
        }

        var workerText = CreateWorkerText(uppercaseFirst: false);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        await process.WaitForDiagnosticsAsync(workerUri);

        async Task AssertCanonicalPrefixAsync(int id)
        {
            var completion = await process.SendRequestAsync(
                id,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri = workerUri },
                    position = new { line = 7, character = "Private Sub ".Length }
                });
            var item = Assert.Single(
                completion.GetProperty("result").EnumerateArray());
            Assert.Equal("Publisher_", item.GetProperty("label").GetString());
            Assert.Equal(
                "WithEvents [#If]",
                item.GetProperty("detail").GetString());
        }

        await AssertCanonicalPrefixAsync(2);
        workerText = CreateWorkerText(uppercaseFirst: true);
        await process.SendNotificationAsync(
            "textDocument/didChange",
            new
            {
                textDocument = new { uri = workerUri, version = 2 },
                contentChanges = new[] { new { text = workerText } }
            });
        await process.WaitForDiagnosticsAsync(workerUri);
        await AssertCanonicalPrefixAsync(3);

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Unconditional_surviving_origin_removes_the_prefix_conditional_marker()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string eventSourceUri = "file:///C:/work/EventSource.cls";
        const string eventSourceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "EventSource"
            Public Event Changed()
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(eventSourceUri, eventSourceText));
        await process.WaitForDiagnosticsAsync(eventSourceUri);

        const string interfaceUri = "file:///C:/work/IPublisher.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IPublisher"
            Public Sub Run()
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents IPublisher As EventSource",
            "#If VBA7 Then",
            "Implements IPublisher",
            "#End If",
            "Private Sub "
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        await process.WaitForDiagnosticsAsync(workerUri);

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 6, character = "Private Sub ".Length }
            });
        var item = Assert.Single(completion.GetProperty("result").EnumerateArray());
        Assert.Equal("IPublisher_", item.GetProperty("label").GetString());
        Assert.Equal("Multiple Contracts", item.GetProperty("detail").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Dead_unconditional_origin_does_not_change_surviving_prefix_provenance()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string eventSourceUri = "file:///C:/work/EventSource.cls";
        const string eventSourceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "EventSource"
            Public Event Changed()
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(eventSourceUri, eventSourceText));
        await process.WaitForDiagnosticsAsync(eventSourceUri);

        const string interfaceUri = "file:///C:/work/IPublisher.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IPublisher"
            Public Sub Run()
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents IPublisher As EventSource",
            "#If VBA7 Then",
            "Implements IPublisher",
            "#End If",
            "Private Sub IPublisher_Changed()",
            "End Sub",
            "Private Sub "
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        await process.WaitForDiagnosticsAsync(workerUri);

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 8, character = "Private Sub ".Length }
            });
        var item = Assert.Single(completion.GetProperty("result").EnumerateArray());
        Assert.Equal("IPublisher_", item.GetProperty("label").GetString());
        Assert.Equal("Interface [#If]", item.GetProperty("detail").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Conditional_Event_marks_only_the_member_stage()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string eventSourceUri = "file:///C:/work/EventSource.cls";
        const string eventSourceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "EventSource"
            #If VBA7 Then
            Public Event Changed()
            #End If
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(eventSourceUri, eventSourceText));
        await process.WaitForDiagnosticsAsync(eventSourceUri);

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As EventSource",
            "Private Sub "
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        await process.WaitForDiagnosticsAsync(workerUri);

        var prefixCompletion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 3, character = "Private Sub ".Length }
            });
        var prefixItem = Assert.Single(
            prefixCompletion.GetProperty("result").EnumerateArray());
        Assert.Equal("WithEvents", prefixItem.GetProperty("detail").GetString());

        const string declaration = "Private Sub publisher_";
        workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As EventSource",
            declaration
        ]);
        await process.SendNotificationAsync(
            "textDocument/didChange",
            new
            {
                textDocument = new { uri = workerUri, version = 2 },
                contentChanges = new[] { new { text = workerText } }
            });
        await process.WaitForDiagnosticsAsync(workerUri);

        var memberCompletion = await process.SendRequestAsync(
            3,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 3, character = declaration.Length }
            });
        var memberItem = Assert.Single(
            memberCompletion.GetProperty("result").EnumerateArray());
        Assert.Equal(
            "Event [#If]",
            memberItem.GetProperty("detail").GetString());

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Recovered_Event_signature_keeps_every_name_eligible_authoring_candidate()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string eventSourceUri = "file:///C:/work/EventSource.cls";
        const string eventSourceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "EventSource"
            Public Event Changed()
            Public Event Broken(Optional ByVal value As Long)
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(eventSourceUri, eventSourceText));
        await process.WaitForDiagnosticsAsync(eventSourceUri);

        const string workerUri = "file:///C:/work/Worker.cls";
        const string declaration = "Private Sub publisher_";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As EventSource",
            declaration
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        await process.WaitForDiagnosticsAsync(workerUri);

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 3, character = declaration.Length }
            });
        var items = completion.GetProperty("result").EnumerateArray().ToArray();
        Assert.Equal(
            ["publisher_Broken", "publisher_Changed"],
            items.Select(item => item.GetProperty("label").GetString()));
        var recovered = Assert.Single(items, item =>
            item.GetProperty("label").GetString() == "publisher_Broken");
        Assert.False(recovered.TryGetProperty("documentation", out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Completion_excludes_the_physical_handler_declaration_being_edited()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.WaitForDiagnosticsAsync(publisherUri);

        const string workerUri = "file:///C:/work/Worker.cls";
        const string declaration = "Private Sub publisher_Changed";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            declaration
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        await process.WaitForDiagnosticsAsync(workerUri);

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 3, character = declaration.Length }
            });
        var item = Assert.Single(completion.GetProperty("result").EnumerateArray());
        Assert.Equal("publisher_Changed", item.GetProperty("label").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Indeterminate_conditional_collision_keeps_advisory_member_completion()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.WaitForDiagnosticsAsync(publisherUri);

        const string workerUri = "file:///C:/work/Worker.cls";
        const string declaration = "Private Sub publisher_";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "#If VBA7 Then",
            "Private Sub publisher_Changed()",
            "End Sub",
            declaration
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        await process.WaitForDiagnosticsAsync(workerUri);

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 6, character = declaration.Length }
            });
        var item = Assert.Single(completion.GetProperty("result").EnumerateArray());
        Assert.Equal("publisher_Changed", item.GetProperty("label").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Unconditional_peer_suppresses_an_indeterminate_prospective_declaration()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.WaitForDiagnosticsAsync(publisherUri);

        const string workerUri = "file:///C:/work/Worker.cls";
        const string declaration = "Private Sub publisher_";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Changed()",
            "End Sub",
            "#If VBA7 Then",
            declaration
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        await process.WaitForDiagnosticsAsync(workerUri);

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 6, character = declaration.Length }
            });
        Assert.Empty(completion.GetProperty("result").EnumerateArray());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Coalesced_Event_and_interface_name_retains_both_Signature_Help_origins()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string eventSourceUri = "file:///C:/work/EventSource.cls";
        const string eventSourceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "EventSource"
            Public Event Changed(ByVal eventValue As String)
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(eventSourceUri, eventSourceText));
        await process.WaitForDiagnosticsAsync(eventSourceUri);

        const string interfaceUri = "file:///C:/work/IPublisher.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IPublisher"
            Public Sub Changed(ByVal interfaceValue As Long)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Implements IPublisher",
            "Private WithEvents IPublisher As EventSource",
            "Private Sub IPublisher_Changed(ByVal argument As Variant)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        await process.WaitForDiagnosticsAsync(workerUri);

        var signatureHelp = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            workerUri,
            workerText,
            "argument");
        Assert.Equal(
            [
                "Event Changed(eventValue As String)",
                "Sub IPublisher_Changed(interfaceValue As Long)"
            ],
            signatureHelp
                .GetProperty("result")
                .GetProperty("signatures")
                .EnumerateArray()
                .Select(signature => signature.GetProperty("label").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Cross_domain_completion_re_resolves_all_origins_without_prefix_session_state()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string eventSourceUri = "file:///C:/work/EventSource.cls";
        const string eventSourceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "EventSource"
            Public Event Changed(ByVal eventValue As String)
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(eventSourceUri, eventSourceText));
        await process.WaitForDiagnosticsAsync(eventSourceUri);

        const string interfaceUri = "file:///C:/work/IPublisher.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IPublisher"
            Public Sub Changed(ByVal interfaceValue As Long)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Implements IPublisher",
            "Private WithEvents IPublisher As EventSource",
            "Private Sub "
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        await process.WaitForDiagnosticsAsync(workerUri);

        var prefixCompletion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 4, character = "Private Sub ".Length },
                context = new { triggerKind = 2, triggerCharacter = " " }
            });
        var prefixItem = Assert.Single(
            prefixCompletion.GetProperty("result").EnumerateArray());
        Assert.Equal("IPublisher_", prefixItem.GetProperty("label").GetString());
        Assert.Equal(
            "Multiple Contracts",
            prefixItem.GetProperty("detail").GetString());
        Assert.False(prefixItem.TryGetProperty("documentation", out _));

        const string declaration = "Private Sub IPublisher_";
        workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "' Implements relationship removed after prefix completion",
            "Private WithEvents IPublisher As EventSource",
            declaration
        ]);
        await process.SendNotificationAsync(
            "textDocument/didChange",
            new
            {
                textDocument = new { uri = workerUri, version = 2 },
                contentChanges = new[] { new { text = workerText } }
            });
        await process.WaitForDiagnosticsAsync(workerUri);

        var explicitCompletion = await process.SendRequestAsync(
            3,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 4, character = declaration.Length }
            });
        var retriggerCompletion = await process.SendRequestAsync(
            4,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 4, character = declaration.Length },
                context = new { triggerKind = 3 }
            });
        var underscoreCompletion = await process.SendRequestAsync(
            5,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 4, character = declaration.Length },
                context = new { triggerKind = 2, triggerCharacter = "_" }
            });
        Assert.Equal(
            explicitCompletion.GetProperty("result").GetRawText(),
            retriggerCompletion.GetProperty("result").GetRawText());
        Assert.Equal(
            explicitCompletion.GetProperty("result").GetRawText(),
            underscoreCompletion.GetProperty("result").GetRawText());
        var memberItem = Assert.Single(
            explicitCompletion.GetProperty("result").EnumerateArray());
        Assert.Equal(
            "IPublisher_Changed",
            memberItem.GetProperty("label").GetString());
        Assert.Equal(
            "Event",
            memberItem.GetProperty("detail").GetString());
        Assert.Equal(
            "Changed",
            memberItem.GetProperty("textEdit").GetProperty("newText").GetString());
        var documentation = memberItem
            .GetProperty("documentation")
            .GetProperty("value")
            .GetString();
        Assert.Contains(
            "Event Changed(eventValue As String)",
            documentation,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Sub IPublisher_Changed(interfaceValue As Long)",
            documentation,
            StringComparison.Ordinal);

        await process.ShutdownAsync(6);
    }

    [Fact]
    public async Task TypeLib_WithEvents_completion_uses_only_the_Event_authoring_surface()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-completion-typelib-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-completion-typelib-cache-").FullName;
        try
        {
            const string referenceName = "Generated Library";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            var changedEvent = new TypeLibCatalogMember(
                "Changed",
                VbaSourceDefinitionKind.Procedure,
                "Changed Event.",
                new VbaCallableSignature(
                    "Sub Changed(ByVal value As Long)",
                    [
                        new VbaCallableParameter(
                            "value",
                            TypeReference: new VbaTypeReference("Long"),
                            IsByRef: false)
                    ],
                    CallableKind: VbaCallableKind.Sub),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: 0));
            var hiddenEvent = new TypeLibCatalogMember(
                "HiddenChanged",
                VbaSourceDefinitionKind.Procedure,
                "Hidden Event.",
                new VbaCallableSignature(
                    "Sub HiddenChanged()",
                    [],
                    CallableKind: VbaCallableKind.Sub),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 2,
                    FunctionFlags: 0x40));
            var restrictedEvent = new TypeLibCatalogMember(
                "RestrictedChanged",
                VbaSourceDefinitionKind.Procedure,
                "Restricted Event.",
                new VbaCallableSignature(
                    "Sub RestrictedChanged()",
                    [],
                    CallableKind: VbaCallableKind.Sub),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 3,
                    FunctionFlags: 0x1));
            var catalog = TypeLibReferenceCatalogBuilder.Build(
                referenceName,
                new TypeLibCatalogMetadata(
                    "Generated",
                    [
                        new TypeLibCatalogType(
                            "Publisher",
                            VbaSourceDefinitionKind.Class,
                            Documentation: null,
                            Members: [],
                            IsCreatable: true,
                            Metadata: new TypeLibCatalogTypeMetadata(
                                TypeLibCatalogRawTypeKind.CoClass,
                                TypeFlags: 0,
                                ImplementedInterfaces:
                                [
                                    new TypeLibCatalogImplementedInterface(
                                        "_PublisherEvents",
                                        TypeFlags: 0,
                                        ImplementationFlags: 0x1 | 0x2,
                                        CallableMembers:
                                            [changedEvent, hiddenEvent, restrictedEvent],
                                        RawTypeKind:
                                            TypeLibCatalogRawTypeKind.Dispatch)
                                ]))
                    ]));
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    catalog));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();

            var sourcePath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.cls");
            var uri = new Uri(sourcePath).AbsoluteUri;
            const string declaration = "Private Sub publisher_";
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Private WithEvents publisher As Publisher",
                declaration
            ]);
            File.WriteAllText(sourcePath, text);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            await process.WaitForDiagnosticsAsync(uri);

            var completion = await process.SendRequestAsync(
                2,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri },
                    position = new { line = 2, character = declaration.Length }
                });
            var item = Assert.Single(
                completion.GetProperty("result").EnumerateArray());
            Assert.Equal("publisher_Changed", item.GetProperty("label").GetString());
            Assert.Equal("Event", item.GetProperty("detail").GetString());
            Assert.Contains(
                "Event Changed(value As Long)",
                item.GetProperty("documentation").GetProperty("value").GetString(),
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
    public async Task Partial_TypeLib_Event_surface_offers_no_handler_authoring_completion()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-partial-typelib-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-partial-typelib-cache-").FullName;
        try
        {
            const string referenceName = "Generated Library";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            var knownEvent = new TypeLibCatalogMember(
                "Changed",
                VbaSourceDefinitionKind.Procedure,
                Documentation: null,
                new VbaCallableSignature(
                    "Sub Changed()",
                    [],
                    CallableKind: VbaCallableKind.Sub),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: 0));
            var incompleteEvent = new TypeLibCatalogMember(
                "Broken",
                VbaSourceDefinitionKind.Procedure,
                Documentation: null,
                new VbaCallableSignature(
                    "Sub Broken(value)",
                    [new VbaCallableParameter("value")],
                    CallableKind: VbaCallableKind.Sub),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 2,
                    FunctionFlags: 0));
            var catalog = TypeLibReferenceCatalogBuilder.Build(
                referenceName,
                new TypeLibCatalogMetadata(
                    "Generated",
                    [
                        new TypeLibCatalogType(
                            "Publisher",
                            VbaSourceDefinitionKind.Class,
                            Documentation: null,
                            Members: [],
                            IsCreatable: true,
                            Metadata: new TypeLibCatalogTypeMetadata(
                                TypeLibCatalogRawTypeKind.CoClass,
                                TypeFlags: 0,
                                ImplementedInterfaces:
                                [
                                    new TypeLibCatalogImplementedInterface(
                                        "_PublisherEvents",
                                        TypeFlags: 0,
                                        ImplementationFlags: 0x1 | 0x2,
                                        CallableMembers:
                                            [knownEvent, incompleteEvent],
                                        RawTypeKind:
                                            TypeLibCatalogRawTypeKind.Dispatch)
                                ]))
                    ]));
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    catalog));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();

            var sourcePath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.cls");
            var uri = new Uri(sourcePath).AbsoluteUri;
            const string declaration = "Private Sub publisher_";
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Private WithEvents publisher As Publisher",
                declaration
            ]);
            File.WriteAllText(sourcePath, text);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            await process.WaitForDiagnosticsAsync(uri);

            var completion = await process.SendRequestAsync(
                2,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri },
                    position = new { line = 2, character = declaration.Length }
                });
            Assert.Empty(completion.GetProperty("result").EnumerateArray());

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(0x40)]
    [InlineData(0x1)]
    public async Task Non_authoring_TypeLib_Event_and_interface_member_both_reach_Signature_Help(
        int functionFlags)
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-hidden-cross-contract-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-hidden-cross-contract-cache-").FullName;
        try
        {
            const string referenceName = "Generated Library";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            var hiddenEvent = new TypeLibCatalogMember(
                "Changed",
                VbaSourceDefinitionKind.Event,
                Documentation: null,
                new VbaCallableSignature(
                    "Event Changed(eventValue As String)",
                    [
                        new VbaCallableParameter(
                            "eventValue",
                            TypeReference: new VbaTypeReference("String"),
                            IsByRef: false)
                    ],
                    CallableKind: VbaCallableKind.Event),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: functionFlags));
            var catalog = TypeLibReferenceCatalogBuilder.Build(
                referenceName,
                new TypeLibCatalogMetadata(
                    "Generated",
                    [
                        new TypeLibCatalogType(
                            "Publisher",
                            VbaSourceDefinitionKind.Class,
                            Documentation: null,
                            Members: [hiddenEvent],
                            IsCreatable: true,
                            Metadata: new TypeLibCatalogTypeMetadata(
                                TypeLibCatalogRawTypeKind.CoClass,
                                TypeFlags: 0,
                                ImplementedInterfaces:
                                [
                                    new TypeLibCatalogImplementedInterface(
                                        "_PublisherEvents",
                                        TypeFlags: 0,
                                        ImplementationFlags: 0x1 | 0x2,
                                        CallableMembers: [hiddenEvent],
                                        RawTypeKind:
                                            TypeLibCatalogRawTypeKind.Dispatch)
                                ]))
                    ]));
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    catalog));

            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var interfacePath = Path.Combine(sourceRoot, "IPublisher.cls");
            var interfaceUri = new Uri(interfacePath).AbsoluteUri;
            const string interfaceText = """
                VERSION 1.0 CLASS
                Attribute VB_Name = "IPublisher"
                Public Sub Changed(ByVal interfaceValue As Long)
                End Sub
                """;
            File.WriteAllText(interfacePath, interfaceText);
            var workerPath = Path.Combine(sourceRoot, "Worker.cls");
            var workerUri = new Uri(workerPath).AbsoluteUri;
            var workerText = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Implements IPublisher",
                "Private WithEvents IPublisher As Publisher",
                "Private Sub IPublisher_Changed(ByVal argument As Variant)",
                "End Sub"
            ]);
            File.WriteAllText(workerPath, workerText);

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(interfaceUri, interfaceText));
            await process.WaitForDiagnosticsAsync(interfaceUri);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(workerUri, workerText));
            await process.WaitForDiagnosticsAsync(workerUri);
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri = workerUri, version = 2 },
                    contentChanges = new[] { new { text = workerText } }
                });
            var workerDiagnostics = await process.WaitForDiagnosticsAsync(workerUri);
            var diagnostics = workerDiagnostics
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray()
                .ToArray();
            Assert.DoesNotContain(
                diagnostics,
                diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.incompatibleEventHandlerSignature");
            Assert.Contains(
                diagnostics,
                diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.incompatibleInterfaceMemberSignature");

            var signatureHelp = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/signatureHelp",
                workerUri,
                workerText,
                "argument");
            Assert.Equal(
                [
                    "Event Changed(eventValue As String)",
                    "Sub IPublisher_Changed(interfaceValue As Long)"
                ],
                signatureHelp
                    .GetProperty("result")
                    .GetProperty("signatures")
                    .EnumerateArray()
                    .Select(signature => signature.GetProperty("label").GetString()));

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Form_host_projection_supplies_external_WithEvents_completion()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-completion-host-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var sourceTemplate = Path.Combine(sourceRoot, "Book1.xlsm");

            var publisherPath = Path.Combine(sourceRoot, "Publisher.frm");
            var publisherUri = new Uri(publisherPath).AbsoluteUri;
            var publisherText = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Publisher",
                "End",
                "Attribute VB_Name = \"Publisher\""
            ]);
            File.WriteAllText(publisherPath, publisherText);

            var workerPath = Path.Combine(sourceRoot, "Worker.cls");
            var workerUri = new Uri(workerPath).AbsoluteUri;
            const string declaration = "Private Sub publisher_";
            var workerText = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Private WithEvents publisher As Publisher",
                declaration
            ]);
            File.WriteAllText(workerPath, workerText);

            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(publisherUri, publisherText));
            await process.WaitForDiagnosticsAsync(publisherUri);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(workerUri, workerText));
            await process.WaitForDiagnosticsAsync(workerUri);
            await process.SendNotificationAsync(
                "vba/hostClassProjectionSnapshot",
                CreateHostProjectionSnapshot(
                    projectRoot,
                    sourceTemplate,
                    "Publisher",
                    "Change"));
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri = workerUri, version = 2 },
                    contentChanges = new[] { new { text = workerText } }
                });
            await process.WaitForDiagnosticsAsync(workerUri);

            var completion = await process.SendRequestAsync(
                2,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri = workerUri },
                    position = new { line = 3, character = declaration.Length }
                });
            var item = Assert.Single(
                completion.GetProperty("result").EnumerateArray());
            Assert.Equal("publisher_Change", item.GetProperty("label").GetString());
            Assert.Equal("Event", item.GetProperty("detail").GetString());

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Conditional_host_projected_WithEvents_binding_marks_completion_and_Signature_Help()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-completion-conditional-host-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var sourceTemplate = Path.Combine(sourceRoot, "Book1.xlsm");

            var publisherPath = Path.Combine(sourceRoot, "Publisher.frm");
            var publisherUri = new Uri(publisherPath).AbsoluteUri;
            var publisherText = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Publisher",
                "End",
                "Attribute VB_Name = \"Publisher\""
            ]);
            File.WriteAllText(publisherPath, publisherText);

            var workerPath = Path.Combine(sourceRoot, "Worker.cls");
            var workerUri = new Uri(workerPath).AbsoluteUri;
            const string declaration = "Private Sub publisher_";
            var workerText = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "#If VBA7 Then",
                "Private WithEvents publisher As Publisher",
                "#End If",
                declaration
            ]);
            File.WriteAllText(workerPath, workerText);

            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(publisherUri, publisherText));
            await process.WaitForDiagnosticsAsync(publisherUri);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(workerUri, workerText));
            await process.WaitForDiagnosticsAsync(workerUri);
            await process.SendNotificationAsync(
                "vba/hostClassProjectionSnapshot",
                CreateHostProjectionSnapshot(
                    projectRoot,
                    sourceTemplate,
                    "Publisher",
                    "Change"));
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri = workerUri, version = 2 },
                    contentChanges = new[] { new { text = workerText } }
                });
            await process.WaitForDiagnosticsAsync(workerUri);

            var completion = await process.SendRequestAsync(
                2,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri = workerUri },
                    position = new { line = 5, character = declaration.Length }
                });
            var item = Assert.Single(
                completion.GetProperty("result").EnumerateArray());
            Assert.Equal("publisher_Change", item.GetProperty("label").GetString());
            Assert.Equal("Event [#If]", item.GetProperty("detail").GetString());
            Assert.Contains(
                "Event Change() [#If]",
                item.GetProperty("documentation").GetProperty("value").GetString(),
                StringComparison.Ordinal);

            workerText = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "#If VBA7 Then",
                "Private WithEvents publisher As Publisher",
                "#End If",
                "Private Sub publisher_Change()",
                "End Sub"
            ]);
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri = workerUri, version = 3 },
                    contentChanges = new[] { new { text = workerText } }
                });
            await process.WaitForDiagnosticsAsync(workerUri);

            var signatureHelp = await SendPositionRequestAsync(
                process,
                3,
                "textDocument/signatureHelp",
                workerUri,
                workerText,
                "publisher_Change(",
                "publisher_Change(".Length);
            var signature = Assert.Single(
                signatureHelp.GetProperty("result")
                    .GetProperty("signatures").EnumerateArray());
            Assert.Equal(
                "Event Change() [#If]",
                signature.GetProperty("label").GetString());

            await process.ShutdownAsync(4);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Conditional_source_Event_retains_the_projected_host_shadow_alternative()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-completion-host-shadow-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var sourceTemplate = Path.Combine(sourceRoot, "Book1.xlsm");

            var publisherPath = Path.Combine(sourceRoot, "Publisher.frm");
            var publisherUri = new Uri(publisherPath).AbsoluteUri;
            var publisherText = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Publisher",
                "End",
                "Attribute VB_Name = \"Publisher\"",
                "#If VBA7 Then",
                "Public Event Change(ByVal sourceValue As Long)",
                "#End If"
            ]);
            File.WriteAllText(publisherPath, publisherText);

            var workerPath = Path.Combine(sourceRoot, "Worker.cls");
            var workerUri = new Uri(workerPath).AbsoluteUri;
            const string declaration = "Private Sub publisher_";
            var workerText = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Private WithEvents publisher As Publisher",
                declaration
            ]);
            File.WriteAllText(workerPath, workerText);

            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(publisherUri, publisherText));
            await process.WaitForDiagnosticsAsync(publisherUri);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(workerUri, workerText));
            await process.WaitForDiagnosticsAsync(workerUri);
            await process.SendNotificationAsync(
                "vba/hostClassProjectionSnapshot",
                CreateHostProjectionSnapshot(
                    projectRoot,
                    sourceTemplate,
                    "Publisher",
                    "Change"));
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri = workerUri, version = 2 },
                    contentChanges = new[] { new { text = workerText } }
                });
            await process.WaitForDiagnosticsAsync(workerUri);

            var completion = await process.SendRequestAsync(
                2,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri = workerUri },
                    position = new { line = 3, character = declaration.Length }
                });
            var item = Assert.Single(
                completion.GetProperty("result").EnumerateArray());
            Assert.Equal("publisher_Change", item.GetProperty("label").GetString());
            Assert.Equal("Event [#If]", item.GetProperty("detail").GetString());
            var documentation = item.GetProperty("documentation")
                .GetProperty("value")
                .GetString();
            Assert.Contains(
                "Event Change(sourceValue As Long) [#If]",
                documentation,
                StringComparison.Ordinal);
            Assert.Contains(
                "Event Change() [#If]",
                documentation,
                StringComparison.Ordinal);

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Standard_module_WithEvents_declarator_reports_placement_diagnostic()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string uri = "file:///C:/work/Worker.bas";
        const string text = "Public WithEvents publisher As Publisher";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            new
            {
                textDocument = new
                {
                    uri,
                    languageId = "vba",
                    version = 1,
                    text
                }
            });

        var notification = await process.WaitForDiagnosticsAsync(uri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.withEventsDeclarationNotAllowedHere");
        Assert.Equal(
            "WithEvents variables are allowed only at module level in a class module.",
            diagnostic.GetProperty("message").GetString());
        Assert.Equal(1, diagnostic.GetProperty("severity").GetInt32());
        Assert.Equal(0, diagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(7, diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(0, diagnostic.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(17, diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Procedure_local_WithEvents_recovers_the_variable_without_handler_projection()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        var publisherText = string.Join('\n', [
            "Attribute VB_Name = \"Publisher\"",
            "Public Event Changed()"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Configure()",
            "    Dim WithEvents publisher As Publisher",
            "    Set publisher = Nothing",
            "End Sub",
            "Private Sub publisher_Changed()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        await process.WaitForDiagnosticsAsync(workerUri);

        var variableDefinition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "Set publisher",
            "Set ".Length);
        var variableLocation = variableDefinition.GetProperty("result");
        Assert.Equal(workerUri, variableLocation.GetProperty("uri").GetString());
        Assert.Equal(
            2,
            variableLocation.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());

        var procedureDefinition = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher_Changed");
        var procedureLocation = procedureDefinition.GetProperty("result");
        Assert.Equal(workerUri, procedureLocation.GetProperty("uri").GetString());
        Assert.Equal(
            5,
            procedureLocation.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());

        await process.ShutdownAsync(4);
    }

    [Theory]
    [InlineData("Public")]
    [InlineData("Private")]
    [InlineData("Friend")]
    [InlineData("Global")]
    [InlineData("Private Static")]
    public async Task Procedure_local_visibility_WithEvents_recovers_the_variable(
        string introducer)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Configure()",
            $"    {introducer} WithEvents publisher As Publisher",
            "    Set publisher = Nothing",
            "End Sub",
            "Private Sub publisher_Changed()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        var placement = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.withEventsDeclarationNotAllowedHere");
        var withEventsStart = 4 + introducer.Length + 1;
        Assert.Equal(
            (2, withEventsStart, 2, withEventsStart + "WithEvents".Length),
            (
                placement.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
                placement.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                placement.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32(),
                placement.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));

        var variableDefinition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "Set publisher",
            "Set ".Length);
        Assert.Equal(
            2,
            variableDefinition
                .GetProperty("result")
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());
        var procedureDefinition = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher_Changed");
        Assert.Equal(
            5,
            procedureDefinition
                .GetProperty("result")
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Procedure_local_WithEvents_without_an_introducer_recovers_the_variable()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed()"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Configure()",
            "    WithEvents publisher As Publisher",
            "    Set publisher = Nothing",
            "End Sub",
            "Private Function publisher_Changed() As Boolean",
            "End Function"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        var placement = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.withEventsDeclarationNotAllowedHere");
        Assert.Equal(
            (2, 4, 2, 14),
            (
                placement.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
                placement.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                placement.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32(),
                placement.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.eventHandlerMustBeSub");

        var variableDefinition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "Set publisher",
            "Set ".Length);
        var variableLocation = variableDefinition.GetProperty("result");
        Assert.Equal(workerUri, variableLocation.GetProperty("uri").GetString());
        Assert.Equal(
            (2, 15, 2, 24),
            (
                variableLocation.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
                variableLocation.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                variableLocation.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32(),
                variableLocation.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task WithEvents_modifier_belongs_only_to_the_declarator_that_writes_it()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed()"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher, other As Publisher",
            "Private Sub publisher_Changed()",
            "End Sub",
            "Private Sub other_Changed()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        await process.WaitForDiagnosticsAsync(workerUri);

        var ordinaryProcedure = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "other_Changed");
        var ordinaryLocation = ordinaryProcedure.GetProperty("result");
        Assert.Equal(workerUri, ordinaryLocation.GetProperty("uri").GetString());
        Assert.Equal(
            4,
            ordinaryLocation.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());

        var ordinaryHover = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/hover",
            workerUri,
            workerText,
            "other As");
        Assert.Equal(
            "```vba\nother As Publisher\n```",
            ordinaryHover
                .GetProperty("result")
                .GetProperty("contents")
                .GetProperty("value")
                .GetString());

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Class_module_WithEvents_requires_Public_Private_or_Dim_introducer()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string uri = "file:///C:/work/Worker.cls";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Friend WithEvents first As Publisher",
            "Global WithEvents second As Publisher"
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
                == "syntax.withEventsDeclarationNotAllowedHere")
            .OrderBy(candidate => candidate
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32())
            .ToArray();
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic =>
        {
            Assert.Equal(
                7,
                diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
            Assert.Equal(
                17,
                diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());
        });

        await process.ShutdownAsync(2);
    }

    [Theory]
    [InlineData("WithEvents publisher As Publisher", 0)]
    [InlineData("Static WithEvents publisher As Publisher", 7)]
    [InlineData("Private Static WithEvents publisher As Publisher", 15)]
    public async Task Invalid_module_introducer_recovers_each_WithEvents_declarator(
        string declaration,
        int withEventsStart)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed()"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            declaration,
            "Private Sub publisher_Changed()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.withEventsDeclarationNotAllowedHere");
        Assert.Equal(
            (1, withEventsStart, 1, withEventsStart + "WithEvents".Length),
            (
                diagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));

        var variableDefinition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher As");
        Assert.Equal(
            1,
            variableDefinition
                .GetProperty("result")
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());

        var handlerDefinition = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher_Changed",
            "publisher_".Length);
        Assert.Equal(
            2,
            handlerDefinition
                .GetProperty("result")
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Invalid_module_introducer_recovers_a_later_WithEvents_declarator()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed()"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        const string declaration =
            "Static ordinary As Publisher, WithEvents publisher As Publisher";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            declaration,
            "Public Sub Run()",
            "    Set publisher = Nothing",
            "End Sub",
            "Private Sub publisher_Changed()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.withEventsDeclarationNotAllowedHere");
        var withEventsStart = declaration.IndexOf("WithEvents", StringComparison.Ordinal);
        Assert.Equal(
            (1, withEventsStart, 1, withEventsStart + "WithEvents".Length),
            (
                diagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));

        var variableDefinition = await SendPositionRequestAsync(
            process,
            4,
            "textDocument/definition",
            workerUri,
            workerText,
            "Set publisher",
            "Set ".Length);
        Assert.Equal(
            1,
            variableDefinition
                .GetProperty("result")
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());

        var handlerDefinition = await SendPositionRequestAsync(
            process,
            5,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher_Changed",
            "publisher_".Length);
        Assert.Equal(
            5,
            handlerDefinition
                .GetProperty("result")
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());

        await process.ShutdownAsync(6);
    }

    [Fact]
    public async Task WithEvents_array_reports_its_designator_and_does_not_bind_a_handler()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed()"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher(1 To 3) As Publisher",
            "Private Sub publisher_Changed()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.withEventsArrayNotAllowed");
        Assert.Equal(
            "WithEvents variables cannot be arrays.",
            diagnostic.GetProperty("message").GetString());
        Assert.Equal(
            28,
            diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(
            36,
            diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());

        var procedureDefinition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher_Changed");
        Assert.Equal(
            workerUri,
            procedureDefinition.GetProperty("result").GetProperty("uri").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task WithEvents_array_bound_New_is_not_misclassified_as_As_New()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher(New) As Publisher",
            "Private Sub publisher_Changed()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();
        Assert.Contains(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.withEventsArrayNotAllowed");
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.withEventsNewNotAllowed");

        var procedureDefinition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher_Changed");
        Assert.Equal(
            2,
            procedureDefinition
                .GetProperty("result")
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task WithEvents_array_bound_As_is_not_misclassified_as_the_type_clause()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher(As Publisher)",
            "Private Sub publisher_Changed()",
            "End Sub",
            "Private Sub UsePublisher()",
            "    Set publisher = Nothing",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();
        var arrayDiagnostic = Assert.Single(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.withEventsArrayNotAllowed");
        Assert.Equal(
            (1, 28, 1, 42),
            (
                arrayDiagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
                arrayDiagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                arrayDiagnostic.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32(),
                arrayDiagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));
        var typeDiagnostic = Assert.Single(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.withEventsTypeRequired");
        Assert.Equal(
            (1, 19, 1, 28),
            (
                typeDiagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
                typeDiagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                typeDiagnostic.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32(),
                typeDiagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));

        var variableDefinition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "Set publisher",
            "Set ".Length);
        Assert.Equal(
            1,
            variableDefinition
                .GetProperty("result")
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());

        var handlerDefinition = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher_Changed",
            "publisher_".Length);
        Assert.Equal(
            2,
            handlerDefinition
                .GetProperty("result")
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Line_continued_WithEvents_type_clause_binds_its_Event()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed()"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher _",
            "    As Publisher",
            "Private Sub publisher_Changed()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString() is { } code
                && code.StartsWith("syntax.withEvents", StringComparison.Ordinal));

        var definition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher_Changed",
            "publisher_".Length);
        var location = definition.GetProperty("result");
        Assert.Equal(publisherUri, location.GetProperty("uri").GetString());
        Assert.Equal(
            1,
            location.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Line_continued_later_WithEvents_declarator_binds_its_Event()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed()"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private ordinary As Long, _",
            "    WithEvents publisher As Publisher",
            "Private Sub publisher_Changed()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString() is { } code
                && code.StartsWith("syntax.withEvents", StringComparison.Ordinal));

        var definition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher_Changed",
            "publisher_".Length);
        var location = definition.GetProperty("result");
        Assert.Equal(publisherUri, location.GetProperty("uri").GetString());
        Assert.Equal(
            1,
            location.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Continuation_tail_is_not_admitted_as_a_WithEvents_declaration()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed()"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Bogus _",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Changed()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        await process.WaitForDiagnosticsAsync(workerUri);

        var definition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher_Changed",
            "publisher_".Length);
        var location = definition.GetProperty("result");
        Assert.Equal(workerUri, location.GetProperty("uri").GetString());
        Assert.Equal(
            3,
            location.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task WithEvents_unclosed_array_designator_recovers_without_event_binding()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed()"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher( As Publisher",
            "Private Sub publisher_Changed()",
            "End Sub",
            "Private Sub UsePublisher()",
            "    Set publisher = Nothing",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.withEventsArrayNotAllowed");
        Assert.Equal(
            "WithEvents variables cannot be arrays.",
            diagnostic.GetProperty("message").GetString());
        Assert.Equal(
            28,
            diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(
            29,
            diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());

        var variableDefinition = await SendPositionRequestAsync(
            process,
            5,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher");
        Assert.Equal(
            1,
            variableDefinition
                .GetProperty("result")
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());

        var procedureDefinition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher_Changed");
        Assert.Equal(
            2,
            procedureDefinition
                .GetProperty("result")
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task WithEvents_malformed_declarator_recovers_without_event_binding()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed()"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher Bogus As Publisher",
            "Private Sub publisher_Changed()",
            "End Sub",
            "Private Sub UsePublisher()",
            "    Set publisher = Nothing",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        await process.WaitForDiagnosticsAsync(workerUri);

        var variableDefinition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher = Nothing");
        Assert.Equal(
            1,
            variableDefinition
                .GetProperty("result")
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());

        var procedureDefinition = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher_Changed");
        Assert.Equal(
            2,
            procedureDefinition
                .GetProperty("result")
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32());

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task WithEvents_As_New_reports_New_and_does_not_bind_a_handler()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed()"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As New Publisher",
            "Private Sub publisher_Changed()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.withEventsNewNotAllowed");
        Assert.Equal(
            "New cannot be used with WithEvents.",
            diagnostic.GetProperty("message").GetString());
        Assert.Equal(
            32,
            diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(
            35,
            diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());

        var procedureDefinition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher_Changed");
        Assert.Equal(
            workerUri,
            procedureDefinition.GetProperty("result").GetProperty("uri").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task WithEvents_type_character_reports_the_suffix_and_does_not_bind_a_handler()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed()"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher$ As Publisher",
            "Private Sub publisher_Changed()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.withEventsTypeDeclarationCharacterNotAllowed");
        Assert.Equal(
            "Type-declaration characters cannot be used with WithEvents.",
            diagnostic.GetProperty("message").GetString());
        Assert.Equal(
            28,
            diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(
            29,
            diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());

        var procedureDefinition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher_Changed");
        Assert.Equal(
            workerUri,
            procedureDefinition.GetProperty("result").GetProperty("uri").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task WithEvents_without_As_type_reports_the_identifier_and_does_not_bind_a_handler()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed()"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher",
            "Private Sub publisher_Changed()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.withEventsTypeRequired");
        Assert.Equal(
            "WithEvents variables require an explicit class type in an As clause.",
            diagnostic.GetProperty("message").GetString());
        Assert.Equal(
            19,
            diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(
            28,
            diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());

        var procedureDefinition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher_Changed");
        Assert.Equal(
            workerUri,
            procedureDefinition.GetProperty("result").GetProperty("uri").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task WithEvents_type_required_ranges_cover_type_less_As_and_type_character_independently()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string uri = "file:///C:/work/Worker.cls";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents missing As",
            "Private WithEvents suffixed$"
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
        var required = diagnostics
            .Where(candidate => candidate.GetProperty("code").GetString()
                == "syntax.withEventsTypeRequired")
            .OrderBy(candidate => candidate
                .GetProperty("range")
                .GetProperty("start")
                .GetProperty("line")
                .GetInt32())
            .ToArray();
        Assert.Equal(2, required.Length);
        Assert.Equal(
            (27, 29),
            (
                required[0].GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                required[0].GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));
        Assert.Equal(
            (19, 27),
            (
                required[1].GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                required[1].GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));
        var typeCharacter = Assert.Single(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.withEventsTypeDeclarationCharacterNotAllowed");
        Assert.Equal(
            (27, 28),
            (
                typeCharacter.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                typeCharacter.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task WithEvents_cannot_use_its_enclosing_class_type()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string uri = "file:///C:/work/Worker.cls";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents self As Worker"
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
                == "validation.withEventsTypeCannotBeEnclosingClass");
        Assert.Equal(
            "A WithEvents variable cannot use its enclosing class as its declared type.",
            diagnostic.GetProperty("message").GetString());
        Assert.Equal(
            (27, 33),
            (
                diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task WithEvents_requires_a_specific_class_type()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string uri = "file:///C:/work/Worker.cls";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private Enum Status",
            "    Ready = 1",
            "End Enum",
            "Private WithEvents statusSource As Status"
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
                == "validation.withEventsTypeMustBeClass");
        Assert.Equal(
            "WithEvents variables must use a specific class type.",
            diagnostic.GetProperty("message").GetString());
        Assert.Equal(
            (35, 41),
            (
                diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));

        await process.ShutdownAsync(2);
    }

    [Theory]
    [InlineData("Object")]
    [InlineData("Variant")]
    [InlineData("Long")]
    [InlineData("String")]
    public async Task WithEvents_rejects_intrinsic_types_as_not_specific_classes(
        string intrinsicType)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string uri = "file:///C:/work/Worker.cls";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            $"Private WithEvents source As {intrinsicType}"
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
                == "validation.withEventsTypeMustBeClass");
        Assert.Equal(
            "WithEvents variables must use a specific class type.",
            diagnostic.GetProperty("message").GetString());
        Assert.Equal(
            (1, 29, 1, 29 + intrinsicType.Length),
            (
                diagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task WithEvents_class_type_must_expose_a_valid_Event()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Sub Publish()",
                    "End Sub"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.withEventsTypeMustExposeEvents");
        Assert.Equal(
            "The declared WithEvents class must expose at least one Event.",
            diagnostic.GetProperty("message").GetString());
        Assert.Equal(
            (32, 41),
            (
                diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task WithEvents_under_an_incomplete_conditional_has_indeterminate_type_authority()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                "Attribute VB_Name = \"Publisher\""));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "#If VBA7 Then",
            "Private WithEvents publisher As Publisher"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray();
        Assert.Contains(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.malformedPreprocessorNesting");
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString() is
                "validation.withEventsTypeCannotBeEnclosingClass"
                    or "validation.withEventsTypeMustBeClass"
                    or "validation.withEventsTypeMustBeAccessible"
                    or "validation.withEventsTypeMustExposeEvents");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Recovered_private_Event_keeps_WithEvents_type_eligibility_indeterminate()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Private Event Hidden()"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Hidden()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.withEventsTypeMustExposeEvents");

        var definition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher_Hidden",
            "publisher_".Length);
        Assert.Equal(JsonValueKind.Null, definition.GetProperty("result").ValueKind);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Unnamed_malformed_Event_keeps_WithEvents_type_eligibility_indeterminate()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event ("
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.withEventsTypeMustExposeEvents");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Unnamed_malformed_Event_keeps_a_known_Event_surface_indeterminate()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed()",
                    "Public Event ("
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Function publisher_Changed() As Boolean",
            "End Function"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString() is
                "validation.withEventsTypeMustExposeEvents"
                    or "validation.eventHandlerMustBeSub"
                    or "validation.incompatibleEventHandlerSignature");

        var definition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher_Changed",
            "publisher_".Length);
        Assert.Contains(
            publisherUri,
            definition.GetRawText(),
            StringComparison.Ordinal);

        await process.ShutdownAsync(3);
    }

    [Theory]
    [InlineData("Private Event Hidden()")]
    [InlineData("#If VBA7 Then\nPublic Event Hidden()")]
    public async Task Incomplete_sibling_Event_keeps_a_known_Event_surface_indeterminate(
        string incompleteEventSource)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed()",
                    incompleteEventSource
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Function publisher_Changed() As Boolean",
            "End Function"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString() is
                "validation.withEventsTypeMustExposeEvents"
                    or "validation.eventHandlerMustBeSub"
                    or "validation.incompatibleEventHandlerSignature");

        var definition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher_Changed",
            "publisher_".Length);
        Assert.Contains(
            publisherUri,
            definition.GetRawText(),
            StringComparison.Ordinal);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Handler_name_uses_only_its_final_underscore_for_decomposition()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed()"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents order As Publisher",
            "Private Sub order_publisher_Changed()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        await process.WaitForDiagnosticsAsync(workerUri);

        var definition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "order_publisher_Changed");
        var location = definition.GetProperty("result");
        Assert.Equal(workerUri, location.GetProperty("uri").GetString());
        Assert.Equal(
            2,
            location.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Source_Event_association_requires_a_Sub_handler()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed()"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Function publisher_Changed() As Boolean",
            "End Function"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.eventHandlerMustBeSub");
        Assert.Equal(
            "Event handlers must be declared as Sub procedures.",
            diagnostic.GetProperty("message").GetString());
        Assert.Equal(
            (2, 8, 2, 16),
            (
                diagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Event_under_an_incomplete_conditional_is_not_diagnostic_authority()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "#If VBA7 Then",
                    "Public Event Changed()"
                ])));

        var publisherNotification = await process.WaitForDiagnosticsAsync(publisherUri);
        Assert.Contains(
            publisherNotification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.malformedPreprocessorNesting");

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Function publisher_Changed() As Boolean",
            "End Function"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var workerNotification = await process.WaitForDiagnosticsAsync(workerUri);
        Assert.DoesNotContain(
            workerNotification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString() is
                "validation.withEventsTypeMustExposeEvents"
                    or "validation.eventHandlerMustBeSub"
                    or "validation.incompatibleEventHandlerSignature");

        var definition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher_Changed",
            "publisher_".Length);
        Assert.Contains(
            publisherUri,
            definition.GetRawText(),
            StringComparison.Ordinal);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Handler_under_an_incomplete_conditional_is_not_diagnostic_authority()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed()"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "#If VBA7 Then",
            "Private Function publisher_Changed() As Boolean",
            "End Function"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        var diagnostics = notification
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray();
        Assert.Contains(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString()
                == "syntax.malformedPreprocessorNesting");
        Assert.DoesNotContain(
            diagnostics,
            candidate => candidate.GetProperty("code").GetString() is
                "validation.eventHandlerMustBeSub"
                    or "validation.incompatibleEventHandlerSignature");

        var definition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher_Changed",
            "publisher_".Length);
        Assert.Contains(
            publisherUri,
            definition.GetRawText(),
            StringComparison.Ordinal);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Recovered_Event_variant_suppresses_the_nonSub_diagnostic()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "#If VBA7 Then",
                    "Public Event Changed()",
                    "#Else",
                    "Private Event Changed()",
                    "#End If"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Function publisher_Changed() As Boolean",
            "End Function"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.eventHandlerMustBeSub");

        var definition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher_Changed",
            "publisher_".Length);
        Assert.Contains(
            publisherUri,
            definition.GetRawText(),
            StringComparison.Ordinal);

        await process.ShutdownAsync(3);
    }

    [Theory]
    [InlineData("Private Property Get publisher_Changed() As Boolean")]
    [InlineData("Private Property Let publisher_Changed(ByVal assignedValue As Boolean)")]
    [InlineData("Private Property Set publisher_Changed(ByVal assignedValue As Object)")]
    public async Task Source_Event_association_rejects_each_Property_accessor(
        string declaration)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed()"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            declaration,
            "End Property"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.eventHandlerMustBeSub");
        Assert.Equal(
            (2, 8, 2, 20),
            (
                diagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Mixed_notWithEvents_binding_suppresses_the_nonSub_diagnostic()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed()"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "#If VBA7 Then",
            "Private WithEvents publisher As Publisher",
            "#Else",
            "Private publisher As Publisher",
            "#End If",
            "Private Function publisher_Changed() As Boolean",
            "End Function"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.eventHandlerMustBeSub");
        var hover = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/hover",
            workerUri,
            workerText,
            "publisher_Changed",
            "publisher_".Length);
        Assert.Contains(
            "Event Changed()",
            hover
                .GetProperty("result")
                .GetProperty("contents")
                .GetProperty("value")
                .GetString(),
            StringComparison.Ordinal);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Mixed_notEvent_binding_suppresses_the_nonSub_diagnostic()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string firstPublisherUri = "file:///C:/work/FirstPublisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                firstPublisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"FirstPublisher\"",
                    "Public Event Changed()"
                ])));
        const string secondPublisherUri = "file:///C:/work/SecondPublisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                secondPublisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"SecondPublisher\"",
                    "Public Event Other()"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "#If VBA7 Then",
            "Private WithEvents publisher As FirstPublisher",
            "#Else",
            "Private WithEvents publisher As SecondPublisher",
            "#End If",
            "Private Function publisher_Changed() As Boolean",
            "End Function"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.eventHandlerMustBeSub");
        var hover = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/hover",
            workerUri,
            workerText,
            "publisher_Changed",
            "publisher_".Length);
        Assert.Contains(
            "Event Changed()",
            hover
                .GetProperty("result")
                .GetProperty("contents")
                .GetProperty("value")
                .GetString(),
            StringComparison.Ordinal);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Mixed_indeterminate_binding_suppresses_the_nonSub_diagnostic()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed()"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "#If VBA7 Then",
            "Private WithEvents publisher As Publisher",
            "#Else",
            "Private WithEvents publisher As MissingPublisher",
            "#End If",
            "Private Function publisher_Changed() As Boolean",
            "End Function"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                is "validation.eventHandlerMustBeSub"
                    or "validation.incompatibleEventHandlerSignature");
        var hover = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/hover",
            workerUri,
            workerText,
            "publisher_Changed",
            "publisher_".Length);
        Assert.Contains(
            "Event Changed()",
            hover
                .GetProperty("result")
                .GetProperty("contents")
                .GetProperty("value")
                .GetString(),
            StringComparison.Ordinal);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Source_Event_handler_reports_a_conclusive_parameter_type_mismatch()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed(ByVal Value As Long)"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        const string handlerLine =
            "Private Sub publisher_Changed(ByVal DifferentName As String)";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            handlerLine,
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleEventHandlerSignature");
        Assert.Equal(
            "Event handler signature does not match any available Event signature.\n"
                + "Expected signature: Event Changed(Value As Long).\n"
                + "Mismatches: parameter 1 type: expected Long, found String.",
            diagnostic.GetProperty("message").GetString());
        Assert.Equal(
            (
                2,
                "Private Sub publisher_Changed".Length,
                2,
                handlerLine.Length),
            (
                diagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Source_Event_handler_mismatch_uses_related_contract_information_when_supported()
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

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed(ByVal Value As Long)"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Changed(ByVal Value As String)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleEventHandlerSignature");
        Assert.Equal(
            "Event handler signature does not match any available Event signature.",
            diagnostic.GetProperty("message").GetString());
        var related = Assert.Single(
            diagnostic.GetProperty("relatedInformation").EnumerateArray());
        Assert.Equal(
            publisherUri,
            related.GetProperty("location").GetProperty("uri").GetString());
        Assert.Equal(
            (1, 13, 1, 20),
            (
                related.GetProperty("location").GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
                related.GetProperty("location").GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                related.GetProperty("location").GetProperty("range").GetProperty("end").GetProperty("line").GetInt32(),
                related.GetProperty("location").GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));
        Assert.Equal(
            "Required contract: Event Changed(Value As Long). "
                + "Mismatches: parameter 1 type: expected Long, found String.",
            related.GetProperty("message").GetString());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Source_Event_handler_reports_parameter_count_mismatch()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed(ByVal First As Long, ByVal Second As String)"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Changed(ByVal Renamed As Long)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleEventHandlerSignature");
        Assert.Equal(
            "Event handler signature does not match any available Event signature.\n"
                + "Expected signature: Event Changed(First As Long, Second As String).\n"
                + "Mismatches: parameter count: expected 2, found 1.",
            diagnostic.GetProperty("message").GetString());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Source_Event_handler_ignores_parameter_names_and_defaults_to_ByRef()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed(ByRef Original As Long)"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Changed(Renamed As Long)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleEventHandlerSignature");

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Source_Event_handler_reports_type_array_passing_role_and_default_mismatches()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed(ByRef Values() As Long)"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Changed(Optional ByVal Renamed As String = \"\")",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleEventHandlerSignature");
        Assert.Equal(
            "Event handler signature does not match any available Event signature.\n"
                + "Expected signature: Event Changed(ByRef Values() As Long).\n"
                + "Mismatches: parameter 1 type: expected Long, found String; "
                + "parameter 1 array shape: expected array, found scalar; "
                + "parameter 1 passing: expected ByRef, found ByVal; "
                + "parameter 1 role: expected required, found Optional; "
                + "parameter 1 default: expected no default, found \"\".",
            diagnostic.GetProperty("message").GetString());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Source_Event_handler_disambiguates_distinct_qualified_parameter_types()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-qualified-types-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-qualified-types-cache-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(
                projectRoot,
                "First Library",
                "Second Library");
            var store = new VbaProjectReferenceCatalogPersistentStore(cacheRoot);
            store.Save(new VbaProjectReferenceCatalogPersistentEntry(
                CreateGeneratedReferenceCatalogIdentity("First Library"),
                new VbaProjectReferenceCatalog(
                    "First Library",
                    ["First"],
                    [
                        new VbaProjectReferenceDefinition(
                            "First Library",
                            "Payload",
                            VbaSourceDefinitionKind.Class)
                    ])));
            store.Save(new VbaProjectReferenceCatalogPersistentEntry(
                CreateGeneratedReferenceCatalogIdentity(
                    "Second Library",
                    "{44444444-4444-4444-4444-444444444444}"),
                new VbaProjectReferenceCatalog(
                    "Second Library",
                    ["Second"],
                    [
                        new VbaProjectReferenceDefinition(
                            "Second Library",
                            "Payload",
                            VbaSourceDefinitionKind.Class)
                    ])));

            var publisherPath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Publisher.cls");
            var publisherUri = new Uri(publisherPath).AbsoluteUri;
            var publisherText = string.Join('\n', [
                "Attribute VB_Name = \"Publisher\"",
                "Public Event Changed(ByVal Value As First.Payload)"
            ]);
            File.WriteAllText(publisherPath, publisherText);
            var workerPath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.cls");
            var workerUri = new Uri(workerPath).AbsoluteUri;
            var workerText = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Private WithEvents publisher As Publisher",
                "Private Sub publisher_Changed(ByVal Renamed As Second.Payload)",
                "End Sub"
            ]);
            File.WriteAllText(workerPath, workerText);

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(publisherUri, publisherText));
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(workerUri, workerText));
            await process.WaitForDiagnosticsAsync(workerUri);
            await process.WaitForLogTextAsync(
                "reference 'First Library' source=persisted outcome=skipped "
                    + "phase=persistent-load expensiveMetadata=false");
            await process.WaitForLogTextAsync(
                "reference 'Second Library' source=persisted outcome=skipped "
                    + "phase=persistent-load expensiveMetadata=false");
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri = workerUri, version = 2 },
                    contentChanges = new[] { new { text = workerText } }
                });

            var notification = await process.WaitForDiagnosticsAsync(workerUri);
            var diagnostic = Assert.Single(
                notification
                    .GetProperty("params")
                    .GetProperty("diagnostics")
                    .EnumerateArray(),
                candidate => candidate.GetProperty("code").GetString()
                    == "validation.incompatibleEventHandlerSignature");
            Assert.Equal(
                "Event handler signature does not match any available Event signature.\n"
                    + "Expected signature: Event Changed(Value As Payload).\n"
                    + "Mismatches: parameter 1 type: expected First.Payload, "
                    + "found Second.Payload.",
                diagnostic.GetProperty("message").GetString());

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Source_Event_handler_without_a_parameter_list_selects_its_identifier()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed(ByVal Value As Long)"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        const string handlerLine = "Private Sub publisher_Changed";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            handlerLine,
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleEventHandlerSignature");
        Assert.Equal(
            (2, "Private Sub ".Length, 2, handlerLine.Length),
            (
                diagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32(),
                diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task One_compatible_conditional_Event_signature_suppresses_the_aggregate_diagnostic()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "#If VBA7 Then",
                    "Public Event Changed(ByVal Value As Long)",
                    "#Else",
                    "Public Event Changed(ByVal Value As String)",
                    "#End If"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Changed(ByVal Value As String)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleEventHandlerSignature");

        var signatureHelp = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            workerUri,
            workerText,
            "publisher_Changed(",
            "publisher_Changed(".Length);
        Assert.Equal(
            [
                "Event Changed(Value As Long) [#If]",
                "Event Changed(Value As String) [#If]"
            ],
            signatureHelp
                .GetProperty("result")
                .GetProperty("signatures")
                .EnumerateArray()
                .Select(signature => signature.GetProperty("label").GetString()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Incompatible_conditional_Event_signatures_retain_source_order()
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

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "#If VBA7 Then",
                    "Public Event Changed(ByVal Value As Long)",
                    "#Else",
                    "Public Event Changed(ByVal Value As String)",
                    "#End If"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Changed(ByVal Value As Boolean)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var notification = await process.WaitForDiagnosticsAsync(workerUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleEventHandlerSignature");
        Assert.Equal(
            "Event handler signature does not match any available Event signature.",
            diagnostic.GetProperty("message").GetString());
        var related = diagnostic
            .GetProperty("relatedInformation")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, related.Length);
        Assert.Equal(
            [2, 4],
            related
                .Select(item => item
                    .GetProperty("location")
                    .GetProperty("range")
                    .GetProperty("start")
                    .GetProperty("line")
                    .GetInt32())
                .ToArray());
        Assert.Equal(
            [
                "Required contract: Event Changed(Value As Long) [#If]. "
                    + "Mismatches: parameter 1 type: expected Long, found Boolean.",
                "Required contract: Event Changed(Value As String) [#If]. "
                    + "Mismatches: parameter 1 type: expected String, found Boolean."
            ],
            related
                .Select(item => item.GetProperty("message").GetString()!)
                .ToArray());
        Assert.DoesNotContain("VBA7", diagnostic.GetRawText(), StringComparison.Ordinal);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Source_Event_handler_declaration_provides_signature_help()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed(ByVal Value As Long)"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Changed(ByVal DifferentName As Long)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            workerUri,
            workerText,
            "DifferentName");
        var result = response.GetProperty("result");
        var signature = Assert.Single(
            result.GetProperty("signatures").EnumerateArray());
        Assert.Equal(
            "Event Changed(Value As Long)",
            signature.GetProperty("label").GetString());
        Assert.Equal(0, result.GetProperty("activeParameter").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Unconditional_Source_Event_handler_hover_has_no_conditional_marker()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                publisherUri,
                string.Join('\n', [
                    "Attribute VB_Name = \"Publisher\"",
                    "Public Event Changed()"
                ])));

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Changed()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var response = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/hover",
            workerUri,
            workerText,
            "publisher_Changed",
            "publisher_".Length);
        var value = response
            .GetProperty("result")
            .GetProperty("contents")
            .GetProperty("value")
            .GetString();
        Assert.Contains("Event Changed()", value, StringComparison.Ordinal);
        Assert.DoesNotContain("[#If]", value, StringComparison.Ordinal);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Hidden_external_coclass_resolves_explicitly_without_type_completion()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-hidden-coclass-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-hidden-coclass-cache-").FullName;
        try
        {
            const string referenceName = "Generated Library";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            var changedEvent = new TypeLibCatalogMember(
                "Changed",
                VbaSourceDefinitionKind.Event,
                "Hidden publisher Event.",
                new VbaCallableSignature(
                    "Event Changed()",
                    [],
                    CallableKind: VbaCallableKind.Event),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: 0));
            var catalog = TypeLibReferenceCatalogBuilder.Build(
                referenceName,
                new TypeLibCatalogMetadata(
                    "Generated",
                    [
                        new TypeLibCatalogType(
                            "HiddenPublisher",
                            VbaSourceDefinitionKind.Class,
                            "Hidden TypeLib publisher.",
                            [changedEvent],
                            IsCreatable: false,
                            IsBrowsable: false,
                            Metadata: new TypeLibCatalogTypeMetadata(
                                TypeLibCatalogRawTypeKind.CoClass,
                                TypeFlags: 0x10,
                                ImplementedInterfaces:
                                [
                                    new TypeLibCatalogImplementedInterface(
                                        "_HiddenPublisherEvents",
                                        TypeFlags: 0,
                                        ImplementationFlags: 0x1 | 0x2,
                                        CallableMembers: [changedEvent],
                                        RawTypeKind:
                                            TypeLibCatalogRawTypeKind.Dispatch)
                                ]))
                    ]));
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    catalog));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();

            var sourcePath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.cls");
            var uri = new Uri(sourcePath).AbsoluteUri;
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Private WithEvents publisher As HiddenPublisher",
                "Private WithEvents candidate As ",
                "Private Sub publisher_Changed()",
                "End Sub"
            ]);
            File.WriteAllText(sourcePath, text);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            var diagnostics = await process.WaitForDiagnosticsAsync(uri);
            Assert.DoesNotContain(
                diagnostics
                    .GetProperty("params")
                    .GetProperty("diagnostics")
                    .EnumerateArray(),
                candidate => candidate.GetProperty("code").GetString()
                    is "validation.withEventsTypeMustBeClass"
                        or "validation.withEventsTypeMustBeAccessible"
                        or "validation.withEventsTypeMustExposeEvents");

            var typeHover = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/hover",
                uri,
                text,
                "HiddenPublisher");
            Assert.Contains(
                "Hidden TypeLib publisher.",
                typeHover
                    .GetProperty("result")
                    .GetProperty("contents")
                    .GetProperty("value")
                    .GetString(),
                StringComparison.Ordinal);
            var handlerHover = await SendPositionRequestAsync(
                process,
                3,
                "textDocument/hover",
                uri,
                text,
                "publisher_Changed",
                "publisher_".Length);
            Assert.Contains(
                "Event Changed()",
                handlerHover
                    .GetProperty("result")
                    .GetProperty("contents")
                    .GetProperty("value")
                    .GetString(),
                StringComparison.Ordinal);

            var definition = await SendPositionRequestAsync(
                process,
                4,
                "textDocument/definition",
                uri,
                text,
                "publisher_Changed",
                "publisher_".Length);
            Assert.Equal(
                JsonValueKind.Null,
                definition.GetProperty("result").ValueKind);
            Assert.DoesNotContain(
                VbaProjectReferenceCatalogSet.ExternalDefinitionUriPrefix,
                definition.GetRawText(),
                StringComparison.Ordinal);
            var references = await SendPositionRequestAsync(
                process,
                5,
                "textDocument/references",
                uri,
                text,
                "publisher_Changed",
                "publisher_".Length);
            var handlerReference = Assert.Single(
                references.GetProperty("result").EnumerateArray());
            Assert.Equal(uri, handlerReference.GetProperty("uri").GetString());
            Assert.Equal(
                (3, 22, 3, 29),
                (
                    handlerReference.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
                    handlerReference.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                    handlerReference.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32(),
                    handlerReference.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));

            var completion = await process.SendRequestAsync(
                6,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri },
                    position = new
                    {
                        line = 2,
                        character = "Private WithEvents candidate As ".Length
                    }
                });
            Assert.DoesNotContain(
                completion
                    .GetProperty("result")
                    .EnumerateArray(),
                item => item.GetProperty("label").GetString()
                    == "HiddenPublisher");

            await process.ShutdownAsync(7);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Restricted_external_coclass_reports_accessibility_diagnostic()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-restricted-coclass-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-restricted-coclass-cache-").FullName;
        try
        {
            const string referenceName = "Generated Library";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            var changedEvent = new TypeLibCatalogMember(
                "Changed",
                VbaSourceDefinitionKind.Event,
                Documentation: null,
                new VbaCallableSignature(
                    "Event Changed()",
                    [],
                    CallableKind: VbaCallableKind.Event),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: 0));
            var catalog = TypeLibReferenceCatalogBuilder.Build(
                referenceName,
                new TypeLibCatalogMetadata(
                    "Generated",
                    [
                        new TypeLibCatalogType(
                            "RestrictedPublisher",
                            VbaSourceDefinitionKind.Class,
                            Documentation: null,
                            [changedEvent],
                            IsCreatable: true,
                            IsBrowsable: false,
                            Metadata: new TypeLibCatalogTypeMetadata(
                                TypeLibCatalogRawTypeKind.CoClass,
                                TypeFlags: 0x200,
                                ImplementedInterfaces:
                                [
                                    new TypeLibCatalogImplementedInterface(
                                        "_RestrictedPublisherEvents",
                                        TypeFlags: 0,
                                        ImplementationFlags: 0x1 | 0x2,
                                        CallableMembers: [changedEvent],
                                        RawTypeKind:
                                            TypeLibCatalogRawTypeKind.Dispatch)
                                ]))
                    ]));
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    catalog));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();

            var sourcePath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.cls");
            var uri = new Uri(sourcePath).AbsoluteUri;
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Private WithEvents publisher As RestrictedPublisher"
            ]);
            File.WriteAllText(sourcePath, text);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
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
                    .EnumerateArray(),
                candidate => candidate.GetProperty("code").GetString()
                    == "validation.withEventsTypeMustBeAccessible");
            Assert.Equal(
                "The declared WithEvents class must be accessible to VBA.",
                diagnostic.GetProperty("message").GetString());
            Assert.Equal(
                (1, 32, 1, 51),
                (
                    diagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
                    diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                    diagnostic.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32(),
                    diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task External_dispinterface_is_not_an_eligible_WithEvents_class()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-dispatch-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-dispatch-cache-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot, "Generated Library");
            var store = new VbaProjectReferenceCatalogPersistentStore(cacheRoot);
            store.Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity("Generated Library"),
                    new VbaProjectReferenceCatalog(
                        "Generated Library",
                        ["Generated"],
                        [
                            new VbaProjectReferenceDefinition(
                                "Generated Library",
                                "GeneratedType",
                                VbaSourceDefinitionKind.Class)
                        ],
                        [
                            new TypeLibCatalogType(
                                "GeneratedType",
                                VbaSourceDefinitionKind.Class,
                                Documentation: null,
                                Members: [],
                                Metadata: new TypeLibCatalogTypeMetadata(
                                    TypeLibCatalogRawTypeKind.Dispatch,
                                    TypeFlags: 0,
                                    ImplementedInterfaces: []))
                        ])));
            var persisted = await store.LoadAsync(
                "Generated Library",
                CancellationToken.None);
            Assert.NotNull(persisted.Entry?.Catalog.TypeLibTypes);
            var persistedSurface = VbaProjectReferenceCatalogSet
                .CreateBundled()
                .WithCatalog(persisted.Entry!.Catalog)
                .GetTypeLibEventSurface("Generated Library", "GeneratedType");
            Assert.Equal(
                TypeLibCatalogRawTypeKind.Dispatch,
                persistedSurface.RawTypeKind);

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();

            var sourcePath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.cls");
            var uri = new Uri(sourcePath).AbsoluteUri;
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Private WithEvents publisher As GeneratedType"
            ]);
            File.WriteAllText(sourcePath, text);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
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
                    .EnumerateArray(),
                candidate => candidate.GetProperty("code").GetString()
                    == "validation.withEventsTypeMustBeClass");
            Assert.Equal(
                "WithEvents variables must use a specific class type.",
                diagnostic.GetProperty("message").GetString());
            Assert.Equal(
                (32, 45),
                (
                    diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                    diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Non_default_TypeLib_source_interface_does_not_expose_WithEvents_events()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-nondefault-source-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-nondefault-source-cache-").FullName;
        try
        {
            const string referenceName = "Generated Library";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            var changedEvent = new TypeLibCatalogMember(
                "Changed",
                VbaSourceDefinitionKind.Event,
                Documentation: null,
                new VbaCallableSignature(
                    "Event Changed()",
                    [],
                    CallableKind: VbaCallableKind.Event),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: 0));
            var catalog = TypeLibReferenceCatalogBuilder.Build(
                referenceName,
                new TypeLibCatalogMetadata(
                    "Generated",
                    [
                        new TypeLibCatalogType(
                            "Publisher",
                            VbaSourceDefinitionKind.Class,
                            Documentation: null,
                            Members: [],
                            IsCreatable: true,
                            Metadata: new TypeLibCatalogTypeMetadata(
                                TypeLibCatalogRawTypeKind.CoClass,
                                TypeFlags: 0,
                                ImplementedInterfaces:
                                [
                                    new TypeLibCatalogImplementedInterface(
                                        "SecondaryEvents",
                                        TypeFlags: 0,
                                        ImplementationFlags: 0x2,
                                        CallableMembers: [changedEvent],
                                        RawTypeKind:
                                            TypeLibCatalogRawTypeKind.Dispatch)
                                ]))
                    ]));
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    catalog));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();

            var sourcePath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.cls");
            var uri = new Uri(sourcePath).AbsoluteUri;
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Private WithEvents publisher As Publisher"
            ]);
            File.WriteAllText(sourcePath, text);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
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
                    .EnumerateArray(),
                candidate => candidate.GetProperty("code").GetString()
                    == "validation.withEventsTypeMustExposeEvents");
            Assert.Equal(
                (1, 32, 1, 41),
                (
                    diagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
                    diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                    diagnostic.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32(),
                    diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Multiple_default_TypeLib_source_interfaces_keep_handler_indeterminate()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-multiple-default-sources-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-multiple-default-sources-cache-").FullName;
        try
        {
            const string referenceName = "Generated Library";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            var firstEvent = new TypeLibCatalogMember(
                "Changed",
                VbaSourceDefinitionKind.Event,
                Documentation: null,
                new VbaCallableSignature(
                    "Event Changed()",
                    [],
                    CallableKind: VbaCallableKind.Event),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: 0));
            var secondEvent = firstEvent with
            {
                Metadata = new TypeLibCatalogCallableMetadata(
                    MemberId: 2,
                    FunctionFlags: 0)
            };
            var catalog = TypeLibReferenceCatalogBuilder.Build(
                referenceName,
                new TypeLibCatalogMetadata(
                    "Generated",
                    [
                        new TypeLibCatalogType(
                            "Publisher",
                            VbaSourceDefinitionKind.Class,
                            Documentation: null,
                            Members: [],
                            IsCreatable: true,
                            Metadata: new TypeLibCatalogTypeMetadata(
                                TypeLibCatalogRawTypeKind.CoClass,
                                TypeFlags: 0,
                                ImplementedInterfaces:
                                [
                                    new TypeLibCatalogImplementedInterface(
                                        "FirstEvents",
                                        TypeFlags: 0,
                                        ImplementationFlags: 0x1 | 0x2,
                                        CallableMembers: [firstEvent],
                                        RawTypeKind:
                                            TypeLibCatalogRawTypeKind.Dispatch),
                                    new TypeLibCatalogImplementedInterface(
                                        "SecondEvents",
                                        TypeFlags: 0,
                                        ImplementationFlags: 0x1 | 0x2,
                                        CallableMembers: [secondEvent],
                                        RawTypeKind:
                                            TypeLibCatalogRawTypeKind.Dispatch)
                                ]))
                    ]));
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    catalog));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();

            var sourcePath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.cls");
            var uri = new Uri(sourcePath).AbsoluteUri;
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Private WithEvents publisher As Publisher",
                "Private Sub publisher_Changed()",
                "End Sub"
            ]);
            File.WriteAllText(sourcePath, text);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });

            var notification = await process.WaitForDiagnosticsAsync(uri);
            Assert.DoesNotContain(
                notification
                    .GetProperty("params")
                    .GetProperty("diagnostics")
                    .EnumerateArray(),
                candidate => candidate.GetProperty("code").GetString()
                    is "validation.withEventsTypeMustBeClass"
                        or "validation.withEventsTypeMustBeAccessible"
                        or "validation.withEventsTypeMustExposeEvents");
            var definition = await SendPositionRequestAsync(
                process,
                3,
                "textDocument/definition",
                uri,
                text,
                "publisher_Changed");
            var location = definition.GetProperty("result");
            Assert.Equal(uri, location.GetProperty("uri").GetString());
            Assert.Equal(
                (1, 19, 1, 28),
                (
                    location.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
                    location.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                    location.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32(),
                    location.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));

            await process.ShutdownAsync(4);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(
        "GeneratedType",
        VbaSourceDefinitionKind.Class,
        TypeLibCatalogRawTypeKind.Dispatch)]
    [InlineData(
        "GeneratedEnum",
        VbaSourceDefinitionKind.Enum,
        TypeLibCatalogRawTypeKind.Other)]
    public async Task Stale_external_type_metadata_does_not_authorize_WithEvents_type_diagnostics(
        string typeName,
        VbaSourceDefinitionKind definitionKind,
        TypeLibCatalogRawTypeKind rawTypeKind)
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-stale-typelib-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-stale-typelib-cache-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot, "Generated Library");
            var store = new VbaProjectReferenceCatalogPersistentStore(cacheRoot);
            store.Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity("Generated Library"),
                    new VbaProjectReferenceCatalog(
                        "Generated Library",
                        ["Generated"],
                        [
                            new VbaProjectReferenceDefinition(
                                "Generated Library",
                                typeName,
                                definitionKind)
                        ],
                        [
                            new TypeLibCatalogType(
                                typeName,
                                definitionKind,
                                Documentation: null,
                                Members: [],
                                Metadata: new TypeLibCatalogTypeMetadata(
                                    rawTypeKind,
                                    TypeFlags: 0,
                                    ImplementedInterfaces: []))
                        ])));
            MarkReferenceCatalogIndexAsStale(store, "Generated Library");

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();

            var sourcePath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.cls");
            var uri = new Uri(sourcePath).AbsoluteUri;
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                $"Private WithEvents publisher As {typeName}"
            ]);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.WaitForLogTextAsync(
                "source=stale-persisted outcome=stale phase=persistent-load expensiveMetadata=false");

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
                    is "validation.withEventsTypeMustBeClass"
                        or "validation.withEventsTypeMustBeAccessible"
                        or "validation.withEventsTypeMustExposeEvents");

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Source_Event_Rename_updates_its_handler_suffix_and_complete_name_references()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        var publisherText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Publisher\"",
            "Public Event Changed(ByVal value As Long)",
            "Private Sub Fire(ByVal value As Long)",
            "    RaiseEvent Changed(value)",
            "End Sub"
        ]);
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Changed(ByVal value As Long)",
            "End Sub",
            "Private Sub Invoke()",
            "    publisher_Changed 1",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = publisherUri },
                position = new
                {
                    line = 2,
                    character = "Public Event ".Length
                },
                newName = "Updated"
            });

        Assert.False(
            rename.TryGetProperty("error", out var renameError),
            renameError.ToString());
        var changes = rename.GetProperty("result").GetProperty("changes");
        Assert.Equal(
            [(Line: 2, NewText: "Updated"), (Line: 4, NewText: "Updated")],
            changes.GetProperty(publisherUri).EnumerateArray().Select(edit => (
                Line: edit.GetProperty("range").GetProperty("start")
                    .GetProperty("line").GetInt32(),
                NewText: edit.GetProperty("newText").GetString())));
        Assert.Equal(
            [
                (Line: 3, NewText: "Updated"),
                (Line: 6, NewText: "publisher_Updated")
            ],
            changes.GetProperty(workerUri).EnumerateArray().Select(edit => (
                Line: edit.GetProperty("range").GetProperty("start")
                    .GetProperty("line").GetInt32(),
                NewText: edit.GetProperty("newText").GetString())));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Source_Event_Rename_treats_notWithEvents_and_notEvent_entries_as_neutral()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            Attribute VB_Name = "FirstPublisher"
            Public Event Changed()
            """;
        const string otherPublisherUri = "file:///C:/work/OtherPublisher.cls";
        const string otherPublisherText = """
            Attribute VB_Name = "SecondPublisher"
            Public Event Other()
            """;
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "#If FIRST_CONFIGURATION Then",
            "Private WithEvents publisher As FirstPublisher",
            "#ElseIf SECOND_CONFIGURATION Then",
            "Private publisher As FirstPublisher",
            "#Else",
            "Private WithEvents publisher As SecondPublisher",
            "#End If",
            "Private Sub publisher_Changed()",
            "End Sub",
            "Private Sub Invoke()",
            "    publisher_Changed",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(otherPublisherUri, otherPublisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = publisherUri },
                position = new
                {
                    line = 1,
                    character = "Public Event ".Length
                },
                newName = "Updated"
            });

        Assert.False(
            rename.TryGetProperty("error", out var renameError),
            renameError.ToString());
        var changes = rename.GetProperty("result").GetProperty("changes");
        Assert.Equal(
            [(Line: 1, NewText: "Updated")],
            changes.GetProperty(publisherUri).EnumerateArray().Select(edit => (
                Line: edit.GetProperty("range").GetProperty("start")
                    .GetProperty("line").GetInt32(),
                NewText: edit.GetProperty("newText").GetString())));
        Assert.Equal(
            [
                (Line: 8, NewText: "Updated"),
                (Line: 11, NewText: "publisher_Updated")
            ],
            changes.GetProperty(workerUri).EnumerateArray().Select(edit => (
                Line: edit.GetProperty("range").GetProperty("start")
                    .GetProperty("line").GetInt32(),
                NewText: edit.GetProperty("newText").GetString())));
        Assert.False(changes.TryGetProperty(otherPublisherUri, out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task RaiseEvent_case_only_Rename_updates_every_conditional_Event_variant_and_dependent()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        var publisherText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Publisher\"",
            "#If VBA7 Then",
            "Public Event Changed()",
            "#Else",
            "Public Event Changed()",
            "#End If",
            "Private Sub Fire()",
            "    RaiseEvent Changed",
            "End Sub"
        ]);
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Changed()",
            "End Sub",
            "Private Sub Invoke()",
            "    publisher_Changed",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = publisherUri },
                position = new
                {
                    line = 8,
                    character = "    RaiseEvent ".Length
                },
                newName = "changed"
            });

        Assert.False(
            rename.TryGetProperty("error", out var renameError),
            renameError.ToString());
        var changes = rename.GetProperty("result").GetProperty("changes");
        Assert.Equal(
            [
                (Line: 3, NewText: "changed"),
                (Line: 5, NewText: "changed"),
                (Line: 8, NewText: "changed")
            ],
            changes.GetProperty(publisherUri).EnumerateArray().Select(edit => (
                Line: edit.GetProperty("range").GetProperty("start")
                    .GetProperty("line").GetInt32(),
                NewText: edit.GetProperty("newText").GetString())));
        Assert.Equal(
            [
                (Line: 3, NewText: "changed"),
                (Line: 6, NewText: "publisher_changed")
            ],
            changes.GetProperty(workerUri).EnumerateArray().Select(edit => (
                Line: edit.GetProperty("range").GetProperty("start")
                    .GetProperty("line").GetInt32(),
                NewText: edit.GetProperty("newText").GetString())));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task WithEvents_variable_Rename_updates_handler_prefixes_and_complete_name_references()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed(ByVal value As Long)
            """;
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Changed(ByVal value As Long)",
            "End Sub",
            "Private Sub Invoke()",
            "    Set publisher = New Publisher",
            "    publisher_Changed 1",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = workerUri },
                position = new
                {
                    line = 2,
                    character = "Private WithEvents ".Length
                },
                newName = "source"
            });

        Assert.False(
            rename.TryGetProperty("error", out var renameError),
            renameError.ToString());
        var changes = rename.GetProperty("result").GetProperty("changes");
        Assert.False(changes.TryGetProperty(publisherUri, out _));
        Assert.Equal(
            [
                (Line: 2, NewText: "source"),
                (Line: 3, NewText: "source"),
                (Line: 6, NewText: "source"),
                (Line: 7, NewText: "source_Changed")
            ],
            changes.GetProperty(workerUri).EnumerateArray().Select(edit => (
                Line: edit.GetProperty("range").GetProperty("start")
                    .GetProperty("line").GetInt32(),
                NewText: edit.GetProperty("newText").GetString())));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task WithEvents_variable_case_only_Rename_updates_its_dependents()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents source As Publisher",
            "Private Sub source_Changed()",
            "End Sub",
            "Private Sub Invoke()",
            "    Set source = Nothing",
            "    source_Changed",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = workerUri },
                position = new
                {
                    line = 2,
                    character = "Private WithEvents ".Length
                },
                newName = "Source"
            });

        Assert.False(
            rename.TryGetProperty("error", out var renameError),
            renameError.ToString());
        Assert.Equal(
            [
                (Line: 2, NewText: "Source"),
                (Line: 3, NewText: "Source"),
                (Line: 6, NewText: "Source"),
                (Line: 7, NewText: "Source_Changed")
            ],
            rename.GetProperty("result").GetProperty("changes")
                .GetProperty(workerUri).EnumerateArray().Select(edit => (
                    Line: edit.GetProperty("range").GetProperty("start")
                        .GetProperty("line").GetInt32(),
                    NewText: edit.GetProperty("newText").GetString())));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Dependent_handler_complete_name_cannot_initiate_non_no_op_Rename()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Changed()",
            "End Sub",
            "Private Sub Invoke()",
            "    publisher_Changed",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 6, character = "    ".Length },
                newName = "DetachedHandler"
            });

        Assert.False(rename.TryGetProperty("result", out _));
        Assert.Equal(
            "notRenameTarget",
            rename.GetProperty("error").GetProperty("data")
                .GetProperty("reason").GetString());

        var noOpRename = await process.SendRequestAsync(
            3,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 6, character = "    ".Length },
                newName = "publisher_Changed"
            });
        Assert.False(
            noOpRename.TryGetProperty("error", out var noOpError),
            noOpError.ToString());
        Assert.Equal(
            JsonValueKind.Null,
            noOpRename.GetProperty("result").ValueKind);

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Event_Rename_rejects_a_handler_shared_with_a_distinct_source_Event()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string firstPublisherUri = "file:///C:/work/FirstPublisher.cls";
        const string firstPublisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "FirstPublisher"
            Public Event Changed()
            """;
        const string secondPublisherUri = "file:///C:/work/SecondPublisher.cls";
        const string secondPublisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "SecondPublisher"
            Public Event Changed()
            """;
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "#If FIRST_CONFIGURATION Then",
            "Private WithEvents source As FirstPublisher",
            "#Else",
            "Private WithEvents source As SecondPublisher",
            "#End If",
            "Private Sub source_Changed()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(firstPublisherUri, firstPublisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(secondPublisherUri, secondPublisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = firstPublisherUri },
                position = new
                {
                    line = 2,
                    character = "Public Event ".Length
                },
                newName = "Updated"
            });

        Assert.False(rename.TryGetProperty("result", out _));
        Assert.Equal(
            "resolutionChanged",
            rename.GetProperty("error").GetProperty("data")
                .GetProperty("reason").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task WithEvents_variable_Rename_fails_closed_for_indeterminate_conditional_type_evidence()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "#If KNOWN_CONFIGURATION Then",
            "Private WithEvents source As Publisher",
            "#Else",
            "Private WithEvents source As MissingPublisher",
            "#End If",
            "Private Sub source_Changed()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = workerUri },
                position = new
                {
                    line = 3,
                    character = "Private WithEvents ".Length
                },
                newName = "publisher"
            });

        Assert.False(rename.TryGetProperty("result", out _));
        Assert.Equal(
            "analysisIncomplete",
            rename.GetProperty("error").GetProperty("data")
                .GetProperty("reason").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Convergent_handler_suffix_Prepare_Rename_selects_only_the_source_Event_name()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        const string workerUri = "file:///C:/work/Worker.cls";
        const string declaration = "Private Sub publisher_Changed()";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            declaration,
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var prepare = await process.SendRequestAsync(
            2,
            "textDocument/prepareRename",
            new
            {
                textDocument = new { uri = workerUri },
                position = new
                {
                    line = 3,
                    character = "Private Sub publisher_".Length
                }
            });

        var result = prepare.GetProperty("result");
        Assert.Equal("Changed", result.GetProperty("placeholder").GetString());
        Assert.Equal(
            (
                Start: "Private Sub publisher_".Length,
                End: "Private Sub publisher_Changed".Length
            ),
            (
                Start: result.GetProperty("range").GetProperty("start")
                    .GetProperty("character").GetInt32(),
                End: result.GetProperty("range").GetProperty("end")
                    .GetProperty("character").GetInt32()
            ));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Handler_prefix_Prepare_Rename_selects_the_variable_but_separator_and_complete_occurrence_do_not()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Changed()",
            "End Sub",
            "Private Sub Invoke()",
            "    publisher_Changed",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var prefixPrepare = await process.SendRequestAsync(
            2,
            "textDocument/prepareRename",
            new
            {
                textDocument = new { uri = workerUri },
                position = new
                {
                    line = 3,
                    character = "Private Sub ".Length
                }
            });
        var prefixResult = prefixPrepare.GetProperty("result");
        Assert.Equal(
            "publisher",
            prefixResult.GetProperty("placeholder").GetString());
        Assert.Equal(
            (
                Start: "Private Sub ".Length,
                End: "Private Sub publisher".Length
            ),
            (
                Start: prefixResult.GetProperty("range").GetProperty("start")
                    .GetProperty("character").GetInt32(),
                End: prefixResult.GetProperty("range").GetProperty("end")
                    .GetProperty("character").GetInt32()
            ));

        var separatorPrepare = await process.SendRequestAsync(
            3,
            "textDocument/prepareRename",
            new
            {
                textDocument = new { uri = workerUri },
                position = new
                {
                    line = 3,
                    character = "Private Sub publisher".Length
                }
            });
        Assert.False(separatorPrepare.TryGetProperty("error", out _));
        Assert.Equal(
            JsonValueKind.Null,
            separatorPrepare.GetProperty("result").ValueKind);

        var completeOccurrencePrepare = await process.SendRequestAsync(
            4,
            "textDocument/prepareRename",
            new
            {
                textDocument = new { uri = workerUri },
                position = new { line = 6, character = "    ".Length }
            });
        Assert.False(completeOccurrencePrepare.TryGetProperty("error", out _));
        Assert.Equal(
            JsonValueKind.Null,
            completeOccurrencePrepare.GetProperty("result").ValueKind);

        await process.ShutdownAsync(5);
    }

    [Fact]
    public async Task Handler_navigation_preserves_segment_and_complete_definition_identities()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        var publisherText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Publisher\"",
            "Public Event Changed()",
            "Private Sub Fire()",
            "    RaiseEvent Changed",
            "End Sub"
        ]);
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Changed()",
            "End Sub",
            "Private Sub Invoke()",
            "    Set publisher = Nothing",
            "    publisher_Changed",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var prefixDefinition = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher_Changed",
            offset: 0);
        Assert.Equal(
            (
                Uri: workerUri,
                Line: 2,
                Start: "Private WithEvents ".Length,
                End: "Private WithEvents publisher".Length
            ),
            GetLocation(prefixDefinition.GetProperty("result")));

        var suffixDefinition = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/definition",
            workerUri,
            workerText,
            "publisher_Changed",
            offset: "publisher_".Length);
        Assert.Equal(
            (
                Uri: publisherUri,
                Line: 2,
                Start: "Public Event ".Length,
                End: "Public Event Changed".Length
            ),
            GetLocation(suffixDefinition.GetProperty("result")));

        var completeDefinition = await SendPositionRequestAsync(
            process,
            4,
            "textDocument/definition",
            workerUri,
            workerText,
            "    publisher_Changed",
            offset: "    ".Length);
        Assert.Equal(
            (
                Uri: workerUri,
                Line: 3,
                Start: "Private Sub ".Length,
                End: "Private Sub publisher_Changed".Length
            ),
            GetLocation(completeDefinition.GetProperty("result")));

        var prefixReferences = await SendPositionRequestAsync(
            process,
            5,
            "textDocument/references",
            workerUri,
            workerText,
            "publisher_Changed",
            offset: 0,
            includeDeclaration: true);
        Assert.Equal(
            [
                (
                    Uri: workerUri,
                    Line: 2,
                    Start: "Private WithEvents ".Length,
                    End: "Private WithEvents publisher".Length
                ),
                (
                    Uri: workerUri,
                    Line: 3,
                    Start: "Private Sub ".Length,
                    End: "Private Sub publisher".Length
                ),
                (
                    Uri: workerUri,
                    Line: 6,
                    Start: "    Set ".Length,
                    End: "    Set publisher".Length
                )
            ],
            prefixReferences.GetProperty("result").EnumerateArray()
                .Select(GetLocation));

        var suffixReferences = await SendPositionRequestAsync(
            process,
            6,
            "textDocument/references",
            workerUri,
            workerText,
            "publisher_Changed",
            offset: "publisher_".Length,
            includeDeclaration: true);
        Assert.Equal(
            [
                (
                    Uri: publisherUri,
                    Line: 2,
                    Start: "Public Event ".Length,
                    End: "Public Event Changed".Length
                ),
                (
                    Uri: publisherUri,
                    Line: 4,
                    Start: "    RaiseEvent ".Length,
                    End: "    RaiseEvent Changed".Length
                ),
                (
                    Uri: workerUri,
                    Line: 3,
                    Start: "Private Sub publisher_".Length,
                    End: "Private Sub publisher_Changed".Length
                )
            ],
            suffixReferences.GetProperty("result").EnumerateArray()
                .Select(GetLocation));

        var completeReferences = await SendPositionRequestAsync(
            process,
            7,
            "textDocument/references",
            workerUri,
            workerText,
            "    publisher_Changed",
            offset: "    ".Length,
            includeDeclaration: true);
        Assert.Equal(
            [
                (
                    Uri: workerUri,
                    Line: 3,
                    Start: "Private Sub ".Length,
                    End: "Private Sub publisher_Changed".Length
                ),
                (
                    Uri: workerUri,
                    Line: 7,
                    Start: "    ".Length,
                    End: "    publisher_Changed".Length
                )
            ],
            completeReferences.GetProperty("result").EnumerateArray()
                .Select(GetLocation));

        await process.ShutdownAsync(8);

        static (string? Uri, int Line, int Start, int End) GetLocation(
            JsonElement location)
            => (
                location.GetProperty("uri").GetString(),
                location.GetProperty("range").GetProperty("start")
                    .GetProperty("line").GetInt32(),
                location.GetProperty("range").GetProperty("start")
                    .GetProperty("character").GetInt32(),
                location.GetProperty("range").GetProperty("end")
                    .GetProperty("character").GetInt32());
    }

    [Fact]
    public async Task Nonconvergent_handler_suffix_has_no_Prepare_Rename_target()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string firstPublisherUri = "file:///C:/work/FirstPublisher.cls";
        const string firstPublisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "FirstPublisher"
            Public Event Changed()
            """;
        const string secondPublisherUri = "file:///C:/work/SecondPublisher.cls";
        const string secondPublisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "SecondPublisher"
            Public Event Changed()
            """;
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "#If FIRST_CONFIGURATION Then",
            "Private WithEvents source As FirstPublisher",
            "#Else",
            "Private WithEvents source As SecondPublisher",
            "#End If",
            "Private Sub source_Changed()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(firstPublisherUri, firstPublisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(secondPublisherUri, secondPublisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var prepare = await process.SendRequestAsync(
            2,
            "textDocument/prepareRename",
            new
            {
                textDocument = new { uri = workerUri },
                position = new
                {
                    line = 7,
                    character = "Private Sub source_".Length
                }
            });

        Assert.False(prepare.TryGetProperty("error", out _));
        Assert.Equal(JsonValueKind.Null, prepare.GetProperty("result").ValueKind);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task WithEvents_variable_Rename_reports_a_derived_handler_collision()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Changed()",
            "End Sub",
            "Private Sub source_Changed()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = workerUri },
                position = new
                {
                    line = 2,
                    character = "Private WithEvents ".Length
                },
                newName = "source"
            });

        Assert.False(rename.TryGetProperty("result", out _));
        var error = rename.GetProperty("error");
        Assert.Equal(
            "sameScopeCollision",
            error.GetProperty("data").GetProperty("reason").GetString());
        var conflict = Assert.Single(
            error.GetProperty("data").GetProperty("conflicts")
                .EnumerateArray());
        Assert.Equal(5, conflict.GetProperty("range").GetProperty("start")
            .GetProperty("line").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Event_Rename_fails_closed_for_incomplete_conditional_handler_coverage()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "#If INCOMPLETE_CONFIGURATION Then",
            "Private Sub publisher_Changed()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = publisherUri },
                position = new
                {
                    line = 2,
                    character = "Public Event ".Length
                },
                newName = "Updated"
            });

        Assert.False(rename.TryGetProperty("result", out _));
        Assert.Equal(
            "analysisIncomplete",
            rename.GetProperty("error").GetProperty("data")
                .GetProperty("reason").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Event_Rename_fails_closed_for_an_unlinked_indeterminate_handler_candidate()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string firstPublisherUri = "file:///C:/work/FirstPublisher.cls";
        const string firstPublisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        const string secondPublisherUri = "file:///C:/work/SecondPublisher.cls";
        const string secondPublisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        const string workerUri = "file:///C:/work/Worker.cls";
        const string workerText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Worker"
            Private WithEvents publisher As Publisher
            Private Sub publisher_Changed()
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(firstPublisherUri, firstPublisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(secondPublisherUri, secondPublisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = firstPublisherUri },
                position = new
                {
                    line = 2,
                    character = "Public Event ".Length
                },
                newName = "Updated"
            });

        Assert.False(rename.TryGetProperty("result", out _));
        Assert.Equal(
            "analysisIncomplete",
            rename.GetProperty("error").GetProperty("data")
                .GetProperty("reason").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Event_Rename_ignores_an_unrelated_indeterminate_handler_candidate()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        const string workerUri = "file:///C:/work/Worker.cls";
        const string workerText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Worker"
            Private WithEvents missing As MissingPublisher
            Private Sub missing_Changed()
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = publisherUri },
                position = new
                {
                    line = 2,
                    character = "Public Event ".Length
                },
                newName = "Updated"
            });

        Assert.False(
            rename.TryGetProperty("error", out var renameError),
            renameError.ToString());
        var changes = rename.GetProperty("result").GetProperty("changes");
        Assert.Equal(
            [(Line: 2, NewText: "Updated")],
            changes.GetProperty(publisherUri).EnumerateArray().Select(edit => (
                Line: edit.GetProperty("range").GetProperty("start")
                    .GetProperty("line").GetInt32(),
                NewText: edit.GetProperty("newText").GetString())));
        Assert.False(changes.TryGetProperty(workerUri, out _));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Source_Event_Rename_ignores_an_indeterminate_qualified_external_candidate_with_the_same_type_name()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-rename-qualified-external-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-rename-qualified-external-cache-").FullName;
        try
        {
            const string referenceName = "Generated Library";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            var knownEvent = new TypeLibCatalogMember(
                "Changed",
                VbaSourceDefinitionKind.Procedure,
                Documentation: null,
                new VbaCallableSignature(
                    "Sub Changed()",
                    [],
                    CallableKind: VbaCallableKind.Sub),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: 0));
            var incompleteEvent = new TypeLibCatalogMember(
                "Broken",
                VbaSourceDefinitionKind.Procedure,
                Documentation: null,
                new VbaCallableSignature(
                    "Sub Broken(value)",
                    [new VbaCallableParameter("value")],
                    CallableKind: VbaCallableKind.Sub),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 2,
                    FunctionFlags: 0));
            var catalog = TypeLibReferenceCatalogBuilder.Build(
                referenceName,
                new TypeLibCatalogMetadata(
                    "Generated",
                    [
                        new TypeLibCatalogType(
                            "Publisher",
                            VbaSourceDefinitionKind.Class,
                            Documentation: null,
                            Members: [],
                            IsCreatable: true,
                            Metadata: new TypeLibCatalogTypeMetadata(
                                TypeLibCatalogRawTypeKind.CoClass,
                                TypeFlags: 0,
                                ImplementedInterfaces:
                                [
                                    new TypeLibCatalogImplementedInterface(
                                        "_PublisherEvents",
                                        TypeFlags: 0,
                                        ImplementationFlags: 0x1 | 0x2,
                                        CallableMembers:
                                            [knownEvent, incompleteEvent],
                                        RawTypeKind:
                                            TypeLibCatalogRawTypeKind.Dispatch)
                                ]))
                    ]));
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    catalog));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();

            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var publisherPath = Path.Combine(sourceRoot, "Publisher.cls");
            var publisherUri = new Uri(publisherPath).AbsoluteUri;
            const string publisherText = """
                VERSION 1.0 CLASS
                Attribute VB_Name = "Publisher"
                Public Event Changed()
                """;
            File.WriteAllText(publisherPath, publisherText);
            var workerPath = Path.Combine(sourceRoot, "Worker.cls");
            var workerUri = new Uri(workerPath).AbsoluteUri;
            const string workerText = """
                VERSION 1.0 CLASS
                Attribute VB_Name = "Worker"
                Private WithEvents external As Generated.Publisher
                Private Sub external_Changed()
                End Sub
                """;
            File.WriteAllText(workerPath, workerText);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(publisherUri, publisherText));
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(workerUri, workerText));
            await process.WaitForDiagnosticsAsync(workerUri);
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri = workerUri, version = 2 },
                    contentChanges = new[] { new { text = workerText } }
                });
            await process.WaitForDiagnosticsAsync(workerUri);

            var rename = await process.SendRequestAsync(
                2,
                "textDocument/rename",
                new
                {
                    textDocument = new { uri = publisherUri },
                    position = new
                    {
                        line = 2,
                        character = "Public Event ".Length
                    },
                    newName = "Updated"
                });

            Assert.False(
                rename.TryGetProperty("error", out var renameError),
                renameError.ToString());
            var changes = rename.GetProperty("result").GetProperty("changes");
            Assert.Equal(
                [(Line: 2, NewText: "Updated")],
                changes.GetProperty(publisherUri).EnumerateArray().Select(edit => (
                    Line: edit.GetProperty("range").GetProperty("start")
                        .GetProperty("line").GetInt32(),
                    NewText: edit.GetProperty("newText").GetString())));
            Assert.False(changes.TryGetProperty(workerUri, out _));

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Event_Rename_rejects_a_conclusively_mixed_conditional_dependent_family()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "#If HANDLER_CONFIGURATION Then",
            "Private Sub publisher_Changed()",
            "End Sub",
            "#Else",
            "Private publisher_Changed As Long",
            "#End If"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = publisherUri },
                position = new
                {
                    line = 2,
                    character = "Public Event ".Length
                },
                newName = "Updated"
            });

        Assert.False(rename.TryGetProperty("result", out _));
        Assert.Equal(
            "resolutionChanged",
            rename.GetProperty("error").GetProperty("data")
                .GetProperty("reason").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Event_Rename_prioritizes_conclusive_mixed_coverage_over_indeterminate_evidence()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "#If VARIABLE_CONFIGURATION Then",
            "Private WithEvents publisher As Publisher",
            "#Else",
            "Private WithEvents publisher As MissingPublisher",
            "#End If",
            "#If HANDLER_CONFIGURATION Then",
            "Private Sub publisher_Changed()",
            "End Sub",
            "#Else",
            "Private publisher_Changed As Long",
            "#End If"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = publisherUri },
                position = new
                {
                    line = 2,
                    character = "Public Event ".Length
                },
                newName = "Updated"
            });

        Assert.False(rename.TryGetProperty("result", out _));
        Assert.Equal(
            "resolutionChanged",
            rename.GetProperty("error").GetProperty("data")
                .GetProperty("reason").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task WithEvents_variable_Rename_leaves_a_conclusively_ordinary_procedure_unchanged()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Changed()",
            "End Sub",
            "Private Sub publisher_NotAnEvent()",
            "End Sub",
            "Private Sub Invoke()",
            "    Set publisher = New Publisher",
            "    publisher_Changed",
            "    publisher_NotAnEvent",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = workerUri },
                position = new
                {
                    line = 2,
                    character = "Private WithEvents ".Length
                },
                newName = "source"
            });

        Assert.False(
            rename.TryGetProperty("error", out var renameError),
            renameError.ToString());
        var changes = rename.GetProperty("result").GetProperty("changes");
        Assert.Equal(
            [
                (Line: 2, NewText: "source"),
                (Line: 3, NewText: "source"),
                (Line: 8, NewText: "source"),
                (Line: 9, NewText: "source_Changed")
            ],
            changes.GetProperty(workerUri).EnumerateArray().Select(edit => (
                Line: edit.GetProperty("range").GetProperty("start")
                    .GetProperty("line").GetInt32(),
                NewText: edit.GetProperty("newText").GetString())));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task WithEvents_variable_Rename_rejects_a_newly_bound_derived_name_occurrence()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Changed()",
            "End Sub",
            "Private Sub Invoke()",
            "    source_Changed",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = workerUri },
                position = new
                {
                    line = 2,
                    character = "Private WithEvents ".Length
                },
                newName = "source"
            });

        Assert.False(rename.TryGetProperty("result", out _));
        Assert.Equal(
            "resolutionChanged",
            rename.GetProperty("error").GetProperty("data")
                .GetProperty("reason").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task WithEvents_variable_Rename_rejects_a_newly_created_non_target_handler_association()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        var publisherText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Publisher\"",
            "Public Event Changed()",
            "Public Event Other()"
        ]);
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Sub publisher_Changed()",
            "End Sub",
            "Private Sub source_Other()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = workerUri },
                position = new
                {
                    line = 2,
                    character = "Private WithEvents ".Length
                },
                newName = "source"
            });

        Assert.False(rename.TryGetProperty("result", out _));
        Assert.Equal(
            "resolutionChanged",
            rename.GetProperty("error").GetProperty("data")
                .GetProperty("reason").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Source_Event_Rename_rejects_a_newly_created_non_target_handler_association()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        const string workerUri = "file:///C:/work/Worker.cls";
        const string workerText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Worker"
            Private WithEvents publisher As Publisher
            Private WithEvents other As Publisher
            Private Sub publisher_Changed()
            End Sub
            Private Sub other_Updated()
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = publisherUri },
                position = new
                {
                    line = 2,
                    character = "Public Event ".Length
                },
                newName = "Updated"
            });

        Assert.False(rename.TryGetProperty("result", out _));
        Assert.Equal(
            "resolutionChanged",
            rename.GetProperty("error").GetProperty("data")
                .GetProperty("reason").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Interface_member_Rename_rejects_detaching_an_overlapping_WithEvents_handler()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/IContract.cls";
        var interfaceText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"IContract\"",
            "Public Sub Changed()",
            "End Sub"
        ]);
        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Implements IContract",
            "Private WithEvents IContract As Publisher",
            "Private Sub IContract_Changed()",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = interfaceUri },
                position = new
                {
                    line = 2,
                    character = "Public Sub ".Length
                },
                newName = "Updated"
            });

        Assert.False(rename.TryGetProperty("result", out _));
        Assert.Equal(
            "resolutionChanged",
            rename.GetProperty("error").GetProperty("data")
                .GetProperty("reason").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Mixed_handler_family_cannot_initiate_independent_Rename()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "#If HANDLER_CONFIGURATION Then",
            "Private Sub publisher_Changed()",
            "End Sub",
            "#Else",
            "Private publisher_Changed As Long",
            "#End If",
            "Private Sub Invoke()",
            "    publisher_Changed",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = workerUri },
                position = new
                {
                    line = 10,
                    character = "    ".Length
                },
                newName = "Detached"
            });

        Assert.False(rename.TryGetProperty("result", out _));
        Assert.Equal(
            "notRenameTarget",
            rename.GetProperty("error").GetProperty("data")
                .GetProperty("reason").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task TypeLib_WithEvents_variable_Rename_preserves_the_external_Event_association()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-rename-typelib-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-rename-typelib-cache-").FullName;
        try
        {
            const string referenceName = "Generated Library";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    CreateGeneratedTypeLibEventCatalog(referenceName)));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();

            var sourcePath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.cls");
            var uri = new Uri(sourcePath).AbsoluteUri;
            var text = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Private WithEvents publisher As Publisher",
                "Private Function publisher_Changed(ByVal value As Long) As Boolean",
                "End Function",
                "Private Sub Invoke()",
                "    Set publisher = Nothing",
                "    publisher_Changed 1",
                "End Sub"
            ]);
            File.WriteAllText(sourcePath, text);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            var diagnosticsBeforeRename =
                await process.WaitForDiagnosticsAsync(uri);
            Assert.DoesNotContain(
                diagnosticsBeforeRename.GetProperty("params")
                    .GetProperty("diagnostics").EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.eventHandlerMustBeSub");

            var rename = await process.SendRequestAsync(
                2,
                "textDocument/rename",
                new
                {
                    textDocument = new { uri },
                    position = new
                    {
                        line = 1,
                        character = "Private WithEvents ".Length
                    },
                    newName = "source"
                });

            Assert.False(
                rename.TryGetProperty("error", out var renameError),
                renameError.ToString());
            Assert.Equal(
                [
                    (Line: 1, NewText: "source"),
                    (Line: 2, NewText: "source"),
                    (Line: 5, NewText: "source"),
                    (Line: 6, NewText: "source_Changed")
                ],
                rename.GetProperty("result").GetProperty("changes")
                    .GetProperty(uri).EnumerateArray().Select(edit => (
                        Line: edit.GetProperty("range").GetProperty("start")
                            .GetProperty("line").GetInt32(),
                        NewText: edit.GetProperty("newText").GetString())));

            var renamedText = text.Replace(
                "publisher",
                "source",
                StringComparison.Ordinal);
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 3 },
                    contentChanges = new[] { new { text = renamedText } }
                });
            var diagnosticsAfterRename =
                await process.WaitForDiagnosticsAsync(uri);
            Assert.DoesNotContain(
                diagnosticsAfterRename.GetProperty("params")
                    .GetProperty("diagnostics").EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.eventHandlerMustBeSub");

            await process.ShutdownAsync(4);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("current")]
    [InlineData("lastKnownGood")]
    public async Task Host_WithEvents_variable_Rename_preserves_the_projected_Event_association(
        string authority)
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-rename-host-").FullName;
        try
        {
            WriteReferenceCatalogProjectManifest(projectRoot);
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var sourceTemplate = Path.Combine(sourceRoot, "Book1.xlsm");

            var publisherPath = Path.Combine(sourceRoot, "Publisher.frm");
            var publisherUri = new Uri(publisherPath).AbsoluteUri;
            var publisherText = string.Join('\n', [
                "VERSION 5.00",
                "Begin VB.Form Publisher",
                "End",
                "Attribute VB_Name = \"Publisher\""
            ]);
            File.WriteAllText(publisherPath, publisherText);

            var workerPath = Path.Combine(sourceRoot, "Worker.cls");
            var workerUri = new Uri(workerPath).AbsoluteUri;
            var workerText = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Private WithEvents publisher As Publisher",
                "Private Function publisher_Change() As Boolean",
                "End Function",
                "Private Sub Invoke()",
                "    Set publisher = Nothing",
                "    publisher_Change",
                "End Sub"
            ]);
            File.WriteAllText(workerPath, workerText);

            await using var process = await LanguageServerProcessHarness.StartAsync();
            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(publisherUri, publisherText));
            await process.WaitForDiagnosticsAsync(publisherUri);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(workerUri, workerText));
            await process.WaitForDiagnosticsAsync(workerUri);
            await process.SendNotificationAsync(
                "vba/hostClassProjectionSnapshot",
                CreateHostProjectionSnapshot(
                    projectRoot,
                    sourceTemplate,
                    "Publisher",
                    "Change",
                    authority));
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri = workerUri, version = 2 },
                    contentChanges = new[] { new { text = workerText } }
                });
            var diagnosticsBeforeRename =
                await process.WaitForDiagnosticsAsync(workerUri);
            Assert.Equal(
                authority == "current",
                diagnosticsBeforeRename.GetProperty("params")
                    .GetProperty("diagnostics").EnumerateArray().Any(
                        diagnostic => diagnostic.GetProperty("code").GetString()
                            == "validation.eventHandlerMustBeSub"));

            var rename = await process.SendRequestAsync(
                2,
                "textDocument/rename",
                new
                {
                    textDocument = new { uri = workerUri },
                    position = new
                    {
                        line = 2,
                        character = "Private WithEvents ".Length
                    },
                    newName = "source"
                });

            Assert.False(
                rename.TryGetProperty("error", out var renameError),
                renameError.ToString());
            Assert.Equal(
                [
                    (Line: 2, NewText: "source"),
                    (Line: 3, NewText: "source"),
                    (Line: 6, NewText: "source"),
                    (Line: 7, NewText: "source_Change")
                ],
                rename.GetProperty("result").GetProperty("changes")
                    .GetProperty(workerUri).EnumerateArray().Select(edit => (
                        Line: edit.GetProperty("range").GetProperty("start")
                            .GetProperty("line").GetInt32(),
                        NewText: edit.GetProperty("newText").GetString())));

            var renamedWorkerText = workerText.Replace(
                "publisher",
                "source",
                StringComparison.Ordinal);
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri = workerUri, version = 3 },
                    contentChanges = new[] { new { text = renamedWorkerText } }
                });
            var diagnosticsAfterRename =
                await process.WaitForDiagnosticsAsync(workerUri);
            Assert.Equal(
                authority == "current",
                diagnosticsAfterRename.GetProperty("params")
                    .GetProperty("diagnostics").EnumerateArray().Any(
                        diagnostic => diagnostic.GetProperty("code").GetString()
                            == "validation.eventHandlerMustBeSub"));

            await process.ShutdownAsync(4);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Interface_member_Rename_rejects_detaching_an_overlapping_TypeLib_Event_handler()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-rename-overlap-typelib-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-rename-overlap-typelib-cache-").FullName;
        try
        {
            const string referenceName = "Generated Library";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    CreateGeneratedTypeLibEventCatalog(referenceName)));

            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var interfacePath = Path.Combine(sourceRoot, "IContract.cls");
            var interfaceUri = new Uri(interfacePath).AbsoluteUri;
            var interfaceText = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"IContract\"",
                "Public Sub Changed(ByVal value As Long)",
                "End Sub"
            ]);
            File.WriteAllText(interfacePath, interfaceText);

            var workerPath = Path.Combine(sourceRoot, "Worker.cls");
            var workerUri = new Uri(workerPath).AbsoluteUri;
            var workerText = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Implements IContract",
                "Private WithEvents IContract As Publisher",
                "Private Sub IContract_Changed(ByVal value As Long)",
                "End Sub"
            ]);
            File.WriteAllText(workerPath, workerText);

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(interfaceUri, interfaceText));
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(workerUri, workerText));
            await process.WaitForDiagnosticsAsync(workerUri);
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri = workerUri, version = 2 },
                    contentChanges = new[] { new { text = workerText } }
                });
            await process.WaitForDiagnosticsAsync(workerUri);

            var rename = await process.SendRequestAsync(
                2,
                "textDocument/rename",
                new
                {
                    textDocument = new { uri = interfaceUri },
                    position = new
                    {
                        line = 2,
                        character = "Public Sub ".Length
                    },
                    newName = "Updated"
                });

            Assert.False(rename.TryGetProperty("result", out _));
            Assert.Equal(
                "resolutionChanged",
                rename.GetProperty("error").GetProperty("data")
                    .GetProperty("reason").GetString());

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task WithEvents_variable_Rename_updates_every_callable_kind_in_a_complete_conditional_family()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "#If VBA7 Then",
            "Private Sub publisher_Changed()",
            "End Sub",
            "#Else",
            "Private Function publisher_Changed() As Boolean",
            "End Function",
            "#End If",
            "Private Sub Invoke()",
            "    publisher_Changed",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = workerUri },
                position = new
                {
                    line = 2,
                    character = "Private WithEvents ".Length
                },
                newName = "source"
            });

        Assert.False(
            rename.TryGetProperty("error", out var renameError),
            renameError.ToString());
        Assert.Equal(
            [
                (Line: 2, NewText: "source"),
                (Line: 4, NewText: "source"),
                (Line: 7, NewText: "source"),
                (Line: 11, NewText: "source_Changed")
            ],
            rename.GetProperty("result").GetProperty("changes")
                .GetProperty(workerUri).EnumerateArray().Select(edit => (
                    Line: edit.GetProperty("range").GetProperty("start")
                        .GetProperty("line").GetInt32(),
                    NewText: edit.GetProperty("newText").GetString())));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task WithEvents_variable_Rename_updates_every_Property_accessor_and_complete_name_reference()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Property Get publisher_Changed() As Object",
            "    Set publisher_Changed = Nothing",
            "End Property",
            "Private Property Let publisher_Changed(ByVal value As Long)",
            "End Property",
            "Private Property Set publisher_Changed(ByVal value As Object)",
            "End Property",
            "Private Sub Invoke()",
            "    Dim value As Object",
            "    Set publisher = Nothing",
            "    Set value = publisher_Changed",
            "    publisher_Changed = 1",
            "    Set publisher_Changed = value",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));
        var initialDiagnostics = await process.WaitForDiagnosticsAsync(workerUri);
        Assert.Equal(
            [3, 6, 8],
            initialDiagnostics.GetProperty("params")
                .GetProperty("diagnostics").EnumerateArray()
                .Where(diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.eventHandlerMustBeSub")
                .Select(diagnostic => diagnostic.GetProperty("range")
                    .GetProperty("start").GetProperty("line").GetInt32()));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = workerUri },
                position = new
                {
                    line = 2,
                    character = "Private WithEvents ".Length
                },
                newName = "source"
            });

        Assert.False(
            rename.TryGetProperty("error", out var renameError),
            renameError.ToString());
        Assert.Equal(
            [
                (Line: 2, NewText: "source"),
                (Line: 3, NewText: "source"),
                (Line: 4, NewText: "source_Changed"),
                (Line: 6, NewText: "source"),
                (Line: 8, NewText: "source"),
                (Line: 12, NewText: "source"),
                (Line: 13, NewText: "source_Changed"),
                (Line: 14, NewText: "source_Changed"),
                (Line: 15, NewText: "source_Changed")
            ],
            rename.GetProperty("result").GetProperty("changes")
                .GetProperty(workerUri).EnumerateArray().Select(edit => (
                    Line: edit.GetProperty("range").GetProperty("start")
                        .GetProperty("line").GetInt32(),
                    NewText: edit.GetProperty("newText").GetString())));

        var renamedWorkerText = workerText.Replace(
            "publisher",
            "source",
            StringComparison.Ordinal);
        await process.SendNotificationAsync(
            "textDocument/didChange",
            new
            {
                textDocument = new { uri = workerUri, version = 2 },
                contentChanges = new[] { new { text = renamedWorkerText } }
            });
        var renamedDiagnostics = await process.WaitForDiagnosticsAsync(workerUri);
        Assert.Equal(
            [3, 6, 8],
            renamedDiagnostics.GetProperty("params")
                .GetProperty("diagnostics").EnumerateArray()
                .Where(diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.eventHandlerMustBeSub")
                .Select(diagnostic => diagnostic.GetProperty("range")
                    .GetProperty("start").GetProperty("line").GetInt32()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Source_Event_Rename_updates_every_Property_accessor_and_complete_name_reference()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        var publisherText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Publisher\"",
            "Public Event Changed()"
        ]);
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private Property Get publisher_Changed() As Object",
            "    Set publisher_Changed = Nothing",
            "End Property",
            "Private Property Let publisher_Changed(ByVal value As Long)",
            "End Property",
            "Private Property Set publisher_Changed(ByVal value As Object)",
            "End Property",
            "Private Sub Invoke()",
            "    Dim value As Object",
            "    Set value = publisher_Changed",
            "    publisher_Changed = 1",
            "    Set publisher_Changed = value",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = publisherUri },
                position = new
                {
                    line = 2,
                    character = "Public Event ".Length
                },
                newName = "Updated"
            });

        Assert.False(
            rename.TryGetProperty("error", out var renameError),
            renameError.ToString());
        Assert.Equal(
            [(Line: 2, NewText: "Updated")],
            rename.GetProperty("result").GetProperty("changes")
                .GetProperty(publisherUri).EnumerateArray().Select(edit => (
                    Line: edit.GetProperty("range").GetProperty("start")
                        .GetProperty("line").GetInt32(),
                    NewText: edit.GetProperty("newText").GetString())));
        Assert.Equal(
            [
                (Line: 3, NewText: "Updated"),
                (Line: 4, NewText: "publisher_Updated"),
                (Line: 6, NewText: "Updated"),
                (Line: 8, NewText: "Updated"),
                (Line: 12, NewText: "publisher_Updated"),
                (Line: 13, NewText: "publisher_Updated"),
                (Line: 14, NewText: "publisher_Updated")
            ],
            rename.GetProperty("result").GetProperty("changes")
                .GetProperty(workerUri).EnumerateArray().Select(edit => (
                    Line: edit.GetProperty("range").GetProperty("start")
                        .GetProperty("line").GetInt32(),
                    NewText: edit.GetProperty("newText").GetString())));

        var renamedPublisherText = publisherText.Replace(
            "Changed",
            "Updated",
            StringComparison.Ordinal);
        var renamedWorkerText = workerText.Replace(
            "publisher_Changed",
            "publisher_Updated",
            StringComparison.Ordinal);
        await process.SendNotificationAsync(
            "textDocument/didChange",
            new
            {
                textDocument = new { uri = publisherUri, version = 2 },
                contentChanges = new[] { new { text = renamedPublisherText } }
            });
        await process.SendNotificationAsync(
            "textDocument/didChange",
            new
            {
                textDocument = new { uri = workerUri, version = 2 },
                contentChanges = new[] { new { text = renamedWorkerText } }
            });
        var renamedDiagnostics = await process.WaitForDiagnosticsAsync(workerUri);
        Assert.Equal(
            [3, 6, 8],
            renamedDiagnostics.GetProperty("params")
                .GetProperty("diagnostics").EnumerateArray()
                .Where(diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.eventHandlerMustBeSub")
                .Select(diagnostic => diagnostic.GetProperty("range")
                    .GetProperty("start").GetProperty("line").GetInt32()));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Recovered_WithEvents_variable_Rename_creates_no_dependent_handler_edits()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents source As",
            "Private Sub source_Changed()",
            "End Sub",
            "Private Sub Invoke()",
            "    Set source = Nothing",
            "    source_Changed",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = workerUri },
                position = new
                {
                    line = 2,
                    character = "Private WithEvents ".Length
                },
                newName = "publisher"
            });

        Assert.False(
            rename.TryGetProperty("error", out var renameError),
            renameError.ToString());
        Assert.Equal(
            [(Line: 2, NewText: "publisher"), (Line: 6, NewText: "publisher")],
            rename.GetProperty("result").GetProperty("changes")
                .GetProperty(workerUri).EnumerateArray().Select(edit => (
                    Line: edit.GetProperty("range").GetProperty("start")
                        .GetProperty("line").GetInt32(),
                    NewText: edit.GetProperty("newText").GetString())));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Invalid_type_WithEvents_variable_Rename_creates_no_dependent_handler_edits()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents source As Long",
            "Private Sub source_Changed()",
            "End Sub",
            "Private Sub Invoke()",
            "    source = 1",
            "    source_Changed",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = workerUri },
                position = new
                {
                    line = 2,
                    character = "Private WithEvents ".Length
                },
                newName = "value"
            });

        Assert.False(
            rename.TryGetProperty("error", out var renameError),
            renameError.ToString());
        Assert.Equal(
            [(Line: 2, NewText: "value"), (Line: 6, NewText: "value")],
            rename.GetProperty("result").GetProperty("changes")
                .GetProperty(workerUri).EnumerateArray().Select(edit => (
                    Line: edit.GetProperty("range").GetProperty("start")
                        .GetProperty("line").GetInt32(),
                    NewText: edit.GetProperty("newText").GetString())));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Eligible_conditional_variable_sibling_establishes_family_wide_dependent_Rename()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string publisherText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Publisher"
            Public Event Changed()
            """;
        const string workerUri = "file:///C:/work/Worker.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "#If VBA7 Then",
            "Private WithEvents source As Publisher",
            "#Else",
            "Private WithEvents source As Long",
            "#End If",
            "Private Sub source_Changed()",
            "End Sub",
            "Private Sub Invoke()",
            "    Set source = Nothing",
            "    source_Changed",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(publisherUri, publisherText));
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(workerUri, workerText));

        var rename = await process.SendRequestAsync(
            2,
            "textDocument/rename",
            new
            {
                textDocument = new { uri = workerUri },
                position = new
                {
                    line = 3,
                    character = "Private WithEvents ".Length
                },
                newName = "publisher"
            });

        Assert.False(
            rename.TryGetProperty("error", out var renameError),
            renameError.ToString());
        Assert.Equal(
            [
                (Line: 3, NewText: "publisher"),
                (Line: 5, NewText: "publisher"),
                (Line: 7, NewText: "publisher"),
                (Line: 10, NewText: "publisher"),
                (Line: 11, NewText: "publisher_Changed")
            ],
            rename.GetProperty("result").GetProperty("changes")
                .GetProperty(workerUri).EnumerateArray().Select(edit => (
                    Line: edit.GetProperty("range").GetProperty("start")
                        .GetProperty("line").GetInt32(),
                    NewText: edit.GetProperty("newText").GetString())));

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Source_Event_Rename_fails_closed_for_a_shared_TypeLib_Event_association()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-rename-shared-typelib-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-withevents-rename-shared-typelib-cache-").FullName;
        try
        {
            const string referenceName = "Generated Library";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    CreateGeneratedTypeLibEventCatalog(referenceName)));

            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            var publisherPath = Path.Combine(sourceRoot, "Publisher.cls");
            var publisherUri = new Uri(publisherPath).AbsoluteUri;
            var publisherText = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Publisher\"",
                "Public Event Changed(ByVal value As Long)"
            ]);
            File.WriteAllText(publisherPath, publisherText);

            var workerPath = Path.Combine(sourceRoot, "Worker.cls");
            var workerUri = new Uri(workerPath).AbsoluteUri;
            var workerText = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "#If SOURCE_CONFIGURATION Then",
                "Private WithEvents publisher As Publisher",
                "#Else",
                "Private WithEvents publisher As Generated.Publisher",
                "#End If",
                "Private Sub publisher_Changed(ByVal value As Long)",
                "End Sub"
            ]);
            File.WriteAllText(workerPath, workerText);

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(publisherUri, publisherText));
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(workerUri, workerText));
            await process.WaitForDiagnosticsAsync(workerUri);
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri = workerUri, version = 2 },
                    contentChanges = new[] { new { text = workerText } }
                });
            await process.WaitForDiagnosticsAsync(workerUri);

            var rename = await process.SendRequestAsync(
                2,
                "textDocument/rename",
                new
                {
                    textDocument = new { uri = publisherUri },
                    position = new
                    {
                        line = 2,
                        character = "Public Event ".Length
                    },
                    newName = "Updated"
                });

            Assert.False(rename.TryGetProperty("result", out _));
            Assert.Equal(
                "resolutionChanged",
                rename.GetProperty("error").GetProperty("data")
                    .GetProperty("reason").GetString());

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
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

    private static void WriteReferenceCatalogProjectManifest(
        string projectRoot,
        params string[] referenceNames)
    {
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
        var references = referenceNames
            .Select(referenceName => new { name = referenceName, requested = true })
            .ToArray();
        var manifest = new
        {
            schemaVersion = 1,
            projectName = "WithEventsReferenceCatalogProject",
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
            JsonSerializer.Serialize(manifest));
    }

    private static VbaProjectReferenceCatalogIdentity CreateGeneratedReferenceCatalogIdentity(
        string referenceName,
        string libraryGuid = "{33333333-3333-3333-3333-333333333333}")
        => new(
            referenceName,
            libraryGuid,
            1,
            0,
            0,
            @"C:\TypeLibs\Generated.tlb");

    private static VbaProjectReferenceCatalog CreateGeneratedTypeLibEventCatalog(
        string referenceName)
    {
        var changedEvent = new TypeLibCatalogMember(
            "Changed",
            VbaSourceDefinitionKind.Procedure,
            "Changed Event.",
            new VbaCallableSignature(
                "Sub Changed(ByVal value As Long)",
                [
                    new VbaCallableParameter(
                        "value",
                        TypeReference: new VbaTypeReference("Long"),
                        IsByRef: false)
                ],
                CallableKind: VbaCallableKind.Sub),
            Metadata: new TypeLibCatalogCallableMetadata(
                MemberId: 1,
                FunctionFlags: 0));
        return TypeLibReferenceCatalogBuilder.Build(
            referenceName,
            new TypeLibCatalogMetadata(
                "Generated",
                [
                    new TypeLibCatalogType(
                        "Publisher",
                        VbaSourceDefinitionKind.Class,
                        Documentation: null,
                        Members: [],
                        IsCreatable: true,
                        Metadata: new TypeLibCatalogTypeMetadata(
                            TypeLibCatalogRawTypeKind.CoClass,
                            TypeFlags: 0,
                            ImplementedInterfaces:
                            [
                                new TypeLibCatalogImplementedInterface(
                                    "_PublisherEvents",
                                    TypeFlags: 0,
                                    ImplementationFlags: 0x1 | 0x2,
                                    CallableMembers: [changedEvent],
                                    RawTypeKind:
                                        TypeLibCatalogRawTypeKind.Dispatch)
                            ]))
                ]));
    }

    private static object CreateHostProjectionSnapshot(
        string projectRoot,
        string sourceTemplate,
        string className,
        string eventName,
        string authority = "current")
        => new
        {
            schemaVersion = 2,
            revision = 1,
            project = Path.GetFullPath(projectRoot),
            document = "Book1",
            sourceTemplate = Path.GetFullPath(sourceTemplate),
            state = "present",
            classEnumerationComplete = true,
            classes = new object[]
            {
                new
                {
                    identity = new { name = className, kind = "form" },
                    authority,
                    projection = new
                    {
                        intrinsicEventSourceName = "UserForm",
                        events = new object[]
                        {
                            new
                            {
                                name = eventName,
                                parameters = Array.Empty<object>(),
                                documentation = "Projected host Event.",
                                authoringAvailable = true,
                                existingHandlerRecognizable = true
                            }
                        }
                    }
                }
            }
        };

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

    private static Task<JsonElement> SendPositionRequestAsync(
        LanguageServerProcessHarness process,
        int id,
        string method,
        string uri,
        string text,
        string needle,
        int offset = 0,
        bool includeDeclaration = false)
    {
        var characterOffset = text.IndexOf(needle, StringComparison.Ordinal) + offset;
        var prefix = text[..characterOffset];
        var line = prefix.Count(character => character == '\n');
        var lineStart = prefix.LastIndexOf('\n');
        var character = lineStart < 0 ? characterOffset : characterOffset - lineStart - 1;
        object parameters = includeDeclaration
            ? new
            {
                textDocument = new { uri },
                position = new { line, character },
                context = new { includeDeclaration = true }
            }
            : new
            {
                textDocument = new { uri },
                position = new { line, character }
            };
        return process.SendRequestAsync(
            id,
            method,
            parameters);
    }
}
