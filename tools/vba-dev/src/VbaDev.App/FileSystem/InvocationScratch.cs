using System.Collections.Immutable;

namespace VbaDev.App.FileSystem;

internal enum InvocationScratchCleanupStatus
{
    Removed,
    Retained,
    Inconclusive
}

internal sealed record InvocationScratchCleanupEvidence(
    InvocationScratchCleanupStatus Status,
    ImmutableArray<string> RetainedPaths,
    ImmutableArray<string> InconclusivePaths);

/// <summary>
/// Registers exact ownership receipts and completes bounded cleanup independently
/// of command cancellation. The caller owns creation, session disposal, and outcome policy.
/// </summary>
internal sealed class InvocationScratch(ExactFileSystemObjectOwnership ownership)
{
    private const int CleanupAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly List<ExactFileSystemObjectOwnership.FileReceipt> files = [];
    private readonly List<ExactFileSystemObjectOwnership.DirectoryReceipt> directories = [];
    private InvocationScratchCleanupEvidence? evidence;
    private bool cleanupStarted;

    internal void Register(ExactFileSystemObjectOwnership.FileReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ThrowIfCleanupStarted();
        if (!files.Contains(receipt)) files.Add(receipt);
    }

    internal void Register(ExactFileSystemObjectOwnership.DirectoryReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ThrowIfCleanupStarted();
        if (!directories.Contains(receipt)) directories.Add(receipt);
    }

    internal InvocationScratchCleanupEvidence Cleanup(Action<string>? onProofComplete = null)
    {
        if (evidence is not null) return evidence;
        cleanupStarted = true;
        var orderedDirectories = directories
            .OrderByDescending(directory => directory.Route.Count(character =>
                character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar))
            .ThenBy(directory => directory.Route, StringComparer.OrdinalIgnoreCase)
            .ThenBy(directory => directory.Route, StringComparer.Ordinal)
            .ToArray();
        var removedFiles = new HashSet<ExactFileSystemObjectOwnership.FileReceipt>();
        var removedDirectories = new HashSet<ExactFileSystemObjectOwnership.DirectoryReceipt>();
        var retained = new HashSet<string>(PathComparer);
        var inconclusive = new HashSet<string>(PathComparer);
        for (var attempt = 1; attempt <= CleanupAttempts; attempt++)
        {
            retained.Clear();
            inconclusive.Clear();
            foreach (var file in files)
            {
                if (removedFiles.Contains(file)) continue;
                var result = ownership.TryDelete(file, onProofComplete);
                if (result.Removed)
                {
                    removedFiles.Add(file);
                }
                else
                {
                    Retain(file.Route, result);
                }
            }

            foreach (var directory in orderedDirectories)
            {
                if (removedDirectories.Contains(directory)) continue;
                if (files.Any(file => !removedFiles.Contains(file) && IsBelow(file.Route, directory.Route))
                    || directories.Any(child => !removedDirectories.Contains(child)
                        && IsBelow(child.Route, directory.Route)))
                {
                    retained.Add(directory.Route);
                    continue;
                }

                var result = ownership.TryDeleteEmpty(directory, onProofComplete);
                if (result.Removed)
                {
                    removedDirectories.Add(directory);
                }
                else
                {
                    Retain(directory.Route, result);
                }
            }

            if (retained.Count == 0 || inconclusive.Count == 0 || attempt == CleanupAttempts) break;
            Thread.Sleep(RetryDelay);
        }

        evidence = new(
            inconclusive.Count > 0 ? InvocationScratchCleanupStatus.Inconclusive
                : retained.Count > 0 ? InvocationScratchCleanupStatus.Retained
                : InvocationScratchCleanupStatus.Removed,
            SortPaths(retained), SortPaths(inconclusive));
        return evidence;

        void Retain(string route, ExactFileSystemObjectOwnership.DeletionResult result)
        {
            retained.Add(route);
            retained.UnionWith(result.RetainedPaths);
            if (!result.Conclusive) inconclusive.Add(route);
        }
    }

    private void ThrowIfCleanupStarted()
    {
        if (cleanupStarted)
        {
            throw new InvalidOperationException("Invocation scratch cleanup has already started.");
        }
    }

    private static ImmutableArray<string> SortPaths(IEnumerable<string> paths)
        => paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal).ToImmutableArray();

    // Routes determine scheduling only. Every deletion still requires the original receipt.
    private static bool IsBelow(string candidate, string parent)
        => candidate.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
