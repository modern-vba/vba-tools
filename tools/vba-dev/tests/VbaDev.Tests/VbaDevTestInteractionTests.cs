using System.Text.Json;
using System.Text;
using VbaDev.Cli;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class VbaDevTestInteractionTests
{
    [Fact]
    public void CapabilitiesAdvertiseSourceWorkbookTestsWithoutChangingResultOrSnapshotSchemas()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(temp.Path);

        var result = application.Run(["capabilities", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        using var payload = JsonDocument.Parse(result.StandardOutput);
        var features = payload.RootElement.GetProperty("featureVersions");
        Assert.True(features.TryGetProperty("test.sourceWorkbook", out var sourceWorkbook));
        Assert.Equal("1.0", sourceWorkbook.GetString());
        Assert.Equal("2.0", features.GetProperty("test.sourceSnapshot").GetString());
        Assert.Equal("1.0", features.GetProperty("invocation.stdinCancellation").GetString());
        Assert.Equal("1.0", features.GetProperty("invocation.stdinWorkbookConfirmation").GetString());
        Assert.Equal("1.2", payload.RootElement.GetProperty("commands")
            .GetProperty("test").GetProperty("outputSchemaVersion").GetString());
    }

    [Fact]
    public async Task DirectTestAcceptsTerminalYesBeforeImportAndExecutionWithoutSaving()
    {
        using var temp = TempDirectory.Create();
        var (application, automation) = CreateDirtyTest(temp.Path);
        using var input = new MemoryStream("y\n"u8.ToArray());
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await application.InvokeAsync(["test"], input, output, error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("[y/N]", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("replace", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("test VBA", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(["import:Local.bas", "test"], automation.Session.Events);
        Assert.Contains("1 passed", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonInteractiveTestRefusesDirtyWorkbookWithoutReadingOrPrompting(bool noBuild)
    {
        using var temp = TempDirectory.Create();
        var (application, automation) = CreateDirtyTest(temp.Path);
        using var input = new MemoryStream("yes\n"u8.ToArray());
        using var output = new StringWriter();
        using var error = new StringWriter();
        var arguments = noBuild
            ? new[] { "test", "--no-build", "--interactive", "false" }
            : new[] { "test", "--interactive", "false" };

        var exitCode = await application.InvokeAsync(arguments, input, output, error,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("[y/N]", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("unsaved", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, input.Position);
        Assert.Empty(automation.Session.Events);
        Assert.Empty(output.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("n\n")]
    public async Task DirectTestDefaultsToDeclineWithoutImportingOrExecuting(string answer)
    {
        using var temp = TempDirectory.Create();
        var (application, automation) = CreateDirtyTest(temp.Path);
        using var input = new MemoryStream(Encoding.ASCII.GetBytes(answer));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await application.InvokeAsync(["test"], input, output, error,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("[y/N]", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(automation.Session.Events);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task DirectNoBuildTestConfirmsExecutionOfCurrentVbaWithoutImportingOrSaving()
    {
        using var temp = TempDirectory.Create();
        var (application, automation) = CreateDirtyTest(temp.Path);
        using var input = new MemoryStream("yes\n"u8.ToArray());
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await application.InvokeAsync(["test", "--no-build"], input, output, error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("[y/N]", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("current VBA", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(["test"], automation.Session.Events);
        Assert.Contains("1 passed", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedTestUsesOneReaderAndOnlyMatchingConfirmation(bool noBuild)
    {
        using var temp = TempDirectory.Create();
        var (application, automation) = CreateDirtyTest(temp.Path);
        using var input = new ReplyInput();
        using var output = new StringWriter();
        using var error = new ReplyWriter(input, id =>
            new string('c', 4096) + $"\n\uFEFFconfirm:{id}:yes\n" +
            $"confirm:{new string('0', 32)}:yes\nconfirm:{id}:yes\r\nconfirm:{id}:yes\n");
        input.Reply($"confirm:{new string('0', 32)}:yes\n");
        var arguments = noBuild
            ? new[] { "test", "--no-build", "--cancellation-transport", "stdin-v1" }
            : new[] { "test", "--cancellation-transport", "stdin-v1" };

        var exitCode = await application.InvokeAsync(arguments, input, output, error,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, exitCode);
        Assert.Equal(1, error.RequestCount);
        Assert.DoesNotContain("[y/N]", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(noBuild ? ["test"] : new[] { "import:Local.bas", "test" }, automation.Session.Events);
        Assert.Equal(1, input.MaximumConcurrentReads);
    }

    [Fact]
    public async Task ManagedDeclineLeavesWorkbookUnchangedAndDoesNotExecuteTests()
    {
        using var temp = TempDirectory.Create();
        var (application, automation) = CreateDirtyTest(temp.Path);
        using var input = new ReplyInput();
        using var output = new StringWriter();
        using var error = new ReplyWriter(input, id => $"confirm:{id}:no\n");

        var exitCode = await application.InvokeAsync(["test", "--cancellation-transport", "stdin-v1"],
            input, output, error, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, exitCode);
        Assert.Equal(1, error.RequestCount);
        Assert.Empty(automation.Session.Events);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task ManagedEofCannotApproveWorkbookChangesOrTestExecution()
    {
        using var temp = TempDirectory.Create();
        var (application, automation) = CreateDirtyTest(temp.Path);
        using var input = new MemoryStream();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await application.InvokeAsync(["test", "--cancellation-transport", "stdin-v1"],
            input, output, error, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, exitCode);
        Assert.Empty(automation.Session.Events);
        Assert.Empty(output.ToString());
        Assert.DoesNotContain("[y/N]", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManagedCancellationDuringConfirmationDoesNotImportSaveOrExecuteTests()
    {
        using var temp = TempDirectory.Create();
        var (application, automation) = CreateDirtyTest(temp.Path);
        using var input = new ReplyInput();
        using var output = new StringWriter();
        using var error = new ReplyWriter(input, _ => "cancel\n");

        var exitCode = await application.InvokeAsync(["test", "--cancellation-transport", "stdin-v1"],
            input, output, error, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(0, exitCode);
        Assert.Empty(automation.Session.Events);
        Assert.Empty(output.ToString());
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

    private static (VbaDevCommandLine, RecordingSourceWorkbookAutomation) CreateDirtyTest(string root)
    {
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        File.WriteAllText(Path.Combine(sourceSet, "Book1.xlsm"), "saved source workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Local.bas"),
            "Attribute VB_Name = \"Local\"\nPublic Sub Test_Passes()\nEnd Sub\n", Encoding.UTF8);
        var automation = new RecordingSourceWorkbookAutomation();
        automation.Session.WasAlreadyOpen = true;
        automation.Session.IsSaved = false;
        var composition = ToolingCompositionRoot.CreateApplicationComposition(root,
            sourceWorkbookAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);
        return (VbaDevCommandLine.Create(composition), automation);
    }
}
