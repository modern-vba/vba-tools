using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VbaTools.Semantics;

namespace VbaDev.App.Build;

// [DEBUG-typelib-input] Temporary, explicitly enabled diagnostic boundary.
internal sealed class TypeLibCatalogBuildEvidence(string? directory)
{
    internal const string EnvironmentVariable = "VBA_TOOLS_TYPELIB_BUILD_EVIDENCE_ROOT";

    internal static VbaProjectReferenceCatalog BuildIfEnabled(VbaProjectReferenceCatalogIdentity identity,
        TypeLibCatalogMetadata metadata)
        => new TypeLibCatalogBuildEvidence(Environment.GetEnvironmentVariable(EnvironmentVariable)).Build(identity, metadata);

    internal VbaProjectReferenceCatalog Build(VbaProjectReferenceCatalogIdentity identity, TypeLibCatalogMetadata metadata)
    {
        string? capture = null;
        string? inputSha256 = null;
        if (directory is null)
            return TypeLibReferenceCatalogBuilder.Build(identity.ReferenceName, metadata);
        try
        {
            VerifyRoot(directory);
            capture = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(capture);
            var input = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "1.0", kind = "vba-dev-typelib-build-input", timestampUtc = DateTimeOffset.UtcNow,
                identity, metadata, runtime = RuntimeIdentity(),
            }, JsonOptions);
            if (input.Length > 64 * 1024 * 1024)
                throw new IOException("TypeLib input exceeds the 64 MiB diagnostic capture bound.");
            inputSha256 = Convert.ToHexString(SHA256.HashData(input));
            WriteNew(capture, "input.json", input);
            WriteNew(capture, "prepared.json", JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "1.0", inputSha256, processId = Environment.ProcessId, identity,
                preparedAtUtc = DateTimeOffset.UtcNow,
            }, JsonOptions));
        }
        catch (Exception error)
        {
            WriteCaptureError(capture, error);
            capture = null;
        }
        try
        {
            var result = TypeLibReferenceCatalogBuilder.Build(identity.ReferenceName, metadata);
            WriteOutcome(capture, inputSha256, identity, null);
            return result;
        }
        catch (Exception exception)
        {
            WriteOutcome(capture, inputSha256, identity, exception);
            throw;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new ExactStringConverter() },
    };

    private sealed class ExactStringConverter : JsonConverter<string>
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.GetString();
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            // The default encoder replaces unpaired UTF-16 surrogates; do not claim a lossy capture is complete.
            _ = StrictUtf8.GetByteCount(value);
            writer.WriteStringValue(value);
        }
    }

    private static void WriteOutcome(string? capture, string? inputSha256,
        VbaProjectReferenceCatalogIdentity identity, Exception? exception)
    {
        if (capture is null) return;
        try
        {
            WriteNew(capture, "outcome.json", JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "1.0", inputSha256, processId = Environment.ProcessId, identity,
                status = exception is null ? "succeeded" : "failed",
                exception = exception is null ? null : new { type = exception.GetType().FullName, hresult = exception.HResult, details = exception.ToString() },
                completedAtUtc = DateTimeOffset.UtcNow,
            }, JsonOptions));
        }
        catch (Exception error) { WriteCaptureError(capture, error); }
    }

    private static void WriteCaptureError(string? capture, Exception error)
    {
        if (capture is null) return;
        try
        {
            WriteNew(capture, "capture-error.json", JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "1.0", status = "capture-failed", details = error.ToString(),
            }, JsonOptions));
        }
        catch (Exception) { /* Diagnostic storage must never replace the product result. */ }
    }

    private static void VerifyRoot(string root)
    {
        if (!Path.IsPathFullyQualified(root) || root.StartsWith("\\\\", StringComparison.Ordinal))
            throw new IOException("Evidence requires an explicit local absolute directory.");
        for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
        {
            if (!current.Exists || (current.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new IOException("Evidence requires existing ordinary directories.");
        }
    }

    private static object RuntimeIdentity()
    {
        using var process = Process.GetCurrentProcess();
        using var executable = File.OpenRead(Environment.ProcessPath!);
        return new
        {
            processId = Environment.ProcessId, processStartedAtUtc = process.StartTime.ToUniversalTime(),
            framework = RuntimeInformation.FrameworkDescription, version = Environment.Version.ToString(),
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            executable = new { path = Environment.ProcessPath, sha256 = Convert.ToHexString(SHA256.HashData(executable)) },
            assemblies = new[]
            {
                AssemblyIdentity(typeof(TypeLibCatalogBuildEvidence).Assembly),
                AssemblyIdentity(typeof(TypeLibReferenceCatalogBuilder).Assembly),
                AssemblyIdentity(typeof(Enumerable).Assembly), AssemblyIdentity(typeof(object).Assembly),
            },
        };
    }

    private static object AssemblyIdentity(Assembly assembly) => new
    {
        name = assembly.GetName().Name, version = assembly.GetName().Version?.ToString(),
        informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        moduleVersionId = assembly.ManifestModule.ModuleVersionId.ToString(),
    };

    private static void WriteNew(string capture, string name, byte[] bytes)
    {
        using var stream = new FileStream(Path.Combine(capture, name), FileMode.CreateNew);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
