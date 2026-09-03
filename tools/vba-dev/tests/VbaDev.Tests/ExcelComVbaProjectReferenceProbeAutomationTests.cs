using System.Dynamic;
using System.Runtime.InteropServices;
using System.Text;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class ExcelComVbaProjectReferenceProbeAutomationTests
{
    [Fact]
    public async Task UsesOneOwnedProcessAndAFreshBaselineCopyForEveryCandidateAttempt()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(templatePath, "source template", new UTF8Encoding(false));
        var lifecycle = new FakeReferenceProbeLifecycle();
        var automation = new ExcelComVbaProjectReferenceProbeAutomation(
            new ImmediateDispatcherFactory(),
            lifecycle);
        var probe = new VbaProjectReferenceAmbiguityProbe(automation);
        var registryResolution = new VbaProjectReferenceResolutionBatch(
            true,
            [],
            null,
            [
                new VbaProjectReferenceNameResolution(
                    "Widget Library",
                    "Widget Library",
                    true,
                    [
                        new ResolvedVbaProjectReference(
                            "Widget Library",
                            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                            1,
                            0),
                        new ResolvedVbaProjectReference(
                            "Widget Library",
                            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                            2,
                            0)
                    ])
            ]);

        await probe.ResolveAsync(
            templatePath,
            registryResolution,
            CancellationToken.None);

        Assert.Equal(1, lifecycle.StartCalls);
        Assert.Equal(1, lifecycle.DisposeHostCalls);
        Assert.Equal(2, lifecycle.OpenedWorkbookPaths.Count);
        Assert.Equal(2, lifecycle.OpenedWorkbookPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(lifecycle.ObservedBaselineContents, content => Assert.Equal("source template", content));
        Assert.All(lifecycle.OpenedWorkbookPaths, path => Assert.False(File.Exists(path)));
        Assert.Equal("source template", File.ReadAllText(templatePath, Encoding.UTF8));
        Assert.Equal(2, lifecycle.CloseWithoutSaveCalls);
    }

    [Fact]
    public async Task UsesOneOwnedProcessAndAFreshBlankWorkbookForEveryCandidateAttempt()
    {
        var lifecycle = new FakeReferenceProbeLifecycle();
        var automation = new ExcelComVbaProjectReferenceProbeAutomation(
            new ImmediateDispatcherFactory(),
            lifecycle);
        var probe = new VbaProjectReferenceAmbiguityProbe(automation);
        var registryResolution = new VbaProjectReferenceResolutionBatch(
            true,
            [],
            null,
            [
                new VbaProjectReferenceNameResolution(
                    "Widget Library",
                    "Widget Library",
                    true,
                    [
                        new ResolvedVbaProjectReference(
                            "Widget Library",
                            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                            1,
                            0),
                        new ResolvedVbaProjectReference(
                            "Widget Library",
                            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                            2,
                            0)
                    ])
            ]);

        var result = await probe.ResolveAsync(
            VbaProjectReferenceProbeBaseline.BlankWorkbook,
            registryResolution,
            CancellationToken.None);

        Assert.True(result.Complete);
        Assert.Equal(2, Assert.Single(result.References).Matches.Count);
        Assert.Equal(1, lifecycle.StartCalls);
        Assert.Equal(1, lifecycle.DisposeHostCalls);
        Assert.Equal(2, lifecycle.BlankWorkbookCreations);
        Assert.Equal(2, lifecycle.BlankWorkbooks.Distinct().Count());
        Assert.Empty(lifecycle.OpenedWorkbookPaths);
        Assert.Equal(2, lifecycle.CloseWithoutSaveCalls);
        Assert.Equal(2, lifecycle.ReleaseReferenceCalls);
    }

    [Fact]
    public async Task BlankWorkbookCreationFailureIsReportedAsAnUnavailableProbeBaseline()
    {
        var lifecycle = new FakeReferenceProbeLifecycle
        {
            CreateBlankWorkbookError = new InvalidOperationException(
                "A blank workbook could not be created.")
        };
        var probe = new VbaProjectReferenceAmbiguityProbe(
            new ExcelComVbaProjectReferenceProbeAutomation(
                new ImmediateDispatcherFactory(),
                lifecycle));

        var result = await probe.ResolveAsync(
            VbaProjectReferenceProbeBaseline.BlankWorkbook,
            CreateAmbiguousRegistryResolution(),
            CancellationToken.None);

        Assert.False(result.Complete);
        Assert.Equal("probeAborted", Assert.Single(result.References).UnverifiedReasonCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("probeBaselineUnavailable", diagnostic.Code);
        Assert.Contains("blank-workbook", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(1, lifecycle.StartCalls);
        Assert.Equal(1, lifecycle.DisposeHostCalls);
        Assert.Equal(1, lifecycle.BlankWorkbookCreations);
        Assert.Equal(0, lifecycle.CloseWithoutSaveCalls);
    }

    [Fact]
    public async Task TimeoutWithComOnlyPostReleaseCleanupPreservesTheProbeTimeout()
    {
        var lifecycle = new FakeReferenceProbeLifecycle
        {
            DisposeHostError = new COMException(
                "The released Excel server rejected Quit.")
        };
        var automation = new ExcelComVbaProjectReferenceProbeAutomation(
            new ImmediateDispatcherFactory(),
            lifecycle);
        var timeout = new WorkbookAutomationTimeoutException(
            new WorkbookAutomationStage(WorkbookAutomationStageKind.ReferenceAttempt),
            TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<VbaProjectReferenceProbeAttemptException>(() =>
            automation.RunAsync(
                VbaProjectReferenceProbeBaseline.BlankWorkbook,
                WorkbookAutomationTimeouts.Default,
                (_, _) => Task.FromException<bool>(timeout),
                CancellationToken.None));

        Assert.Equal("probeTimeout", error.ReasonCode);
        Assert.Same(timeout, error.InnerException);
        Assert.False(error.ProcessTrusted);
        Assert.Equal(1, lifecycle.DisposeHostCalls);
        Assert.True(lifecycle.OwnerHasExited);
    }

    [Fact]
    public async Task TimeoutWithMixedPostReleaseCleanupSurfacesTheProbeCleanupFailure()
    {
        var lifecycle = new FakeReferenceProbeLifecycle
        {
            DisposeHostError = new AggregateException(
                new COMException("The released Excel server rejected Quit."),
                new InvalidOperationException("Unexpected cleanup defect."))
        };
        var automation = new ExcelComVbaProjectReferenceProbeAutomation(
            new ImmediateDispatcherFactory(),
            lifecycle);
        var timeout = new WorkbookAutomationTimeoutException(
            new WorkbookAutomationStage(WorkbookAutomationStageKind.ReferenceAttempt),
            TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<VbaProjectReferenceProbeAttemptException>(() =>
            automation.RunAsync(
                VbaProjectReferenceProbeBaseline.BlankWorkbook,
                WorkbookAutomationTimeouts.Default,
                (_, _) => Task.FromException<bool>(timeout),
                CancellationToken.None));

        Assert.Equal("cleanupFailure", error.ReasonCode);
        Assert.False(error.ProcessTrusted);
        var failures = Assert.IsType<AggregateException>(error.InnerException)
            .Flatten()
            .InnerExceptions;
        Assert.Contains(
            failures,
            failure => failure is VbaProjectReferenceProbeAttemptException
            {
                ReasonCode: "probeTimeout"
            });
        Assert.Contains(failures, failure => failure is COMException);
        Assert.Contains(failures, failure => failure is InvalidOperationException);
        Assert.Equal(1, lifecycle.DisposeHostCalls);
        Assert.True(lifecycle.OwnerHasExited);
    }

    [Fact]
    public async Task DispatcherDisposalFailureAfterExactReleaseIsClassifiedAsReleasedCleanup()
    {
        var lifecycle = new FakeReferenceProbeLifecycle();
        var dispatcherError = new InvalidOperationException(
            "The reference-probe dispatcher could not be disposed.");
        var automation = new ExcelComVbaProjectReferenceProbeAutomation(
            new FakeStaComDispatcherFactory(
                new DisposeFailingDispatcher(dispatcherError)),
            lifecycle);

        var error = await Assert.ThrowsAsync<VbaProjectReferenceProbeAttemptException>(() =>
            automation.RunAsync(
                VbaProjectReferenceProbeBaseline.BlankWorkbook,
                WorkbookAutomationTimeouts.Default,
                (_, _) => Task.FromResult(true),
                CancellationToken.None));

        Assert.Equal("cleanupFailure", error.ReasonCode);
        Assert.False(error.ProcessTrusted);
        var releasedCleanup = Assert.IsType<WorkbookAutomationReleasedProcessCleanupException>(
            error.InnerException);
        Assert.Same(dispatcherError, releasedCleanup.InnerException);
        Assert.True(lifecycle.OwnerHasExited);
    }

    [Fact]
    public async Task ReleasedCleanupAndDispatcherDisposalFailureRemainReleasedCleanup()
    {
        var lifecycleCleanup = new WorkbookAutomationReleasedProcessCleanupException(
            "The process was released after cooperative cleanup failed.",
            new COMException("The released Excel server rejected Quit."));
        var lifecycle = new FakeReferenceProbeLifecycle
        {
            DisposeHostError = lifecycleCleanup
        };
        var dispatcherError = new InvalidOperationException(
            "The reference-probe dispatcher could not be disposed.");
        var automation = new ExcelComVbaProjectReferenceProbeAutomation(
            new FakeStaComDispatcherFactory(
                new DisposeFailingDispatcher(dispatcherError)),
            lifecycle);

        var error = await Assert.ThrowsAsync<VbaProjectReferenceProbeAttemptException>(() =>
            automation.RunAsync(
                VbaProjectReferenceProbeBaseline.BlankWorkbook,
                WorkbookAutomationTimeouts.Default,
                (_, _) => Task.FromResult(true),
                CancellationToken.None));

        Assert.Equal("cleanupFailure", error.ReasonCode);
        Assert.False(error.ProcessTrusted);
        var releasedCleanup = Assert.IsType<WorkbookAutomationReleasedProcessCleanupException>(
            error.InnerException);
        var failures = Assert.IsType<AggregateException>(releasedCleanup.InnerException)
            .InnerExceptions;
        Assert.Same(lifecycleCleanup, failures[0]);
        var dispatcherCleanup = Assert.IsType<WorkbookAutomationReleasedProcessCleanupException>(
            failures[1]);
        Assert.Same(dispatcherError, dispatcherCleanup.InnerException);
        Assert.True(lifecycle.OwnerHasExited);
    }

    [Fact]
    public async Task CooperativeAndReleasedOwnershipCleanupRemainReleaseVerified()
    {
        var cooperativeError = new InvalidOperationException(
            "Cooperative reference-probe cleanup failed.");
        var ownershipError = new WorkbookAutomationReleasedProcessCleanupException(
            "Exact owned-process release was verified despite an isolation failure.");
        var lifecycle = new FakeReferenceProbeLifecycle
        {
            DisposeHostError = cooperativeError,
            OwnerDisposeError = ownershipError
        };
        var automation = new ExcelComVbaProjectReferenceProbeAutomation(
            new ImmediateDispatcherFactory(),
            lifecycle);

        var error = await Assert.ThrowsAsync<VbaProjectReferenceProbeAttemptException>(() =>
            automation.RunAsync(
                VbaProjectReferenceProbeBaseline.BlankWorkbook,
                WorkbookAutomationTimeouts.Default,
                (_, _) => Task.FromResult(true),
                CancellationToken.None));

        Assert.Equal("cleanupFailure", error.ReasonCode);
        Assert.False(WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(error));
        var cleanup = Assert.IsType<WorkbookAutomationReleasedProcessCleanupException>(
            error.InnerException);
        var failures = Assert.IsType<AggregateException>(cleanup.InnerException)
            .InnerExceptions;
        Assert.Contains(failures, failure => ReferenceEquals(failure, cooperativeError));
        Assert.Contains(failures, failure => ReferenceEquals(failure, ownershipError));
    }

    [Fact]
    public async Task CooperativeAndUnprovedOwnershipCleanupRemainProofFailure()
    {
        var cooperativeError = new InvalidOperationException(
            "Cooperative reference-probe cleanup failed.");
        var ownershipError = new WorkbookAutomationCleanupException(
            "Exact owned-process release could not be proved.");
        var lifecycle = new FakeReferenceProbeLifecycle
        {
            DisposeHostError = cooperativeError,
            OwnerDisposeError = ownershipError
        };
        var automation = new ExcelComVbaProjectReferenceProbeAutomation(
            new ImmediateDispatcherFactory(),
            lifecycle);

        var error = await Assert.ThrowsAsync<VbaProjectReferenceProbeAttemptException>(() =>
            automation.RunAsync(
                VbaProjectReferenceProbeBaseline.BlankWorkbook,
                WorkbookAutomationTimeouts.Default,
                (_, _) => Task.FromResult(true),
                CancellationToken.None));

        Assert.Equal("cleanupFailure", error.ReasonCode);
        Assert.True(WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(error));
        var cleanup = Assert.IsType<WorkbookAutomationCleanupException>(error.InnerException);
        var failures = Assert.IsType<AggregateException>(cleanup.InnerException)
            .InnerExceptions;
        Assert.Contains(failures, failure => ReferenceEquals(failure, cooperativeError));
        Assert.Contains(
            failures,
            failure => failure is WorkbookAutomationCleanupException ownershipCleanup &&
                       ReferenceEquals(ownershipCleanup.InnerException, ownershipError));
    }

    [Fact]
    public async Task ReleasedProcessCleanupPreservesFinalWorkspaceCleanupFailure()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(templatePath, "source template", new UTF8Encoding(false));
        var ownershipError = new WorkbookAutomationReleasedProcessCleanupException(
            "Exact owned-process release was verified despite an isolation failure.");
        var lifecycle = new FakeReferenceProbeLifecycle
        {
            CreateWorkspaceBlockerOnOpen = true,
            OwnerDisposeError = ownershipError
        };
        var automation = new ExcelComVbaProjectReferenceProbeAutomation(
            new ImmediateDispatcherFactory(),
            lifecycle);
        try
        {
            var error = await Assert.ThrowsAsync<VbaProjectReferenceProbeAttemptException>(() =>
                automation.RunAsync(
                    VbaProjectReferenceProbeBaseline.SourceTemplate(templatePath),
                    WorkbookAutomationTimeouts.Default,
                    (session, cancellationToken) => session.TryResolveAsync(
                        "Widget Library",
                        new ResolvedVbaProjectReference(
                            "Widget Library",
                            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                            1,
                            0),
                        cancellationToken),
                    CancellationToken.None));

            Assert.Equal("cleanupFailure", error.ReasonCode);
            Assert.False(WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(error));
            var cleanup = Assert.IsType<WorkbookAutomationReleasedProcessCleanupException>(
                error.InnerException);
            var failures = Assert.IsType<AggregateException>(cleanup.InnerException)
                .InnerExceptions;
            Assert.Contains(failures, failure => ReferenceEquals(failure, ownershipError));
            Assert.Contains(failures, failure => failure is IOException);
        }
        finally
        {
            if (lifecycle.WorkspaceBlockerPath is not null)
            {
                var workspacePath = Path.GetDirectoryName(
                    lifecycle.WorkspaceBlockerPath)!;
                if (Directory.Exists(workspacePath))
                {
                    Directory.Delete(workspacePath, recursive: true);
                }
            }
        }
    }

    [Fact]
    public async Task UnprovedProcessCleanupAndFinalWorkspaceFailureRemainProofFailure()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(templatePath, "source template", new UTF8Encoding(false));
        var ownershipError = new WorkbookAutomationCleanupException(
            "Exact owned-process release could not be proved.");
        var lifecycle = new FakeReferenceProbeLifecycle
        {
            CreateWorkspaceBlockerOnOpen = true,
            OwnerDisposeError = ownershipError
        };
        var automation = new ExcelComVbaProjectReferenceProbeAutomation(
            new ImmediateDispatcherFactory(),
            lifecycle);
        try
        {
            var error = await Assert.ThrowsAsync<VbaProjectReferenceProbeAttemptException>(() =>
                automation.RunAsync(
                    VbaProjectReferenceProbeBaseline.SourceTemplate(templatePath),
                    WorkbookAutomationTimeouts.Default,
                    (session, cancellationToken) => session.TryResolveAsync(
                        "Widget Library",
                        new ResolvedVbaProjectReference(
                            "Widget Library",
                            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                            1,
                            0),
                        cancellationToken),
                    CancellationToken.None));

            Assert.True(WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(error));
            var cleanup = Assert.IsType<WorkbookAutomationCleanupException>(
                error.InnerException);
            var failures = Assert.IsType<AggregateException>(cleanup.InnerException)
                .InnerExceptions;
            Assert.Contains(
                failures,
                failure => WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(
                    failure));
            Assert.Contains(failures, failure => failure is IOException);
        }
        finally
        {
            if (lifecycle.WorkspaceBlockerPath is not null)
            {
                var workspacePath = Path.GetDirectoryName(
                    lifecycle.WorkspaceBlockerPath)!;
                if (Directory.Exists(workspacePath))
                {
                    Directory.Delete(workspacePath, recursive: true);
                }
            }
        }
    }

    [Fact]
    public async Task ReleasedBootstrapCleanupFailureUsesCleanupFailureReason()
    {
        var releasedCleanup = new WorkbookAutomationReleasedProcessCleanupException(
            "The bootstrap process was released, but artifact cleanup failed.");
        var lifecycle = new FakeReferenceProbeLifecycle
        {
            StartError = releasedCleanup
        };
        var automation = new ExcelComVbaProjectReferenceProbeAutomation(
            new ImmediateDispatcherFactory(),
            lifecycle);

        var error = await Assert.ThrowsAsync<VbaProjectReferenceProbeAttemptException>(() =>
            automation.RunAsync(
                VbaProjectReferenceProbeBaseline.BlankWorkbook,
                WorkbookAutomationTimeouts.Default,
                (_, _) => Task.FromResult(true),
                CancellationToken.None));

        Assert.Equal("cleanupFailure", error.ReasonCode);
        Assert.False(error.ProcessTrusted);
        Assert.Same(releasedCleanup, error.InnerException);
        Assert.False(WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(error));
    }

    [Fact]
    public async Task IdentityReadFailureRemainsCandidateLocalAfterVerifiedBaselineCleanup()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(templatePath, "source template", new UTF8Encoding(false));
        var lifecycle = new FakeReferenceProbeLifecycle
        {
            ReadIdentityError = new InvalidOperationException(
                "The returned reference identity could not be read.")
        };
        var automation = new ExcelComVbaProjectReferenceProbeAutomation(
            new ImmediateDispatcherFactory(),
            lifecycle);
        var probe = new VbaProjectReferenceAmbiguityProbe(automation);
        var registryResolution = new VbaProjectReferenceResolutionBatch(
            true,
            [],
            null,
            [
                new VbaProjectReferenceNameResolution(
                    "Widget Library",
                    "Widget Library",
                    true,
                    [
                        new ResolvedVbaProjectReference(
                            "Widget Library",
                            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                            1,
                            0),
                        new ResolvedVbaProjectReference(
                            "Widget Library",
                            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                            2,
                            0)
                    ])
            ]);

        var result = await probe.ResolveAsync(
            templatePath,
            registryResolution,
            CancellationToken.None);

        Assert.False(result.Complete);
        var reference = Assert.Single(result.References);
        Assert.Equal("identityReadFailure", reference.UnverifiedReasonCode);
        Assert.Equal(2, lifecycle.OpenedWorkbookPaths.Count);
        Assert.Equal(2, lifecycle.CloseWithoutSaveCalls);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "probeProcessUntrusted");
    }

    [Fact]
    public async Task AttemptCleanupDeadlineStopsLaterWorkAndForcesOnlyTheOwnedProcess()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(templatePath, "source template", new UTF8Encoding(false));
        var lifecycle = new FakeReferenceProbeLifecycle();
        var dispatcher = new BlockingInvocationDispatcher(blockedInvocation: 6);
        var automation = new ExcelComVbaProjectReferenceProbeAutomation(
            new FakeStaComDispatcherFactory(dispatcher),
            lifecycle);
        var probe = new VbaProjectReferenceAmbiguityProbe(
            automation,
            WorkbookAutomationTimeouts.Default with
            {
                ProcessCleanup = TimeSpan.FromMilliseconds(20)
            });
        var registryResolution = new VbaProjectReferenceResolutionBatch(
            true,
            [],
            null,
            [
                new VbaProjectReferenceNameResolution(
                    "Widget Library",
                    "Widget Library",
                    true,
                    [
                        new ResolvedVbaProjectReference(
                            "Widget Library",
                            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                            1,
                            0),
                        new ResolvedVbaProjectReference(
                            "Widget Library",
                            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                            2,
                            0)
                    ])
            ]);

        var result = await probe.ResolveAsync(
                templatePath,
                registryResolution,
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(result.Complete);
        Assert.Equal(
            "cleanupFailure",
            Assert.Single(result.References).UnverifiedReasonCode);
        Assert.Equal(
            "probeProcessUntrusted",
            Assert.Single(result.Diagnostics).Code);
        Assert.Equal(1, lifecycle.AddReferenceCalls);
        Assert.Equal(1, lifecycle.TerminateCalls);
        Assert.Equal("source template", File.ReadAllText(templatePath, Encoding.UTF8));
    }

    [Theory]
    [InlineData(4, "reference attempt")]
    [InlineData(5, "reference identity inspection")]
    public async Task AddFromGuidAndReturnedIdentityHaveIndependentDeadlines(
        int blockedInvocation,
        string expectedStage)
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(templatePath, "source template", new UTF8Encoding(false));
        var lifecycle = new FakeReferenceProbeLifecycle();
        var dispatcher = new BlockingInvocationDispatcher(blockedInvocation);
        var automation = new ExcelComVbaProjectReferenceProbeAutomation(
            new FakeStaComDispatcherFactory(dispatcher),
            lifecycle);
        var probe = new VbaProjectReferenceAmbiguityProbe(
            automation,
            WorkbookAutomationTimeouts.Default with
            {
                ReferenceAttempt = TimeSpan.FromMilliseconds(20),
                ProcessCleanup = TimeSpan.FromMilliseconds(20)
            });
        var registryResolution = new VbaProjectReferenceResolutionBatch(
            true,
            [],
            null,
            [
                new VbaProjectReferenceNameResolution(
                    "Widget Library",
                    "Widget Library",
                    true,
                    [
                        new ResolvedVbaProjectReference(
                            "Widget Library",
                            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                            1,
                            0),
                        new ResolvedVbaProjectReference(
                            "Widget Library",
                            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                            2,
                            0)
                    ])
            ]);

        var result = await probe.ResolveAsync(
                templatePath,
                registryResolution,
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        var reference = Assert.Single(result.References);
        Assert.Equal("probeTimeout", reference.UnverifiedReasonCode);
        Assert.Contains(expectedStage, reference.Message, StringComparison.Ordinal);
        Assert.Equal(1, lifecycle.StartCalls);
        Assert.Equal(1, lifecycle.TerminateCalls);
        Assert.Equal(
            "probeProcessUntrusted",
            Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ExistingSameNameBaselineReferenceSkipsAddFromGuidAndSuppliesAuthoritativeIdentity()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(templatePath, "source template", new UTF8Encoding(false));
        var existingIdentity = new ResolvedVbaProjectReference(
            "Widget Library",
            "cccccccc-cccc-cccc-cccc-cccccccccccc",
            5,
            6);
        var lifecycle = new FakeReferenceProbeLifecycle
        {
            ExistingReference = existingIdentity
        };
        var probe = new VbaProjectReferenceAmbiguityProbe(
            new ExcelComVbaProjectReferenceProbeAutomation(
                new ImmediateDispatcherFactory(),
                lifecycle));
        var registryResolution = new VbaProjectReferenceResolutionBatch(
            true,
            [],
            null,
            [
                new VbaProjectReferenceNameResolution(
                    "Widget Library",
                    "Widget Library",
                    true,
                    [
                        new ResolvedVbaProjectReference(
                            "Widget Library",
                            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                            1,
                            0),
                        new ResolvedVbaProjectReference(
                            "Widget Library",
                            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                            2,
                            0)
                    ])
            ]);

        var result = await probe.ResolveAsync(
            templatePath,
            registryResolution,
            CancellationToken.None);

        Assert.Equal(0, lifecycle.AddReferenceCalls);
        Assert.Equal(2, lifecycle.OpenedWorkbookPaths.Count);
        Assert.Equal(
            existingIdentity with
            {
                Guid = "cccccccc-cccc-cccc-cccc-cccccccccccc"
            },
            Assert.Single(Assert.Single(result.References).Matches));
    }

    [Fact]
    public void DiagnosticMetadataReadFailureDoesNotInvalidateTheReturnedIdentity()
    {
        var lifecycle = new ExcelComVbaProjectReferenceProbeAutomation
            .ExcelComVbaProjectReferenceProbeLifecycle();
        var reference = new ReferenceWithFailingDiagnosticMetadata();

        var identity = lifecycle.ReadIdentity(reference, "Widget Library");

        Assert.Equal(
            new ResolvedVbaProjectReference(
                "Widget Library",
                "{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}",
                3,
                2),
            identity);
    }

    [Fact]
    public async Task OrdinaryWorkbookOpenFailureReportsTheSelectedBaselineAsUnavailable()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(templatePath, "source template", new UTF8Encoding(false));
        var lifecycle = new FakeReferenceProbeLifecycle
        {
            OpenWorkbookError = new InvalidOperationException(
                "Excel could not open the selected source template.")
        };
        var probe = new VbaProjectReferenceAmbiguityProbe(
            new ExcelComVbaProjectReferenceProbeAutomation(
                new ImmediateDispatcherFactory(),
                lifecycle));
        var registryResolution = new VbaProjectReferenceResolutionBatch(
            true,
            [],
            null,
            [
                new VbaProjectReferenceNameResolution(
                    "Widget Library",
                    "Widget Library",
                    true,
                    [
                        new ResolvedVbaProjectReference(
                            "Widget Library",
                            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                            1,
                            0),
                        new ResolvedVbaProjectReference(
                            "Widget Library",
                            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                            2,
                            0)
                    ])
            ]);

        var result = await probe.ResolveAsync(
            templatePath,
            registryResolution,
            CancellationToken.None);

        Assert.False(result.Complete);
        Assert.Equal(
            "probeAborted",
            Assert.Single(result.References).UnverifiedReasonCode);
        Assert.Equal(
            "probeBaselineUnavailable",
            Assert.Single(result.Diagnostics).Code);
        Assert.Equal(1, lifecycle.StartCalls);
        Assert.Equal(1, lifecycle.DisposeHostCalls);
        Assert.Equal(0, lifecycle.AddReferenceCalls);
    }

    [Fact]
    public async Task VbProjectAccessFailureReportsTheSelectedBaselineAsUnavailable()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(templatePath, "source template", new UTF8Encoding(false));
        var lifecycle = new FakeReferenceProbeLifecycle
        {
            FindReferenceError = new InvalidOperationException(
                "The selected workbook did not expose its VBProject.")
        };
        var probe = new VbaProjectReferenceAmbiguityProbe(
            new ExcelComVbaProjectReferenceProbeAutomation(
                new ImmediateDispatcherFactory(),
                lifecycle));
        var registryResolution = new VbaProjectReferenceResolutionBatch(
            true,
            [],
            null,
            [
                new VbaProjectReferenceNameResolution(
                    "Widget Library",
                    "Widget Library",
                    true,
                    [
                        new ResolvedVbaProjectReference(
                            "Widget Library",
                            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                            1,
                            0),
                        new ResolvedVbaProjectReference(
                            "Widget Library",
                            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                            2,
                            0)
                    ])
            ]);

        var result = await probe.ResolveAsync(
            templatePath,
            registryResolution,
            CancellationToken.None);

        Assert.Equal(
            "probeAborted",
            Assert.Single(result.References).UnverifiedReasonCode);
        Assert.Equal(
            "probeBaselineUnavailable",
            Assert.Single(result.Diagnostics).Code);
        Assert.Equal(1, lifecycle.CloseWithoutSaveCalls);
        Assert.Equal(0, lifecycle.AddReferenceCalls);
    }

    [Fact]
    public async Task DispatcherCreationFailureReturnsAStablePreStartVbeFailure()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(templatePath, "source template", new UTF8Encoding(false));
        var probe = new VbaProjectReferenceAmbiguityProbe(
            new ExcelComVbaProjectReferenceProbeAutomation(
                new ThrowingDispatcherFactory(),
                new FakeReferenceProbeLifecycle()));
        var registryResolution = new VbaProjectReferenceResolutionBatch(
            true,
            [],
            null,
            [
                new VbaProjectReferenceNameResolution(
                    "Widget Library",
                    "Widget Library",
                    true,
                    [
                        new ResolvedVbaProjectReference(
                            "Widget Library",
                            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                            1,
                            0),
                        new ResolvedVbaProjectReference(
                            "Widget Library",
                            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                            2,
                            0)
                    ])
            ]);

        var result = await probe.ResolveAsync(
            templatePath,
            registryResolution,
            CancellationToken.None);

        Assert.Equal(
            "excelVbeFailure",
            Assert.Single(result.References).UnverifiedReasonCode);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "probeProcessUntrusted");
    }

    [Fact]
    public void AddFromGuidRpcDisconnectIsNotAConclusiveCandidateRejection()
    {
        const int rpcDisconnected = unchecked((int)0x80010108);
        var lifecycle = new ExcelComVbaProjectReferenceProbeAutomation
            .ExcelComVbaProjectReferenceProbeLifecycle();
        var workbook = new WorkbookWithAddFromGuidError(
            new COMException("The COM server disconnected.", rpcDisconnected));
        var candidate = new ResolvedVbaProjectReference(
            "Widget Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0);

        var error = Assert.Throws<COMException>(() =>
            lifecycle.AddReference(workbook, candidate));

        Assert.Equal(rpcDisconnected, error.HResult);
    }

    private sealed class ImmediateDispatcherFactory : IStaComDispatcherFactory
    {
        public IStaComDispatcher Create() => new ImmediateDispatcher();
    }

    private sealed class FakeStaComDispatcherFactory(IStaComDispatcher dispatcher)
        : IStaComDispatcherFactory
    {
        public IStaComDispatcher Create() => dispatcher;
    }

    private sealed class ThrowingDispatcherFactory : IStaComDispatcherFactory
    {
        public IStaComDispatcher Create()
            => throw new InvalidOperationException(
                "The Excel STA dispatcher could not be created.");
    }

    private sealed class ImmediateDispatcher : IStaComDispatcher
    {
        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(operation());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DisposeFailingDispatcher(Exception error) : IStaComDispatcher
    {
        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(operation());
        }

        public ValueTask DisposeAsync()
            => new(Task.FromException(error));
    }

    private sealed class BlockingInvocationDispatcher(int blockedInvocation)
        : IStaComDispatcher
    {
        private readonly TaskCompletionSource<object?> blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int invocationCount;

        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            invocationCount++;
            if (invocationCount == blockedInvocation)
            {
                return blocked.Task.ContinueWith(
                    static task => (T)task.Result!,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(operation());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ReferenceWithFailingDiagnosticMetadata : DynamicObject
    {
        public override bool TryGetMember(
            GetMemberBinder binder,
            out object? result)
        {
            result = binder.Name switch
            {
                "Guid" => "{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}",
                "Major" => 3,
                "Minor" => 2,
                "Description" or "FullPath" => throw new InvalidOperationException(
                    "Diagnostic metadata is unavailable."),
                _ => null
            };
            return result is not null;
        }
    }

    private sealed class WorkbookWithAddFromGuidError(Exception error)
        : DynamicObject
    {
        private readonly VbProjectWithAddFromGuidError vbProject = new(error);

        public override bool TryGetMember(
            GetMemberBinder binder,
            out object? result)
        {
            result = binder.Name == "VBProject" ? vbProject : null;
            return result is not null;
        }
    }

    private sealed class VbProjectWithAddFromGuidError(Exception error)
        : DynamicObject
    {
        private readonly ReferencesWithAddFromGuidError references = new(error);

        public override bool TryGetMember(
            GetMemberBinder binder,
            out object? result)
        {
            result = binder.Name == "References" ? references : null;
            return result is not null;
        }
    }

    private sealed class ReferencesWithAddFromGuidError(Exception error)
        : DynamicObject
    {
        public override bool TryInvokeMember(
            InvokeMemberBinder binder,
            object?[]? args,
            out object? result)
        {
            result = null;
            if (binder.Name == "AddFromGuid")
            {
                throw error;
            }

            return false;
        }
    }

    private static VbaProjectReferenceResolutionBatch CreateAmbiguousRegistryResolution()
        => new(
            true,
            [],
            null,
            [
                new VbaProjectReferenceNameResolution(
                    "Widget Library",
                    "Widget Library",
                    true,
                    [
                        new ResolvedVbaProjectReference(
                            "Widget Library",
                            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                            1,
                            0),
                        new ResolvedVbaProjectReference(
                            "Widget Library",
                            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                            2,
                            0)
                    ])
            ]);

    private sealed class FakeReferenceProbeLifecycle : IExcelComVbaProjectReferenceProbeLifecycle
    {
        private readonly FakeOwnedProcess owner = new();

        public int StartCalls { get; private set; }

        public int DisposeHostCalls { get; private set; }

        public int CloseWithoutSaveCalls { get; private set; }

        public int AddReferenceCalls { get; private set; }

        public int BlankWorkbookCreations { get; private set; }

        public int ReleaseReferenceCalls { get; private set; }

        public int TerminateCalls => owner.TerminateCalls;

        public bool OwnerHasExited => owner.HasExited;

        public List<string> OpenedWorkbookPaths { get; } = [];

        public List<string> ObservedBaselineContents { get; } = [];

        public List<object> BlankWorkbooks { get; } = [];

        public bool CreateWorkspaceBlockerOnOpen { get; init; }

        public string? WorkspaceBlockerPath { get; private set; }

        public Exception? ReadIdentityError { get; init; }

        public ResolvedVbaProjectReference? ExistingReference { get; init; }

        public Exception? OpenWorkbookError { get; init; }

        public Exception? CreateBlankWorkbookError { get; init; }

        public Exception? FindReferenceError { get; init; }

        public Exception? DisposeHostError { get; init; }

        public Exception? StartError { get; init; }

        public Exception? OwnerDisposeError
        {
            get => owner.DisposeError;
            init => owner.DisposeError = value;
        }

        public object Start(
            OwnedExcelTerminationController terminationController,
            CancellationToken cancellationToken)
        {
            StartCalls++;
            if (StartError is not null)
            {
                throw StartError;
            }

            terminationController.Attach(owner);
            return new object();
        }

        public object OpenWorkbook(object host, string workbookPath)
        {
            OpenedWorkbookPaths.Add(workbookPath);
            ObservedBaselineContents.Add(File.ReadAllText(workbookPath, Encoding.UTF8));
            if (CreateWorkspaceBlockerOnOpen && WorkspaceBlockerPath is null)
            {
                WorkspaceBlockerPath = Path.Combine(
                    Path.GetDirectoryName(workbookPath)!,
                    "workspace-cleanup-blocker.tmp");
                File.WriteAllText(
                    WorkspaceBlockerPath,
                    "block final non-recursive workspace deletion",
                    new UTF8Encoding(false));
            }

            if (OpenWorkbookError is not null)
            {
                throw OpenWorkbookError;
            }

            return workbookPath;
        }

        public object CreateBlankWorkbook(object host)
        {
            BlankWorkbookCreations++;
            if (CreateBlankWorkbookError is not null)
            {
                throw CreateBlankWorkbookError;
            }

            var workbook = new object();
            BlankWorkbooks.Add(workbook);
            return workbook;
        }

        public object? FindReference(object workbook, string referenceName)
        {
            if (FindReferenceError is not null)
            {
                throw FindReferenceError;
            }

            return ExistingReference;
        }

        public object AddReference(
            object workbook,
            ResolvedVbaProjectReference candidate)
        {
            AddReferenceCalls++;
            return candidate;
        }

        public ResolvedVbaProjectReference ReadIdentity(
            object reference,
            string referenceName)
        {
            if (ReadIdentityError is not null)
            {
                throw ReadIdentityError;
            }

            return (ResolvedVbaProjectReference)reference;
        }

        public void ReleaseReference(object? reference)
        {
            ReleaseReferenceCalls++;
        }

        public void CloseWorkbookWithoutSave(object workbook)
            => CloseWithoutSaveCalls++;

        public void DisposeHost(object host, TimeSpan cleanupGrace)
        {
            DisposeHostCalls++;
            owner.Complete();
            if (DisposeHostError is not null)
            {
                throw DisposeHostError;
            }
        }
    }

    private sealed class FakeOwnedProcess : IOwnedExcelProcessControl
    {
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasExited => completion.Task.IsCompleted;

        public Task Completion => completion.Task;

        public int TerminateCalls { get; private set; }

        public Exception? DisposeError { get; set; }

        public Task TerminateAsync()
        {
            TerminateCalls++;
            completion.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
            => DisposeError is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeError);

        public void Complete() => completion.TrySetResult();
    }
}
