using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.SourceModel;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Projects source-model feature results into LSP response payloads.
/// </summary>
internal static class VbaLspFeatureProjection
{
    public static object CreateInitializeResult(VbaLspCapabilityContract contract)
    {
        return new
        {
            capabilities = new
            {
                textDocumentSync = contract.TextDocumentSync,
                definitionProvider = contract.DefinitionProvider,
                referencesProvider = contract.ReferencesProvider,
                documentSymbolProvider = contract.DocumentSymbolProvider,
                workspaceSymbolProvider = contract.WorkspaceSymbolProvider,
                hoverProvider = contract.HoverProvider,
                documentFormattingProvider = contract.DocumentFormattingProvider,
                renameProvider = new
                {
                    prepareProvider = contract.RenamePrepareProvider
                },
                signatureHelpProvider = new
                {
                    triggerCharacters = contract.SignatureHelpTriggerCharacters,
                    retriggerCharacters = contract.SignatureHelpRetriggerCharacters
                },
                completionProvider = new
                {
                    triggerCharacters = contract.CompletionTriggerCharacters
                },
                semanticTokensProvider = new
                {
                    legend = new
                    {
                        tokenTypes = contract.SemanticTokenTypes,
                        tokenModifiers = contract.SemanticTokenModifiers
                    },
                    full = contract.SemanticTokensFull,
                    range = contract.SemanticTokensRange
                }
            },
            serverInfo = new
            {
                name = contract.ServerName,
                version = contract.ServerVersion
            }
        };
    }

    public static object[] CreateDiagnostics(IReadOnlyList<VbaDiagnostic> diagnostics)
        => diagnostics
            .Select(diagnostic => new
            {
                code = diagnostic.Code,
                message = diagnostic.Message,
                range = diagnostic.Range,
                severity = 1,
                source = diagnostic.Source
            })
            .ToArray<object>();

    public static object[] CreateDocumentSymbols(IReadOnlyList<VbaSourceDefinition> definitions)
        => definitions
            .Select(definition => new
            {
                name = definition.Name,
                kind = GetSymbolKind(definition.Kind),
                range = definition.Range,
                selectionRange = definition.Range
            })
            .ToArray<object>();

    public static object? CreateLocation(VbaDefinitionLocation? location)
        => location is null
            ? null
            : new
            {
                uri = location.Uri,
                range = location.Range
            };

    public static object[] CreateLocations(IReadOnlyList<VbaDefinitionLocation> locations)
        => locations
            .Select(location => new
            {
                uri = location.Uri,
                range = location.Range
            })
            .ToArray<object>();

    public static object[] CreateWorkspaceSymbols(IReadOnlyList<VbaWorkspaceSymbol> symbols)
        => symbols
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

    public static object[] CreateCompletionItems(VbaCompletionResult completion)
    {
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

        return sourceItems
            .Concat(vocabularyItems)
            .GroupBy(item => item.label, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.label, StringComparer.OrdinalIgnoreCase)
            .ToArray<object>();
    }

    public static object? CreateHover(VbaSourceDefinition? definition)
    {
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

    public static object? CreateSignatureHelp(VbaSignatureHelp? signatureHelp)
    {
        if (signatureHelp is null)
        {
            return null;
        }

        var signature = new Dictionary<string, object?>
        {
            ["label"] = signatureHelp.Signature.Label,
            ["parameters"] = signatureHelp.Signature.Parameters.Select(CreateSignatureParameter).ToArray()
        };
        var documentation = ToMarkup(signatureHelp.Signature.Documentation);
        if (documentation is not null)
        {
            signature["documentation"] = documentation;
        }

        return new
        {
            signatures = new[]
            {
                signature
            },
            activeSignature = 0,
            activeParameter = signatureHelp.ActiveParameter
        };
    }

    private static IReadOnlyDictionary<string, object?> CreateSignatureParameter(VbaCallableParameter parameter)
    {
        var projected = new Dictionary<string, object?>
        {
            ["label"] = parameter.Name
        };
        var documentation = ToMarkup(parameter.Documentation);
        if (documentation is not null)
        {
            projected["documentation"] = documentation;
        }

        return projected;
    }

    public static object? CreateWorkspaceEdit(IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>>? changes)
        => changes is null
            ? null
            : new
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

    public static object[] CreateFormattingEdits(VbaTextEdit? edit)
        => edit is null
            ? []
            :
            [
                new
                {
                    range = edit.Range,
                    newText = edit.NewText
                }
            ];

    public static object CreateSemanticTokens(IReadOnlyList<int> data)
        => new
        {
            data
        };

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
