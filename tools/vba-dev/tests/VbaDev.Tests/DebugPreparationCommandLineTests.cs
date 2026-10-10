using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using VbaDev.Cli;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class DebugPreparationCommandLineTests
{
    public static IEnumerable<object[]> InvalidPreparationModes()
    {
        yield return [new[] { "prepare-debug" }];
        yield return [new[] { "prepare-debug", "--describe", "--interactive", "false" }];
        yield return [new[] { "prepare-debug", "--describe", "--source-snapshot", "sources" }];
        yield return [new[] { "prepare-debug", "--describe", "--excel-process-id", "1234" }];
        yield return [new[] { "prepare-debug", "--source-snapshot", "sources", "--generation", "abc" }];
    }

    [Theory]
    [MemberData(nameof(InvalidPreparationModes))]
    public async Task InvalidPreparationModeUsesTheGrammarRouterBeforeAnyDomainWork(string[] arguments)
    {
        using var temp = TempDirectory.Create();
        var automationCalls = 0;
        var composition = ToolingCompositionRoot.CreateApplicationComposition(temp.Path,
            debugSourceWorkbookAutomationFactory: (_, _) =>
            {
                automationCalls++;
                throw new InvalidOperationException("Invalid grammar cannot acquire Excel automation.");
            });

        var result = await VbaDevCommandLine.Create(composition).RunAsync(arguments);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal("Error: The supplied values do not form a valid intent for command 'vba-dev prepare-debug'."
            + Environment.NewLine + "Hint: Run 'vba-dev prepare-debug --help' for usage."
            + Environment.NewLine, result.StandardError);
        Assert.Equal(0, automationCalls);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task DescribeResolvesTheSelectedSourceWorkbookWithoutOpeningExcelOrRequiringASnapshot()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        var workbookPath = Path.Combine(sourceSet, "Book1.xlsm");
        File.WriteAllText(workbookPath, "saved source", new UTF8Encoding(false));
        var composition = ToolingCompositionRoot.CreateApplicationComposition(root,
            debugSourceWorkbookAutomationFactory: (_, _) =>
                throw new InvalidOperationException("Description must never acquire Excel automation."));

        var result = await VbaDevCommandLine.Create(composition).RunAsync(
            ["prepare-debug", "--project", root, "--document", "Book1", "--describe"]);

        Assert.Equal(0, result.ExitCode);
        using var receipt = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("debugWorkbookDescription", receipt.RootElement.GetProperty("type").GetString());
        Assert.Equal("1.0", receipt.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(root, receipt.RootElement.GetProperty("projectRoot").GetString());
        Assert.Equal("Book1", receipt.RootElement.GetProperty("documentName").GetString());
        Assert.Equal(workbookPath, receipt.RootElement.GetProperty("workbookPath").GetString());
        Assert.Equal("saved source", File.ReadAllText(workbookPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    [InlineData("false")]
    public async Task ManagedDirtyPreparationUsesCapturedSnapshotWithoutConfirmationOrSaving(string? interactive)
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceSet);
        var workbookPath = Path.Combine(sourceSet, "Book1.xlsm");
        File.WriteAllText(workbookPath, "unchanged saved source workbook", new UTF8Encoding(false));
        var snapshot = temp.CreateDirectory("CapturedSources");
        File.WriteAllText(Path.Combine(snapshot, "Local.bas"),
            "Attribute VB_Name = \"Local\"\r\nPublic Sub RunProbe()\r\nEnd Sub\r\n",
            new UTF8Encoding(false));
        var automation = new RecordingSourceWorkbookAutomation();
        automation.Session.WasAlreadyOpen = true;
        automation.Session.IsSaved = false;
        const int processId = 1234;
        const long startTicks = 638900000000000000;
        var factoryCalls = 0;
        var composition = ToolingCompositionRoot.CreateApplicationComposition(root,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty,
            debugSourceWorkbookAutomationFactory: (pid, ticks) =>
            {
                Assert.Equal(processId, pid);
                Assert.Equal(startTicks, ticks);
                factoryCalls++;
                return automation;
            });
        using var input = new ReadyInput();
        using var output = new StringWriter();
        var generation = new string('a', 32);
        using var error = new ReadyWriter(input, generation, workbookPath, processId, startTicks);

        var arguments = new List<string> { "prepare-debug", "--project", root, "--document", "Book1",
                "--source-snapshot", snapshot, "--generation", generation,
                "--excel-process-id", processId.ToString(),
                "--excel-process-start-utc-ticks", startTicks.ToString(),
                "--cancellation-transport", "stdin-v1" };
        if (interactive is not null) arguments.AddRange(["--interactive", interactive]);
        var exitCode = await VbaDevCommandLine.Create(composition).InvokeAsync(
            arguments.ToArray(),
            input, output, error, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, error.ReadyRequestCount);
        Assert.DoesNotContain("workbookConfirmation", error.ToString());
        Assert.DoesNotContain("[y/N]", error.ToString());
        Assert.Equal(workbookPath, Assert.Single(automation.OpenedPaths));
        Assert.Contains("import:Local.bas", automation.Session.Events);
        Assert.DoesNotContain("save", automation.Session.Events);
        Assert.Equal("unchanged saved source workbook", File.ReadAllText(workbookPath));
        using var receipt = JsonDocument.Parse(output.ToString());
        Assert.Equal("debugWorkbookPrepared", receipt.RootElement.GetProperty("type").GetString());
        Assert.Equal(workbookPath, receipt.RootElement.GetProperty("workbookPath").GetString());
        Assert.Equal(generation, receipt.RootElement.GetProperty("generationId").GetString());
    }

    private sealed class ReadyWriter(
        ReadyInput input, string generation, string workbookPath, int processId, long startTicks) : StringWriter
    {
        internal int ReadyRequestCount { get; private set; }

        public override Task WriteLineAsync(string? value)
        {
            WriteLine(value);
            if (value is null || !value.StartsWith('{')) return Task.CompletedTask;
            using var payload = JsonDocument.Parse(value);
            var ready = payload.RootElement;
            if (ready.GetProperty("type").GetString() != "debugPreparationReady")
                return Task.CompletedTask;
            Assert.Equal(generation, ready.GetProperty("generationId").GetString());
            Assert.Equal(workbookPath, ready.GetProperty("workbookPath").GetString());
            Assert.Equal(processId, ready.GetProperty("excelProcessId").GetInt32());
            Assert.Equal(startTicks, ready.GetProperty("excelProcessStartUtcTicks").GetInt64());
            ReadyRequestCount++;
            input.Reply($"prepare:{ready.GetProperty("requestId").GetString()}:ready\n");
            return Task.CompletedTask;
        }
    }

    private sealed class ReadyInput : Stream
    {
        private readonly Channel<byte> bytes = Channel.CreateUnbounded<byte>();

        internal void Reply(string frame)
        {
            foreach (var value in Encoding.UTF8.GetBytes(frame)) bytes.Writer.TryWrite(value);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var first = await bytes.Reader.ReadAsync(cancellationToken);
            buffer.Span[0] = first;
            var count = 1;
            while (count < buffer.Length && bytes.Reader.TryRead(out var value))
                buffer.Span[count++] = value;
            return count;
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
}
