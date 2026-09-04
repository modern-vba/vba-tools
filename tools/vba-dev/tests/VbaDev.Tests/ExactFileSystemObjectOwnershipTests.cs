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
    public void ReceiptTypesAcceptOnlyOpaqueProvenState()
    {
        var receiptTypes = new[]
        {
            typeof(ExactFileSystemObjectOwnership.FileReceipt),
            typeof(ExactFileSystemObjectOwnership.DirectoryReceipt)
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
            .Where(method =>
                method.ReturnType == typeof(ExactFileSystemObjectOwnership.FileReceipt)
                || method.ReturnType == typeof(ExactFileSystemObjectOwnership.DirectoryReceipt)
                || method.ReturnType == typeof(ExactFileSystemObjectOwnership.StableFileCapture))
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["CaptureTrustedStableFile", "CreateOnlyFile", "TryCreateOnlyDirectory"],
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
    public void CreateOnlyFileDoesNotFollowARetargetedParentRoute()
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

        Assert.ThrowsAny<IOException>(() => ownership.CreateOnlyFile(
            directory,
            "Feature.bas",
            "owned-content"u8,
            _ =>
            {
                Directory.Move(parent, movedParent);
                Directory.CreateDirectory(replacementDirectory);
            }));

        Assert.False(File.Exists(Path.Combine(replacementDirectory, "Feature.bas")));
        Assert.False(File.Exists(Path.Combine(movedParent, "owned", "Feature.bas")));
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

    [Fact]
    public void TrustedStableCaptureRejectsAFileThatAlreadyHasAnotherHardLink()
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

        Assert.Throws<IOException>(() => ownership.CaptureTrustedStableFile(path));

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
