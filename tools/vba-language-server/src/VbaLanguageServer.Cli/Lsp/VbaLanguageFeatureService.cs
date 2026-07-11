using System.Text.Json.Nodes;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

internal sealed class VbaLanguageFeatureService
{
    private readonly VbaLanguageWorkspace workspace;

    public VbaLanguageFeatureService(VbaLanguageWorkspace workspace)
    {
        this.workspace = workspace;
    }

    public static object CreateInitializeResult()
    {
        return new
        {
            capabilities = new
            {
                textDocumentSync = 2,
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

    public static object[] CreateDiagnostics(string uri, string text)
        => CreateDiagnostics(uri, VbaSyntaxTree.ParseModule(uri, text));

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

    public object[] CreateCompletionItems(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        if (!TryGetTextDocumentPosition(parameters, out var uri, out var line, out var character))
        {
            return [];
        }

        var snapshot = workspace.CreateProjectSnapshot(uri, cancellationToken);
        var sourceItems = snapshot.SourceIndex
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
