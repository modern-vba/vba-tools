using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using VbaDev.App.Projects;
using VbaDev.Domain;
using VbaDev.Infrastructure.FileSystem;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class ProjectManifestMutationCoordinatorTests
{
    [Fact]
    public async Task CommitRejectsANonParticipatingEditAfterTheRebaseSnapshot()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp, new VbaProjectReference("Remove Me"));
        var store = new JsonProjectManifestStore();
        var coordinator = new ProjectManifestMutationCoordinator();

        var exception = await Assert.ThrowsAsync<ProjectManifestMutationException>(() =>
            coordinator.ExecuteAsync(
                root,
                ProjectManifestMutationCommand.ReferenceRemove,
                snapshot =>
                {
                    var planned = ProjectManifestEditor.Clone(snapshot.Manifest);
                    planned.Documents["Book1"].References.Clear();

                    var external = ProjectManifestEditor.Clone(snapshot.Manifest);
                    external.Documents["Book1"].References.Add(
                        new VbaProjectReference("External Edit"));
                    store.Save(root, external);

                    return ProjectManifestMutationPlan<string>.Commit(planned, "removed");
                },
                CancellationToken.None));

        Assert.Equal("manifestExternalEditConflict", exception.Code);
        var manifest = store.Load(Path.Combine(root, ProjectManifest.ManifestFileName));
        Assert.Equal(
            ["Remove Me", "External Edit"],
            manifest.Documents["Book1"].References.Select(reference => reference.Name));
    }

    [Fact]
    public async Task NoOpRejectsANonParticipatingEditAfterTheRebaseSnapshot()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp, new VbaProjectReference("Keep Me"));
        var store = new JsonProjectManifestStore();
        var coordinator = new ProjectManifestMutationCoordinator();

        var exception = await Assert.ThrowsAsync<ProjectManifestMutationException>(() =>
            coordinator.ExecuteAsync(
                root,
                ProjectManifestMutationCommand.ReferenceRemove,
                snapshot =>
                {
                    var external = ProjectManifestEditor.Clone(snapshot.Manifest);
                    external.Documents["Book1"].References.Add(
                        new VbaProjectReference("External Edit"));
                    store.Save(root, external);

                    return ProjectManifestMutationPlan<string>.NoOp("unchanged");
                },
                CancellationToken.None));

        Assert.Equal("manifestExternalEditConflict", exception.Code);
        var manifest = store.Load(Path.Combine(root, ProjectManifest.ManifestFileName));
        Assert.Equal(
            ["Keep Me", "External Edit"],
            manifest.Documents["Book1"].References.Select(reference => reference.Name));
    }

    [Fact]
    public async Task CancellationBeforeCommitPreservesTheExactManifestBytes()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp, new VbaProjectReference("Keep Me"));
        var manifestPath = Path.Combine(root, ProjectManifest.ManifestFileName);
        var initialBytes = File.ReadAllBytes(manifestPath);
        using var cancellation = new CancellationTokenSource();
        var coordinator = new ProjectManifestMutationCoordinator();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.ExecuteAsync(
                root,
                ProjectManifestMutationCommand.ReferenceRemove,
                snapshot =>
                {
                    var planned = ProjectManifestEditor.Clone(snapshot.Manifest);
                    planned.Documents["Book1"].References.Clear();
                    cancellation.Cancel();
                    return ProjectManifestMutationPlan<string>.Commit(planned, "removed");
                },
                cancellation.Token));

        Assert.Equal(initialBytes, File.ReadAllBytes(manifestPath));
    }

    [Fact]
    public async Task NoSourceCommitCancellationDoesNotInvokeRecovery()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp, new VbaProjectReference("Keep Me"));
        var manifestPath = Path.Combine(root, ProjectManifest.ManifestFileName);
        var initialBytes = File.ReadAllBytes(manifestPath);
        using var cancellation = new CancellationTokenSource();
        var recoveryInvoked = false;
        var coordinator = new ProjectManifestMutationCoordinator(
            new BoundaryCallbackAtomicWriter(beforeCommit: cancellation.Cancel),
            new ProjectManifestMutationLeaseProvider());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.ExecuteAsync(
                root,
                ProjectManifestMutationCommand.CommonModuleAdd,
                snapshot =>
                {
                    var planned = ProjectManifestEditor.Clone(snapshot.Manifest);
                    planned.Documents["Book1"].References.Clear();
                    return ProjectManifestMutationPlan<string>.Commit(
                        planned,
                        "promoted",
                        sourceMutationCommitted: false,
                        commitFailureRecovery: failure =>
                        {
                            recoveryInvoked = true;
                            return failure;
                        });
                },
                cancellation.Token));

        Assert.False(recoveryInvoked);
        Assert.Equal(initialBytes, File.ReadAllBytes(manifestPath));
        Assert.Empty(Directory.EnumerateFiles(root, "vba-project.failed-*.json"));
    }

    [Fact]
    public async Task CancellationAfterSourceMutationCommitmentIsDeferredThroughManifestCommit()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp, new VbaProjectReference("Remove Me"));
        using var cancellation = new CancellationTokenSource();
        var coordinator = new ProjectManifestMutationCoordinator();

        var outcome = await coordinator.ExecuteAsync(
            root,
            ProjectManifestMutationCommand.CommonModuleUpdate,
            snapshot =>
            {
                var planned = ProjectManifestEditor.Clone(snapshot.Manifest);
                planned.Documents["Book1"].References.Clear();
                cancellation.Cancel();
                return ProjectManifestMutationPlan<string>.Commit(
                    planned,
                    "updated",
                    sourceMutationCommitted: true);
            },
            cancellation.Token);

        Assert.Equal("updated", outcome.Result);
        var warning = Assert.Single(outcome.Warnings);
        Assert.Equal("cancellationDeferred", warning.Code);
        var manifest = new JsonProjectManifestStore().Load(
            Path.Combine(root, ProjectManifest.ManifestFileName));
        Assert.Empty(manifest.Documents["Book1"].References);
    }

    [Fact]
    public async Task CommitFailureRecoveryRunsBeforeTheMutationLeaseIsReleased()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp, new VbaProjectReference("Remove Me"));
        var manifestPath = Path.Combine(root, ProjectManifest.ManifestFileName);
        var leaseMarkerPath = manifestPath + ".vba-dev.lock";
        var recoveryObservedOwnedLease = false;
        var coordinator = new ProjectManifestMutationCoordinator(
            new BoundaryCallbackAtomicWriter(
                beforeCommit: () => throw new IOException("commit failed")),
            new ProjectManifestMutationLeaseProvider());

        var error = await Assert.ThrowsAsync<ProjectManifestMutationException>(() =>
            coordinator.ExecuteAsync(
                root,
                ProjectManifestMutationCommand.CommonModuleUpdate,
                snapshot =>
                {
                    var planned = ProjectManifestEditor.Clone(snapshot.Manifest);
                    planned.Documents["Book1"].References.Clear();
                    return ProjectManifestMutationPlan<string>.Commit(
                        planned,
                        "updated",
                        sourceMutationCommitted: true,
                        commitFailureRecovery: failure =>
                        {
                            recoveryObservedOwnedLease = File.Exists(leaseMarkerPath);
                            return new ProjectManifestMutationException(
                                "recoveredCommitFailure",
                                "Recovery was established while the lease was owned.",
                                failure);
                        });
                },
                CancellationToken.None));

        Assert.Equal("recoveredCommitFailure", error.Code);
        Assert.True(recoveryObservedOwnedLease);
        Assert.False(File.Exists(leaseMarkerPath));
    }

    [Fact]
    public async Task PostRebaseLeaseContinuityFailureRunsSourceMutationRecoveryBeforeRelease()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp, new VbaProjectReference("Remove Me"));
        var leaseProvider = new SecondProofFailureLeaseProvider(root);
        var recoveryInvoked = false;
        var coordinator = new ProjectManifestMutationCoordinator(
            new ProjectManifestAtomicWriter(),
            leaseProvider);

        var error = await Assert.ThrowsAsync<ProjectManifestMutationException>(() =>
            coordinator.ExecuteAsync(
                root,
                ProjectManifestMutationCommand.CommonModuleUpdate,
                snapshot =>
                {
                    var planned = ProjectManifestEditor.Clone(snapshot.Manifest);
                    planned.Documents["Book1"].References.Clear();
                    return ProjectManifestMutationPlan<string>.Commit(
                        planned,
                        "updated",
                        sourceMutationCommitted: true,
                        commitFailureRecovery: failure =>
                        {
                            recoveryInvoked = true;
                            Assert.False(leaseProvider.Released);
                            return new ProjectManifestMutationException(
                                "recoveredContinuityFailure",
                                "Recovery was established after ownership continuity failed.",
                                failure);
                        });
                },
                CancellationToken.None));

        Assert.Equal("recoveredContinuityFailure", error.Code);
        Assert.True(recoveryInvoked);
        Assert.True(leaseProvider.Released);
    }

    [Fact]
    public async Task CancellationAfterCommitCannotReplaceEstablishedSuccess()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp, new VbaProjectReference("Remove Me"));
        using var cancellation = new CancellationTokenSource();
        var coordinator = new ProjectManifestMutationCoordinator(
            new BoundaryCallbackAtomicWriter(afterCommit: cancellation.Cancel),
            new ProjectManifestMutationLeaseProvider());

        var outcome = await coordinator.ExecuteAsync(
            root,
            ProjectManifestMutationCommand.ReferenceRemove,
            snapshot =>
            {
                var planned = ProjectManifestEditor.Clone(snapshot.Manifest);
                planned.Documents["Book1"].References.Clear();
                return ProjectManifestMutationPlan<string>.Commit(planned, "removed");
            },
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal("removed", outcome.Result);
        var manifest = new JsonProjectManifestStore().Load(
            Path.Combine(root, ProjectManifest.ManifestFileName));
        Assert.Empty(manifest.Documents["Book1"].References);
    }

    [Fact]
    public async Task CancellationBeforeNoOpWithholdsTheCompleteResult()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp, new VbaProjectReference("Keep Me"));
        var manifestPath = Path.Combine(root, ProjectManifest.ManifestFileName);
        var initialBytes = File.ReadAllBytes(manifestPath);
        using var cancellation = new CancellationTokenSource();
        var coordinator = new ProjectManifestMutationCoordinator();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.ExecuteAsync(
                root,
                ProjectManifestMutationCommand.ReferenceRemove,
                _ =>
                {
                    cancellation.Cancel();
                    return ProjectManifestMutationPlan<string>.NoOp("unchanged");
                },
                cancellation.Token));

        Assert.Equal(initialBytes, File.ReadAllBytes(manifestPath));
    }

    [Fact]
    public async Task CancellationAfterNoOpCannotReplaceEstablishedSuccess()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp, new VbaProjectReference("Keep Me"));
        var manifestPath = Path.Combine(root, ProjectManifest.ManifestFileName);
        var initialBytes = File.ReadAllBytes(manifestPath);
        using var cancellation = new CancellationTokenSource();
        var coordinator = new ProjectManifestMutationCoordinator(
            new BoundaryCallbackAtomicWriter(afterNoOp: cancellation.Cancel),
            new ProjectManifestMutationLeaseProvider());

        var outcome = await coordinator.ExecuteAsync(
            root,
            ProjectManifestMutationCommand.ReferenceRemove,
            _ => ProjectManifestMutationPlan<string>.NoOp("unchanged"),
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal("unchanged", outcome.Result);
        Assert.Equal(initialBytes, File.ReadAllBytes(manifestPath));
    }

    [Fact]
    public async Task AtomicReplacementFailurePreservesTheExactPriorManifest()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp, new VbaProjectReference("Keep Me"));
        var manifestPath = Path.Combine(root, ProjectManifest.ManifestFileName);
        var initialBytes = File.ReadAllBytes(manifestPath);
        using var replacementBlocker = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var coordinator = new ProjectManifestMutationCoordinator();

        await Assert.ThrowsAsync<IOException>(() => coordinator.ExecuteAsync(
            root,
            ProjectManifestMutationCommand.ReferenceRemove,
            snapshot =>
            {
                var planned = ProjectManifestEditor.Clone(snapshot.Manifest);
                planned.Documents["Book1"].References.Clear();
                return ProjectManifestMutationPlan<string>.Commit(planned, "removed");
            },
            CancellationToken.None));

        Assert.Equal(initialBytes, File.ReadAllBytes(manifestPath));
        Assert.Empty(Directory.EnumerateFiles(
            root,
            ProjectManifest.ManifestFileName + ".vba-dev.*.tmp"));
    }

    [Fact]
    public async Task ParticipatingWritersSerializeRebaseAndPreserveBothMutations()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var store = new JsonProjectManifestStore();
        var coordinator = new ProjectManifestMutationCoordinator();
        using var firstRebaseEntered = new ManualResetEventSlim();
        using var releaseFirstRebase = new ManualResetEventSlim();
        using var secondRebaseEntered = new ManualResetEventSlim();

        var first = Task.Run(async () => await coordinator.ExecuteAsync(
            root,
            ProjectManifestMutationCommand.ReferenceAdd,
            snapshot =>
            {
                firstRebaseEntered.Set();
                releaseFirstRebase.Wait();
                return AddReference(snapshot, "Alpha Library");
            },
            CancellationToken.None));
        Assert.True(firstRebaseEntered.Wait(TimeSpan.FromSeconds(5)));

        var second = Task.Run(async () => await coordinator.ExecuteAsync(
            root,
            ProjectManifestMutationCommand.ReferenceAdd,
            snapshot =>
            {
                secondRebaseEntered.Set();
                return AddReference(snapshot, "Beta Library");
            },
            CancellationToken.None));
        var secondEnteredBeforeFirstReleased =
            secondRebaseEntered.Wait(TimeSpan.FromMilliseconds(250));
        releaseFirstRebase.Set();

        await Task.WhenAll(first, second);

        Assert.False(secondEnteredBeforeFirstReleased);
        Assert.True(secondRebaseEntered.IsSet);
        var manifest = store.Load(Path.Combine(root, ProjectManifest.ManifestFileName));
        Assert.Equal(
            ["Alpha Library", "Beta Library"],
            manifest.Documents["Book1"].References.Select(reference => reference.Name));
    }

    [Theory]
    [InlineData(ProjectManifestMutationCommand.ReferenceAdd, "reference add")]
    [InlineData(ProjectManifestMutationCommand.NewExcel, "new excel")]
    public async Task LeaseTimeoutReportsOnlySafeReadableOwnerMetadata(
        ProjectManifestMutationCommand ownerCommand,
        string stableOwnerCommand)
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var identityResolver = new FileSystemPathIdentityResolver();
        var ownerProvider = new ProjectManifestMutationLeaseProvider(
            identityResolver,
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            "test-version");
        var contenderProvider = new ProjectManifestMutationLeaseProvider(
            identityResolver,
            TimeProvider.System,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(10),
            "test-version");
        var owner = await ownerProvider.AcquireAsync(
            root,
            ownerCommand,
            CancellationToken.None);

        try
        {
            var exception = await Assert.ThrowsAsync<ProjectManifestMutationException>(async () =>
                await contenderProvider.AcquireAsync(
                    root,
                    ProjectManifestMutationCommand.ReferenceRemove,
                    CancellationToken.None));

            Assert.Equal("manifestMutationBusy", exception.Code);
            Assert.Contains(
                $"command '{stableOwnerCommand}'",
                exception.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain(Environment.CommandLine, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            _ = await owner.ReleaseAsync();
        }
    }

    [Fact]
    public async Task LeaseTimeoutDoesNotParseOversizedOwnerMetadata()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var markerPath = Path.Combine(
            root,
            ProjectManifest.ManifestFileName + ".vba-dev.lock");
        var metadata = """
            {
              "schemaVersion": "1.0",
              "leaseId": "00000000-0000-0000-0000-000000000001",
              "machineName": "safe-machine",
              "processId": 123,
              "processStartTimeUtc": "2026-08-25T00:00:00Z",
              "command": "reference add",
              "acquiredAtUtc": "2026-08-25T00:00:00Z",
              "toolVersion": "test-version"
            }
            """.PadRight(64 * 1024, ' ');
        File.WriteAllText(markerPath, metadata, new UTF8Encoding(false));
        using var owner = new FileStream(
            markerPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read);
        var provider = new ProjectManifestMutationLeaseProvider(
            new FileSystemPathIdentityResolver(),
            TimeProvider.System,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(10),
            "test-version");

        var failure = await Assert.ThrowsAsync<ProjectManifestMutationException>(async () =>
            await provider.AcquireAsync(
                root,
                ProjectManifestMutationCommand.ReferenceRemove,
                CancellationToken.None));

        Assert.Equal("manifestMutationBusy", failure.Code);
        Assert.DoesNotContain("Owner:", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadOnlyStaleMarkerFailsImmediatelyWithoutPollingOrChangingBytes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var markerPath = Path.Combine(
            root,
            ProjectManifest.ManifestFileName + ".vba-dev.lock");
        var markerBytes = Encoding.UTF8.GetBytes("{}");
        File.WriteAllBytes(markerPath, markerBytes);
        var originalAttributes = File.GetAttributes(markerPath);
        File.SetAttributes(markerPath, originalAttributes | FileAttributes.ReadOnly);
        var timeProvider = new AdvancingTimeProvider();
        var provider = new ProjectManifestMutationLeaseProvider(
            new FileSystemPathIdentityResolver(),
            timeProvider,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(10),
            "test-version");
        try
        {
            var failure = await Assert.ThrowsAsync<ProjectManifestMutationException>(async () =>
                await provider.AcquireAsync(
                    root,
                    ProjectManifestMutationCommand.ReferenceAdd,
                    CancellationToken.None));

            Assert.Equal("manifestMutationLeaseFailed", failure.Code);
            Assert.Equal(TimeSpan.Zero, timeProvider.Elapsed);
            Assert.Equal(markerBytes, File.ReadAllBytes(markerPath));
            Assert.Equal(
                originalAttributes | FileAttributes.ReadOnly,
                File.GetAttributes(markerPath));
        }
        finally
        {
            File.SetAttributes(markerPath, originalAttributes);
        }
    }

    [Fact]
    public async Task TemporaryMarkerReaderIsPolledUntilItsHandleIsReleased()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var markerPath = Path.Combine(
            root,
            ProjectManifest.ManifestFileName + ".vba-dev.lock");
        File.WriteAllText(markerPath, "{}", new UTF8Encoding(false));
        using var reader = new FileStream(
            markerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var readerReleased = false;
        var timeProvider = new BoundaryTimeProvider(
            () =>
            {
                reader.Dispose();
                readerReleased = true;
            },
            TimeSpan.FromMilliseconds(10));
        var provider = new ProjectManifestMutationLeaseProvider(
            new FileSystemPathIdentityResolver(),
            timeProvider,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            "test-version");

        var lease = await provider.AcquireAsync(
            root,
            ProjectManifestMutationCommand.ReferenceAdd,
            CancellationToken.None);
        var release = await lease.ReleaseAsync();

        Assert.True(readerReleased);
        Assert.Empty(release.Warnings);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task LeaseCannotSucceedAfterTheAcquisitionBoundExpiresBetweenPolls()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var markerPath = Path.Combine(
            root,
            ProjectManifest.ManifestFileName + ".vba-dev.lock");
        File.WriteAllText(markerPath, "{}", new UTF8Encoding(false));
        var owner = new FileStream(
            markerPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read);
        var timeProvider = new BoundaryTimeProvider(
            () => owner.Dispose(),
            TimeSpan.FromMilliseconds(101));
        var provider = new ProjectManifestMutationLeaseProvider(
            new FileSystemPathIdentityResolver(),
            timeProvider,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(10),
            "test-version");
        IProjectManifestMutationLease? unexpectedLease = null;
        ProjectManifestMutationException? failure = null;

        try
        {
            unexpectedLease = await provider.AcquireAsync(
                root,
                ProjectManifestMutationCommand.ReferenceAdd,
                CancellationToken.None);
        }
        catch (ProjectManifestMutationException ex)
        {
            failure = ex;
        }
        finally
        {
            owner.Dispose();
            if (unexpectedLease is not null)
            {
                _ = await unexpectedLease.ReleaseAsync();
            }
        }

        Assert.NotNull(failure);
        Assert.Equal("manifestMutationBusy", failure.Code);
    }

    [Fact]
    public async Task LeasePollingNeverSchedulesPastTheRemainingAcquisitionBound()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var markerPath = Path.Combine(
            root,
            ProjectManifest.ManifestFileName + ".vba-dev.lock");
        File.WriteAllText(markerPath, "{}", new UTF8Encoding(false));
        using var owner = new FileStream(
            markerPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read);
        var timeProvider = new AdvancingTimeProvider();
        var provider = new ProjectManifestMutationLeaseProvider(
            new FileSystemPathIdentityResolver(),
            timeProvider,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(60),
            "test-version");

        var failure = await Assert.ThrowsAsync<ProjectManifestMutationException>(async () =>
            await provider.AcquireAsync(
                root,
                ProjectManifestMutationCommand.ReferenceRemove,
                CancellationToken.None));

        Assert.Equal("manifestMutationBusy", failure.Code);
        Assert.Equal(TimeSpan.FromMilliseconds(100), timeProvider.Elapsed);
    }

    [Fact]
    public async Task VanishedProjectRootIsNotMisreportedAsLeaseContention()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        Directory.Delete(root, recursive: true);
        var provider = new ProjectManifestMutationLeaseProvider(
            new FileSystemPathIdentityResolver(),
            TimeProvider.System,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(10),
            "test-version");

        var failure = await Assert.ThrowsAsync<ProjectManifestMutationException>(async () =>
            await provider.AcquireAsync(
                root,
                ProjectManifestMutationCommand.ReferenceRemove,
                CancellationToken.None));

        Assert.Equal("manifestMutationLeaseFailed", failure.Code);
    }

    [Fact]
    public async Task OwnerMarkerCreateIoFailureIsNotMisreportedAsLeaseContention()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var provider = new ProjectManifestMutationLeaseProvider(
            new FileSystemPathIdentityResolver(),
            TimeProvider.System,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(10),
            "test-version",
            afterOwnerRelease: null,
            useDeleteOnClose: false,
            createOwnerStreamOverride: _ =>
                throw new IOException("simulated marker storage failure"));

        var failure = await Assert.ThrowsAsync<ProjectManifestMutationException>(async () =>
            await provider.AcquireAsync(
                root,
                ProjectManifestMutationCommand.ReferenceAdd,
                CancellationToken.None));

        Assert.Equal("manifestMutationLeaseFailed", failure.Code);
        Assert.IsType<IOException>(failure.InnerException);
    }

    [Fact]
    public async Task ParticipatingCreateCollisionThatDisappearsIsRetried()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var createAttempts = 0;
        var provider = new ProjectManifestMutationLeaseProvider(
            new FileSystemPathIdentityResolver(),
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            "test-version",
            afterOwnerRelease: null,
            useDeleteOnClose: false,
            createOwnerStreamOverride: markerPath =>
            {
                if (Interlocked.Increment(ref createAttempts) == 1)
                {
                    using (new FileStream(
                               markerPath,
                               FileMode.CreateNew,
                               FileAccess.ReadWrite,
                               FileShare.Read))
                    {
                    }

                    try
                    {
                        return new FileStream(
                            markerPath,
                            FileMode.CreateNew,
                            FileAccess.ReadWrite,
                            FileShare.Read);
                    }
                    catch (IOException)
                    {
                        File.Delete(markerPath);
                        throw;
                    }
                }

                return new FileStream(
                    markerPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.Read);
            });

        var lease = await provider.AcquireAsync(
            root,
            ProjectManifestMutationCommand.ReferenceAdd,
            CancellationToken.None);
        var release = await lease.ReleaseAsync();

        Assert.Equal(2, createAttempts);
        Assert.Empty(release.Warnings);
    }

    [Fact]
    public async Task CancellationWhileWaitingForTheLeaseChangesNothing()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp, new VbaProjectReference("Keep Me"));
        var manifestPath = Path.Combine(root, ProjectManifest.ManifestFileName);
        var initialBytes = File.ReadAllBytes(manifestPath);
        var identityResolver = new FileSystemPathIdentityResolver();
        var ownerProvider = new ProjectManifestMutationLeaseProvider(
            identityResolver,
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            "test-version");
        var contenderProvider = new ProjectManifestMutationLeaseProvider(
            identityResolver,
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            "test-version");
        var owner = await ownerProvider.AcquireAsync(
            root,
            ProjectManifestMutationCommand.ReferenceAdd,
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(100));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await contenderProvider.AcquireAsync(
                    root,
                    ProjectManifestMutationCommand.ReferenceRemove,
                    cancellation.Token));

            Assert.Equal(initialBytes, File.ReadAllBytes(manifestPath));
        }
        finally
        {
            _ = await owner.ReleaseAsync();
        }
    }

    [Fact]
    public async Task UnownedMarkerLeftByADeadProcessIsImmediatelyReacquired()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var markerPath = Path.Combine(
            root,
            ProjectManifest.ManifestFileName + ".vba-dev.lock");
        File.WriteAllText(
            markerPath,
            "{\"schemaVersion\":\"1.0\",\"leaseId\":\"00000000-0000-0000-0000-000000000001\"}",
            new UTF8Encoding(false));
        var provider = new ProjectManifestMutationLeaseProvider(
            new FileSystemPathIdentityResolver(),
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            "current-version");

        var lease = await provider.AcquireAsync(
            root,
            ProjectManifestMutationCommand.ReferenceRemove,
            CancellationToken.None);
        var release = await lease.ReleaseAsync();

        Assert.Empty(release.Warnings);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task WindowsOwnerHandleRequestsDeleteOnCloseCrashCleanup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var markerWasDeletedOnOwnerClose = false;
        var provider = new ProjectManifestMutationLeaseProvider(
            new FileSystemPathIdentityResolver(),
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            "test-version",
            markerPath => markerWasDeletedOnOwnerClose = !File.Exists(markerPath));
        var lease = await provider.AcquireAsync(
            root,
            ProjectManifestMutationCommand.ReferenceAdd,
            CancellationToken.None);

        var release = await lease.ReleaseAsync();

        Assert.True(markerWasDeletedOnOwnerClose);
        Assert.Empty(release.Warnings);
    }

    [Fact]
    public async Task WindowsOwnerHandlePreventsMarkerRenameUntilRelease()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var markerPath = Path.Combine(
            root,
            ProjectManifest.ManifestFileName + ".vba-dev.lock");
        var displacedPath = markerPath + ".displaced";
        var provider = new ProjectManifestMutationLeaseProvider(
            new FileSystemPathIdentityResolver(),
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            "test-version");
        var lease = await provider.AcquireAsync(
            root,
            ProjectManifestMutationCommand.NewExcel,
            CancellationToken.None);

        try
        {
            Assert.Throws<IOException>(() => File.Move(markerPath, displacedPath));
            Assert.Throws<IOException>(() => File.Delete(markerPath));
            lease.ProveOwnershipContinuity();
            Assert.True(File.Exists(markerPath));
        }
        finally
        {
            _ = await lease.ReleaseAsync();
        }

        Assert.False(File.Exists(markerPath));
        Assert.False(File.Exists(displacedPath));
    }

    [Fact]
    public async Task LeaseContinuityRejectsAPathVisibleReplacementMarker()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var markerPath = Path.Combine(
            root,
            ProjectManifest.ManifestFileName + ".vba-dev.lock");
        var displacedPath = markerPath + ".displaced";
        var provider = new ProjectManifestMutationLeaseProvider(
            new FileSystemPathIdentityResolver(),
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            "test-version",
            afterOwnerRelease: null,
            useDeleteOnClose: false,
            createOwnerStreamOverride: path => new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.WriteThrough));
        var lease = await provider.AcquireAsync(
            root,
            ProjectManifestMutationCommand.NewExcel,
            CancellationToken.None);

        try
        {
            File.Move(markerPath, displacedPath);
            File.WriteAllText(
                markerPath,
                "{\"leaseId\":\"00000000-0000-0000-0000-000000000001\"}",
                new UTF8Encoding(false));

            var error = Assert.Throws<ProjectManifestMutationException>(
                lease.ProveOwnershipContinuity);

            Assert.Equal("manifestMutationLeaseChanged", error.Code);
            Assert.True(File.Exists(markerPath));
        }
        finally
        {
            _ = await lease.ReleaseAsync();
            File.Delete(markerPath);
            File.Delete(displacedPath);
        }
    }

    [Fact]
    public async Task LeaseContinuityRejectsAReplacementWithCopiedOwnerMetadata()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var markerPath = Path.Combine(
            root,
            ProjectManifest.ManifestFileName + ".vba-dev.lock");
        var displacedPath = markerPath + ".displaced";
        var provider = new ProjectManifestMutationLeaseProvider(
            new FileSystemPathIdentityResolver(),
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            "test-version",
            afterOwnerRelease: null,
            useDeleteOnClose: false,
            createOwnerStreamOverride: path => new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.WriteThrough));
        var lease = await provider.AcquireAsync(
            root,
            ProjectManifestMutationCommand.NewExcel,
            CancellationToken.None);

        try
        {
            byte[] copiedMetadata;
            using (var metadataStream = new FileStream(
                       markerPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var buffer = new MemoryStream())
            {
                metadataStream.CopyTo(buffer);
                copiedMetadata = buffer.ToArray();
            }
            File.Move(markerPath, displacedPath);
            File.WriteAllBytes(markerPath, copiedMetadata);

            var error = Assert.Throws<ProjectManifestMutationException>(
                lease.ProveOwnershipContinuity);

            Assert.Equal("manifestMutationLeaseChanged", error.Code);
        }
        finally
        {
            _ = await lease.ReleaseAsync();
        }

        Assert.True(File.Exists(markerPath));
        File.Delete(markerPath);
        File.Delete(displacedPath);
    }

    [Fact]
    public async Task IdentityRevalidationFailureReleasesTheOpenedOwnerHandle()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var markerPath = Path.Combine(
            root,
            ProjectManifest.ManifestFileName + ".vba-dev.lock");
        var resolver = new ThrowOnSecondIdentityResolution(
            new FileSystemPathIdentityResolver());
        var provider = new ProjectManifestMutationLeaseProvider(
            resolver,
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            "test-version");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await provider.AcquireAsync(
                root,
                ProjectManifestMutationCommand.ReferenceAdd,
                CancellationToken.None));

        var reopened = false;
        try
        {
            using var stream = new FileStream(
                markerPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.DeleteOnClose);
            reopened = true;
        }
        catch (IOException)
        {
            // The assertion below reports retained ownership through the public marker boundary.
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            File.Delete(markerPath);
        }

        Assert.True(reopened);
    }

    [Fact]
    public async Task PreexistingMarkerSymlinkCannotRedirectOwnerMetadata()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var markerPath = Path.Combine(
            root,
            ProjectManifest.ManifestFileName + ".vba-dev.lock");
        var sentinelPath = Path.Combine(temp.Path, "sentinel.txt");
        var sentinelBytes = Encoding.UTF8.GetBytes("sentinel authority");
        File.WriteAllBytes(sentinelPath, sentinelBytes);
        File.CreateSymbolicLink(markerPath, sentinelPath);
        var provider = new ProjectManifestMutationLeaseProvider();
        IProjectManifestMutationLease? unexpectedLease = null;
        ProjectManifestMutationException? failure = null;

        try
        {
            unexpectedLease = await provider.AcquireAsync(
                root,
                ProjectManifestMutationCommand.ReferenceAdd,
                CancellationToken.None);
        }
        catch (ProjectManifestMutationException ex)
        {
            failure = ex;
        }
        finally
        {
            if (unexpectedLease is not null)
            {
                _ = await unexpectedLease.ReleaseAsync();
            }
        }

        Assert.NotNull(failure);
        Assert.Equal("manifestMutationLeaseFailed", failure.Code);
        Assert.True(File.Exists(sentinelPath));
        Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinelPath));
    }

    [Fact]
    public async Task DanglingMarkerSymlinkCannotCreateAnExternalOwnerMetadataTarget()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var markerPath = Path.Combine(
            root,
            ProjectManifest.ManifestFileName + ".vba-dev.lock");
        var absentTargetPath = Path.Combine(temp.Path, "absent-owner-target.txt");
        File.CreateSymbolicLink(markerPath, absentTargetPath);
        var provider = new ProjectManifestMutationLeaseProvider();

        var failure = await Assert.ThrowsAsync<ProjectManifestMutationException>(async () =>
            await provider.AcquireAsync(
                root,
                ProjectManifestMutationCommand.ReferenceAdd,
                CancellationToken.None));

        Assert.Equal("manifestMutationLeaseFailed", failure.Code);
        Assert.False(File.Exists(absentTargetPath));
    }

    [Fact]
    public async Task StaleMarkerHardLinkIsRejectedWithoutRemovingEitherAlias()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var markerPath = Path.Combine(
            root,
            ProjectManifest.ManifestFileName + ".vba-dev.lock");
        var sentinelPath = Path.Combine(temp.Path, "hard-linked-sentinel.txt");
        var sentinelBytes = Encoding.UTF8.GetBytes("sentinel authority");
        File.WriteAllBytes(sentinelPath, sentinelBytes);
        Assert.True(CreateHardLink(markerPath, sentinelPath, IntPtr.Zero));
        var provider = new ProjectManifestMutationLeaseProvider();
        IProjectManifestMutationLease? unexpectedLease = null;
        try
        {
            var failure = await Assert.ThrowsAsync<ProjectManifestMutationException>(async () =>
                unexpectedLease = await provider.AcquireAsync(
                    root,
                    ProjectManifestMutationCommand.ReferenceAdd,
                    CancellationToken.None));

            Assert.Equal("manifestMutationLeaseFailed", failure.Code);
        }
        finally
        {
            if (unexpectedLease is not null)
            {
                await unexpectedLease.ReleaseAsync();
            }
        }

        Assert.Equal(sentinelBytes, File.ReadAllBytes(markerPath));
        Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinelPath));
    }

    [Fact]
    public async Task ProcessDeathReleasesOwnershipWithoutForceUnlockingTheMarker()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var markerPath = Path.Combine(
            root,
            ProjectManifest.ManifestFileName + ".vba-dev.lock");
        var readyPath = Path.Combine(root, "lease-owner.ready");
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "$marker = $env:VBA_DEV_TEST_MARKER; $ready = $env:VBA_DEV_TEST_READY; " +
            "$stream = [System.IO.File]::Open(" +
            "$marker, [System.IO.FileMode]::OpenOrCreate, " +
            "[System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Read); " +
            "[System.IO.File]::WriteAllText($ready, 'ready'); " +
            "Start-Sleep -Seconds 30");
        startInfo.Environment["VBA_DEV_TEST_MARKER"] = markerPath;
        startInfo.Environment["VBA_DEV_TEST_READY"] = readyPath;
        using var ownerProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The lease-owner process did not start.");

        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => File.Exists(readyPath),
                TimeSpan.FromSeconds(5)));
            var identityResolver = new FileSystemPathIdentityResolver();
            var blockedProvider = new ProjectManifestMutationLeaseProvider(
                identityResolver,
                TimeProvider.System,
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(10),
                "test-version");
            var busy = await Assert.ThrowsAsync<ProjectManifestMutationException>(async () =>
                await blockedProvider.AcquireAsync(
                    root,
                    ProjectManifestMutationCommand.ReferenceAdd,
                    CancellationToken.None));
            Assert.Equal("manifestMutationBusy", busy.Code);

            ownerProcess.Kill(entireProcessTree: true);
            await ownerProcess.WaitForExitAsync();
            var recoveryProvider = new ProjectManifestMutationLeaseProvider(
                identityResolver,
                TimeProvider.System,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(10),
                "test-version");
            var recovered = await recoveryProvider.AcquireAsync(
                root,
                ProjectManifestMutationCommand.ReferenceRemove,
                CancellationToken.None);
            var release = await recovered.ReleaseAsync();

            Assert.Empty(release.Warnings);
            Assert.False(File.Exists(markerPath));
        }
        finally
        {
            if (!ownerProcess.HasExited)
            {
                ownerProcess.Kill(entireProcessTree: true);
                await ownerProcess.WaitForExitAsync();
            }

            File.Delete(readyPath);
            File.Delete(markerPath);
        }
    }

    [Fact]
    public async Task ReleasingTheSameLeaseTwiceFailsWithoutRepeatingCleanup()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var markerPath = Path.Combine(
            root,
            ProjectManifest.ManifestFileName + ".vba-dev.lock");
        var releaseCallbacks = 0;
        var provider = new ProjectManifestMutationLeaseProvider(
            new FileSystemPathIdentityResolver(),
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            "test-version",
            afterOwnerRelease: _ => releaseCallbacks++);
        var lease = await provider.AcquireAsync(
            root,
            ProjectManifestMutationCommand.ReferenceAdd,
            CancellationToken.None);

        var firstRelease = await lease.ReleaseAsync();
        var failure = await Assert.ThrowsAsync<ProjectManifestMutationException>(async () =>
            await lease.ReleaseAsync());

        Assert.Empty(firstRelease.Warnings);
        Assert.Equal("manifestMutationReleaseFailed", failure.Code);
        Assert.Equal(1, releaseCallbacks);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task ReleasedOwnerReportsItsRetainedMarkerAsACleanupWarning()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var markerPath = Path.Combine(
            root,
            ProjectManifest.ManifestFileName + ".vba-dev.lock");
        FileStream? observer = null;
        var provider = new ProjectManifestMutationLeaseProvider(
            new FileSystemPathIdentityResolver(),
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            "test-version",
            releasedMarkerPath => observer = new FileStream(
                releasedMarkerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read),
            useDeleteOnClose: false);
        var lease = await provider.AcquireAsync(
            root,
            ProjectManifestMutationCommand.ReferenceAdd,
            CancellationToken.None);

        try
        {
            var release = await lease.ReleaseAsync();

            var warning = Assert.Single(release.Warnings);
            Assert.Equal("leaseMarkerCleanupFailed", warning.Code);
            Assert.True(File.Exists(markerPath));
        }
        finally
        {
            observer?.Dispose();
            File.Delete(markerPath);
        }
    }

    [Fact]
    public async Task ReleasedOwnerPreservesAReplacementMarkerSymlinkWithoutFailingRelease()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var markerPath = Path.Combine(
            root,
            ProjectManifest.ManifestFileName + ".vba-dev.lock");
        var sentinelPath = Path.Combine(temp.Path, "foreign-marker-target.txt");
        var probePath = Path.Combine(temp.Path, "symlink-probe.txt");
        var sentinelBytes = Encoding.UTF8.GetBytes("foreign marker target");
        File.WriteAllBytes(sentinelPath, sentinelBytes);
        try
        {
            File.CreateSymbolicLink(probePath, sentinelPath);
            File.Delete(probePath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var provider = new ProjectManifestMutationLeaseProvider(
            new FileSystemPathIdentityResolver(),
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            "test-version",
            releasedMarkerPath =>
            {
                File.Delete(releasedMarkerPath);
                File.CreateSymbolicLink(releasedMarkerPath, sentinelPath);
            },
            useDeleteOnClose: false);
        var lease = await provider.AcquireAsync(
            root,
            ProjectManifestMutationCommand.ReferenceAdd,
            CancellationToken.None);

        try
        {
            var release = await lease.ReleaseAsync();

            Assert.Empty(release.Warnings);
            Assert.True(File.Exists(markerPath));
            Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinelPath));
        }
        finally
        {
            File.Delete(markerPath);
        }

        Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinelPath));
    }

    [Fact]
    public async Task ReleasedOwnerPreservesAReplacementWithCopiedMetadataWithoutWarning()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var markerPath = Path.Combine(
            root,
            ProjectManifest.ManifestFileName + ".vba-dev.lock");
        var originalMarkerPath = Path.Combine(temp.Path, "released-original-marker.lock");
        var identityResolver = new FileSystemPathIdentityResolver();
        byte[]? copiedBytes = null;
        var provider = new ProjectManifestMutationLeaseProvider(
            identityResolver,
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            "test-version",
            releasedMarkerPath =>
            {
                var originalIdentity = identityResolver.Resolve(releasedMarkerPath);
                copiedBytes = File.ReadAllBytes(releasedMarkerPath);
                File.Move(releasedMarkerPath, originalMarkerPath);
                File.WriteAllBytes(releasedMarkerPath, copiedBytes);
                Assert.NotNull(originalIdentity.ObjectIdentity);
                Assert.NotEqual(
                    originalIdentity.ObjectIdentity,
                    identityResolver.Resolve(releasedMarkerPath).ObjectIdentity);
            },
            useDeleteOnClose: false);
        var lease = await provider.AcquireAsync(
            root,
            ProjectManifestMutationCommand.ReferenceAdd,
            CancellationToken.None);

        var release = await lease.ReleaseAsync();

        Assert.Empty(release.Warnings);
        Assert.NotNull(copiedBytes);
        Assert.Equal(copiedBytes, File.ReadAllBytes(markerPath));
        Assert.Equal(copiedBytes, File.ReadAllBytes(originalMarkerPath));
    }

    [Fact]
    public async Task ReleasedOwnerPreservesBothMarkerHardLinkAliasesAsACleanupWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var markerPath = Path.Combine(
            root,
            ProjectManifest.ManifestFileName + ".vba-dev.lock");
        var aliasPath = Path.Combine(temp.Path, "released-marker-alias.lock");
        byte[]? markerBytes = null;
        var provider = new ProjectManifestMutationLeaseProvider(
            new FileSystemPathIdentityResolver(),
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            "test-version",
            releasedMarkerPath =>
            {
                markerBytes = File.ReadAllBytes(releasedMarkerPath);
                Assert.True(CreateHardLink(aliasPath, releasedMarkerPath, IntPtr.Zero));
            },
            useDeleteOnClose: false);
        var lease = await provider.AcquireAsync(
            root,
            ProjectManifestMutationCommand.ReferenceAdd,
            CancellationToken.None);

        var release = await lease.ReleaseAsync();

        Assert.NotNull(markerBytes);
        Assert.Equal(markerBytes, File.ReadAllBytes(markerPath));
        Assert.Equal(markerBytes, File.ReadAllBytes(aliasPath));
        var warning = Assert.Single(release.Warnings);
        Assert.Equal("leaseMarkerCleanupFailed", warning.Code);
    }

    [Fact]
    public async Task ReleasedOwnerPreservesChangedMetadataWithTheSameLeaseId()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var markerPath = Path.Combine(
            root,
            ProjectManifest.ManifestFileName + ".vba-dev.lock");
        var identityResolver = new FileSystemPathIdentityResolver();
        byte[]? changedBytes = null;
        var provider = new ProjectManifestMutationLeaseProvider(
            identityResolver,
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            "test-version",
            releasedMarkerPath =>
            {
                var originalIdentity = identityResolver.Resolve(releasedMarkerPath);
                var originalText = File.ReadAllText(releasedMarkerPath);
                var changedText = originalText.Replace(
                    "test-version",
                    "edit-version",
                    StringComparison.Ordinal);
                Assert.NotEqual(originalText, changedText);
                Assert.Equal(originalText.Length, changedText.Length);
                changedBytes = Encoding.UTF8.GetBytes(changedText);
                File.WriteAllBytes(releasedMarkerPath, changedBytes);
                Assert.NotNull(originalIdentity.ObjectIdentity);
                Assert.Equal(
                    originalIdentity.ObjectIdentity,
                    identityResolver.Resolve(releasedMarkerPath).ObjectIdentity);
            },
            useDeleteOnClose: false);
        var lease = await provider.AcquireAsync(
            root,
            ProjectManifestMutationCommand.ReferenceAdd,
            CancellationToken.None);

        var release = await lease.ReleaseAsync();

        Assert.True(File.Exists(markerPath));
        Assert.NotNull(changedBytes);
        Assert.Equal(changedBytes, File.ReadAllBytes(markerPath));
        var warning = Assert.Single(release.Warnings);
        Assert.Equal("leaseMarkerCleanupFailed", warning.Code);
    }

    [Fact]
    public async Task MalformedReleasedOwnerMarkerIsClassifiedAsACleanupWarning()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var markerPath = Path.Combine(
            root,
            ProjectManifest.ManifestFileName + ".vba-dev.lock");
        var provider = new ProjectManifestMutationLeaseProvider(
            new FileSystemPathIdentityResolver(),
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            "test-version",
            releasedMarkerPath => File.WriteAllText(
                releasedMarkerPath,
                "[]",
                new UTF8Encoding(false)),
            useDeleteOnClose: false);
        var lease = await provider.AcquireAsync(
            root,
            ProjectManifestMutationCommand.ReferenceAdd,
            CancellationToken.None);

        try
        {
            var release = await lease.ReleaseAsync();

            var warning = Assert.Single(release.Warnings);
            Assert.Equal("leaseMarkerCleanupFailed", warning.Code);
            Assert.True(File.Exists(markerPath));
        }
        finally
        {
            File.Delete(markerPath);
        }
    }

    [Fact]
    public async Task AliasSelectedProjectsShareOnePhysicalMutationLease()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var aliasRoot = Path.Combine(temp.Path, "ProjectAlias");
        Directory.CreateSymbolicLink(aliasRoot, root);
        var coordinator = new ProjectManifestMutationCoordinator();
        using var firstRebaseEntered = new ManualResetEventSlim();
        using var releaseFirstRebase = new ManualResetEventSlim();
        using var aliasRebaseEntered = new ManualResetEventSlim();

        var first = Task.Run(async () => await coordinator.ExecuteAsync(
            root,
            ProjectManifestMutationCommand.ReferenceAdd,
            snapshot =>
            {
                firstRebaseEntered.Set();
                releaseFirstRebase.Wait();
                return AddReference(snapshot, "Alpha Library");
            },
            CancellationToken.None));
        Assert.True(firstRebaseEntered.Wait(TimeSpan.FromSeconds(5)));

        var alias = Task.Run(async () => await coordinator.ExecuteAsync(
            aliasRoot,
            ProjectManifestMutationCommand.ReferenceAdd,
            snapshot =>
            {
                aliasRebaseEntered.Set();
                return AddReference(snapshot, "Beta Library");
            },
            CancellationToken.None));
        var aliasEnteredBeforeFirstReleased =
            aliasRebaseEntered.Wait(TimeSpan.FromMilliseconds(250));
        releaseFirstRebase.Set();

        await Task.WhenAll(first, alias);

        Assert.False(aliasEnteredBeforeFirstReleased);
        var manifest = new JsonProjectManifestStore().Load(
            Path.Combine(root, ProjectManifest.ManifestFileName));
        Assert.Equal(
            ["Alpha Library", "Beta Library"],
            manifest.Documents["Book1"].References.Select(reference => reference.Name));
    }

    private static ProjectManifestMutationPlan<string> AddReference(
        ProjectManifestMutationSnapshot snapshot,
        string referenceName)
    {
        var manifest = ProjectManifestEditor.Clone(snapshot.Manifest);
        manifest.Documents["Book1"].References.Add(
            new VbaProjectReference(referenceName));
        return ProjectManifestMutationPlan<string>.Commit(manifest, referenceName);
    }

    private static string CreateProject(
        TempDirectory temp,
        params VbaProjectReference[] references)
    {
        var root = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(root, "src", "Book1"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        Directory.CreateDirectory(Path.Combine(root, "publish"));
        File.WriteAllText(
            Path.Combine(root, "src", "Book1", "Book1.xlsm"),
            "template",
            new UTF8Encoding(false));
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"].References.AddRange(references);
        new JsonProjectManifestStore().Save(root, manifest);
        return root;
    }

    private sealed class BoundaryCallbackAtomicWriter(
        Action? beforeCommit = null,
        Action? afterCommit = null,
        Action? afterNoOp = null)
        : IProjectManifestAtomicWriter
    {
        private readonly ProjectManifestAtomicWriter inner = new();

        public void Save(string manifestPath, ProjectManifest manifest)
            => inner.Save(manifestPath, manifest);

        public void ReplaceExisting(
            string manifestPath,
            ReadOnlyMemory<byte> expectedRawBytes,
            ProjectManifest manifest,
            CancellationToken cancellationToken)
        {
            beforeCommit?.Invoke();
            inner.ReplaceExisting(
                manifestPath,
                expectedRawBytes,
                manifest,
                cancellationToken);
            afterCommit?.Invoke();
        }

        public void EstablishNoOp(
            string manifestPath,
            ReadOnlyMemory<byte> expectedRawBytes,
            CancellationToken cancellationToken)
        {
            inner.EstablishNoOp(
                manifestPath,
                expectedRawBytes,
                cancellationToken);
            afterNoOp?.Invoke();
        }

        public string CreateRecovery(string projectRoot, ProjectManifest manifest)
            => inner.CreateRecovery(projectRoot, manifest);
    }

    private sealed class SecondProofFailureLeaseProvider(string projectRoot)
        : IProjectManifestMutationLeaseProvider
    {
        public bool Released { get; private set; }

        public ValueTask<IProjectManifestMutationLease> AcquireAsync(
            string requestedProjectRoot,
            ProjectManifestMutationCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IProjectManifestMutationLease>(
                new SecondProofFailureLease(
                    new FileSystemPathIdentityResolver().Resolve(projectRoot),
                    Path.Combine(projectRoot, ProjectManifest.ManifestFileName),
                    () => Released = true));
        }
    }

    private sealed class SecondProofFailureLease(
        FileSystemPathIdentity projectIdentity,
        string manifestPath,
        Action onRelease)
        : IProjectManifestMutationLease
    {
        private int proofCount;

        public FileSystemPathIdentity ProjectIdentity { get; } = projectIdentity;

        public string ManifestPath { get; } = manifestPath;

        public void ProveOwnershipContinuity()
        {
            if (Interlocked.Increment(ref proofCount) == 2)
            {
                throw new ProjectManifestMutationException(
                    "manifestMutationLeaseChanged",
                    "The project mutation lease changed after source mutation.");
            }
        }

        public ValueTask<ProjectManifestLeaseRelease> ReleaseAsync()
        {
            onRelease();
            return ValueTask.FromResult(new ProjectManifestLeaseRelease([]));
        }
    }

    private sealed class ThrowOnSecondIdentityResolution(
        IFileSystemPathIdentityResolver inner)
        : IFileSystemPathIdentityResolver
    {
        private int count;

        public FileSystemPathIdentity Resolve(string path)
            => Interlocked.Increment(ref count) == 2
                ? throw new InvalidOperationException("simulated identity revalidation failure")
                : inner.Resolve(path);
    }

    private sealed class BoundaryTimeProvider(
        Action beforeFirstDelayCompletes,
        TimeSpan elapsedAfterFirstDelay)
        : TimeProvider
    {
        private long timestamp;
        private int firstDelayCompleted;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
            => Interlocked.Read(ref timestamp);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
            => new BoundaryTimer(
                callback,
                state,
                dueTime,
                period,
                () =>
                {
                    if (Interlocked.Exchange(ref firstDelayCompleted, 1) != 0)
                    {
                        return;
                    }

                    beforeFirstDelayCompletes();
                    Interlocked.Exchange(
                        ref timestamp,
                        elapsedAfterFirstDelay.Ticks);
                });
    }

    private sealed class BoundaryTimer : ITimer
    {
        private readonly Timer timer;

        public BoundaryTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period,
            Action beforeCallback)
        {
            timer = new Timer(
                callbackState =>
                {
                    beforeCallback();
                    callback(callbackState);
                },
                state,
                dueTime,
                period);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
            => timer.Change(dueTime, period);

        public void Dispose()
            => timer.Dispose();

        public ValueTask DisposeAsync()
            => timer.DisposeAsync();
    }

    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private long timestamp;

        public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref timestamp));

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
            => Interlocked.Read(ref timestamp);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
            => new BoundaryTimer(
                callback,
                state,
                TimeSpan.FromMilliseconds(1),
                Timeout.InfiniteTimeSpan,
                () => Interlocked.Add(ref timestamp, dueTime.Ticks));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
