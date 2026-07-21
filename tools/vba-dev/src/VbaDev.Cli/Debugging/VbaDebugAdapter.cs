using System.Collections.Immutable;
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
        var breakpointRegistry = new DapBreakpointRegistry();
        var configurationDone = false;
        DebugRunningSession? runningSession = null;
        Task? monitorTask = null;
        CancellationTokenSource? launchCancellation = null;
        Task<(DebugRunningSession? Session, Task? Monitor, bool TerminatedEventSent)>? launchTask = null;
        var launchTerminationEventSent = false;
        var stopHandled = false;
        try
        {
            while (await connection.ReadRequestAsync(cancellationToken).ConfigureAwait(false) is { } request)
            {
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
                    if (pendingLaunchRequest is not null || launchTask is not null)
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
                        launchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken);
                        launchTask = StartLaunchAsync(
                            connection,
                            pendingLaunchRequest,
                            pendingLaunch,
                            breakpointRegistry.Breakpoints,
                            breakpointRegistry.UnsupportedBreakpoints,
                            cancellationToken,
                            launchCancellation.Token);
                    }

                    continue;
                }

                if (request.Command.Equals("configurationDone", StringComparison.Ordinal))
                {
                    configurationDone = true;
                    breakpointRegistry.Freeze();
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
                        launchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken);
                        launchTask = StartLaunchAsync(
                            connection,
                            pendingLaunchRequest,
                            pendingLaunch,
                            breakpointRegistry.Breakpoints,
                            breakpointRegistry.UnsupportedBreakpoints,
                            cancellationToken,
                            launchCancellation.Token);
                    }

                    continue;
                }

                if (request.Command.Equals("disconnect", StringComparison.Ordinal) ||
                    request.Command.Equals("terminate", StringComparison.Ordinal))
                {
                    launchCancellation?.Cancel();
                    await connection.WriteResponseAsync(
                        request,
                        success: true,
                        body: null,
                        message: null,
                        cancellationToken).ConfigureAwait(false);

                    if (launchTask is not null)
                    {
                        (runningSession, monitorTask, launchTerminationEventSent) =
                            await launchTask.ConfigureAwait(false);
                    }

                    if (runningSession is not null && !runningSession.Completion.IsCompleted)
                    {
                        await runningSession.TerminateAsync().ConfigureAwait(false);
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
        finally
        {
            if (!stopHandled && launchTask is not null)
            {
                if (!launchTask.IsCompleted)
                {
                    launchCancellation?.Cancel();
                }

                (runningSession, monitorTask, launchTerminationEventSent) =
                    await launchTask.ConfigureAwait(false);
            }

            if (!stopHandled)
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

            launchCancellation?.Dispose();
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
        bool TerminatedEventSent)> StartLaunchAsync(
        DapConnection connection,
        DapRequest launchRequest,
        DebugLaunchRequest launch,
        IReadOnlyList<PendingDapBreakpoint> pendingBreakpoints,
        IReadOnlyList<UnsupportedDebugBreakpoint> unsupportedBreakpoints,
        CancellationToken transportCancellationToken,
        CancellationToken launchCancellationToken)
    {
        try
        {
            launch = ReconcileBreakpointPlan(launch, pendingBreakpoints, unsupportedBreakpoints);
            var lifecycleSink = new DapDebugLaunchEventSink(connection, pendingBreakpoints);
            var session = await launchCoordinator
                .LaunchAsync(launch, lifecycleSink, launchCancellationToken)
                .ConfigureAwait(false);
            await connection.WriteResponseAsync(
                launchRequest,
                success: true,
                body: null,
                message: null,
                transportCancellationToken).ConfigureAwait(false);
            var monitor = MonitorSessionAsync(connection, session, transportCancellationToken);
            if (session.Completion.IsCompleted)
            {
                await monitor.ConfigureAwait(false);
            }

            return (session, monitor, false);
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
            return (null, null, false);
        }
        catch (Exception ex) when (ex is DebugSetupException or DebugLaunchBusyException or ProjectManifestException)
        {
            await WriteLaunchFailureAsync(
                    connection,
                    launchRequest,
                    ex.Message,
                    transportCancellationToken)
                .ConfigureAwait(false);
            return (null, null, true);
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

    private sealed class DapBreakpointRegistry
    {
        private readonly Dictionary<string, List<PendingDapBreakpoint>> bySource =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<UnsupportedDebugBreakpointKind, UnsupportedDebugBreakpoint>
            unsupportedByKind = [];
        private int nextBreakpointId;
        private bool isFrozen;

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
            EnsureMutable();

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
            EnsureMutable();
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
            if (!isFrozen)
            {
                unsupportedByKind[kind] = new UnsupportedDebugBreakpoint(kind, description);
            }
        }

        public void Freeze() => isFrozen = true;

        private void EnsureMutable()
        {
            if (isFrozen)
            {
                throw new DebugSetupException(
                    "The VBA breakpoint configuration is frozen after configurationDone.");
            }
        }
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
