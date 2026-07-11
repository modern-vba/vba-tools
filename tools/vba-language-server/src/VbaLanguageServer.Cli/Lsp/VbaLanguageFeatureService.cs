using System.Text.Json.Nodes;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Creates LSP response payloads for VBA language features from workspace snapshots.
/// </summary>
internal sealed class VbaLanguageFeatureService
{
    private readonly VbaLanguageWorkspace workspace;

    /// <summary>
    /// Creates a feature service over a language workspace.
    /// </summary>
    /// <param name="workspace">The workspace used to build project snapshots.</param>
    public VbaLanguageFeatureService(VbaLanguageWorkspace workspace)
    {
        this.workspace = workspace;
    }

    /// <summary>
    /// Creates the initialize response payload with language-server capabilities.
    /// </summary>
    /// <returns>The initialize result payload.</returns>
    public static object CreateInitializeResult()
    {
        return new
        {
            capabilities = new
            {
                textDocumentSync = 1,
                definitionProvider = true,
                referencesProvider = true,
                documentSymbolProvider = true,
                workspaceSymbolProvider = true,
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
                },
                semanticTokensProvider = new
                {
                    legend = new
                    {
                        tokenTypes = VbaSourceIndex.SemanticTokenTypes,
                        tokenModifiers = VbaSourceIndex.SemanticTokenModifiers
                    },
                    full = true,
                    range = false
                }
            },
            serverInfo = new
            {
                name = "vba-language-server",
                version = "0.1.0"
            }
        };
    }

    /// <summary>
    /// Creates LSP diagnostic payloads by parsing source text.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="text">The source text.</param>
    /// <returns>The diagnostic payload objects.</returns>
    public static object[] CreateDiagnostics(string uri, string text)
        => CreateDiagnostics(uri, VbaSyntaxTree.ParseModule(uri, text));

    /// <summary>
    /// Creates LSP diagnostic payloads from a parsed syntax tree.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="tree">The parsed syntax tree.</param>
    /// <returns>The diagnostic payload objects.</returns>
    public static object[] CreateDiagnostics(string uri, VbaSyntaxTree tree)
    {
        return VbaDocumentDiagnostics.Collect(tree, uri)
            .Select(diagnostic => new
            {
                code = diagnostic.Code,
                message = diagnostic.Message,
                range = diagnostic.Range,
                severity = 1,
                source = diagnostic.Source
            })
            .ToArray<object>();
    }

    /// <summary>
    /// Creates textDocument/documentSymbol response items.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The document symbol payload objects, or an empty array for invalid input.</returns>
    public object[] CreateDocumentSymbols(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
        if (string.IsNullOrEmpty(uri))
        {
            return [];
        }

        var snapshot = workspace.CreateProjectSnapshot(uri, cancellationToken);
        return snapshot.SourceIndex
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

    /// <summary>
    /// Creates a textDocument/definition response location.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The definition location payload, or null when unresolved.</returns>
    public object? CreateDefinitionLocation(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
        var position = parameters?["position"];
        var line = position?["line"]?.GetValue<int>();
        var character = position?["character"]?.GetValue<int>();
        if (string.IsNullOrEmpty(uri) || line is null || character is null)
        {
            return null;
        }

        var snapshot = workspace.CreateProjectSnapshot(uri, cancellationToken);
        var definition = snapshot.SourceIndex.ResolveDefinition(uri, line.Value, character.Value);
        return definition is null
            ? null
            : new
            {
                uri = definition.Uri,
                range = definition.Range
            };
    }

    /// <summary>
    /// Creates textDocument/references response locations.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The reference location payload objects, or an empty array for invalid input.</returns>
    public object[] CreateReferenceLocations(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        if (!TryGetTextDocumentPosition(parameters, out var uri, out var line, out var character))
        {
            return [];
        }

        var snapshot = workspace.CreateProjectSnapshot(uri, cancellationToken);
        return snapshot.SourceIndex
            .FindReferences(uri, line, character)
            .Select(reference => new
            {
                uri = reference.Uri,
                range = reference.Range
            })
            .ToArray<object>();
    }

    /// <summary>
    /// Creates workspace/symbol response items across distinct workspace snapshots.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The workspace symbol payload objects.</returns>
    public object[] CreateWorkspaceSymbols(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var query = parameters?["query"]?.GetValue<string>() ?? "";
        return workspace.CreateProjectSnapshots(cancellationToken)
            .SelectMany(snapshot => snapshot.SourceIndex.GetWorkspaceSymbols(query))
            .GroupBy(symbol => $"{symbol.Uri}:{symbol.Range.Start.Line}:{symbol.Range.Start.Character}:{symbol.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(symbol => new
            {
                name = symbol.Name,
                kind = GetSymbolKind(symbol.Kind),
                location = new
                {
                    uri = symbol.Uri,
                    range = symbol.Range
                }
            })
            .ToArray<object>();
    }

    /// <summary>
    /// Creates textDocument/completion response items.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The completion item payload objects, or an empty array for invalid input.</returns>
    public object[] CreateCompletionItems(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        if (!TryGetTextDocumentPosition(parameters, out var uri, out var line, out var character))
        {
            return [];
        }

        var snapshot = workspace.CreateProjectSnapshot(uri, cancellationToken);
        var completion = snapshot.SourceIndex.GetCompletionResult(uri, line, character);
        var sourceItems = completion
            .Definitions
            .Select(definition => new
            {
                label = definition.Name,
                kind = GetCompletionKind(definition.Kind)
            });
        var vocabularyItems = GetCompletionVocabulary(completion.VocabularyKind).Select(label => new
        {
            label,
            kind = 14
        });

        var items = sourceItems.Concat(vocabularyItems);
        return items
            .GroupBy(item => item.label, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.label, StringComparer.OrdinalIgnoreCase)
            .ToArray<object>();
    }

    /// <summary>
    /// Creates a textDocument/hover response.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The hover payload, or null when no definition resolves.</returns>
    public object? CreateHover(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        if (!TryGetTextDocumentPosition(parameters, out var uri, out var line, out var character))
        {
            return null;
        }

        var snapshot = workspace.CreateProjectSnapshot(uri, cancellationToken);
        var definition = snapshot.SourceIndex.ResolveSourceDefinition(uri, line, character);
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

    /// <summary>
    /// Creates a textDocument/signatureHelp response.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The signature help payload, or null when no callable resolves.</returns>
    public object? CreateSignatureHelp(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        if (!TryGetTextDocumentPosition(parameters, out var uri, out var line, out var character))
        {
            return null;
        }

        var snapshot = workspace.CreateProjectSnapshot(uri, cancellationToken);
        var signatureHelp = snapshot.SourceIndex.GetSignatureHelp(uri, line, character);
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

    /// <summary>
    /// Creates a textDocument/prepareRename response range.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The rename target range, or null when rename is unavailable.</returns>
    public object? CreatePrepareRename(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        if (!TryGetTextDocumentPosition(parameters, out var uri, out var line, out var character))
        {
            return null;
        }

        var snapshot = workspace.CreateProjectSnapshot(uri, cancellationToken);
        var definition = snapshot.SourceIndex.ResolveSourceDefinition(uri, line, character);
        return definition is null ? null : definition.Range;
    }

    /// <summary>
    /// Creates a textDocument/rename workspace edit response.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The workspace edit payload, or null when rename is invalid.</returns>
    public object? CreateRenameEdit(JsonNode? parameters, CancellationToken cancellationToken = default)
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

        var snapshot = workspace.CreateProjectSnapshot(uri, cancellationToken);
        var changes = snapshot.SourceIndex.CreateRenameChanges(uri, line, character, newName);
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

    /// <summary>
    /// Creates textDocument/formatting response edits.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The formatting edit payload objects, or an empty array when no edit is needed.</returns>
    public object[] CreateFormattingEdits(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
        if (string.IsNullOrEmpty(uri))
        {
            return [];
        }

        var tabSize = parameters?["options"]?["tabSize"]?.GetValue<int>() ?? 4;
        var snapshot = workspace.CreateProjectSnapshot(uri, cancellationToken);
        var edit = snapshot.SourceIndex.FormatDocument(uri, tabSize);
        if (edit is null)
        {
            return [];
        }

        return
        [
            new
            {
                range = edit.Range,
                newText = edit.NewText
            }
        ];
    }

    /// <summary>
    /// Creates a textDocument/semanticTokens/full response.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The semantic tokens payload.</returns>
    public object CreateSemanticTokens(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
        if (string.IsNullOrEmpty(uri))
        {
            return new
            {
                data = Array.Empty<int>()
            };
        }

        var snapshot = workspace.CreateProjectSnapshot(uri, cancellationToken);
        return new
        {
            data = snapshot.SourceIndex.GetSemanticTokenData(uri)
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

    private static object? ToMarkup(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : new
            {
                kind = "markdown",
                value
            };

    private static IReadOnlyList<string> GetCompletionVocabulary(VbaCompletionVocabularyKind vocabularyKind)
        => vocabularyKind switch
        {
            VbaCompletionVocabularyKind.Keyword => VbaSourceIndex.LanguageVocabulary,
            VbaCompletionVocabularyKind.TypeName => VbaSourceIndex.TypeVocabulary,
            _ => []
        };

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
}
