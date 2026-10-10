using VbaDebugAdapter.Build;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using System.Globalization;
using System.Text.Json;

namespace VbaDebugAdapter.Cli;

/// <summary>Prepares and runs immutable sources in a retained source workbook.</summary>
internal sealed class SourceWorkbookVbaDebugLaunchService(
    DebugSourceAdmission admission,
    VbaDevSourceWorkbookResolver workbookResolver,
    IManagedDebugPreparationProcess preparationProcess,
    ISourceVbeDebugSessionFactory sessions) : IStandaloneVbaDebugLaunchService
{
    public async Task<IPreparedDebugLaunchPlan> PrepareAsync(string vbaDevPath,
        IVbaDebugSessionWorkspaceLease workspaceLease, StandaloneVbaDebugLaunchRequest request,
        DebugRestartLaunchBinding? restartBinding, CancellationToken cancellationToken,
        IDebugLifecycleSink? lifecycleSink = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workspaceLease);
        PreparedSourcePlan? plan = null;
        IVbaDebugGenerationWorkspace? workspace = null;
        ISourceVbeDebugSession? native = null;
        var borrowedRestart = false;
        var nativeAcquisitionAttempted = false;
        var sourceRejected = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var retention = workspaceLease as IVbaDebugSessionWorkspaceRetention
                ?? throw new DebugSetupException(
                    "The source debug workspace cannot retain an unproved preparation companion safely.");
            AdmittedDebugSourceSnapshot source;
            try
            {
                source = admission.Admit(request.SourceSnapshot, request.ModuleName,
                    request.ProcedureName,
                    DebugGenerationId.FromValue(request.RestartPreparation?.Generation.Value ?? 0));
            }
            catch (DebugSourceRejectedException) { sourceRejected = true; throw; }
            var description = await workbookResolver.ResolveAsync(vbaDevPath, request.ProjectRoot,
                request.DocumentName, cancellationToken).ConfigureAwait(false);
            if (!Path.GetFileName(description.WorkbookPath).Equals(request.WorkbookFileName,
                    StringComparison.OrdinalIgnoreCase)
                || request.SourceWorkbookPath is { } expectedPath
                    && !Path.GetFullPath(expectedPath).Equals(description.WorkbookPath,
                        StringComparison.OrdinalIgnoreCase))
                throw new DebugSetupException("The selected source workbook does not match the CLI-resolved document.");
            restartBinding?.ValidateLaunch(request, description.ProjectRoot, source.Target, workspaceLease.SessionId);
            workspace = workspaceLease.CreateGenerationWorkspace(source.GenerationId,
                Path.GetFileName(description.WorkbookPath));
            source.BuildSources.MaterializeInto(workspace);
            workspace.SealSourceSnapshot();
            workspace.VerifySourceSnapshot();
            if (restartBinding is not null)
            {
                if (restartBinding.BoundSession is not SourceRunningSession retained
                    || !retained.WorkbookPath.Equals(description.WorkbookPath, StringComparison.OrdinalIgnoreCase))
                    throw new DebugSetupException("The restart is not bound to the retained source workbook session.");
                native = retained.Native;
                borrowedRestart = true;
            }
            else
            {
                nativeAcquisitionAttempted = true;
                native = await sessions.AttachOrOpenAsync(description.WorkbookPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            var live = await native.InspectAsync(cancellationToken).ConfigureAwait(false);
            ValidateLiveBinding(description.WorkbookPath, native, live);
            if (source.RequiresConditionalCompilationVerification)
            {
                var hostFacts = await native.GetCompilationHostFactsAsync(cancellationToken)
                    .ConfigureAwait(false);
                source.VerifyConditionalCompilation(
                    new DebugCompilationEnvironmentFactory().CreateForLiveHost(hostFacts));
            }
            var snapshot = new PreparedDebugLaunchPlanSnapshot(source, workspace.GenerationWorkspacePath,
                new(description.ProjectRoot, description.DocumentName, request.WorkbookFileName,
                    request.ModuleName, request.ProcedureName, request.RestartPreparation), restartBinding);
            plan = new PreparedSourcePlan(snapshot, description.WorkbookPath, native,
                borrowedRestart ? (SourceRunningSession)restartBinding!.BoundSession : null,
                workspace, retention, preparationProcess, vbaDevPath, lifecycleSink, cancellationToken);
            workspace = null;
            native = null;
            await plan.BeginAsync().ConfigureAwait(false);
            return plan;
        }
        catch (Exception failure)
        {
            var completion = new DebugFailureCompletion(failure);
            if (plan is not null)
            {
                try { await plan.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { completion.AddFailure("source-preparation-cleanup", "prepared source", DebugResourceKind.Handle, cleanup); }
                if (plan.CleanupOutcome is { } outcome) completion.Merge(outcome);
            }
            else
            {
                if (native is not null && !borrowedRestart)
                    await ReleaseBindingAsync(native, completion).ConfigureAwait(false);
                if (workspace is not null) await ReleaseWorkspaceAsync(workspace, completion).ConfigureAwait(false);
                if (native is null && !nativeAcquisitionAttempted)
                    foreach (var kind in new[] { DebugResourceKind.Process, DebugResourceKind.Com, DebugResourceKind.Handle })
                        completion.AddEvidence(new("source-admission", "source session", kind, true,
                            "No live source-session ownership was acquired at this boundary."));
                else if (native is null)
                    foreach (var kind in new[] { DebugResourceKind.Com, DebugResourceKind.Handle })
                        if (failure is not IDebugFailureEvidence evidence
                            || evidence.FailureOutcome.HasUnprovedRelease
                            || !evidence.FailureOutcome.Evidence.Any(item => item.Kind == kind && item.Released))
                            completion.AddEvidence(new("source-acquisition", "source session", kind, false,
                                "The failed native acquisition did not prove its partial binding/dispatcher release."));
            }
            var result = completion.Complete();
            if (sourceRejected) throw new DebugSourceRejectedPreparationException(result);
            result.ThrowWithEvidence();
            throw;
        }
    }

    private static void ValidateLiveBinding(string expectedPath, ISourceVbeDebugSession native,
        SourceVbeDebugSessionInspection live)
    {
        if (!Path.GetFullPath(live.WorkbookPath).Equals(expectedPath, StringComparison.OrdinalIgnoreCase)
            || live.ProcessId != native.ProcessId || live.ProcessStartUtcTicks != native.ProcessStartUtcTicks)
            throw new DebugSetupException("The exact live source workbook/process binding changed.");
    }

    private static async Task ReleaseBindingAsync(ISourceVbeDebugSession native, DebugFailureCompletion completion)
    {
        try { await native.DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanup) { completion.AddFailure("source-binding-release", "retained Excel binding", DebugResourceKind.Com, cleanup, native.ProcessId); }
        completion.Merge(CompleteBindingEvidence(native));
    }

    private static DebugFailureOutcome CompleteBindingEvidence(ISourceVbeDebugSession native)
    {
        var completion = new DebugFailureCompletion();
        var owner = (native as IDebugResourceOwnerEvidence)?.CleanupOutcome;
        if (owner is not null) completion.Merge(owner);
        foreach (var kind in new[] { DebugResourceKind.Process, DebugResourceKind.Com, DebugResourceKind.Handle })
            if (owner is null || !owner.Evidence.Any(item => item.Kind == kind
                    && item.ProcessId == native.ProcessId))
                completion.AddEvidence(new("source-binding-release", "retained Excel binding", kind,
                    false, "The native source binding supplied no terminal evidence for this resource; "
                        + "Excel lifetime was not taken over or terminated.", native.ProcessId));
        return completion.Complete();
    }

    private static async Task ReleaseWorkspaceAsync(IVbaDebugGenerationWorkspace workspace, DebugFailureCompletion completion)
    {
        try { await workspace.DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanup) { completion.AddFailure("source-snapshot-cleanup", workspace.GenerationWorkspacePath,
            DebugResourceKind.FileSystem, cleanup, retainedPath: workspace.GenerationWorkspacePath); }
        if (workspace is IDebugResourceOwnerEvidence { CleanupOutcome: { } outcome }) completion.Merge(outcome);
        else completion.AddEvidence(new("source-snapshot-cleanup", workspace.GenerationWorkspacePath,
            DebugResourceKind.Handle, false, "The snapshot owner supplied no release evidence.", RetainedPath: workspace.GenerationWorkspacePath));
    }

    private sealed class PreparedSourcePlan : IPreparedDebugLaunchPlan, IDebugResourceOwnerEvidence
    {
        private readonly string workbookPath;
        private readonly ISourceVbeDebugSession native;
        private readonly SourceRunningSession? retained;
        private readonly IVbaDebugGenerationWorkspace workspace;
        private readonly IVbaDebugSessionWorkspaceRetention retention;
        private readonly IManagedDebugPreparationProcess process;
        private readonly string executable;
        private readonly IDebugLifecycleSink? sink;
        private readonly CancellationTokenSource cancellation;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> continuation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly DebugPreparationProcessBinding binding;
        private readonly object gate = new();
        private Task<ManagedDebugPreparationProcessResult>? invocation;
        private Task<IStandaloneVbaDebugRunningSession>? commit;
        private Task? disposal;
        private bool committed;
        private bool consumed;
        private bool executionStarted;
        private bool restartReleased;
        private bool companionRetentionArmed;
        private bool invocationAttempted;
        private DebugFailureOutcome? synchronousStartFailure;

        internal PreparedSourcePlan(PreparedDebugLaunchPlanSnapshot snapshot, string workbookPath,
            ISourceVbeDebugSession native, SourceRunningSession? retained,
            IVbaDebugGenerationWorkspace workspace, IVbaDebugSessionWorkspaceRetention retention,
            IManagedDebugPreparationProcess process,
            string executable, IDebugLifecycleSink? sink, CancellationToken token)
        {
            Snapshot = snapshot;
            this.workbookPath = workbookPath;
            this.native = native;
            this.retained = retained;
            this.workspace = workspace;
            this.retention = retention;
            this.process = process;
            this.executable = executable;
            this.sink = sink;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            binding = new(Guid.NewGuid().ToString("N"), workbookPath, native.ProcessId, native.ProcessStartUtcTicks);
        }

        public PreparedDebugLaunchPlanSnapshot Snapshot { get; }
        public bool RestartSessionReleased => restartReleased;
        public DebugFailureOutcome? CleanupOutcome { get; private set; }

        internal async Task BeginAsync()
        {
            var args = new[] { "--cancellation-transport", "stdin-v1", "prepare-debug",
                "--project", Snapshot.LaunchSettings.CanonicalProjectRoot,
                "--document", Snapshot.LaunchSettings.DocumentName,
                "--source-snapshot", workspace.SourceSnapshotPath,
                "--generation", binding.GenerationId,
                "--excel-process-id", binding.ExcelProcessId.ToString(CultureInfo.InvariantCulture),
                "--excel-process-start-utc-ticks", binding.ExcelProcessStartUtcTicks.ToString(CultureInfo.InvariantCulture) };
            companionRetentionArmed = true;
            retention.ArmForUnprovedCompanion(Snapshot.GenerationId, workspace.GenerationWorkspacePath,
                "The preparation companion may be reading this source generation or performing code recovery.");
            invocationAttempted = true;
            try
            {
                invocation = process.RunAsync(executable, args, binding,
                    (warning, token) => sink is IDebugWorkbookConfirmationSink confirmation
                        ? confirmation.ConfirmReplacementAsync(Snapshot.GenerationId, workbookPath, warning, token)
                        : Task.FromResult(false),
                    async token =>
                    {
                        ready.TrySetResult();
                        return await continuation.Task.WaitAsync(token).ConfigureAwait(false);
                    }, cancellation.Token);
            }
            catch (Exception failure)
            {
                synchronousStartFailure = (failure as IDebugFailureEvidence)?.FailureOutcome;
                throw;
            }
            var first = await Task.WhenAny(ready.Task, invocation).ConfigureAwait(false);
            if (first == invocation) ValidatePrepared(await invocation.ConfigureAwait(false));
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ready.Task.IsCompletedSuccessfully)
                throw new DebugSetupException("Source preparation ended without captured replacement readiness.");
        }

        public Task<IStandaloneVbaDebugRunningSession> CommitAsync(DebugRestartLaunchBinding? restartBinding,
            CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (consumed) throw new InvalidOperationException("The source preparation plan was already consumed.");
                consumed = true;
                return commit = CommitCoreAsync(restartBinding, cancellationToken);
            }
        }

        private async Task<IStandaloneVbaDebugRunningSession> CommitCoreAsync(DebugRestartLaunchBinding? restartBinding,
            CancellationToken callerToken)
        {
            using var commitCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken, cancellation.Token);
            var cancellationToken = commitCancellation.Token;
            if ((Snapshot.RestartBinding is null) != (restartBinding is null)
                || Snapshot.RestartBinding is { } expected
                    && (restartBinding is null || !expected.HasSameIdentityAs(restartBinding)
                        || !expected.IsBoundSessionCurrent))
                throw new DebugSetupException("The prepared source-workbook restart binding is stale.");
            cancellationToken.ThrowIfCancellationRequested();
            cancellation.Token.ThrowIfCancellationRequested();
            ValidateLiveBinding(workbookPath, native, await native.InspectAsync(cancellationToken).ConfigureAwait(false));
            await native.ResetExecutionAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            restartReleased = retained is not null;
            continuation.TrySetResult(true);
            var result = await invocation!.ConfigureAwait(false);
            ValidatePrepared(result);
            cancellationToken.ThrowIfCancellationRequested();
            workspace.VerifySourceSnapshot();
            if (!Snapshot.MappedBreakpoints.IsEmpty)
                await native.SetNativeBreakpointsAsync(Snapshot.MappedBreakpoints, cancellationToken).ConfigureAwait(false);
            executionStarted = true;
            await native.RunTargetAsync(Snapshot.Target, sink, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var completion = new DebugFailureCompletion();
            completion.Merge(result.CleanupOutcome);
            await ReleaseWorkspaceAsync(workspace, completion).ConfigureAwait(false);
            CleanupOutcome = completion.Complete();
            CleanupOutcome.ThrowWithEvidence();
            var running = retained ?? new SourceRunningSession(native, workbookPath);
            running.ApplyGeneration(Snapshot);
            committed = true;
            cancellation.Dispose();
            return running;
        }

        private void ValidatePrepared(ManagedDebugPreparationProcessResult result)
        {
            var ownerOutcome = CompleteCompanionEvidence(result.CleanupOutcome);
            if (!ConfirmCompanionRelease(ownerOutcome))
                throw new DebugFailureException(ownerOutcome);
            ownerOutcome.ThrowWithEvidence();
            if (result.ExitCode != 0)
                throw new DebugSetupException($"Source workbook preparation exited with code {result.ExitCode}. {result.StandardError.TrimEnd()}");
            using var json = JsonDocument.Parse(result.StandardOutput);
            var root = json.RootElement;
            var names = new HashSet<string>(StringComparer.Ordinal);
            var expectedNames = new HashSet<string>(StringComparer.Ordinal)
            {
                "type", "schemaVersion", "generationId", "workbookPath", "excelProcessId",
                "excelProcessStartUtcTicks", "importedSourceFileCount", "warnings"
            };
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Any(property => !expectedNames.Contains(property.Name) || !names.Add(property.Name))
                || !names.SetEquals(expectedNames)
                || new[] { "type", "schemaVersion", "generationId", "workbookPath" }
                    .Any(name => root.GetProperty(name).ValueKind != JsonValueKind.String)
                || root.GetProperty("excelProcessId").ValueKind != JsonValueKind.Number
                || !root.GetProperty("excelProcessId").TryGetInt32(out var processId)
                || root.GetProperty("excelProcessStartUtcTicks").ValueKind != JsonValueKind.Number
                || !root.GetProperty("excelProcessStartUtcTicks").TryGetInt64(out var startTicks)
                || root.GetProperty("importedSourceFileCount").ValueKind != JsonValueKind.Number
                || !root.GetProperty("importedSourceFileCount").TryGetInt32(out var importedCount)
                || importedCount < 0
                || root.GetProperty("warnings").ValueKind != JsonValueKind.Array
                || root.GetProperty("warnings").EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                throw new DebugSetupException("The source preparation completion is not a closed, unambiguous schema-1.0 receipt.");
            var reportedPath = root.GetProperty("workbookPath").GetString()!;
            if (root.GetProperty("type").GetString() != "debugWorkbookPrepared"
                || root.GetProperty("schemaVersion").GetString() != "1.0"
                || root.GetProperty("generationId").GetString() != binding.GenerationId
                || !Path.IsPathFullyQualified(reportedPath)
                || !Path.GetFullPath(reportedPath).Equals(workbookPath, StringComparison.OrdinalIgnoreCase)
                || processId != native.ProcessId || startTicks != native.ProcessStartUtcTicks)
                throw new DebugSetupException("The source preparation completion does not match its captured live binding.");
        }

        public ValueTask DisposeAsync()
        {
            lock (gate) return new(disposal ??= DisposeCoreAsync());
        }

        private async Task DisposeCoreAsync()
        {
            if (committed) return;
            consumed = true;
            continuation.TrySetResult(false);
            cancellation.Cancel();
            if (commit is not null)
            {
                // A native operation may already have entered the STA. Its caller retains
                // the original failure; disposal must observe its end before releasing COM.
                try { _ = await commit.ConfigureAwait(false); }
                catch { }
                if (committed) return;
            }
            var completion = new DebugFailureCompletion();
            var childReleased = !companionRetentionArmed;
            if (invocation is not null)
            {
                try
                {
                    var result = await invocation.ConfigureAwait(false);
                    var ownerOutcome = CompleteCompanionEvidence(result.CleanupOutcome);
                    completion.Merge(ownerOutcome);
                    childReleased = ConfirmCompanionRelease(ownerOutcome);
                }
                catch (Exception cleanup)
                {
                    completion.AddFailure("source-preparation-completion", executable, DebugResourceKind.Process, cleanup);
                    if (cleanup is IDebugFailureEvidence evidence)
                    {
                        var ownerOutcome = CompleteCompanionEvidence(evidence.FailureOutcome);
                        completion.Merge(ownerOutcome);
                        try { childReleased = ConfirmCompanionRelease(ownerOutcome); }
                        catch (Exception markerFailure)
                        {
                            completion.AddFailure("source-retention-clear", workspace.GenerationWorkspacePath,
                                DebugResourceKind.FileSystem, markerFailure,
                                retainedPath: workspace.GenerationWorkspacePath);
                        }
                    }
                }
            }
            else if (synchronousStartFailure is not null || !invocationAttempted)
            {
                var unstarted = new DebugFailureCompletion();
                foreach (var kind in new[] { DebugResourceKind.Process, DebugResourceKind.Handle })
                    unstarted.AddEvidence(new("source-companion-not-started", executable, kind, true,
                        "The retention boundary failed before attempting to acquire a preparation child."));
                var terminalOutcome = CompleteCompanionEvidence(synchronousStartFailure ?? unstarted.Complete());
                completion.Merge(terminalOutcome);
                try { childReleased = ConfirmCompanionRelease(terminalOutcome); }
                catch (Exception markerFailure)
                {
                    completion.AddFailure("source-retention-clear", workspace.GenerationWorkspacePath,
                        DebugResourceKind.FileSystem, markerFailure,
                        retainedPath: workspace.GenerationWorkspacePath);
                }
            }
            if (executionStarted)
            {
                try { await native.ResetExecutionAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false); }
                catch (Exception cleanup) { completion.AddFailure("source-stop", workbookPath, DebugResourceKind.Observation, cleanup, native.ProcessId); }
            }
            if (retained is null) await ReleaseBindingAsync(native, completion).ConfigureAwait(false);
            if (childReleased) await ReleaseWorkspaceAsync(workspace, completion).ConfigureAwait(false);
            else completion.AddEvidence(new("source-snapshot-retention", workspace.GenerationWorkspacePath,
                DebugResourceKind.FileSystem, false, "Companion terminal release was not proved; retain this snapshot for pending recovery.",
                RetainedPath: workspace.GenerationWorkspacePath));
            cancellation.Dispose();
            CleanupOutcome = completion.Complete();
            CleanupOutcome.ThrowWithEvidence();
        }

        private bool ConfirmCompanionRelease(DebugFailureOutcome outcome)
        {
            if (!companionRetentionArmed) return true;
            if (!retention.ConfirmCompanionRelease(Snapshot.GenerationId, outcome)) return false;
            companionRetentionArmed = false;
            return true;
        }

        private DebugFailureOutcome CompleteCompanionEvidence(DebugFailureOutcome outcome)
        {
            var completion = new DebugFailureCompletion();
            completion.Merge(outcome);
            foreach (var kind in new[] { DebugResourceKind.Process, DebugResourceKind.Handle })
                if (!outcome.Evidence.Any(item => item.Kind == kind))
                    completion.AddEvidence(new("source-companion-release", executable, kind, false,
                        "The preparation companion supplied no terminal release evidence for this resource.",
                        RetainedPath: workspace.GenerationWorkspacePath));
            return completion.Complete();
        }
    }

    private sealed class SourceRunningSession(ISourceVbeDebugSession native, string workbookPath)
        : IStandaloneVbaDebugRunningSession, IDebugResourceOwnerEvidence
    {
        private PreparedDebugLaunchPlanSnapshot? snapshot;
        internal ISourceVbeDebugSession Native { get; } = native;
        internal string WorkbookPath { get; } = workbookPath;
        public Task<SourceVbeDebugSessionCompletion>? SourceWorkbookCompletion => Native.Completion;
        public Task<int> Completion { get; } = AwaitCompletionAsync(native.Completion);
        public int ProcessId => Native.ProcessId;
        public string TargetModuleName => snapshot!.Target.ModuleName;
        public string TargetProcedureName => snapshot!.Target.ProcedureName;
        public IReadOnlyList<VbeBreakpoint> VerifiedBreakpoints => snapshot!.MappedBreakpoints;
        public DebugFailureOutcome? CleanupOutcome => CompleteBindingEvidence(Native);
        internal void ApplyGeneration(PreparedDebugLaunchPlanSnapshot generation) => snapshot = generation;
        public ValueTask TerminateAsync() => new(Native.ResetExecutionAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
        public ValueTask DisposeAsync() => Native.DisposeAsync();
        private static async Task<int> AwaitCompletionAsync(Task<SourceVbeDebugSessionCompletion> completion)
            => (await completion.ConfigureAwait(false)).ProcessExitCode ?? 0;
    }
}
