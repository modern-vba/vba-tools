using System.Globalization;
using System.Text;
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

    public static object[] CreateDiagnostics(
        IReadOnlyList<VbaDiagnostic> diagnostics,
        bool supportsRelatedInformation = false)
        => diagnostics
            .Select(diagnostic =>
            {
                var details = diagnostic.Details ?? [];
                var fallbackDetails = supportsRelatedInformation
                    ? details.Where(detail => detail.Location is null)
                    : details;
                var fallbackText = fallbackDetails
                    .Select(detail => detail.FallbackText)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var projected = new Dictionary<string, object?>
                {
                    ["code"] = diagnostic.Code,
                    ["message"] = fallbackText.Length == 0
                        ? diagnostic.Message
                        : $"{diagnostic.Message}\n{string.Join('\n', fallbackText)}",
                    ["range"] = diagnostic.Range,
                    ["severity"] = 1,
                    ["source"] = diagnostic.Source
                };
                if (supportsRelatedInformation)
                {
                    var relatedInformation = details
                        .Where(detail => detail.Location is not null)
                        .Select(detail => new
                        {
                            location = new
                            {
                                uri = detail.Location!.Uri,
                                range = detail.Location.Range
                            },
                            message = detail.RelatedMessage
                        })
                        .ToArray();
                    if (relatedInformation.Length > 0)
                    {
                        projected["relatedInformation"] = relatedInformation;
                    }
                }

                if (diagnostic.Data is not null)
                {
                    projected["data"] = diagnostic.Data;
                }

                return projected;
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

    public static object? CreateDefinitionLocations(
        IReadOnlyList<VbaDefinitionLocation> locations)
        => locations.Count switch
        {
            0 => null,
            1 => CreateLocation(locations[0]),
            _ => CreateLocations(locations)
        };

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
        => completion.Candidates
            .Select(CreateCompletionItem)
            .ToArray<object>();

    public static object? CreateHover(VbaHoverResult? hover)
    {
        if (hover is null
            || hover.Definitions.Count == 0
                && hover.ResolvedProjectedEventContracts.Count == 0)
        {
            return null;
        }

        var projectedEvents = hover.ResolvedProjectedEventContracts;
        var value = projectedEvents.Count == 1
                && hover.Definitions.Count == 0
                && !hover.IsConditionalFamily
            ? CreateProjectedEventHoverValue(projectedEvents[0])
            : hover.IsConditionalFamily
            ? $"**{hover.CanonicalName} [#If]**\n\n"
                + string.Join(
                    "\n\n",
                    hover.Definitions
                        .Select(CreateConditionalHoverVariant)
                        .Concat(projectedEvents.Select(
                            CreateProjectedEventHoverVariant)))
            : CreateOrdinaryHoverValue(hover.Definitions[0]);
        return new
        {
            contents = new
            {
                kind = "markdown",
                value
            },
            range = hover.Range
        };
    }

    private static string CreateProjectedEventHoverValue(
        VbaResolvedEventContract contract)
    {
        var declaration = CreateHoverDeclarationBlock(
            contract.Signature?.Label ?? contract.Name);
        return string.IsNullOrWhiteSpace(contract.Documentation)
            ? declaration
            : $"{contract.Documentation}\n\n---\n\n{declaration}";
    }

    private static string CreateProjectedEventHoverVariant(
        VbaResolvedEventContract contract)
    {
        var conditionalMarker = contract.IsConditionalContract
            ? " [#If]"
            : string.Empty;
        var block = CreateHoverDeclarationBlock(
            $"{contract.Signature?.Label ?? contract.Name}{conditionalMarker}");
        return string.IsNullOrWhiteSpace(contract.Documentation)
            ? block
            : $"{block}\n\n{contract.Documentation}";
    }

    private static string CreateOrdinaryHoverValue(VbaSourceDefinition definition)
    {
        var declaration = CreateHoverDeclarationBlock(
            definition.Signature?.Label ?? definition.DeclarationLabel ?? definition.Name);
        return string.IsNullOrWhiteSpace(definition.Documentation)
            ? declaration
            : $"{definition.Documentation}\n\n---\n\n{declaration}";
    }

    private static string CreateConditionalHoverVariant(VbaSourceDefinition definition)
    {
        var declaration = definition.Signature?.Label
            ?? definition.DeclarationLabel
            ?? definition.Name;
        var conditionalMarker = definition.ConditionalCompilationPath is { IsEmpty: false }
            ? " [#If]"
            : string.Empty;
        var block = CreateHoverDeclarationBlock($"{declaration}{conditionalMarker}");
        return string.IsNullOrWhiteSpace(definition.Documentation)
            ? block
            : $"{block}\n\n{definition.Documentation}";
    }

    private static string CreateHoverDeclarationBlock(string declaration)
        => $"```vba\n{declaration}\n```";

    public static object? CreateSignatureHelp(
        VbaSignatureHelp? signatureHelp,
        VbaSignatureHelpClientCapabilities? clientCapabilities = null)
    {
        if (signatureHelp is null)
        {
            return null;
        }

        clientCapabilities ??= VbaSignatureHelpClientCapabilities.None;
        var signatures = signatureHelp.Signatures
            .Select(variant =>
            {
                var projected = new Dictionary<string, object?>
                {
                    ["label"] = variant.DisplayLabel,
                    ["parameters"] = variant.Signature.Parameters
                        .Select(CreateSignatureParameter)
                        .ToArray()
                };
                if (clientCapabilities.ActiveParameterSupport)
                {
                    if (variant.ActiveParameter is int activeParameter)
                    {
                        projected["activeParameter"] = activeParameter;
                    }
                    else if (clientCapabilities.NoActiveParameterSupport)
                    {
                        projected["activeParameter"] = null;
                    }
                }

                return projected;
            })
            .ToArray();
        var result = new Dictionary<string, object?>
        {
            ["signatures"] = signatures,
            ["activeSignature"] = signatureHelp.ActiveSignature
        };
        if (!clientCapabilities.ActiveParameterSupport)
        {
            if (signatureHelp.ActiveParameter is int activeParameter)
            {
                result["activeParameter"] = activeParameter;
            }
            else if (clientCapabilities.NoActiveParameterSupport)
            {
                result["activeParameter"] = null;
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, object?> CreateSignatureParameter(VbaCallableParameter parameter)
    {
        var projected = new Dictionary<string, object?>
        {
            ["label"] = parameter.Label
        };
        var documentation = ToMarkup(parameter.Documentation);
        if (documentation is not null)
        {
            projected["documentation"] = documentation;
        }

        return projected;
    }

    public static object? CreateWorkspaceEdit(VbaRenamePlan? plan)
    {
        if (plan is null)
        {
            return null;
        }

        if (plan.FileRenames.Count == 0)
        {
            return new
            {
                changes = plan.Changes.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Select(edit => new
                    {
                        range = edit.Range,
                        newText = edit.NewText
                    }).ToArray(),
                    StringComparer.OrdinalIgnoreCase)
            };
        }

        var documentChanges = new List<object>();
        foreach (var (uri, edits) in plan.Changes
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            documentChanges.Add(new
            {
                textDocument = new
                {
                    uri,
                    version = (int?)null
                },
                edits = edits.Select(edit => new
                {
                    range = edit.Range,
                    newText = edit.NewText
                }).ToArray()
            });
        }

        foreach (var rename in plan.FileRenames)
        {
            documentChanges.Add(new
            {
                kind = "rename",
                oldUri = rename.OldUri,
                newUri = rename.NewUri,
                options = new
                {
                    overwrite = rename.Overwrite,
                    ignoreIfExists = false
                }
            });
        }

        return new
        {
            documentChanges = documentChanges.ToArray()
        };
    }

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

    private static IReadOnlyDictionary<string, object?> CreateCompletionItem(
        VbaCompletionCandidate candidate)
    {
        var item = new Dictionary<string, object?>
        {
            ["label"] = candidate.Label,
            ["kind"] = GetCompletionKind(candidate),
            ["sortText"] = CreateCompletionSortText(candidate)
        };
        if (!string.IsNullOrWhiteSpace(candidate.FilterText))
        {
            item["filterText"] = candidate.FilterText;
        }

        if (candidate.RetriggerCompletion)
        {
            item["data"] = new
            {
                retriggerCompletion = true
            };
        }

        var detail = CreateCompletionDetail(candidate);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            item["detail"] = detail;
        }

        var documentation = CreateCompletionDocumentation(candidate);
        if (documentation is not null)
        {
            item["documentation"] = documentation;
        }

        if (candidate.TextEdit is not null)
        {
            item["textEdit"] = new
            {
                range = candidate.TextEdit.Range,
                newText = candidate.TextEdit.NewText
            };
        }
        else if (!string.IsNullOrWhiteSpace(candidate.InsertText))
        {
            item["insertText"] = candidate.InsertText;
        }

        return item;
    }

    private static object? CreateCompletionDocumentation(
        VbaCompletionCandidate candidate)
    {
        if (candidate.SignaturePresentations.Count == 0)
        {
            return null;
        }

        var sections = candidate.SignaturePresentations
            .Select(CreateCompletionSignatureSection)
            .ToArray();
        return ToMarkup(string.Join("\n\n---\n\n", sections));
    }

    private static string CreateCompletionSignatureSection(
        VbaCompletionSignaturePresentation presentation)
    {
        var result = new StringBuilder()
            .Append("```vba\n")
            .Append(presentation.DisplayLabel)
            .Append("\n```");
        if (presentation.DocumentationVariants.Count == 1)
        {
            result.Append("\n\n")
                .Append(presentation.DocumentationVariants[0]);
        }
        else if (presentation.DocumentationVariants.Count > 1)
        {
            result.Append("\n\n**Documentation variants**");
            for (var index = 0;
                 index < presentation.DocumentationVariants.Count;
                 index++)
            {
                result.Append("\n\n")
                    .Append(index + 1)
                    .Append(". ")
                    .Append(presentation.DocumentationVariants[index]);
            }
        }

        return result.ToString();
    }

    private static string? CreateCompletionDetail(VbaCompletionCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Detail))
        {
            return candidate.Detail;
        }

        if (candidate.Kind == VbaCompletionCandidateKind.SourceQualifier)
        {
            return "Module qualifier";
        }

        if (candidate.Kind == VbaCompletionCandidateKind.ReferenceQualifier)
        {
            return "Reference qualifier";
        }

        if (candidate.Kind == VbaCompletionCandidateKind.NamedArgument)
        {
            return candidate.IsConditionalFamily ? "[#If]" : null;
        }

        var definition = candidate.Definition;
        if (candidate.Kind != VbaCompletionCandidateKind.Definition || definition is null)
        {
            return null;
        }

        var detail = !string.IsNullOrWhiteSpace(definition.DeclarationLabel)
            ? definition.DeclarationLabel
            : !string.IsNullOrWhiteSpace(definition.Signature?.Label)
                ? definition.Signature.Label
                : definition.Kind switch
        {
            VbaSourceDefinitionKind.Module => $"Module {definition.Name}",
            VbaSourceDefinitionKind.Class => $"Class {definition.Name}",
            VbaSourceDefinitionKind.Form => $"Form {definition.Name}",
            VbaSourceDefinitionKind.Enum => $"Enum {definition.Name}",
            VbaSourceDefinitionKind.Type => $"Type {definition.Name}",
            _ => definition.Name
        };
        return candidate.IsConditionalFamily ? $"{detail} [#If]" : detail;
    }

    private static string CreateCompletionSortText(VbaCompletionCandidate candidate)
    {
        const int unrankedSortGroup = 3;
        var effectiveInsertionText = candidate.TextEdit?.NewText
            ?? candidate.InsertText
            ?? candidate.Label;
        return string.Join(
            "|",
            (candidate.SortRank ?? unrankedSortGroup).ToString(
                "D2",
                CultureInfo.InvariantCulture),
            candidate.Label.ToUpperInvariant(),
            ((int)candidate.Kind).ToString("D2", CultureInfo.InvariantCulture),
            candidate.Definition is null
                ? string.Empty
                : ((int)candidate.Definition.Kind).ToString(
                    "D2",
                    CultureInfo.InvariantCulture),
            effectiveInsertionText.ToUpperInvariant());
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

    private static int GetCompletionKind(VbaCompletionCandidate candidate)
        => candidate.Kind switch
        {
            VbaCompletionCandidateKind.Definition when candidate.Definition is not null =>
                GetDefinitionCompletionKind(candidate.Definition.Kind),
            VbaCompletionCandidateKind.SourceQualifier
                or VbaCompletionCandidateKind.ReferenceQualifier => 9,
            VbaCompletionCandidateKind.NamedArgument => 5,
            VbaCompletionCandidateKind.Label => 18,
            _ => 14
        };

    private static int GetDefinitionCompletionKind(VbaSourceDefinitionKind kind)
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
