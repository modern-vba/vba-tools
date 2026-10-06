using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VbaDev.App.Projects;
using VbaTools.Semantics;
using VbaTools.Syntax;

namespace VbaDev.App.Workbooks;

/// <summary>Retains bounded local failure evidence without changing analysis or its terminal result.</summary>
internal sealed class SourceAnalysisEvidenceStore(
    string? directory = null,
    Func<string?>? diagnosticRunRootProvider = null)
{
    internal const int MaximumReports = 20;
    internal const int MaximumFailures = 16;
    internal const int MaximumSources = 128;
    internal const int MaximumReferences = 64;
    internal const int MaximumReportBytes = 2 * 1024 * 1024;
    private const int MaximumExceptionCharacters = 16384;
    private const string FilePrefix = "source-analysis-";
    private static readonly object StorageGate = new();
    private static readonly Regex ReportName = new(
        @"\Asource-analysis-\d{8}T\d{13}Z-[0-9a-f]{32}\.json\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex DiagnosticRunName = new(
        @"\Arun-[0-9]{8}T[0-9]{9}Z-[0-9a-f]{16}\z",
        RegexOptions.CultureInvariant);

    internal string Save(ResolvedProjectContext context, string operation, VbaSourceAnalysisReport report)
    {
        if (report.Failures.IsEmpty || report.Failures.All(failure => IsCancellationOnly(failure.Exception)))
            return string.Empty;

        string? targetDirectory = directory;
        string? diagnosticRunId = null;
        string? partialPath = null;
        try
        {
            (targetDirectory, diagnosticRunId) = ResolveDirectory();
            var timestamp = DateTimeOffset.UtcNow;
            var invocationId = Guid.NewGuid().ToString("N");
            var name = FilePrefix + timestamp.ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture)
                + "-" + invocationId + ".json";
            var path = Path.Combine(targetDirectory, name);
            var bytes = Serialize(context, operation, report, timestamp, invocationId, diagnosticRunId);
            if (bytes.Length > MaximumReportBytes)
                throw new IOException("The bounded failure report exceeded its maximum serialized size.");

            lock (StorageGate)
            {
                EnsureOrdinaryDirectory(targetDirectory);
                partialPath = path + ".partial";
                using (var stream = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                EnsureOrdinaryDirectory(targetDirectory);
                File.Move(partialPath, path);
                partialPath = null;
                var retentionWarning = Prune(targetDirectory);
                return $"Source-analysis failure evidence saved: {path}{Environment.NewLine}" + retentionWarning;
            }
        }
        catch (Exception error)
        {
            TryRemovePartial(partialPath);
            return $"Warning: Source-analysis failure evidence could not be saved to '{targetDirectory ?? "the local diagnostics directory"}': "
                + SafeExceptionText(error, 2048)
                + Environment.NewLine
                + "Check the diagnostics directory permissions and available disk space; preserve the following failure details."
                + Environment.NewLine + RenderFallback(report);
        }
    }

    private (string Directory, string? DiagnosticRunId) ResolveDirectory()
    {
        if (directory is not null) return (Path.GetFullPath(directory), null);
        var diagnosticRunRoot = (diagnosticRunRootProvider
            ?? (() => Environment.GetEnvironmentVariable("VBA_TOOLS_DIAGNOSTIC_RUN_ROOT")))();
        if (diagnosticRunRoot is not null)
        {
            if (!Path.IsPathFullyQualified(diagnosticRunRoot))
                throw new IOException("The diagnostic run root must be a local absolute path with a valid run ID.");
            var fullRoot = Path.GetFullPath(diagnosticRunRoot);
            if (OperatingSystem.IsWindows() && fullRoot.StartsWith(@"\\", StringComparison.Ordinal))
                throw new IOException("The diagnostic run root must be a local absolute path with a valid run ID.");
            var runId = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullRoot));
            if (!DiagnosticRunName.IsMatch(runId))
                throw new IOException("The diagnostic run root must be a local absolute path with a valid run ID.");
            return (Path.Combine(fullRoot, "source-analysis"), runId);
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            throw new IOException("The operating system did not provide a local application data directory.");
        return (Path.GetFullPath(Path.Combine(local, "VbaTools", "Diagnostics", "source-analysis")), null);
    }

    private static byte[] Serialize(ResolvedProjectContext context, string operation, VbaSourceAnalysisReport report,
        DateTimeOffset timestamp, string invocationId, string? diagnosticRunId)
    {
        var budget = new TextBudget();
        var failures = report.Failures.Take(MaximumFailures).Select(failure => new
        {
            scope = budget.Take(failure.Scope),
            sourceUri = budget.Take(failure.SourceUri),
            activeSourcePath = budget.Take(failure.ActiveSourcePath),
            phase = budget.Take(failure.Phase),
            message = budget.Take(failure.Message, 4096),
            exception = CaptureException(failure.Exception, budget)
        }).ToArray();
        var sources = report.SyntaxTrees.Take(MaximumSources).Select(source => new
        {
            uri = budget.Take(source.Uri),
            characterCount = source.Text.Length,
            hashDomain = "captured-parser-text:utf8",
            sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Text)))
        }).ToArray();
        var inputs = report.SemanticInputs;
        var references = inputs?.ReferenceCatalogIdentities.OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Take(MaximumReferences).Select(entry => new
            {
                name = budget.Take(entry.Key),
                guid = budget.Take(entry.Value.Guid),
                major = entry.Value.MajorVersion,
                minor = entry.Value.MinorVersion,
                lcid = entry.Value.Lcid,
                path = budget.Take(entry.Value.Path),
                origin = budget.Take(inputs.ReferenceCatalogSources.TryGetValue(entry.Key, out var origin)
                    ? origin.ToString() : null)
            }).ToArray();
        // Keep project and loaded-build identities available even when exception text consumes its budget.
        var identityBudget = new TextBudget(16 * 1024);
        var assemblies = new[] { Assembly.GetEntryAssembly(), typeof(SourceAnalysisEvidenceStore).Assembly,
                typeof(VbaSyntaxTree).Assembly, typeof(VbaProjectSemanticInputs).Assembly, typeof(object).Assembly,
                typeof(Uri).Assembly, typeof(Enumerable).Assembly }
            .OfType<Assembly>().Distinct().Select(assembly => CaptureAssembly(assembly, identityBudget)).ToArray();
        var executable = CaptureExecutable(identityBudget);
        var project = new
        {
            root = identityBudget.Take(context.ProjectRoot),
            manifestPath = identityBudget.Take(context.ManifestPath),
            projectName = identityBudget.Take(context.Manifest.ProjectName),
            document = identityBudget.Take(context.DocumentName),
            operation = identityBudget.Take(operation),
            admissionPurpose = identityBudget.Take(report.AdmissionPurpose),
            sourceDirectory = identityBudget.Take(report.SourceDirectory ?? context.DocumentSourceSetPath),
            templatePath = identityBudget.Take(context.TemplateDocumentPath),
            activeCodePage = report.ActiveCodePage
        };
        var runtime = new
        {
            framework = identityBudget.Take(RuntimeInformation.FrameworkDescription),
            version = identityBudget.Take(Environment.Version.ToString()),
            runtimeIdentifier = identityBudget.Take(RuntimeInformation.RuntimeIdentifier),
            operatingSystem = identityBudget.Take(RuntimeInformation.OSDescription),
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processId = Environment.ProcessId,
            assemblies,
            executable
        };
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = "1.0",
            ["kind"] = "vba-dev-source-analysis-failure",
            ["timestampUtc"] = timestamp,
            ["invocationId"] = invocationId,
            ["project"] = project,
            ["failures"] = failures,
            ["sources"] = sources,
            ["semanticInputs"] = new
            {
                acquired = inputs is not null,
                referenceCount = inputs?.ReferenceCatalogIdentities.Count,
                selectedReferenceCount = inputs?.ReferenceSelection?.References.Length,
                hostEventsAcquired = inputs?.IntrinsicHostEvents is not null,
                references
            },
            ["runtime"] = runtime,
            ["truncation"] = new
            {
                failuresOmitted = Math.Max(0, report.Failures.Length - failures.Length),
                sourcesOmitted = Math.Max(0, report.SyntaxTrees.Length - sources.Length),
                referencesOmitted = Math.Max(0, (inputs?.ReferenceCatalogIdentities.Count ?? 0) - (references?.Length ?? 0)),
                textTruncated = budget.Truncated || identityBudget.Truncated
            },
            ["limitations"] = "Source hashes identify captured parser text encoded as UTF-8, not original file bytes. "
                + "Only parsed trees and successfully acquired semantic identities are available. "
                + "No source text, source copies, environment dump, or external upload is produced. "
                + "Exception messages may contain paths or other application-provided details."
        };
        if (diagnosticRunId is not null) payload.Add("diagnosticRunId", diagnosticRunId);
        return JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static object? CaptureException(Exception? error, TextBudget budget)
    {
        if (error is null) return null;
        return new
        {
            type = budget.Take(error.GetType().FullName),
            hresult = error.HResult,
            details = budget.Take(SafeExceptionText(error, MaximumExceptionCharacters), MaximumExceptionCharacters),
            stackTrace = budget.Take(SafeStack(error), 8192),
            uriIdentification = CaptureUriIdentification(error, budget),
            positionSyntax = CapturePositionSyntax(error, budget),
            lexerAdvance = CaptureLexerAdvance(error)
        };
    }

    // [DEBUG-415-lexer-v1] Temporary primitive-only capture; source contents are never copied.
    private static object? CaptureLexerAdvance(Exception error)
    {
        var depth = 0;
        for (Exception? current = error; current is not null && depth < 8; current = current.InnerException, depth++)
        {
            try
            {
                if (current.Data["DEBUG-415-lexer-v1"] is not Dictionary<string, object> evidence)
                    continue;
                if (evidence.GetValueOrDefault("stateIsNull") is not bool stateIsNull
                    || evidence.GetValueOrDefault("sourceTextIsNull") is not bool sourceTextIsNull
                    || evidence.GetValueOrDefault("textIsNull") is not bool textIsNull
                    || evidence.GetValueOrDefault("positionIsNull") is not bool positionIsNull)
                    return new { status = "unavailable" };

                var phase = evidence.GetValueOrDefault("phase") as string;
                if (phase is not ("ReadIdentifierOrKeyword.Advance"
                    or "ReadIdentifierOrKeyword.StartOffset"
                    or "ReadIdentifierOrKeyword.PositionBeforeSlice"
                    or "ReadIdentifierOrKeyword.Slice"
                    or "LexerState.Slice"))
                    return new { status = "unavailable" };

                var rawHash = evidence.GetValueOrDefault("sourceSha256") as string;
                var hash = rawHash is { Length: 64 } && rawHash.All(char.IsAsciiHexDigit)
                    ? rawHash : null;
                return new
                {
                    status = "available",
                    phase,
                    stateIsNull,
                    sourceTextIsNull,
                    textIsNull,
                    textReadFailed = evidence.GetValueOrDefault("textReadFailed") as bool?,
                    positionIsNull,
                    positionReadFailed = evidence.GetValueOrDefault("positionReadFailed") as bool?,
                    cachedPositionIsNull = evidence.GetValueOrDefault("cachedPositionIsNull") as bool?,
                    rawLine = evidence.GetValueOrDefault("rawLine") as int?,
                    rawCharacter = evidence.GetValueOrDefault("rawCharacter") as int?,
                    rawOffset = evidence.GetValueOrDefault("rawOffset") as int?,
                    positionLine = evidence.GetValueOrDefault("positionLine") as int?,
                    positionCharacter = evidence.GetValueOrDefault("positionCharacter") as int?,
                    positionOffset = evidence.GetValueOrDefault("positionOffset") as int?,
                    startLine = evidence.GetValueOrDefault("startLine") as int?,
                    startCharacter = evidence.GetValueOrDefault("startCharacter") as int?,
                    startOffset = evidence.GetValueOrDefault("startOffset") as int?,
                    identifierLength = evidence.GetValueOrDefault("identifierLength") as int?,
                    loopIndex = evidence.GetValueOrDefault("loopIndex") as int?,
                    sliceStartOffset = evidence.GetValueOrDefault("sliceStartOffset") as int?,
                    sliceEndOffset = evidence.GetValueOrDefault("sliceEndOffset") as int?,
                    slicePreLine = evidence.GetValueOrDefault("slicePreLine") as int?,
                    slicePreCharacter = evidence.GetValueOrDefault("slicePreCharacter") as int?,
                    slicePreOffset = evidence.GetValueOrDefault("slicePreOffset") as int?,
                    slicePostLine = evidence.GetValueOrDefault("slicePostLine") as int?,
                    slicePostCharacter = evidence.GetValueOrDefault("slicePostCharacter") as int?,
                    slicePostOffset = evidence.GetValueOrDefault("slicePostOffset") as int?,
                    slicePreSourceLength = evidence.GetValueOrDefault("slicePreSourceLength") as int?,
                    slicePrePostSourceSameReference =
                        evidence.GetValueOrDefault("slicePrePostSourceSameReference") as bool?,
                    sourceLength = evidence.GetValueOrDefault("sourceLength") as int?,
                    sourceHashDomain = "utf16-platform-endian-code-units",
                    sourceSha256 = hash,
                    sourceHashComplete = evidence.GetValueOrDefault("sourceHashComplete") is true && hash is not null
                };
            }
            catch (Exception)
            {
                return new { status = "unavailable" };
            }
        }

        return null;
    }

    // [DEBUG-415-syntax-v1] Temporary whitelist for failed position token graphs.
    private static object? CapturePositionSyntax(Exception error, TextBudget budget)
    {
        var depth = 0;
        for (Exception? current = error; current is not null && depth < 8; current = current.InnerException, depth++)
        {
            try
            {
                if (current.Data["DEBUG-415-syntax-v1"] is not Dictionary<string, object> evidence)
                    continue;
                if (evidence.GetValueOrDefault("uri") is not string rawUri
                    || evidence.GetValueOrDefault("uriLength") is not int uriLength || uriLength < -1
                    || evidence.GetValueOrDefault("positionIsNull") is not bool positionIsNull)
                    return new { status = "unavailable" };

                var phase = evidence.GetValueOrDefault("phase") as string;
                if (phase is not ("TryGetLabelReference.prefix" or "FindIdentifier.query"
                    or "GetProcedureSyntaxWords.prefix"))
                    return new { status = "unavailable" };

                var uri = budget.TakeIdentifier(rawUri, 2048);
                var rawKind = evidence.GetValueOrDefault("firstBadReferenceKind") as string;
                var rawStatus = evidence.GetValueOrDefault("postFaultGraphStatus") as string;
                var referenceKind = rawKind is "none" or "significantTokens" or "count" or "indexer"
                    or "token" or "range" or "start" ? rawKind : null;
                var graphStatus = rawStatus is "intact" or "broken" or "unverified" ? rawStatus : null;
                return new
                {
                    status = "available",
                    phase,
                    uri,
                    uriLength,
                    uriCaptureComplete = evidence.GetValueOrDefault("uriCaptureComplete") is true
                        && uriLength == rawUri.Length && uri == rawUri,
                    positionIsNull,
                    positionLine = evidence.GetValueOrDefault("positionLine") as int?,
                    positionCharacter = evidence.GetValueOrDefault("positionCharacter") as int?,
                    positionOffset = evidence.GetValueOrDefault("positionOffset") as int?,
                    statementStartOffset = evidence.GetValueOrDefault("statementStartOffset") as int?,
                    statementEndOffset = evidence.GetValueOrDefault("statementEndOffset") as int?,
                    statementNextOffset = evidence.GetValueOrDefault("statementNextOffset") as int?,
                    significantTokenCount = evidence.GetValueOrDefault("significantTokenCount") as int?,
                    inspectedTokenCount = evidence.GetValueOrDefault("inspectedTokenCount") as int?,
                    inspectionComplete = evidence.GetValueOrDefault("inspectionComplete") as bool?,
                    firstBadIndex = evidence.GetValueOrDefault("firstBadIndex") as int?,
                    firstBadReferenceKind = referenceKind,
                    postFaultGraphStatus = graphStatus
                };
            }
            catch (Exception)
            {
                return new { status = "unavailable" };
            }
        }

        return null;
    }

    // [DEBUG-415-uri-v1] Temporary whitelist, not a general Exception.Data serializer.
    private static object? CaptureUriIdentification(Exception error, TextBudget budget)
    {
        try
        {
            if (error.Data["DEBUG-415-uri-v1"] is not Dictionary<string, object> evidence) return null;
            if (evidence.GetValueOrDefault("uriCodeUnitLength") is not int length || length < 0
                || evidence.GetValueOrDefault("uriUtf16Hex") is not string hex)
                return new { status = "unavailable" };
            var capturedHex = budget.TakeHex(hex, 4096 * 4);
            var phase = budget.Take(evidence.GetValueOrDefault("phase") as string, 128);
            var stage = budget.Take(evidence.GetValueOrDefault("stage") as string, 128);
            var rawOrigins = evidence.GetValueOrDefault("origins") as string;
            var origins = budget.Take(rawOrigins, 128);
            var rawComparisonSide = evidence.GetValueOrDefault("comparisonSide") as string;
            var comparisonSide = rawComparisonSide is "left" or "right" ? rawComparisonSide : null;
            var comparisonOtherUriCodeUnitLength = evidence.GetValueOrDefault("comparisonOtherUriCodeUnitLength")
                is int otherLength && otherLength >= 0 ? otherLength : (int?)null;
            var rawComparisonOtherUriHex = evidence.GetValueOrDefault("comparisonOtherUriUtf16Hex") as string;
            var comparisonOtherUriHex = rawComparisonOtherUriHex is null
                ? null : budget.TakeHex(rawComparisonOtherUriHex, 4096 * 4);
            // [DEBUG-415-uri-v2] Copy only known bounded identifier fields, not arbitrary metadata.
            var originDetails = new List<Dictionary<string, object>>();
            var detailsComplete = evidence.GetValueOrDefault("originDetailsComplete") is true;
            if (evidence.GetValueOrDefault("originDetails") is List<Dictionary<string, object>> rawDetails)
            {
                detailsComplete &= rawDetails.Count <= 32;
                foreach (var rawDetail in rawDetails.Take(32))
                {
                    var detail = new Dictionary<string, object>(StringComparer.Ordinal);
                    var complete = rawDetail.GetValueOrDefault("fieldsComplete") is true;
                    foreach (var name in new[] { "category", "documentUri", "moduleName", "definitionName",
                        "definitionKind", "definitionModuleName", "identityOrigin", "identityName", "identitySourceUri", "identityKind",
                        "referenceName", "parentTypeName", "propertyAccessorKind" })
                    {
                        if (rawDetail.GetValueOrDefault(name) is not string value) continue;
                        var captured = budget.TakeIdentifier(value, 2048);
                        detail[name] = captured;
                        complete &= captured == value;
                    }
                    foreach (var name in new[] { "documentIndex", "definitionIndex", "startLine",
                        "startCharacter", "endLine", "endCharacter", "identityStartLine", "identityStartCharacter",
                        "identityEndLine", "identityEndCharacter" })
                        if (rawDetail.GetValueOrDefault(name) is int value) detail[name] = value;
                    detail["fieldsComplete"] = complete;
                    detailsComplete &= complete;
                    originDetails.Add(detail);
                }
            }
            else detailsComplete = false;
            return new
            {
                status = "available",
                uriCodeUnitLength = length,
                uriCapturedCodeUnits = capturedHex.Length / 4,
                uriCaptureComplete = capturedHex.Length == (long)length * 4,
                uriUtf16Hex = capturedHex,
                phase,
                stage,
                comparisonSide,
                comparisonOtherUriCodeUnitLength,
                comparisonOtherUriCapturedCodeUnits = comparisonOtherUriHex?.Length / 4,
                comparisonOtherUriCaptureComplete = comparisonOtherUriCodeUnitLength is { } expectedOtherLength
                    && comparisonOtherUriHex?.Length == (long)expectedOtherLength * 4,
                comparisonOtherUriUtf16Hex = comparisonOtherUriHex,
                origins,
                originsComplete = evidence.GetValueOrDefault("originsComplete") is true
                    && rawOrigins is not null && origins == rawOrigins,
                originDetails,
                originDetailsComplete = detailsComplete,
                originScanComplete = evidence.GetValueOrDefault("originScanComplete") is true,
                originMatchesObserved = evidence.GetValueOrDefault("originMatchesObserved") as int?,
                inventoryIndex = evidence.GetValueOrDefault("inventoryIndex") as int?
            };
        }
        catch (Exception) { return new { status = "unavailable" }; }
    }

    private static object CaptureAssembly(Assembly assembly, TextBudget budget)
    {
        try
        {
            return new
            {
                name = budget.Take(assembly.GetName().Name),
                version = budget.Take(assembly.GetName().Version?.ToString()),
                informationalVersion = budget.Take(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion),
                moduleVersionId = assembly.ManifestModule.ModuleVersionId.ToString(),
                status = "available"
            };
        }
        catch (Exception error)
        {
            return new { status = "unavailable", reason = budget.Take(SafeExceptionText(error, 1024)) };
        }
    }

    private static object CaptureExecutable(TextBudget budget)
    {
        var path = Environment.ProcessPath;
        try
        {
            if (string.IsNullOrWhiteSpace(path)) throw new IOException("The running executable path is unavailable.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return new { path = budget.Take(path), sha256 = hash, status = "available", hashDomain = "executable-file-observed-after-failure" };
        }
        catch (Exception error)
        {
            return new { path = budget.Take(path), status = "unavailable", reason = budget.Take(SafeExceptionText(error, 1024)) };
        }
    }

    private static string SafeExceptionText(Exception error, int maximum)
    {
        try { return Clip(error.ToString(), maximum); }
        catch (Exception renderingError)
        {
            return Clip($"{error.GetType().FullName}: exception text unavailable ({renderingError.GetType().FullName})."
                + Environment.NewLine + SafeStack(error), maximum);
        }
    }

    private static string? SafeStack(Exception error)
    {
        try { return error.StackTrace; }
        catch (Exception) { return "Stack trace unavailable."; }
    }

    private static string RenderFallback(VbaSourceAnalysisReport report)
    {
        var builder = new StringBuilder();
        foreach (var failure in report.Failures.Take(4))
        {
            builder.AppendLine($"Phase: {Clip(failure.Phase, 256)}; source: {Clip(failure.ActiveSourcePath ?? failure.SourceUri ?? "project", 1024)}");
            builder.AppendLine(failure.Exception is { } error
                ? SafeExceptionText(error, MaximumExceptionCharacters)
                : Clip(failure.Message, 4096));
        }
        if (report.Failures.Length > 4) builder.AppendLine("[Additional failures omitted from stderr fallback.]");
        return builder.ToString();
    }

    private static string Clip(string? text, int maximum)
        => text is null ? string.Empty : text.Length <= maximum ? text : text[..maximum] + " [truncated]";

    private static bool IsCancellationOnly(Exception? error)
        => error is OperationCanceledException
            || error is AggregateException aggregate && aggregate.InnerExceptions.Count > 0
                && aggregate.InnerExceptions.All(IsCancellationOnly);

    private static void EnsureOrdinaryDirectory(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            try
            {
                var attributes = File.GetAttributes(current.FullName);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Diagnostics paths must not traverse a reparse point: {current.FullName}");
                if ((attributes & FileAttributes.Directory) == 0)
                    throw new IOException($"The diagnostics directory path contains a file: {current.FullName}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        Directory.CreateDirectory(path);
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Diagnostics paths must not traverse a reparse point: {current.FullName}");
    }

    private static string Prune(string targetDirectory)
    {
        try
        {
            var completed = new DirectoryInfo(targetDirectory).EnumerateFiles(FilePrefix + "*.json", SearchOption.TopDirectoryOnly)
                .Where(file => ReportName.IsMatch(file.Name) && (file.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(file => file.Name, StringComparer.Ordinal).ToArray();
            foreach (var file in completed.Skip(MaximumReports))
            {
                EnsureOrdinaryDirectory(targetDirectory);
                if ((File.GetAttributes(file.FullName) & FileAttributes.ReparsePoint) == 0) File.Delete(file.FullName);
            }
            return string.Empty;
        }
        catch (Exception error)
        {
            return "Warning: Older source-analysis evidence could not be pruned: " + SafeExceptionText(error, 2048)
                + Environment.NewLine;
        }
    }

    private static void TryRemovePartial(string? path)
    {
        if (path is null) return;
        try
        {
            EnsureOrdinaryDirectory(Path.GetDirectoryName(path)!);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.Delete(path);
        }
        catch (Exception) { }
    }

    private sealed class TextBudget(int remaining = 192 * 1024)
    {
        internal bool Truncated { get; private set; }

        // Identifier values remain literal prefixes; completeness flags describe clipping separately.
        internal string TakeIdentifier(string text, int maximum)
        {
            var length = Math.Min(text.Length, Math.Min(maximum, remaining));
            remaining -= length;
            if (length != text.Length) Truncated = true;
            return text[..length];
        }

        // Keep hex aligned to complete UTF-16 code units and report clipping through the owning record.
        internal string TakeHex(string text, int maximum)
        {
            var length = Math.Min(text.Length, Math.Min(maximum, remaining));
            length -= length % 4;
            remaining -= length;
            if (length != text.Length) Truncated = true;
            return text[..length];
        }

        internal string? Take(string? text, int maximum = 2048)
        {
            if (text is null) return null;
            var length = Math.Min(text.Length, Math.Min(maximum, remaining));
            remaining -= length;
            if (length == text.Length) return text;
            Truncated = true;
            return text[..length] + " [truncated]";
        }
    }
}
