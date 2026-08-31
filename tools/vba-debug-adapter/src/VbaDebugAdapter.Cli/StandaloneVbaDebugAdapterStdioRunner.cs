using System.Text.Json;
using VbaDebugAdapter.Build;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using VbaDebugAdapter.Protocol;

namespace VbaDebugAdapter.Cli;

public sealed class StandaloneVbaDebugAdapterStdioRunner : IVbaDebugAdapterStdioRunner
{
    private readonly IStandaloneVbaDebugLaunchService launchService;

    public StandaloneVbaDebugAdapterStdioRunner()
        : this(CreateDefaultLaunchService())
    {
    }

    private static IStandaloneVbaDebugLaunchService CreateDefaultLaunchService()
    {
        var validator = TransportedDebugSourceSnapshotValidator
            .CreateForCurrentWindowsSession();
        return new StandaloneVbaDebugLaunchService(
            validator,
            new VbaDevSnapshotWorkbookBuilder(
                new ProcessVbaDevBuildProcess(),
                validator),
            new VbeDebugAutomation(),
            new BreakpointSourceMapper(),
            new OpenXmlDebugCompilationSettingsReader(),
            new DebugCompilationEnvironmentFactory(),
            new DebugConditionalCompilationPreflight());
    }

    internal StandaloneVbaDebugAdapterStdioRunner(
        IStandaloneVbaDebugLaunchService launchService)
    {
        this.launchService = launchService
            ?? throw new ArgumentNullException(nameof(launchService));
    }

    public async Task<int> RunAsync(
        string vbaDevPath,
        IVbaDebugSessionWorkspaceLease workspaceLease,
        Stream standardInput,
        Stream standardOutput,
        Stream standardError,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vbaDevPath);
        ArgumentNullException.ThrowIfNull(workspaceLease);
        var sessionId = workspaceLease.SessionId;
        _ = standardError;
        var connection = new DapConnection(standardInput, standardOutput);
        DapRequest? pendingLaunchRequest = null;
        StandaloneVbaDebugLaunchRequest? pendingLaunch = null;
        StandaloneVbaDebugLaunchRequest? activeLaunch = null;
        PendingDebugRestartRequest? pendingRestart = null;
        IStandaloneVbaDebugRunningSession? runningSession = null;
        Task<StandaloneVbaDebugLaunchExecutionResult>? launchTask = null;
        CancellationTokenSource? launchCancellation = null;
        DebugRestartSwapAuthority? restartSwapAuthority = null;
        var breakpointRegistry = new DapSourceBreakpointRegistry();
        var configurationDone = false;
        var restartGeneration = DebugRestartGeneration.Initial;
        var lastRestartRequestSequence = -1;
        using var requestReadCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        Task<DapRequest?>? requestReadTask = null;
        try
        {
            while (true)
            {
                requestReadTask ??= connection.ReadRequestAsync(
                    requestReadCancellation.Token);
                Task completedTask;
                if (launchTask is not null)
                {
                    completedTask = await Task.WhenAny(
                        launchTask,
                        requestReadTask).ConfigureAwait(false);
                }
                else if (runningSession is not null)
                {
                    completedTask = await Task.WhenAny(
                        runningSession.Completion,
                        requestReadTask).ConfigureAwait(false);
                }
                else
                {
                    completedTask = requestReadTask;
                }

                if (ReferenceEquals(completedTask, launchTask))
                {
                    var completedLaunchTask = launchTask;
                    launchTask = null;
                    try
                    {
                        var launchResult = await completedLaunchTask.ConfigureAwait(false);
                        runningSession = launchResult.RunningSession;
                        activeLaunch = launchResult.ActiveLaunch;
                        if (runningSession is not null)
                        {
                            restartGeneration = DebugRestartGeneration.Max(
                                restartGeneration,
                                activeLaunch?.RestartPreparation?.Generation
                                    ?? DebugRestartGeneration.Initial);
                        }
                    }
                    catch (OperationCanceledException)
                        when (launchCancellation?.IsCancellationRequested == true)
                    {
                    }
                    finally
                    {
                        restartSwapAuthority?.Dispose();
                        restartSwapAuthority = null;
                        launchCancellation?.Dispose();
                        launchCancellation = null;
                    }
                    continue;
                }

                if (runningSession is not null &&
                    (ReferenceEquals(completedTask, runningSession.Completion) ||
                     runningSession.Completion.IsCompleted))
                {
                    if (pendingRestart is not null)
                    {
                        await connection.WriteResponseAsync(
                            pendingRestart.Request,
                            success: false,
                            body: null,
                            message: "The owned VBA debug session exited before restart preparation completed.",
                            cancellationToken).ConfigureAwait(false);
                        pendingRestart = null;
                    }
                    requestReadCancellation.Cancel();
                    ObserveDetachedRequestRead(requestReadTask);
                    requestReadTask = null;
                    var exitCode = await runningSession.Completion.ConfigureAwait(false);
                    var processId = runningSession.ProcessId;
                    await runningSession.DisposeAsync().ConfigureAwait(false);
                    runningSession = null;
                    await connection.WriteEventAsync(
                        "output",
                        new
                        {
                            category = "console",
                            output = $"Owned Excel process {processId} exited with code {exitCode}.{Environment.NewLine}"
                        },
                        cancellationToken).ConfigureAwait(false);
                    await connection.WriteEventAsync(
                        "exited",
                        new { exitCode },
                        cancellationToken).ConfigureAwait(false);
                    await connection.WriteEventAsync(
                        "terminated",
                        body: null,
                        cancellationToken).ConfigureAwait(false);
                    break;
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
                        var breakpoints = ParseDapSourceBreakpoints(
                            request.Arguments,
                            breakpointRegistry);
                        await connection.WriteResponseAsync(
                            request,
                            success: true,
                            body: new { breakpoints },
                            message: null,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (DebugSetupException exception)
                    {
                        await connection.WriteResponseAsync(
                            request,
                            success: false,
                            body: null,
                            message: $"DebugSetupError: {exception.Message}",
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
                        var unsupported = HasUnsupportedBreakpointConfiguration(
                            request.Command,
                            request.Arguments);
                        breakpointRegistry.ReplaceUnsupportedCategory(
                            request.Command,
                            unsupported);
                        await connection.WriteResponseAsync(
                            request,
                            success: !unsupported,
                            body: new { breakpoints = Array.Empty<object>() },
                            message: unsupported
                                ? $"DebugSetupError: VBA {UnsupportedBreakpointKind(request.Command)} breakpoints are unsupported."
                                : null,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (DebugSetupException exception)
                    {
                        await connection.WriteResponseAsync(
                            request,
                            success: false,
                            body: new { breakpoints = Array.Empty<object>() },
                            message: $"DebugSetupError: {exception.Message}",
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
                        runningSession is not null)
                    {
                        await connection.WriteResponseAsync(
                            request,
                            success: false,
                            body: null,
                            message: "DebugLaunchBusy: A VBA debug launch is already pending.",
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        pendingLaunch = ParseLaunchRequest(request.Arguments);
                        pendingLaunchRequest = request;
                    }
                    catch (DebugSetupException exception)
                    {
                        await connection.WriteResponseAsync(
                            request,
                            success: false,
                            body: null,
                            message: $"DebugSetupError: {exception.Message}",
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (configurationDone)
                    {
                        if (!await ValidateLaunchBreakpointsAsync(
                                connection,
                                pendingLaunchRequest,
                                pendingLaunch,
                                breakpointRegistry,
                                cancellationToken).ConfigureAwait(false))
                        {
                            pendingLaunchRequest = null;
                            pendingLaunch = null;
                            continue;
                        }
                        launchCancellation = CancellationTokenSource
                            .CreateLinkedTokenSource(cancellationToken);
                        launchTask = ExecuteLaunchAsync(
                            connection,
                            pendingLaunchRequest,
                            pendingLaunch,
                            restartSwapAuthority: null,
                            retainedLaunch: null,
                            vbaDevPath,
                            workspaceLease,
                            breakpointRegistry,
                            launchCancellation.Token,
                            cancellationToken);
                        pendingLaunchRequest = null;
                        pendingLaunch = null;
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
                        if (!await ValidateLaunchBreakpointsAsync(
                                connection,
                                pendingLaunchRequest,
                                pendingLaunch,
                                breakpointRegistry,
                                cancellationToken).ConfigureAwait(false))
                        {
                            pendingLaunchRequest = null;
                            pendingLaunch = null;
                            continue;
                        }
                        launchCancellation = CancellationTokenSource
                            .CreateLinkedTokenSource(cancellationToken);
                        launchTask = ExecuteLaunchAsync(
                            connection,
                            pendingLaunchRequest,
                            pendingLaunch,
                            restartSwapAuthority: null,
                            retainedLaunch: null,
                            vbaDevPath,
                            workspaceLease,
                            breakpointRegistry,
                            launchCancellation.Token,
                            cancellationToken);
                        pendingLaunchRequest = null;
                        pendingLaunch = null;
                    }
                    continue;
                }

                if (request.Command.Equals("restart", StringComparison.Ordinal))
                {
                    if (launchTask is not null || pendingRestart is not null)
                    {
                        await connection.WriteResponseAsync(
                            request,
                            success: false,
                            body: null,
                            message: "DebugLaunchBusy: A VBA debug restart preparation is already pending.",
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var restartPreparation = activeLaunch?.RestartPreparation;
                    if (runningSession is null || restartPreparation is null)
                    {
                        await connection.WriteResponseAsync(
                            request,
                            success: false,
                            body: null,
                            message: "DebugSetupError: The active VBA debug session is not bound for restart.",
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (request.Sequence <= lastRestartRequestSequence)
                    {
                        await connection.WriteResponseAsync(
                            request,
                            success: false,
                            body: null,
                            message: "DebugSetupError: VBA restart request sequences must increase monotonically.",
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (restartGeneration.Value == int.MaxValue)
                    {
                        await connection.WriteResponseAsync(
                            request,
                            success: false,
                            body: null,
                            message: "DebugSetupError: The VBA restart generation is exhausted.",
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    restartGeneration = restartGeneration.Next();
                    lastRestartRequestSequence = request.Sequence;
                    pendingRestart = new PendingDebugRestartRequest(
                        request,
                        restartPreparation,
                        restartGeneration);
                    continue;
                }

                if (request.Command.Equals("vba/restartPrepared", StringComparison.Ordinal))
                {
                    if (pendingRestart is null)
                    {
                        await connection.WriteResponseAsync(
                            request,
                            success: true,
                            body: null,
                            message: null,
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    RestartPreparationResult preparationResult;
                    try
                    {
                        preparationResult = ParseRestartPreparationResult(request.Arguments);
                    }
                    catch (DebugSetupException exception)
                    {
                        var invalidRestart = pendingRestart;
                        pendingRestart = null;
                        await connection.WriteResponseAsync(
                            request,
                            success: true,
                            body: null,
                            message: null,
                            cancellationToken).ConfigureAwait(false);
                        await connection.WriteResponseAsync(
                            invalidRestart.Request,
                            success: false,
                            body: null,
                            message: exception.Message,
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    string? identityError = preparationResult.SessionId != sessionId
                        ? "The VBA restart preparation session identity is stale."
                        : preparationResult.RestartRequestSequence != pendingRestart.Request.Sequence
                            ? "The VBA restart preparation request sequence is stale."
                            : preparationResult.PreparationId != pendingRestart.Descriptor.Id
                                ? "The VBA restart preparation identity is stale."
                                : preparationResult.Generation != pendingRestart.Generation
                                    ? "The VBA restart preparation generation is stale."
                                    : null;
                    if (identityError is not null)
                    {
                        var invalidRestart = pendingRestart;
                        pendingRestart = null;
                        await connection.WriteResponseAsync(
                            request,
                            success: true,
                            body: null,
                            message: null,
                            cancellationToken).ConfigureAwait(false);
                        await connection.WriteResponseAsync(
                            invalidRestart.Request,
                            success: false,
                            body: null,
                            message: identityError,
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var preparedRestart = pendingRestart;
                    pendingRestart = null;
                    if (!preparationResult.Success)
                    {
                        await connection.WriteResponseAsync(
                            request,
                            success: true,
                            body: null,
                            message: null,
                            cancellationToken).ConfigureAwait(false);
                        await connection.WriteResponseAsync(
                            preparedRestart.Request,
                            success: false,
                            body: null,
                            message: string.IsNullOrWhiteSpace(preparationResult.Message)
                                ? "VBA debug restart preparation failed."
                                : preparationResult.Message,
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    StandaloneVbaDebugLaunchRequest freshLaunch;
                    DebugRestartLaunchBinding restartBinding;
                    string? validationError = null;
                    try
                    {
                        if (preparationResult.Launch is null)
                        {
                            throw new DebugSetupException(
                                "A successful VBA restart preparation requires a fresh launch snapshot.");
                        }
                        freshLaunch = ParseLaunchRequest(preparationResult.Launch.Value);
                        if (activeLaunch is null || runningSession is null)
                        {
                            throw new DebugSetupException(
                                "The owned VBA debug session already exited.");
                        }
                        if (runningSession.Completion.IsCompleted)
                        {
                            throw new DebugSetupException(
                                "The owned VBA debug session exited before restart replacement committed.");
                        }
                        var requestedModuleName = freshLaunch.ModuleName;
                        var requestedProcedureName = freshLaunch.ProcedureName;
                        freshLaunch = freshLaunch with
                        {
                            ModuleName = runningSession.TargetModuleName,
                            ProcedureName = runningSession.TargetProcedureName
                        };
                        breakpointRegistry.ValidateForLaunch(freshLaunch.SourceSnapshot);
                        restartBinding = new DebugRestartLaunchBinding(
                            sessionId,
                            runningSession,
                            activeLaunch.ProjectRoot,
                            activeLaunch.DocumentName,
                            activeLaunch.WorkbookFileName,
                            runningSession.TargetModuleName,
                            runningSession.TargetProcedureName,
                            requestedModuleName,
                            requestedProcedureName,
                            preparedRestart.Descriptor.Id,
                            preparedRestart.Generation,
                            preparedRestart.Request.Sequence);
                    }
                    catch (Exception exception)
                    {
                        freshLaunch = null!;
                        restartBinding = null!;
                        validationError = exception.Message;
                    }

                    if (validationError is not null)
                    {
                        await connection.WriteResponseAsync(
                            request,
                            success: true,
                            body: null,
                            message: null,
                            cancellationToken).ConfigureAwait(false);
                        await connection.WriteResponseAsync(
                            preparedRestart.Request,
                            success: false,
                            body: null,
                            message: validationError,
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await connection.WriteResponseAsync(
                        request,
                        success: true,
                        body: null,
                        message: null,
                        cancellationToken).ConfigureAwait(false);
                    var retainedLaunch = activeLaunch!;
                    runningSession = null;
                    activeLaunch = null;
                    restartSwapAuthority = new DebugRestartSwapAuthority(restartBinding);
                    launchCancellation?.Dispose();
                    launchCancellation = CancellationTokenSource
                        .CreateLinkedTokenSource(cancellationToken);
                    launchTask = ExecuteLaunchAsync(
                        connection,
                        preparedRestart.Request,
                        freshLaunch,
                        restartSwapAuthority,
                        retainedLaunch,
                        vbaDevPath,
                        workspaceLease,
                        breakpointRegistry,
                        launchCancellation.Token,
                        cancellationToken);
                    continue;
                }

                if (request.Command.Equals("disconnect", StringComparison.Ordinal) ||
                    request.Command.Equals("terminate", StringComparison.Ordinal))
                {
                    if (pendingRestart is not null)
                    {
                        await connection.WriteResponseAsync(
                            pendingRestart.Request,
                            success: false,
                            body: null,
                            message: "VBA debug restart preparation was cancelled.",
                            cancellationToken).ConfigureAwait(false);
                        pendingRestart = null;
                    }
                    if (launchTask is not null)
                    {
                        restartSwapAuthority?.InvalidateForCancellation();
                        launchCancellation!.Cancel();
                        try
                        {
                            var launchResult = await launchTask.ConfigureAwait(false);
                            if (launchResult.RunningSession is not null)
                            {
                                await StopOwnedSessionAsync(
                                    launchResult.RunningSession).ConfigureAwait(false);
                            }
                        }
                        catch (OperationCanceledException)
                            when (launchCancellation.IsCancellationRequested)
                        {
                        }
                        launchTask = null;
                        restartSwapAuthority?.Dispose();
                        restartSwapAuthority = null;
                        launchCancellation.Dispose();
                        launchCancellation = null;
                    }
                    if (runningSession is not null)
                    {
                        await StopOwnedSessionAsync(runningSession).ConfigureAwait(false);
                        runningSession = null;
                    }
                    await connection.WriteResponseAsync(
                        request,
                        success: true,
                        body: null,
                        message: null,
                        cancellationToken).ConfigureAwait(false);
                    break;
                }

                await connection.WriteResponseAsync(
                    request,
                    success: false,
                    body: null,
                    message: $"Unsupported VBA debug request '{request.Command}'.",
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            requestReadCancellation.Cancel();
            if (requestReadTask is not null)
            {
                try
                {
                    _ = await requestReadTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (requestReadCancellation.IsCancellationRequested)
                {
                }
            }
            if (launchTask is not null)
            {
                restartSwapAuthority?.InvalidateForCancellation();
                launchCancellation!.Cancel();
                try
                {
                    var launchResult = await launchTask.ConfigureAwait(false);
                    if (launchResult.RunningSession is not null)
                    {
                        await StopOwnedSessionAsync(
                            launchResult.RunningSession).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                    when (launchCancellation.IsCancellationRequested)
                {
                }
            }
            restartSwapAuthority?.Dispose();
            launchCancellation?.Dispose();
            if (runningSession is not null)
            {
                await StopOwnedSessionAsync(runningSession).ConfigureAwait(false);
            }
        }

        return 0;
    }

    private static async Task<bool> ValidateLaunchBreakpointsAsync(
        DapConnection connection,
        DapRequest request,
        StandaloneVbaDebugLaunchRequest launchRequest,
        DapSourceBreakpointRegistry breakpointRegistry,
        CancellationToken cancellationToken)
    {
        try
        {
            breakpointRegistry.ValidateForLaunch(launchRequest.SourceSnapshot);
            return true;
        }
        catch (DebugSetupException exception)
        {
            await connection.WriteResponseAsync(
                request,
                success: false,
                body: null,
                message: $"DebugSetupError: {exception.Message}",
                cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    private async Task<StandaloneVbaDebugLaunchExecutionResult> ExecuteLaunchAsync(
        DapConnection connection,
        DapRequest dapRequest,
        StandaloneVbaDebugLaunchRequest launchRequest,
        DebugRestartSwapAuthority? restartSwapAuthority,
        StandaloneVbaDebugLaunchRequest? retainedLaunch,
        string vbaDevPath,
        IVbaDebugSessionWorkspaceLease workspaceLease,
        DapSourceBreakpointRegistry breakpointRegistry,
        CancellationToken launchCancellationToken,
        CancellationToken transportCancellationToken)
    {
        var restartBinding = restartSwapAuthority?.Binding;
        using var effectiveLaunchCancellation = restartSwapAuthority is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(
                launchCancellationToken,
                restartSwapAuthority.InvalidationToken);
        var effectiveLaunchCancellationToken =
            effectiveLaunchCancellation?.Token ?? launchCancellationToken;
        IPreparedDebugLaunchPlan? preparedPlan = null;
        IStandaloneVbaDebugRunningSession? runningSession = null;
        IStandaloneVbaDebugRunningSession? resultSession = null;
        try
        {
            try
            {
                preparedPlan = await launchService
                    .PrepareAsync(
                        vbaDevPath,
                        workspaceLease,
                        launchRequest,
                        restartBinding,
                        effectiveLaunchCancellationToken,
                        new DapDebugLifecycleSink(connection, transportCancellationToken))
                    .ConfigureAwait(false);
                var currentRestartBinding = restartSwapAuthority?.ClaimForSwap(
                    preparedPlan.Snapshot.RestartBinding,
                    effectiveLaunchCancellationToken);
                runningSession = await preparedPlan
                    .CommitAsync(
                        currentRestartBinding,
                        effectiveLaunchCancellationToken)
                    .ConfigureAwait(false);
                foreach (var breakpoint in runningSession.VerifiedBreakpoints)
                {
                    await connection.WriteEventAsync(
                        "breakpoint",
                        new
                        {
                            reason = "changed",
                            breakpoint = new
                            {
                                id = breakpointRegistry.GetOrAdd(
                                    new Uri(breakpoint.Source.SourceUri).LocalPath,
                                    breakpoint.Source.EditorLine + 1),
                                verified = true,
                                line = breakpoint.Source.EditorLine + 1,
                                source = new
                                {
                                    path = new Uri(breakpoint.Source.SourceUri).LocalPath
                                }
                            }
                        },
                        transportCancellationToken).ConfigureAwait(false);
                }
                await connection.WriteResponseAsync(
                    dapRequest,
                    success: true,
                    body: null,
                    message: null,
                    transportCancellationToken).ConfigureAwait(false);
                resultSession = runningSession;
                runningSession = null;
                return new StandaloneVbaDebugLaunchExecutionResult(
                    resultSession,
                    launchRequest with
                    {
                        ProjectRoot = preparedPlan.Snapshot.LaunchSettings.CanonicalProjectRoot,
                        DocumentName = preparedPlan.Snapshot.LaunchSettings.DocumentName,
                        WorkbookFileName = preparedPlan.Snapshot.LaunchSettings.WorkbookFileName,
                        ModuleName = resultSession.TargetModuleName,
                        ProcedureName = resultSession.TargetProcedureName,
                        RestartPreparation = preparedPlan.Snapshot.LaunchSettings.RestartPreparation
                    });
            }
            catch (OperationCanceledException)
                when (effectiveLaunchCancellationToken.IsCancellationRequested)
            {
                if (runningSession is not null)
                {
                    await StopOwnedSessionAsync(runningSession).ConfigureAwait(false);
                }
                resultSession = RetainedRestartSession(preparedPlan, restartBinding);
                var cancellationMessage = restartSwapAuthority?.SessionEnded == true
                    ? "The owned VBA debug session exited during restart build before replacement committed."
                    : "VBA debug launch was cancelled.";
                await connection.WriteEventAsync(
                    "output",
                    new
                    {
                        category = "console",
                        output = cancellationMessage + Environment.NewLine
                    },
                    transportCancellationToken).ConfigureAwait(false);
                await connection.WriteResponseAsync(
                    dapRequest,
                    success: false,
                    body: null,
                    message: cancellationMessage,
                    transportCancellationToken).ConfigureAwait(false);
                return new StandaloneVbaDebugLaunchExecutionResult(
                    resultSession,
                    resultSession is null ? null : retainedLaunch);
            }
            catch (Exception exception)
            {
                if (runningSession is not null)
                {
                    try
                    {
                        await StopOwnedSessionAsync(runningSession).ConfigureAwait(false);
                    }
                    catch (Exception cleanupException)
                    {
                        exception.Data["VbaDebugAdapter.SessionCleanup"] = cleanupException;
                    }
                }
                resultSession = RetainedRestartSession(preparedPlan, restartBinding);
                var failureMessage = $"DebugSetupError: {exception.Message}";
                await connection.WriteEventAsync(
                    "output",
                    new
                    {
                        category = "important",
                        output = failureMessage + Environment.NewLine
                    },
                    transportCancellationToken).ConfigureAwait(false);
                await connection.WriteResponseAsync(
                    dapRequest,
                    success: false,
                    body: null,
                    message: failureMessage,
                    transportCancellationToken).ConfigureAwait(false);
                if (resultSession is null)
                {
                    await connection.WriteEventAsync(
                        "terminated",
                        body: null,
                        transportCancellationToken).ConfigureAwait(false);
                }
                return new StandaloneVbaDebugLaunchExecutionResult(
                    resultSession,
                    resultSession is null ? null : retainedLaunch);
            }
            finally
            {
                if (preparedPlan is not null)
                {
                    await preparedPlan.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            if (resultSession is not null)
            {
                try
                {
                    await StopOwnedSessionAsync(resultSession).ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    exception.Data["VbaDebugAdapter.ResultSessionCleanup"] = cleanupException;
                }
            }
            throw;
        }
    }

    private static IStandaloneVbaDebugRunningSession? RetainedRestartSession(
        IPreparedDebugLaunchPlan? preparedPlan,
        DebugRestartLaunchBinding? restartBinding)
        => restartBinding is not null &&
           (preparedPlan is null || !preparedPlan.RestartSessionReleased)
            ? restartBinding.BoundSession
            : null;

    private static void ObserveDetachedRequestRead(Task<DapRequest?> requestReadTask)
    {
        _ = requestReadTask.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task StopOwnedSessionAsync(
        IStandaloneVbaDebugRunningSession runningSession)
    {
        try
        {
            await runningSession.TerminateAsync().ConfigureAwait(false);
        }
        finally
        {
            await runningSession.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static StandaloneVbaDebugLaunchRequest ParseLaunchRequest(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new DebugSetupException("The VBA launch request requires an object argument.");
        }

        RejectUnsupportedLaunchField(arguments, "args");
        RejectUnsupportedLaunchField(arguments, "noBuild");
        RejectUnsupportedLaunchField(arguments, "stopOnEntry");
        ValidateExactObjectShape(
            arguments,
            "launch",
            requiredProperties:
            [
                "project",
                "document",
                "__vbaDebugWorkbookFileName",
                "sourceSnapshot"
            ],
            optionalProperties:
            [
                "type",
                "request",
                "name",
                "module",
                "procedure",
                "noDebug",
                "__vbaRestartPreparation"
            ]);
        if (arguments.TryGetProperty("noDebug", out var noDebug))
        {
            if (noDebug.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                throw new DebugSetupException(
                    "The VBA launch noDebug property must be a boolean.");
            }
            if (noDebug.GetBoolean())
            {
                throw new DebugSetupException(
                    "The VBA launch request does not support noDebug mode.");
            }
        }

        var projectRoot = RequiredString(arguments, "project");
        string canonicalProjectRoot;
        try
        {
            if (!Path.IsPathFullyQualified(projectRoot))
            {
                throw new DebugSetupException(
                    "The VBA launch project must be an absolute path.");
            }
            canonicalProjectRoot = Path.GetFullPath(projectRoot);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DebugSetupException(
                "The VBA launch project must be a valid absolute path.");
        }
        var documentName = RequiredString(arguments, "document");
        var workbookFileName = RequiredString(arguments, "__vbaDebugWorkbookFileName");
        if (workbookFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(Path.GetFileName(workbookFileName), workbookFileName, StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(workbookFileName), ".xlsm", StringComparison.OrdinalIgnoreCase))
        {
            throw new DebugSetupException(
                "The VBA launch debug workbook name must be a path-free .xlsm file name.");
        }
        var moduleName = OptionalExactString(arguments, "module");
        var procedureName = OptionalExactString(arguments, "procedure");
        if ((moduleName is null) != (procedureName is null))
        {
            throw new DebugSetupException(
                "The VBA launch request must specify 'module' and 'procedure' together.");
        }
        if (!arguments.TryGetProperty("sourceSnapshot", out var sourceSnapshot) ||
            sourceSnapshot.ValueKind != JsonValueKind.Object ||
            !sourceSnapshot.TryGetProperty("schemaVersion", out var schemaVersion) ||
            !schemaVersion.TryGetInt32(out var schema) ||
            !sourceSnapshot.TryGetProperty("sources", out var sources) ||
            sources.ValueKind != JsonValueKind.Array)
        {
            throw new DebugSetupException(
                "The VBA launch request requires sourceSnapshot schemaVersion and sources.");
        }

        ValidateExactObjectShape(
            sourceSnapshot,
            "sourceSnapshot",
            requiredProperties: ["schemaVersion", "sources"],
            optionalProperties: ["activeSource", "breakpoints"]);

        var transportedSources = sources.EnumerateArray()
            .Select(ParseTransportedSource)
            .ToArray();
        var activeSource = sourceSnapshot.TryGetProperty("activeSource", out var activeSourceValue)
            ? ParseTransportedSourcePosition(activeSourceValue)
            : null;
        var breakpoints = sourceSnapshot.TryGetProperty("breakpoints", out var breakpointsValue)
            ? ParseTransportedBreakpoints(breakpointsValue)
            : [];
        var restartPreparation = arguments.TryGetProperty(
            "__vbaRestartPreparation",
            out var restartPreparationValue)
            ? ParseRestartPreparation(restartPreparationValue)
            : null;
        return new StandaloneVbaDebugLaunchRequest(
            canonicalProjectRoot,
            documentName,
            workbookFileName,
            moduleName,
            procedureName,
            new TransportedDebugSourceSnapshot(schema, transportedSources)
            {
                ActiveSource = activeSource,
                Breakpoints = breakpoints
            })
        {
            RestartPreparation = restartPreparation
        };
    }

    private static RestartPreparationDescriptor ParseRestartPreparation(JsonElement preparation)
    {
        if (preparation.ValueKind != JsonValueKind.Object)
        {
            throw new DebugSetupException(
                "The VBA launch restart preparation must be an object.");
        }

        ValidateExactObjectShape(
            preparation,
            "__vbaRestartPreparation",
            requiredProperties: ["protocolVersion", "id", "generation"],
            optionalProperties: []);
        var protocolVersion = RequiredInt32(preparation, "protocolVersion");
        if (protocolVersion != 1)
        {
            throw new DebugSetupException(
                $"Unsupported VBA restart preparation protocol version '{protocolVersion}'.");
        }

        var id = RequiredString(preparation, "id");
        if (!IsCanonicalHex32(id))
        {
            throw new DebugSetupException(
                "The VBA restart preparation ID must contain 32 lowercase hexadecimal characters.");
        }

        var generation = RequiredInt32(preparation, "generation");
        if (generation < 0)
        {
            throw new DebugSetupException(
                "The VBA restart preparation generation must be nonnegative.");
        }

        return new RestartPreparationDescriptor(
            DebugRestartPreparationId.Parse(id),
            DebugRestartGeneration.FromValue(generation));
    }

    private static RestartPreparationResult ParseRestartPreparationResult(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new DebugSetupException(
                "The VBA restart preparation result must be an object.");
        }

        ValidateExactObjectShape(
            arguments,
            "vba/restartPrepared",
            requiredProperties:
            [
                "sessionId",
                "restartRequestSequence",
                "preparationId",
                "generation",
                "success"
            ],
            optionalProperties: ["message", "launch"]);
        if (!arguments.TryGetProperty("success", out var successValue) ||
            successValue.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new DebugSetupException(
                "The VBA restart preparation result requires a Boolean 'success'.");
        }

        var sessionId = RequiredString(arguments, "sessionId");
        if (!DebugSessionId.TryParse(sessionId, out var parsedSessionId))
        {
            throw new DebugSetupException(
                "The VBA restart preparation result session ID must contain 32 lowercase hexadecimal characters.");
        }
        var preparationId = RequiredString(arguments, "preparationId");
        if (!IsCanonicalHex32(preparationId))
        {
            throw new DebugSetupException(
                "The VBA restart preparation result ID must contain 32 lowercase hexadecimal characters.");
        }
        var generation = RequiredInt32(arguments, "generation");
        if (generation < 0)
        {
            throw new DebugSetupException(
                "The VBA restart preparation result generation must be nonnegative.");
        }

        return new RestartPreparationResult(
            parsedSessionId!,
            RequiredInt32(arguments, "restartRequestSequence"),
            DebugRestartPreparationId.Parse(preparationId),
            DebugRestartGeneration.FromValue(generation),
            successValue.GetBoolean(),
            OptionalString(arguments, "message"),
            arguments.TryGetProperty("launch", out var launch)
                ? launch.Clone()
                : null);
    }

    private static bool IsCanonicalHex32(string value)
        => value.Length == 32 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static IReadOnlyList<object> ParseDapSourceBreakpoints(
        JsonElement arguments,
        DapSourceBreakpointRegistry breakpointRegistry)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("source", out var source) ||
            source.ValueKind != JsonValueKind.Object)
        {
            throw new DebugSetupException(
                "The setBreakpoints request requires a source object.");
        }
        var sourcePath = RequiredString(source, "path");
        try
        {
            if (!Path.IsPathFullyQualified(sourcePath))
            {
                throw new DebugSetupException(
                    "The setBreakpoints source path must be absolute.");
            }
            sourcePath = Path.GetFullPath(sourcePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DebugSetupException(
                "The setBreakpoints source path must be a valid absolute path.");
        }
        if (!arguments.TryGetProperty("breakpoints", out var breakpoints) ||
            breakpoints.ValueKind != JsonValueKind.Array)
        {
            throw new DebugSetupException(
                "The setBreakpoints request requires a breakpoints array.");
        }

        var requestedBreakpoints = breakpoints.EnumerateArray().Select(breakpoint =>
        {
            if (breakpoint.ValueKind != JsonValueKind.Object)
            {
                throw new DebugSetupException(
                    "Each source breakpoint must be an object.");
            }
            var line = RequiredInt32(breakpoint, "line");
            if (line <= 0)
            {
                throw new DebugSetupException(
                    "Each source breakpoint line must be a positive one-based line.");
            }
            return new DapSourceBreakpointIntent(
                line,
                HasNonNullProperty(breakpoint, "condition"),
                HasNonNullProperty(breakpoint, "hitCondition"),
                HasNonNullProperty(breakpoint, "logMessage"),
                HasNonNullProperty(breakpoint, "column"),
                HasNonNullProperty(breakpoint, "mode"));
        }).ToArray();
        return breakpointRegistry.Replace(sourcePath, requestedBreakpoints)
            .Select(breakpoint => (object)new
            {
                id = breakpoint.Id,
                verified = false,
                line = breakpoint.Intent.Line,
                source = new { path = sourcePath }
            }).ToArray();
    }

    private static bool HasNonNullProperty(JsonElement value, string propertyName)
        => value.TryGetProperty(propertyName, out var property) &&
           property.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    private sealed class DapSourceBreakpointRegistry
    {
        private readonly Dictionary<string, List<RegisteredDapSourceBreakpoint>> bySource =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> unsupportedCategories =
            new(StringComparer.Ordinal);
        private int nextId;

        public int GetOrAdd(string sourcePath, int line)
        {
            var canonicalSourcePath = Path.GetFullPath(sourcePath);
            if (bySource.TryGetValue(canonicalSourcePath, out var breakpoints))
            {
                var existing = breakpoints.FirstOrDefault(item => item.Intent.Line == line);
                if (existing is not null)
                {
                    return existing.Id;
                }
            }

            var registered = new RegisteredDapSourceBreakpoint(
                checked(++nextId),
                new DapSourceBreakpointIntent(
                    line,
                    HasCondition: false,
                    HasHitCondition: false,
                    HasLogMessage: false,
                    HasColumn: false,
                    HasMode: false));
            if (breakpoints is null)
            {
                breakpoints = [];
                bySource.Add(canonicalSourcePath, breakpoints);
            }
            breakpoints.Add(registered);
            return registered.Id;
        }

        public IReadOnlyList<RegisteredDapSourceBreakpoint> Replace(
            string sourcePath,
            IReadOnlyList<DapSourceBreakpointIntent> breakpoints)
        {
            var canonicalSourcePath = Path.GetFullPath(sourcePath);
            bySource.TryGetValue(canonicalSourcePath, out var previous);
            var replacement = breakpoints
                .Select(intent => new RegisteredDapSourceBreakpoint(
                    previous?.FirstOrDefault(item => item.Intent.Line == intent.Line)?.Id
                        ?? checked(++nextId),
                    intent))
                .ToList();
            if (replacement.Count == 0)
            {
                bySource.Remove(canonicalSourcePath);
            }
            else
            {
                bySource[canonicalSourcePath] = replacement;
            }
            return replacement;
        }

        public void ValidateForLaunch(TransportedDebugSourceSnapshot snapshot)
        {
            if (unsupportedCategories.FirstOrDefault() is { } unsupportedCategory)
            {
                throw new DebugSetupException(
                    $"VBA {UnsupportedBreakpointKind(unsupportedCategory)} breakpoints are unsupported.");
            }
            var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in snapshot.Sources.Where(source => source.SourceUri is not null))
            {
                if (!Uri.TryCreate(source.SourceUri, UriKind.Absolute, out var sourceUri) ||
                    !sourceUri.IsFile)
                {
                    throw new DebugSetupException(
                        $"Debug source snapshot path '{source.RelativePath}' requires a persistent file URI.");
                }
                try
                {
                    sourcePaths.Add(Path.GetFullPath(sourceUri.LocalPath));
                }
                catch (Exception exception) when (
                    exception is ArgumentException or InvalidOperationException or
                        NotSupportedException or PathTooLongException or UriFormatException)
                {
                    throw new DebugSetupException(
                        $"Debug source snapshot path '{source.RelativePath}' requires a persistent file URI.");
                }
            }
            foreach (var (sourcePath, breakpoints) in bySource.Where(item =>
                         sourcePaths.Contains(item.Key)))
            {
                if (breakpoints.GroupBy(item => item.Intent.Line).Any(group => group.Count() > 1))
                {
                    throw new DebugSetupException(
                        $"The DAP breakpoint configuration contains a duplicate in-scope position in '{sourcePath}'.");
                }
                foreach (var breakpoint in breakpoints)
                {
                    var unsupported = breakpoint.Intent.UnsupportedFeature;
                    if (unsupported is not null)
                    {
                        throw new DebugSetupException(
                            $"Unsupported VBA {unsupported} at '{sourcePath}:{breakpoint.Intent.Line}'.");
                    }
                }
            }
        }

        public void ReplaceUnsupportedCategory(string command, bool configured)
        {
            if (configured)
            {
                unsupportedCategories.Add(command);
            }
            else
            {
                unsupportedCategories.Remove(command);
            }
        }
    }

    private sealed record RegisteredDapSourceBreakpoint(
        int Id,
        DapSourceBreakpointIntent Intent);

    private sealed record DapSourceBreakpointIntent(
        int Line,
        bool HasCondition,
        bool HasHitCondition,
        bool HasLogMessage,
        bool HasColumn,
        bool HasMode)
    {
        public string? UnsupportedFeature =>
            HasCondition ? "conditional breakpoint" :
            HasHitCondition ? "hit-count breakpoint" :
            HasLogMessage ? "log point" :
            HasColumn ? "column breakpoint" :
            HasMode ? "breakpoint mode" :
            null;
    }

    private static void RejectUnsupportedLaunchField(
        JsonElement arguments,
        string propertyName)
    {
        if (arguments.TryGetProperty(propertyName, out _))
        {
            throw new DebugSetupException(
                $"VBA launch does not support '{propertyName}'.");
        }
    }

    private static bool HasUnsupportedBreakpointConfiguration(
        string command,
        JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new DebugSetupException(
                $"The {command} request requires an object argument.");
        }

        var propertyNames = command.Equals("setExceptionBreakpoints", StringComparison.Ordinal)
            ? new[] { "filters", "filterOptions", "exceptionOptions" }
            : new[] { "breakpoints" };
        foreach (var propertyName in propertyNames)
        {
            if (!arguments.TryGetProperty(propertyName, out var values))
            {
                continue;
            }
            if (values.ValueKind != JsonValueKind.Array)
            {
                throw new DebugSetupException(
                    $"The {command} request property '{propertyName}' must be an array.");
            }
            if (values.GetArrayLength() > 0)
            {
                return true;
            }
        }
        return false;
    }

    private static string UnsupportedBreakpointKind(string command)
        => command switch
        {
            "setFunctionBreakpoints" => "function",
            "setExceptionBreakpoints" => "exception",
            "setDataBreakpoints" => "data",
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };

    private static TransportedDebugSource ParseTransportedSource(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object)
        {
            throw new DebugSetupException(
                "Each sourceSnapshot source must be an object.");
        }
        ValidateExactObjectShape(
            source,
            "sourceSnapshot.sources[]",
            requiredProperties: ["relativePath", "contentBase64"],
            optionalProperties: ["sourceUri", "encoding"]);
        var relativePath = RequiredString(source, "relativePath");
        var contentBase64 = RequiredStringAllowEmpty(source, "contentBase64");
        return new TransportedDebugSource(
            relativePath,
            OptionalString(source, "sourceUri"),
            OptionalString(source, "encoding"),
            contentBase64);
    }

    private static TransportedDebugSourcePosition ParseTransportedSourcePosition(
        JsonElement position)
    {
        if (position.ValueKind != JsonValueKind.Object)
        {
            throw new DebugSetupException(
                "The transported active source must be an object.");
        }
        ValidateExactObjectShape(
            position,
            "sourceSnapshot.activeSource",
            requiredProperties: ["sourceUri", "line", "character"],
            optionalProperties: []);
        return new TransportedDebugSourcePosition(
            RequiredString(position, "sourceUri"),
            RequiredInt32(position, "line"),
            RequiredInt32(position, "character"));
    }

    private static IReadOnlyList<TransportedDebugSourceBreakpoint> ParseTransportedBreakpoints(
        JsonElement breakpoints)
    {
        if (breakpoints.ValueKind != JsonValueKind.Array)
        {
            throw new DebugSetupException(
                "The transported source breakpoints must be an array.");
        }
        return breakpoints.EnumerateArray().Select(breakpoint =>
        {
            if (breakpoint.ValueKind != JsonValueKind.Object)
            {
                throw new DebugSetupException(
                    "Each transported source breakpoint must be an object.");
            }
            ValidateExactObjectShape(
                breakpoint,
                "sourceSnapshot.breakpoints[]",
                requiredProperties: ["sourceUri", "line"],
                optionalProperties: []);
            return new TransportedDebugSourceBreakpoint(
                RequiredString(breakpoint, "sourceUri"),
                RequiredInt32(breakpoint, "line"));
        }).ToArray();
    }

    private static string RequiredString(JsonElement value, string propertyName)
        => OptionalString(value, propertyName)
           ?? throw new DebugSetupException(
               $"The VBA launch request requires string '{propertyName}'.");

    private static string RequiredStringAllowEmpty(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            throw new DebugSetupException(
                $"The VBA launch request requires string '{propertyName}'.");
        }
        return property.GetString()!;
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

    private static int RequiredInt32(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) ||
            !property.TryGetInt32(out var result))
        {
            throw new DebugSetupException(
                $"The VBA launch request requires integer '{propertyName}'.");
        }
        return result;
    }

    private static string? OptionalString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }
        if (property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new DebugSetupException(
                $"The VBA launch request property '{propertyName}' must be a non-empty string.");
        }
        return property.GetString();
    }

    private static string? OptionalExactString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }
        if (property.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(property.GetString()))
        {
            throw new DebugSetupException(
                $"The VBA launch request property '{propertyName}' must be a non-empty string.");
        }
        return property.GetString();
    }

    private sealed class DapDebugLifecycleSink(
        DapConnection connection,
        CancellationToken transportCancellationToken) : IDebugLifecycleSink
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
                transportCancellationToken));
        }
    }

}

internal sealed class DebugRestartSwapAuthority : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource invalidation = new();
    private RestartSwapState state;

    public DebugRestartSwapAuthority(DebugRestartLaunchBinding binding)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        var weakAuthority = new WeakReference<DebugRestartSwapAuthority>(this);
        _ = binding.BoundSession.Completion.ContinueWith(
            static (_, state) =>
            {
                var weakAuthority =
                    (WeakReference<DebugRestartSwapAuthority>)state!;
                if (weakAuthority.TryGetTarget(out var authority))
                {
                    authority.Invalidate(RestartSwapState.SessionEnded);
                }
            },
            weakAuthority,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public DebugRestartLaunchBinding Binding { get; }

    public CancellationToken InvalidationToken => invalidation.Token;

    public bool SessionEnded
    {
        get
        {
            lock (gate)
            {
                return state == RestartSwapState.SessionEnded;
            }
        }
    }

    public void InvalidateForCancellation() =>
        Invalidate(RestartSwapState.Cancelled);

    public DebugRestartLaunchBinding ClaimForSwap(
        DebugRestartLaunchBinding? preparedBinding,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (state == RestartSwapState.SessionEnded)
            {
                throw new DebugSetupException(
                    "The owned VBA debug session exited during restart build before replacement committed.");
            }
            if (state is RestartSwapState.Cancelled or RestartSwapState.Disposed)
            {
                throw new OperationCanceledException(
                    "The VBA debug restart swap was cancelled.",
                    cancellationToken);
            }
            if (state == RestartSwapState.Claimed)
            {
                throw new DebugSetupException(
                    "The prepared VBA restart launch binding is stale.");
            }
            if (cancellationToken.IsCancellationRequested)
            {
                SetInvalidated(RestartSwapState.Cancelled);
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (preparedBinding is null ||
                !Binding.HasSameIdentityAs(preparedBinding))
            {
                throw new DebugSetupException(
                    "The prepared VBA restart launch binding is stale.");
            }
            if (!Binding.IsBoundSessionCurrent)
            {
                SetInvalidated(RestartSwapState.SessionEnded);
                throw new DebugSetupException(
                    "The owned VBA debug session exited during restart build before replacement committed.");
            }

            state = RestartSwapState.Claimed;
            return Binding with { };
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (state == RestartSwapState.Pending)
            {
                state = RestartSwapState.Disposed;
            }
            invalidation.Dispose();
        }
    }

    private void Invalidate(RestartSwapState invalidatedState)
    {
        lock (gate)
        {
            if (state == RestartSwapState.Pending)
            {
                SetInvalidated(invalidatedState);
            }
        }
    }

    private void SetInvalidated(RestartSwapState invalidatedState)
    {
        state = invalidatedState;
        invalidation.Cancel();
    }

    private enum RestartSwapState
    {
        Pending,
        Claimed,
        SessionEnded,
        Cancelled,
        Disposed
    }
}

internal sealed record StandaloneVbaDebugLaunchExecutionResult(
    IStandaloneVbaDebugRunningSession? RunningSession,
    StandaloneVbaDebugLaunchRequest? ActiveLaunch);

public sealed record StandaloneVbaDebugLaunchRequest(
    string ProjectRoot,
    string DocumentName,
    string WorkbookFileName,
    string? ModuleName,
    string? ProcedureName,
    TransportedDebugSourceSnapshot SourceSnapshot)
{
    public RestartPreparationDescriptor? RestartPreparation { get; init; }
}

public sealed record RestartPreparationDescriptor(
    DebugRestartPreparationId Id,
    DebugRestartGeneration Generation);

internal sealed record PendingDebugRestartRequest(
    DapRequest Request,
    RestartPreparationDescriptor Descriptor,
    DebugRestartGeneration Generation);

internal sealed record RestartPreparationResult(
    DebugSessionId SessionId,
    int RestartRequestSequence,
    DebugRestartPreparationId PreparationId,
    DebugRestartGeneration Generation,
    bool Success,
    string? Message,
    JsonElement? Launch);

internal interface IStandaloneVbaDebugLaunchService
{
    Task<IPreparedDebugLaunchPlan> PrepareAsync(
        string vbaDevPath,
        IVbaDebugSessionWorkspaceLease workspaceLease,
        StandaloneVbaDebugLaunchRequest request,
        DebugRestartLaunchBinding? restartBinding,
        CancellationToken cancellationToken,
        IDebugLifecycleSink? lifecycleSink = null);
}

public interface IStandaloneVbaDebugRunningSession : IAsyncDisposable
{
    Task<int> Completion { get; }

    int ProcessId { get; }

    string TargetModuleName { get; }

    string TargetProcedureName { get; }

    IReadOnlyList<VbeBreakpoint> VerifiedBreakpoints { get; }

    ValueTask TerminateAsync();
}
