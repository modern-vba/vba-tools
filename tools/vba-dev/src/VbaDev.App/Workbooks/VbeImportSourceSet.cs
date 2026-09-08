using System.Runtime.InteropServices;
using System.Text;

namespace VbaDev.App.Workbooks;

/// <summary>
/// Creates invocation-private source mirrors accepted losslessly by VBIDE's active code page.
/// </summary>
public sealed class VbeImportSourceSetFactory
{
    private readonly Action<VbeImportSourceSet>? sourceSetCreated;

    /// <summary>Creates a factory that projects only admitted source facts.</summary>
    public VbeImportSourceSetFactory()
    {
    }

    internal VbeImportSourceSetFactory(Action<VbeImportSourceSet>? sourceSetCreated)
    {
        this.sourceSetCreated = sourceSetCreated;
    }

    internal VbeImportSourceSet Create(AdmittedVbaSourceSet admission)
        => NotifyCreated(VbeImportSourceSet.Create(admission));

    private VbeImportSourceSet NotifyCreated(VbeImportSourceSet sourceSet)
    {
        try
        {
            sourceSetCreated?.Invoke(sourceSet);
            return sourceSet;
        }
        catch (Exception creationError)
        {
            try
            {
                sourceSet.Dispose();
            }
            catch (Exception cleanupError)
            {
                throw new InvalidOperationException(
                    $"{creationError.Message} {cleanupError.Message}",
                    new AggregateException(creationError, cleanupError));
            }

            throw;
        }
    }
}

/// <summary>
/// Owns the temporary VBE-facing mirror for one import invocation.
/// </summary>
public sealed class VbeImportSourceSet : IDisposable
{
    private static readonly UTF8Encoding Utf8Strict = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private VbeImportSourceSet(
        string stagingPath,
        IReadOnlyList<VbeImportSourceFile> sourceFiles,
        int activeCodePage,
        AdmittedVbaSourceSet admission)
    {
        StagingPath = stagingPath;
        SourceFiles = sourceFiles;
        ActiveCodePage = activeCodePage;
        Admission = admission;
    }

    /// <summary>
    /// Gets the invocation-private staging directory.
    /// </summary>
    public string StagingPath { get; }

    /// <summary>
    /// Gets the exact active code page fixed for this source set.
    /// </summary>
    public int ActiveCodePage { get; }

    /// <summary>
    /// Gets the staged sources that may be passed to VBComponents.Import.
    /// </summary>
    public IReadOnlyList<VbeImportSourceFile> SourceFiles { get; }

    internal AdmittedVbaSourceSet Admission { get; }

    /// <summary>
    /// Derives the VBE mirror solely from the invocation's admitted source facts.
    /// </summary>
    internal static VbeImportSourceSet Create(AdmittedVbaSourceSet admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        var activeEncoding = CreateStrictActiveEncoding(admission.ActiveCodePage);
        var stagingPath = Path.Combine(Path.GetTempPath(), "vba-dev-vbe-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingPath);
        try
        {
            var stagedSources = new List<VbeImportSourceFile>(admission.Sources.Length);
            foreach (var source in admission.Sources)
            {
                var importBytes = EncodeForVbe(
                    source.Text,
                    activeEncoding,
                    admission.ActiveCodePage,
                    source.DiagnosticSourcePath);
                var stagedSourcePath = Path.Combine(stagingPath, source.FileName);
                File.WriteAllBytes(stagedSourcePath, importBytes);
                string? stagedBinaryPath = null;
                if (source.BinaryBytes is { } binaryBytes)
                {
                    stagedBinaryPath = Path.Combine(stagingPath, Path.GetFileNameWithoutExtension(source.FileName) + ".frx");
                    File.WriteAllBytes(stagedBinaryPath, binaryBytes.AsSpan());
                }

                stagedSources.Add(new VbeImportSourceFile(
                    stagedSourcePath,
                    source.Kind,
                    stagedBinaryPath,
                    new VbeImportVerification(
                        source.ModuleIdentityAuthority.Name ?? source.Projection.ModuleName,
                        source.Kind,
                        source.Projection.CodeModuleLines,
                        source.OriginalEncoding),
                    source.DiagnosticSourcePath,
                    source.ModuleIdentityAuthority));
            }

            return new VbeImportSourceSet(
                stagingPath,
                stagedSources.AsReadOnly(),
                admission.ActiveCodePage,
                admission);
        }
        catch (Exception stagingError)
        {
            try
            {
                DeleteStagingDirectory(stagingPath);
            }
            catch (Exception cleanupError)
            {
                throw new InvalidOperationException(
                    $"{stagingError.Message} The VBE import staging directory could not be removed: '{stagingPath}'.",
                    new AggregateException(stagingError, cleanupError));
            }

            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose() => DeleteStagingDirectory(StagingPath);

    private static byte[] EncodeForVbe(
        string text,
        Encoding activeEncoding,
        int activeCodePage,
        string sourcePath)
    {
        try
        {
            var bytes = activeEncoding.GetBytes(text);
            var reproducedText = activeEncoding.GetString(bytes);
            if (!text.Equals(reproducedText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"VBA source '{sourcePath}' changes text when converted through Windows code page {activeCodePage}.");
            }

            return bytes;
        }
        catch (EncoderFallbackException ex)
        {
            throw new InvalidOperationException(
                $"VBA source '{sourcePath}' cannot be represented losslessly in Windows code page {activeCodePage}.",
                ex);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException(
                $"VBA source '{sourcePath}' cannot round-trip losslessly through Windows code page {activeCodePage}.",
                ex);
        }
    }

    private static Encoding CreateStrictActiveEncoding(int activeCodePage)
    {
        if (activeCodePage <= 0)
        {
            throw new InvalidOperationException(
                $"The active Windows ANSI code page '{activeCodePage}' is invalid.");
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return activeCodePage == 65001
                ? Utf8Strict
                : Encoding.GetEncoding(
                    activeCodePage,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"The active Windows ANSI code page '{activeCodePage}' is not available.",
                ex);
        }
    }

    private static void DeleteStagingDirectory(string stagingPath)
    {
        try
        {
            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The VBE import staging directory could not be removed: '{stagingPath}'.",
                ex);
        }
    }

}

internal static class ActiveWindowsAnsiCodePage
{
    public static int Get()
        => OperatingSystem.IsWindows()
            ? checked((int)GetACP())
            : 65001;

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
