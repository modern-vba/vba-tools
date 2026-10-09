using Xunit;
using System.Text.Json;
using System.Text;
using VbaDev.Cli;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;

namespace VbaDev.Tests;

public sealed class WorkbookBuildInteractionTests
{
    [Fact]
    public void BuildOffersExplicitInteractionModeWithoutChangingPublish()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(temp.Path);

        var build = application.Run(["build", "--help"]);
        var publish = application.Run(["publish", "--help"]);

        Assert.Equal(0, build.ExitCode);
        Assert.Contains("--interactive", build.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("false", build.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(0, publish.ExitCode);
        Assert.DoesNotContain("--interactive", publish.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilitiesIdentifySourceWorkbookBuildExportAndConfirmation()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(temp.Path);

        var result = application.Run(["capabilities", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        using var payload = JsonDocument.Parse(result.StandardOutput);
        var features = payload.RootElement.GetProperty("featureVersions");
        Assert.True(features.TryGetProperty("build.sourceWorkbook", out var build));
        Assert.Equal("1.0", build.GetString());
        Assert.Equal("1.0", features.GetProperty("export.sourceWorkbook").GetString());
        Assert.Equal("1.0", features.GetProperty("invocation.stdinWorkbookConfirmation").GetString());
    }

    [Fact]
    public async Task DirectBuildAcceptsTerminalYesBeforeImportAndSave()
    {
        using var temp = TempDirectory.Create();
        var (application, automation) = CreateDirtyBuild(temp.Path);
        using var input = new MemoryStream("y\n"u8.ToArray());
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await application.InvokeAsync(["build"], input, output, error, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("[y/N]", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("save", automation.Session.Events);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("n\n")]
    public async Task DirectBuildDefaultsToDeclineWithoutChangingWorkbook(string answer)
    {
        using var temp = TempDirectory.Create();
        var (application, automation) = CreateDirtyBuild(temp.Path);
        using var input = new MemoryStream(Encoding.ASCII.GetBytes(answer));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await application.InvokeAsync(["build"], input, output, error, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("[y/N]", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(automation.Session.Events);
    }

    [Fact]
    public async Task NonInteractiveBuildRefusesDirtyWorkbookWithoutReadingOrPrompting()
    {
        using var temp = TempDirectory.Create();
        var (application, automation) = CreateDirtyBuild(temp.Path);
        using var input = new MemoryStream("yes\n"u8.ToArray());
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await application.InvokeAsync(["build", "--interactive", "false"],
            input, output, error, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("[y/N]", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("unsaved", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, input.Position);
        Assert.Empty(automation.Session.Events);
    }

    [Fact]
    public async Task ManagedBuildUsesOneReaderAndOnlyMatchingConfirmation()
    {
        using var temp = TempDirectory.Create();
        var (application, automation) = CreateDirtyBuild(temp.Path);
        using var input = new ReplyInput();
        using var output = new StringWriter();
        using var error = new ReplyWriter(input, id =>
            new string('c', 4096) + $"\n\uFEFFconfirm:{id}:yes\n" +
            $"confirm:{new string('0', 32)}:yes\nconfirm:{id}:yes\r\nconfirm:{id}:yes\n");
        input.Reply($"confirm:{new string('0', 32)}:yes\n");

        var exitCode = await application.InvokeAsync(
            ["build", "--interactive", "true", "--cancellation-transport", "stdin-v1"],
            input, output, error, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, error.RequestCount);
        Assert.DoesNotContain("[y/N]", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("save", automation.Session.Events);
        Assert.Equal(1, input.MaximumConcurrentReads);
    }

    [Fact]
    public async Task ConcurrentTerminalInvocationsKeepConfirmationStreamsIsolated()
    {
        using var firstTemp = TempDirectory.Create();
        using var secondTemp = TempDirectory.Create();
        var (first, firstAutomation) = CreateDirtyBuild(firstTemp.Path);
        var (second, secondAutomation) = CreateDirtyBuild(secondTemp.Path);
        using var firstInput = new ReplyInput();
        using var secondInput = new MemoryStream("no\n"u8.ToArray());
        using var firstOutput = new StringWriter();
        using var secondOutput = new StringWriter();
        using var firstError = new StringWriter();
        using var secondError = new StringWriter();

        var firstInvocation = first.InvokeAsync(["build"], firstInput, firstOutput, firstError,
            CancellationToken.None);
        Assert.False(firstInvocation.IsCompleted);
        var secondExit = await second.InvokeAsync(["build"], secondInput, secondOutput, secondError,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        firstInput.Reply("yes\n");
        var firstExit = await firstInvocation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, firstExit);
        Assert.Equal(1, secondExit);
        Assert.Contains("save", firstAutomation.Session.Events);
        Assert.Empty(secondAutomation.Session.Events);
        Assert.Contains(firstTemp.Path, firstError.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secondTemp.Path, firstError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManagedDeclineLeavesWorkbookUnchanged()
    {
        using var temp = TempDirectory.Create();
        var (application, automation) = CreateDirtyBuild(temp.Path);
        using var input = new ReplyInput();
        using var output = new StringWriter();
        using var error = new ReplyWriter(input, id => $"confirm:{id}:no\n");

        var exitCode = await application.InvokeAsync(["build", "--cancellation-transport", "stdin-v1"],
            input, output, error, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, exitCode);
        Assert.Equal(1, error.RequestCount);
        Assert.Empty(automation.Session.Events);
    }

    [Fact]
    public async Task ManagedEofCannotApproveWorkbookChanges()
    {
        using var temp = TempDirectory.Create();
        var (application, automation) = CreateDirtyBuild(temp.Path);
        using var input = new MemoryStream();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await application.InvokeAsync(["build", "--cancellation-transport", "stdin-v1"],
            input, output, error, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, exitCode);
        Assert.Empty(automation.Session.Events);
        Assert.DoesNotContain("[y/N]", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManagedCancellationDuringConfirmationDoesNotImportOrSave()
    {
        using var temp = TempDirectory.Create();
        var (application, automation) = CreateDirtyBuild(temp.Path);
        using var input = new ReplyInput();
        using var output = new StringWriter();
        using var error = new ReplyWriter(input, _ => "cancel\n");

        var exitCode = await application.InvokeAsync(["build", "--cancellation-transport", "stdin-v1"],
            input, output, error, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(0, exitCode);
        Assert.Empty(automation.Session.Events);
        Assert.Equal(1, input.MaximumConcurrentReads);
    }

    private sealed class ReplyWriter(ReplyInput input, Func<string, string> response) : StringWriter
    {
        internal int RequestCount { get; private set; }

        public override Task WriteLineAsync(string? value)
        {
            WriteLine(value);
            if (value is not null && value.StartsWith('{'))
            {
                using var payload = JsonDocument.Parse(value);
                if (payload.RootElement.GetProperty("type").GetString() == "workbookConfirmation")
                {
                    Assert.Equal("1.0", payload.RootElement.GetProperty("schemaVersion").GetString());
                    RequestCount++;
                    input.Reply(response(payload.RootElement.GetProperty("requestId").GetString()!));
                }
            }
            return Task.CompletedTask;
        }
    }

    private sealed class ReplyInput : Stream
    {
        private readonly System.Threading.Channels.Channel<byte> bytes =
            System.Threading.Channels.Channel.CreateUnbounded<byte>();
        private int readers;
        internal int MaximumConcurrentReads { get; private set; }
        internal void Reply(string text)
        {
            foreach (var value in Encoding.UTF8.GetBytes(text)) bytes.Writer.TryWrite(value);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var activeReaders = Interlocked.Increment(ref readers);
            MaximumConcurrentReads = Math.Max(MaximumConcurrentReads, activeReaders);
            try
            {
                var first = await bytes.Reader.ReadAsync(cancellationToken);
                buffer.Span[0] = first;
                var count = 1;
                while (count < buffer.Length && bytes.Reader.TryRead(out var value))
                    buffer.Span[count++] = value;
                return count;
            }
            finally { Interlocked.Decrement(ref readers); }
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static (VbaDevCommandLine, RecordingSourceWorkbookAutomation) CreateDirtyBuild(string root)
    {
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        File.WriteAllText(Path.Combine(sourceSet, "Book1.xlsm"), "saved workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"), "Attribute VB_Name = \"Local\"", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation();
        automation.Session.WasAlreadyOpen = true;
        automation.Session.IsSaved = false;
        var composition = ToolingCompositionRoot.CreateApplicationComposition(root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);
        return (VbaDevCommandLine.Create(composition), automation);
    }
}
