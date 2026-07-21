using System.Collections.Immutable;
using VbaDev.App.Debugging;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using System.Text.Json;
using VbaLanguageServer.Syntax;

namespace VbaDev.Cli.Debugging;

/// <summary>
/// Hosts the minimal VBA Debug Adapter Protocol session over stdio-compatible streams.
/// </summary>
public sealed class VbaDebugAdapter
{
    private readonly ProjectContextResolver projectContextResolver;
    private readonly DebugLaunchCoordinator launchCoordinator;
    private readonly Func<string> getWorkingDirectory;
    private readonly DebugLaunchRequestResolver launchRequestResolver = new();

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
        RestartPreparationDescriptor? pendingLaunchRestartPreparation = null;
        DapRequest? pendingRestartRequest = null;
        DebugLaunchRequest? pendingRestartLaunch = null;
        RestartPreparationDescriptor? pendingRestartPreparation = null;
        var breakpointRegistry = new DapBreakpointRegistry();
        var configurationDone = false;
        DebugRunningSession? runningSession = null;
        Task? monitorTask = null;
        CancellationTokenSource? monitorEventCancellation = null;
        CancellationTokenSource? launchCancellation = null;
        Task<(
            DebugRunningSession? Session,
            Task? Monitor,
            CancellationTokenSource? MonitorEventCancellation,
            bool TerminatedEventSent)>? launchTask = null;
        DebugSessionLifecycle? sessionLifecycle = null;
        var launchTerminationEventSent = false;
        var stopHandled = false;

        async Task ExecuteRestartAsync(
            DapRequest restartRequest,
            DebugLaunchRequest requestedLaunch,
            bool preparedRestart)
        {
            var restartLaunch = RefreshRestartLaunchRequest(
                requestedLaunch,
                breakpointRegistry.ResolveRestartBreakpoints(
                    requestedLaunch.SourceSnapshot.Breakpoints));
            if (preparedRestart &&
                (sessionLifecycle is null || !sessionLifecycle.TryCommitRestartPreparation()))
            {
                throw new DebugSetupException(
                    "The owned Excel process exited while VBA debug restart preparation was pending.");
            }

            launchCancellation?.Cancel();
            if (launchTask is not null)
            {
                var previousLaunchTask = launchTask;
                launchTask = null;
                (runningSession, monitorTask, monitorEventCancellation, launchTerminationEventSent) =
                    await previousLaunchTask.ConfigureAwait(false);
            }

            monitorEventCancellation?.Cancel();
            if (runningSession is not null && !runningSession.Completion.IsCompleted)
            {
                await StopRunningSessionAsync(runningSession).ConfigureAwait(false);
            }

            if (monitorTask is not null)
            {
                await monitorTask.ConfigureAwait(false);
            }

            monitorEventCancellation?.Dispose();
            launchCancellation?.Dispose();
            runningSession = null;
            monitorTask = null;
            monitorEventCancellation = null;
            launchTerminationEventSent = false;
            pendingLaunch = restartLaunch;
            sessionLifecycle = new DebugSessionLifecycle();
            launchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            launchTask = StartLaunchAsync(
                connection,
                restartRequest,
                restartLaunch,
                breakpointRegistry.Breakpoints,
                breakpointRegistry.UnsupportedBreakpoints,
                cancellationToken,
                launchCancellation.Token,
                sessionLifecycle);
        }

        using var requestReadCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task<DapRequest?>? requestReadTask = null;
        try
        {
            while (true)
            {
                requestReadTask ??= connection.ReadRequestAsync(requestReadCancellation.Token);
                Task completedTask;
                if (launchTask is not null && monitorTask is not null)
                {
                    completedTask = await Task.WhenAny(
                        requestReadTask,
                        launchTask,
                        monitorTask).ConfigureAwait(false);
                }
                else if (launchTask is not null)
                {
                    completedTask = await Task.WhenAny(
                        requestReadTask,
                        launchTask).ConfigureAwait(false);
                }
                else if (monitorTask is not null)
                {
                    completedTask = await Task.WhenAny(
                        requestReadTask,
                        monitorTask).ConfigureAwait(false);
                }
                else
                {
                    completedTask = requestReadTask;
                }

                if (completedTask == launchTask)
                {
                    var completedLaunchTask = launchTask;
                    launchTask = null;
                    (runningSession, monitorTask, monitorEventCancellation, launchTerminationEventSent) =
                        await completedLaunchTask.ConfigureAwait(false);
                    continue;
                }

                if (completedTask == monitorTask)
                {
                    var completedMonitorTask = monitorTask;
                    monitorTask = null;
                    await completedMonitorTask.ConfigureAwait(false);
                    monitorEventCancellation?.Dispose();
                    monitorEventCancellation = null;
                    launchTerminationEventSent = true;
                    continue;
                }

                var request = await requestReadTask.ConfigureAwait(false);
                requestReadTask = null;
                if (request is null)
                {
                    break;
                }

                if (request.Command.Equals("initialize", StringComparison.Ordinal))
                {
                    await connection.WriteResponseAsync(
                        request,
                        success: true,
                        body: new
                        {
                            supportsConfigurationDoneRequest = true,
                            supportsConditionalBreakpoints = false,
                            supportsHitConditionalBreakpoints = false,
                            supportsLogPoints = false,
                            supportsFunctionBreakpoints = false,
                            supportsDataBreakpoints = false,
                            supportsTerminateRequest = true,
                            supportsRestartRequest = true,
                            exceptionBreakpointFilters = Array.Empty<object>()
                        },
                        message: null,
                        cancellationToken).ConfigureAwait(false);
                    await connection.WriteEventAsync(
                        "initialized",
                        body: null,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (request.Command.Equals("setBreakpoints", StringComparison.Ordinal))
                {
                    try
                    {
                        var update = ParseSourceBreakpointUpdate(request, breakpointRegistry);
                        await connection.WriteResponseAsync(
                            request,
                            success: true,
                            body: new { breakpoints = update.ResponseBreakpoints },
                            message: null,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (DebugSetupException ex)
                    {
                        await connection.WriteResponseAsync(
                            request,
                            success: false,
                            body: null,
                            message: $"DebugSetupError: {ex.Message}",
                            cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                if (request.Command.Equals("setFunctionBreakpoints", StringComparison.Ordinal) ||
                    request.Command.Equals("setExceptionBreakpoints", StringComparison.Ordinal) ||
                    request.Command.Equals("setDataBreakpoints", StringComparison.Ordinal))
                {
                    try
                    {
                        var update = ParseUnsupportedBreakpointUpdate(request, breakpointRegistry);
                        await connection.WriteResponseAsync(
                            request,
                            success: !update.IsUnsupported,
                            body: new { breakpoints = Array.Empty<object>() },
                            message: update.IsUnsupported
                                ? $"DebugSetupError: {update.Description}"
                                : null,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (DebugSetupException ex)
                    {
                        var policy = GetUnsupportedBreakpointPolicy(request.Command);
                        breakpointRegistry.TryLatchMalformed(
                            policy.Kind,
                            $"{policy.Description} Malformed request: {ex.Message}");
                        await connection.WriteResponseAsync(
                            request,
                            success: false,
                            body: null,
                            message: $"DebugSetupError: {ex.Message}",
                            cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                if (request.Command.Equals("dataBreakpointInfo", StringComparison.Ordinal))
                {
                    await connection.WriteResponseAsync(
                        request,
                        success: false,
                        body: null,
                        message: "DebugSetupError: VBA data breakpoints are unsupported.",
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
                    if (pendingLaunchRequest is not null ||
                        launchTask is not null ||
                        runningSession is not null ||
                        monitorTask is not null)
                    {
                        await connection.WriteResponseAsync(
                            request,
                            success: false,
                            body: null,
                            message: "DebugLaunchBusy: A VBA debug launch is already pending or active.",
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        var resolvedLaunch = ResolveLaunchRequest(request);
                        var resolvedRestartPreparation =
                            ParseRestartPreparation(request.Arguments);
                        pendingLaunch = resolvedLaunch;
                        pendingLaunchRestartPreparation = resolvedRestartPreparation;
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
                        var launchRequest = pendingLaunchRequest;
                        sessionLifecycle = new DebugSessionLifecycle();
                        launchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken);
                        launchTask = StartLaunchAsync(
                            connection,
                            launchRequest,
                            pendingLaunch,
                            breakpointRegistry.Breakpoints,
                            breakpointRegistry.UnsupportedBreakpoints,
                            cancellationToken,
                            launchCancellation.Token,
                            sessionLifecycle);
                        pendingLaunchRequest = null;
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
                    if (launchTask is null &&
                        pendingLaunchRequest is not null &&
                        pendingLaunch is not null)
                    {
                        var launchRequest = pendingLaunchRequest;
                        sessionLifecycle = new DebugSessionLifecycle();
                        launchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken);
                        launchTask = StartLaunchAsync(
                            connection,
                            launchRequest,
                            pendingLaunch,
                            breakpointRegistry.Breakpoints,
                            breakpointRegistry.UnsupportedBreakpoints,
                            cancellationToken,
                            launchCancellation.Token,
                            sessionLifecycle);
                        pendingLaunchRequest = null;
                    }

                    continue;
                }

                if (request.Command.Equals("restart", StringComparison.Ordinal))
                {
                    if (pendingRestartRequest is not null)
                    {
                        await connection.WriteResponseAsync(
                            request,
                            success: false,
                            body: null,
                            message: "DebugLaunchBusy: A VBA debug restart preparation is already pending.",
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    DebugLaunchRequest restartLaunch;
                    RestartPreparationDescriptor? restartPreparation;
                    try
                    {
                        restartLaunch = ResolveRestartLaunchRequest(request, pendingLaunch);
                        restartPreparation = ResolveRestartPreparation(
                            request,
                            pendingLaunchRestartPreparation);
                    }
                    catch (Exception ex) when (ex is DebugSetupException or ProjectManifestException)
                    {
                        await connection.WriteResponseAsync(
                            request,
                            success: false,
                            body: null,
                            message: $"DebugSetupError: {ex.Message}",
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (restartPreparation is not null)
                    {
                        if (sessionLifecycle is null ||
                            !sessionLifecycle.TryBeginRestartPreparation())
                        {
                            await connection.WriteResponseAsync(
                                request,
                                success: false,
                                body: null,
                                message: "The owned Excel process has already exited.",
                                cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        pendingRestartRequest = request;
                        pendingRestartLaunch = restartLaunch;
                        pendingRestartPreparation = restartPreparation;
                        continue;
                    }

                    try
                    {
                        await ExecuteRestartAsync(
                            request,
                            restartLaunch,
                            preparedRestart: false).ConfigureAwait(false);
                        pendingLaunchRestartPreparation = null;
                    }
                    catch (Exception ex) when (ex is DebugSetupException or ProjectManifestException)
                    {
                        await connection.WriteResponseAsync(
                            request,
                            success: false,
                            body: null,
                            message: $"DebugSetupError: {ex.Message}",
                            cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                if (request.Command.Equals("vba/restartPrepared", StringComparison.Ordinal))
                {
                    if (pendingRestartRequest is null ||
                        pendingRestartLaunch is null ||
                        pendingRestartPreparation is null)
                    {
                        await connection.WriteResponseAsync(
                            request,
                            success: false,
                            body: null,
                            message: "No VBA debug restart preparation is pending.",
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (request.Arguments.ValueKind != JsonValueKind.Object ||
                        !request.Arguments.TryGetProperty(
                            "restartRequestSequence",
                            out var restartRequestSequenceValue) ||
                        !restartRequestSequenceValue.TryGetInt32(out var restartRequestSequence) ||
                        restartRequestSequence != pendingRestartRequest.Sequence)
                    {
                        await connection.WriteResponseAsync(
                            request,
                            success: false,
                            body: null,
                            message: "The VBA debug restart preparation result is stale.",
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    JsonElement preparationSuccessValue = default;
                    string? preparationResultError;
                    if (!request.Arguments.TryGetProperty(
                            "preparationId",
                            out var preparationIdValue) ||
                        preparationIdValue.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(preparationIdValue.GetString()))
                    {
                        preparationResultError =
                            "The VBA debug restart preparation result requires 'preparationId'.";
                    }
                    else if (!preparationIdValue.GetString()!.Equals(
                        pendingRestartPreparation.Id,
                        StringComparison.Ordinal))
                    {
                        preparationResultError =
                            "The VBA debug restart preparation identity does not match the pending restart.";
                    }
                    else if (!request.Arguments.TryGetProperty(
                            "success",
                            out preparationSuccessValue) ||
                        preparationSuccessValue.ValueKind is not (
                            JsonValueKind.True or JsonValueKind.False))
                    {
                        preparationResultError =
                            "The VBA debug restart preparation result requires a Boolean 'success'.";
                    }
                    else
                    {
                        preparationResultError = null;
                    }
                    if (preparationResultError is not null)
                    {
                        var invalidRestartRequest = pendingRestartRequest;
                        pendingRestartRequest = null;
                        pendingRestartLaunch = null;
                        pendingRestartPreparation = null;
                        sessionLifecycle?.CancelRestartPreparation();
                        await connection.WriteResponseAsync(
                            request,
                            success: false,
                            body: null,
                            message: preparationResultError,
                            cancellationToken).ConfigureAwait(false);
                        await connection.WriteResponseAsync(
                            invalidRestartRequest,
                            success: false,
                            body: null,
                            message: preparationResultError,
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var preparedRestartRequest = pendingRestartRequest;
                    var preparedRestartLaunch = pendingRestartLaunch;
                    var preparedRestartPreparation = pendingRestartPreparation;
                    pendingRestartRequest = null;
                    pendingRestartLaunch = null;
                    pendingRestartPreparation = null;
                    await connection.WriteResponseAsync(
                        request,
                        success: true,
                        body: null,
                        message: null,
                        cancellationToken).ConfigureAwait(false);

                    if (!preparationSuccessValue.GetBoolean())
                    {
                        sessionLifecycle?.CancelRestartPreparation();
                        var preparationMessage = request.Arguments.TryGetProperty(
                            "message",
                            out var preparationMessageValue) &&
                            preparationMessageValue.ValueKind == JsonValueKind.String
                            ? preparationMessageValue.GetString()
                            : null;
                        await connection.WriteResponseAsync(
                            preparedRestartRequest,
                            success: false,
                            body: null,
                            message: string.IsNullOrWhiteSpace(preparationMessage)
                                ? "VBA debug restart preparation failed."
                                : preparationMessage,
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        await ExecuteRestartAsync(
                            preparedRestartRequest,
                            preparedRestartLaunch,
                            preparedRestart: true).ConfigureAwait(false);
                        pendingLaunchRestartPreparation = preparedRestartPreparation;
                    }
                    catch (Exception ex) when (ex is DebugSetupException or ProjectManifestException)
                    {
                        sessionLifecycle?.CancelRestartPreparation();
                        await connection.WriteResponseAsync(
                            preparedRestartRequest,
                            success: false,
                            body: null,
                            message: $"DebugSetupError: {ex.Message}",
                            cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                if (request.Command.Equals("disconnect", StringComparison.Ordinal) ||
                    request.Command.Equals("terminate", StringComparison.Ordinal))
                {
                    if (pendingRestartRequest is not null)
                    {
                        sessionLifecycle?.CancelRestartPreparation();
                        await connection.WriteResponseAsync(
                            pendingRestartRequest,
                            success: false,
                            body: null,
                            message: "VBA debug restart preparation was cancelled.",
                            cancellationToken).ConfigureAwait(false);
                        pendingRestartRequest = null;
                        pendingRestartLaunch = null;
                        pendingRestartPreparation = null;
                    }

                    launchCancellation?.Cancel();
                    await connection.WriteResponseAsync(
                        request,
                        success: true,
                        body: null,
                        message: null,
                        cancellationToken).ConfigureAwait(false);

                    if (launchTask is not null)
                    {
                        var stoppingLaunchTask = launchTask;
                        launchTask = null;
                        (runningSession, monitorTask, monitorEventCancellation, launchTerminationEventSent) =
                            await stoppingLaunchTask.ConfigureAwait(false);
                    }

                    if (runningSession is not null && !runningSession.Completion.IsCompleted)
                    {
                        await StopRunningSessionAsync(runningSession).ConfigureAwait(false);
                    }

                    if (monitorTask is not null)
                    {
                        await monitorTask.ConfigureAwait(false);
                    }
                    else if (!launchTerminationEventSent)
                    {
                        await connection.WriteEventAsync(
                            "terminated",
                            body: null,
                            cancellationToken).ConfigureAwait(false);
                    }

                    monitorEventCancellation?.Dispose();

                    stopHandled = true;
                    break;
                }

                await connection.WriteResponseAsync(
                    request,
                    success: false,
                    body: null,
                    message: $"Unsupported request '{request.Command}'.",
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            requestReadCancellation.Cancel();
            if (requestReadTask is { IsCompleted: false })
            {
                try
                {
                    await requestReadTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            throw;
        }
        finally
        {
            try
            {
                if (!stopHandled && launchTask is not null)
                {
                    if (!launchTask.IsCompleted)
                    {
                        launchCancellation?.Cancel();
                    }

                    (runningSession, monitorTask, monitorEventCancellation, launchTerminationEventSent) =
                        await launchTask.ConfigureAwait(false);
                }

                if (!stopHandled)
                {
                    if (runningSession is not null && !runningSession.Completion.IsCompleted)
                    {
                        await StopRunningSessionAsync(runningSession).ConfigureAwait(false);
                    }

                    if (monitorTask is not null)
                    {
                        await monitorTask.ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                monitorEventCancellation?.Dispose();
                launchCancellation?.Dispose();
            }
        }
    }

    private DebugLaunchRequest ResolveRestartLaunchRequest(
        DapRequest request,
        DebugLaunchRequest? previousLaunch)
    {
        if (request.Arguments.ValueKind == JsonValueKind.Object &&
            request.Arguments.TryGetProperty("arguments", out var latestArguments))
        {
            if (latestArguments.ValueKind != JsonValueKind.Object)
            {
                throw new DebugSetupException(
                    "The VBA restart request arguments must contain a launch configuration object.");
            }

            return ResolveLaunchRequest(new DapRequest(
                request.Sequence,
                "launch",
                latestArguments.Clone()));
        }

        return previousLaunch ?? throw new DebugSetupException(
            "The VBA restart request requires a previous launch or fresh launch arguments.");
    }

    private static RestartPreparationDescriptor? ResolveRestartPreparation(
        DapRequest request,
        RestartPreparationDescriptor? previousRestartPreparation)
    {
        if (request.Arguments.ValueKind == JsonValueKind.Object &&
            request.Arguments.TryGetProperty("arguments", out var latestArguments))
        {
            return ParseRestartPreparation(latestArguments);
        }

        return previousRestartPreparation;
    }

    private static RestartPreparationDescriptor? ParseRestartPreparation(
        JsonElement launchArguments)
    {
        if (!launchArguments.TryGetProperty("__vbaRestartPreparation", out var preparation))
        {
            return null;
        }

        if (preparation.ValueKind != JsonValueKind.Object)
        {
            throw new DebugSetupException(
                "The VBA launch request property '__vbaRestartPreparation' must be an object.");
        }

        ValidateExactObjectShape(
            preparation,
            "__vbaRestartPreparation",
            requiredProperties: ["protocolVersion", "id"],
            optionalProperties: []);
        var protocolVersion = RequiredInt32(
            preparation,
            "protocolVersion",
            "__vbaRestartPreparation.protocolVersion");
        if (protocolVersion != 1)
        {
            throw new DebugSetupException(
                $"Unsupported VBA debug restart preparation protocol version '{protocolVersion}'.");
        }

        return new RestartPreparationDescriptor(RequiredString(preparation, "id"));
    }

    private static async Task StopRunningSessionAsync(DebugRunningSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // The running-session disposal path still closes strong containment and
            // releases the launch guard before surfacing a termination failure.
        }
    }

    private DebugLaunchRequest ResolveLaunchRequest(DapRequest request)
    {
        if (request.Arguments.ValueKind != JsonValueKind.Object)
        {
            throw new DebugSetupException(
                "The VBA launch request requires project, document, and sourceSnapshot.");
        }

        RejectUnsupportedLaunchFields(request.Arguments);
        if (request.Arguments.TryGetProperty("noDebug", out var noDebug) && noDebug.ValueKind == JsonValueKind.True)
        {
            throw new DebugSetupException("VBA launch does not support noDebug.");
        }

        var project = RequiredString(request.Arguments, "project");
        var document = RequiredString(request.Arguments, "document");
        var module = OptionalString(request.Arguments, "module");
        var procedure = OptionalString(request.Arguments, "procedure");
        var sourceSnapshot = ParseSourceSnapshot(request.Arguments);
        var context = projectContextResolver.Resolve(new ProjectResolutionRequest(
            ProjectRoot: project,
            DocumentName: document,
            StartDirectory: getWorkingDirectory()));
        return launchRequestResolver.Resolve(
            context,
            sourceSnapshot,
            module,
            procedure);
    }

    private DebugLaunchRequest RefreshRestartLaunchRequest(
        DebugLaunchRequest requestedLaunch,
        ImmutableArray<DebugSourceBreakpoint> breakpoints)
    {
        ImmutableArray<DebugSourceFileSnapshot> sources;
        try
        {
            sources = DocumentSourceSetLayout
                .EnumerateVbaSourcePaths(requestedLaunch.Context.DocumentSourceSetPath)
                .Select(Path.GetFullPath)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => new DebugSourceFileSnapshot(
                    path,
                    VbaSourceFileTextReader.Decode(File.ReadAllBytes(path))))
                .ToImmutableArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DebugSetupException(
                $"The saved VBA source set could not be captured for restart: " +
                $"'{requestedLaunch.Context.DocumentSourceSetPath}'.",
                ex);
        }

        var sourceSnapshot = new DebugSourceSnapshot(
            DebugSourceSnapshot.CurrentSchemaVersion,
            sources,
            requestedLaunch.SourceSnapshot.ActiveSource)
        {
            Breakpoints = breakpoints
        };
        return launchRequestResolver.Resolve(
            requestedLaunch.Context,
            sourceSnapshot,
            requestedLaunch.Target.ModuleName,
            requestedLaunch.Target.ProcedureName);
    }

    private static void RejectUnsupportedLaunchFields(JsonElement arguments)
    {
        string[] unsupportedFields =
        [
            "args",
            "arguments",
            "noBuild",
            "stopOnEntry",
            "compilerConstants"
        ];
        foreach (var field in unsupportedFields)
        {
            if (arguments.TryGetProperty(field, out _))
            {
                throw new DebugSetupException(
                    $"VBA launch does not support '{field}'.");
            }
        }
    }

    private async Task<(
        DebugRunningSession? Session,
        Task? Monitor,
        CancellationTokenSource? MonitorEventCancellation,
        bool TerminatedEventSent)> StartLaunchAsync(
        DapConnection connection,
        DapRequest launchRequest,
        DebugLaunchRequest launch,
        IReadOnlyList<PendingDapBreakpoint> pendingBreakpoints,
        IReadOnlyList<UnsupportedDebugBreakpoint> unsupportedBreakpoints,
        CancellationToken transportCancellationToken,
        CancellationToken launchCancellationToken,
        DebugSessionLifecycle sessionLifecycle)
    {
        DebugRunningSession? acquiredSession = null;
        try
        {
            launch = ReconcileBreakpointPlan(launch, pendingBreakpoints, unsupportedBreakpoints);
            var lifecycleSink = new DapDebugLaunchEventSink(connection, pendingBreakpoints);
            acquiredSession = await launchCoordinator
                .LaunchAsync(launch, lifecycleSink, launchCancellationToken)
                .ConfigureAwait(false);
            sessionLifecycle.BindProcessCompletion(acquiredSession.ProcessCompletion);
            await connection.WriteResponseAsync(
                launchRequest,
                success: true,
                body: null,
                message: null,
                transportCancellationToken).ConfigureAwait(false);
            var monitorEvents = CancellationTokenSource.CreateLinkedTokenSource(
                transportCancellationToken);
            var monitor = MonitorSessionAsync(
                connection,
                acquiredSession,
                monitorEvents.Token,
                sessionLifecycle);
            if (acquiredSession.Completion.IsCompleted)
            {
                await monitor.ConfigureAwait(false);
            }

            var session = acquiredSession;
            acquiredSession = null;
            return (session, monitor, monitorEvents, false);
        }
        catch (OperationCanceledException) when (launchCancellationToken.IsCancellationRequested)
        {
            await connection.WriteEventAsync(
                "output",
                new
                {
                    category = "console",
                    output = $"VBA debug launch was cancelled.{Environment.NewLine}"
                },
                transportCancellationToken).ConfigureAwait(false);
            await connection.WriteResponseAsync(
                launchRequest,
                success: false,
                body: null,
                message: "VBA debug launch was cancelled.",
                transportCancellationToken).ConfigureAwait(false);
            return (null, null, null, false);
        }
        catch (Exception ex) when (ex is DebugSetupException or DebugLaunchBusyException or ProjectManifestException)
        {
            sessionLifecycle.TryMarkTerminated();
            await WriteLaunchFailureAsync(
                    connection,
                    launchRequest,
                    ex.Message,
                    transportCancellationToken)
                .ConfigureAwait(false);
            return (null, null, null, true);
        }
        finally
        {
            if (acquiredSession is not null)
            {
                await StopRunningSessionAsync(acquiredSession).ConfigureAwait(false);
            }
        }
    }

    private static async Task MonitorSessionAsync(
        DapConnection connection,
        DebugRunningSession session,
        CancellationToken cancellationToken,
        DebugSessionLifecycle sessionLifecycle)
    {
        var exit = await session.Completion.ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (!sessionLifecycle.TryMarkTerminated())
        {
            return;
        }

        try
        {
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Restart suppresses terminal events from the previous owned process.
        }
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

    private static string? OptionalString(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new DebugSetupException(
                $"The VBA launch request property '{propertyName}' must be a non-empty string when supplied.");
        }

        return value.GetString();
    }

    private static DebugSourceSnapshot ParseSourceSnapshot(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("sourceSnapshot", out var snapshot) ||
            snapshot.ValueKind != JsonValueKind.Object)
        {
            throw new DebugSetupException("The VBA launch request requires 'sourceSnapshot'.");
        }

        ValidateExactObjectShape(
            snapshot,
            "sourceSnapshot",
            requiredProperties: ["schemaVersion", "sources"],
            optionalProperties: ["activeSource", "breakpoints"]);

        if (!snapshot.TryGetProperty("schemaVersion", out var schemaVersionValue) ||
            schemaVersionValue.ValueKind != JsonValueKind.Number ||
            !schemaVersionValue.TryGetInt32(out var schemaVersion))
        {
            throw new DebugSetupException(
                "The VBA launch request requires integer 'sourceSnapshot.schemaVersion'.");
        }

        if (!snapshot.TryGetProperty("sources", out var sourcesValue) ||
            sourcesValue.ValueKind != JsonValueKind.Array)
        {
            throw new DebugSetupException(
                "The VBA launch request requires array 'sourceSnapshot.sources'.");
        }

        var sources = ImmutableArray.CreateBuilder<DebugSourceFileSnapshot>(sourcesValue.GetArrayLength());
        foreach (var sourceValue in sourcesValue.EnumerateArray())
        {
            if (sourceValue.ValueKind != JsonValueKind.Object)
            {
                throw new DebugSetupException(
                    "Each 'sourceSnapshot.sources' entry must be an object with path and text.");
            }

            ValidateExactObjectShape(
                sourceValue,
                "sourceSnapshot.sources[]",
                requiredProperties: ["path", "text"],
                optionalProperties: []);

            sources.Add(new DebugSourceFileSnapshot(
                RequiredString(sourceValue, "path"),
                RequiredSourceText(sourceValue)));
        }

        DebugSourcePosition? activeSource = null;
        if (snapshot.TryGetProperty("activeSource", out var activeSourceValue))
        {
            if (activeSourceValue.ValueKind != JsonValueKind.Object)
            {
                throw new DebugSetupException(
                    "The VBA launch request property 'sourceSnapshot.activeSource' must be an object when supplied.");
            }

            ValidateExactObjectShape(
                activeSourceValue,
                "sourceSnapshot.activeSource",
                requiredProperties: ["path", "line", "character"],
                optionalProperties: []);

            activeSource = new DebugSourcePosition(
                RequiredString(activeSourceValue, "path"),
                RequiredInt32(activeSourceValue, "line", "sourceSnapshot.activeSource.line"),
                RequiredInt32(activeSourceValue, "character", "sourceSnapshot.activeSource.character"));
        }

        var breakpoints = ImmutableArray<DebugSourceBreakpoint>.Empty;
        if (snapshot.TryGetProperty("breakpoints", out var breakpointsValue))
        {
            if (breakpointsValue.ValueKind != JsonValueKind.Array)
            {
                throw new DebugSetupException(
                    "The VBA launch request property 'sourceSnapshot.breakpoints' must be an array when supplied.");
            }

            var breakpointBuilder = ImmutableArray.CreateBuilder<DebugSourceBreakpoint>(
                breakpointsValue.GetArrayLength());
            foreach (var breakpointValue in breakpointsValue.EnumerateArray())
            {
                if (breakpointValue.ValueKind != JsonValueKind.Object)
                {
                    throw new DebugSetupException(
                        "Each 'sourceSnapshot.breakpoints' entry must be an object with path and line.");
                }

                ValidateExactObjectShape(
                    breakpointValue,
                    "sourceSnapshot.breakpoints[]",
                    requiredProperties: ["path", "line"],
                    optionalProperties: []);
                var path = RequiredString(breakpointValue, "path");
                var line = RequiredInt32(
                    breakpointValue,
                    "line",
                    "sourceSnapshot.breakpoints[].line");
                if (!Path.IsPathFullyQualified(path) ||
                    !path.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                {
                    throw new DebugSetupException(
                        $"Debug breakpoint source path must be absolute and canonical: '{path}'.");
                }

                if (line < 0)
                {
                    throw new DebugSetupException(
                        "Debug breakpoint editor line must be a non-negative zero-based line number.");
                }

                breakpointBuilder.Add(new DebugSourceBreakpoint(path, line));
            }

            breakpoints = breakpointBuilder.MoveToImmutable();
        }

        return new DebugSourceSnapshot(schemaVersion, sources.MoveToImmutable(), activeSource)
        {
            Breakpoints = breakpoints
        };
    }

    private static void ValidateExactObjectShape(
        JsonElement value,
        string displayName,
        IReadOnlyList<string> requiredProperties,
        IReadOnlyList<string> optionalProperties)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!requiredProperties.Contains(property.Name, StringComparer.Ordinal) &&
                !optionalProperties.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new DebugSetupException(
                    $"The VBA launch request does not support property '{displayName}.{property.Name}'.");
            }

            if (!seen.Add(property.Name))
            {
                throw new DebugSetupException(
                    $"The VBA launch request contains duplicate property '{displayName}.{property.Name}'.");
            }
        }

        foreach (var requiredProperty in requiredProperties)
        {
            if (!seen.Contains(requiredProperty))
            {
                throw new DebugSetupException(
                    $"The VBA launch request requires '{displayName}.{requiredProperty}'.");
            }
        }
    }

    private static string RequiredSourceText(JsonElement source)
    {
        if (!source.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new DebugSetupException(
                "Each 'sourceSnapshot.sources' entry requires string 'text'.");
        }

        return value.GetString()!;
    }

    private static int RequiredInt32(JsonElement value, string propertyName, string displayName)
    {
        if (!value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var result))
        {
            throw new DebugSetupException(
                $"The VBA launch request requires integer '{displayName}'.");
        }

        return result;
    }

    private static SourceBreakpointUpdate ParseSourceBreakpointUpdate(
        DapRequest request,
        DapBreakpointRegistry registry)
    {
        if (request.Arguments.ValueKind != JsonValueKind.Object)
        {
            throw new DebugSetupException(
                "The VBA setBreakpoints request requires source and breakpoints arguments.");
        }

        if (!request.Arguments.TryGetProperty("source", out var source) ||
            source.ValueKind != JsonValueKind.Object)
        {
            throw new DebugSetupException(
                "The VBA setBreakpoints request requires a source object.");
        }

        var sourcePath = RequiredString(source, "path");
        if (!Path.IsPathFullyQualified(sourcePath))
        {
            throw new DebugSetupException(
                $"VBA source breakpoint path must be absolute: '{sourcePath}'.");
        }

        sourcePath = Path.GetFullPath(sourcePath);
        if (!request.Arguments.TryGetProperty("breakpoints", out var breakpoints))
        {
            breakpoints = default;
        }

        if (breakpoints.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Array)
        {
            throw new DebugSetupException(
                "The VBA setBreakpoints request requires an array 'breakpoints' property.");
        }

        var captured = new List<DapSourceBreakpointIntent>();
        if (breakpoints.ValueKind == JsonValueKind.Array)
        {
            foreach (var breakpoint in breakpoints.EnumerateArray())
            {
                if (breakpoint.ValueKind != JsonValueKind.Object)
                {
                    throw new DebugSetupException(
                        "Each VBA source breakpoint must be an object with a one-based line.");
                }

                var dapLine = RequiredInt32(breakpoint, "line", "breakpoints[].line");
                if (dapLine <= 0)
                {
                    throw new DebugSetupException(
                        "VBA source breakpoint line must be a positive one-based line number.");
                }

                captured.Add(new DapSourceBreakpointIntent(
                    new DebugSourceBreakpoint(sourcePath, dapLine - 1),
                    OptionalRawValue(breakpoint, "condition"),
                    OptionalRawValue(breakpoint, "hitCondition"),
                    OptionalRawValue(breakpoint, "logMessage"),
                    OptionalRawValue(breakpoint, "column"),
                    OptionalRawValue(breakpoint, "mode")));
            }
        }

        var pending = registry.Replace(sourcePath, captured);
        var responseBreakpoints = pending
            .Select(item => (object)new
            {
                id = item.Id,
                verified = false,
                source = new { path = item.Intent.Breakpoint.SourcePath },
                line = item.Intent.Breakpoint.EditorLine + 1
            })
            .ToArray();
        return new SourceBreakpointUpdate(responseBreakpoints);
    }

    private static UnsupportedBreakpointUpdate ParseUnsupportedBreakpointUpdate(
        DapRequest request,
        DapBreakpointRegistry registry)
    {
        var policy = GetUnsupportedBreakpointPolicy(request.Command);
        if (request.Arguments.ValueKind != JsonValueKind.Object)
        {
            throw new DebugSetupException(
                $"The VBA {request.Command} request requires an arguments object.");
        }

        var isUnsupported = request.Command switch
        {
            "setFunctionBreakpoints" =>
                RequiredArrayLength(request.Arguments, "breakpoints", request.Command) != 0,
            "setExceptionBreakpoints" =>
                RequiredArrayLength(request.Arguments, "filters", request.Command) != 0 ||
                    OptionalArrayLength(request.Arguments, "filterOptions", request.Command) != 0 ||
                    OptionalArrayLength(request.Arguments, "exceptionOptions", request.Command) != 0,
            "setDataBreakpoints" =>
                RequiredArrayLength(request.Arguments, "breakpoints", request.Command) != 0,
            _ => throw new DebugSetupException(
                $"Unsupported breakpoint configuration request '{request.Command}'.")
        };
        registry.ReplaceUnsupported(policy.Kind, isUnsupported, policy.Description);
        return new UnsupportedBreakpointUpdate(isUnsupported, policy.Description);
    }

    private static UnsupportedBreakpointPolicy GetUnsupportedBreakpointPolicy(string command)
        => command switch
        {
            "setFunctionBreakpoints" => new UnsupportedBreakpointPolicy(
                UnsupportedDebugBreakpointKind.Function,
                "Function breakpoints are unsupported for VBA debug launch."),
            "setExceptionBreakpoints" => new UnsupportedBreakpointPolicy(
                UnsupportedDebugBreakpointKind.Exception,
                "Exception breakpoints are unsupported for VBA debug launch."),
            "setDataBreakpoints" => new UnsupportedBreakpointPolicy(
                UnsupportedDebugBreakpointKind.Data,
                "Data breakpoints are unsupported for VBA debug launch."),
            _ => throw new DebugSetupException(
                $"Unsupported breakpoint configuration request '{command}'.")
        };

    private static int RequiredArrayLength(
        JsonElement arguments,
        string propertyName,
        string command)
    {
        if (!arguments.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            throw new DebugSetupException(
                $"The VBA {command} request requires array '{propertyName}'.");
        }

        return value.GetArrayLength();
    }

    private static int OptionalArrayLength(
        JsonElement arguments,
        string propertyName,
        string command)
    {
        if (!arguments.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new DebugSetupException(
                $"The VBA {command} request property '{propertyName}' must be an array.");
        }

        return value.GetArrayLength();
    }

    private static DebugLaunchRequest ReconcileBreakpointPlan(
        DebugLaunchRequest launch,
        IReadOnlyList<PendingDapBreakpoint> pendingBreakpoints,
        IReadOnlyList<UnsupportedDebugBreakpoint> unsupportedBreakpoints)
    {
        var sourcePaths = launch.SourceSnapshot.Sources
            .Select(source => Path.GetFullPath(source.Path))
            .Where(IsVbaBreakpointSource)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var participating = launch.SourceSnapshot.Breakpoints
            .Where(breakpoint =>
                IsVbaBreakpointSource(breakpoint.SourcePath) &&
                sourcePaths.Contains(Path.GetFullPath(breakpoint.SourcePath)))
            .ToImmutableArray();
        var duplicate = participating
            .GroupBy(
                breakpoint => $"{Path.GetFullPath(breakpoint.SourcePath)}\0{breakpoint.EditorLine}",
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new DebugSetupException(
                "The post-save debug source snapshot contains a duplicate breakpoint position.");
        }

        var duplicatePending = pendingBreakpoints
            .Where(item => sourcePaths.Contains(Path.GetFullPath(item.Intent.Breakpoint.SourcePath)))
            .GroupBy(
                item => $"{Path.GetFullPath(item.Intent.Breakpoint.SourcePath)}\0{item.Intent.Breakpoint.EditorLine}",
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicatePending is not null)
        {
            throw new DebugSetupException(
                "The DAP breakpoint configuration contains a duplicate in-scope source position.");
        }

        var unsupported = ImmutableArray.CreateBuilder<UnsupportedDebugBreakpoint>();
        unsupported.AddRange(unsupportedBreakpoints);
        foreach (var pending in pendingBreakpoints.Where(item =>
            sourcePaths.Contains(Path.GetFullPath(item.Intent.Breakpoint.SourcePath))))
        {
            AddUnsupported(
                unsupported,
                pending,
                pending.Intent.Condition,
                UnsupportedDebugBreakpointKind.Conditional,
                "conditional breakpoint");
            AddUnsupported(
                unsupported,
                pending,
                pending.Intent.HitCondition,
                UnsupportedDebugBreakpointKind.HitCondition,
                "hit condition");
            AddUnsupported(
                unsupported,
                pending,
                pending.Intent.LogMessage,
                UnsupportedDebugBreakpointKind.Logpoint,
                "logpoint");
            AddUnsupported(
                unsupported,
                pending,
                pending.Intent.Column,
                UnsupportedDebugBreakpointKind.Column,
                "column breakpoint");
            AddUnsupported(
                unsupported,
                pending,
                pending.Intent.Mode,
                UnsupportedDebugBreakpointKind.Mode,
                "breakpoint mode");
        }

        return launch with
        {
            BreakpointPlan = new DebugBreakpointPlan(
                participating,
                unsupported.ToImmutable())
        };
    }

    private static bool IsVbaBreakpointSource(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        return extension.Equals(".bas", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cls", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".frm", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddUnsupported(
        ImmutableArray<UnsupportedDebugBreakpoint>.Builder unsupported,
        PendingDapBreakpoint pending,
        DapRawBreakpointValue value,
        UnsupportedDebugBreakpointKind kind,
        string featureName)
    {
        if (!value.IsPresent)
        {
            return;
        }

        unsupported.Add(new UnsupportedDebugBreakpoint(
            kind,
            $"Unsupported VBA {featureName} at " +
            $"'{pending.Intent.Breakpoint.SourcePath}:{pending.Intent.Breakpoint.EditorLine + 1}'."));
    }

    private static DapRawBreakpointValue OptionalRawValue(
        JsonElement breakpoint,
        string propertyName)
        => breakpoint.TryGetProperty(propertyName, out var value)
            ? new DapRawBreakpointValue(true, value.Clone())
            : default;

    private sealed class DebugSessionLifecycle
    {
        private readonly object gate = new();
        private DebugSessionLifecycleState state = DebugSessionLifecycleState.Active;
        private Task<DebugProcessExit>? processCompletion;
        private bool terminalEventsClaimed;

        public void BindProcessCompletion(Task<DebugProcessExit> completion)
        {
            lock (gate)
            {
                processCompletion = completion;
                if (completion.IsCompleted &&
                    state != DebugSessionLifecycleState.RestartCommitted)
                {
                    state = DebugSessionLifecycleState.Terminated;
                }
            }

            _ = completion.ContinueWith(
                static (_, lifecycleState) =>
                    ((DebugSessionLifecycle)lifecycleState!).ObserveProcessExit(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public bool TryBeginRestartPreparation()
        {
            lock (gate)
            {
                if (state != DebugSessionLifecycleState.Active)
                {
                    return false;
                }

                state = DebugSessionLifecycleState.RestartPreparing;
                return true;
            }
        }

        public bool TryCommitRestartPreparation()
        {
            lock (gate)
            {
                if (state != DebugSessionLifecycleState.RestartPreparing)
                {
                    return false;
                }

                if (processCompletion?.IsCompleted == true)
                {
                    state = DebugSessionLifecycleState.Terminated;
                    return false;
                }

                state = DebugSessionLifecycleState.RestartCommitted;
                return true;
            }
        }

        public void CancelRestartPreparation()
        {
            lock (gate)
            {
                if (state == DebugSessionLifecycleState.RestartPreparing)
                {
                    state = DebugSessionLifecycleState.Active;
                }
            }
        }

        public bool TryMarkTerminated()
        {
            lock (gate)
            {
                if (state == DebugSessionLifecycleState.RestartCommitted ||
                    terminalEventsClaimed)
                {
                    return false;
                }

                state = DebugSessionLifecycleState.Terminated;
                terminalEventsClaimed = true;
                return true;
            }
        }

        private void ObserveProcessExit()
        {
            lock (gate)
            {
                if (state != DebugSessionLifecycleState.RestartCommitted)
                {
                    state = DebugSessionLifecycleState.Terminated;
                }
            }
        }
    }

    private enum DebugSessionLifecycleState
    {
        Active,
        RestartPreparing,
        RestartCommitted,
        Terminated
    }

    private sealed class DapDebugLaunchEventSink(
        DapConnection connection,
        IReadOnlyList<PendingDapBreakpoint> pendingBreakpoints) : IDebugLaunchEventSink
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

        public ValueTask BreakpointVerifiedAsync(
            VbeBreakpoint breakpoint,
            CancellationToken cancellationToken)
        {
            var pendingBreakpoint = pendingBreakpoints.SingleOrDefault(item =>
                SameSourceBreakpoint(item.Intent.Breakpoint, breakpoint.Source));
            if (pendingBreakpoint is null)
            {
                return ValueTask.CompletedTask;
            }

            return new ValueTask(connection.WriteEventAsync(
                "breakpoint",
                new
                {
                    reason = "changed",
                    breakpoint = new
                    {
                        id = pendingBreakpoint.Id,
                        verified = true,
                        source = new { path = pendingBreakpoint.Intent.Breakpoint.SourcePath },
                        line = breakpoint.Source.EditorLine + 1
                    }
                },
                cancellationToken));
        }
    }

    private readonly record struct DapRawBreakpointValue(bool IsPresent, JsonElement Value);

    private sealed record DapSourceBreakpointIntent(
        DebugSourceBreakpoint Breakpoint,
        DapRawBreakpointValue Condition,
        DapRawBreakpointValue HitCondition,
        DapRawBreakpointValue LogMessage,
        DapRawBreakpointValue Column,
        DapRawBreakpointValue Mode);

    private sealed record PendingDapBreakpoint(int Id, DapSourceBreakpointIntent Intent);

    private sealed record RestartPreparationDescriptor(string Id);

    private sealed class DapBreakpointRegistry
    {
        private readonly Dictionary<string, List<PendingDapBreakpoint>> bySource =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<UnsupportedDebugBreakpointKind, UnsupportedDebugBreakpoint>
            unsupportedByKind = [];
        private int nextBreakpointId;
        private bool hasSourceBreakpointUpdates;

        public IReadOnlyList<PendingDapBreakpoint> Breakpoints => bySource.Values
            .SelectMany(items => items)
            .ToArray();

        public IReadOnlyList<UnsupportedDebugBreakpoint> UnsupportedBreakpoints =>
            unsupportedByKind
                .OrderBy(item => item.Key)
                .Select(item => item.Value)
                .ToArray();

        public IReadOnlyList<PendingDapBreakpoint> Replace(
            string sourcePath,
            IReadOnlyList<DapSourceBreakpointIntent> breakpoints)
        {
            hasSourceBreakpointUpdates = true;

            bySource.TryGetValue(sourcePath, out var previous);
            var replacement = breakpoints
                .Select(breakpoint =>
                {
                    var existing = previous?.FirstOrDefault(item =>
                        SameSourceBreakpoint(item.Intent.Breakpoint, breakpoint.Breakpoint));
                    return new PendingDapBreakpoint(
                        existing?.Id ?? ++nextBreakpointId,
                        breakpoint);
                })
                .ToList();
            if (replacement.Count == 0)
            {
                bySource.Remove(sourcePath);
            }
            else
            {
                bySource[sourcePath] = replacement;
            }

            return replacement;
        }

        public void ReplaceUnsupported(
            UnsupportedDebugBreakpointKind kind,
            bool isUnsupported,
            string description)
        {
            if (isUnsupported)
            {
                unsupportedByKind[kind] = new UnsupportedDebugBreakpoint(kind, description);
            }
            else
            {
                unsupportedByKind.Remove(kind);
            }
        }

        public void TryLatchMalformed(
            UnsupportedDebugBreakpointKind kind,
            string description)
        {
            unsupportedByKind[kind] = new UnsupportedDebugBreakpoint(kind, description);
        }

        public ImmutableArray<DebugSourceBreakpoint> ResolveRestartBreakpoints(
            ImmutableArray<DebugSourceBreakpoint> fallback)
            => hasSourceBreakpointUpdates
                ? Breakpoints
                    .Select(item => item.Intent.Breakpoint)
                    .ToImmutableArray()
                : fallback;
    }

    private static bool SameSourceBreakpoint(
        DebugSourceBreakpoint left,
        DebugSourceBreakpoint right)
        => left.EditorLine == right.EditorLine &&
            Path.GetFullPath(left.SourcePath).Equals(
                Path.GetFullPath(right.SourcePath),
                StringComparison.OrdinalIgnoreCase);

    private sealed record SourceBreakpointUpdate(
        object[] ResponseBreakpoints);

    private sealed record UnsupportedBreakpointUpdate(
        bool IsUnsupported,
        string Description);

    private sealed record UnsupportedBreakpointPolicy(
        UnsupportedDebugBreakpointKind Kind,
        string Description);
}
