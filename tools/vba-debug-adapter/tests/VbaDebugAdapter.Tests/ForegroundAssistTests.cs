using VbaDebugAdapter.ForegroundAssist;
using Xunit;
using System.Collections.Concurrent;
using System.Globalization;

namespace VbaDebugAdapter.Tests;

public sealed class ForegroundAssistTests
{
    [Fact]
    public void AssistOnlyTouchesTheMatchingDebugWorkbookProcess()
    {
        var probe = new RecordingProbe([41, 84, 99], new HashSet<int> { 99 });
        var assist = new ForegroundAssistLoop(new HashSet<int> { 41 }, probe);

        Assert.True(assist.TryOneCycle());
        Assert.Equal([99], probe.Attempts);
    }

    [Fact]
    public void AssistFailsClosedWhenTwoNewProcessesMatchTheDebugWorkbook()
    {
        var probe = new RecordingProbe([84, 99], new HashSet<int> { 84, 99 });
        var assist = new ForegroundAssistLoop(new HashSet<int>(), probe);

        Assert.Throws<InvalidOperationException>(() => assist.TryOneCycle());
        Assert.Empty(probe.Attempts);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ProtocolSignalsReadyAndRunsAProbeWhileInputReadIsBlocked(int minimumAttempts)
    {
        using var input = new BlockingLineReader();
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var probe = new RecordingProbe([84], new HashSet<int> { 84 }, minimumAttempts);
        var assist = new ForegroundAssistLoop(new HashSet<int>(), probe);

        var run = ForegroundAssistProtocol.RunAsync(input, output, assist);
        try
        {
            Assert.Equal($"READY{Environment.NewLine}", output.ToString());
            await input.WaitForReadAsync();
            await probe.WaitForAttemptsAsync();
            Assert.False(run.IsCompleted);
            var attempts = probe.Attempts;
            Assert.True(attempts.Count >= minimumAttempts);
            Assert.All(attempts, processId => Assert.Equal(84, processId));
        }
        finally
        {
            input.Release();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void DebugWorkbookPathMatchRequiresThisFixturesSourceMarker()
    {
        using var temp = TempDirectory.Create();
        var root = Path.Combine(temp.Path, "workspaces");
        const string sessionId = "0123456789abcdef0123456789abcdef";
        const string ownerToken = "11111111111111111111111111111111";
        const string otherOwnerToken = "22222222222222222222222222222222";
        var generationRoot = Path.Combine(root, sessionId, "generations", "g1");
        var source = Path.Combine(generationRoot, "source", "nested", "Caller.bas");
        var workbook = Path.Combine(generationRoot, "output", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, $"Public Sub Run()\r\n' fixture owner {ownerToken}\r\nEnd Sub\r\n");

        Assert.True(DebugWorkbookIdentity.Matches(root, "Book1.xlsm", workbook, ownerToken));
        Assert.False(DebugWorkbookIdentity.Matches(root, "Book1.xlsm", workbook, otherOwnerToken));
        Assert.False(DebugWorkbookIdentity.Matches(root, "Book1.xlsm",
            Path.Combine(generationRoot, "output", "Other.xlsm"), ownerToken));
        Assert.False(DebugWorkbookIdentity.Matches(root + "-other", "Book1.xlsm", workbook, ownerToken));
        Assert.False(DebugWorkbookIdentity.Matches(root, "Book1.xlsm",
            Path.Combine(root, "user", "generations", "g1", "output", "Book1.xlsm"), ownerToken));
        Assert.False(DebugWorkbookIdentity.Matches(root, "Book1.xlsm", workbook, "not-a-token"));
    }

    private sealed class RecordingProbe(
        IReadOnlyList<int> processIds,
        IReadOnlySet<int> matches,
        int minimumAttempts = 1)
        : IForegroundAssistProbe
    {
        private readonly ConcurrentQueue<int> attempts = new();
        private readonly TaskCompletionSource attemptsReady = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<int> Attempts => attempts.ToArray();

        public Task WaitForAttemptsAsync() => attemptsReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public IReadOnlyList<int> CaptureExcelProcessIds() => processIds;

        public bool IsDebugWorkbookProcess(int processId) => matches.Contains(processId);

        public bool TryBringVbeToForeground(int processId)
        {
            attempts.Enqueue(processId);
            if (attempts.Count >= minimumAttempts)
            {
                attemptsReady.TrySetResult();
            }
            return true;
        }
    }

    private sealed class BlockingLineReader : StringReader
    {
        private readonly ManualResetEventSlim release = new(false);
        private readonly TaskCompletionSource readStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingLineReader() : base(string.Empty) { }

        public override string? ReadLine()
        {
            readStarted.TrySetResult();
            release.Wait();
            return null;
        }

        public Task WaitForReadAsync() => readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Release() => release.Set();

        protected override void Dispose(bool disposing)
        {
            release.Set();
            release.Dispose();
            base.Dispose(disposing);
        }
    }
}
