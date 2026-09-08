using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class WorkspaceFailureCompletionTests
{
    [Fact]
    public async Task ActiveLeaseRetentionReportsItsInspectionCleanupFailure()
    {
        using var temp = TempDirectory.Create();
        var root = Path.Combine(temp.Path, "workspace");
        var sessionId = DebugSessionId.Parse("0123456789abcdef0123456789abcdef");
        Directory.CreateDirectory(Path.Combine(root, "workspaces", sessionId.Value));
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 1, sessionId = sessionId.Value,
            leaseId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", processId = process.Id,
            processStartTimeUtc = process.StartTime.ToUniversalTime().ToString("O")
        });
        var releaseFailure = new IOException("Active lease reader release could not be proved.");
        var operations = new FailedStaleCleanupOperations(sessionId, [],
            new IOException("The active lease must not acquire a deletion scope."),
            leaseFactory: () => new FailingLeaseReader(json, releaseFailure));
        var manager = new VbaDebugSessionWorkspaceManager(root, operations);

        var result = await manager.CleanupAsync(sessionId, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("still active", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(releaseFailure.Message, result.Message, StringComparison.Ordinal);
        Assert.Contains("active-lease-reader-release", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, operations.DeleteCalls);
        Assert.Equal(0, operations.DisposeCalls);
        var outcome = Assert.IsAssignableFrom<IDebugResourceOwnerEvidence>(result).CleanupOutcome!;
        Assert.Contains(outcome.CleanupFailures, item => ReferenceEquals(item.Exception, releaseFailure));
    }

    [Fact]
    public async Task CancelledCleanupRetainsTheAlreadyObservedDeleteFailureAndScopeReleaseFailure()
    {
        using var temp = TempDirectory.Create();
        var root = Path.Combine(temp.Path, "workspace");
        var sessionId = DebugSessionId.Parse("0123456789abcdef0123456789abcdef");
        Directory.CreateDirectory(Path.Combine(root, "workspaces", sessionId.Value));
        var deletionFailure = new IOException("Deletion failed before cancellation.");
        var releaseFailure = new IOException("Scope release failed after cancellation.");
        var cancellation = new OperationCanceledException("Cleanup retry delay cancelled.");
        var operations = new FailedStaleCleanupOperations(sessionId, [deletionFailure], releaseFailure, cancellation);
        var manager = new VbaDebugSessionWorkspaceManager(root, operations);

        var failure = await Assert.ThrowsAsync<DebugFailureException>(() =>
            manager.CleanupAsync(sessionId, CancellationToken.None).AsTask());

        Assert.Same(cancellation, failure.FailureOutcome.PrimaryFailure);
        Assert.Contains("DelayAsync", failure.FailureOutcome.PrimaryFailure!.StackTrace);
        Assert.Equal(new Exception[] { deletionFailure, releaseFailure },
            failure.FailureOutcome.CleanupFailures.Select(item => item.Exception));
        Assert.Equal(1, operations.DeleteCalls);
        Assert.Equal(1, operations.DisposeCalls);
        Assert.True(failure.FailureOutcome.HasUnprovedRelease);
    }

    [Fact]
    public async Task StaleCleanupRetainsEachLeaseReadAndProcessIdentityQueryHandleRelease()
    {
        using var temp = TempDirectory.Create();
        var root = Path.Combine(temp.Path, "workspace");
        var sessionId = DebugSessionId.Parse("0123456789abcdef0123456789abcdef");
        var sessionPath = Path.Combine(root, "workspaces", sessionId.Value);
        Directory.CreateDirectory(sessionPath);
        // A reused PID with a different start time is stale even while that PID is alive.
        await File.WriteAllTextAsync(Path.Combine(sessionPath, "lease.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = 1, sessionId = sessionId.Value,
                leaseId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", processId = Environment.ProcessId,
                processStartTimeUtc = DateTime.UnixEpoch.ToString("O")
            }));
        var manager = new VbaDebugSessionWorkspaceManager(root);

        var result = await manager.CleanupAsync(sessionId, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var outcome = Assert.IsAssignableFrom<IDebugResourceOwnerEvidence>(result).CleanupOutcome!;
        Assert.False(outcome.HasCleanupFailure, outcome.Describe());
        foreach (var stage in new[] { "reaper-preliminary-lease", "reaper-pinned-lease" })
        {
            Assert.Contains(outcome.Evidence, item => item.Stage == stage && item.Resource == "lease stream"
                && item.Kind == DebugResourceKind.Handle && item.Released);
            Assert.Contains(outcome.Evidence, item => item.Stage == stage && item.Resource == "lease process identity"
                && item.Kind == DebugResourceKind.Handle && item.Released && item.ProcessId == Environment.ProcessId);
        }
    }

    [Fact]
    public async Task ClaimedLeaseRetainsItsProcessIdentityQueryReleaseEvidence()
    {
        using var temp = TempDirectory.Create();
        var manager = new VbaDebugSessionWorkspaceManager(Path.Combine(temp.Path, "workspace"));
        var lease = await manager.ClaimAsync(
            DebugSessionId.Parse("0123456789abcdef0123456789abcdef"), CancellationToken.None);

        await lease.DisposeAsync();

        var outcome = Assert.IsAssignableFrom<IDebugResourceOwnerEvidence>(lease).CleanupOutcome!;
        Assert.False(outcome.HasCleanupFailure, outcome.Describe());
        Assert.Contains(outcome.Evidence, item => item.Stage == "session-metadata-query"
            && item.Kind == DebugResourceKind.Handle && item.Released
            && item.ProcessId == Environment.ProcessId);
    }

    [Fact]
    public async Task StaleCleanupRetainsEveryBoundedDeleteFailureAndItsScopeReleaseFailure()
    {
        using var temp = TempDirectory.Create();
        var sessionId = DebugSessionId.Parse("0123456789abcdef0123456789abcdef");
        var root = Path.Combine(temp.Path, "workspace");
        var retainedPath = Path.Combine(root, "workspaces", sessionId.Value);
        Directory.CreateDirectory(retainedPath);
        var deletionFailures = new[] { new IOException("First delete failed."), new IOException("Final delete failed.") };
        var releaseFailure = new IOException("Reaper scope release failed.");
        var operations = new FailedStaleCleanupOperations(sessionId, deletionFailures, releaseFailure);
        var manager = new VbaDebugSessionWorkspaceManager(root, operations);

        var result = await manager.CleanupAsync(sessionId, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(retainedPath, result.RetainedPath);
        var outcome = Assert.IsAssignableFrom<IDebugResourceOwnerEvidence>(result).CleanupOutcome!;
        Assert.Equal(deletionFailures.Cast<Exception>().Append(releaseFailure),
            outcome.CleanupFailures.Select(item => item.Exception));
        Assert.True(outcome.HasUnprovedRelease);
        Assert.False(outcome.OnlyFileDeletionFailed);
        Assert.Contains("five seconds", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(releaseFailure.Message, result.Message, StringComparison.Ordinal);
        Assert.Equal(2, operations.DeleteCalls);
        Assert.Equal(1, operations.DisposeCalls);
        Assert.Equal(TimeSpan.FromSeconds(5), operations.Elapsed);
    }

    [Fact]
    public async Task GenerationCleanupContinuesAfterEachFaultAndRetainsTheSameMultifaultOutcome()
    {
        using var temp = TempDirectory.Create();
        var descendantFailure = new IOException("Descendant handle release unproved.");
        var deletionFailure = new IOException("Generation tree deletion failed.");
        var remainingFailure = new IOException("Remaining handle release unproved.");
        var attempts = new List<string>();
        var injected = new HashSet<string>();
        var manager = new VbaDebugSessionWorkspaceManager(
            Path.Combine(temp.Path, "workspace"), cleanupOperations: null,
            beforeDeleteOwnedTree: path =>
            {
                if (Path.GetFileName(Path.GetDirectoryName(path)) == "generations")
                {
                    attempts.Add("generation-delete");
                    throw deletionFailure;
                }
            },
            releaseOwnedHandle: (handle, stage) =>
            {
                // Perform real cleanup while simulating loss of its success acknowledgement.
                new DebugNativeHandleRelease(handle).Release();
                attempts.Add(stage);
                if (injected.Add(stage))
                {
                    if (stage == "generation-descendant-handles") { throw descendantFailure; }
                    if (stage == "generation-remaining-handles") { throw remainingFailure; }
                }
            });
        await using var lease = await manager.ClaimAsync(
            DebugSessionId.Parse("0123456789abcdef0123456789abcdef"), CancellationToken.None);
        var generation = lease.CreateGenerationWorkspace(DebugGenerationId.Initial, "debug.xlsm");

        var first = await Record.ExceptionAsync(() => generation.DisposeAsync().AsTask());
        var attemptCount = attempts.Count;
        var repeated = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(
            () => Record.ExceptionAsync(() => generation.DisposeAsync().AsTask()))));

        var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(first).FailureOutcome;
        Assert.Equal(new[] { descendantFailure, deletionFailure, remainingFailure },
            outcome.CleanupFailures.Select(failure => failure.Exception));
        Assert.True(outcome.HasUnprovedRelease);
        Assert.False(outcome.OnlyFileDeletionFailed);
        Assert.True(attempts.LastIndexOf("generation-descendant-handles") < attempts.IndexOf("generation-delete"));
        Assert.True(attempts.IndexOf("generation-delete") < attempts.IndexOf("generation-remaining-handles"));
        Assert.All(repeated, exception => Assert.Same(first, exception));
        Assert.Equal(attemptCount, attempts.Count);
    }

    [Fact]
    public async Task GenerationClaimFailureRetainsItsCauseAndLockedTreeCleanupFailure()
    {
        using var temp = TempDirectory.Create();
        var primary = new InvalidOperationException("Generation setup failed.");
        FileStream? lockedFile = null;
        var manager = new VbaDebugSessionWorkspaceManager(
            Path.Combine(temp.Path, "workspace"), cleanupOperations: null,
            afterCreateDirectoryBeforeOpen: path =>
            {
                if (Path.GetFileName(Path.GetDirectoryName(path)) == "generations")
                {
                    lockedFile = new FileStream(Path.Combine(path, "locked.tmp"),
                        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                    throw primary;
                }
            });
        await using var lease = await manager.ClaimAsync(
            DebugSessionId.Parse("0123456789abcdef0123456789abcdef"), CancellationToken.None);
        try
        {
            var exception = Record.Exception(() => lease.CreateGenerationWorkspace(
                DebugGenerationId.Initial, "debug.xlsm"));

            var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(exception).FailureOutcome;
            Assert.Same(primary, outcome.PrimaryFailure);
            Assert.Contains(nameof(GenerationClaimFailureRetainsItsCauseAndLockedTreeCleanupFailure),
                outcome.PrimaryFailure!.StackTrace);
            Assert.True(outcome.OnlyFileDeletionFailed);
            Assert.Contains(outcome.CleanupFailures, failure => failure.Kind == DebugResourceKind.FileSystem);
        }
        finally
        {
            lockedFile?.Dispose();
        }
    }

    [Fact]
    public async Task LeaseDeletionFailureRetainsPositiveLeaseAndDirectoryHandleEvidence()
    {
        using var temp = TempDirectory.Create();
        var deletionFailure = new InvalidOperationException("Pinned session deletion failed.");
        var manager = new VbaDebugSessionWorkspaceManager(
            Path.Combine(temp.Path, "workspace"), cleanupOperations: null,
            beforeDeleteOwnedTree: _ => throw deletionFailure);
        var lease = await manager.ClaimAsync(
            DebugSessionId.Parse("0123456789abcdef0123456789abcdef"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => lease.DisposeAsync().AsTask());

        var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(exception).FailureOutcome;
        Assert.Same(outcome, Assert.IsAssignableFrom<IDebugResourceOwnerEvidence>(lease).CleanupOutcome);
        Assert.True(outcome.OnlyFileDeletionFailed);
        Assert.Contains(outcome.Evidence, evidence => evidence.Stage == "session-lease-handle" && evidence.Released);
        Assert.Contains(outcome.Evidence, evidence => evidence.Stage == "session-remaining-handles" && evidence.Released);
        Assert.Contains(outcome.CleanupFailures, failure => ReferenceEquals(failure.Exception, deletionFailure));
    }

    [Fact]
    public async Task GenerationDeletionFailureProvesItsHandleReleaseAndRetainsOnlyItsOwnedPath()
    {
        using var temp = TempDirectory.Create();
        var deletionFailure = new IOException("Pinned generation deletion failed.");
        var manager = new VbaDebugSessionWorkspaceManager(
            Path.Combine(temp.Path, "workspace"), cleanupOperations: null,
            beforeDeleteOwnedTree: path =>
            {
                if (Path.GetFileName(Path.GetDirectoryName(path)) == "generations")
                {
                    throw deletionFailure;
                }
            });
        await using var lease = await manager.ClaimAsync(
            DebugSessionId.Parse("0123456789abcdef0123456789abcdef"), CancellationToken.None);
        var generation = lease.CreateGenerationWorkspace(DebugGenerationId.Initial, "debug.xlsm");

        var exception = await Record.ExceptionAsync(() => generation.DisposeAsync().AsTask());

        var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(exception).FailureOutcome;
        Assert.Same(outcome, Assert.IsAssignableFrom<IDebugResourceOwnerEvidence>(generation).CleanupOutcome);
        Assert.True(outcome.OnlyFileDeletionFailed);
        Assert.False(outcome.HasUnprovedRelease);
        Assert.Contains(outcome.CleanupFailures, failure => ReferenceEquals(failure.Exception, deletionFailure));
        Assert.Contains(outcome.Evidence, evidence => evidence.Stage == "generation-descendant-handles" && evidence.Released);
        Assert.Contains(outcome.Evidence, evidence => evidence.Stage == "generation-remaining-handles" && evidence.Released);
        Assert.All(outcome.Evidence.Where(evidence => !evidence.Released),
            evidence => Assert.Equal(generation.GenerationWorkspacePath, evidence.RetainedPath));
    }

    [Fact]
    public async Task FailedLeaseDisposalRetainsItsOutcomeForRepeatedAndConcurrentCallers()
    {
        using var temp = TempDirectory.Create();
        var deletionFailure = new InvalidOperationException("Pinned session deletion failed.");
        var deletionAttempts = 0;
        var manager = new VbaDebugSessionWorkspaceManager(
            Path.Combine(temp.Path, "workspace"),
            cleanupOperations: null,
            beforeDeleteOwnedTree: _ =>
            {
                Interlocked.Increment(ref deletionAttempts);
                throw deletionFailure;
            });
        var lease = await manager.ClaimAsync(
            DebugSessionId.Parse("0123456789abcdef0123456789abcdef"),
            CancellationToken.None);

        var first = await Record.ExceptionAsync(() => lease.DisposeAsync().AsTask());
        var repeated = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(
            () => Record.ExceptionAsync(() => lease.DisposeAsync().AsTask()))));

        Assert.NotNull(first);
        Assert.All(repeated, exception => Assert.Same(first, exception));
        Assert.Equal(1, deletionAttempts);
        Assert.True(Directory.Exists(lease.SessionWorkspacePath));
    }
    private sealed class FailingLeaseReader(string json, Exception failure)
        : MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)), IDebugResourceOwnerEvidence
    {
        public DebugFailureOutcome? CleanupOutcome { get; private set; }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            var completion = new DebugFailureCompletion();
            completion.AddFailure("active-lease-reader-release", "active lease reader", DebugResourceKind.Handle, failure);
            completion.AddEvidence(new("active-lease-reader-release", "active lease reader", DebugResourceKind.Handle,
                false, "The reader owner could not prove release."));
            CleanupOutcome = completion.Complete();
            CleanupOutcome.Throw();
        }
    }

    private sealed class FailedStaleCleanupOperations(DebugSessionId sessionId,
        IReadOnlyList<IOException> deletionFailures, Exception releaseFailure,
        OperationCanceledException? delayFailure = null,
        Func<Stream>? leaseFactory = null) : IVbaDebugWorkspaceCleanupOperations
    {
        public TimeSpan Elapsed { get; private set; }
        public int DeleteCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public bool IsReparsePoint(string path) => false;
        public Stream OpenSessionLeaseStream(DebugSessionId id) => OpenLease();
        public IVbaDebugWorkspaceCleanupScope OpenSessionCleanupScope(DebugSessionId id) => new Scope(this);
        public long GetTimestamp() => Elapsed.Ticks;
        public TimeSpan GetElapsedTime(long started) => Elapsed - TimeSpan.FromTicks(started);
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (delayFailure is not null) { throw delayFailure; }
            Elapsed = TimeSpan.FromSeconds(5);
            return ValueTask.CompletedTask;
        }
        private Exception NextDeletionFailure() => deletionFailures[DeleteCalls++];
        private Exception ScopeReleaseFailure => releaseFailure;
        private Stream OpenLease() => leaseFactory?.Invoke() ?? new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"sessionId\":\"" + sessionId.Value +
            "\",\"leaseId\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"processId\":2147483647," +
            "\"processStartTimeUtc\":\"2020-01-02T03:04:05.0000000Z\"}"));

        private sealed class Scope(FailedStaleCleanupOperations owner)
            : IVbaDebugWorkspaceCleanupScope, IDebugResourceOwnerEvidence
        {
            public DebugFailureOutcome? CleanupOutcome { get; private set; }
            public Stream OpenLeaseStream() => owner.OpenLease();
            public void DeleteDirectory() => throw owner.NextDeletionFailure();
            public void Dispose()
            {
                owner.DisposeCalls++;
                var completion = new DebugFailureCompletion();
                completion.AddFailure("reaper-scope-release", "reaper scope", DebugResourceKind.Handle, owner.ScopeReleaseFailure);
                completion.AddEvidence(new("reaper-scope-release", "reaper scope", DebugResourceKind.Handle,
                    false, "The scope owner could not prove release."));
                CleanupOutcome = completion.Complete();
                CleanupOutcome.Throw();
            }
        }
    }
}
