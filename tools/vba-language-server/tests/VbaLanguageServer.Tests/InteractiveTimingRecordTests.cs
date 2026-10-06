using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class InteractiveTimingRecordTests
{
    [Theory]
    [InlineData("readFence=invalid\n", typeof(FormatException))]
    [InlineData("inputSequence=42\n", typeof(InvalidOperationException))]
    [InlineData("readFence=41\nreadFence=42\n", typeof(InvalidOperationException))]
    public async Task Malformed_record_is_not_retried_or_reinterpreted(string text, Type errorType)
    {
        var directory = Directory.CreateTempSubdirectory("vba-ls-timing-record-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "hover.completed"), text);
            var read = InteractiveTimingRecord.WaitAsync(
                directory,
                name => name == "hover.completed",
                TimeSpan.FromSeconds(5));

            Assert.True(read.IsCompletedSuccessfully);
            var record = await read;
            Assert.Throws(errorType, () => record.ReadValue("readFence"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Disappearing_record_preserves_the_non_sharing_io_error_without_retry()
    {
        var directory = Directory.CreateTempSubdirectory("vba-ls-timing-record-").FullName;
        var path = Path.Combine(directory, "hover.completed");
        try
        {
            File.WriteAllText(path, "readFence=41\n");
            var read = InteractiveTimingRecord.WaitAsync(
                directory,
                name =>
                {
                    if (name != "hover.completed")
                    {
                        return false;
                    }

                    File.Delete(path);
                    return true;
                },
                TimeSpan.FromSeconds(5));

            Assert.True(read.IsFaulted);
            var error = await Assert.ThrowsAsync<FileNotFoundException>(() => read);
            Assert.Equal(path, error.FileName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Wait_includes_publication_of_a_matching_record()
    {
        var directory = Directory.CreateTempSubdirectory("vba-ls-timing-record-").FullName;
        var path = Path.Combine(directory, "hover.completed");
        try
        {
            File.WriteAllText(Path.Combine(directory, "other.completed"), "readFence=99\n");
            var temporaryPath = Path.Combine(directory, "hover.tmp");
            File.WriteAllText(temporaryPath, "readFence=41\n");
            var read = InteractiveTimingRecord.WaitAsync(
                directory,
                name => name == "hover.completed",
                TimeSpan.FromSeconds(5));

            Assert.False(read.IsCompleted);
            File.Move(temporaryPath, path);
            var record = await read.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(41, record.ReadValue("readFence"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_record_times_out_without_a_fabricated_sharing_error()
    {
        var directory = Directory.CreateTempSubdirectory("vba-ls-timing-record-").FullName;
        try
        {
            var read = InteractiveTimingRecord.WaitAsync(
                directory,
                name => name == "hover.completed",
                TimeSpan.FromMilliseconds(100));

            var error = await Assert.ThrowsAsync<TimeoutException>(
                () => read.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Contains(directory, error.Message);
            Assert.Null(error.InnerException);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsSharingFact]
    public async Task Persistent_sharing_lock_times_out_with_the_path_and_original_error()
    {
        var directory = Directory.CreateTempSubdirectory("vba-ls-timing-record-").FullName;
        var path = Path.Combine(directory, "hover.completed");
        try
        {
            File.WriteAllText(path, "readFence=41\n");
            using var held = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var read = InteractiveTimingRecord.WaitAsync(
                directory,
                name => name == "hover.completed",
                TimeSpan.FromMilliseconds(100));

            var error = await Assert.ThrowsAsync<TimeoutException>(
                () => read.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Contains(path, error.Message);
            var sharingError = Assert.IsType<IOException>(error.InnerException);
            Assert.Equal(unchecked((int)0x80070020), sharingError.HResult);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [WindowsSharingFact]
    public async Task Transient_sharing_lock_waits_for_release_and_returns_a_stable_record()
    {
        var directory = Directory.CreateTempSubdirectory("vba-ls-timing-record-").FullName;
        var path = Path.Combine(directory, "hover.completed");
        try
        {
            File.WriteAllText(path, "inputSequence=42\nreadFence=41\n");
            using var held = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var read = InteractiveTimingRecord.WaitAsync(
                directory,
                name => name == "hover.completed",
                TimeSpan.FromSeconds(5));
            if (read.IsCompleted)
            {
                await read;
            }

            Assert.False(read.IsCompleted);
            held.Dispose();
            var record = await read.WaitAsync(TimeSpan.FromSeconds(5));

            using var reacquired = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(42, record.ReadValue("inputSequence"));
            Assert.Equal(41, record.ReadValue("readFence"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class WindowsSharingFactAttribute : FactAttribute
    {
        public WindowsSharingFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "This regression requires Windows sharing-violation HRESULT 0x80070020.";
            }
        }
    }
}
