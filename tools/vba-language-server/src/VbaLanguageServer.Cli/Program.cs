using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;

var server = new MinimalLanguageServer(Console.OpenStandardInput(), Console.OpenStandardOutput());
await server.RunAsync();

internal sealed class MinimalLanguageServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Stream input;
    private readonly Stream output;
    private readonly Dictionary<string, string> documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim outputLock = new(1, 1);
    private readonly VbaProjectReferenceCatalogCache referenceCatalogCache;
    private readonly VbaProjectReferenceCatalogRefreshService catalogRefreshService;
    private bool shutdownRequested;

    public MinimalLanguageServer(
        Stream input,
        Stream output,
        VbaProjectReferenceCatalogCache? referenceCatalogCache = null,
        VbaProjectReferenceCatalogRefreshService? catalogRefreshService = null)
    {
        this.input = input;
        this.output = output;
        this.referenceCatalogCache = referenceCatalogCache
            ?? new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled());
        this.catalogRefreshService = catalogRefreshService
            ?? new VbaProjectReferenceCatalogRefreshService(
                this.referenceCatalogCache,
                new TypeLibReferenceCatalogDiscovery(new RegistryTypeLibRegistryReader()));
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await ReadMessageAsync(cancellationToken);
            if (message is null)
            {
                return;
            }

            if (!message.TryGetPropertyValue("method", out var methodNode))
            {
                continue;
            }

            var method = methodNode?.GetValue<string>();
            var hasId = message.TryGetPropertyValue("id", out var idNode);

            if (hasId)
            {
                await HandleRequestAsync(idNode, method, message["params"], cancellationToken);
                continue;
            }

            if (method == "exit")
            {
                Environment.ExitCode = shutdownRequested ? 0 : 1;
                return;
            }

            await HandleNotificationAsync(method, message["params"], cancellationToken);
        }
    }

    private async Task HandleRequestAsync(
        JsonNode? idNode,
        string? method,
        JsonNode? parameters,
        CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialize":
                await WriteResponseAsync(idNode, CreateInitializeResult(), cancellationToken);
                return;
            case "shutdown":
                shutdownRequested = true;
                await WriteResponseAsync(idNode, null, cancellationToken);
                return;
            case "textDocument/completion":
                await WriteResponseAsync(idNode, CreateCompletionItems(parameters), cancellationToken);
                return;
            case "textDocument/documentSymbol":
                await WriteResponseAsync(idNode, CreateDocumentSymbols(parameters), cancellationToken);
                return;
            case "textDocument/definition":
                await WriteResponseAsync(idNode, CreateDefinitionLocation(parameters), cancellationToken);
                return;
            case "textDocument/hover":
                await WriteResponseAsync(idNode, CreateHover(parameters), cancellationToken);
                return;
            case "textDocument/signatureHelp":
                await WriteResponseAsync(idNode, CreateSignatureHelp(parameters), cancellationToken);
                return;
            case "textDocument/prepareRename":
                await WriteResponseAsync(idNode, CreatePrepareRename(parameters), cancellationToken);
                return;
            case "textDocument/rename":
                await WriteResponseAsync(idNode, CreateRenameEdit(parameters), cancellationToken);
                return;
            case "textDocument/formatting":
                await WriteResponseAsync(idNode, CreateFormattingEdits(parameters), cancellationToken);
                return;
            default:
                await WriteErrorResponseAsync(idNode, -32601, $"Method not found: {method}", cancellationToken);
                return;
        }
    }

    private async Task HandleNotificationAsync(string? method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "textDocument/didOpen":
                await RecordOpenedDocumentAsync(parameters, cancellationToken);
                return;
            case "textDocument/didChange":
                await RecordChangedDocumentAsync(parameters, cancellationToken);
                return;
            default:
                return;
        }
    }

    private async Task RecordOpenedDocumentAsync(JsonNode? parameters, CancellationToken cancellationToken)
    {
        var textDocument = parameters?["textDocument"];
        var uri = textDocument?["uri"]?.GetValue<string>();
        var text = textDocument?["text"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(uri) && text is not null)
        {
            documents[uri] = text;
            await PublishDiagnosticsAsync(uri, text, cancellationToken);
            await PublishReferenceSelectionTraceAsync(uri, cancellationToken);
            RefreshReferenceCatalogsInBackground(uri, text, cancellationToken);
        }
    }

    private async Task RecordChangedDocumentAsync(JsonNode? parameters, CancellationToken cancellationToken)
    {
        var textDocument = parameters?["textDocument"];
        var uri = textDocument?["uri"]?.GetValue<string>();
        var changes = parameters?["contentChanges"]?.AsArray();
        var text = changes?.LastOrDefault()?["text"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(uri) && text is not null)
        {
            documents[uri] = text;
            await PublishDiagnosticsAsync(uri, text, cancellationToken);
            RefreshReferenceCatalogsInBackground(uri, text, cancellationToken);
        }
    }

    private Task PublishDiagnosticsAsync(string uri, string text, CancellationToken cancellationToken)
    {
        var diagnostics = VbaSyntaxDiagnostics.Collect(text, uri)
            .Select(diagnostic => new
            {
                code = diagnostic.Code,
                message = diagnostic.Message,
                range = diagnostic.Range,
                severity = 1,
                source = diagnostic.Source
            })
            .ToArray();

        return WriteNotificationAsync(
            "textDocument/publishDiagnostics",
            new
            {
                uri,
                diagnostics
            },
            cancellationToken);
    }

    private async Task PublishReferenceSelectionTraceAsync(string uri, CancellationToken cancellationToken)
    {
        VbaProjectResolution resolution;
        try
        {
            resolution = VbaProjectResolver.Resolve(uri);
        }
        catch (ProjectManifestException ex)
        {
            await WriteLogMessageAsync(
                2,
                $"Project manifest could not be resolved for reference selection: {ex.Message}",
                cancellationToken);
            return;
        }

        if (resolution.Kind != VbaProjectResolutionKind.ManifestDocument
            || string.IsNullOrEmpty(resolution.DocumentName)
            || string.IsNullOrEmpty(resolution.DocumentKind))
        {
            return;
        }

        var selection = VbaProjectReferenceSelection.Create(
            resolution.DocumentKind,
            resolution.ReferenceEntries);
        var references = selection.References.Count == 0
            ? "<none>"
            : string.Join(", ", selection.References.Select(reference => reference.Name));
        await WriteLogMessageAsync(
            3,
            $"VbaProjectReferenceSelection document={resolution.DocumentName} references={references} main={selection.MainVbaProjectReference?.Name ?? "<none>"}",
            cancellationToken);

        if (selection.MissingExpectedMainReference is not null)
        {
            await WriteLogMessageAsync(
                2,
                $"Manifest/reference consistency warning: document '{resolution.DocumentName}' kind '{resolution.DocumentKind}' is missing expected main reference '{selection.MissingExpectedMainReference}'. Host definitions will not be activated implicitly.",
                cancellationToken);
        }

        foreach (var referenceName in referenceCatalogCache.Current.GetMissingCatalogReferenceNames(selection))
        {
            await WriteLogMessageAsync(
                2,
                $"Reference catalog availability warning: document '{resolution.DocumentName}' reference '{referenceName}' has no bundled or cached VbaProjectReferenceCatalog metadata. The reference remains active, but external definitions are unavailable.",
                cancellationToken);
        }
    }

    private void RefreshReferenceCatalogsInBackground(string uri, string text, CancellationToken cancellationToken)
    {
        if (TryCreateReferenceSelections(uri, text, out var selections))
        {
            _ = RefreshReferenceCatalogsInBackgroundAsync(uri, selections, cancellationToken);
        }
    }

    private async Task RefreshReferenceCatalogsInBackgroundAsync(
        string uri,
        IReadOnlyList<(string DocumentName, VbaProjectReferenceSelection Selection)> selections,
        CancellationToken cancellationToken)
    {
        foreach (var (documentName, selection) in selections)
        {
            IReadOnlyList<VbaProjectReferenceCatalogRefreshResult> results;
            try
            {
                results = await catalogRefreshService.RefreshAsync(selection, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            foreach (var result in results)
            {
                await PublishCatalogRefreshResultAsync(uri, documentName, result, cancellationToken);
            }
        }
    }

    private async Task PublishCatalogRefreshResultAsync(
        string uri,
        string documentName,
        VbaProjectReferenceCatalogRefreshResult result,
        CancellationToken cancellationToken)
    {
        var discovery = result.DiscoveryResult;
        if (discovery.IsFailure)
        {
            await WriteLogMessageAsync(
                2,
                $"Reference catalog discovery warning: document '{documentName}' reference '{result.ReferenceName}' could not be discovered for {uri}: {discovery.ErrorMessage}",
                cancellationToken);
            return;
        }

        if (discovery.IsAmbiguous)
        {
            await WriteLogMessageAsync(
                2,
                $"Reference catalog discovery warning: document '{documentName}' reference '{result.ReferenceName}' is ambiguous across {discovery.Identities.Count} TypeLib candidates; no catalog was cached.",
                cancellationToken);
            return;
        }

        var identity = discovery.Identities.SingleOrDefault();
        if (identity is not null)
        {
            await WriteLogMessageAsync(
                3,
                $"Reference catalog discovery: document '{documentName}' reference '{result.ReferenceName}' resolved to TypeLib {identity.Guid} {identity.MajorVersion}.{identity.MinorVersion} LCID {identity.Lcid} at {identity.Path}.",
                cancellationToken);
        }

        if (discovery.HasUsableCatalog)
        {
            await WriteLogMessageAsync(
                3,
                $"Reference catalog refresh: document '{documentName}' reference '{result.ReferenceName}' cached {discovery.Catalog!.Definitions.Count} external definitions.",
                cancellationToken);
        }
    }

    private static bool TryCreateReferenceSelections(
        string uri,
        string text,
        out IReadOnlyList<(string DocumentName, VbaProjectReferenceSelection Selection)> selections)
    {
        selections = [];
        if (IsProjectManifestUri(uri))
        {
            try
            {
                var manifest = ProjectManifestReader.Parse(text, uri);
                selections = manifest.Documents
                    .Select(document => (
                        document.Key,
                        VbaProjectReferenceSelection.Create(
                            document.Value.Kind,
                            document.Value.References ?? [])))
                    .ToArray();
                return selections.Count > 0;
            }
            catch (ProjectManifestException)
            {
                return false;
            }
        }

        VbaProjectResolution resolution;
        try
        {
            resolution = VbaProjectResolver.Resolve(uri);
        }
        catch (ProjectManifestException)
        {
            return false;
        }

        if (resolution.Kind != VbaProjectResolutionKind.ManifestDocument
            || string.IsNullOrEmpty(resolution.DocumentName)
            || string.IsNullOrEmpty(resolution.DocumentKind))
        {
            return false;
        }

        selections =
        [
            (
                resolution.DocumentName,
                VbaProjectReferenceSelection.Create(
                    resolution.DocumentKind,
                    resolution.ReferenceEntries))
        ];
        return true;
    }

    private static bool IsProjectManifestUri(string uri)
    {
        var localPath = VbaProjectResolver.TryGetLocalPath(uri);
        return localPath is not null
            && Path.GetFileName(localPath).Equals("project.json", StringComparison.OrdinalIgnoreCase);
    }

    private static object CreateInitializeResult()
    {
        return new
        {
            capabilities = new
            {
                textDocumentSync = 2,
                definitionProvider = true,
                documentSymbolProvider = true,
                hoverProvider = true,
                documentFormattingProvider = true,
                renameProvider = new
                {
                    prepareProvider = true
                },
                signatureHelpProvider = new
                {
                    triggerCharacters = new[] { "(", "," }
                },
                completionProvider = new
                {
                    triggerCharacters = new[] { ".", " " }
                }
            },
            serverInfo = new
            {
                name = "vba-language-server",
                version = "0.1.0"
            }
        };
    }

    private object[] CreateDocumentSymbols(JsonNode? parameters)
    {
        var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
        if (string.IsNullOrEmpty(uri))
        {
            return Array.Empty<object>();
        }

        var sourceIndex = CreateSourceIndex(uri);
        return sourceIndex
            .GetDocumentDefinitions(uri)
            .Select(definition => new
            {
                name = definition.Name,
                kind = GetSymbolKind(definition.Kind),
                range = definition.Range,
                selectionRange = definition.Range
            })
            .ToArray<object>();
    }

    private object? CreateDefinitionLocation(JsonNode? parameters)
    {
        var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
        var position = parameters?["position"];
        var line = position?["line"]?.GetValue<int>();
        var character = position?["character"]?.GetValue<int>();
        if (string.IsNullOrEmpty(uri) || line is null || character is null)
        {
            return null;
        }

        var sourceIndex = CreateSourceIndex(uri);
        var definition = sourceIndex.ResolveDefinition(uri, line.Value, character.Value);
        return definition is null
            ? null
            : new
            {
                uri = definition.Uri,
                range = definition.Range
            };
    }

    private static int GetSymbolKind(VbaSourceDefinitionKind kind)
        => kind switch
        {
            VbaSourceDefinitionKind.Module => 2,
            VbaSourceDefinitionKind.Class => 5,
            VbaSourceDefinitionKind.Form => 5,
            VbaSourceDefinitionKind.Procedure => 12,
            VbaSourceDefinitionKind.Property => 7,
            VbaSourceDefinitionKind.Constant => 14,
            VbaSourceDefinitionKind.Variable => 13,
            VbaSourceDefinitionKind.Parameter => 13,
            VbaSourceDefinitionKind.Enum => 10,
            VbaSourceDefinitionKind.EnumMember => 22,
            VbaSourceDefinitionKind.Type => 23,
            VbaSourceDefinitionKind.TypeMember => 8,
            VbaSourceDefinitionKind.Event => 24,
            _ => 13
        };

    private object[] CreateCompletionItems(JsonNode? parameters)
    {
        if (!TryGetTextDocumentPosition(parameters, out var uri, out var line, out var character))
        {
            return Array.Empty<object>();
        }

        var sourceIndex = CreateSourceIndex(uri);
        var sourceItems = sourceIndex
            .GetCompletionDefinitions(uri, line, character)
            .Select(definition => new
            {
                label = definition.Name,
                kind = GetCompletionKind(definition.Kind)
            });
        var vocabularyItems = VbaSourceIndex.LanguageVocabulary.Select(label => new
        {
            label,
            kind = 14
        });

        return sourceItems
            .Concat(vocabularyItems)
            .GroupBy(item => item.label, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.label, StringComparer.OrdinalIgnoreCase)
            .ToArray<object>();
    }

    private object? CreateHover(JsonNode? parameters)
    {
        if (!TryGetTextDocumentPosition(parameters, out var uri, out var line, out var character))
        {
            return null;
        }

        var sourceIndex = CreateSourceIndex(uri);
        var definition = sourceIndex.ResolveSourceDefinition(uri, line, character);
        if (definition is null)
        {
            return null;
        }

        var declaration = definition.Signature?.Label ?? definition.Name;
        var value = string.IsNullOrWhiteSpace(definition.Documentation)
            ? declaration
            : $"{definition.Documentation}\n\n{declaration}";
        return new
        {
            contents = new
            {
                kind = "markdown",
                value
            },
            range = definition.Range
        };
    }

    private object? CreateSignatureHelp(JsonNode? parameters)
    {
        if (!TryGetTextDocumentPosition(parameters, out var uri, out var line, out var character))
        {
            return null;
        }

        var sourceIndex = CreateSourceIndex(uri);
        var signatureHelp = sourceIndex.GetSignatureHelp(uri, line, character);
        if (signatureHelp is null)
        {
            return null;
        }

        return new
        {
            signatures = new[]
            {
                new
                {
                    label = signatureHelp.Signature.Label,
                    documentation = ToMarkup(signatureHelp.Signature.Documentation),
                    parameters = signatureHelp.Signature.Parameters.Select(parameter => new
                    {
                        label = parameter.Name,
                        documentation = ToMarkup(parameter.Documentation)
                    }).ToArray()
                }
            },
            activeSignature = 0,
            activeParameter = signatureHelp.ActiveParameter
        };
    }

    private object? CreatePrepareRename(JsonNode? parameters)
    {
        if (!TryGetTextDocumentPosition(parameters, out var uri, out var line, out var character))
        {
            return null;
        }

        var sourceIndex = CreateSourceIndex(uri);
        var definition = sourceIndex.ResolveSourceDefinition(uri, line, character);
        return definition is null ? null : definition.Range;
    }

    private object? CreateRenameEdit(JsonNode? parameters)
    {
        if (!TryGetTextDocumentPosition(parameters, out var uri, out var line, out var character))
        {
            return null;
        }

        var newName = parameters?["newName"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(newName))
        {
            return null;
        }

        var sourceIndex = CreateSourceIndex(uri);
        var changes = sourceIndex.CreateRenameChanges(uri, line, character, newName);
        if (changes is null)
        {
            return null;
        }

        return new
        {
            changes = changes.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Select(edit => new
                {
                    range = edit.Range,
                    newText = edit.NewText
                }).ToArray(),
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private object[] CreateFormattingEdits(JsonNode? parameters)
    {
        var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
        if (string.IsNullOrEmpty(uri))
        {
            return Array.Empty<object>();
        }

        var tabSize = parameters?["options"]?["tabSize"]?.GetValue<int>() ?? 4;
        var sourceIndex = CreateSourceIndex(uri);
        var edit = sourceIndex.FormatDocument(uri, tabSize);
        if (edit is null)
        {
            return Array.Empty<object>();
        }

        return new object[]
        {
            new
            {
                range = edit.Range,
                newText = edit.NewText
            }
        };
    }

    private static bool TryGetTextDocumentPosition(
        JsonNode? parameters,
        out string uri,
        out int line,
        out int character)
    {
        uri = parameters?["textDocument"]?["uri"]?.GetValue<string>() ?? "";
        line = parameters?["position"]?["line"]?.GetValue<int>() ?? -1;
        character = parameters?["position"]?["character"]?.GetValue<int>() ?? -1;
        return !string.IsNullOrEmpty(uri) && line >= 0 && character >= 0;
    }

    private VbaSourceIndex CreateSourceIndex(string activeUri)
    {
        var resolution = VbaProjectResolver.Resolve(activeUri);
        var scopedDocuments = documents
            .Where(pair => resolution.ContainsUri(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        if (!scopedDocuments.ContainsKey(activeUri) && documents.TryGetValue(activeUri, out var activeText))
        {
            scopedDocuments[activeUri] = activeText;
        }

        var referenceSelection =
            resolution.Kind == VbaProjectResolutionKind.ManifestDocument
            && !string.IsNullOrEmpty(resolution.DocumentKind)
                ? VbaProjectReferenceSelection.Create(
                    resolution.DocumentKind,
                    resolution.ReferenceEntries)
                : null;

        return VbaSourceIndex.Build(scopedDocuments, referenceSelection, referenceCatalogCache.Current);
    }

    private static object? ToMarkup(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : new
            {
                kind = "markdown",
                value
            };

    private static int GetCompletionKind(VbaSourceDefinitionKind kind)
        => kind switch
        {
            VbaSourceDefinitionKind.Class => 7,
            VbaSourceDefinitionKind.Form => 7,
            VbaSourceDefinitionKind.Procedure => 3,
            VbaSourceDefinitionKind.Property => 10,
            VbaSourceDefinitionKind.Constant => 21,
            VbaSourceDefinitionKind.Variable => 6,
            VbaSourceDefinitionKind.Parameter => 6,
            VbaSourceDefinitionKind.Enum => 13,
            VbaSourceDefinitionKind.EnumMember => 20,
            VbaSourceDefinitionKind.Type => 22,
            VbaSourceDefinitionKind.TypeMember => 5,
            VbaSourceDefinitionKind.Event => 23,
            _ => 1
        };

    private async Task<JsonObject?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var headers = await ReadHeaderAsync(cancellationToken);
        if (headers is null)
        {
            return null;
        }

        if (!headers.TryGetValue("Content-Length", out var lengthText)
            || !int.TryParse(lengthText, out var contentLength))
        {
            return null;
        }

        var content = new byte[contentLength];
        var offset = 0;
        while (offset < content.Length)
        {
            var read = await input.ReadAsync(content.AsMemory(offset, content.Length - offset), cancellationToken);
            if (read == 0)
            {
                return null;
            }

            offset += read;
        }

        return JsonNode.Parse(content)?.AsObject();
    }

    private async Task<Dictionary<string, string>?> ReadHeaderAsync(CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        while (true)
        {
            var next = input.ReadByte();
            if (next < 0)
            {
                return null;
            }

            buffer.GetSpan(1)[0] = (byte)next;
            buffer.Advance(1);

            var written = buffer.WrittenSpan;
            if (written.Length >= 4
                && written[^4] == '\r'
                && written[^3] == '\n'
                && written[^2] == '\r'
                && written[^1] == '\n')
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var headerText = Encoding.ASCII.GetString(buffer.WrittenSpan);
        foreach (var line in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var delimiter = line.IndexOf(':');
            if (delimiter <= 0)
            {
                continue;
            }

            headers[line[..delimiter]] = line[(delimiter + 1)..].Trim();
        }

        return headers;
    }

    private Task WriteResponseAsync(JsonNode? idNode, object? result, CancellationToken cancellationToken)
    {
        return WriteMessageAsync(new
        {
            jsonrpc = "2.0",
            id = idNode,
            result
        }, cancellationToken);
    }

    private Task WriteErrorResponseAsync(
        JsonNode? idNode,
        int code,
        string message,
        CancellationToken cancellationToken)
    {
        return WriteMessageAsync(new
        {
            jsonrpc = "2.0",
            id = idNode,
            error = new
            {
                code,
                message
            }
        }, cancellationToken);
    }

    private Task WriteNotificationAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        return WriteMessageAsync(new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters
        }, cancellationToken);
    }

    private Task WriteLogMessageAsync(int type, string message, CancellationToken cancellationToken)
    {
        return WriteNotificationAsync(
            "window/logMessage",
            new
            {
                type,
                message
            },
            cancellationToken);
    }

    private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {content.Length}\r\n\r\n");
        await outputLock.WaitAsync(cancellationToken);
        try
        {
            await output.WriteAsync(header, cancellationToken);
            await output.WriteAsync(content, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        finally
        {
            outputLock.Release();
        }
    }
}
