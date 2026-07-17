using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class BlockSkeletonInsertionProcessTests
{
    [Fact]
    public async Task Complete_sub_header_at_eof_returns_a_version_bound_insertion_plan()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        const string uri = "file:///C:/work/Module1.bas";
        const string header = "Public Sub Run()";
        const int version = 7;
        await server.SendNotificationAsync(
            "textDocument/didOpen",
            new
            {
                textDocument = new
                {
                    uri,
                    languageId = "vba",
                    version,
                    text = $"{header}\n    "
                }
            });

        var response = await server.SendRequestAsync(
            2,
            "vba/blockSkeletonInsertion",
            new
            {
                documentUri = uri,
                documentVersion = version,
                position = new { line = 0, character = header.Length },
                options = new
                {
                    insertSpaces = true,
                    indentSize = 2,
                    tabSize = 4
                }
            });

        var plan = response.GetProperty("result");
        Assert.Equal(version, plan.GetProperty("documentVersion").GetInt32());
        Assert.Equal(0, plan.GetProperty("position").GetProperty("line").GetInt32());
        Assert.Equal(header.Length, plan.GetProperty("position").GetProperty("character").GetInt32());
        Assert.Equal("\n  ", plan.GetProperty("textBeforeCursor").GetString());
        Assert.Equal("\nEnd Sub", plan.GetProperty("textAfterCursor").GetString());

        await server.ShutdownAsync(3);
    }

    [Fact]
    public async Task Safe_non_eof_sub_boundaries_return_the_same_literal_plan()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        const string uri = "file:///C:/work/Boundary.bas";
        const string header = "Public Sub First()";
        const int version = 8;
        string[] boundaries =
        [
            "Public Sub Second()\nEnd Sub",
            "Public Function Second() As Long\nEnd Function",
            "Public Property Get Second() As Long\nEnd Property"
        ];

        for (var index = 0; index < boundaries.Length; index++)
        {
            var candidateUri = index == 0 ? uri : $"file:///C:/work/Boundary{index}.bas";
            await server.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(
                    candidateUri,
                    version,
                    text: $"{header}\n    \n\n{boundaries[index]}"));

            var response = await SendInsertionRequestAsync(
                server,
                index + 2,
                candidateUri,
                version,
                character: header.Length);

            var plan = response.GetProperty("result");
            Assert.Equal(version, plan.GetProperty("documentVersion").GetInt32());
            Assert.Equal("\n  ", plan.GetProperty("textBeforeCursor").GetString());
            Assert.Equal("\nEnd Sub", plan.GetProperty("textAfterCursor").GetString());
        }

        await server.ShutdownAsync(boundaries.Length + 2);
    }

    [Fact]
    public async Task Complete_function_and_property_headers_return_version_bound_literal_plans()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        (string Uri, string Header, string Before, string After)[] cases =
        [
            (
                "file:///C:/work/Build.bas",
                "Public Function Build() As String",
                "\n  ",
                "\nEnd Function"),
            (
                "file:///C:/work/Read.cls",
                "Public Property Get Value() As Long",
                "\n  ",
                "\nEnd Property"),
            (
                "file:///C:/work/ReadModule.bas",
                "Public Property Get ModuleValue() As Long",
                "\n  ",
                "\nEnd Property"),
            (
                "file:///C:/work/Write.cls",
                "Public Property Let Value(ByVal assignedValue As Long)",
                "\n  ",
                "\nEnd Property"),
            (
                "file:///C:/work/Object.cls",
                "Public Property Set Value(ByVal assignedValue As Object)",
                "\n  ",
                "\nEnd Property")
        ];

        for (var index = 0; index < cases.Length; index++)
        {
            var candidate = cases[index];
            var version = 40 + index;
            await server.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(
                    candidate.Uri,
                    version,
                    $"{candidate.Header}\n    "));

            var response = await SendInsertionRequestAsync(
                server,
                index + 2,
                candidate.Uri,
                version,
                candidate.Header.Length);

            var plan = response.GetProperty("result");
            Assert.Equal(version, plan.GetProperty("documentVersion").GetInt32());
            Assert.Equal(candidate.Header.Length, plan.GetProperty("position").GetProperty("character").GetInt32());
            Assert.Equal(candidate.Before, plan.GetProperty("textBeforeCursor").GetString());
            Assert.Equal(candidate.After, plan.GetProperty("textAfterCursor").GetString());
        }

        await server.ShutdownAsync(cases.Length + 2);
    }

    [Fact]
    public async Task Excluded_illegal_and_owned_callable_headers_return_null()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        (string Uri, string Text, string Header)[] cases =
        [
            (
                "file:///C:/work/Event.cls",
                "Public Event Changed(ByVal value As Long)\n    ",
                "Public Event Changed(ByVal value As Long)"),
            (
                "file:///C:/work/DeclareSub.bas",
                "Public Declare Sub Run Lib \"library\" ()\n    ",
                "Public Declare Sub Run Lib \"library\" ()"),
            (
                "file:///C:/work/DeclareFunction.bas",
                "Private Declare PtrSafe Function Read Lib \"library\" () As Long\n    ",
                "Private Declare PtrSafe Function Read Lib \"library\" () As Long"),
            (
                "file:///C:/work/FriendFunction.bas",
                "Friend Function Build() As String\n    ",
                "Friend Function Build() As String"),
            (
                "file:///C:/work/GlobalProperty.cls",
                "Global Property Get Value() As Long\n    ",
                "Global Property Get Value() As Long"),
            (
                "file:///C:/work/OwnedFunction.bas",
                "Public Function Build() As String\n    \n    Build = \"value\"",
                "Public Function Build() As String"),
            (
                "file:///C:/work/OwnedProperty.cls",
                "Public Property Get Value() As Long\n    \nEnd Property",
                "Public Property Get Value() As Long")
        ];

        for (var index = 0; index < cases.Length; index++)
        {
            var candidate = cases[index];
            var version = 50 + index;
            await server.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(candidate.Uri, version, candidate.Text));

            var response = await SendInsertionRequestAsync(
                server,
                index + 2,
                candidate.Uri,
                version,
                candidate.Header.Length);

            Assert.Equal(
                System.Text.Json.JsonValueKind.Null,
                response.GetProperty("result").ValueKind);
        }

        await server.ShutdownAsync(cases.Length + 2);
    }

    [Fact]
    public async Task Complete_enum_and_type_headers_return_version_bound_literal_plans()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        (string Uri, string Text, int Line, int Character, string Before, string After)[] cases =
        [
            (
                "file:///C:/work/State.bas",
                "Public Enum State\n    ",
                0,
                "Public Enum State".Length,
                "\n  ",
                "\nEnd Enum"),
            (
                "file:///C:/work/Record.cls",
                "Private Type Record\n    ",
                0,
                "Private Type Record".Length,
                "\n  ",
                "\nEnd Type"),
            (
                "file:///C:/work/Dialog.frm",
                "VERSION 5.00\nBegin VB.Form Dialog\nEnd\nAttribute VB_Name = \"Dialog\"\nPublic Enum State\n    ",
                4,
                "Public Enum State".Length,
                "\n  ",
                "\nEnd Enum"),
            (
                "file:///C:/work/PrivateDialog.frm",
                "VERSION 5.00\nBegin VB.Form Dialog\nEnd\nAttribute VB_Name = \"Dialog\"\nPrivate Type Record\n    ",
                4,
                "Private Type Record".Length,
                "\n  ",
                "\nEnd Type")
        ];

        for (var index = 0; index < cases.Length; index++)
        {
            var candidate = cases[index];
            var version = 60 + index;
            await server.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(candidate.Uri, version, candidate.Text));

            var response = await SendInsertionRequestAsync(
                server,
                index + 2,
                candidate.Uri,
                version,
                candidate.Character,
                candidate.Line);

            var plan = response.GetProperty("result");
            Assert.Equal(version, plan.GetProperty("documentVersion").GetInt32());
            Assert.Equal(candidate.Line, plan.GetProperty("position").GetProperty("line").GetInt32());
            Assert.Equal(candidate.Character, plan.GetProperty("position").GetProperty("character").GetInt32());
            Assert.Equal(candidate.Before, plan.GetProperty("textBeforeCursor").GetString());
            Assert.Equal(candidate.After, plan.GetProperty("textAfterCursor").GetString());
        }

        await server.ShutdownAsync(cases.Length + 2);
    }

    [Fact]
    public async Task Continued_type_header_returns_a_plan_only_on_its_final_physical_line()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        const string uri = "file:///C:/work/ContinuedRecord.cls";
        string[] lines =
        [
            "\tPrivate _",
            "        Type _",
            "    Record   ' keep",
            "\t\t"
        ];
        const int version = 70;
        await server.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, version, string.Join("\r\n", lines)));

        var intermediate = await SendInsertionRequestAsync(
            server,
            2,
            uri,
            version,
            lines[1].Length,
            line: 1);
        var final = await server.SendRequestAsync(
            3,
            "vba/blockSkeletonInsertion",
            new
            {
                documentUri = uri,
                documentVersion = version,
                position = new { line = 2, character = lines[2].Length },
                options = new { insertSpaces = false, indentSize = 8, tabSize = 8 }
            });

        Assert.Equal(System.Text.Json.JsonValueKind.Null, intermediate.GetProperty("result").ValueKind);
        Assert.Equal("\r\n\t\t", final.GetProperty("result").GetProperty("textBeforeCursor").GetString());
        Assert.Equal("\r\n\tEnd Type", final.GetProperty("result").GetProperty("textAfterCursor").GetString());

        await server.ShutdownAsync(4);
    }

    [Fact]
    public async Task Illegal_or_owned_enum_and_type_contexts_return_null()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        (string Uri, string Text, int Line, int Character)[] cases =
        [
            (
                "file:///C:/work/PublicRecord.cls",
                "Public Type Record\n    ",
                0,
                "Public Type Record".Length),
            (
                "file:///C:/work/DefaultRecord.cls",
                "Type Record\n    ",
                0,
                "Type Record".Length),
            (
                "file:///C:/work/FriendState.bas",
                "Friend Enum State\n    ",
                0,
                "Friend Enum State".Length),
            (
                "file:///C:/work/GlobalRecord.bas",
                "Global Type Record\n    ",
                0,
                "Global Type Record".Length),
            (
                "file:///C:/work/EnumBody.bas",
                "Public Enum State\n    \n    Ready",
                0,
                "Public Enum State".Length),
            (
                "file:///C:/work/TypeCloser.bas",
                "Private Type Record\n    \nEnd Type",
                0,
                "Private Type Record".Length),
            (
                "file:///C:/work/Dialog.frm",
                "VERSION 5.00\nBegin VB.Form Dialog\nEnd\nAttribute VB_Name = \"Dialog\"\nType Record\n    ",
                4,
                "Type Record".Length),
            (
                "file:///C:/work/EnumAfterSub.bas",
                "Public Sub Main()\nEnd Sub\nPublic Enum State\n    ",
                2,
                "Public Enum State".Length)
        ];

        for (var index = 0; index < cases.Length; index++)
        {
            var candidate = cases[index];
            var version = 80 + index;
            await server.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(candidate.Uri, version, candidate.Text));
            var response = await SendInsertionRequestAsync(
                server,
                index + 2,
                candidate.Uri,
                version,
                candidate.Character,
                candidate.Line);

            Assert.Equal(
                System.Text.Json.JsonValueKind.Null,
                response.GetProperty("result").ValueKind);
        }

        await server.ShutdownAsync(cases.Length + 2);
    }

    [Fact]
    public async Task Complete_block_if_header_returns_a_version_bound_literal_plan()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        const string uri = "file:///C:/work/BlockIf.bas";
        const string header = "    If True Then";
        const int version = 11;
        await server.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                uri,
                version,
                text: $"Public Sub Main()\n{header}\n    \nEnd Sub"));

        var response = await SendInsertionRequestAsync(
            server,
            2,
            uri,
            version,
            character: header.Length,
            line: 1);

        var plan = response.GetProperty("result");
        Assert.Equal(version, plan.GetProperty("documentVersion").GetInt32());
        Assert.Equal(1, plan.GetProperty("position").GetProperty("line").GetInt32());
        Assert.Equal(header.Length, plan.GetProperty("position").GetProperty("character").GetInt32());
        Assert.Equal("\n      ", plan.GetProperty("textBeforeCursor").GetString());
        Assert.Equal("\n    End If", plan.GetProperty("textAfterCursor").GetString());

        await server.ShutdownAsync(3);
    }

    [Fact]
    public async Task Nested_block_if_header_preserves_the_ancestor_boundary_and_returns_its_own_terminator()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        const string uri = "file:///C:/work/NestedBlockIf.bas";
        const string header = "        If True Then";
        const int version = 12;
        await server.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                uri,
                version,
                text:
                    $"Public Sub Example()\n" +
                    "    If True Then\n" +
                    $"{header}\n" +
                    "        \n" +
                    "    End If\n" +
                    "End Sub"));

        var response = await SendInsertionRequestAsync(
            server,
            2,
            uri,
            version,
            character: header.Length,
            line: 2);

        var plan = response.GetProperty("result");
        Assert.Equal(version, plan.GetProperty("documentVersion").GetInt32());
        Assert.Equal(2, plan.GetProperty("position").GetProperty("line").GetInt32());
        Assert.Equal(header.Length, plan.GetProperty("position").GetProperty("character").GetInt32());
        Assert.Equal("\n          ", plan.GetProperty("textBeforeCursor").GetString());
        Assert.Equal("\n        End If", plan.GetProperty("textAfterCursor").GetString());

        await server.ShutdownAsync(3);
    }

    [Fact]
    public async Task Complete_with_header_returns_a_version_bound_literal_plan()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        const string uri = "file:///C:/work/With.bas";
        const string header = "    With target.Parent";
        const int version = 31;
        await server.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                uri,
                version,
                text: $"Public Sub Main()\n{header}\n    \nEnd Sub"));

        var response = await SendInsertionRequestAsync(
            server,
            2,
            uri,
            version,
            character: header.Length,
            line: 1);

        var plan = response.GetProperty("result");
        Assert.Equal(version, plan.GetProperty("documentVersion").GetInt32());
        Assert.Equal(1, plan.GetProperty("position").GetProperty("line").GetInt32());
        Assert.Equal(header.Length, plan.GetProperty("position").GetProperty("character").GetInt32());
        Assert.Equal("\n      ", plan.GetProperty("textBeforeCursor").GetString());
        Assert.Equal("\n    End With", plan.GetProperty("textAfterCursor").GetString());

        await server.ShutdownAsync(3);
    }

    [Fact]
    public async Task Continued_with_returns_a_plan_only_on_its_final_physical_line()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        const string uri = "file:///C:/work/ContinuedWith.bas";
        string[] lines =
        [
            "Public Sub Main()",
            "  With Worksheets( _",
            "        \"Sheet1\")   ' keep",
            "      ",
            "End Sub"
        ];
        const int version = 32;
        await server.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, version, string.Join("\r\n", lines)));

        var intermediate = await SendInsertionRequestAsync(
            server,
            2,
            uri,
            version,
            character: lines[1].Length,
            line: 1);
        var final = await SendInsertionRequestAsync(
            server,
            3,
            uri,
            version,
            character: lines[2].Length,
            line: 2);

        Assert.Equal(System.Text.Json.JsonValueKind.Null, intermediate.GetProperty("result").ValueKind);
        Assert.Equal("\r\n    ", final.GetProperty("result").GetProperty("textBeforeCursor").GetString());
        Assert.Equal("\r\n  End With", final.GetProperty("result").GetProperty("textAfterCursor").GetString());

        await server.ShutdownAsync(4);
    }

    [Fact]
    public async Task Nested_with_preserves_the_ancestor_boundary_and_returns_its_own_terminator()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        const string uri = "file:///C:/work/NestedWith.bas";
        const string header = "        With .Font";
        const int version = 33;
        await server.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(
                uri,
                version,
                text:
                    "Public Sub Main()\n" +
                    "    With target\n" +
                    $"{header}\n" +
                    "        \n" +
                    "    End With\n" +
                    "End Sub"));

        var response = await SendInsertionRequestAsync(
            server,
            2,
            uri,
            version,
            character: header.Length,
            line: 2);

        var plan = response.GetProperty("result");
        Assert.Equal("\n          ", plan.GetProperty("textBeforeCursor").GetString());
        Assert.Equal("\n        End With", plan.GetProperty("textAfterCursor").GetString());

        await server.ShutdownAsync(3);
    }

    [Fact]
    public async Task Ineligible_with_headers_and_owned_contexts_return_null()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        (string Uri, int Version, string Text, int Line, int Character)[] cases =
        [
            CreateWithRefusalCase("IncompleteWith", 41, "    With"),
            CreateWithRefusalCase("InvalidWith", 42, "    With target +"),
            CreateWithRefusalCase("ScalarWith", 47, "    With 1"),
            CreateWithRefusalCase("StandardConstantWith", 48, "    With VBA.Constants.vbCrLf"),
            CreateWithRefusalCase("StandardEnumWith", 49, "    With VbCompareMethod.vbBinaryCompare"),
            CreateWithRefusalCase("ErrScalarWith", 50, "    With Err.Number"),
            CreateWithRefusalCase("ScalarChainWith", 51, "    With VBA.DateTime.Timer.Value"),
            CreateWithRefusalCase("StandardOwnerWith", 52, "    With VBA.Strings"),
            CreateWithRefusalCase("UnknownStandardMemberWith", 53, "    With Strings.Unknown"),
            CreateWithRefusalCase("InvalidStandardCallWith", 54, "    With VBA.String(1)"),
            CreateWithRefusalCase("ColonWith", 43, "    With target:"),
            (
                "file:///C:/work/ModuleLevelWith.bas",
                44,
                "With target\n    ",
                0,
                "With target".Length),
            (
                "file:///C:/work/WithBody.bas",
                45,
                "Public Sub Main()\n" +
                "    With target\n" +
                "    \n" +
                "        .Value = 1\n" +
                "End Sub",
                1,
                "    With target".Length),
            (
                "file:///C:/work/CandidateOwnedEndWith.bas",
                46,
                "Public Sub Main()\n" +
                "    With target\n" +
                "    \n" +
                "    End With\n" +
                "End Sub",
                1,
                "    With target".Length)
        ];

        for (var index = 0; index < cases.Length; index++)
        {
            var candidate = cases[index];
            await server.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(candidate.Uri, candidate.Version, candidate.Text));

            var response = await SendInsertionRequestAsync(
                server,
                index + 2,
                candidate.Uri,
                candidate.Version,
                candidate.Character,
                candidate.Line);

            Assert.Equal(
                System.Text.Json.JsonValueKind.Null,
                response.GetProperty("result").ValueKind);
        }

        await server.ShutdownAsync(cases.Length + 2);
    }

    [Fact]
    public async Task Ineligible_if_headers_and_candidate_owned_boundaries_return_null()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        (string Uri, int Version, string Text, int Line, int Character)[] cases =
        [
            CreateIfRefusalCase(
                "SingleLineIf",
                version: 21,
                header: "    If True Then Debug.Print 1"),
            CreateIfRefusalCase(
                "IncompleteIf",
                version: 22,
                header: "    If value + Then"),
            CreateIfRefusalCase(
                "ColonIf",
                version: 23,
                header: "    If True Then:"),
            CreateIfRefusalCase(
                "LiteralPostfixIf",
                version: 24,
                header: "    If True() Then"),
            (
                "file:///C:/work/ElseIfBranch.bas",
                25,
                "Public Sub Main()\n" +
                "    If False Then\n" +
                "    ElseIf True Then\n" +
                "        \n" +
                "    End If\n" +
                "End Sub",
                2,
                "    ElseIf True Then".Length),
            (
                "file:///C:/work/CandidateOwnedEndIf.bas",
                26,
                "Public Sub Main()\n" +
                "    If True Then\n" +
                "        \n" +
                "    End If\n" +
                "End Sub",
                1,
                "    If True Then".Length)
        ];

        for (var index = 0; index < cases.Length; index++)
        {
            var candidate = cases[index];
            await server.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(candidate.Uri, candidate.Version, candidate.Text));

            var response = await SendInsertionRequestAsync(
                server,
                index + 2,
                candidate.Uri,
                candidate.Version,
                candidate.Character,
                candidate.Line);

            Assert.Equal(
                System.Text.Json.JsonValueKind.Null,
                response.GetProperty("result").ValueKind);
        }

        await server.ShutdownAsync(cases.Length + 2);
    }

    [Fact]
    public async Task Unsafe_non_eof_sub_contexts_return_null()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        const string header = "Public Sub First()";
        string[] contexts =
        [
            "    Debug.Print 1",
            "    ' existing comment",
            "End Sub",
            "    Public Sub Nested()\n    End Sub",
            "Public Sub Broken("
        ];

        for (var index = 0; index < contexts.Length; index++)
        {
            var uri = $"file:///C:/work/UnsafeBoundary{index}.bas";
            await server.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(
                    uri,
                    version: 1,
                    text: $"{header}\n    \n\n{contexts[index]}"));
            var response = await SendInsertionRequestAsync(
                server,
                index + 2,
                uri,
                version: 1,
                character: header.Length);

            Assert.Equal(
                System.Text.Json.JsonValueKind.Null,
                response.GetProperty("result").ValueKind);
        }

        await server.ShutdownAsync(contexts.Length + 2);
    }

    [Fact]
    public async Task CrLf_open_buffer_returns_the_same_eof_plan_used_by_the_extension()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        const string uri = "file:///C:/work/CrLfModule.bas";
        const string header = "Public Sub Main()";
        const int version = 2;
        await server.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, version, text: $"{header}\r\n"));

        var response = await SendInsertionRequestAsync(
            server,
            2,
            uri,
            version,
            character: header.Length);

        var plan = response.GetProperty("result");
        Assert.Equal(version, plan.GetProperty("documentVersion").GetInt32());
        Assert.Equal("\r\n  ", plan.GetProperty("textBeforeCursor").GetString());
        Assert.Equal("\r\nEnd Sub", plan.GetProperty("textAfterCursor").GetString());

        await server.ShutdownAsync(3);
    }

    [Fact]
    public async Task Omitted_indent_size_uses_the_protocol_tab_size_fallback()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        const string uri = "file:///C:/work/Fallback.bas";
        const string header = "Public Sub Run()";
        const int version = 4;
        await server.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, version, text: $"{header}\n    "));

        var response = await server.SendRequestAsync(
            2,
            "vba/blockSkeletonInsertion",
            new
            {
                documentUri = uri,
                documentVersion = version,
                position = new { line = 0, character = header.Length },
                options = new { insertSpaces = true, tabSize = 3 }
            });

        var plan = response.GetProperty("result");
        Assert.Equal("\n   ", plan.GetProperty("textBeforeCursor").GetString());
        Assert.Equal("\nEnd Sub", plan.GetProperty("textAfterCursor").GetString());

        await server.ShutdownAsync(3);
    }

    [Fact]
    public async Task Only_the_current_open_document_version_can_return_a_plan()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        const string uri = "file:///C:/work/Versioned.bas";
        const string header = "Public Sub Run()";
        await server.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(uri, version: 3, text: $"{header}\n    "));
        await server.SendNotificationAsync(
            "textDocument/didChange",
            new
            {
                textDocument = new { uri, version = 4 },
                contentChanges = new[] { new { text = $"{header}\n    " } }
            });

        var stale = await SendInsertionRequestAsync(
            server,
            2,
            uri,
            version: 3,
            character: header.Length);
        var current = await SendInsertionRequestAsync(
            server,
            3,
            uri,
            version: 4,
            character: header.Length);

        Assert.Equal(System.Text.Json.JsonValueKind.Null, stale.GetProperty("result").ValueKind);
        Assert.Equal(4, current.GetProperty("result").GetProperty("documentVersion").GetInt32());

        await server.ShutdownAsync(4);
    }

    [Fact]
    public async Task Balanced_but_illegal_sub_headers_fail_closed_through_the_protocol()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        (string Uri, string Text, int Line, int Character)[] cases =
        [
            (
                "file:///C:/work/MissingParameter.bas",
                "Public Sub Run(ByVal)\n    ",
                0,
                "Public Sub Run(ByVal)".Length),
            (
                "file:///C:/work/EmptyParameter.bas",
                "Public Sub Run(,)\n    ",
                0,
                "Public Sub Run(,)".Length),
            (
                "file:///C:/work/ExpressionParameter.bas",
                "Public Sub Run(foo + bar)\n    ",
                0,
                "Public Sub Run(foo + bar)".Length),
            (
                "file:///C:/work/MalformedDefaultOperator.bas",
                "Public Sub Run(Optional value As Long = 1 + * 2)\n    ",
                0,
                "Public Sub Run(Optional value As Long = 1 + * 2)".Length),
            (
                "file:///C:/work/MalformedDefaultOperands.bas",
                "Public Sub Run(Optional value As Long = 1 2)\n    ",
                0,
                "Public Sub Run(Optional value As Long = 1 2)".Length),
            (
                "file:///C:/work/OptionalArray.bas",
                "Public Sub Run(Optional values() As Variant)\n    ",
                0,
                "Public Sub Run(Optional values() As Variant)".Length),
            (
                "file:///C:/work/ObjectDefault.bas",
                "Public Sub Run(Optional value As Object = 1)\n    ",
                0,
                "Public Sub Run(Optional value As Object = 1)".Length),
            (
                "file:///C:/work/InvalidIdentifier.bas",
                "Public Sub _Run()\n    ",
                0,
                "Public Sub _Run()".Length),
            (
                "file:///C:/work/IfContext.bas",
                "If True Then\nPublic Sub Run()\n    ",
                1,
                "Public Sub Run()".Length),
            (
                "file:///C:/work/TypeContext.bas",
                "Public Type Record\nPublic Sub Run()\n    ",
                1,
                "Public Sub Run()".Length),
            (
                "file:///C:/work/WhileContext.bas",
                "While True\nPublic Sub Run()\n    ",
                1,
                "Public Sub Run()".Length),
            (
                "file:///C:/work/FriendModule.bas",
                "Friend Sub Run()\n    ",
                0,
                "Friend Sub Run()".Length),
            (
                "file:///C:/work/Conditional.bas",
                "#If VBA7 Then\nPublic Sub Run()\n    ",
                1,
                "Public Sub Run()".Length)
        ];

        for (var index = 0; index < cases.Length; index++)
        {
            var candidate = cases[index];
            await server.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(candidate.Uri, version: 1, text: candidate.Text));
            var response = await server.SendRequestAsync(
                index + 2,
                "vba/blockSkeletonInsertion",
                new
                {
                    documentUri = candidate.Uri,
                    documentVersion = 1,
                    position = new { line = candidate.Line, character = candidate.Character },
                    options = new { insertSpaces = true, tabSize = 4 }
                });

            Assert.Equal(
                System.Text.Json.JsonValueKind.Null,
                response.GetProperty("result").ValueKind);
        }

        await server.ShutdownAsync(cases.Length + 2);
    }

    [Fact]
    public async Task Malformed_insertion_requests_return_invalid_params()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        object[] invalidRequests =
        [
            new
            {
                documentUri = "file:///C:/work/Module1.bas",
                position = new { line = 0, character = 0 },
                options = new { insertSpaces = true, tabSize = 4 }
            },
            new
            {
                documentUri = "file:///C:/work/Module1.bas",
                documentVersion = 1,
                position = new { line = -1, character = 0 },
                options = new { insertSpaces = true, tabSize = 4 }
            },
            new
            {
                documentUri = "file:///C:/work/Module1.bas",
                documentVersion = 1,
                position = new { line = 0, character = 0 },
                options = new { insertSpaces = true, tabSize = 0 }
            },
            new
            {
                documentUri = "file:///C:/work/Module1.bas",
                documentVersion = 1,
                position = new { line = 0, character = 0 },
                options = new { insertSpaces = true, indentSize = 0, tabSize = 4 }
            }
        ];

        for (var index = 0; index < invalidRequests.Length; index++)
        {
            var response = await server.SendRequestAsync(
                index + 2,
                "vba/blockSkeletonInsertion",
                invalidRequests[index]);

            Assert.True(
                response.TryGetProperty("error", out var error),
                $"Invalid request {index} unexpectedly returned {response}.");
            Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        }

        await server.ShutdownAsync(invalidRequests.Length + 2);
    }

    [Fact]
    public async Task Valid_request_for_a_missing_document_returns_null()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        var response = await SendInsertionRequestAsync(
            server,
            2,
            "file:///C:/work/Missing.bas",
            version: 1,
            character: 0);

        Assert.Equal(System.Text.Json.JsonValueKind.Null, response.GetProperty("result").ValueKind);

        await server.ShutdownAsync(3);
    }

    private static object CreateOpenDocument(string uri, int version, string text)
        => new
        {
            textDocument = new
            {
                uri,
                languageId = "vba",
                version,
                text
            }
        };

    private static (string Uri, int Version, string Text, int Line, int Character) CreateIfRefusalCase(
        string name,
        int version,
        string header)
        => (
            $"file:///C:/work/{name}.bas",
            version,
            $"Public Sub Main()\n{header}\n    \nEnd Sub",
            1,
            header.Length);

    private static (string Uri, int Version, string Text, int Line, int Character) CreateWithRefusalCase(
        string name,
        int version,
        string header)
        => (
            $"file:///C:/work/{name}.bas",
            version,
            $"Public Sub Main()\n{header}\n    \nEnd Sub",
            1,
            header.Length);

    private static Task<System.Text.Json.JsonElement> SendInsertionRequestAsync(
        LanguageServerProcessHarness server,
        int id,
        string uri,
        int version,
        int character,
        int line = 0)
        => server.SendRequestAsync(
            id,
            "vba/blockSkeletonInsertion",
            new
            {
                documentUri = uri,
                documentVersion = version,
                position = new { line, character },
                options = new
                {
                    insertSpaces = true,
                    indentSize = 2,
                    tabSize = 4
                }
            });
}
