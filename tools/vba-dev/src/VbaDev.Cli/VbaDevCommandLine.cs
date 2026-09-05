using System.CommandLine;
using VbaDev.Composition;

namespace VbaDev.Cli;

/// <summary>
/// Invokes the constructed <c>vba-dev</c> command graph against supplied process streams.
/// </summary>
public sealed class VbaDevCommandLine
{
    private readonly VbaDevCommandGraph commandGraph;

    private VbaDevCommandLine(VbaDevCommandGraph commandGraph)
    {
        this.commandGraph = commandGraph;
    }

    internal VbaDevCommandGraph CommandGraph => commandGraph;

    /// <summary>
    /// Creates the default command line.
    /// </summary>
    /// <returns>The command line used by the standalone executable.</returns>
    public static VbaDevCommandLine CreateDefault()
        => Create(ToolingCompositionRoot.CreateApplicationComposition());

    /// <summary>
    /// Creates a command line over shell-neutral composed application services.
    /// </summary>
    /// <param name="composition">The services and working directory used by command handlers.</param>
    /// <returns>A command line using the supplied application services.</returns>
    public static VbaDevCommandLine Create(ToolingApplicationComposition composition)
        => Create(
            composition,
            Environment.ProcessPath
            ?? throw new InvalidOperationException("The generating vba-dev executable path is unavailable."));

    internal static VbaDevCommandLine Create(
        ToolingApplicationComposition composition,
        string generatingExecutablePath)
        => new(VbaDevCommandGrammar.Create(composition, generatingExecutablePath));

    /// <summary>
    /// Parses and invokes the command line against explicit output streams.
    /// </summary>
    /// <param name="args">The arguments after the executable name.</param>
    /// <param name="standardOutput">The standard output writer.</param>
    /// <param name="standardError">The standard error writer.</param>
    /// <param name="cancellationToken">The cooperative invocation cancellation token.</param>
    /// <returns>The process exit code.</returns>
    public Task<int> InvokeAsync(
        IReadOnlyList<string> args,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
        => InvokeAsync(
            args,
            Stream.Null,
            standardOutput,
            standardError,
            cancellationToken);

    /// <summary>
    /// Parses and invokes the command line against explicit process streams.
    /// </summary>
    /// <param name="args">The arguments after the executable name.</param>
    /// <param name="standardInput">The raw standard input byte stream.</param>
    /// <param name="standardOutput">The standard output writer.</param>
    /// <param name="standardError">The standard error writer.</param>
    /// <param name="cancellationToken">The cooperative invocation cancellation token.</param>
    /// <returns>The process exit code.</returns>
    public async Task<int> InvokeAsync(
        IReadOnlyList<string> args,
        Stream standardInput,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        var configuration = new InvocationConfiguration
        {
            Output = standardOutput,
            Error = standardError,
            ProcessTerminationTimeout = Timeout.InfiniteTimeSpan
        };
        var parseResult = commandGraph.RootCommand.Parse(args);
        if (parseResult.Errors.Count > 0 ||
            !string.Equals(
                parseResult.GetValue(commandGraph.CancellationTransportOption),
                "stdin-v1",
                StringComparison.Ordinal))
        {
            return await parseResult
                .InvokeAsync(configuration, cancellationToken)
                .ConfigureAwait(false);
        }

        using var invocationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        using var monitorCancellation = new CancellationTokenSource();
        var monitor = ObserveStdinCancellationAsync(
            standardInput,
            invocationCancellation,
            monitorCancellation.Token);
        try
        {
            return await parseResult
                .InvokeAsync(configuration, invocationCancellation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                monitorCancellation.Cancel();
            }
            catch
            {
                // Transport-reader cancellation cannot replace command outcome authority.
            }

            await monitor.ConfigureAwait(false);
        }
    }

    private static async Task ObserveStdinCancellationAsync(
        Stream standardInput,
        CancellationTokenSource invocationCancellation,
        CancellationToken monitorCancellation)
    {
        ReadOnlyMemory<byte> expectedPayload = "cancel"u8.ToArray();
        var buffer = new byte[64];
        var matchedBytes = 0;
        var discardingFrame = false;
        var monitorStopped = Task.Delay(Timeout.InfiniteTimeSpan, monitorCancellation);
        try
        {
            while (true)
            {
                if (monitorCancellation.IsCancellationRequested)
                {
                    return;
                }

                var readTask = standardInput.ReadAsync(buffer, monitorCancellation).AsTask();
                var completedTask = await Task.WhenAny(readTask, monitorStopped)
                    .ConfigureAwait(false);
                if (completedTask != readTask)
                {
                    _ = readTask.ContinueWith(
                        static completedRead => _ = completedRead.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted |
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    return;
                }

                var read = await readTask.ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                foreach (var value in buffer.AsSpan(0, read))
                {
                    if (value == (byte)'\n')
                    {
                        if (!discardingFrame && matchedBytes == expectedPayload.Length)
                        {
                            invocationCancellation.Cancel();
                        }

                        matchedBytes = 0;
                        discardingFrame = false;
                        continue;
                    }

                    if (
                        discardingFrame ||
                        matchedBytes >= expectedPayload.Length ||
                        value != expectedPayload.Span[matchedBytes])
                    {
                        discardingFrame = true;
                        continue;
                    }

                    matchedBytes++;
                }

                await Task.Yield();
            }
        }
        catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested)
        {
        }
        catch
        {
            // Invalid or unavailable transport input does not replace command outcome authority.
        }
    }
}
