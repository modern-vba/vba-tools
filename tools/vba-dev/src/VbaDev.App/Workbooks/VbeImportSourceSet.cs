using System.Runtime.InteropServices;
using System.Text;
using VbaDev.App.FileSystem;

namespace VbaDev.App.Workbooks;

/// <summary>
/// Creates invocation-private source mirrors accepted losslessly by VBIDE's active code page.
/// </summary>
public sealed class VbeImportSourceSetFactory
{
    private readonly Action<VbeImportSourceSet>? sourceSetCreated;
    private readonly IExactFileSystemObjectOwnershipFactory ownershipFactory;

    /// <summary>Creates a factory that projects only admitted source facts.</summary>
    public VbeImportSourceSetFactory(IExactFileSystemObjectOwnershipFactory ownershipFactory)
        : this(ownershipFactory, null)
    {
    }

    internal VbeImportSourceSetFactory(IExactFileSystemObjectOwnershipFactory ownershipFactory, Action<VbeImportSourceSet>? sourceSetCreated)
    {
        this.ownershipFactory = ownershipFactory;
        this.sourceSetCreated = sourceSetCreated;
    }

    internal VbeImportSourceSet Create(AdmittedVbaSourceSet admission)
        => NotifyCreated(VbeImportSourceSet.Create(ownershipFactory, admission));

    internal VbeImportSourceSet CreateForSourceWorkbookBuild(AdmittedVbaSourceSet admission)
        => NotifyCreated(VbeImportSourceSet.CreateFlatForSourceWorkbookBuild(ownershipFactory, admission));

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
    private readonly ExactFileSystemObjectOwnership ownership;
    private readonly InvocationScratch scratch;
    private readonly bool usesStableParent;
    private bool disposed;

    private VbeImportSourceSet(
        ExactFileSystemObjectOwnership ownership,
        InvocationScratch scratch,
        string stagingPath,
        IReadOnlyList<VbeImportSourceFile> sourceFiles,
        int activeCodePage,
        AdmittedVbaSourceSet admission,
        bool usesStableParent = false)
    {
        this.ownership = ownership;
        this.scratch = scratch;
        this.usesStableParent = usesStableParent;
        StagingPath = stagingPath;
        SourceFiles = sourceFiles;
        ActiveCodePage = activeCodePage;
        Admission = admission;
    }

    /// <summary>
    /// Gets the directory containing the VBE-facing source files. For source-workbook
    /// Build this is a stable shared parent, not an invocation-owned cleanup target.
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

    internal InvocationScratchCleanupEvidence? CleanupEvidence { get; private set; }

    /// <summary>
    /// Derives the VBE mirror solely from the invocation's admitted source facts.
    /// </summary>
    internal static VbeImportSourceSet Create(IExactFileSystemObjectOwnershipFactory ownershipFactory, AdmittedVbaSourceSet admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        var activeEncoding = CreateStrictActiveEncoding(admission.ActiveCodePage);
        var container = Path.Combine(Path.GetTempPath(), "vba-dev-vbe-import");
        Directory.CreateDirectory(container);
        var ownership = ownershipFactory.Open();
        var scratch = new InvocationScratch(ownership);
        ExactFileSystemObjectOwnership.DirectoryReceipt? directory = null;
        var transferred = false;
        try
        {
            directory = ownership.TryCreateOnlyDirectory(container, Guid.NewGuid().ToString("N"))
                ?? throw new IOException($"A unique VBE source mirror could not be created beneath '{container}'.");
            scratch.Register(directory);
            var stagingPath = directory.Route;
            var stagedSources = new List<VbeImportSourceFile>(admission.Sources.Length);
            foreach (var source in admission.Sources)
            {
                var importBytes = EncodeForVbe(
                    source.Text,
                    activeEncoding,
                    admission.ActiveCodePage,
                    source.DiagnosticSourcePath);
                var stagedSourcePath = CopyExact(source.FileName, importBytes);
                string? stagedBinaryPath = null;
                if (source.BinaryBytes is { } binaryBytes)
                {
                    stagedBinaryPath = CopyExact(Path.GetFileNameWithoutExtension(source.FileName) + ".frx", binaryBytes.AsSpan());
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

            ownership.ReleaseCreationFence(directory);
            var sourceSet = new VbeImportSourceSet(
                ownership,
                scratch,
                stagingPath,
                stagedSources.AsReadOnly(),
                admission.ActiveCodePage,
                admission);
            transferred = true;
            return sourceSet;
        }
        catch (Exception stagingError)
        {
            if (directory is not null) ownership.ReleaseCreationFence(directory);
            var cleanup = scratch.Cleanup();
            if (cleanup.Status != InvocationScratchCleanupStatus.Removed)
            {
                throw new InvalidOperationException(
                    $"{stagingError.Message} {DescribeCleanup(cleanup)}", stagingError);
            }

            throw;
        }
        finally
        {
            if (!transferred) ownership.Dispose();
        }

        string CopyExact(string fileName, ReadOnlySpan<byte> bytes)
        {
            try
            {
                var receipt = ownership.CreateOnlyFile(directory!, fileName, bytes);
                scratch.Register(receipt);
                return receipt.Route;
            }
            catch (ExactFileSystemObjectOwnership.FileCreationCleanupException error)
            {
                if (error.RetainedReceipt is not null) scratch.Register(error.RetainedReceipt);
                throw;
            }
        }
    }

    /// <summary>
    /// Stages source-workbook Build files directly in a stable parent because VBIDE may
    /// retain a directory handle for the lifetime of an already-open Excel process.
    /// Only exact, invocation-created file receipts are registered for cleanup.
    /// </summary>
    internal static VbeImportSourceSet CreateFlatForSourceWorkbookBuild(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        AdmittedVbaSourceSet admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        var activeEncoding = CreateStrictActiveEncoding(admission.ActiveCodePage);
        var stableParent = Path.Combine(Path.GetTempPath(), "vba-dev-vbe-import", "source-workbook-build");
        Directory.CreateDirectory(stableParent);
        var ownership = ownershipFactory.Open();
        var scratch = new InvocationScratch(ownership);
        var transferred = false;
        try
        {
            var prefix = Guid.NewGuid().ToString("N") + "_";
            var stagedSources = new List<VbeImportSourceFile>(admission.Sources.Length);
            foreach (var source in admission.Sources)
            {
                var stagedBaseName = prefix + Path.GetFileNameWithoutExtension(source.FileName);
                var importBytes = EncodeForVbe(
                    RewriteFormResourceBasenames(source, stagedBaseName),
                    activeEncoding,
                    admission.ActiveCodePage,
                    source.DiagnosticSourcePath);
                var stagedSourcePath = CopyExact(prefix + source.FileName, importBytes);
                string? stagedBinaryPath = null;
                if (source.BinaryBytes is { } binaryBytes)
                {
                    stagedBinaryPath = CopyExact(
                        stagedBaseName + ".frx",
                        binaryBytes.AsSpan());
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

            var sourceSet = new VbeImportSourceSet(
                ownership,
                scratch,
                stableParent,
                stagedSources.AsReadOnly(),
                admission.ActiveCodePage,
                admission,
                usesStableParent: true);
            transferred = true;
            return sourceSet;
        }
        catch (Exception stagingError)
        {
            var cleanup = scratch.Cleanup();
            if (cleanup.Status != InvocationScratchCleanupStatus.Removed)
            {
                throw new InvalidOperationException(
                    $"{stagingError.Message} {DescribeCleanup(cleanup, usesStableParent: true)}", stagingError);
            }

            throw;
        }
        finally
        {
            if (!transferred) ownership.Dispose();
        }

        string CopyExact(string fileName, ReadOnlySpan<byte> bytes)
        {
            try
            {
                var receipt = ownership.CreateOnlyFile(stableParent, fileName, bytes);
                scratch.Register(receipt);
                return receipt.Route;
            }
            catch (ExactFileSystemObjectOwnership.FileCreationCleanupException error)
            {
                if (error.RetainedReceipt is not null) scratch.Register(error.RetainedReceipt);
                throw;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        InvocationScratchCleanupEvidence cleanup;
        try { CleanupEvidence = cleanup = scratch.Cleanup(); }
        finally { ownership.Dispose(); }
        if (cleanup.Status != InvocationScratchCleanupStatus.Removed)
            throw new InvalidOperationException(DescribeCleanup(cleanup, usesStableParent));
    }

    internal void RetainWithoutCleanup()
    {
        if (disposed) return;
        disposed = true;
        ownership.Dispose();
    }

    private static string DescribeCleanup(InvocationScratchCleanupEvidence cleanup, bool usesStableParent = false)
        => usesStableParent
            ? $"The invocation-owned VBE import files could not be removed ({cleanup.Status}). Retained paths: {string.Join(", ", cleanup.RetainedPaths)}"
            : $"The VBE import staging directory could not be removed ({cleanup.Status}). Retained paths: {string.Join(", ", cleanup.RetainedPaths)}";

    private static string RewriteFormResourceBasenames(AdmittedVbaSource source, string stagedBaseName)
    {
        if (source.Kind != VbaSourceKind.Form) return source.Text;
        var designer = source.Syntax.Module.FormDesignerBlock;
        if (designer is null) return source.Text;
        if (designer.EvidenceProblems.Count > 0)
        {
            throw new InvalidOperationException(
                $"The UserForm designer has incomplete resource evidence: {source.DiagnosticSourcePath}");
        }

        if (designer.ResourceReferences.Count == 0) return source.Text;
        if (source.BinaryBytes is null)
        {
            throw new InvalidOperationException(
                $"The UserForm resource sidecar is missing: {source.DiagnosticSourcePath}");
        }

        var originalSidecarName = Path.GetFileNameWithoutExtension(source.FileName) + ".frx";
        var stagedSidecarName = stagedBaseName + ".frx";
        var rewritten = new StringBuilder(source.Text);
        var nextStart = source.Text.Length;
        foreach (var reference in designer.ResourceReferences
                     .OrderByDescending(reference => reference.FileNameRange.Start.Offset))
        {
            var start = reference.FileNameRange.Start.Offset;
            var end = reference.FileNameRange.End.Offset;
            if (!reference.FileName.Equals(originalSidecarName, StringComparison.OrdinalIgnoreCase)
                || start < 0 || end < start || end > nextStart
                || !source.Text.AsSpan(start, end - start).SequenceEqual(reference.FileName.AsSpan()))
            {
                throw new InvalidOperationException(
                    $"The UserForm resource reference cannot be mirrored safely: {source.DiagnosticSourcePath}");
            }

            rewritten.Remove(start, end - start);
            rewritten.Insert(start, stagedSidecarName);
            nextStart = start;
        }

        return rewritten.ToString();
    }

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
