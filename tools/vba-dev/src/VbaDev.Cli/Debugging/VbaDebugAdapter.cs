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
        PendingDapBreakpoint? pendingBreakpoint = null;
        var nextBreakpointId = 0;
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

                if (request.Command.Equals("setBreakpoints", StringComparison.Ordinal))
                {
                    try
                    {
                        var update = ParseSourceBreakpointUpdate(
                            request,
                            pendingBreakpoint,
                            ref nextBreakpointId);
                        pendingBreakpoint = update.PendingBreakpoint;
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
                            pendingBreakpoint,
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
                            pendingBreakpoint,
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

    private async Task<(DebugRunningSession? Session, Task? Monitor)> StartLaunchAsync(
        DapConnection connection,
        DapRequest launchRequest,
        DebugLaunchRequest launch,
        PendingDapBreakpoint? pendingBreakpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateBreakpointCorrelation(launch.SourceSnapshot.Breakpoints, pendingBreakpoint);
            var lifecycleSink = new DapDebugLaunchEventSink(connection, pendingBreakpoint);
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

            if (breakpointsValue.GetArrayLength() > 1)
            {
                throw new DebugSetupException(
                    "Initial VBA breakpoint transfer supports at most one enabled ordinary source breakpoint.");
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
        PendingDapBreakpoint? currentBreakpoint,
        ref int nextBreakpointId)
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
        if (!Path.GetExtension(sourcePath).Equals(".bas", StringComparison.OrdinalIgnoreCase))
        {
            throw new DebugSetupException(
                "Initial VBA breakpoint transfer supports ordinary .bas source breakpoints only.");
        }

        if (!request.Arguments.TryGetProperty("breakpoints", out var breakpoints))
        {
            breakpoints = default;
        }

        if (breakpoints.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Array)
        {
            throw new DebugSetupException(
                "The VBA setBreakpoints request requires an array 'breakpoints' property.");
        }

        var count = breakpoints.ValueKind == JsonValueKind.Array
            ? breakpoints.GetArrayLength()
            : 0;
        if (count > 1)
        {
            throw new DebugSetupException(
                "Initial VBA breakpoint transfer supports at most one enabled ordinary source breakpoint.");
        }

        if (count == 0)
        {
            var retained = currentBreakpoint is not null &&
                !currentBreakpoint.Breakpoint.SourcePath.Equals(
                    sourcePath,
                    StringComparison.OrdinalIgnoreCase)
                    ? currentBreakpoint
                    : null;
            return new SourceBreakpointUpdate(retained, []);
        }

        if (currentBreakpoint is not null &&
            !currentBreakpoint.Breakpoint.SourcePath.Equals(
                sourcePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new DebugSetupException(
                "Initial VBA breakpoint transfer supports only one source breakpoint per launch.");
        }

        var breakpoint = breakpoints.EnumerateArray().Single();
        if (breakpoint.ValueKind != JsonValueKind.Object)
        {
            throw new DebugSetupException(
                "Each VBA source breakpoint must be an object with a one-based line.");
        }

        foreach (var unsupportedProperty in new[] { "column", "condition", "hitCondition", "logMessage" })
        {
            if (breakpoint.TryGetProperty(unsupportedProperty, out _))
            {
                throw new DebugSetupException(
                    $"VBA breakpoint property '{unsupportedProperty}' is unsupported.");
            }
        }

        var dapLine = RequiredInt32(breakpoint, "line", "breakpoints[].line");
        if (dapLine <= 0)
        {
            throw new DebugSetupException(
                "VBA source breakpoint line must be a positive one-based line number.");
        }

        var captured = new DebugSourceBreakpoint(sourcePath, dapLine - 1);
        var pending = currentBreakpoint is not null &&
            SameSourceBreakpoint(currentBreakpoint.Breakpoint, captured)
            ? currentBreakpoint
            : new PendingDapBreakpoint(++nextBreakpointId, captured);
        object[] responseBreakpoints =
        [
            new
            {
                id = pending.Id,
                verified = false,
                source = new { path = pending.Breakpoint.SourcePath },
                line = pending.Breakpoint.EditorLine + 1
            }
        ];
        return new SourceBreakpointUpdate(pending, responseBreakpoints);
    }

    private static void ValidateBreakpointCorrelation(
        ImmutableArray<DebugSourceBreakpoint> launchBreakpoints,
        PendingDapBreakpoint? pendingBreakpoint)
    {
        if (launchBreakpoints.IsDefaultOrEmpty)
        {
            if (pendingBreakpoint is not null)
            {
                throw new DebugSetupException(
                    "The pending DAP breakpoint is not present in the post-save debug source snapshot.");
            }

            return;
        }

        var launchBreakpoint = launchBreakpoints.Single();
        if (pendingBreakpoint is null ||
            !SameSourceBreakpoint(pendingBreakpoint.Breakpoint, launchBreakpoint))
        {
            throw new DebugSetupException(
                "The pending DAP breakpoint does not match the post-save debug source snapshot.");
        }
    }

    private sealed class DapDebugLaunchEventSink(
        DapConnection connection,
        PendingDapBreakpoint? pendingBreakpoint) : IDebugLaunchEventSink
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
            if (pendingBreakpoint is null ||
                !SameSourceBreakpoint(pendingBreakpoint.Breakpoint, breakpoint.Source))
            {
                throw new DebugSetupException(
                    "The transferred VBE breakpoint does not match the pending DAP breakpoint.");
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
                        source = new { path = pendingBreakpoint.Breakpoint.SourcePath },
                        line = breakpoint.Source.EditorLine + 1
                    }
                },
                cancellationToken));
        }
    }

    private sealed record PendingDapBreakpoint(int Id, DebugSourceBreakpoint Breakpoint);

    private static bool SameSourceBreakpoint(
        DebugSourceBreakpoint left,
        DebugSourceBreakpoint right)
        => left.EditorLine == right.EditorLine &&
            Path.GetFullPath(left.SourcePath).Equals(
                Path.GetFullPath(right.SourcePath),
                StringComparison.OrdinalIgnoreCase);

    private sealed record SourceBreakpointUpdate(
        PendingDapBreakpoint? PendingBreakpoint,
        object[] ResponseBreakpoints);
}
