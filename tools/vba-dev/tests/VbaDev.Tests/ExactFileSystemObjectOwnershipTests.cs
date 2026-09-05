using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VbaDev.App.FileSystem;
using Xunit;

namespace VbaDev.Tests;

public sealed class ExactFileSystemObjectOwnershipTests
{
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileDispositionInfoClass = 4;

    [Fact]
    public void ReceiptAndPendingCaptureTypesAcceptOnlyOpaqueProvenState()
    {
        var receiptTypes = new[]
        {
            typeof(ExactFileSystemObjectOwnership.FileReceipt),
            typeof(ExactFileSystemObjectOwnership.DirectoryReceipt),
            typeof(ExactFileSystemObjectOwnership.PendingFileCapture)
        };

        foreach (var receiptType in receiptTypes)
        {
            var constructor = Assert.Single(receiptType.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
            Assert.True(constructor.IsAssembly);
            Assert.Equal(
                [typeof(object)],
                constructor.GetParameters().Select(parameter => parameter.ParameterType));
            var exception = Assert.Throws<TargetInvocationException>(
                () => constructor.Invoke([new object()]));
            Assert.IsType<InvalidOperationException>(exception.InnerException);
            Assert.DoesNotContain(
                receiptType.GetMethods(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
                method => method.ReturnType == receiptType);
            Assert.DoesNotContain(
                receiptType.GetProperties(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
                property => property.GetMethod is not null);
        }
    }

    [Fact]
    public void OwnershipSessionExposesOnlyProvenReceiptIssuanceRoutes()
    {
        var receiptIssuers = typeof(ExactFileSystemObjectOwnership)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => !method.IsPrivate)
            .Where(method =>
                method.ReturnType == typeof(ExactFileSystemObjectOwnership.FileReceipt)
                || method.ReturnType == typeof(ExactFileSystemObjectOwnership.DirectoryReceipt)
                || method.ReturnType == typeof(ExactFileSystemObjectOwnership.StableFileCapture)
                || method.ReturnType == typeof(ExactFileSystemObjectOwnership.StableCaptureCompletion))
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["CaptureTrustedStableFile", "CompleteStableCapture", "CreateOnlyFile", "CreateOnlyFile", "TryCreateOnlyDirectory"],
            receiptIssuers);
    }

    [Fact]
    public void DeleteDispositionUsesTheNativeOneByteBooleanLayout()
    {
        var dispositionType = Assert.IsAssignableFrom<Type>(
            typeof(ExactFileSystemObjectOwnership).GetNestedType(
                "FileDispositionInformation",
                BindingFlags.NonPublic));

        Assert.Equal(1, Marshal.SizeOf(dispositionType));
    }

    [Fact]
    public void CreateOnlyDirectoryDoesNotAdoptAnExistingRoute()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var existing = temp.CreateDirectory("existing");

        var receipt = ownership.TryCreateOnlyDirectory(temp.Path, "existing");

        Assert.Null(receipt);
        Assert.True(Directory.Exists(existing));
    }

    [Fact]
    public void CreateOnlyFileInExistingParentOwnsOnlyTheNewChild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var parent = temp.CreateDirectory("existing");
        var original = Path.Combine(parent, "original.txt");
        File.WriteAllText(original, "untouched");

        var receipt = ownership.CreateOnlyFile(parent, "created.txt", "owned"u8);

        Assert.Equal("owned", File.ReadAllText(receipt.Route));
        Assert.True(ownership.TryDelete(receipt).Removed);
        Assert.Equal("untouched", File.ReadAllText(original));
        Assert.True(Directory.Exists(parent));
    }

    [Fact]
    public void ObservePreservesUnchangedReceiptsAndTheDirectoryCreationFence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var directory = Assert.IsType<ExactFileSystemObjectOwnership.DirectoryReceipt>(
            ownership.TryCreateOnlyDirectory(temp.Path, "owned"));
        var file = ownership.CreateOnlyFile(directory, "first.txt", "first"u8);

        Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Unchanged, ownership.Observe(file));
        Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Unchanged, ownership.Observe(directory));
        Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Unchanged, ownership.Observe(file));
        var later = ownership.CreateOnlyFile(directory, "later.txt", "later"u8);

        Assert.True(ownership.TryDelete(file).Removed);
        Assert.True(ownership.TryDelete(later).Removed);
        Assert.True(ownership.TryDeleteEmpty(directory).Removed);
    }

    [Fact]
    public void ObserveMissingDoesNotRetireEitherReceipt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var directory = Assert.IsType<ExactFileSystemObjectOwnership.DirectoryReceipt>(
            ownership.TryCreateOnlyDirectory(temp.Path, "owned"));
        var file = ownership.CreateOnlyFile(directory, "missing.txt", "original"u8);
        ownership.ReleaseCreationFence(directory);
        File.Delete(file.Route);
        Directory.Delete(directory.Route);

        Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Missing, ownership.Observe(file));
        Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Missing, ownership.Observe(directory));
        Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Missing, ownership.Observe(file));
        Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Missing, ownership.Observe(directory));
        Assert.True(ownership.TryDelete(file).Removed);
        Assert.True(ownership.TryDeleteEmpty(directory).Removed);
    }

    [Fact]
    public void ObserveCallbackKeepsTheProvedFileUnavailableForWriteOrDelete()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var file = ownership.CreateOnlyFile(temp.Path, "owned.txt", "original"u8);
        var callbackInvoked = false;

        var observation = ownership.Observe(file, route =>
        {
            callbackInvoked = true;
            Assert.Equal(file.Route, route);
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(route, "changed"));
            Assert.ThrowsAny<IOException>(() => File.Delete(route));
        });

        Assert.True(callbackInvoked);
        Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Unchanged, observation);
        Assert.True(ownership.TryDelete(file).Removed);
    }

    [Fact]
    public void ObservePreservesTheCallbackFailureAndTheReceipt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var file = ownership.CreateOnlyFile(temp.Path, "owned.txt", "original"u8);
        var failure = new IOException("observer failed");

        Assert.Same(failure, Assert.Throws<IOException>(
            () => ownership.Observe(file, _ => throw failure)));

        Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Unchanged, ownership.Observe(file));
        Assert.True(ownership.TryDelete(file).Removed);
    }

    [Fact]
    public void CreateOnlyFileCancellationStopsAfterOneChunkAndRemovesThePartialFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        using var cancellation = new CancellationTokenSource();
        var path = Path.Combine(temp.Path, "partial.bin");
        var created = false;
        var lengths = new List<long>();

        var failure = Assert.Throws<OperationCanceledException>(() => ownership.CreateOnlyFile(
            temp.Path,
            "partial.bin",
            new byte[128 * 1024],
            onFileCreated: route =>
            {
                created = true;
                Assert.Equal(path, route);
                Assert.Equal(0, new FileInfo(route).Length);
            },
            onBytesWritten: (route, length) =>
            {
                Assert.True(created);
                Assert.Equal(path, route);
                lengths.Add(length);
                cancellation.Cancel();
            },
            cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal([64 * 1024L], lengths);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void UnprovenCreationCleanupRollbackRetainsBothChangedAndUncertainFacts()
    {
        var originalFailure = new IOException("copy failed");
        var rollbackFailure = new ExactFileSystemObjectOwnership.RollbackException("owned.bin");
        var failure = new ExactFileSystemObjectOwnership.FileCreationCleanupException(
            "owned.bin", null, targetChanged: true, originalFailure, rollbackFailure);

        Assert.True(failure.TargetChanged);
        Assert.True(failure.RollbackUnproven);
        var causes = Assert.IsType<AggregateException>(failure.InnerException);
        Assert.Equal([originalFailure, rollbackFailure], causes.InnerExceptions);
    }

    [Fact]
    public void PartialCreationCleanupFailureRetainsTheOriginalFailureAndProvenReceipt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var path = Path.Combine(temp.Path, "partial.bin");
        var originalFailure = new IOException("copy observer failed");
        try
        {
            var failure = Assert.Throws<ExactFileSystemObjectOwnership.FileCreationCleanupException>(
                () => ownership.CreateOnlyFile(
                    temp.Path,
                    "partial.bin",
                    new byte[128 * 1024],
                    onBytesWritten: (route, _) =>
                    {
                        File.SetAttributes(route, FileAttributes.ReadOnly);
                        throw originalFailure;
                    }));

            Assert.Equal(path, failure.Route);
            Assert.False(failure.TargetChanged);
            Assert.False(failure.RollbackUnproven);
            var aggregate = Assert.IsType<AggregateException>(failure.InnerException);
            Assert.Contains(originalFailure, aggregate.InnerExceptions);
            var retained = Assert.IsType<ExactFileSystemObjectOwnership.FileReceipt>(failure.RetainedReceipt);
            Assert.Equal(64 * 1024, new FileInfo(path).Length);
            Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Unchanged, ownership.Observe(retained));
            File.SetAttributes(path, FileAttributes.Normal);
            Assert.True(ownership.TryDelete(retained).Removed);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }
    }

    [Fact]
    public void ObserveRechecksHardLinksAfterTheProofCallback()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var file = ownership.CreateOnlyFile(temp.Path, "owned.txt", "original"u8);
        var alias = Path.Combine(temp.Path, "alias.txt");
        var aliasCreated = false;
        var aliasError = 0;

        var observation = ownership.Observe(file, route =>
        {
            aliasCreated = CreateHardLink(alias, route, IntPtr.Zero);
            if (!aliasCreated)
            {
                aliasError = Marshal.GetLastWin32Error();
            }
        });

        if (aliasCreated)
        {
            Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Changed, observation);
            Assert.False(ownership.TryDelete(file).Removed);
            File.Delete(alias);
        }
        else
        {
            Assert.Equal(32, aliasError);
            Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Unchanged, observation);
        }

        Assert.True(ownership.TryDelete(file).Removed);
    }

    [Fact]
    public void ObserveUnavailableFileIsInconclusiveWithoutRetiringTheReceipt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var file = ownership.CreateOnlyFile(temp.Path, "owned.txt", "original"u8);

        using (var writer = new FileStream(file.Route, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Inconclusive, ownership.Observe(file));
        }

        Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Unchanged, ownership.Observe(file));
        Assert.True(ownership.TryDelete(file).Removed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateOnlyFileDoesNotFollowARetargetedParentRoute(bool useExistingParent)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var parent = temp.CreateDirectory("scratch");
        var directory = Assert.IsType<ExactFileSystemObjectOwnership.DirectoryReceipt>(
            ownership.TryCreateOnlyDirectory(parent, "owned"));
        var movedParent = Path.Combine(temp.Path, "scratch-moved");
        var replacementDirectory = Path.Combine(parent, "owned");

        void ReplaceParent(string _)
        {
            Directory.Move(parent, movedParent);
            Directory.CreateDirectory(replacementDirectory);
        }

        Assert.ThrowsAny<IOException>(() =>
        {
            if (useExistingParent)
            {
                ownership.CreateOnlyFile(directory.Route, "Feature.bas", "owned-content"u8, ReplaceParent);
            }
            else
            {
                ownership.CreateOnlyFile(directory, "Feature.bas", "owned-content"u8, ReplaceParent);
            }
        });

        Assert.False(File.Exists(Path.Combine(replacementDirectory, "Feature.bas")));
        Assert.False(File.Exists(Path.Combine(movedParent, "owned", "Feature.bas")));
    }

    [Fact]
    public void SavedProducerCaptureGainsReceiptAuthorityOnlyAfterTheWriterCloses()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var path = Path.Combine(temp.Path, "saved.bin");
        var expected = "saved producer bytes"u8.ToArray();
        ExactFileSystemObjectOwnership.PendingFileCapture pending;
        using (var writer = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            writer.Write(expected);
            writer.Flush(flushToDisk: true);

            pending = ownership.CapturePendingSavedFile(path);
            var mutableCopy = pending.Bytes;
            mutableCopy[0] ^= 0xff;
            Assert.Equal(expected, pending.Bytes);
            var blocked = ownership.CompleteStableCapture(pending);
            Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Inconclusive, blocked.Observation);
            Assert.Null(blocked.Capture);
        }

        var completion = ownership.CompleteStableCapture(pending);
        Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Unchanged, completion.Observation);
        var capture = Assert.IsType<ExactFileSystemObjectOwnership.StableFileCapture>(completion.Capture);
        Assert.Equal(expected, capture.Bytes);
        Assert.True(ownership.TryDelete(capture.Receipt).Removed);
    }

    [Fact]
    public void PendingCaptureRejectsChangedSavedBytesWithoutIssuingAReceipt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var path = Path.Combine(temp.Path, "saved.bin");
        File.WriteAllBytes(path, "generation-one"u8.ToArray());
        var pending = ownership.CapturePendingSavedFile(path);
        var changed = "generation-two"u8.ToArray();
        File.WriteAllBytes(path, changed);

        var completion = ownership.CompleteStableCapture(pending);

        Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Changed, completion.Observation);
        Assert.Null(completion.Capture);
        Assert.Equal(changed, File.ReadAllBytes(path));
    }

    [Fact]
    public void PendingCaptureFencesSameBytesReplacementUntilCompletion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var path = Path.Combine(temp.Path, "saved.bin");
        var replacement = Path.Combine(temp.Path, "replacement.bin");
        var bytes = "same bytes, different object"u8.ToArray();
        File.WriteAllBytes(path, bytes);
        File.WriteAllBytes(replacement, bytes);
        var pending = ownership.CapturePendingSavedFile(path);

        var replacementFailure = Record.Exception(() => File.Move(replacement, path, overwrite: true));
        Assert.True(replacementFailure is IOException or UnauthorizedAccessException);

        var completion = ownership.CompleteStableCapture(pending);
        Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Unchanged, completion.Observation);
        Assert.True(ownership.TryDelete(completion.Capture!.Receipt).Removed);
        Assert.Equal(bytes, File.ReadAllBytes(replacement));
    }

    [Fact]
    public void PendingCaptureDoesNotAuthorizeAdditionalHardLinks()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var path = Path.Combine(temp.Path, "saved.bin");
        var alias = Path.Combine(temp.Path, "alias.bin");
        var bytes = "saved producer bytes"u8.ToArray();
        File.WriteAllBytes(path, bytes);
        var pending = ownership.CapturePendingSavedFile(path);

        if (CreateHardLink(alias, path, IntPtr.Zero))
        {
            var completion = ownership.CompleteStableCapture(pending);
            Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Changed, completion.Observation);
            Assert.Null(completion.Capture);
            Assert.Equal(bytes, File.ReadAllBytes(alias));
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        else
        {
            Assert.Equal(32, Marshal.GetLastWin32Error());
            var completion = ownership.CompleteStableCapture(pending);
            Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Unchanged, completion.Observation);
            Assert.True(ownership.TryDelete(completion.Capture!.Receipt).Removed);
        }
    }

    [Fact]
    public void PendingCaptureCanBeCompletedOnlyOnceByItsIssuingSession()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        using var otherOwnership = ExactFileSystemObjectOwnership.Open();
        var path = Path.Combine(temp.Path, "saved.bin");
        File.WriteAllBytes(path, "saved bytes"u8.ToArray());
        var pending = ownership.CapturePendingSavedFile(path);

        Assert.Throws<ArgumentException>(() => otherOwnership.CompleteStableCapture(pending));
        var completion = ownership.CompleteStableCapture(pending);
        Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Unchanged, completion.Observation);
        Assert.Throws<ArgumentException>(() => ownership.CompleteStableCapture(pending));
        Assert.True(ownership.TryDelete(completion.Capture!.Receipt).Removed);
    }

    [Fact]
    public void SessionDisposalClosesThePendingFenceWithoutDeletingOrAdoptingTheFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var path = Path.Combine(temp.Path, "saved.bin");
        var bytes = "saved bytes"u8.ToArray();
        File.WriteAllBytes(path, bytes);
        var pending = ownership.CapturePendingSavedFile(path);
        Assert.ThrowsAny<IOException>(() => File.Delete(path));

        ownership.Dispose();

        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Throws<ObjectDisposedException>(() => ownership.CompleteStableCapture(pending));
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TrustedStableCaptureDeletesAnUnchangedSingleLinkFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var path = Path.Combine(temp.Path, "stable.bas");
        var expected = "stable-content"u8.ToArray();
        File.WriteAllBytes(path, expected);

        var capture = ownership.CaptureTrustedStableFile(path);
        var returnedBytes = capture.Bytes;
        returnedBytes[0] ^= 0xff;
        var deletion = ownership.TryDelete(capture.Receipt);

        Assert.Equal(expected, capture.Bytes);
        Assert.True(deletion.Removed);
        Assert.True(deletion.Conclusive);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureRejectsAFileThatAlreadyHasAnotherHardLink(bool pendingCapture)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var path = Path.Combine(temp.Path, "linked.bas");
        var aliasPath = Path.Combine(temp.Path, "linked-alias.bas");
        File.WriteAllText(path, "linked-content");
        Assert.True(
            CreateHardLink(aliasPath, path, IntPtr.Zero),
            new Win32Exception(Marshal.GetLastWin32Error()).Message);

        Assert.Throws<IOException>(() =>
        {
            if (pendingCapture)
            {
                ownership.CapturePendingSavedFile(path);
            }
            else
            {
                ownership.CaptureTrustedStableFile(path);
            }
        });

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(aliasPath));
    }

    [Fact]
    public void FileReceiptRejectsASameLengthContentChange()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var directory = Assert.IsType<ExactFileSystemObjectOwnership.DirectoryReceipt>(
            ownership.TryCreateOnlyDirectory(temp.Path, "owned"));
        var original = "generation-one"u8.ToArray();
        var changed = "generation-two"u8.ToArray();
        Assert.Equal(original.Length, changed.Length);
        var receipt = ownership.CreateOnlyFile(directory, "Feature.bas", original);
        ownership.ReleaseCreationFence(directory);
        File.WriteAllBytes(receipt.Route, changed);

        Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Changed, ownership.Observe(receipt));
        var deletion = ownership.TryDelete(receipt);

        Assert.False(deletion.Removed);
        Assert.True(deletion.Conclusive);
        Assert.Equal(changed, File.ReadAllBytes(receipt.Route));
    }

    [Fact]
    public void FileReceiptRejectsAReplacementAtTheSameRoute()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var directory = Assert.IsType<ExactFileSystemObjectOwnership.DirectoryReceipt>(
            ownership.TryCreateOnlyDirectory(temp.Path, "owned"));
        var content = "same-content"u8.ToArray();
        var receipt = ownership.CreateOnlyFile(directory, "Feature.bas", content);
        ownership.ReleaseCreationFence(directory);
        File.Delete(receipt.Route);
        File.WriteAllBytes(receipt.Route, content);

        Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Changed, ownership.Observe(receipt));
        var deletion = ownership.TryDelete(receipt);

        Assert.False(deletion.Removed);
        Assert.True(deletion.Conclusive);
        Assert.Equal(content, File.ReadAllBytes(receipt.Route));
    }

    [Fact]
    public void FileReceiptDoesNotAuthorizeAnAlternateHardLinkRoute()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var directory = Assert.IsType<ExactFileSystemObjectOwnership.DirectoryReceipt>(
            ownership.TryCreateOnlyDirectory(temp.Path, "owned"));
        var receipt = ownership.CreateOnlyFile(
            directory,
            "Feature.bas",
            "owned-content"u8);
        ownership.ReleaseCreationFence(directory);
        var aliasPath = Path.Combine(temp.Path, "Feature-alias.bas");
        Assert.True(
            CreateHardLink(aliasPath, receipt.Route, IntPtr.Zero),
            new Win32Exception(Marshal.GetLastWin32Error()).Message);
        File.Delete(receipt.Route);

        Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Changed, ownership.Observe(receipt));
        var deletion = ownership.TryDelete(receipt);

        Assert.False(deletion.Removed);
        Assert.True(deletion.Conclusive);
        Assert.True(File.Exists(aliasPath));
        Assert.False(File.Exists(receipt.Route));
    }

    [Fact]
    public void FileReceiptRollsBackDispositionWhenAHardLinkAppearsAfterFinalProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var directory = Assert.IsType<ExactFileSystemObjectOwnership.DirectoryReceipt>(
            ownership.TryCreateOnlyDirectory(temp.Path, "owned"));
        var content = "owned-content"u8.ToArray();
        var receipt = ownership.CreateOnlyFile(directory, "Feature.bas", content);
        ownership.ReleaseCreationFence(directory);
        var aliasPath = Path.Combine(temp.Path, "Feature-racing-alias.bas");
        var hardLinkWasCreated = false;
        var hardLinkError = 0;

        var deletion = ownership.TryDelete(
            receipt,
            onDispositionStarting: path =>
            {
                hardLinkWasCreated = CreateHardLink(
                    aliasPath,
                    path,
                    IntPtr.Zero);
                if (!hardLinkWasCreated)
                {
                    hardLinkError = Marshal.GetLastWin32Error();
                }
            });

        if (hardLinkWasCreated)
        {
            Assert.False(deletion.Removed);
            Assert.True(deletion.Conclusive);
            Assert.Equal(content, File.ReadAllBytes(receipt.Route));
            Assert.Equal(content, File.ReadAllBytes(aliasPath));
        }
        else
        {
            Assert.Equal(32, hardLinkError);
            Assert.True(deletion.Removed);
            Assert.False(File.Exists(receipt.Route));
            Assert.False(File.Exists(aliasPath));
        }
    }

    [Fact]
    public void FileReceiptRollsBackDispositionWhenPostDispositionProofFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var directory = Assert.IsType<ExactFileSystemObjectOwnership.DirectoryReceipt>(
            ownership.TryCreateOnlyDirectory(temp.Path, "owned"));
        var content = "owned-content"u8.ToArray();
        var receipt = ownership.CreateOnlyFile(directory, "Feature.bas", content);
        ownership.ReleaseCreationFence(directory);

        var deletion = ownership.TryDelete(
            receipt,
            onDispositionSet: _ => throw new IOException("Injected proof failure."));

        Assert.False(deletion.Removed);
        Assert.False(deletion.Conclusive);
        Assert.Equal(content, File.ReadAllBytes(receipt.Route));
    }

    [Fact]
    public void FileReceiptRetriesRollbackBeforeReportingRetained()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var directory = Assert.IsType<ExactFileSystemObjectOwnership.DirectoryReceipt>(
            ownership.TryCreateOnlyDirectory(temp.Path, "owned"));
        var content = "owned-content"u8.ToArray();
        var receipt = ownership.CreateOnlyFile(directory, "Feature.bas", content);
        ownership.ReleaseCreationFence(directory);
        var rollbackAttempts = 0;

        var deletion = ownership.TryDelete(
            receipt,
            onDispositionSet: _ => throw new IOException("Injected proof failure."),
            rollbackAttemptGate: attempt =>
            {
                rollbackAttempts++;
                return attempt > 1;
            });

        Assert.False(deletion.Removed);
        Assert.False(deletion.Conclusive);
        Assert.Equal(2, rollbackAttempts);
        Assert.Equal(content, File.ReadAllBytes(receipt.Route));
    }

    [Fact]
    public void FileReceiptDoesNotReportRetainedWhenRollbackCannotBeProved()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var directory = Assert.IsType<ExactFileSystemObjectOwnership.DirectoryReceipt>(
            ownership.TryCreateOnlyDirectory(temp.Path, "owned"));
        var receipt = ownership.CreateOnlyFile(
            directory,
            "Feature.bas",
            "owned-content"u8);
        ownership.ReleaseCreationFence(directory);
        var rollbackAttempts = 0;

        Assert.Throws<ExactFileSystemObjectOwnership.RollbackException>(
            () => ownership.TryDelete(
                receipt,
                onDispositionSet: _ => throw new IOException("Injected proof failure."),
                rollbackAttemptGate: _ =>
                {
                    rollbackAttempts++;
                    return false;
                }));

        Assert.Equal(2, rollbackAttempts);
        ownership.Dispose();
        Assert.False(File.Exists(receipt.Route));
    }

    [Fact]
    public void FileReceiptRetainsAReversibleExternalDeleteDisposition()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var directory = Assert.IsType<ExactFileSystemObjectOwnership.DirectoryReceipt>(
            ownership.TryCreateOnlyDirectory(temp.Path, "owned"));
        var content = "owned-content"u8.ToArray();
        var receipt = ownership.CreateOnlyFile(directory, "Feature.bas", content);
        ownership.ReleaseCreationFence(directory);
        using var externalDelete = OpenForDelete(receipt.Route, isDirectory: false);
        Assert.True(SetDeleteDisposition(externalDelete, delete: true));

        var deletion = ownership.TryDelete(receipt);

        Assert.False(deletion.Removed);
        Assert.False(deletion.Conclusive);
        Assert.True(SetDeleteDisposition(externalDelete, delete: false));
        externalDelete.Dispose();
        Assert.Equal(content, File.ReadAllBytes(receipt.Route));
        Assert.True(ownership.TryDelete(receipt).Removed);
        Assert.True(ownership.TryDeleteEmpty(directory).Removed);
    }

    [Fact]
    public void DirectoryReceiptRetainsAReversibleExternalDeleteDisposition()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var directory = Assert.IsType<ExactFileSystemObjectOwnership.DirectoryReceipt>(
            ownership.TryCreateOnlyDirectory(temp.Path, "owned"));
        ownership.ReleaseCreationFence(directory);
        using var externalDelete = OpenForDelete(directory.Route, isDirectory: true);
        Assert.True(SetDeleteDisposition(externalDelete, delete: true));

        var deletion = ownership.TryDeleteEmpty(directory);

        Assert.False(deletion.Removed);
        Assert.False(deletion.Conclusive);
        Assert.True(SetDeleteDisposition(externalDelete, delete: false));
        Assert.True(Directory.Exists(directory.Route));
        externalDelete.Dispose();
        Assert.True(ownership.TryDeleteEmpty(directory).Removed);
    }

    [Fact]
    public void FileReceiptRejectsAReparsePointReplacementWithoutFollowingIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var directory = Assert.IsType<ExactFileSystemObjectOwnership.DirectoryReceipt>(
            ownership.TryCreateOnlyDirectory(temp.Path, "owned"));
        var receipt = ownership.CreateOnlyFile(
            directory,
            "Feature.bas",
            "owned-content"u8);
        ownership.ReleaseCreationFence(directory);
        var sentinelPath = Path.Combine(temp.Path, "sentinel.bas");
        File.WriteAllText(sentinelPath, "sentinel-content");
        File.Delete(receipt.Route);
        File.CreateSymbolicLink(receipt.Route, sentinelPath);

        var deletion = ownership.TryDelete(receipt);

        Assert.False(deletion.Removed);
        Assert.True(deletion.Conclusive);
        Assert.True(File.GetAttributes(receipt.Route).HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal("sentinel-content", File.ReadAllText(sentinelPath));
    }

    [Fact]
    public void ReceiptAuthorityIsLimitedToItsIssuingSession()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var issuer = ExactFileSystemObjectOwnership.Open();
        using var foreignSession = ExactFileSystemObjectOwnership.Open();
        var directory = Assert.IsType<ExactFileSystemObjectOwnership.DirectoryReceipt>(
            issuer.TryCreateOnlyDirectory(temp.Path, "owned"));
        var receipt = issuer.CreateOnlyFile(
            directory,
            "Feature.bas",
            "owned-content"u8);
        issuer.ReleaseCreationFence(directory);

        Assert.Throws<InvalidOperationException>(
            () => foreignSession.TryDelete(receipt));

        Assert.True(File.Exists(receipt.Route));
        Assert.True(issuer.TryDelete(receipt).Removed);
        Assert.True(issuer.TryDeleteEmpty(directory).Removed);
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    private static SafeFileHandle OpenForDelete(string route, bool isDirectory)
    {
        var handle = CreateFile(
            route,
            DeleteAccess,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal
            | FileFlagOpenReparsePoint
            | (isDirectory ? FileFlagBackupSemantics : 0),
            IntPtr.Zero);
        Assert.False(
            handle.IsInvalid,
            new Win32Exception(Marshal.GetLastWin32Error()).Message);
        return handle;
    }

    private static bool SetDeleteDisposition(SafeFileHandle handle, bool delete)
    {
        var disposition = new FileDispositionInformation
        {
            DeleteFile = delete ? (byte)1 : (byte)0
        };
        return SetFileInformationByHandle(
            handle,
            FileDispositionInfoClass,
            ref disposition,
            (uint)Marshal.SizeOf<FileDispositionInformation>());
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        public byte DeleteFile;
    }
}
