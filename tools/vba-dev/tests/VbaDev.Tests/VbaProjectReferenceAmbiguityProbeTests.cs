using VbaDev.App.References;
using VbaDev.App.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class VbaProjectReferenceAmbiguityProbeTests
{
    [Fact]
    public async Task CoalescesRegistryCandidatesThatReturnTheSameCanonicalIdentity()
    {
        var returnedIdentity = new ResolvedVbaProjectReference(
            "VBE Description",
            "{CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC}",
            3,
            0);
        var session = new FakeReferenceProbeSession(_ =>
            VbaProjectReferenceProbeAttemptResult.Accepted(returnedIdentity));
        var automation = new FakeReferenceProbeAutomation(session);
        var probe = new VbaProjectReferenceAmbiguityProbe(automation);
        var registryResolution = new VbaProjectReferenceResolutionBatch(
            true,
            [],
            null,
            [
                new VbaProjectReferenceNameResolution(
                    "Widget Library",
                    "WIDGET LIBRARY",
                    true,
                    [
                        new ResolvedVbaProjectReference(
                            "WIDGET LIBRARY",
                            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                            1,
                            0),
                        new ResolvedVbaProjectReference(
                            "WIDGET LIBRARY",
                            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                            2,
                            0)
                    ])
            ]);

        var result = await probe.ResolveAsync(
            "C:\\project\\src\\Book1\\Book1.xlsm",
            registryResolution,
            CancellationToken.None);

        Assert.True(result.Complete);
        Assert.Equal(1, automation.RunCount);
        Assert.Equal(2, session.Attempts.Count);
        var resolution = Assert.Single(result.References);
        Assert.Equal(
            new ResolvedVbaProjectReference(
                "WIDGET LIBRARY",
                "cccccccc-cccc-cccc-cccc-cccccccccccc",
                3,
                0),
            Assert.Single(resolution.Matches));
    }

    [Fact]
    public async Task FallsBackThroughOneGuidLineageInDescendingVersionOrder()
    {
        var rejected = new ResolvedVbaProjectReference(
            "Widget Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            3,
            0);
        var accepted = rejected with { Major = 2 };
        var session = new FakeReferenceProbeSession(candidate =>
            candidate.Major == 3
                ? VbaProjectReferenceProbeAttemptResult.Rejected()
                : VbaProjectReferenceProbeAttemptResult.Accepted(candidate));
        var probe = new VbaProjectReferenceAmbiguityProbe(
            new FakeReferenceProbeAutomation(session));
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
                        rejected,
                        new ResolvedVbaProjectReference(
                            "Widget Library",
                            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                            1,
                            0)
                    ],
                    [
                        new VbaProjectReferenceCandidateLineage(
                            rejected.Guid,
                            [accepted, rejected]),
                        new VbaProjectReferenceCandidateLineage(
                            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                            [
                                new ResolvedVbaProjectReference(
                                    "Widget Library",
                                    "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                                    1,
                                    0)
                            ])
                    ])
            ]);

        var result = await probe.ResolveAsync(
            "C:\\project\\src\\Book1\\Book1.xlsm",
            registryResolution,
            CancellationToken.None);

        Assert.Equal(
            [(3, 0), (2, 0), (1, 0)],
            session.Attempts.Select(candidate => (candidate.Major, candidate.Minor)));
        Assert.Equal(
            [
                ("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 2, 0),
                ("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", 1, 0)
            ],
            Assert.Single(result.References).Matches
                .Select(match => (match.Guid, match.Major, match.Minor)));
    }

    [Fact]
    public async Task TrustedIdentityReadFailureLeavesItsNameUnverifiedAndContinuesLaterProbeWork()
    {
        var session = new FakeReferenceProbeSession(candidate =>
        {
            if (candidate.Name == "First Library" && candidate.Guid.StartsWith("a", StringComparison.Ordinal))
            {
                throw new VbaProjectReferenceProbeAttemptException(
                    "identityReadFailure",
                    "The returned reference identity could not be read.",
                    processTrusted: true);
            }

            return VbaProjectReferenceProbeAttemptResult.Accepted(candidate);
        });
        var probe = new VbaProjectReferenceAmbiguityProbe(
            new FakeReferenceProbeAutomation(session));
        var registryResolution = new VbaProjectReferenceResolutionBatch(
            true,
            [],
            null,
            [
                CreateAmbiguousResolution(
                    "First Library",
                    "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                CreateAmbiguousResolution(
                    "Second Library",
                    "cccccccc-cccc-cccc-cccc-cccccccccccc",
                    "dddddddd-dddd-dddd-dddd-dddddddddddd")
            ]);

        var result = await probe.ResolveAsync(
            "C:\\project\\src\\Book1\\Book1.xlsm",
            registryResolution,
            CancellationToken.None);

        Assert.False(result.Complete);
        Assert.Equal(4, session.Attempts.Count);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("identityReadFailure", result.References[0].UnverifiedReasonCode);
        Assert.Equal(2, result.References[0].Candidates.Count);
        Assert.Null(result.References[1].UnverifiedReasonCode);
        Assert.Equal(2, result.References[1].Matches.Count);
    }

    [Fact]
    public async Task InvalidReturnedIdentityLeavesItsNameUnverifiedAndContinuesLaterProbeWork()
    {
        var session = new FakeReferenceProbeSession(candidate =>
            candidate.Name == "First Library" &&
            candidate.Guid.StartsWith("a", StringComparison.Ordinal)
                ? VbaProjectReferenceProbeAttemptResult.Accepted(
                    candidate with { Guid = "not-a-guid" })
                : VbaProjectReferenceProbeAttemptResult.Accepted(candidate));
        var probe = new VbaProjectReferenceAmbiguityProbe(
            new FakeReferenceProbeAutomation(session));
        var registryResolution = new VbaProjectReferenceResolutionBatch(
            true,
            [],
            null,
            [
                CreateAmbiguousResolution(
                    "First Library",
                    "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                CreateAmbiguousResolution(
                    "Second Library",
                    "cccccccc-cccc-cccc-cccc-cccccccccccc",
                    "dddddddd-dddd-dddd-dddd-dddddddddddd")
            ]);

        var result = await probe.ResolveAsync(
            "C:\\project\\src\\Book1\\Book1.xlsm",
            registryResolution,
            CancellationToken.None);

        Assert.False(result.Complete);
        Assert.Equal(4, session.Attempts.Count);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("identityReadFailure", result.References[0].UnverifiedReasonCode);
        Assert.Null(result.References[1].UnverifiedReasonCode);
        Assert.Equal(2, result.References[1].Matches.Count);
    }

    [Fact]
    public async Task ProbeTimeoutStopsLaterVbeWorkWithoutStartingAReplacementProcess()
    {
        var session = new FakeReferenceProbeSession(_ =>
            throw new VbaProjectReferenceProbeAttemptException(
                "probeTimeout",
                "The VBE reference attempt timed out.",
                processTrusted: false));
        var automation = new FakeReferenceProbeAutomation(session);
        var probe = new VbaProjectReferenceAmbiguityProbe(automation);
        var registryResolution = new VbaProjectReferenceResolutionBatch(
            true,
            [],
            null,
            [
                CreateAmbiguousResolution(
                    "First Library",
                    "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                CreateAmbiguousResolution(
                    "Second Library",
                    "cccccccc-cccc-cccc-cccc-cccccccccccc",
                    "dddddddd-dddd-dddd-dddd-dddddddddddd")
            ]);

        var result = await probe.ResolveAsync(
            "C:\\project\\src\\Book1\\Book1.xlsm",
            registryResolution,
            CancellationToken.None);

        Assert.False(result.Complete);
        Assert.Equal(1, automation.RunCount);
        Assert.Single(session.Attempts);
        Assert.Equal("probeTimeout", result.References[0].UnverifiedReasonCode);
        Assert.Equal("probeAborted", result.References[1].UnverifiedReasonCode);
        Assert.Equal(
            "probeProcessUntrusted",
            Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task BaselineUnavailableAbortsOnlyProbeDependentNamesWithoutDistrustingAProcess()
    {
        var automation = new ThrowingReferenceProbeAutomation(
            new VbaProjectReferenceProbeBaselineException(
                "The selected source template could not be copied."));
        var probe = new VbaProjectReferenceAmbiguityProbe(automation);
        var registryResolution = new VbaProjectReferenceResolutionBatch(
            true,
            [],
            null,
            [
                new VbaProjectReferenceNameResolution(
                    "Unique Library",
                    "Unique Library",
                    true,
                    [
                        new ResolvedVbaProjectReference(
                            "Unique Library",
                            "11111111-1111-1111-1111-111111111111",
                            1,
                            0)
                    ]),
                CreateAmbiguousResolution(
                    "Ambiguous Library",
                    "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
            ]);

        var result = await probe.ResolveAsync(
            "C:\\project\\src\\Book1\\missing.xlsm",
            registryResolution,
            CancellationToken.None);

        Assert.False(result.Complete);
        Assert.Null(result.References[0].UnverifiedReasonCode);
        Assert.Equal("probeAborted", result.References[1].UnverifiedReasonCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("probeBaselineUnavailable", diagnostic.Code);
        Assert.DoesNotContain(
            result.Diagnostics,
            item => item.Code == "probeProcessUntrusted");
    }

    [Fact]
    public async Task ProcessFailureBeforeFirstAttemptMarksTheCurrentNameAndAbortsLaterVbeWork()
    {
        var automation = new ThrowingReferenceProbeAutomation(
            new VbaProjectReferenceProbeAttemptException(
                "excelVbeFailure",
                "The owned Excel process failed during startup.",
                processTrusted: false));
        var probe = new VbaProjectReferenceAmbiguityProbe(automation);
        var registryResolution = new VbaProjectReferenceResolutionBatch(
            true,
            [],
            null,
            [
                new VbaProjectReferenceNameResolution(
                    "Unique Library",
                    "Unique Library",
                    true,
                    [
                        new ResolvedVbaProjectReference(
                            "Unique Library",
                            "11111111-1111-1111-1111-111111111111",
                            1,
                            0)
                    ]),
                CreateAmbiguousResolution(
                    "First Ambiguous Library",
                    "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                CreateAmbiguousResolution(
                    "Second Ambiguous Library",
                    "cccccccc-cccc-cccc-cccc-cccccccccccc",
                    "dddddddd-dddd-dddd-dddd-dddddddddddd")
            ]);

        var result = await probe.ResolveAsync(
            "C:\\project\\src\\Book1\\Book1.xlsm",
            registryResolution,
            CancellationToken.None);

        Assert.False(result.Complete);
        Assert.Null(result.References[0].UnverifiedReasonCode);
        Assert.Equal("excelVbeFailure", result.References[1].UnverifiedReasonCode);
        Assert.Equal("probeAborted", result.References[2].UnverifiedReasonCode);
        Assert.Equal(
            "probeProcessUntrusted",
            Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task FinalLifecycleCleanupFailurePreservesEarlierConclusiveProbeResults()
    {
        var session = new FakeReferenceProbeSession(candidate =>
        {
            var returnedGuid = candidate.Name == "First Library"
                ? "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"
                : "ffffffff-ffff-ffff-ffff-ffffffffffff";
            return VbaProjectReferenceProbeAttemptResult.Accepted(
                candidate with { Guid = returnedGuid, Major = 9, Minor = 8 });
        });
        var probe = new VbaProjectReferenceAmbiguityProbe(
            new ThrowAfterOperationReferenceProbeAutomation(session));
        var registryResolution = new VbaProjectReferenceResolutionBatch(
            true,
            [],
            null,
            [
                CreateAmbiguousResolution(
                    "First Library",
                    "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                CreateAmbiguousResolution(
                    "Second Library",
                    "cccccccc-cccc-cccc-cccc-cccccccccccc",
                    "dddddddd-dddd-dddd-dddd-dddddddddddd")
            ]);

        var result = await probe.ResolveAsync(
            "C:\\project\\src\\Book1\\Book1.xlsm",
            registryResolution,
            CancellationToken.None);

        Assert.False(result.Complete);
        Assert.Equal(
            "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
            Assert.Single(result.References[0].Matches).Guid);
        Assert.Null(result.References[0].UnverifiedReasonCode);
        Assert.Empty(result.References[1].Matches);
        Assert.Equal("cleanupFailure", result.References[1].UnverifiedReasonCode);
        Assert.Equal(
            "probeProcessUntrusted",
            Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task CancellationMarksCurrentAndRemainingProbeDependentNamesWithoutDistrustingTheProcess()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new FakeReferenceProbeSession(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        var probe = new VbaProjectReferenceAmbiguityProbe(
            new FakeReferenceProbeAutomation(session));
        var registryResolution = new VbaProjectReferenceResolutionBatch(
            true,
            [],
            null,
            [
                new VbaProjectReferenceNameResolution(
                    "Unique Library",
                    "Unique Library",
                    true,
                    [
                        new ResolvedVbaProjectReference(
                            "Unique Library",
                            "11111111-1111-1111-1111-111111111111",
                            1,
                            0)
                    ]),
                CreateAmbiguousResolution(
                    "First Ambiguous Library",
                    "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                CreateAmbiguousResolution(
                    "Second Ambiguous Library",
                    "cccccccc-cccc-cccc-cccc-cccccccccccc",
                    "dddddddd-dddd-dddd-dddd-dddddddddddd")
            ]);

        var result = await probe.ResolveAsync(
            "C:\\project\\src\\Book1\\Book1.xlsm",
            registryResolution,
            cancellation.Token);

        Assert.False(result.Complete);
        Assert.Null(result.References[0].UnverifiedReasonCode);
        Assert.Equal("cancelled", result.References[1].UnverifiedReasonCode);
        Assert.Equal("cancelled", result.References[2].UnverifiedReasonCode);
        Assert.Equal(
            "operationCancelled",
            Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task CancellationBeforeFirstAttemptMarksEveryProbeDependentNameAsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var automation = new ThrowingReferenceProbeAutomation(
            new OperationCanceledException(cancellation.Token));
        var probe = new VbaProjectReferenceAmbiguityProbe(automation);
        var registryResolution = new VbaProjectReferenceResolutionBatch(
            true,
            [],
            null,
            [
                new VbaProjectReferenceNameResolution(
                    "Unique Library",
                    "Unique Library",
                    true,
                    [
                        new ResolvedVbaProjectReference(
                            "Unique Library",
                            "11111111-1111-1111-1111-111111111111",
                            1,
                            0)
                    ]),
                CreateAmbiguousResolution(
                    "First Ambiguous Library",
                    "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                CreateAmbiguousResolution(
                    "Second Ambiguous Library",
                    "cccccccc-cccc-cccc-cccc-cccccccccccc",
                    "dddddddd-dddd-dddd-dddd-dddddddddddd")
            ]);

        var result = await probe.ResolveAsync(
            "C:\\project\\src\\Book1\\Book1.xlsm",
            registryResolution,
            cancellation.Token);

        Assert.False(result.Complete);
        Assert.Null(result.References[0].UnverifiedReasonCode);
        Assert.Equal("cancelled", result.References[1].UnverifiedReasonCode);
        Assert.Equal("cancelled", result.References[2].UnverifiedReasonCode);
        Assert.Equal(
            "operationCancelled",
            Assert.Single(result.Diagnostics).Code);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "probeProcessUntrusted");
    }

    private static VbaProjectReferenceNameResolution CreateAmbiguousResolution(
        string name,
        string firstGuid,
        string secondGuid)
        => new(
            name,
            name,
            true,
            [
                new ResolvedVbaProjectReference(name, firstGuid, 1, 0),
                new ResolvedVbaProjectReference(name, secondGuid, 1, 0)
            ]);

    private sealed class FakeReferenceProbeAutomation(
        IVbaProjectReferenceProbeSession session)
        : IVbaProjectReferenceProbeAutomation
    {
        public int RunCount { get; private set; }

        public async Task<TResult> RunAsync<TResult>(
            string baselineWorkbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<IVbaProjectReferenceProbeSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
        {
            RunCount++;
            return await operation(session, cancellationToken);
        }
    }

    private sealed class FakeReferenceProbeSession(
        Func<ResolvedVbaProjectReference, VbaProjectReferenceProbeAttemptResult> attempt)
        : IVbaProjectReferenceProbeSession
    {
        public List<ResolvedVbaProjectReference> Attempts { get; } = [];

        public Task<VbaProjectReferenceProbeAttemptResult> TryResolveAsync(
            string baselineWorkbookPath,
            string referenceName,
            ResolvedVbaProjectReference candidate,
            CancellationToken cancellationToken)
        {
            Attempts.Add(candidate);
            return Task.FromResult(attempt(candidate));
        }
    }

    private sealed class ThrowingReferenceProbeAutomation(Exception exception)
        : IVbaProjectReferenceProbeAutomation
    {
        public Task<TResult> RunAsync<TResult>(
            string baselineWorkbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<IVbaProjectReferenceProbeSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
            => Task.FromException<TResult>(exception);
    }

    private sealed class ThrowAfterOperationReferenceProbeAutomation(
        IVbaProjectReferenceProbeSession session)
        : IVbaProjectReferenceProbeAutomation
    {
        public async Task<TResult> RunAsync<TResult>(
            string baselineWorkbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<IVbaProjectReferenceProbeSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
        {
            var partialResult = await operation(session, cancellationToken);
            var exception = new VbaProjectReferenceProbeAttemptException(
                "cleanupFailure",
                "The final owned-process cleanup could not be verified.",
                processTrusted: false,
                partialResult: partialResult);
            throw exception;
        }
    }
}
