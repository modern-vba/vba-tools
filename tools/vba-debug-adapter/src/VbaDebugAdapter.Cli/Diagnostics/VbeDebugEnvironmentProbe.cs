using System.Security.Cryptography;
using System.Text;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using VbaTools.Syntax;

namespace VbaDebugAdapter.Diagnostics;

internal sealed class VbeDebugEnvironmentProbeFactory
    : IDebugEnvironmentProbeFactory
{
    private readonly IVbaDebugSessionWorkspaceManager workspaceManager;
    private readonly IVbeDebugSessionFactory sessionFactory;

    public VbeDebugEnvironmentProbeFactory(
        IVbaDebugSessionWorkspaceManager workspaceManager)
        : this(workspaceManager, UnconfiguredVbeDebugSessionFactory.Instance)
    {
    }

    public VbeDebugEnvironmentProbeFactory(
        IVbaDebugSessionWorkspaceManager workspaceManager,
        IVbeDebugSessionFactory sessionFactory)
    {
        ArgumentNullException.ThrowIfNull(workspaceManager);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        this.workspaceManager = workspaceManager;
        this.sessionFactory = sessionFactory;
    }

    public IDebugEnvironmentProbe Create()
        => new VbeDebugEnvironmentProbe(workspaceManager, sessionFactory);

    private sealed class UnconfiguredVbeDebugSessionFactory : IVbeDebugSessionFactory
    {
        public static UnconfiguredVbeDebugSessionFactory Instance { get; } = new();

        public Task<IVbeDebugSession> StartVisibleAsync(
            CancellationToken cancellationToken)
            => Task.FromException<IVbeDebugSession>(new InvalidOperationException(
                "The production VBE debug session factory is not configured."));
    }
}

internal sealed class VbeDebugEnvironmentProbe(
    IVbaDebugSessionWorkspaceManager workspaceManager,
    IVbeDebugSessionFactory sessionFactory)
    : IDebugEnvironmentProbe
{
    private const string ModuleName = "VbaToolsDoctorProbe";
    private const string ProcedureName = "RunDoctorProbe";
    private const string CompletionMarker = "vba-tools-doctor-complete";
    private const int CompletionAssignmentEditorLine = 5;
    private const string ProbeSource =
        "Attribute VB_Name = \"VbaToolsDoctorProbe\"\r\n" +
        "Option Explicit\r\n" +
        "Option Private Module\r\n" +
        "\r\n" +
        "Public Sub RunDoctorProbe()\r\n" +
        "    ThisWorkbook.Worksheets(1).Range(\"A1\").Value2 = \"vba-tools-doctor-complete\"\r\n" +
        "End Sub\r\n";

    private IVbaDebugSessionWorkspaceLease? workspaceLease;
    private IVbeDebugSession? debugSession;
    private DebugSessionId? sessionId;
    private string? fixtureWorkbookPath;
    private string? fixtureSourcePath;
    private VbeBreakpoint? breakpoint;
    private bool breakpointSet;
    private bool excelStartupAttempted;
    private bool startupCleanupClassified;
    private bool ownedProcessCleanupVerified;
    private Exception? startupCleanupException;

    public async Task<DebugEnvironmentProbeCheckResult> RunStageAsync(
        string checkId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RunStageCoreAsync(
                checkId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DebugSetupException exception)
        {
            return new DebugEnvironmentProbeCheckResult(
                DebugEnvironmentDiagnosticStatus.Fail,
                exception.Message)
            {
                Remediation = RemediationFor(checkId),
                Details = new Dictionary<string, object?>
                {
                    ["exceptionType"] = exception.GetType().Name
                }
            };
        }
    }

    private Task<DebugEnvironmentProbeCheckResult> RunStageCoreAsync(
        string checkId,
        CancellationToken cancellationToken)
        => checkId switch
        {
            "workspace.session" => CreateWorkspaceSessionAsync(cancellationToken),
            "excel.startup" => StartExcelAsync(cancellationToken),
            "excel.processOwnership" => Task.FromResult(VerifyProcessOwnership()),
            "workbook.fixtureCreation" => CreateFixtureAsync(cancellationToken),
            "workbook.open" => OpenFixtureAsync(cancellationToken),
            "vbide.access" => VerifyVbideAccessAsync(cancellationToken),
            "vbe.commandContext" => VerifyCommandContextAsync(cancellationToken),
            "vbe.breakpoint" => SetBreakpointAsync(cancellationToken),
            "vbe.breakMode" => EnterBreakModeAsync(cancellationToken),
            "vbe.continue" => ContinueAsync(cancellationToken),
            "vbe.procedureCompletion" => VerifyCompletionAsync(cancellationToken),
            "vbe.breakpointCleanup" => ClearBreakpointAsync(cancellationToken),
            "excel.processClose" => CloseProcessAsync(cancellationToken),
            "workspace.deletion" => DeleteWorkspaceAsync(cancellationToken),
            _ => Task.FromResult(new DebugEnvironmentProbeCheckResult(
                DebugEnvironmentDiagnosticStatus.Fail,
                $"The production Doctor stage '{checkId}' is not configured."))
        };

    private static string? RemediationFor(string checkId)
        => checkId switch
        {
            "excel.startup" =>
                "Verify that desktop Microsoft Excel is installed and can start in the current user session.",
            "vbide.access" =>
                "Enable Trust access to the VBA project object model in Excel Trust Center, then retry.",
            "vbe.commandContext" or "vbe.breakpoint" or "vbe.breakMode" or
                "vbe.continue" or "vbe.procedureCompletion" or
                "vbe.breakpointCleanup" =>
                "Close any VBE dialog, restore the temporary project to design mode, and retry Doctor.",
            _ => null
        };

    private async Task<DebugEnvironmentProbeCheckResult> CloseProcessAsync(
        CancellationToken cancellationToken)
    {
        if (debugSession is null)
        {
            if (!excelStartupAttempted)
            {
                return DebugEnvironmentProbeCheckResult.Pass(
                    "No Doctor Excel process was started.");
            }
            if (startupCleanupClassified && ownedProcessCleanupVerified)
            {
                return DebugEnvironmentProbeCheckResult.Pass(
                    "Excel startup failure cleanup was explicitly verified before no session was returned.");
            }

            return new DebugEnvironmentProbeCheckResult(
                DebugEnvironmentDiagnosticStatus.Fail,
                startupCleanupException is null
                    ? "Excel startup did not return an owned session and cleanup was not verified."
                    : $"Excel startup cleanup failed: {startupCleanupException.Message}")
            {
                Remediation =
                    "Close any Excel process created by the failed Doctor startup before running scoped cleanup."
            };
        }
        if (debugSession.Completion.IsCompletedSuccessfully)
        {
            var completedSession = debugSession;
            await completedSession.DisposeAsync().ConfigureAwait(false);
            debugSession = null;
            ownedProcessCleanupVerified = true;
            return DebugEnvironmentProbeCheckResult.Pass(
                "Owned Excel process exit was observed before cooperative close, and its session ownership was disposed.");
        }
        if (debugSession is not IVbeDebugDoctorControl doctorControl)
        {
            throw new InvalidOperationException(
                "The owned Excel session does not expose cooperative Doctor cleanup.");
        }

        var session = debugSession;
        await doctorControl.CloseOwnedProcessCooperativelyAsync(
            cancellationToken).ConfigureAwait(false);
        await session.DisposeAsync().ConfigureAwait(false);
        debugSession = null;
        ownedProcessCleanupVerified = true;
        return DebugEnvironmentProbeCheckResult.Pass(
            "The owned Excel process closed and its Job/COM session was disposed.");
    }

    private async Task<DebugEnvironmentProbeCheckResult> ClearBreakpointAsync(
        CancellationToken cancellationToken)
    {
        if (!breakpointSet)
        {
            return DebugEnvironmentProbeCheckResult.Pass(
                "No native Doctor breakpoint remained to clear.");
        }
        if (debugSession?.Completion.IsCompletedSuccessfully == true)
        {
            breakpointSet = false;
            return DebugEnvironmentProbeCheckResult.Pass(
                "Owned Excel process exit proved that no session-local Doctor breakpoint remains.");
        }
        if (debugSession is not IVbeDebugDoctorControl doctorControl ||
            breakpoint is null)
        {
            throw new InvalidOperationException(
                "The Doctor breakpoint state could not be reconciled with its owned session.");
        }

        await doctorControl.ClearNativeBreakpointAsync(
            breakpoint,
            cancellationToken).ConfigureAwait(false);
        breakpointSet = false;
        return DebugEnvironmentProbeCheckResult.Pass(
            "The exact native Doctor breakpoint was cleared.");
    }

    private async Task<DebugEnvironmentProbeCheckResult> VerifyCompletionAsync(
        CancellationToken cancellationToken)
    {
        if (debugSession is not IVbeDebugDoctorControl doctorControl)
        {
            throw new InvalidOperationException(
                "A Doctor-capable Excel session is required to verify completion.");
        }

        await doctorControl.WaitForCompletionAsync(
            CompletionMarker,
            cancellationToken).ConfigureAwait(false);
        return DebugEnvironmentProbeCheckResult.Pass(
            "The temporary procedure returned to design mode and wrote its harmless completion marker.");
    }

    private async Task<DebugEnvironmentProbeCheckResult> ContinueAsync(
        CancellationToken cancellationToken)
    {
        if (debugSession is not IVbeDebugProbeControl probeControl)
        {
            throw new InvalidOperationException(
                "A VBE probe control is required to continue the temporary target.");
        }

        await probeControl.ContinueTargetAsync(
            new DebugTargetProcedure(ModuleName, ProcedureName),
            cancellationToken).ConfigureAwait(false);
        return DebugEnvironmentProbeCheckResult.Pass(
            "The native Run/Continue command continued the temporary target from break mode.");
    }

    private async Task<DebugEnvironmentProbeCheckResult> EnterBreakModeAsync(
        CancellationToken cancellationToken)
    {
        if (debugSession is not IVbeDebugDoctorControl doctorControl)
        {
            throw new InvalidOperationException(
                "A Doctor-capable Excel session is required to observe break mode.");
        }

        var target = new DebugTargetProcedure(ModuleName, ProcedureName);
        await debugSession.RunTargetAsync(
            target,
            inputWaitSink: null,
            cancellationToken).ConfigureAwait(false);
        await doctorControl.WaitForBreakModeAsync(
            cancellationToken).ConfigureAwait(false);
        return DebugEnvironmentProbeCheckResult.Pass(
            "The harmless temporary procedure stopped in native VBE break mode.");
    }

    private async Task<DebugEnvironmentProbeCheckResult> SetBreakpointAsync(
        CancellationToken cancellationToken)
    {
        if (debugSession is null || breakpoint is null)
        {
            throw new InvalidOperationException(
                "Native command context must be verified before a breakpoint is set.");
        }

        await debugSession.SetNativeBreakpointsAsync(
            [breakpoint],
            cancellationToken).ConfigureAwait(false);
        breakpointSet = true;
        return DebugEnvironmentProbeCheckResult.Pass(
            $"A native breakpoint was set at {breakpoint.ModuleName}:{breakpoint.VbideLine}.") with
        {
            Details = new Dictionary<string, object?>
            {
                ["moduleName"] = breakpoint.ModuleName,
                ["line"] = breakpoint.VbideLine
            }
        };
    }

    private async Task<DebugEnvironmentProbeCheckResult> VerifyCommandContextAsync(
        CancellationToken cancellationToken)
    {
        if (debugSession is not IVbeDebugDoctorControl doctorControl ||
            breakpoint is null)
        {
            throw new InvalidOperationException(
                "VBIDE access must be established before native command context is verified.");
        }

        await doctorControl.VerifyCommandContextAsync(
            breakpoint,
            new DebugTargetProcedure(ModuleName, ProcedureName),
            cancellationToken).ConfigureAwait(false);
        return DebugEnvironmentProbeCheckResult.Pass(
            "The native Toggle Breakpoint and Run Sub/UserForm controls are enabled in the exact VBE code context.") with
        {
            Details = new Dictionary<string, object?>
            {
                ["toggleBreakpointCommandId"] =
                    VbeNativeCommandContract.ToggleBreakpointCommandId,
                ["runOrContinueCommandId"] =
                    VbeNativeCommandContract.RunOrContinueCommandId
            }
        };
    }

    private async Task<DebugEnvironmentProbeCheckResult> VerifyVbideAccessAsync(
        CancellationToken cancellationToken)
    {
        if (debugSession is not IVbeDebugDoctorControl doctorControl ||
            fixtureSourcePath is null ||
            breakpoint is null)
        {
            throw new InvalidOperationException(
                "The Doctor fixture must be mapped and opened before VBIDE access is verified.");
        }

        await doctorControl.ImportFixtureModuleAsync(
            fixtureSourcePath,
            breakpoint.SourceMap,
            cancellationToken).ConfigureAwait(false);
        return DebugEnvironmentProbeCheckResult.Pass(
            "Trusted VBIDE access imported and verified the exact temporary standard module.");
    }

    private async Task<DebugEnvironmentProbeCheckResult> OpenFixtureAsync(
        CancellationToken cancellationToken)
    {
        if (debugSession is not IVbeDebugDoctorControl doctorControl ||
            fixtureWorkbookPath is null)
        {
            throw new InvalidOperationException(
                "The Doctor fixture must be created before it is opened.");
        }

        await doctorControl.OpenFixtureWorkbookAsync(
            fixtureWorkbookPath,
            cancellationToken).ConfigureAwait(false);
        return DebugEnvironmentProbeCheckResult.Pass(
            "The exact temporary Doctor workbook opened in the owned Excel process.");
    }

    private async Task<DebugEnvironmentProbeCheckResult> CreateFixtureAsync(
        CancellationToken cancellationToken)
    {
        if (workspaceLease is null || debugSession is null)
        {
            throw new InvalidOperationException(
                "The Doctor workspace and Excel session must exist before fixture creation.");
        }
        if (debugSession is not IVbeDebugDoctorControl doctorControl)
        {
            return new DebugEnvironmentProbeCheckResult(
                DebugEnvironmentDiagnosticStatus.Fail,
                "The owned Excel session does not expose the production Doctor control surface.");
        }

        fixtureWorkbookPath = Path.Combine(
            workspaceLease.SessionWorkspacePath,
            $"{ModuleName}.xlsm");
        fixtureSourcePath = Path.Combine(
            workspaceLease.SessionWorkspacePath,
            $"{ModuleName}.bas");
        await File.WriteAllTextAsync(
            fixtureSourcePath,
            ProbeSource,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
        var sourceUri = new Uri(Path.GetFullPath(fixtureSourcePath)).AbsoluteUri;
        var syntaxTree = VbaSyntaxTree.ParseModule(sourceUri, ProbeSource);
        breakpoint = DebugBreakpointProjection.Create(syntaxTree).Map(
            new DebugSourceBreakpoint(
                sourceUri,
                CompletionAssignmentEditorLine));
        await doctorControl.CreateFixtureWorkbookAsync(
            fixtureWorkbookPath,
            cancellationToken).ConfigureAwait(false);
        return DebugEnvironmentProbeCheckResult.Pass(
            "A temporary macro-enabled workbook and standard-module source were created in the Doctor session workspace.") with
        {
            Details = new Dictionary<string, object?>
            {
                ["workbookPath"] = fixtureWorkbookPath,
                ["sourcePath"] = fixtureSourcePath,
                ["moduleName"] = ModuleName,
                ["procedureName"] = ProcedureName,
                ["breakpointLine"] = breakpoint.VbideLine
            }
        };
    }

    private DebugEnvironmentProbeCheckResult VerifyProcessOwnership()
    {
        if (debugSession is null)
        {
            throw new InvalidOperationException(
                "Excel must start before its process ownership is verified.");
        }

        var strongOwnership = debugSession is IVbeDebugProbeControl probeControl &&
            probeControl.StrongProcessOwnershipEstablished;
        return new DebugEnvironmentProbeCheckResult(
            strongOwnership
                ? DebugEnvironmentDiagnosticStatus.Pass
                : DebugEnvironmentDiagnosticStatus.Fail,
            strongOwnership
                ? $"Excel PID {debugSession.ProcessId} is assigned to a kill-on-close Job Object."
                : $"Excel PID {debugSession.ProcessId} is not proven to be assigned to a kill-on-close Job Object.")
        {
            Remediation = strongOwnership
                ? null
                : "Ensure the adapter can create and assign a Windows Job Object for its owned Excel process.",
            Details = new Dictionary<string, object?>
            {
                ["processId"] = debugSession.ProcessId,
                ["killOnCloseJobAssigned"] = strongOwnership
            }
        };
    }

    private async Task<DebugEnvironmentProbeCheckResult> StartExcelAsync(
        CancellationToken cancellationToken)
    {
        if (workspaceLease is null)
        {
            throw new InvalidOperationException(
                "The Doctor workspace must be claimed before Excel starts.");
        }
        if (debugSession is not null)
        {
            throw new InvalidOperationException(
                "The Doctor Excel session has already been started.");
        }

        excelStartupAttempted = true;
        try
        {
            debugSession = await sessionFactory.StartVisibleAsync(
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (exception is IVbeDebugSessionStartFailure startFailure)
            {
                startupCleanupClassified = true;
                startupCleanupException = startFailure.CleanupException;
                ownedProcessCleanupVerified = startFailure.CleanupVerified;
            }
            throw;
        }
        if (debugSession.ProcessId <= 0)
        {
            return new DebugEnvironmentProbeCheckResult(
                DebugEnvironmentDiagnosticStatus.Fail,
                "The Doctor Excel session did not report a valid owned process ID.");
        }

        return DebugEnvironmentProbeCheckResult.Pass(
            $"A dedicated Excel process started with PID {debugSession.ProcessId}.") with
        {
            Details = new Dictionary<string, object?>
            {
                ["processId"] = debugSession.ProcessId
            }
        };
    }

    private async Task<DebugEnvironmentProbeCheckResult> CreateWorkspaceSessionAsync(
        CancellationToken cancellationToken)
    {
        if (workspaceLease is not null)
        {
            throw new InvalidOperationException(
                "The Doctor workspace session has already been claimed.");
        }

        var newSessionId = DebugSessionId.Parse(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
                .ToLowerInvariant());
        sessionId = newSessionId;
        var retained = await workspaceManager.ReapStaleAsync(
            newSessionId,
            cancellationToken).ConfigureAwait(false);
        workspaceLease = await workspaceManager.ClaimAsync(
            newSessionId,
            cancellationToken).ConfigureAwait(false);
        return new DebugEnvironmentProbeCheckResult(
            retained.Count == 0
                ? DebugEnvironmentDiagnosticStatus.Pass
                : DebugEnvironmentDiagnosticStatus.Warning,
            retained.Count == 0
                ? "A dedicated Doctor session workspace lease was claimed."
                : $"A dedicated Doctor session workspace lease was claimed; " +
                  $"{retained.Count} unrelated stale workspace(s) were retained.")
        {
            Details = new Dictionary<string, object?>
            {
                ["sessionId"] = newSessionId.Value,
                ["workspacePath"] = workspaceLease.SessionWorkspacePath,
                ["retainedWorkspaceCount"] = retained.Count
            }
        };
    }

    private async Task<DebugEnvironmentProbeCheckResult> DeleteWorkspaceAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (debugSession is not null)
        {
            var session = debugSession;
            try
            {
                await session.TerminateAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                await session.DisposeAsync().ConfigureAwait(false);
                debugSession = null;
                ownedProcessCleanupVerified = true;
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return new DebugEnvironmentProbeCheckResult(
                    DebugEnvironmentDiagnosticStatus.Fail,
                    $"The Doctor workspace was retained because Job-backed Excel cleanup failed: {exception.Message}")
                {
                    Remediation =
                        "Close the retained owned Excel process, then run adapter cleanup for the retained session.",
                    Details = new Dictionary<string, object?>
                    {
                        ["retainedPath"] = workspaceLease?.SessionWorkspacePath,
                        ["processId"] = session.ProcessId,
                        ["sessionId"] = sessionId?.Value
                    }
                };
            }
        }
        if (excelStartupAttempted && !ownedProcessCleanupVerified)
        {
            return new DebugEnvironmentProbeCheckResult(
                DebugEnvironmentDiagnosticStatus.Fail,
                "The Doctor workspace was retained because Excel startup cleanup was not verified.")
            {
                Remediation =
                    "Inspect the retained workspace and any Excel process created during startup before running scoped cleanup.",
                Details = new Dictionary<string, object?>
                {
                    ["retainedPath"] = workspaceLease?.SessionWorkspacePath,
                    ["sessionId"] = sessionId?.Value
                }
            };
        }
        if (workspaceLease is null)
        {
            return DebugEnvironmentProbeCheckResult.Pass(
                "No Doctor session workspace was created.");
        }

        var lease = workspaceLease;
        workspaceLease = null;
        await lease.DisposeAsync().ConfigureAwait(false);
        return DebugEnvironmentProbeCheckResult.Pass(
            "The Doctor session workspace and lease were deleted.");
    }
}
