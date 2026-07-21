using VbaDev.App.Debugging;
using VbaDev.App.Projects;
using System.Text.Json;

namespace VbaDev.Cli.Debugging;

/// <summary>
/// Hosts the minimal VBA Debug Adapter Protocol session over stdio-compatible streams.
/// </summary>
public sealed class VbaDebugAdapter
{
    private readonly ProjectContextResolver projectContextResolver;
    private readonly DebugLaunchCoordinator launchCoordinator;
    private readonly Func<string> getWorkingDirectory;

    /// <summary>
    /// Creates a VBA debug adapter over project resolution and launch application services.
    /// </summary>
    public VbaDebugAdapter(
        ProjectContextResolver projectContextResolver,
        DebugLaunchCoordinator launchCoordinator,
        Func<string> getWorkingDirectory)
    {
        this.projectContextResolver = projectContextResolver;
        this.launchCoordinator = launchCoordinator;
        this.getWorkingDirectory = getWorkingDirectory;
    }

    /// <summary>
    /// Processes one DAP session until the input stream reaches EOF.
    /// </summary>
    public async Task RunAsync(
        Stream input,
        Stream output,
        CancellationToken cancellationToken)
    {
        var connection = new DapConnection(input, output);
        DapRequest? pendingLaunchRequest = null;
        DebugLaunchRequest? pendingLaunch = null;
        var configurationDone = false;
        DebugRunningSession? runningSession = null;
        Task? monitorTask = null;
        try
        {
            while (await connection.ReadRequestAsync(cancellationToken).ConfigureAwait(false) is { } request)
            {
                if (request.Command.Equals("initialize", StringComparison.Ordinal))
                {
                    await connection.WriteResponseAsync(
                        request,
                        success: true,
                        body: new { supportsConfigurationDoneRequest = true },
                        message: null,
                        cancellationToken).ConfigureAwait(false);
                    await connection.WriteEventAsync(
                        "initialized",
                        body: null,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (request.Command.Equals("setBreakpoints", StringComparison.Ordinal) &&
                    HasEmptyBreakpointSet(request))
                {
                    await connection.WriteResponseAsync(
                        request,
                        success: true,
                        body: new { breakpoints = Array.Empty<object>() },
                        message: null,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (request.Command.Equals("threads", StringComparison.Ordinal))
                {
                    await connection.WriteResponseAsync(
                        request,
                        success: true,
                        body: new { threads = new[] { new { id = 1, name = "VBE" } } },
                        message: null,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (request.Command.Equals("launch", StringComparison.Ordinal))
                {
                    try
                    {
                        pendingLaunch = ResolveLaunchRequest(request);
                        pendingLaunchRequest = request;
                    }
                    catch (Exception ex) when (ex is DebugSetupException or ProjectManifestException)
                    {
                        await WriteLaunchFailureAsync(connection, request, ex.Message, cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (configurationDone)
                    {
                        (runningSession, monitorTask) = await StartLaunchAsync(
                            connection,
                            pendingLaunchRequest,
                            pendingLaunch,
                            cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                if (request.Command.Equals("configurationDone", StringComparison.Ordinal))
                {
                    configurationDone = true;
                    await connection.WriteResponseAsync(
                        request,
                        success: true,
                        body: null,
                        message: null,
                        cancellationToken).ConfigureAwait(false);
                    if (pendingLaunchRequest is not null && pendingLaunch is not null)
                    {
                        (runningSession, monitorTask) = await StartLaunchAsync(
                            connection,
                            pendingLaunchRequest,
                            pendingLaunch,
                            cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                await connection.WriteResponseAsync(
                    request,
                    success: false,
                    body: null,
                    message: $"Unsupported request '{request.Command}'.",
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (runningSession is not null && !runningSession.Completion.IsCompleted)
            {
                await runningSession.TerminateAsync().ConfigureAwait(false);
            }

            if (monitorTask is not null)
            {
                await monitorTask.ConfigureAwait(false);
            }
        }
    }

    private DebugLaunchRequest ResolveLaunchRequest(DapRequest request)
    {
        if (request.Arguments.ValueKind != JsonValueKind.Object)
        {
            throw new DebugSetupException("The VBA launch request requires project, document, module, and procedure.");
        }

        if (request.Arguments.TryGetProperty("noDebug", out var noDebug) && noDebug.ValueKind == JsonValueKind.True)
        {
            throw new DebugSetupException("VBA launch does not support noDebug.");
        }

        var project = RequiredString(request.Arguments, "project");
        var document = RequiredString(request.Arguments, "document");
        var module = RequiredString(request.Arguments, "module");
        var procedure = RequiredString(request.Arguments, "procedure");
        var context = projectContextResolver.Resolve(new ProjectResolutionRequest(
            ProjectRoot: project,
            DocumentName: document,
            StartDirectory: getWorkingDirectory()));
        return new DebugLaunchRequest(
            context,
            new DebugTargetProcedure(module, procedure));
    }

    private async Task<(DebugRunningSession? Session, Task? Monitor)> StartLaunchAsync(
        DapConnection connection,
        DapRequest launchRequest,
        DebugLaunchRequest launch,
        CancellationToken cancellationToken)
    {
        try
        {
            var lifecycleSink = new DapDebugLifecycleSink(connection);
            var session = await launchCoordinator
                .LaunchAsync(launch, lifecycleSink, cancellationToken)
                .ConfigureAwait(false);
            await connection.WriteResponseAsync(
                launchRequest,
                success: true,
                body: null,
                message: null,
                cancellationToken).ConfigureAwait(false);
            var monitor = MonitorSessionAsync(connection, session, cancellationToken);
            if (session.Completion.IsCompleted)
            {
                await monitor.ConfigureAwait(false);
            }

            return (session, monitor);
        }
        catch (Exception ex) when (ex is DebugSetupException or DebugLaunchBusyException or ProjectManifestException)
        {
            await WriteLaunchFailureAsync(connection, launchRequest, ex.Message, cancellationToken)
                .ConfigureAwait(false);
            return (null, null);
        }
    }

    private static async Task MonitorSessionAsync(
        DapConnection connection,
        DebugRunningSession session,
        CancellationToken cancellationToken)
    {
        var exit = await session.Completion.ConfigureAwait(false);
        await connection.WriteEventAsync(
            "output",
            new
            {
                category = "console",
                output = $"Owned Excel process {session.ProcessId} exited with code {exit.ExitCode}.{Environment.NewLine}"
            },
            cancellationToken).ConfigureAwait(false);
        await connection.WriteEventAsync(
            "exited",
            new { exitCode = exit.ExitCode },
            cancellationToken).ConfigureAwait(false);
        await connection.WriteEventAsync(
            "terminated",
            body: null,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteLaunchFailureAsync(
        DapConnection connection,
        DapRequest request,
        string message,
        CancellationToken cancellationToken)
    {
        await connection.WriteEventAsync(
            "output",
            new
            {
                category = "important",
                output = $"DebugSetupError: {message}{Environment.NewLine}"
            },
            cancellationToken).ConfigureAwait(false);
        await connection.WriteResponseAsync(
            request,
            success: false,
            body: null,
            message: $"DebugSetupError: {message}",
            cancellationToken).ConfigureAwait(false);
        await connection.WriteEventAsync(
            "terminated",
            body: null,
            cancellationToken).ConfigureAwait(false);
    }

    private static string RequiredString(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new DebugSetupException($"The VBA launch request requires '{propertyName}'.");
        }

        return value.GetString()!;
    }

    private static bool HasEmptyBreakpointSet(DapRequest request)
    {
        if (request.Arguments.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return false;
        }

        if (request.Arguments.TryGetProperty("breakpoints", out var breakpoints) &&
            (breakpoints.ValueKind != System.Text.Json.JsonValueKind.Array || breakpoints.GetArrayLength() != 0))
        {
            return false;
        }

        return !request.Arguments.TryGetProperty("lines", out var lines) ||
            (lines.ValueKind == System.Text.Json.JsonValueKind.Array && lines.GetArrayLength() == 0);
    }

    private sealed class DapDebugLifecycleSink(DapConnection connection) : IDebugLifecycleSink
    {
        public ValueTask WriteAsync(
            DebugLifecycleMessage message,
            CancellationToken cancellationToken)
        {
            var output = message.Output.EndsWith('\n')
                ? message.Output
                : message.Output + Environment.NewLine;
            return new ValueTask(connection.WriteEventAsync(
                "output",
                new { category = "console", output },
                cancellationToken));
        }
    }
}
