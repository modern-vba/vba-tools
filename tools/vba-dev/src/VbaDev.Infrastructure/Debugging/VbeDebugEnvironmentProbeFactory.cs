using System.Collections.Immutable;
using System.Text;
using VbaDev.App.Debugging;
using VbaDev.App.Diagnostics;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Workbooks;

namespace VbaDev.Infrastructure.Debugging;

internal interface IDebugProbeWorkbookBuilder
{
    IDebugProbeWorkbookArtifact Build(CancellationToken cancellationToken);
}

internal interface IDebugProbeWorkbookArtifact : IDisposable
{
    string WorkbookPath { get; }

    DebugTargetProcedure Target { get; }

    VbeBreakpoint Breakpoint { get; }

    string CompletionMarker { get; }

    IReadOnlyList<DiagnosticResult> StartupDiagnostics { get; }
}

internal interface IVbeDebugProbeControl
{
    bool StrongProcessOwnershipEstablished { get; }

    Task WaitForBreakModeAsync(TimeSpan timeout, CancellationToken cancellationToken);

    Task ContinueTargetAsync(
        DebugTargetProcedure target,
        CancellationToken cancellationToken);

    Task WaitForCompletionAsync(
        string expectedMarker,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>
/// Creates a real native VBE readiness probe from a hidden build and a fresh visible debug session.
/// </summary>
public sealed class VbeDebugEnvironmentProbeFactory : IDebugEnvironmentProbeFactory
{
    private static readonly TimeSpan DefaultTransitionTimeout = TimeSpan.FromSeconds(15);

    private readonly IDebugProbeWorkbookBuilder workbookBuilder;
    private readonly IVbeDebugSessionFactory debugSessionFactory;
    private readonly TimeSpan transitionTimeout;

    /// <summary>
    /// Creates the production native VBE readiness probe factory.
    /// </summary>
    public VbeDebugEnvironmentProbeFactory()
        : this(
            new ExcelComDebugProbeWorkbookBuilder(
                new ExcelComWorkbookBuildAutomation(),
                new BreakpointSourceMapper()),
            new VbeDebugAutomation(),
            DefaultTransitionTimeout)
    {
    }

    internal VbeDebugEnvironmentProbeFactory(
        IDebugProbeWorkbookBuilder workbookBuilder,
        IVbeDebugSessionFactory debugSessionFactory,
        TimeSpan transitionTimeout)
    {
        this.workbookBuilder = workbookBuilder;
        this.debugSessionFactory = debugSessionFactory;
        this.transitionTimeout = transitionTimeout;
    }

    /// <inheritdoc />
    public async Task<IDebugEnvironmentProbeSession> StartAsync(
        CancellationToken cancellationToken)
    {
        var artifact = workbookBuilder.Build(cancellationToken);
        IVbeDebugSession? debugSession = null;
        try
        {
            debugSession = await debugSessionFactory
                .StartVisibleAsync(cancellationToken)
                .ConfigureAwait(false);
            if (debugSession is not IVbeDebugProbeControl probeControl)
            {
                throw new DebugEnvironmentProbeStartException(
                    "Native VBE readiness",
                    "The configured VBE session does not support the native Doctor probe contract.",
                    cleanupVerified: true);
            }

            return new VbeDebugEnvironmentProbeSession(
                artifact,
                debugSession,
                probeControl,
                transitionTimeout);
        }
        catch (Exception ex)
        {
            Exception? cleanupError = null;
            if (debugSession is not null)
            {
                try
                {
                    await debugSession.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    cleanupError = cleanupException;
                }
            }

            try
            {
                artifact.Dispose();
            }
            catch (Exception cleanupException)
            {
                cleanupError ??= cleanupException;
            }

            if (ex is DebugEnvironmentProbeStartException categorized)
            {
                var combinedCleanupError = categorized.CleanupException ?? cleanupError;
                throw new DebugEnvironmentProbeStartException(
                    categorized.DiagnosticName,
                    categorized.Message,
                    categorized.InnerException,
                    combinedCleanupError,
                    cleanupVerified: categorized.CleanupVerified && combinedCleanupError is null);
            }

            if (ex is IVbeDebugSessionStartFailure startFailure)
            {
                var startError = startFailure.StartException;
                var combinedCleanupError = startFailure.CleanupException ?? cleanupError;
                throw new DebugEnvironmentProbeStartException(
                    ClassifyVisibleStartFailure(startError),
                    startError.Message,
                    startError,
                    combinedCleanupError,
                    cleanupVerified: startFailure.CleanupVerified && combinedCleanupError is null);
            }

            throw new DebugEnvironmentProbeStartException(
                ClassifyVisibleStartFailure(ex),
                ex.Message,
                ex,
                cleanupError,
                cleanupVerified: false);
        }
    }

    private static string ClassifyVisibleStartFailure(Exception error)
    {
        if (error.Message.Contains("Job", StringComparison.OrdinalIgnoreCase) ||
            error.Message.Contains("ownership", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows Job ownership";
        }

        return error.Message.Contains("Excel", StringComparison.OrdinalIgnoreCase)
            ? "Excel COM availability"
            : "Owned Excel process";
    }

    private sealed class VbeDebugEnvironmentProbeSession(
        IDebugProbeWorkbookArtifact artifact,
        IVbeDebugSession debugSession,
        IVbeDebugProbeControl probeControl,
        TimeSpan transitionTimeout) : IDebugEnvironmentProbeSession
    {
        private int disposed;

        public IReadOnlyList<DiagnosticResult> StartupDiagnostics =>
            artifact.StartupDiagnostics;

        public int ProcessId => debugSession.ProcessId;

        public bool StrongProcessOwnershipEstablished =>
            probeControl.StrongProcessOwnershipEstablished;

        public async Task<IReadOnlyList<DiagnosticResult>> RunAsync(
            CancellationToken cancellationToken)
        {
            var results = new List<DiagnosticResult>();
            if (!await RunStageAsync(
                    results,
                    "VBIDE project access",
                    "The temporary workbook VBA project is accessible and in design mode.",
                    () => debugSession.OpenGeneratedWorkbookAsync(
                        artifact.WorkbookPath,
                        null,
                        cancellationToken)).ConfigureAwait(false))
            {
                return results;
            }

            if (!await RunStageAsync(
                    results,
                    $"Native Toggle Breakpoint command (ID {VbaDebugCapabilityContract.ToggleBreakpointCommandId})",
                    "The exact mapped executable line accepted a native VBE breakpoint.",
                    () => debugSession.SetNativeBreakpointsAsync(
                        [artifact.Breakpoint],
                        cancellationToken)).ConfigureAwait(false))
            {
                return results;
            }

            if (!await RunStageAsync(
                    results,
                    $"Native Run command (ID {VbaDebugCapabilityContract.RunOrContinueCommandId})",
                    "The native VBE Run Sub/UserForm command started the probe procedure.",
                    () => debugSession.RunTargetAsync(
                        artifact.Target,
                        null,
                        cancellationToken)).ConfigureAwait(false))
            {
                return results;
            }

            if (!await RunStageAsync(
                    results,
                    "VBE break mode",
                    "The VBA project entered native break mode at the exact mapped line.",
                    () => probeControl.WaitForBreakModeAsync(
                        transitionTimeout,
                        cancellationToken)).ConfigureAwait(false))
            {
                return results;
            }

            if (!await RunStageAsync(
                    results,
                    $"Native Continue command (ID {VbaDebugCapabilityContract.RunOrContinueCommandId})",
                    "The native VBE Run Sub/UserForm command continued execution from break mode.",
                    () => probeControl.ContinueTargetAsync(
                        artifact.Target,
                        cancellationToken)).ConfigureAwait(false))
            {
                return results;
            }

            if (!await RunStageAsync(
                    results,
                    "Debug procedure completion",
                    "The procedure wrote its completion marker and returned to design mode.",
                    () => probeControl.WaitForCompletionAsync(
                        artifact.CompletionMarker,
                        transitionTimeout,
                        cancellationToken)).ConfigureAwait(false))
            {
                return results;
            }

            _ = await RunStageAsync(
                results,
                $"Native breakpoint cleanup (ID {VbaDebugCapabilityContract.ToggleBreakpointCommandId})",
                "The native VBE breakpoint was cleared from the exact mapped line.",
                () => debugSession.SetNativeBreakpointsAsync(
                    [artifact.Breakpoint],
                    cancellationToken)).ConfigureAwait(false);
            return results;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            Exception? cleanupError = null;
            try
            {
                await debugSession.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cleanupError = ex;
            }

            try
            {
                artifact.Dispose();
            }
            catch (Exception ex)
            {
                cleanupError ??= ex;
            }

            if (cleanupError is not null)
            {
                throw new DebugSetupException(
                    "The native Doctor probe did not completely remove its owned Excel process and temporary artifacts.",
                    cleanupError);
            }
        }

        private static async Task<bool> RunStageAsync(
            List<DiagnosticResult> results,
            string name,
            string successMessage,
            Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
                results.Add(DiagnosticResult.Pass(name, successMessage));
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(DiagnosticResult.Fail(name, ex.Message));
                return false;
            }
        }
    }
}

internal sealed class ExcelComDebugProbeWorkbookBuilder(
    IDebugProbeWorkbookAutomation workbookAutomation,
    IBreakpointSourceMapper breakpointSourceMapper) : IDebugProbeWorkbookBuilder
{
    private const string ModuleName = "VbaToolsDoctorProbe";
    private const string ProcedureName = "RunDoctorProbe";
    private const string Marker = "vba-tools-doctor-complete";
    private const string WorkbookFileName = "DoctorProbe.xlsm";
    private const string SourceFileName = ModuleName + ".bas";

    public IDebugProbeWorkbookArtifact Build(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new DebugEnvironmentProbeStartException(
                "Temporary macro workbook",
                "The Doctor probe was canceled before temporary workbook creation started.",
                new OperationCanceledException(cancellationToken),
                cleanupVerified: true);
        }
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"vba-tools-doctor-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directoryPath);
        }
        catch (Exception ex)
        {
            Exception? directoryCleanupError = null;
            TryDeleteDirectory(directoryPath, ref directoryCleanupError);
            throw new DebugEnvironmentProbeStartException(
                "Temporary macro workbook",
                "The Doctor probe could not create its temporary working directory.",
                ex,
                directoryCleanupError,
                cleanupVerified: directoryCleanupError is null);
        }

        var workbookPath = Path.Combine(directoryPath, WorkbookFileName);
        var sourcePath = Path.Combine(directoryPath, SourceFileName);
        var sourceText = CreateSourceText();
        try
        {
            File.WriteAllText(sourcePath, sourceText, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Exception? sourceCleanupError = null;
            TryDeleteDirectory(directoryPath, ref sourceCleanupError);
            throw new DebugEnvironmentProbeStartException(
                "Temporary macro workbook",
                "The Doctor probe could not create its temporary exported VBA source.",
                ex,
                sourceCleanupError,
                cleanupVerified: sourceCleanupError is null);
        }

        IWorkbookBuildSession? buildSession = null;
        DebugEnvironmentProbeStartException? stageError = null;
        OperationCanceledException? cancellationError = null;
        Exception? cleanupError = null;
        try
        {
            buildSession = workbookAutomation.CreateMacroEnabledWorkbook(
                workbookPath,
                cancellationToken);
            try
            {
                buildSession.ImportModule(new VbaSourceFile(
                    sourcePath,
                    VbaSourceKind.StandardModule,
                    null));
            }
            catch (Exception ex)
            {
                stageError = new DebugEnvironmentProbeStartException(
                    "VBIDE project access",
                    "The hidden Excel build could not import the Doctor probe module. Enable " +
                    "'Trust access to the VBA project object model' in Excel Trust Center.",
                    ex,
                    cleanupVerified: true);
            }

            if (stageError is null)
            {
                try
                {
                    buildSession.Save();
                }
                catch (Exception ex)
                {
                    stageError = new DebugEnvironmentProbeStartException(
                        "Temporary macro workbook",
                        "The hidden Excel build could not save the imported Doctor probe workbook.",
                        ex,
                        cleanupVerified: true);
                }
            }
        }
        catch (DebugEnvironmentProbeStartException ex)
        {
            stageError = ex;
        }
        catch (OperationCanceledException ex)
        {
            cancellationError = ex;
        }
        catch (Exception ex)
        {
            stageError = new DebugEnvironmentProbeStartException(
                "Temporary macro workbook",
                "The hidden Excel build failed before the Doctor probe workbook was ready.",
                ex,
                cleanupVerified: true);
        }
        finally
        {
            if (buildSession is not null)
            {
                try
                {
                    buildSession.Dispose();
                }
                catch (Exception ex)
                {
                    cleanupError = ex;
                }
            }
        }

        if (stageError is not null || cancellationError is not null || cleanupError is not null)
        {
            TryDeleteDirectory(directoryPath, ref cleanupError);
            if (cancellationError is not null)
            {
                if (cleanupError is not null)
                {
                    throw new DebugEnvironmentProbeStartException(
                        "Temporary debug probe cleanup",
                        "The canceled hidden Excel build did not release the temporary Doctor probe cleanly.",
                        cancellationError,
                        cleanupError);
                }

                throw new DebugEnvironmentProbeStartException(
                    "Temporary macro workbook",
                    "The Doctor probe was canceled while creating its temporary workbook.",
                    cancellationError,
                    cleanupVerified: true);
            }

            if (stageError is not null)
            {
                var combinedCleanupError = stageError.CleanupException ?? cleanupError;
                throw new DebugEnvironmentProbeStartException(
                    stageError.DiagnosticName,
                    stageError.Message,
                    stageError.InnerException,
                    combinedCleanupError,
                    cleanupVerified: stageError.CleanupVerified && combinedCleanupError is null);
            }

            throw new DebugEnvironmentProbeStartException(
                "Temporary debug probe cleanup",
                "The hidden Excel build did not release the temporary Doctor probe cleanly.",
                cleanupException: cleanupError);
        }

        try
        {
            var snapshot = new DebugSourceSnapshot(
                DebugSourceSnapshot.CurrentSchemaVersion,
                ImmutableArray.Create(new DebugSourceFileSnapshot(sourcePath, sourceText)),
                null);
            var assignmentLine = Array.FindIndex(
                sourceText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'),
                line => line.Contains(Marker, StringComparison.Ordinal));
            var breakpoint = breakpointSourceMapper.Map(
                snapshot,
                new DebugSourceBreakpoint(sourcePath, assignmentLine));
            return new FileSystemDebugProbeWorkbookArtifact(
                directoryPath,
                workbookPath,
                sourcePath,
                new DebugTargetProcedure(ModuleName, ProcedureName),
                breakpoint,
                Marker);
        }
        catch (Exception ex)
        {
            cleanupError = null;
            TryDeleteDirectory(directoryPath, ref cleanupError);
            throw new DebugEnvironmentProbeStartException(
                "Native breakpoint source map",
                "The Doctor probe could not map its exported assignment to the exact generated VBIDE line.",
                ex,
                cleanupError,
                cleanupVerified: cleanupError is null);
        }
    }

    private static string CreateSourceText()
        => string.Join(
            "\r\n",
            [
                $"Attribute VB_Name = \"{ModuleName}\"",
                "Option Explicit",
                "Option Private Module",
                string.Empty,
                $"Public Sub {ProcedureName}()",
                $"    ThisWorkbook.Worksheets(1).Range(\"A1\").Value2 = \"{Marker}\"",
                "End Sub",
                string.Empty
            ]);

    private static void TryDeleteDirectory(string directoryPath, ref Exception? cleanupError)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            cleanupError ??= ex;
        }
    }

    private sealed class FileSystemDebugProbeWorkbookArtifact(
        string directoryPath,
        string workbookPath,
        string sourcePath,
        DebugTargetProcedure target,
        VbeBreakpoint breakpoint,
        string completionMarker) : IDebugProbeWorkbookArtifact
    {
        private int disposed;

        public string WorkbookPath => workbookPath;

        public DebugTargetProcedure Target => target;

        public VbeBreakpoint Breakpoint => breakpoint;

        public string CompletionMarker => completionMarker;

        public IReadOnlyList<DiagnosticResult> StartupDiagnostics =>
            [
                DiagnosticResult.Pass(
                    "Temporary macro workbook",
                    $"Created {Path.GetFileName(workbookPath)} in a strongly owned hidden Excel process."),
                DiagnosticResult.Pass(
                    "Hidden VBIDE module import",
                    $"Imported {Path.GetFileName(sourcePath)} before the visible debug Excel process started.")
            ];

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }
}
