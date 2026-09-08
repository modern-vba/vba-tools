using Microsoft.Win32.SafeHandles;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class DebugNativeHandleReleaseTests
{
    [Fact]
    public void AReleasedOwnedFileHandleProvidesNativeReleaseEvidence()
    {
        using var directory = TempDirectory.Create();
        using var file = File.OpenHandle(Path.Combine(directory.Path, "release.tmp"), FileMode.CreateNew, FileAccess.ReadWrite);
        var release = new DebugNativeHandleRelease(file);

        Assert.False(release.IsVerified);
        release.Release();
        release.Release();

        Assert.True(release.IsVerified);
    }
}
