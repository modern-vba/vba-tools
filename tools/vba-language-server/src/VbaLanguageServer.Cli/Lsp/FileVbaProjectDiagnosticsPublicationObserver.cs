using System.Text.Json;
using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Emits atomic completion markers for deterministic project-diagnostics process tests.
/// </summary>
internal sealed class FileVbaProjectDiagnosticsPublicationObserver
    : IVbaDiagnosticsPublicationObserver
{
    internal const string DirectoryEnvironmentVariable =
        "VBA_TOOLS_PROJECT_DIAGNOSTICS_PUBLICATION_DIRECTORY";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string directory;
    private long sequence;

    private FileVbaProjectDiagnosticsPublicationObserver(string directory)
    {
        this.directory = directory;
        Directory.CreateDirectory(directory);
    }

    public static IVbaDiagnosticsPublicationObserver CreateFromEnvironment()
    {
        var directory = Environment.GetEnvironmentVariable(
            DirectoryEnvironmentVariable);
        return string.IsNullOrWhiteSpace(directory)
            ? NullVbaDiagnosticsPublicationObserver.Instance
            : new FileVbaProjectDiagnosticsPublicationObserver(directory);
    }

    public void AfterRevisionReserved(string uri, long revision)
    {
    }

    public void AfterProjectDiagnosticsTransportWrite(
        VbaProjectAuthorityIdentity authority,
        string uri,
        long revision,
        int? clientVersion)
    {
        var markerSequence = Interlocked.Increment(ref sequence);
        var markerPath = Path.Combine(
            directory,
            $"{markerSequence:D20}.completed");
        var temporaryPath = $"{markerPath}.{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(
                    new ProjectDiagnosticsPublicationMarker(
                        authority.StableKey,
                        uri,
                        revision,
                        clientVersion),
                    JsonOptions));
            File.Move(temporaryPath, markerPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record ProjectDiagnosticsPublicationMarker(
        string Authority,
        string Uri,
        long Revision,
        int? ClientVersion);
}
