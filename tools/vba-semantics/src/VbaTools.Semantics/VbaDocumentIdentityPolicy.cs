using VbaTools.SourceIdentities;

namespace VbaTools.Semantics;

internal static class VbaDocumentIdentityPolicy
{
    internal static bool TryIdentifyDocument(
        string uri,
        out VbaDocumentIdentity identity)
    {
        VbaSemanticWorkObservation.RecordDocumentIdentification();
        identity = default;
        // [DEBUG-415-uri-v1] Temporary failure-only observation; never reinterpret a failure as rejection.
        var phase = "localPathCheck";
        try
        {
            if (string.IsNullOrWhiteSpace(uri) || LooksLikeLocalPath(uri)) return false;
            phase = "Uri.TryCreate";
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return false;

            phase = "Uri.IsFile";
            if (parsed.IsFile)
            {
                // Reuse the admitted parse; reparsing adds no identity evidence and re-enters runtime URI canonicalization.
                phase = "SourceIdentity.TryFromUri";
                if (!SourceIdentity.TryFromUri(parsed, out var sourceIdentity))
                {
                    phase = "unresolvedFile.AbsoluteUri";
                    identity = new VbaDocumentIdentity(
                        VbaDocumentIdentityKind.UnresolvedFileUri,
                        parsed.AbsoluteUri);
                    return true;
                }

                identity = new VbaDocumentIdentity(
                    VbaDocumentIdentityKind.LocalFile,
                    sourceIdentity.Path);
                return true;
            }

            phase = "normalizedUri.AbsoluteUri";
            identity = new VbaDocumentIdentity(
                VbaDocumentIdentityKind.NormalizedUri,
                parsed.AbsoluteUri);
            return true;
        }
        catch (Exception error)
        {
            VbaDocumentIdentificationEvidence.Capture(error, uri, phase);
            throw;
        }
    }

    internal static bool SameDocument(
        string leftUri,
        string rightUri)
    {
        VbaDocumentIdentity left;
        try
        {
            if (!TryIdentifyDocument(leftUri, out left)) return false;
        }
        catch (Exception error)
        {
            VbaDocumentIdentificationEvidence.CaptureComparison(error, "left", rightUri);
            throw;
        }

        VbaDocumentIdentity right;
        try
        {
            if (!TryIdentifyDocument(rightUri, out right)) return false;
        }
        catch (Exception error)
        {
            VbaDocumentIdentificationEvidence.CaptureComparison(error, "right", leftUri);
            throw;
        }

        return left == right;
    }

    internal static IEnumerable<string> DistinctDocumentUris(
        IEnumerable<string> uris)
    {
        var identified = new HashSet<VbaDocumentIdentity>();
        var unidentified = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var uri in uris)
        {
            if (TryIdentifyDocument(uri, out var identity)
                    ? identified.Add(identity)
                    : unidentified.Add(uri))
            {
                yield return uri;
            }
        }
    }

    internal static string GetDocumentStableKey(string uri)
        => TryIdentifyDocument(uri, out var identity)
            ? identity.StableKey
            : string.Join("\u001e", "unidentified", uri);

    internal static bool TryIdentifyLocalDocumentPath(
        string path,
        out VbaDocumentIdentity identity)
    {
        identity = default;
        if (!TryNormalizePath(path, out var canonicalPath))
        {
            return false;
        }

        identity = new VbaDocumentIdentity(
            VbaDocumentIdentityKind.LocalFile,
            canonicalPath);
        return true;
    }

    internal static bool TryNormalizePath(string path, out string canonicalPath)
    {
        var identified = SourceIdentity.TryFromPath(path, out var identity);
        canonicalPath = identity.Path;
        return identified;
    }

    private static bool LooksLikeLocalPath(string value)
        => Path.IsPathFullyQualified(value)
            || value.StartsWith("\\\\", StringComparison.Ordinal)
            || value.Length >= 3
                && char.IsAsciiLetter(value[0])
                && value[1] == ':'
                && value[2] is '\\' or '/';

    public static string? TryGetLocalPath(string uri)
        => SourceIdentity.TryFromUri(uri, out var identity) ? identity.Path : null;
}

// [DEBUG-415-uri-v1] Temporary local diagnostics. Remove after the URI investigation.
internal static class VbaDocumentIdentificationEvidence
{
    internal const string Key = "DEBUG-415-uri-v1";
    private const int MaximumUriCodeUnits = 4096;

    internal static void Capture(Exception error, string uri, string phase)
    {
        if (error is OperationCanceledException) return;
        try
        {
            // A parser may throw the same exception again; never combine an old URI with a new inventory.
            error.Data.Remove(Key);
            var length = Math.Min(uri.Length, MaximumUriCodeUnits);
            var hex = new System.Text.StringBuilder(length * 4);
            for (var index = 0; index < length; index++)
                hex.Append(((int)uri[index]).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            error.Data[Key] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["phase"] = phase,
                ["uriCodeUnitLength"] = uri.Length,
                ["uriUtf16Hex"] = hex.ToString()
            };
        }
        catch (Exception) { /* Evidence must never replace the original exception. */ }
    }

    internal static void CaptureComparison(Exception error, string side, string? otherUri)
    {
        if (error is OperationCanceledException) return;
        try
        {
            if (error.Data[Key] is not Dictionary<string, object> evidence) return;
            evidence["stage"] = "comparison.sameDocument";
            evidence["comparisonSide"] = side;
            if (otherUri is null) return;
            var length = Math.Min(otherUri.Length, MaximumUriCodeUnits);
            var hex = new System.Text.StringBuilder(length * 4);
            for (var index = 0; index < length; index++)
                hex.Append(((int)otherUri[index]).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            evidence["comparisonOtherUriCodeUnitLength"] = otherUri.Length;
            evidence["comparisonOtherUriUtf16Hex"] = hex.ToString();
        }
        catch (Exception) { /* Comparison context must never replace the original exception. */ }
    }

    internal static void CaptureInventoryOrigin(Exception error, string uri, int index,
        IReadOnlyList<VbaSourceDocument> documents, IReadOnlyList<VbaSourceDefinition> references)
    {
        if (error is OperationCanceledException) return;
        try
        {
            if (error.Data[Key] is not Dictionary<string, object> evidence) return;
            evidence["stage"] = "inventory.admittedIdentities";
            evidence["inventoryIndex"] = index;
            evidence["originsComplete"] = false;
            var origins = new List<string>(3);
            evidence["origins"] = string.Empty;
            // [DEBUG-415-uri-v2] Snapshot only bounded identifiers; never retain source text or input objects.
            var details = new List<Dictionary<string, object>>();
            evidence["originDetails"] = details;
            evidence["originDetailsComplete"] = false;
            evidence["originScanComplete"] = false;
            evidence["originMatchesObserved"] = 0;
            var textRemaining = 16384;
            var inspected = 0;
            var matches = 0;
            var fieldsComplete = true;
            for (var documentIndex = 0; documentIndex < documents.Count; documentIndex++)
            {
                if (++inspected > 100000) return;
                var document = documents[documentIndex];
                if (string.Equals(document.Uri, uri, StringComparison.Ordinal))
                    Add("documentUri", documentIndex, -1, document, null);
                for (var definitionIndex = 0; definitionIndex < document.Definitions.Count; definitionIndex++)
                {
                    if (++inspected > 100000) return;
                    var definition = document.Definitions[definitionIndex];
                    if (string.Equals(definition.Uri, uri, StringComparison.Ordinal))
                        Add("sourceDefinitionUri", documentIndex, definitionIndex, document, definition);
                }
            }
            for (var definitionIndex = 0; definitionIndex < references.Count; definitionIndex++)
            {
                if (++inspected > 100000) return;
                var definition = references[definitionIndex];
                if (string.Equals(definition.Uri, uri, StringComparison.Ordinal))
                    Add("activeReferenceDefinitionUri", -1, definitionIndex, null, definition);
            }
            // Preserve the historical category order independently of encounter order.
            evidence["origins"] = string.Join(',', new[] { "documentUri", "sourceDefinitionUri",
                "activeReferenceDefinitionUri" }.Where(origins.Contains));
            evidence["originsComplete"] = true;
            evidence["originScanComplete"] = true;
            evidence["originDetailsComplete"] = matches == details.Count && fieldsComplete;

            void Add(string category, int documentIndex, int definitionIndex,
                VbaSourceDocument? document, VbaSourceDefinition? definition)
            {
                matches++;
                evidence["originMatchesObserved"] = matches;
                if (!origins.Contains(category))
                {
                    origins.Add(category);
                    evidence["origins"] = string.Join(',', origins);
                }
                if (details.Count == 32) return;
                var entry = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["category"] = category,
                    ["documentIndex"] = documentIndex,
                    ["definitionIndex"] = definitionIndex,
                    ["fieldsComplete"] = false
                };
                details.Add(entry);
                var entryComplete = true;
                Text("documentUri", document?.Uri);
                Text("moduleName", document?.ModuleName ?? definition?.ModuleName);
                if (definition is null)
                {
                    entry["fieldsComplete"] = entryComplete;
                    return;
                }
                Text("definitionName", definition.Name);
                Text("definitionModuleName", definition.ModuleName);
                Text("definitionKind", definition.Kind.ToString());
                Text("identityOrigin", definition.Identity.Origin.ToString());
                Text("identityName", definition.Identity.Name);
                Text("identitySourceUri", definition.Identity.SourceUri);
                Text("identityKind", definition.Identity.Kind?.ToString());
                if (definition.Identity.DeclarationRange is { } declaration)
                {
                    entry["identityStartLine"] = declaration.Start.Line;
                    entry["identityStartCharacter"] = declaration.Start.Character;
                    entry["identityEndLine"] = declaration.End.Line;
                    entry["identityEndCharacter"] = declaration.End.Character;
                }
                Text("referenceName", definition.Identity.ReferenceName);
                Text("parentTypeName", definition.Identity.ParentTypeName);
                Text("propertyAccessorKind", definition.Identity.PropertyAccessorKind?.ToString());
                entry["startLine"] = definition.Range.Start.Line;
                entry["startCharacter"] = definition.Range.Start.Character;
                entry["endLine"] = definition.Range.End.Line;
                entry["endCharacter"] = definition.Range.End.Character;
                entry["fieldsComplete"] = entryComplete;

                void Text(string name, string? value)
                {
                    if (value is null) return;
                    var length = Math.Min(value.Length, Math.Min(2048, textRemaining));
                    textRemaining -= length;
                    entry[name] = value[..length];
                    var validText = true;
                    for (var position = 0; position < length; position++)
                    {
                        if (char.IsHighSurrogate(value[position]))
                        {
                            if (++position >= length || !char.IsLowSurrogate(value[position])) validText = false;
                        }
                        else if (char.IsLowSurrogate(value[position])) validText = false;
                    }
                    if (length != value.Length || !validText)
                    {
                        entryComplete = false;
                        fieldsComplete = false;
                    }
                }
            }
        }
        catch (Exception) { /* Keep the original failure even if classification fails. */ }
    }
}
