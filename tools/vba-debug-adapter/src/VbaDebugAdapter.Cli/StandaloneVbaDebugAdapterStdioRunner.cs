using System.Text.Json;
using VbaDebugAdapter.Build;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using VbaDebugAdapter.Protocol;
using VbaTools.SourceIdentities;

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
        return new StandaloneVbaDebugLaunchService(
            DebugSourceAdmission.CreateForCurrentWindowsSession(),
            new VbaDevSnapshotWorkbookBuilder(new ProcessVbaDevBuildProcess()),
            new VbeDebugAutomation(),
            new OpenXmlDebugCompilationSettingsReader(),
            new DebugCompilationEnvironmentFactory());
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
        using var restartPreparation = new DebugRestartPreparation(sessionId);
        IStandaloneVbaDebugRunningSession? runningSession = null;
        IStandaloneVbaDebugRunningSession? endedSession = null;
        DebugFailureOutcome? terminalLaunchFailure = null;
        Task<StandaloneVbaDebugLaunchExecutionResult>? launchTask = null;
        CancellationTokenSource? launchCancellation = null;
        var breakpointRegistry = new DapSourceBreakpointRegistry();
        var configurationDone = false;
        using var requestReadCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        Task<DapRequest?>? requestReadTask = null;
        Exception? runFailure = null;
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
                else if (endedSession is not null)
                {
                    completedTask = endedSession.Completion;
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
                        endedSession = launchResult.EndedSession;
                        terminalLaunchFailure = launchResult.TerminalFailure;
                        activeLaunch = launchResult.ActiveLaunch;
                        if (terminalLaunchFailure is not null && endedSession is null)
                        {
                            requestReadCancellation.Cancel();
                            ObserveDetachedRequestRead(requestReadTask);
                            requestReadTask = null;
                            return 1;
                        }
                    }
                    catch (OperationCanceledException)
                        when (launchCancellation?.IsCancellationRequested == true)
                    {
                    }
                    finally
                    {
                        restartPreparation.CompleteLaunch(activeLaunch);
                        launchCancellation?.Dispose();
                        launchCancellation = null;
                    }
                    continue;
                }

                if (endedSession is not null || (runningSession is not null &&
                    (ReferenceEquals(completedTask, runningSession.Completion) ||
                     runningSession.Completion.IsCompleted)))
                {
                    var completedSession = endedSession ?? runningSession!;
                    endedSession = null;
                    runningSession = null;
                    var processId = completedSession.ProcessId;
                    int? exitCode = null;
                    Exception? failure = null;
                    try
                    {
                        exitCode = await completedSession.Completion.ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                    var terminalCompletion = new DebugFailureCompletion(terminalLaunchFailure is null
                        ? failure : new DebugFailureException(terminalLaunchFailure));
                    if (terminalLaunchFailure is not null && failure is not null)
                    {
                        terminalCompletion.AddFailure("session completion", "owned Excel session",
                            DebugResourceKind.Observation, failure, processId);
                    }
                    terminalLaunchFailure = null;
                    if (failure is not null)
                    {
                        await CompleteSessionCleanupAsync(terminalCompletion, completedSession, "owned Excel session")
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        try { await completedSession.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception exception)
                        {
                            terminalCompletion.AddFailure("session disposal", "owned Excel session", DebugResourceKind.Handle,
                                exception, processId);
                        }
                        MergeOwnerEvidence(terminalCompletion, completedSession, "session disposal", "owned Excel session");
                    }
                    var terminalOutcome = terminalCompletion.Complete();
                    failure = terminalOutcome.PrimaryFailure is not null || terminalOutcome.HasCleanupFailure
                        ? new DebugFailureException(terminalOutcome) : null;
                    requestReadCancellation.Cancel();
                    ObserveDetachedRequestRead(requestReadTask);
                    requestReadTask = null;
                    if (connection.OutputFailed)
                    {
                        return 1;
                    }
                    try
                    {
                        if (restartPreparation.TakePending() is { } pendingRestart)
                        {
                            await connection.WriteResponseAsync(pendingRestart.Request, false, null,
                                failure is null
                                    ? "The owned VBA debug session exited before restart preparation completed."
                                    : "The owned VBA debug session failed before restart preparation completed.",
                                cancellationToken).ConfigureAwait(false);
                        }
                        await connection.WriteEventAsync("output", new
                        {
                            category = failure is null ? "console" : "important",
                            output = failure is null
                                ? $"Owned Excel process {processId} exited with code {exitCode}.{Environment.NewLine}"
                                : $"DebugSessionError: {failure.Message}{Environment.NewLine}"
                        }, cancellationToken).ConfigureAwait(false);
                        if (exitCode is not null)
                        {
                            await connection.WriteEventAsync("exited", new { exitCode }, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        await connection.WriteEventAsync("terminated", null, cancellationToken).ConfigureAwait(false);
                        return failure is null ? 0 : 1;
                    }
                    catch (Exception exception)
                    {
                        var notificationCompletion = new DebugFailureCompletion(failure ?? exception);
                        if (failure is not null)
                        {
                            notificationCompletion.AddFailure("DAP output", "session terminal notification",
                                DebugResourceKind.Observation, exception, processId);
                        }
                        notificationCompletion.Complete().ThrowWithEvidence();
                        throw;
                    }
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
                    AdmittedDapSourceBreakpoints admitted;
                    try
                    {
                        admitted = DebugRequestAdmission.AdmitSourceBreakpoints(request.Arguments);
                    }
                    catch (DebugRequestRejectedException exception)
                    {
                        await connection.WriteResponseAsync(
                            request,
                            success: false,
                            body: null,
                            message: $"DebugSetupError: {exception.Message}",
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    var breakpoints = breakpointRegistry.Replace(admitted.Identity, admitted.Breakpoints)
                        .Select(breakpoint => new
                        {
                            id = breakpoint.Id,
                            verified = false,
                            line = breakpoint.Intent.Line,
                            source = new { path = admitted.SourcePath }
                        }).ToArray();
                    await connection.WriteResponseAsync(request, true, new { breakpoints }, null,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (request.Command.Equals("setFunctionBreakpoints", StringComparison.Ordinal) ||
                    request.Command.Equals("setExceptionBreakpoints", StringComparison.Ordinal) ||
                    request.Command.Equals("setDataBreakpoints", StringComparison.Ordinal))
                {
                    AdmittedDapBreakpointConfiguration admitted;
                    try
                    {
                        admitted = DebugRequestAdmission.AdmitBreakpointConfiguration(
                            request.Command,
                            request.Arguments);
                    }
                    catch (DebugRequestRejectedException exception)
                    {
                        await connection.WriteResponseAsync(
                            request,
                            success: false,
                            body: new { breakpoints = Array.Empty<object>() },
                            message: $"DebugSetupError: {exception.Message}",
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    breakpointRegistry.ReplaceUnsupportedCategory(admitted.Command, admitted.Unsupported);
                    await connection.WriteResponseAsync(
                        request,
                        success: !admitted.Unsupported,
                        body: new { breakpoints = Array.Empty<object>() },
                        message: admitted.Unsupported
                            ? $"DebugSetupError: VBA {DebugRequestAdmission.UnsupportedBreakpointKind(admitted.Command)} breakpoints are unsupported."
                            : null,
                        cancellationToken).ConfigureAwait(false);
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
                        pendingLaunch = DebugRequestAdmission.AdmitLaunch(request.Arguments);
                        pendingLaunchRequest = request;
                    }
                    catch (DebugRequestRejectedException exception)
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
                    var error = restartPreparation.Begin(
                        request, activeLaunch, runningSession, launchTask is not null);
                    if (error is not null)
                    {
                        await connection.WriteResponseAsync(
                            request,
                            success: false,
                            body: null,
                            message: error,
                            cancellationToken).ConfigureAwait(false);
                    }
                    continue;
                }

                if (request.Command.Equals("vba/restartPrepared", StringComparison.Ordinal))
                {
                    var preparedRestart = restartPreparation.ConsumeNotification(request.Arguments);
                    await connection.WriteResponseAsync(
                        request,
                        success: true,
                        body: null,
                        message: null,
                        cancellationToken).ConfigureAwait(false);
                    if (preparedRestart is null)
                    {
                        continue;
                    }

                    RestartPreparationResult preparationResult;
                    try
                    {
                        preparationResult = DebugRequestAdmission.AdmitRestartPreparation(request.Arguments);
                    }
                    catch (DebugRequestRejectedException exception)
                    {
                        await connection.WriteResponseAsync(
                            preparedRestart.Request,
                            success: false,
                            body: null,
                            message: exception.Message,
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (!preparationResult.Success)
                    {
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
                        freshLaunch = preparationResult.Launch!;
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
                            ModuleName = preparedRestart.Binding.TargetModuleName,
                            ProcedureName = preparedRestart.Binding.TargetProcedureName
                        };
                        breakpointRegistry.ValidateDapPolicyForLaunch(
                            freshLaunch.SourceSnapshot);
                        restartBinding = preparedRestart.BindRequestedTarget(
                            requestedModuleName,
                            requestedProcedureName);
                    }
                    catch (DebugSetupException exception)
                    {
                        freshLaunch = null!;
                        restartBinding = null!;
                        validationError = exception.Message;
                    }

                    if (validationError is not null)
                    {
                        await connection.WriteResponseAsync(
                            preparedRestart.Request,
                            success: false,
                            body: null,
                            message: validationError,
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    var retainedLaunch = activeLaunch!;
                    runningSession = null;
                    activeLaunch = null;
                    var restartSwapAuthority = restartPreparation.StartBuild(restartBinding);
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
                    if (restartPreparation.TakePending() is { } pendingRestart)
                    {
                        await connection.WriteResponseAsync(
                            pendingRestart.Request,
                            success: false,
                            body: null,
                            message: "VBA debug restart preparation was cancelled.",
                            cancellationToken).ConfigureAwait(false);
                    }
                    if (launchTask is not null)
                    {
                        restartPreparation.Cancel();
                        launchCancellation!.Cancel();
                        var stoppingLaunch = launchTask;
                        launchTask = null;
                        try
                        {
                            var launchResult = await stoppingLaunch.ConfigureAwait(false);
                            if (launchResult.TerminalFailure is { } terminalFailure)
                            {
                                var stopCompletion = new DebugFailureCompletion(new DebugFailureException(terminalFailure));
                                if (launchResult.EndedSession is { } terminalSession)
                                {
                                    await CompleteSessionCleanupAsync(stopCompletion, terminalSession, "ended Excel session")
                                        .ConfigureAwait(false);
                                }
                                stopCompletion.Complete().ThrowWithEvidence();
                            }
                            else if ((launchResult.RunningSession ?? launchResult.EndedSession) is { } stoppingSession)
                            {
                                await StopOwnedSessionAsync(stoppingSession).ConfigureAwait(false);
                            }
                        }
                        catch (OperationCanceledException)
                            when (launchCancellation.IsCancellationRequested)
                        {
                        }
                        launchTask = null;
                        restartPreparation.CompleteLaunch();
                        launchCancellation.Dispose();
                        launchCancellation = null;
                    }
                    if (runningSession is not null)
                    {
                        var stoppingSession = runningSession;
                        runningSession = null;
                        await StopOwnedSessionAsync(stoppingSession).ConfigureAwait(false);
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
        catch (Exception exception)
        {
            runFailure = exception;
        }
        finally
        {
            var completion = new DebugFailureCompletion(runFailure);
            requestReadCancellation.Cancel();
            if (requestReadTask is not null)
            {
                if (requestReadTask.IsCompleted)
                {
                    try { _ = await requestReadTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (requestReadCancellation.IsCancellationRequested) { }
                    catch (Exception exception)
                    {
                        completion.AddFailure("DAP input", "pending request", DebugResourceKind.Observation, exception);
                    }
                }
                else
                {
                    ObserveDetachedRequestRead(requestReadTask);
                }
            }
            if (launchTask is not null)
            {
                restartPreparation.Cancel();
                launchCancellation!.Cancel();
                try
                {
                    var launchResult = await launchTask.ConfigureAwait(false);
                    if (launchResult.TerminalFailure is { } terminalFailure) { completion.Merge(terminalFailure); }
                    if ((launchResult.RunningSession ?? launchResult.EndedSession) is { } ownedSession)
                    {
                        await CompleteSessionCleanupAsync(completion, ownedSession, "pending launch session")
                            .ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException exception) when (launchCancellation.IsCancellationRequested)
                {
                    if (exception is IDebugFailureEvidence retained)
                    {
                        foreach (var evidence in retained.FailureOutcome.Evidence) { completion.AddEvidence(evidence); }
                    }
                    else
                    {
                        completion.AddFailure("launch cancellation", "pending launch", DebugResourceKind.Handle, exception);
                        completion.AddEvidence(new("launch cancellation", "pending launch", DebugResourceKind.Handle,
                            false, "The cancelled launch did not supply a terminal owner outcome."));
                    }
                }
                catch (Exception exception)
                {
                    completion.AddFailure("launch completion", "pending launch", DebugResourceKind.Handle, exception);
                }
            }
            restartPreparation.CompleteLaunch();
            launchCancellation?.Dispose();
            if (runningSession is not null)
            {
                await CompleteSessionCleanupAsync(completion, runningSession, "owned Excel session").ConfigureAwait(false);
            }
            if (endedSession is not null)
            {
                await CompleteSessionCleanupAsync(completion, endedSession, "ended Excel session").ConfigureAwait(false);
            }
            completion.Complete().ThrowWithEvidence();
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
            breakpointRegistry.ValidateDapPolicyForLaunch(
                launchRequest.SourceSnapshot);
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
        Exception? cause = null;
        try
        {
            preparedPlan = await launchService.PrepareAsync(
                vbaDevPath, workspaceLease, launchRequest, restartBinding,
                effectiveLaunchCancellationToken,
                new DapDebugLifecycleSink(connection, transportCancellationToken)).ConfigureAwait(false);
            var currentBinding = restartSwapAuthority?.ClaimForSwap(
                preparedPlan.Snapshot.RestartBinding, effectiveLaunchCancellationToken);
            runningSession = await preparedPlan.CommitAsync(
                currentBinding, effectiveLaunchCancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cause = exception;
        }

        var completion = new DebugFailureCompletion(cause);
        if (preparedPlan is not null)
        {
            try { await preparedPlan.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception)
            {
                completion.AddFailure("prepared plan disposal", "prepared launch", DebugResourceKind.Handle, exception);
            }
            MergeOwnerEvidence(completion, preparedPlan, "prepared plan disposal", "prepared launch");
        }
        else if (cause is not IDebugFailureEvidence)
        {
            completion.AddEvidence(new("launch failure", "prepared launch resources", DebugResourceKind.Handle,
                false, "The failing launch boundary did not supply owner release evidence."));
        }
        var outcome = completion.Complete();
        if (cause is null && !outcome.HasCleanupFailure)
        {
            try
            {
                foreach (var breakpoint in runningSession!.VerifiedBreakpoints)
                {
                    var sourceIdentity = breakpoint.Source.Identity ??
                        throw new InvalidOperationException("A verified breakpoint requires an admitted source URI.");
                    await connection.WriteEventAsync("breakpoint", new
                    {
                        reason = "changed",
                        breakpoint = new
                        {
                            id = breakpointRegistry.GetOrAdd(sourceIdentity,
                                breakpoint.Source.EditorLine + 1),
                            verified = true,
                            line = breakpoint.Source.EditorLine + 1,
                            source = new { path = sourceIdentity.Path }
                        }
                    }, transportCancellationToken).ConfigureAwait(false);
                }
                await connection.WriteResponseAsync(dapRequest, true, null, null,
                    transportCancellationToken).ConfigureAwait(false);
                return new(runningSession, launchRequest with
                {
                    ProjectRoot = preparedPlan!.Snapshot.LaunchSettings.CanonicalProjectRoot,
                    DocumentName = preparedPlan.Snapshot.LaunchSettings.DocumentName,
                    WorkbookFileName = preparedPlan.Snapshot.LaunchSettings.WorkbookFileName,
                    ModuleName = runningSession.TargetModuleName,
                    ProcedureName = runningSession.TargetProcedureName,
                    RestartPreparation = preparedPlan.Snapshot.LaunchSettings.RestartPreparation
                });
            }
            catch (Exception exception)
            {
                completion = new DebugFailureCompletion(exception);
                await CompleteSessionCleanupAsync(completion, runningSession!, "new Excel session").ConfigureAwait(false);
                completion.Complete().ThrowWithEvidence();
                throw;
            }
        }

        completion = new DebugFailureCompletion(new DebugFailureException(outcome));
        if (runningSession is not null)
        {
            await CompleteSessionCleanupAsync(completion, runningSession, "new Excel session").ConfigureAwait(false);
        }
        outcome = completion.Complete();
        IStandaloneVbaDebugRunningSession? retainedSession =
            restartSwapAuthority?.CanRetainCurrentSession(launchCancellationToken) == true
            && (!outcome.HasCleanupFailure || outcome.OnlyFileDeletionFailed)
                ? restartBinding!.BoundSession : null;
        IStandaloneVbaDebugRunningSession? endedSession = null;
        if (retainedSession is null && restartBinding is not null)
        {
            restartSwapAuthority!.InvalidateForCleanupFailure();
            if (restartBinding.BoundSession.Completion.IsCompleted)
            {
                endedSession = restartBinding.BoundSession;
            }
            else if (!SessionReleaseIsProved(restartBinding.BoundSession))
            {
                completion = new DebugFailureCompletion(new DebugFailureException(outcome));
                await CompleteSessionCleanupAsync(completion, restartBinding.BoundSession, "retained Excel session")
                    .ConfigureAwait(false);
                outcome = completion.Complete();
            }
        }
        var ordinaryCancellation = outcome.PrimaryFailure is OperationCanceledException && !outcome.HasCleanupFailure;
        var message = restartSwapAuthority?.SessionEnded == true
            ? "The owned VBA debug session exited during restart build before replacement committed."
                + (outcome.HasCleanupFailure ? Environment.NewLine + outcome.Describe() : string.Empty)
            : ordinaryCancellation ? "VBA debug launch was cancelled." : $"DebugSetupError: {outcome.Describe()}";
        try
        {
            await connection.WriteEventAsync("output", new
            {
                category = ordinaryCancellation ? "console" : "important",
                output = message + Environment.NewLine
            }, transportCancellationToken).ConfigureAwait(false);
            await connection.WriteResponseAsync(dapRequest, false, null, message,
                transportCancellationToken).ConfigureAwait(false);
            // Output can await user/client activity. Recheck the same authority at the return boundary.
            if (retainedSession is not null &&
                !restartSwapAuthority!.CanRetainCurrentSession(launchCancellationToken))
            {
                var invalidatedSession = retainedSession;
                retainedSession = null;
                if (invalidatedSession.Completion.IsCompleted)
                {
                    endedSession = invalidatedSession;
                }
                else
                {
                    completion = new DebugFailureCompletion(new DebugFailureException(outcome));
                    await CompleteSessionCleanupAsync(completion, invalidatedSession, "retained Excel session")
                        .ConfigureAwait(false);
                    completion.Complete().ThrowWithEvidence();
                }
            }
            var requestOnlySourceRejection = cause is DebugSourceRejectedPreparationException
                && preparedPlan is null && restartBinding is null && !outcome.HasCleanupFailure
                && !launchCancellationToken.IsCancellationRequested;
            if (retainedSession is null && endedSession is null && !ordinaryCancellation && !requestOnlySourceRejection)
            {
                await connection.WriteEventAsync("terminated", null, transportCancellationToken).ConfigureAwait(false);
            }
            return new(retainedSession, retainedSession is null ? null : retainedLaunch, endedSession,
                outcome.HasUnprovedRelease ? outcome : null);
        }
        catch (Exception exception)
        {
            completion = new DebugFailureCompletion(new DebugFailureException(outcome));
            if (!(ordinaryCancellation && exception is OperationCanceledException && transportCancellationToken.IsCancellationRequested))
            {
                completion.AddFailure("DAP output", "launch result", DebugResourceKind.Observation, exception);
            }
            if (retainedSession is not null)
            {
                restartSwapAuthority!.InvalidateForCleanupFailure();
                await CompleteSessionCleanupAsync(completion, retainedSession, "retained Excel session")
                    .ConfigureAwait(false);
            }
            if (endedSession is not null)
            {
                await CompleteSessionCleanupAsync(completion, endedSession, "ended Excel session")
                    .ConfigureAwait(false);
            }
            completion.Complete().ThrowWithEvidence();
            throw;
        }
    }

    private static bool SessionReleaseIsProved(IStandaloneVbaDebugRunningSession session)
    {
        if (session is not IDebugResourceOwnerEvidence { CleanupOutcome: { } outcome } || outcome.HasUnprovedRelease)
        {
            return false;
        }
        return new[] { DebugResourceKind.Process, DebugResourceKind.Com, DebugResourceKind.Handle }
            .All(kind => outcome.Evidence.Any(item => item.Kind == kind && item.Released));
    }

    private static void MergeOwnerEvidence(DebugFailureCompletion completion, object owner,
        string stage, string resource)
    {
        if (owner is IDebugResourceOwnerEvidence { CleanupOutcome: { } outcome })
        {
            completion.Merge(outcome);
        }
        else
        {
            completion.AddEvidence(new(stage, resource, DebugResourceKind.Handle, false,
                "The responsible owner did not supply a terminal release outcome."));
        }
    }

    private static async Task CompleteSessionCleanupAsync(DebugFailureCompletion completion,
        IStandaloneVbaDebugRunningSession session, string resource)
    {
        try { await session.TerminateAsync().ConfigureAwait(false); }
        catch (Exception exception)
        {
            completion.AddFailure("session termination", resource, DebugResourceKind.Process, exception, session.ProcessId);
        }
        try { await session.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception)
        {
            completion.AddFailure("session disposal", resource, DebugResourceKind.Handle, exception, session.ProcessId);
        }
        MergeOwnerEvidence(completion, session, "session disposal", resource);
    }

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
        Exception? cause = null;
        try { await runningSession.TerminateAsync().ConfigureAwait(false); }
        catch (Exception exception) { cause = exception; }
        var completion = new DebugFailureCompletion(cause);
        try { await runningSession.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception)
        {
            completion.AddFailure("session disposal", "owned Excel session", DebugResourceKind.Handle,
                exception, runningSession.ProcessId);
        }
        MergeOwnerEvidence(completion, runningSession, "session disposal", "owned Excel session");
        completion.Complete().ThrowWithEvidence();
    }

    private sealed class DapSourceBreakpointRegistry
    {
        private readonly Dictionary<SourceIdentity, List<RegisteredDapSourceBreakpoint>> bySource = [];
        private readonly HashSet<string> unsupportedCategories =
            new(StringComparer.Ordinal);
        private int nextId;

        public int GetOrAdd(SourceIdentity sourceIdentity, int line)
        {
            if (bySource.TryGetValue(sourceIdentity, out var breakpoints))
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
                bySource.Add(sourceIdentity, breakpoints);
            }
            breakpoints.Add(registered);
            return registered.Id;
        }

        public IReadOnlyList<RegisteredDapSourceBreakpoint> Replace(
            SourceIdentity sourceIdentity,
            IReadOnlyList<DapSourceBreakpointIntent> breakpoints)
        {
            bySource.TryGetValue(sourceIdentity, out var previous);
            var replacement = breakpoints
                .Select(intent => new RegisteredDapSourceBreakpoint(
                    previous?.FirstOrDefault(item => item.Intent.Line == intent.Line)?.Id
                        ?? checked(++nextId),
                    intent))
                .ToList();
            if (replacement.Count == 0)
            {
                bySource.Remove(sourceIdentity);
            }
            else
            {
                bySource[sourceIdentity] = replacement;
            }
            return replacement;
        }

        public void ValidateDapPolicyForLaunch(TransportedDebugSourceSnapshot snapshot)
        {
            if (unsupportedCategories.FirstOrDefault() is { } unsupportedCategory)
            {
                throw new DebugSetupException(
                    $"VBA {DebugRequestAdmission.UnsupportedBreakpointKind(unsupportedCategory)} breakpoints are unsupported.");
            }
            var sourceIdentities = new HashSet<SourceIdentity>();
            foreach (var source in snapshot.Sources.Where(source => source.SourceUri is not null))
            {
                if (source.Uri.Identity is not { } sourceIdentity)
                {
                    // Source transport validation belongs to DebugSourceAdmission.
                    return;
                }
                sourceIdentities.Add(sourceIdentity);
            }
            foreach (var (sourcePath, breakpoints) in bySource.Where(item =>
                         sourceIdentities.Contains(item.Key)))
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

    private sealed class DapDebugLifecycleSink(
        DapConnection connection,
        CancellationToken transportCancellationToken) : IDebugLifecycleSink
    {
        public ValueTask WriteAsync(
            DebugLifecycleMessage message,
            CancellationToken cancellationToken)
        {
            if (message.SnapshotBuild is { } report)
            {
                return new ValueTask(connection.WriteEventAsync("vba/snapshotBuild", new
                {
                    schemaVersion = report.SchemaVersion,
                    projectRoot = report.ProjectRoot,
                    documentName = report.DocumentName,
                    generation = report.Generation,
                    exitCode = report.ExitCode,
                    stdout = report.Stdout,
                    stderr = report.Stderr,
                    origins = report.Origins.Select(origin => new
                    {
                        snapshotUri = origin.SnapshotUri,
                        sourceUri = origin.SourceUri
                    })
                }, transportCancellationToken));
            }
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

internal sealed record StandaloneVbaDebugLaunchExecutionResult(
    IStandaloneVbaDebugRunningSession? RunningSession,
    StandaloneVbaDebugLaunchRequest? ActiveLaunch,
    IStandaloneVbaDebugRunningSession? EndedSession = null,
    DebugFailureOutcome? TerminalFailure = null);

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

internal sealed record RestartPreparationResult(
    bool Success,
    string? Message,
    StandaloneVbaDebugLaunchRequest? Launch);

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
