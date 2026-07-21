using System.Collections.Immutable;
using VbaDev.App.Debugging;
using VbaDev.App.Projects;
using VbaDev.Domain;
using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaDev.Tests;

public sealed class DebugLaunchCoordinatorTests
{
    [Fact]
    public async Task AllBreakpointMappingsCompleteBeforeBuildAndAnyMappingFailureHasNoLaunchSideEffects()
    {
        var context = CreateContext();
        var first = new DebugSourceBreakpoint(
            Path.Combine(context.DocumentSourceSetPath, "First.bas"),
            EditorLine: 4);
        var second = new DebugSourceBreakpoint(
            Path.Combine(context.DocumentSourceSetPath, "Second.bas"),
            EditorLine: 8);
        var events = new List<string>();
        var coordinator = new DebugLaunchCoordinator(
            new FakeDebugWorkbookBuilder(events),
            new FakeVbeDebugSessionFactory(events, new FakeVbeDebugSession(events)),
            new FakeBreakpointSourceMapper(
                events,
                breakpoint => breakpoint == first
                    ? Mapped(first, "First", 3, "    firstValue = 1")
                    : throw new DebugSetupException("The second breakpoint is invalid.")));
        var request = new DebugLaunchRequest(
            context,
            new DebugTargetProcedure("DebugModule", "RunTarget"),
            CreateSourceSnapshot())
        {
            BreakpointPlan = new DebugBreakpointPlan([first, second], [])
        };

        var error = await Assert.ThrowsAsync<DebugSetupException>(() => coordinator.LaunchAsync(
            request,
            new RecordingDebugLaunchEventSink(events),
            CancellationToken.None));

        Assert.Equal("The second breakpoint is invalid.", error.Message);
        Assert.Equal(
            [
                $"map:{first.SourcePath}:4",
                $"map:{second.SourcePath}:8"
            ],
            events);
    }

    [Theory]
    [InlineData(UnsupportedDebugBreakpointKind.Conditional)]
    [InlineData(UnsupportedDebugBreakpointKind.HitCondition)]
    [InlineData(UnsupportedDebugBreakpointKind.Logpoint)]
    [InlineData(UnsupportedDebugBreakpointKind.Column)]
    [InlineData(UnsupportedDebugBreakpointKind.Mode)]
    [InlineData(UnsupportedDebugBreakpointKind.Function)]
    [InlineData(UnsupportedDebugBreakpointKind.Exception)]
    [InlineData(UnsupportedDebugBreakpointKind.Data)]
    public async Task UnsupportedBreakpointDiscoveryFailsBeforeAnyLaunchSideEffect(
        UnsupportedDebugBreakpointKind kind)
    {
        var context = CreateContext();
        var breakpoint = new DebugSourceBreakpoint(
            Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas"),
            EditorLine: 4);
        var events = new List<string>();
        var coordinator = new DebugLaunchCoordinator(
            new FakeDebugWorkbookBuilder(events),
            new FakeVbeDebugSessionFactory(events, new FakeVbeDebugSession(events)),
            new FakeBreakpointSourceMapper(
                events,
                Mapped(breakpoint, "DebugModule", 4, "    value = 1")));
        var request = new DebugLaunchRequest(
            context,
            new DebugTargetProcedure("DebugModule", "RunTarget"),
            CreateSourceSnapshot())
        {
            BreakpointPlan = new DebugBreakpointPlan(
                [breakpoint],
                [new UnsupportedDebugBreakpoint(kind, $"unsupported {kind}")])
        };

        var error = await Assert.ThrowsAsync<DebugSetupException>(() => coordinator.LaunchAsync(
            request,
            new RecordingDebugLifecycleSink(),
            CancellationToken.None));

        Assert.Contains(kind.ToString(), error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events);
    }

    [Fact]
    public async Task DuplicateParticipatingSourcePositionsFailBeforeAnyLaunchSideEffect()
    {
        var context = CreateContext();
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var events = new List<string>();
        var coordinator = new DebugLaunchCoordinator(
            new FakeDebugWorkbookBuilder(events),
            new FakeVbeDebugSessionFactory(events, new FakeVbeDebugSession(events)),
            new FakeBreakpointSourceMapper(
                events,
                Mapped(
                    new DebugSourceBreakpoint(sourcePath, EditorLine: 4),
                    "DebugModule",
                    4,
                    "    value = 1")));
        var request = new DebugLaunchRequest(
            context,
            new DebugTargetProcedure("DebugModule", "RunTarget"),
            CreateSourceSnapshot())
        {
            BreakpointPlan = new DebugBreakpointPlan(
                [
                    new DebugSourceBreakpoint(sourcePath, EditorLine: 4),
                    new DebugSourceBreakpoint(sourcePath.ToUpperInvariant(), EditorLine: 4)
                ],
                [])
        };

        var error = await Assert.ThrowsAsync<DebugSetupException>(() => coordinator.LaunchAsync(
            request,
            new RecordingDebugLifecycleSink(),
            CancellationToken.None));

        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events);
    }

    [Fact]
    public async Task MultipleParticipatingBreakpointsAreAllVerifiedBeforeTheTargetRuns()
    {
        var context = CreateContext();
        var firstSourcePath = Path.Combine(context.DocumentSourceSetPath, "First.bas");
        var secondSourcePath = Path.Combine(context.DocumentSourceSetPath, "Second.bas");
        var first = new DebugSourceBreakpoint(firstSourcePath, EditorLine: 4);
        var second = new DebugSourceBreakpoint(secondSourcePath, EditorLine: 8);
        var firstMapped = Mapped(first, "First", 3, "    firstValue = 1");
        var secondMapped = Mapped(second, "Second", 7, "    secondValue = 2");
        var events = new List<string>();
        var vbeSession = new FakeVbeDebugSession(events);
        var coordinator = new DebugLaunchCoordinator(
            new FakeDebugWorkbookBuilder(events),
            new FakeVbeDebugSessionFactory(events, vbeSession),
            new FakeBreakpointSourceMapper(
                events,
                breakpoint => breakpoint == first ? firstMapped : secondMapped));
        var request = new DebugLaunchRequest(
            context,
            new DebugTargetProcedure("DebugModule", "RunTarget"),
            CreateSourceSnapshot())
        {
            BreakpointPlan = new DebugBreakpointPlan([first, second], [])
        };

        var running = await coordinator.LaunchAsync(
            request,
            new RecordingDebugLaunchEventSink(events),
            CancellationToken.None);

        Assert.Equal(
            [
                $"map:{firstSourcePath}:4",
                $"map:{secondSourcePath}:8",
                "build:Book1",
                "start-visible",
                $"open:{context.BinDocumentPath}",
                "set:First:3:    firstValue = 1",
                "set:Second:7:    secondValue = 2",
                $"verified:{firstSourcePath}:4",
                $"verified:{secondSourcePath}:8",
                "run:DebugModule.RunTarget"
            ],
            events);

        vbeSession.Exit(0);
        await running.Completion;
    }

    [Fact]
    public async Task ASecondNativeBreakpointFailurePreventsTargetExecution()
    {
        var context = CreateContext();
        var first = new DebugSourceBreakpoint(
            Path.Combine(context.DocumentSourceSetPath, "First.bas"),
            EditorLine: 4);
        var second = new DebugSourceBreakpoint(
            Path.Combine(context.DocumentSourceSetPath, "Second.bas"),
            EditorLine: 8);
        var events = new List<string>();
        var vbeSession = new FakeVbeDebugSession(events)
        {
            SetError = new DebugSetupException("The second native breakpoint failed."),
            SetErrorAtCall = 2
        };
        var coordinator = new DebugLaunchCoordinator(
            new FakeDebugWorkbookBuilder(events),
            new FakeVbeDebugSessionFactory(events, vbeSession),
            new FakeBreakpointSourceMapper(
                events,
                breakpoint => Mapped(
                    breakpoint,
                    Path.GetFileNameWithoutExtension(breakpoint.SourcePath),
                    breakpoint.EditorLine,
                    $"line {breakpoint.EditorLine}")));
        var request = new DebugLaunchRequest(
            context,
            new DebugTargetProcedure("DebugModule", "RunTarget"),
            CreateSourceSnapshot())
        {
            BreakpointPlan = new DebugBreakpointPlan([first, second], [])
        };

        var error = await Assert.ThrowsAsync<DebugSetupException>(() => coordinator.LaunchAsync(
            request,
            new RecordingDebugLaunchEventSink(events),
            CancellationToken.None));

        Assert.Equal("The second native breakpoint failed.", error.Message);
        Assert.DoesNotContain($"verified:{first.SourcePath}:4", events);
        Assert.DoesNotContain($"verified:{second.SourcePath}:8", events);
        Assert.Equal(2, events.Count(item => item.StartsWith("set:", StringComparison.Ordinal)));
        Assert.DoesNotContain(events, item => item.StartsWith("run:", StringComparison.Ordinal));
        Assert.True(vbeSession.Terminated);
        Assert.True(vbeSession.Disposed);
    }

    [Fact]
    public async Task OneBreakpointIsSetAndVerifiedBeforeTheTargetRuns()
    {
        var context = CreateContext();
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var requested = new DebugSourceBreakpoint(sourcePath, EditorLine: 4);
        var mapped = Mapped(requested, "DebugModule", 4, "    value = 1");
        var snapshot = CreateSourceSnapshot() with
        {
            Breakpoints = [requested]
        };
        var events = new List<string>();
        var vbeSession = new FakeVbeDebugSession(events);
        var coordinator = new DebugLaunchCoordinator(
            new FakeDebugWorkbookBuilder(events),
            new FakeVbeDebugSessionFactory(events, vbeSession),
            new FakeBreakpointSourceMapper(events, mapped));

        var running = await coordinator.LaunchAsync(
            new DebugLaunchRequest(
                context,
                new DebugTargetProcedure("DebugModule", "RunTarget"),
                snapshot),
            new RecordingDebugLaunchEventSink(events),
            CancellationToken.None);

        Assert.Equal(
            [
                $"map:{sourcePath}:4",
                "build:Book1",
                "start-visible",
                $"open:{context.BinDocumentPath}",
                "set:DebugModule:4:    value = 1",
                $"verified:{sourcePath}:4",
                "run:DebugModule.RunTarget"
            ],
            events);

        vbeSession.Exit(0);
        await running.Completion;
    }

    [Fact]
    public async Task AFailedNativeBreakpointIsNeitherVerifiedNorFollowedByTargetExecution()
    {
        var context = CreateContext();
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var requested = new DebugSourceBreakpoint(sourcePath, EditorLine: 4);
        var mapped = Mapped(requested, "DebugModule", 4, "    value = 1");
        var events = new List<string>();
        var vbeSession = new FakeVbeDebugSession(events)
        {
            SetError = new DebugSetupException("The native Toggle Breakpoint command is disabled.")
        };
        var coordinator = new DebugLaunchCoordinator(
            new FakeDebugWorkbookBuilder(events),
            new FakeVbeDebugSessionFactory(events, vbeSession),
            new FakeBreakpointSourceMapper(events, mapped));
        var snapshot = CreateSourceSnapshot() with
        {
            Breakpoints = [requested]
        };

        var error = await Assert.ThrowsAsync<DebugSetupException>(() => coordinator.LaunchAsync(
            new DebugLaunchRequest(
                context,
                new DebugTargetProcedure("DebugModule", "RunTarget"),
                snapshot),
            new RecordingDebugLaunchEventSink(events),
            CancellationToken.None));

        Assert.Equal("The native Toggle Breakpoint command is disabled.", error.Message);
        Assert.DoesNotContain(events, item => item.StartsWith("verified:", StringComparison.Ordinal));
        Assert.DoesNotContain(events, item => item.StartsWith("run:", StringComparison.Ordinal));
        Assert.True(vbeSession.Terminated);
        Assert.True(vbeSession.Disposed);
    }

    [Fact]
    public async Task AnExplicitZeroBreakpointLaunchBuildsThenRunsTheManifestBinWorkbook()
    {
        var context = CreateContext();
        var events = new List<string>();
        var builder = new FakeDebugWorkbookBuilder(events);
        var vbeSession = new FakeVbeDebugSession(events);
        var coordinator = new DebugLaunchCoordinator(
            builder,
            new FakeVbeDebugSessionFactory(events, vbeSession));

        var running = await coordinator.LaunchAsync(
            new DebugLaunchRequest(
                context,
                new DebugTargetProcedure("DebugModule", "RunTarget"),
                CreateSourceSnapshot()),
            new RecordingDebugLifecycleSink(),
            CancellationToken.None);

        Assert.Equal(31415, running.ProcessId);
        Assert.Equal(
            [
                "build:Book1",
                "start-visible",
                $"open:{context.BinDocumentPath}",
                "run:DebugModule.RunTarget"
            ],
            events);

        vbeSession.Exit(0);
        Assert.Equal(0, (await running.Completion).ExitCode);
    }

    [Fact]
    public async Task BuildWarningsAreReportedWithoutPreventingVisibleExcel()
    {
        var events = new List<string>();
        var vbeSession = new FakeVbeDebugSession(events);
        var lifecycle = new RecordingDebugLifecycleSink();
        var coordinator = new DebugLaunchCoordinator(
            new FakeDebugWorkbookBuilder(events, ["WARN Book1/Protected Library remains."]),
            new FakeVbeDebugSessionFactory(events, vbeSession));

        var running = await coordinator.LaunchAsync(
            new DebugLaunchRequest(
                CreateContext(),
                new DebugTargetProcedure("DebugModule", "RunTarget"),
                CreateSourceSnapshot()),
            lifecycle,
            CancellationToken.None);

        Assert.Contains("WARN Book1/Protected Library remains.", lifecycle.Output);
        Assert.Contains("start-visible", events);

        vbeSession.Exit(0);
        await running.Completion;
    }

    [Fact]
    public async Task ABuildErrorPreventsVisibleExcelFromStarting()
    {
        var events = new List<string>();
        var vbeSession = new FakeVbeDebugSession(events);
        var coordinator = new DebugLaunchCoordinator(
            new FailingDebugWorkbookBuilder(events, new DebugSetupException("Build failed.")),
            new FakeVbeDebugSessionFactory(events, vbeSession));

        var error = await Assert.ThrowsAsync<DebugSetupException>(() => coordinator.LaunchAsync(
            new DebugLaunchRequest(
                CreateContext(),
                new DebugTargetProcedure("DebugModule", "RunTarget"),
                CreateSourceSnapshot()),
            new RecordingDebugLifecycleSink(),
            CancellationToken.None));

        Assert.Equal("Build failed.", error.Message);
        Assert.Equal(["build:Book1"], events);
    }

    [Fact]
    public async Task ASetupErrorAfterExcelStartsTerminatesTheOwnedProcess()
    {
        var events = new List<string>();
        var vbeSession = new FakeVbeDebugSession(events)
        {
            OpenError = new DebugSetupException("The native Run command is disabled.")
        };
        var coordinator = new DebugLaunchCoordinator(
            new FakeDebugWorkbookBuilder(events),
            new FakeVbeDebugSessionFactory(events, vbeSession));

        var error = await Assert.ThrowsAsync<DebugSetupException>(() => coordinator.LaunchAsync(
            new DebugLaunchRequest(
                CreateContext(),
                new DebugTargetProcedure("DebugModule", "RunTarget"),
                CreateSourceSnapshot()),
            new RecordingDebugLifecycleSink(),
            CancellationToken.None));

        Assert.Equal("The native Run command is disabled.", error.Message);
        Assert.True(vbeSession.Terminated);
        Assert.True(vbeSession.Disposed);
    }

    [Fact]
    public async Task ASecondProcessLocalLaunchIsRejectedWhileExcelIsStillRunning()
    {
        var events = new List<string>();
        var vbeSession = new FakeVbeDebugSession(events);
        var coordinator = new DebugLaunchCoordinator(
            new FakeDebugWorkbookBuilder(events),
            new FakeVbeDebugSessionFactory(events, vbeSession));
        var request = new DebugLaunchRequest(
            CreateContext(),
            new DebugTargetProcedure("DebugModule", "RunTarget"),
            CreateSourceSnapshot());

        var running = await coordinator.LaunchAsync(
            request,
            new RecordingDebugLifecycleSink(),
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<DebugLaunchBusyException>(() => coordinator.LaunchAsync(
            request,
            new RecordingDebugLifecycleSink(),
            CancellationToken.None));

        Assert.Contains("already active", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, events.Count(item => item == "start-visible"));

        vbeSession.Exit(0);
        await running.Completion;
    }

    [Fact]
    public async Task ConditionalParticipantsAreValidatedAgainstTheSameGeneratedArtifactBeforeNativeCommands()
    {
        var context = CreateContext();
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var source = CreateConditionalSource();
        var requested = new DebugSourceBreakpoint(sourcePath, EditorLine: 3);
        var snapshot = Snapshot(sourcePath, source, [requested]);
        var mapped = new BreakpointSourceMapper().Map(snapshot, requested);
        var events = new List<string>();
        var session = new FakeVbeDebugSession(events)
        {
            HostFacts = VerifiedWindows64HostFacts()
        };
        var coordinator = ConditionalCoordinator(
            events,
            session,
            new FakeDebugCompilationSettingsReader(
                events,
                Settings('A'),
                Settings('A')),
            new FakeBreakpointSourceMapper(events, mapped));
        var request = new DebugLaunchRequest(
            context,
            new DebugTargetProcedure("DebugModule", "RunTarget"),
            snapshot)
        {
            BreakpointPlan = new DebugBreakpointPlan([requested], [])
        };

        var running = await coordinator.LaunchAsync(
            request,
            new RecordingDebugLaunchEventSink(events),
            CancellationToken.None);

        Assert.Equal(
            [
                $"map:{sourcePath}:3",
                "build:Book1",
                $"settings:1:{context.BinDocumentPath}",
                "start-visible",
                $"open:{context.BinDocumentPath}",
                $"settings:2:{context.BinDocumentPath}",
                "host-facts",
                "set:DebugModule:3:    Debug.Print \"modern\"",
                $"verified:{sourcePath}:3",
                "run:DebugModule.RunTarget"
            ],
            events);

        session.Exit(0);
        await running.Completion;
    }

    [Fact]
    public async Task InactiveConditionalBreakpointFailsBeforeAnyNativeCommand()
    {
        var context = CreateContext();
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var source = CreateConditionalSource();
        var requested = new DebugSourceBreakpoint(sourcePath, EditorLine: 7);
        var snapshot = Snapshot(sourcePath, source, [requested]);
        var mapped = new BreakpointSourceMapper().Map(snapshot, requested);
        var events = new List<string>();
        var session = new FakeVbeDebugSession(events)
        {
            HostFacts = VerifiedWindows64HostFacts()
        };
        var coordinator = ConditionalCoordinator(
            events,
            session,
            new FakeDebugCompilationSettingsReader(
                events,
                Settings('A'),
                Settings('A')),
            new FakeBreakpointSourceMapper(events, mapped));
        var request = new DebugLaunchRequest(
            context,
            new DebugTargetProcedure("DebugModule", "RunTarget"),
            snapshot)
        {
            BreakpointPlan = new DebugBreakpointPlan([requested], [])
        };

        var error = await Assert.ThrowsAsync<DebugSetupException>(() => coordinator.LaunchAsync(
            request,
            new RecordingDebugLaunchEventSink(events),
            CancellationToken.None));

        Assert.Contains("inactive", error.Message, StringComparison.OrdinalIgnoreCase);
        AssertNoNativeCommandsOrVerification(events);
        Assert.True(session.Terminated);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task InactiveConditionalTargetFailsBeforeAnyNativeCommand()
    {
        var context = CreateContext();
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var source = CreateConditionalSource();
        var snapshot = Snapshot(sourcePath, source, []);
        var events = new List<string>();
        var session = new FakeVbeDebugSession(events)
        {
            HostFacts = VerifiedWindows64HostFacts()
        };
        var coordinator = ConditionalCoordinator(
            events,
            session,
            new FakeDebugCompilationSettingsReader(
                events,
                Settings('A'),
                Settings('A')),
            new FakeBreakpointSourceMapper(
                events,
                _ => throw new InvalidOperationException("No breakpoint was expected.")));
        var request = new DebugLaunchRequest(
            context,
            new DebugTargetProcedure("DebugModule", "LegacyTarget")
            {
                ConditionalCompilationPath = CallablePath(sourcePath, source, "LegacyTarget")
            },
            snapshot);

        var error = await Assert.ThrowsAsync<DebugSetupException>(() => coordinator.LaunchAsync(
            request,
            new RecordingDebugLaunchEventSink(events),
            CancellationToken.None));

        Assert.Contains("target", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inactive", error.Message, StringComparison.OrdinalIgnoreCase);
        AssertNoNativeCommandsOrVerification(events);
        Assert.True(session.Terminated);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task UnevaluableConditionalTargetFailsBeforeAnyNativeCommand()
    {
        var context = CreateContext();
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var source = string.Join('\n',
        [
            "Attribute VB_Name = \"DebugModule\"",
            "#If CStr(1) Then",
            "Public Sub RunTarget()",
            "End Sub",
            "#End If"
        ]);
        var snapshot = Snapshot(sourcePath, source, []);
        var events = new List<string>();
        var session = new FakeVbeDebugSession(events)
        {
            HostFacts = VerifiedWindows64HostFacts()
        };
        var coordinator = ConditionalCoordinator(
            events,
            session,
            new FakeDebugCompilationSettingsReader(
                events,
                Settings('A'),
                Settings('A')),
            new FakeBreakpointSourceMapper(
                events,
                _ => throw new InvalidOperationException("No breakpoint was expected.")));
        var request = new DebugLaunchRequest(
            context,
            new DebugTargetProcedure("DebugModule", "RunTarget")
            {
                ConditionalCompilationPath = CallablePath(sourcePath, source, "RunTarget")
            },
            snapshot);

        var error = await Assert.ThrowsAsync<DebugSetupException>(() => coordinator.LaunchAsync(
            request,
            new RecordingDebugLaunchEventSink(events),
            CancellationToken.None));

        Assert.Contains("could not be proved", error.Message, StringComparison.OrdinalIgnoreCase);
        AssertNoNativeCommandsOrVerification(events);
        Assert.True(session.Terminated);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task GeneratedWorkbookFingerprintChangeAfterOpenFailsBeforeHostFactsAndNativeCommands()
    {
        var context = CreateContext();
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var source = CreateConditionalSource();
        var snapshot = Snapshot(sourcePath, source, []);
        var events = new List<string>();
        var session = new FakeVbeDebugSession(events)
        {
            HostFacts = VerifiedWindows64HostFacts()
        };
        var coordinator = ConditionalCoordinator(
            events,
            session,
            new FakeDebugCompilationSettingsReader(
                events,
                Settings('A'),
                Settings('B')),
            new FakeBreakpointSourceMapper(
                events,
                _ => throw new InvalidOperationException("No breakpoint was expected.")));
        var request = new DebugLaunchRequest(
            context,
            new DebugTargetProcedure("DebugModule", "ModernTarget")
            {
                ConditionalCompilationPath = CallablePath(sourcePath, source, "ModernTarget")
            },
            snapshot);

        var error = await Assert.ThrowsAsync<DebugSetupException>(() => coordinator.LaunchAsync(
            request,
            new RecordingDebugLaunchEventSink(events),
            CancellationToken.None));

        Assert.Contains("changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("host-facts", events);
        AssertNoNativeCommandsOrVerification(events);
        Assert.True(session.Terminated);
        Assert.True(session.Disposed);
    }

    [Theory]
    [InlineData(DebugCompilationHostFactsStatus.Unknown)]
    [InlineData(DebugCompilationHostFactsStatus.Mismatch)]
    public async Task UnprovedActualHostFactsFailBeforeAnyNativeCommand(
        DebugCompilationHostFactsStatus status)
    {
        var context = CreateContext();
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var source = CreateConditionalSource();
        var snapshot = Snapshot(sourcePath, source, []);
        var events = new List<string>();
        var session = new FakeVbeDebugSession(events)
        {
            HostFacts = new DebugCompilationHostFacts(
                "16.0",
                "7.01",
                "Windows (64-bit) NT 10.00",
                DebugExcelProcessArchitecture.X64,
                status,
                BuiltInConstants: null,
                "unproved host")
        };
        var coordinator = ConditionalCoordinator(
            events,
            session,
            new FakeDebugCompilationSettingsReader(
                events,
                Settings('A'),
                Settings('A')),
            new FakeBreakpointSourceMapper(
                events,
                _ => throw new InvalidOperationException("No breakpoint was expected.")));
        var request = new DebugLaunchRequest(
            context,
            new DebugTargetProcedure("DebugModule", "ModernTarget")
            {
                ConditionalCompilationPath = CallablePath(sourcePath, source, "ModernTarget")
            },
            snapshot);

        var error = await Assert.ThrowsAsync<DebugSetupException>(() => coordinator.LaunchAsync(
            request,
            new RecordingDebugLaunchEventSink(events),
            CancellationToken.None));

        Assert.Contains("unproved host", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("host-facts", events);
        AssertNoNativeCommandsOrVerification(events);
        Assert.True(session.Terminated);
        Assert.True(session.Disposed);
    }

    private static DebugLaunchCoordinator ConditionalCoordinator(
        List<string> events,
        FakeVbeDebugSession session,
        IDebugCompilationSettingsReader settingsReader,
        IBreakpointSourceMapper sourceMapper)
        => new(
            new FakeDebugWorkbookBuilder(events),
            new FakeVbeDebugSessionFactory(events, session),
            sourceMapper,
            settingsReader,
            new DebugCompilationEnvironmentFactory(),
            new DebugConditionalCompilationPreflight());

    private static DebugCompilationSettings Settings(char fingerprintCharacter)
        => new(
            VbaProjectSystemKind.Win64,
            1252,
            [],
            new string(fingerprintCharacter, 64));

    private static DebugCompilationHostFacts VerifiedWindows64HostFacts()
        => new(
            "16.0",
            "7.01",
            "Windows (64-bit) NT 10.00",
            DebugExcelProcessArchitecture.X64,
            DebugCompilationHostFactsStatus.Verified,
            new DebugCompilerBuiltInConstants(
                Vba6: true,
                Vba7: true,
                Win16: false,
                Win32: true,
                Win64: true,
                Mac: false),
            UnavailableReason: null);

    private static string CreateConditionalSource()
        => string.Join('\n',
        [
            "Attribute VB_Name = \"DebugModule\"",
            "#If VBA7 Then",
            "Public Sub ModernTarget()",
            "    Debug.Print \"modern\"",
            "End Sub",
            "#Else",
            "Public Sub LegacyTarget()",
            "    Debug.Print \"legacy\"",
            "End Sub",
            "#End If"
        ]);

    private static DebugSourceSnapshot Snapshot(
        string sourcePath,
        string source,
        ImmutableArray<DebugSourceBreakpoint> breakpoints)
        => new(
            DebugSourceSnapshot.CurrentSchemaVersion,
            [new DebugSourceFileSnapshot(sourcePath, source)],
            null)
        {
            Breakpoints = breakpoints
        };

    private static VbaConditionalCompilationBranchPath CallablePath(
        string sourcePath,
        string source,
        string procedureName)
    {
        var tree = VbaSyntaxTree.ParseModule(new Uri(sourcePath).AbsoluteUri, source);
        var declaration = Assert.Single(
            tree.Module.CallableDeclarations,
            candidate => candidate.Name == procedureName);
        Assert.True(VbaConditionalCompilationBranchFacts.TryGetPath(
            tree,
            declaration.Range,
            requireCompleteStructure: true,
            out var path));
        return path;
    }

    private static void AssertNoNativeCommandsOrVerification(IReadOnlyList<string> events)
    {
        Assert.DoesNotContain(events, item => item.StartsWith("set:", StringComparison.Ordinal));
        Assert.DoesNotContain(events, item => item.StartsWith("run:", StringComparison.Ordinal));
        Assert.DoesNotContain(events, item => item.StartsWith("verified:", StringComparison.Ordinal));
    }

    private static VbeBreakpoint Mapped(
        DebugSourceBreakpoint source,
        string moduleName,
        int vbideLine,
        string expectedCodeLine)
    {
        var codeLines = Enumerable.Range(1, vbideLine)
            .Select(line => line == vbideLine ? expectedCodeLine : $"line {line}")
            .ToImmutableArray();
        return new VbeBreakpoint(
            source,
            new VbeCodeModuleSourceMap(
                moduleName,
                VbaModuleKind.StandardModule,
                codeLines),
            vbideLine);
    }

    private static ResolvedProjectContext CreateContext()
    {
        var root = Path.GetFullPath(Path.Combine("DebugProject", Guid.NewGuid().ToString("N")));
        var document = ProjectDocument.CreateExcel("Book1") with
        {
            BinPath = "custom-bin/GeneratedBook.xlsm"
        };
        var manifest = new ProjectManifest(
            ProjectManifest.CurrentSchemaVersion,
            "DebugProject",
            "Book1",
            new Dictionary<string, ProjectDocument>(StringComparer.OrdinalIgnoreCase)
            {
                ["Book1"] = document
            });

        return new ResolvedProjectContext(
            root,
            Path.Combine(root, ProjectManifest.ManifestFileName),
            manifest,
            "Book1",
            document,
            Path.Combine(root, "src", "Book1"),
            Path.Combine(root, "src", "Book1", "Book1.xlsm"),
            Path.Combine(root, "custom-bin", "GeneratedBook.xlsm"),
            Path.Combine(root, "publish", "Book1.xlsm"),
            null);
    }

    private static DebugSourceSnapshot CreateSourceSnapshot()
        => new(DebugSourceSnapshot.CurrentSchemaVersion, [], null);

    private sealed class FakeDebugWorkbookBuilder(
        List<string> events,
        IReadOnlyList<string>? output = null) : IDebugWorkbookBuilder
    {
        public Task<DebugWorkbookBuildResult> BuildAsync(
            ResolvedProjectContext context,
            DebugSourceSnapshot sourceSnapshot,
            CancellationToken cancellationToken)
        {
            events.Add($"build:{context.DocumentName}");
            return Task.FromResult(new DebugWorkbookBuildResult(output ?? []));
        }
    }

    private sealed class FailingDebugWorkbookBuilder(
        List<string> events,
        Exception error) : IDebugWorkbookBuilder
    {
        public Task<DebugWorkbookBuildResult> BuildAsync(
            ResolvedProjectContext context,
            DebugSourceSnapshot sourceSnapshot,
            CancellationToken cancellationToken)
        {
            events.Add($"build:{context.DocumentName}");
            return Task.FromException<DebugWorkbookBuildResult>(error);
        }
    }

    private sealed class FakeVbeDebugSessionFactory(
        List<string> events,
        IVbeDebugSession session) : IVbeDebugSessionFactory
    {
        public Task<IVbeDebugSession> StartVisibleAsync(CancellationToken cancellationToken)
        {
            events.Add("start-visible");
            return Task.FromResult(session);
        }
    }

    private sealed class FakeBreakpointSourceMapper : IBreakpointSourceMapper
    {
        private readonly List<string> events;
        private readonly Func<DebugSourceBreakpoint, VbeBreakpoint> map;

        public FakeBreakpointSourceMapper(List<string> events, VbeBreakpoint mapped)
            : this(events, _ => mapped)
        {
        }

        public FakeBreakpointSourceMapper(
            List<string> events,
            Func<DebugSourceBreakpoint, VbeBreakpoint> map)
        {
            this.events = events;
            this.map = map;
        }

        public VbeBreakpoint Map(DebugSourceSnapshot snapshot, DebugSourceBreakpoint breakpoint)
        {
            events.Add($"map:{breakpoint.SourcePath}:{breakpoint.EditorLine}");
            return map(breakpoint);
        }
    }

    private sealed class FakeVbeDebugSession(List<string> events) : IVbeDebugSession
    {
        private readonly TaskCompletionSource<DebugProcessExit> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId => 31415;

        public Task<DebugProcessExit> Completion => completion.Task;

        public Task<DebugCompilationHostFacts> GetCompilationHostFactsAsync(
            CancellationToken cancellationToken)
        {
            events.Add("host-facts");
            return HostFacts is null
                ? Task.FromException<DebugCompilationHostFacts>(
                    new InvalidOperationException("Compilation host facts were not expected."))
                : Task.FromResult(HostFacts);
        }

        public DebugCompilationHostFacts? HostFacts { get; init; }

        public Exception? OpenError { get; init; }

        public Exception? SetError { get; init; }

        public int? SetErrorAtCall { get; init; }

        public bool Terminated { get; private set; }

        public bool Disposed { get; private set; }

        public Task OpenGeneratedWorkbookAsync(
            string workbookPath,
            CancellationToken cancellationToken)
        {
            events.Add($"open:{workbookPath}");
            return OpenError is null
                ? Task.CompletedTask
                : Task.FromException(OpenError);
        }

        public Task SetNativeBreakpointsAsync(
            IReadOnlyList<VbeBreakpoint> breakpoints,
            CancellationToken cancellationToken)
        {
            foreach (var breakpoint in breakpoints)
            {
                SetCalls++;
                events.Add(
                    $"set:{breakpoint.ModuleName}:{breakpoint.VbideLine}:{breakpoint.ExpectedCodeLine}");
                if (SetError is not null
                    && (SetErrorAtCall is not int errorCall || errorCall == SetCalls))
                {
                    return Task.FromException(SetError);
                }
            }

            return Task.CompletedTask;
        }

        private int SetCalls { get; set; }

        public Task RunTargetAsync(
            DebugTargetProcedure target,
            CancellationToken cancellationToken)
        {
            events.Add($"run:{target.ModuleName}.{target.ProcedureName}");
            return Task.CompletedTask;
        }

        public ValueTask TerminateAsync()
        {
            Terminated = true;
            completion.TrySetResult(new DebugProcessExit(-1));
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public void Exit(int exitCode) => completion.TrySetResult(new DebugProcessExit(exitCode));
    }

    private sealed class FakeDebugCompilationSettingsReader(
        List<string> events,
        params DebugCompilationSettings[] settings) : IDebugCompilationSettingsReader
    {
        private int readCount;

        public DebugCompilationSettings Read(string workbookPath)
        {
            readCount++;
            events.Add($"settings:{readCount}:{workbookPath}");
            return settings[readCount - 1];
        }
    }

    private sealed class RecordingDebugLifecycleSink : IDebugLifecycleSink
    {
        public List<string> Output { get; } = [];

        public ValueTask WriteAsync(DebugLifecycleMessage message, CancellationToken cancellationToken)
        {
            Output.Add(message.Output);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingDebugLaunchEventSink(List<string> events) : IDebugLaunchEventSink
    {
        public ValueTask WriteAsync(DebugLifecycleMessage message, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask BreakpointVerifiedAsync(
            VbeBreakpoint breakpoint,
            CancellationToken cancellationToken)
        {
            events.Add($"verified:{breakpoint.Source.SourcePath}:{breakpoint.Source.EditorLine}");
            return ValueTask.CompletedTask;
        }
    }
}
