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
internal sealed class SourceAnalysisEvidenceStore(string? directory = null)
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

    internal string Save(ResolvedProjectContext context, string operation, VbaSourceAnalysisReport report)
    {
        if (report.Failures.IsEmpty || report.Failures.All(failure => IsCancellationOnly(failure.Exception)))
            return string.Empty;

        string? targetDirectory = directory;
        string? partialPath = null;
        try
        {
            targetDirectory = ResolveDirectory();
            var timestamp = DateTimeOffset.UtcNow;
            var invocationId = Guid.NewGuid().ToString("N");
            var name = FilePrefix + timestamp.ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture)
                + "-" + invocationId + ".json";
            var path = Path.Combine(targetDirectory, name);
            var bytes = Serialize(context, operation, report, timestamp, invocationId);
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

    private string ResolveDirectory()
    {
        if (directory is not null) return Path.GetFullPath(directory);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            throw new IOException("The operating system did not provide a local application data directory.");
        return Path.GetFullPath(Path.Combine(local, "VbaTools", "Diagnostics", "source-analysis"));
    }

    private static byte[] Serialize(ResolvedProjectContext context, string operation, VbaSourceAnalysisReport report,
        DateTimeOffset timestamp, string invocationId)
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
                typeof(VbaSyntaxTree).Assembly, typeof(VbaProjectSemanticInputs).Assembly, typeof(object).Assembly }
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
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = "1.0",
            kind = "vba-dev-source-analysis-failure",
            timestampUtc = timestamp,
            invocationId,
            project,
            failures,
            sources,
            semanticInputs = new
            {
                acquired = inputs is not null,
                referenceCount = inputs?.ReferenceCatalogIdentities.Count,
                selectedReferenceCount = inputs?.ReferenceSelection?.References.Length,
                hostEventsAcquired = inputs?.IntrinsicHostEvents is not null,
                references
            },
            runtime,
            truncation = new
            {
                failuresOmitted = Math.Max(0, report.Failures.Length - failures.Length),
                sourcesOmitted = Math.Max(0, report.SyntaxTrees.Length - sources.Length),
                referencesOmitted = Math.Max(0, (inputs?.ReferenceCatalogIdentities.Count ?? 0) - (references?.Length ?? 0)),
                textTruncated = budget.Truncated || identityBudget.Truncated
            },
            limitations = "Source hashes identify captured parser text encoded as UTF-8, not original file bytes. "
                + "Only parsed trees and successfully acquired semantic identities are available. "
                + "No source text, source copies, environment dump, or external upload is produced. "
                + "Exception messages may contain paths or other application-provided details."
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static object? CaptureException(Exception? error, TextBudget budget)
    {
        if (error is null) return null;
        return new
        {
            type = budget.Take(error.GetType().FullName),
            hresult = error.HResult,
            details = budget.Take(SafeExceptionText(error, MaximumExceptionCharacters), MaximumExceptionCharacters),
            stackTrace = budget.Take(SafeStack(error), 8192)
        };
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
