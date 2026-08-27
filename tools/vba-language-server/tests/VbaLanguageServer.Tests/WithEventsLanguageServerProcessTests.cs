using System.Text.Json;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class WithEventsLanguageServerProcessTests
{
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
    public async Task Source_Event_handler_reports_type_array_passing_and_role_mismatches()
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
                + "parameter 1 role: expected required, found Optional.",
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
                CreateGeneratedReferenceCatalogIdentity("Second Library"),
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
        string referenceName)
        => new(
            referenceName,
            "{33333333-3333-3333-3333-333333333333}",
            1,
            0,
            0,
            @"C:\TypeLibs\Generated.tlb");

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
        int offset = 0)
    {
        var characterOffset = text.IndexOf(needle, StringComparison.Ordinal) + offset;
        var prefix = text[..characterOffset];
        var line = prefix.Count(character => character == '\n');
        var lineStart = prefix.LastIndexOf('\n');
        var character = lineStart < 0 ? characterOffset : characterOffset - lineStart - 1;
        return process.SendRequestAsync(
            id,
            method,
            new
            {
                textDocument = new { uri },
                position = new { line, character }
            });
    }
}
