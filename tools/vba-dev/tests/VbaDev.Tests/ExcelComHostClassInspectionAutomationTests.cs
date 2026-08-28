using System.Text;
using System.IO.Compression;
using System.Security.Cryptography;
using VbaDev.App.HostClasses;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class ExcelComHostClassInspectionAutomationTests
{
    [Theory]
    [InlineData("CDecl")]
    [InlineData("Run$")]
    public void HostClassIdentityRejectsNamesThatAreNotExactVbaIdentifiers(string name)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new HostClassIdentity(name, HostClassComponentKind.Document));

        Assert.Contains("VBA IDENTIFIER", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishesOnlyAfterPrivateCopyInspectionOwnedProcessReleaseAndWorkspaceDeletion()
    {
        using var temp = TempDirectory.Create();
        var sourceTemplate = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(sourceTemplate, "fixed template bytes", new UTF8Encoding(false));
        var events = new List<string>();
        var fileSystem = new RecordingWorkspaceFileSystem(events);
        var lifecycle = new RecordingHostClassLifecycle(events, sourceTemplate);
        var automation = new ExcelComHostClassInspectionAutomation(
            new RecordingDispatcherFactory(events),
            lifecycle,
            new HostClassInspectionWorkspaceFactory(
                temp.CreateDirectory("scratch"),
                fileSystem,
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));

        var completion = await automation.InspectAsync(
            new HostClassInspectionRequest(
                sourceTemplate,
                new HostClassInspectionTimeouts(
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(300),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(60),
                    TimeSpan.FromSeconds(60))),
            CancellationToken.None);

        Assert.True(completion.Batch.ClassEnumerationComplete);
        Assert.IsType<ResolvedHostClassInspectionEntry>(Assert.Single(completion.Batch.Classes));
        Assert.Equal("BookProject", completion.Batch.VbaProjectName);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes("fixed template bytes"))),
            completion.Batch.SourceTemplateFingerprint);
        Assert.Empty(completion.Warnings);
        Assert.Equal("mutated after the start-time copy", File.ReadAllText(sourceTemplate, Encoding.UTF8));
        Assert.Equal("fixed template bytes", lifecycle.ObservedPrivateCopyContents);
        Assert.NotEqual(Path.GetFullPath(sourceTemplate), lifecycle.OpenedWorkbookPath);
        Assert.False(File.Exists(lifecycle.OpenedWorkbookPath));
        Assert.Equal(
            [
                "copy",
                "safe-open-preflight",
                "start",
                "security-force-disable",
                "events-off",
                "open-copy-readonly",
                "enumerate-classes",
                "inspect:ThisWorkbook",
                "close:false",
                "quit",
                "process-exit-proved",
                "dispatcher-dispose",
                "delete",
                "return"
            ],
            [.. events, "return"]);
    }

    [Fact]
    public async Task PreflightFailureReportsTheRetainedWorkspaceAfterCleanupRetriesExhaust()
    {
        using var temp = TempDirectory.Create();
        var sourceTemplate = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(sourceTemplate, "fixed template bytes", new UTF8Encoding(false));
        var events = new List<string>();
        var lifecycle = new RecordingHostClassLifecycle(
            events,
            sourceTemplate,
            failSafePrivateCopyPreflight: true);
        var automation = new ExcelComHostClassInspectionAutomation(
            new RecordingDispatcherFactory(events),
            lifecycle,
            new HostClassInspectionWorkspaceFactory(
                temp.CreateDirectory("scratch"),
                new RecordingWorkspaceFileSystem(events, deleteFailures: 3),
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));

        var error = await Assert.ThrowsAsync<HostClassInspectionPreparationException>(() =>
            automation.InspectAsync(
                new HostClassInspectionRequest(
                    sourceTemplate,
                    new HostClassInspectionTimeouts(
                        TimeSpan.FromSeconds(30),
                        TimeSpan.FromSeconds(300),
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(60),
                        TimeSpan.FromSeconds(60))),
                CancellationToken.None));

        Assert.Contains(error.WorkspacePath, error.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(error.WorkspacePath));
        Assert.Equal(3, events.Count(entry => entry == "delete"));
        Assert.DoesNotContain("start", events);
    }

    [Fact]
    public async Task DispatcherCreationFailureReportsTheRetainedWorkspaceAfterCleanupRetriesExhaust()
    {
        using var temp = TempDirectory.Create();
        var sourceTemplate = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(sourceTemplate, "fixed template bytes", new UTF8Encoding(false));
        var events = new List<string>();
        var automation = new ExcelComHostClassInspectionAutomation(
            new ThrowingDispatcherFactory(events),
            new RecordingHostClassLifecycle(events, sourceTemplate),
            new HostClassInspectionWorkspaceFactory(
                temp.CreateDirectory("scratch"),
                new RecordingWorkspaceFileSystem(events, deleteFailures: 3),
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));

        var error = await Assert.ThrowsAsync<HostClassInspectionPreparationException>(() =>
            automation.InspectAsync(
                new HostClassInspectionRequest(
                    sourceTemplate,
                    new HostClassInspectionTimeouts(
                        TimeSpan.FromSeconds(30),
                        TimeSpan.FromSeconds(300),
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(60),
                        TimeSpan.FromSeconds(60))),
                CancellationToken.None));

        Assert.Contains(error.WorkspacePath, error.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(error.WorkspacePath));
        Assert.Equal(3, events.Count(entry => entry == "delete"));
        Assert.Contains("dispatcher-create", events);
        Assert.DoesNotContain("start", events);
    }

    [Fact]
    public async Task OperationFailureReportsTheRetainedWorkspaceAfterProcessReleaseAndCleanupRetriesExhaust()
    {
        using var temp = TempDirectory.Create();
        var sourceTemplate = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(sourceTemplate, "fixed template bytes", new UTF8Encoding(false));
        var events = new List<string>();
        var scratchRoot = temp.CreateDirectory("scratch");
        var automation = new ExcelComHostClassInspectionAutomation(
            new RecordingDispatcherFactory(events),
            new RecordingHostClassLifecycle(
                events,
                sourceTemplate,
                failPrivateWorkbookOpen: true),
            new HostClassInspectionWorkspaceFactory(
                scratchRoot,
                new RecordingWorkspaceFileSystem(events, deleteFailures: 3),
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));

        var error = await Assert.ThrowsAsync<WorkbookAutomationReleasedProcessCleanupException>(() =>
            automation.InspectAsync(
                new HostClassInspectionRequest(
                    sourceTemplate,
                    new HostClassInspectionTimeouts(
                        TimeSpan.FromSeconds(30),
                        TimeSpan.FromSeconds(300),
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(60),
                        TimeSpan.FromSeconds(60))),
                CancellationToken.None));

        var retainedPath = Path.GetFullPath(Assert.Single(
            Directory.EnumerateDirectories(scratchRoot)));
        Assert.Contains(retainedPath, error.Message, StringComparison.Ordinal);
        Assert.Contains("process-exit-proved", events);
        Assert.Equal(3, events.Count(entry => entry == "delete"));
    }

    [Fact]
    public async Task CancellationAfterOneClassRetainsItAndMarksKnownUnfinishedClassesCancelledAfterRelease()
    {
        using var temp = TempDirectory.Create();
        var sourceTemplate = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(sourceTemplate, "fixed template bytes", new UTF8Encoding(false));
        var events = new List<string>();
        var inspectionPaused = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lifecycle = new RecordingHostClassLifecycle(
            events,
            sourceTemplate,
            includeSecondClass: true);
        var automation = new ExcelComHostClassInspectionAutomation(
            new PausingInvocationDispatcherFactory(events, inspectionPaused, 5),
            lifecycle,
            new HostClassInspectionWorkspaceFactory(
                temp.CreateDirectory("scratch"),
                new RecordingWorkspaceFileSystem(events),
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));
        using var cancellation = new CancellationTokenSource();

        var invocation = automation.InspectAsync(
            new HostClassInspectionRequest(
                sourceTemplate,
                new HostClassInspectionTimeouts(
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(300),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(60),
                    TimeSpan.FromSeconds(60))),
            cancellation.Token);
        await inspectionPaused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var completion = await invocation;

        Assert.Equal(HostClassInspectionOutcome.Cancelled, completion.Batch.Outcome);
        Assert.True(completion.Batch.ClassEnumerationComplete);
        Assert.Collection(
            completion.Batch.Classes,
            entry => Assert.IsType<ResolvedHostClassInspectionEntry>(entry),
            entry => Assert.Equal(
                HostClassInspectionFailureReason.Cancelled,
                Assert.IsType<UnverifiedHostClassInspectionEntry>(entry).Reason));
        Assert.Contains(
            completion.Batch.Diagnostics,
            diagnostic => diagnostic.Code == "operationCancelled");
        Assert.Contains("process-exit-proved", events);
        Assert.Contains("delete", events);
    }

    [Fact]
    public async Task ClassTimeoutRetainsEarlierResultsAndAbortsLaterClassesAfterRelease()
    {
        using var temp = TempDirectory.Create();
        var sourceTemplate = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(sourceTemplate, "fixed template bytes", new UTF8Encoding(false));
        var events = new List<string>();
        var inspectionPaused = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lifecycle = new RecordingHostClassLifecycle(
            events,
            sourceTemplate,
            includeSecondClass: true,
            includeThirdClass: true);
        var automation = new ExcelComHostClassInspectionAutomation(
            new PausingInvocationDispatcherFactory(events, inspectionPaused, 5),
            lifecycle,
            new HostClassInspectionWorkspaceFactory(
                temp.CreateDirectory("scratch"),
                new RecordingWorkspaceFileSystem(events),
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));

        var completion = await automation.InspectAsync(
            new HostClassInspectionRequest(
                sourceTemplate,
                new HostClassInspectionTimeouts(
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(300),
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(60),
                    TimeSpan.FromMilliseconds(20))),
            CancellationToken.None);

        Assert.Equal(
            HostClassInspectionOutcome.InspectionStateUntrusted,
            completion.Batch.Outcome);
        Assert.Collection(
            completion.Batch.Classes,
            entry => Assert.IsType<ResolvedHostClassInspectionEntry>(entry),
            entry => Assert.Equal(
                HostClassInspectionFailureReason.InspectionTimeout,
                Assert.IsType<UnverifiedHostClassInspectionEntry>(entry).Reason),
            entry => Assert.Equal(
                HostClassInspectionFailureReason.InspectionAborted,
                Assert.IsType<UnverifiedHostClassInspectionEntry>(entry).Reason));
        Assert.Contains(
            completion.Batch.Diagnostics,
            diagnostic => diagnostic.Code == "inspectionStateUntrusted");
        Assert.Single(events, entry => entry == "start");
        Assert.Contains("process-exit-proved", events);
        Assert.Contains("delete", events);
    }

    [Fact]
    public async Task UntrustedClassMutationRetainsEarlierResultsAndAbortsLaterClassesAfterRelease()
    {
        using var temp = TempDirectory.Create();
        var sourceTemplate = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(sourceTemplate, "fixed template bytes", new UTF8Encoding(false));
        var events = new List<string>();
        var lifecycle = new RecordingHostClassLifecycle(
            events,
            sourceTemplate,
            includeSecondClass: true,
            includeThirdClass: true,
            makeSecondInspectionUntrusted: true);
        var automation = new ExcelComHostClassInspectionAutomation(
            new RecordingDispatcherFactory(events),
            lifecycle,
            new HostClassInspectionWorkspaceFactory(
                temp.CreateDirectory("scratch"),
                new RecordingWorkspaceFileSystem(events),
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));

        var completion = await automation.InspectAsync(
            new HostClassInspectionRequest(
                sourceTemplate,
                new HostClassInspectionTimeouts(
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(300),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(60),
                    TimeSpan.FromSeconds(60))),
            CancellationToken.None);

        Assert.Equal(
            HostClassInspectionOutcome.InspectionStateUntrusted,
            completion.Batch.Outcome);
        Assert.Collection(
            completion.Batch.Classes,
            entry => Assert.IsType<ResolvedHostClassInspectionEntry>(entry),
            entry => Assert.Equal(
                HostClassInspectionFailureReason.InspectionFailure,
                Assert.IsType<UnverifiedHostClassInspectionEntry>(entry).Reason),
            entry => Assert.Equal(
                HostClassInspectionFailureReason.InspectionAborted,
                Assert.IsType<UnverifiedHostClassInspectionEntry>(entry).Reason));
        Assert.Contains(
            completion.Batch.Diagnostics,
            diagnostic => diagnostic.Code == "inspectionStateUntrusted");
        Assert.Single(events, entry => entry == "start");
        Assert.Contains("process-exit-proved", events);
        Assert.Contains("delete", events);
    }

    [Fact]
    public async Task ProcessLossDuringClassInspectionRetainsEarlierResultsAndAbortsLaterClasses()
    {
        using var temp = TempDirectory.Create();
        var sourceTemplate = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(sourceTemplate, "fixed template bytes", new UTF8Encoding(false));
        var events = new List<string>();
        var lifecycle = new RecordingHostClassLifecycle(
            events,
            sourceTemplate,
            includeSecondClass: true,
            includeThirdClass: true,
            loseProcessDuringSecondClassInspection: true);
        var automation = new ExcelComHostClassInspectionAutomation(
            new RecordingDispatcherFactory(events),
            lifecycle,
            new HostClassInspectionWorkspaceFactory(
                temp.CreateDirectory("scratch"),
                new RecordingWorkspaceFileSystem(events),
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));

        var completion = await automation.InspectAsync(
            new HostClassInspectionRequest(
                sourceTemplate,
                new HostClassInspectionTimeouts(
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(300),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(60),
                    TimeSpan.FromSeconds(60))),
            CancellationToken.None);

        Assert.Equal(
            HostClassInspectionOutcome.InspectionStateUntrusted,
            completion.Batch.Outcome);
        Assert.Collection(
            completion.Batch.Classes,
            entry => Assert.IsType<ResolvedHostClassInspectionEntry>(entry),
            entry => Assert.Equal(
                HostClassInspectionFailureReason.InspectionFailure,
                Assert.IsType<UnverifiedHostClassInspectionEntry>(entry).Reason),
            entry => Assert.Equal(
                HostClassInspectionFailureReason.InspectionAborted,
                Assert.IsType<UnverifiedHostClassInspectionEntry>(entry).Reason));
        Assert.Contains(
            completion.Batch.Diagnostics,
            diagnostic => diagnostic.Code == "inspectionStateUntrusted");
        Assert.Single(events, entry => entry == "start");
        Assert.DoesNotContain("inspect:UserForm1", events);
        Assert.Contains("process-exit-proved", events);
        Assert.Contains("delete", events);
    }

    [Fact]
    public async Task ClassLocalFailureIsUnverifiedAndLaterClassesContinueAfterRelease()
    {
        using var temp = TempDirectory.Create();
        var sourceTemplate = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(sourceTemplate, "fixed template bytes", new UTF8Encoding(false));
        var events = new List<string>();
        var lifecycle = new RecordingHostClassLifecycle(
            events,
            sourceTemplate,
            includeSecondClass: true,
            failFirstClassInspection: true);
        var automation = new ExcelComHostClassInspectionAutomation(
            new RecordingDispatcherFactory(events),
            lifecycle,
            new HostClassInspectionWorkspaceFactory(
                temp.CreateDirectory("scratch"),
                new RecordingWorkspaceFileSystem(events),
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));

        var completion = await automation.InspectAsync(
            new HostClassInspectionRequest(
                sourceTemplate,
                new HostClassInspectionTimeouts(
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(300),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(60),
                    TimeSpan.FromSeconds(60))),
            CancellationToken.None);

        Assert.Equal(HostClassInspectionOutcome.Completed, completion.Batch.Outcome);
        Assert.Collection(
            completion.Batch.Classes,
            entry => Assert.Equal(
                HostClassInspectionFailureReason.InspectionFailure,
                Assert.IsType<UnverifiedHostClassInspectionEntry>(entry).Reason),
            entry => Assert.IsType<ResolvedHostClassInspectionEntry>(entry));
        Assert.Contains("inspect:Sheet1", events);
        Assert.Contains("process-exit-proved", events);
        Assert.Contains("delete", events);
    }

    [Fact]
    public async Task EnumerationTimeoutReturnsAnIncompleteUntrustedResultAfterRelease()
    {
        using var temp = TempDirectory.Create();
        var sourceTemplate = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(sourceTemplate, "fixed template bytes", new UTF8Encoding(false));
        var events = new List<string>();
        var enumerationPaused = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var automation = new ExcelComHostClassInspectionAutomation(
            new PausingInvocationDispatcherFactory(events, enumerationPaused, 3),
            new RecordingHostClassLifecycle(events, sourceTemplate),
            new HostClassInspectionWorkspaceFactory(
                temp.CreateDirectory("scratch"),
                new RecordingWorkspaceFileSystem(events),
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));

        var completion = await automation.InspectAsync(
            new HostClassInspectionRequest(
                sourceTemplate,
                new HostClassInspectionTimeouts(
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(300),
                    TimeSpan.Zero,
                    TimeSpan.FromMilliseconds(20),
                    TimeSpan.FromSeconds(60))),
            CancellationToken.None);

        Assert.Equal(
            HostClassInspectionOutcome.InspectionStateUntrusted,
            completion.Batch.Outcome);
        Assert.False(completion.Batch.ClassEnumerationComplete);
        Assert.Empty(completion.Batch.Classes);
        Assert.Contains(
            completion.Batch.Diagnostics,
            diagnostic => diagnostic.Code == "classEnumerationFailure");
        Assert.Contains(
            completion.Batch.Diagnostics,
            diagnostic => diagnostic.Code == "inspectionStateUntrusted");
        Assert.Contains("process-exit-proved", events);
        Assert.Contains("delete", events);
    }

    [Fact]
    public async Task EnumerationFailureReturnsAnIncompleteResultAfterRelease()
    {
        using var temp = TempDirectory.Create();
        var sourceTemplate = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(sourceTemplate, "fixed template bytes", new UTF8Encoding(false));
        var events = new List<string>();
        var automation = new ExcelComHostClassInspectionAutomation(
            new RecordingDispatcherFactory(events),
            new RecordingHostClassLifecycle(
                events,
                sourceTemplate,
                failClassEnumeration: true),
            new HostClassInspectionWorkspaceFactory(
                temp.CreateDirectory("scratch"),
                new RecordingWorkspaceFileSystem(events),
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));

        var completion = await automation.InspectAsync(
            new HostClassInspectionRequest(
                sourceTemplate,
                new HostClassInspectionTimeouts(
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(300),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(60),
                    TimeSpan.FromSeconds(60))),
            CancellationToken.None);

        Assert.Equal(HostClassInspectionOutcome.Completed, completion.Batch.Outcome);
        Assert.False(completion.Batch.ClassEnumerationComplete);
        Assert.Empty(completion.Batch.Classes);
        var diagnostic = Assert.Single(completion.Batch.Diagnostics);
        Assert.Equal("classEnumerationFailure", diagnostic.Code);
        Assert.Contains("process-exit-proved", events);
        Assert.Contains("delete", events);
    }

    [Fact]
    public async Task ProcessLossDuringEnumerationReturnsAnIncompleteUntrustedResultAfterRelease()
    {
        using var temp = TempDirectory.Create();
        var sourceTemplate = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(sourceTemplate, "fixed template bytes", new UTF8Encoding(false));
        var events = new List<string>();
        var automation = new ExcelComHostClassInspectionAutomation(
            new RecordingDispatcherFactory(events),
            new RecordingHostClassLifecycle(
                events,
                sourceTemplate,
                loseProcessDuringClassEnumeration: true),
            new HostClassInspectionWorkspaceFactory(
                temp.CreateDirectory("scratch"),
                new RecordingWorkspaceFileSystem(events),
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));

        var completion = await automation.InspectAsync(
            new HostClassInspectionRequest(
                sourceTemplate,
                new HostClassInspectionTimeouts(
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(300),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(60),
                    TimeSpan.FromSeconds(60))),
            CancellationToken.None);

        Assert.Equal(
            HostClassInspectionOutcome.InspectionStateUntrusted,
            completion.Batch.Outcome);
        Assert.False(completion.Batch.ClassEnumerationComplete);
        Assert.Empty(completion.Batch.Classes);
        Assert.Contains(
            completion.Batch.Diagnostics,
            diagnostic => diagnostic.Code == "classEnumerationFailure");
        Assert.Contains(
            completion.Batch.Diagnostics,
            diagnostic => diagnostic.Code == "inspectionStateUntrusted");
        Assert.Contains("process-exit-proved", events);
        Assert.Contains("delete", events);
    }

    [Fact]
    public async Task CancellationDuringEnumerationReturnsAnIncompleteTerminalResultAfterRelease()
    {
        using var temp = TempDirectory.Create();
        var sourceTemplate = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(sourceTemplate, "fixed template bytes", new UTF8Encoding(false));
        var events = new List<string>();
        var enumerationPaused = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var automation = new ExcelComHostClassInspectionAutomation(
            new PausingInvocationDispatcherFactory(events, enumerationPaused, 3),
            new RecordingHostClassLifecycle(events, sourceTemplate),
            new HostClassInspectionWorkspaceFactory(
                temp.CreateDirectory("scratch"),
                new RecordingWorkspaceFileSystem(events),
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));
        using var cancellation = new CancellationTokenSource();

        var invocation = automation.InspectAsync(
            new HostClassInspectionRequest(
                sourceTemplate,
                new HostClassInspectionTimeouts(
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(300),
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(60),
                    TimeSpan.FromSeconds(60))),
            cancellation.Token);
        await enumerationPaused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var completion = await invocation;

        Assert.Equal(HostClassInspectionOutcome.Cancelled, completion.Batch.Outcome);
        Assert.False(completion.Batch.ClassEnumerationComplete);
        Assert.Empty(completion.Batch.Classes);
        var diagnostic = Assert.Single(completion.Batch.Diagnostics);
        Assert.Equal("operationCancelled", diagnostic.Code);
        Assert.Contains("process-exit-proved", events);
        Assert.Contains("delete", events);
    }

    [Fact]
    public async Task CancellationDuringWorkbookOpenReturnsATerminalResultAfterRelease()
    {
        using var temp = TempDirectory.Create();
        var sourceTemplate = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(sourceTemplate, "fixed template bytes", new UTF8Encoding(false));
        var events = new List<string>();
        var openPaused = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var automation = new ExcelComHostClassInspectionAutomation(
            new PausingInvocationDispatcherFactory(events, openPaused, 2),
            new RecordingHostClassLifecycle(events, sourceTemplate),
            new HostClassInspectionWorkspaceFactory(
                temp.CreateDirectory("scratch"),
                new RecordingWorkspaceFileSystem(events),
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));
        using var cancellation = new CancellationTokenSource();

        var invocation = automation.InspectAsync(
            new HostClassInspectionRequest(
                sourceTemplate,
                new HostClassInspectionTimeouts(
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(300),
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(60),
                    TimeSpan.FromSeconds(60))),
            cancellation.Token);
        await openPaused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var completion = await invocation;

        Assert.Equal(HostClassInspectionOutcome.Cancelled, completion.Batch.Outcome);
        Assert.False(completion.Batch.ClassEnumerationComplete);
        Assert.Empty(completion.Batch.Classes);
        Assert.Equal("operationCancelled", Assert.Single(completion.Batch.Diagnostics).Code);
        Assert.Contains("process-exit-proved", events);
        Assert.Contains("delete", events);
    }

    [Fact]
    public async Task CancellationDuringCleanupWinsOverACompletedInspectionResult()
    {
        using var temp = TempDirectory.Create();
        var sourceTemplate = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(sourceTemplate, "fixed template bytes", new UTF8Encoding(false));
        var events = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var automation = new ExcelComHostClassInspectionAutomation(
            new CancellingCleanupDispatcherFactory(events, cancellation),
            new RecordingHostClassLifecycle(events, sourceTemplate),
            new HostClassInspectionWorkspaceFactory(
                temp.CreateDirectory("scratch"),
                new RecordingWorkspaceFileSystem(events),
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));

        var completion = await automation.InspectAsync(
            new HostClassInspectionRequest(
                sourceTemplate,
                new HostClassInspectionTimeouts(
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(300),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(60),
                    TimeSpan.FromSeconds(60))),
            cancellation.Token);

        Assert.Equal(HostClassInspectionOutcome.Cancelled, completion.Batch.Outcome);
        Assert.IsType<ResolvedHostClassInspectionEntry>(
            Assert.Single(completion.Batch.Classes));
        Assert.Equal("operationCancelled", Assert.Single(completion.Batch.Diagnostics).Code);
        Assert.Contains("process-exit-proved", events);
        Assert.Contains("delete", events);
    }

    [Fact]
    public async Task CooperativeCleanupFailureAfterProvedProcessExitIsNotAReleaseProofFailure()
    {
        using var temp = TempDirectory.Create();
        var sourceTemplate = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(sourceTemplate, "fixed template bytes", new UTF8Encoding(false));
        var events = new List<string>();
        var automation = new ExcelComHostClassInspectionAutomation(
            new RecordingDispatcherFactory(events),
            new RecordingHostClassLifecycle(
                events,
                sourceTemplate,
                throwAfterProvedReleaseOnDisposeHost: true),
            new HostClassInspectionWorkspaceFactory(
                temp.CreateDirectory("scratch"),
                new RecordingWorkspaceFileSystem(events),
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));

        var error = await Assert.ThrowsAsync<WorkbookAutomationReleasedProcessCleanupException>(
            () => automation.InspectAsync(
                new HostClassInspectionRequest(
                    sourceTemplate,
                    new HostClassInspectionTimeouts(
                        TimeSpan.FromSeconds(30),
                        TimeSpan.FromSeconds(300),
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(60),
                        TimeSpan.FromSeconds(60))),
                CancellationToken.None));

        Assert.False(WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(error));
        Assert.Contains("process-exit-proved", events);
        Assert.Contains("delete", events);
    }

    [Fact]
    public async Task ReleaseProofFailureSuppressesProjectionAndRetainsTheWorkspace()
    {
        using var temp = TempDirectory.Create();
        var sourceTemplate = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(sourceTemplate, "fixed template bytes", new UTF8Encoding(false));
        var events = new List<string>();
        var scratchRoot = temp.CreateDirectory("scratch");
        var automation = new ExcelComHostClassInspectionAutomation(
            new RecordingDispatcherFactory(events),
            new RecordingHostClassLifecycle(
                events,
                sourceTemplate,
                failReleaseProof: true),
            new HostClassInspectionWorkspaceFactory(
                scratchRoot,
                new RecordingWorkspaceFileSystem(events),
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));

        var error = await Assert.ThrowsAsync<WorkbookAutomationCleanupException>(
            () => automation.InspectAsync(
                new HostClassInspectionRequest(
                    sourceTemplate,
                    new HostClassInspectionTimeouts(
                        TimeSpan.FromSeconds(30),
                        TimeSpan.FromSeconds(300),
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(60),
                        TimeSpan.FromSeconds(60))),
                CancellationToken.None));

        Assert.True(WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(error));
        var retainedPath = Assert.Single(Directory.GetDirectories(scratchRoot));
        Assert.Contains(retainedPath, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("process-exit-proved", events);
        Assert.DoesNotContain("delete", events);
    }

    [Fact]
    public async Task ExhaustedWorkspaceDeletionRetriesPreserveSuccessWithTheStableWarning()
    {
        using var temp = TempDirectory.Create();
        var sourceTemplate = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(sourceTemplate, "fixed template bytes", new UTF8Encoding(false));
        var events = new List<string>();
        var automation = new ExcelComHostClassInspectionAutomation(
            new RecordingDispatcherFactory(events),
            new RecordingHostClassLifecycle(events, sourceTemplate),
            new HostClassInspectionWorkspaceFactory(
                temp.CreateDirectory("scratch"),
                new RecordingWorkspaceFileSystem(events, deleteFailures: 3),
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));

        var completion = await automation.InspectAsync(
            new HostClassInspectionRequest(
                sourceTemplate,
                new HostClassInspectionTimeouts(
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(300),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(60),
                    TimeSpan.FromSeconds(60))),
            CancellationToken.None);

        Assert.Equal(HostClassInspectionOutcome.Completed, completion.Batch.Outcome);
        var warning = Assert.Single(completion.Warnings);
        Assert.Equal("inspectionWorkspaceRetained", warning.Code);
        var retainedPath = Assert.Single(
            Directory.GetDirectories(Path.Combine(temp.Path, "scratch")));
        Assert.True(Path.IsPathFullyQualified(retainedPath));
        Assert.True(Directory.Exists(retainedPath));
        Assert.Contains(retainedPath, warning.Message, StringComparison.Ordinal);
        Assert.Equal(3, events.Count(entry => entry == "delete"));
    }

    [Fact]
    public async Task DuplicateIdentitiesAreOmittedBeforeUniqueClassesAreInspected()
    {
        using var temp = TempDirectory.Create();
        var sourceTemplate = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(sourceTemplate, "fixed template bytes", new UTF8Encoding(false));
        var events = new List<string>();
        var automation = new ExcelComHostClassInspectionAutomation(
            new RecordingDispatcherFactory(events),
            new RecordingHostClassLifecycle(
                events,
                sourceTemplate,
                includeDuplicateIdentity: true),
            new HostClassInspectionWorkspaceFactory(
                temp.CreateDirectory("scratch"),
                new RecordingWorkspaceFileSystem(events),
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));

        var completion = await automation.InspectAsync(
            new HostClassInspectionRequest(
                sourceTemplate,
                new HostClassInspectionTimeouts(
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(300),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(60),
                    TimeSpan.FromSeconds(60))),
            CancellationToken.None);

        Assert.False(completion.Batch.ClassEnumerationComplete);
        var projected = Assert.IsType<ResolvedHostClassInspectionEntry>(
            Assert.Single(completion.Batch.Classes));
        Assert.Equal("UserForm1", projected.Identity.Name);
        Assert.DoesNotContain(events, entry => entry.StartsWith("inspect:Sheet1", StringComparison.OrdinalIgnoreCase));
        Assert.Single(events, entry => entry == "inspect:UserForm1");
        Assert.Contains(
            completion.Batch.Diagnostics,
            diagnostic => diagnostic.Code == "classEnumerationFailure");
    }

    [Fact]
    public void SafetyPreflightRejectsAMacroSheetDeclaredAtAnUnusualPackagePath()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "Book1.xlsm");
        using (var archive = ZipFile.Open(workbookPath, ZipArchiveMode.Create))
        {
            var contentTypes = archive.CreateEntry("[Content_Types].xml");
            using (var writer = new StreamWriter(
                       contentTypes.Open(),
                       new UTF8Encoding(false)))
            {
                writer.Write(
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                    "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Override PartName=\"/xl/unusual/legacy.xml\" ContentType=\"application/vnd.ms-excel.macrosheet+xml\"/>" +
                    "</Types>");
            }

            archive.CreateEntry("xl/unusual/legacy.xml");
        }

        var error = Assert.Throws<InvalidOperationException>(
            () => HostClassWorkbookSafetyPreflight.ThrowIfUnsafe(workbookPath));

        Assert.Contains("macro or dialog sheet", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SafetyPreflightRejectsAMacroSheetDeclaredByWorkbookRelationshipType()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "Book1.xlsm");
        using (var archive = ZipFile.Open(workbookPath, ZipArchiveMode.Create))
        {
            var contentTypes = archive.CreateEntry("[Content_Types].xml");
            using (var writer = new StreamWriter(
                       contentTypes.Open(),
                       new UTF8Encoding(false)))
            {
                writer.Write(
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                    "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "</Types>");
            }

            var relationships = archive.CreateEntry("xl/_rels/workbook.xml.rels");
            using var relationshipWriter = new StreamWriter(
                relationships.Open(),
                new UTF8Encoding(false));
            relationshipWriter.Write(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.microsoft.com/office/2006/relationships/xlMacrosheet\" Target=\"../unusual/legacy.xml\"/>" +
                "</Relationships>");
        }

        var error = Assert.Throws<InvalidOperationException>(
            () => HostClassWorkbookSafetyPreflight.ThrowIfUnsafe(workbookPath));

        Assert.Contains("macro or dialog sheet", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpenValidationRejectsAWorkbookOtherThanThePrivateCopy()
    {
        using var temp = TempDirectory.Create();
        var privateCopy = Path.Combine(temp.Path, "private", "Book1.xlsm");
        var unexpected = new FakeOpenedWorkbook(
            Path.Combine(temp.Path, "original", "Book1.xlsm"),
            readOnly: true);

        var error = Assert.Throws<InvalidOperationException>(() =>
            ExcelComHostClassInspectionAutomation.ExcelComHostClassInspectionLifecycle
                .ValidateOpenedPrivateWorkbook(
                    unexpected.FullName,
                    unexpected.ReadOnly,
                    privateCopy));

        Assert.Contains("private copy", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrivateCopyOpenFailsClosedWhenTheOwnedProcessAlreadyHasAnOpenWorkbook()
    {
        using var temp = TempDirectory.Create();
        var privateCopy = Path.Combine(temp.Path, "private", "Book1.xlsm");
        var workbooks = new FakeWorkbooksCollection(initialCount: 1);
        var host = new ExcelComWorkbookSession.ExcelComHostObjects(
            new object(),
            workbooks,
            ExcelProcess: null,
            StrongExcelProcess: null,
            TerminationController: null,
            CancellationRegistration: default);
        var lifecycle = new ExcelComHostClassInspectionAutomation
            .ExcelComHostClassInspectionLifecycle();

        var error = Assert.Throws<InvalidOperationException>(() =>
            lifecycle.OpenPrivateWorkbookReadOnly(host, privateCopy));

        Assert.Contains("no open workbooks", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(workbooks.OpenCalled);
    }

    [Fact]
    public void PrivateCopyOpenFailsClosedWhenOpeningItLeavesAnotherWorkbookOpen()
    {
        using var temp = TempDirectory.Create();
        var privateCopy = Path.Combine(temp.Path, "private", "Book1.xlsm");
        var workbooks = new FakeWorkbooksCollection(
            initialCount: 0,
            countAfterOpen: 2);
        var host = new ExcelComWorkbookSession.ExcelComHostObjects(
            new object(),
            workbooks,
            ExcelProcess: null,
            StrongExcelProcess: null,
            TerminationController: null,
            CancellationRegistration: default);
        var lifecycle = new ExcelComHostClassInspectionAutomation
            .ExcelComHostClassInspectionLifecycle();

        var error = Assert.Throws<InvalidOperationException>(() =>
            lifecycle.OpenPrivateWorkbookReadOnly(host, privateCopy));

        Assert.Contains("exactly one open workbook", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(workbooks.OpenCalled);
    }

    private sealed class RecordingDispatcherFactory(List<string> events)
        : IStaComDispatcherFactory
    {
        public IStaComDispatcher Create() => new RecordingDispatcher(events);
    }

    private sealed class ThrowingDispatcherFactory(List<string> events)
        : IStaComDispatcherFactory
    {
        public IStaComDispatcher Create()
        {
            events.Add("dispatcher-create");
            throw new InvalidOperationException("The STA dispatcher could not be created.");
        }
    }

    public sealed class FakeOpenedWorkbook(string fullName, bool readOnly)
    {
        public string FullName { get; } = fullName;

        public bool ReadOnly { get; } = readOnly;
    }

    public sealed class FakeWorkbooksCollection(
        int initialCount,
        int? countAfterOpen = null)
    {
        public int Count { get; private set; } = initialCount;

        public bool OpenCalled { get; private set; }

        public object Open(
            string workbookPath,
            object updateLinks,
            bool readOnly,
            object format,
            object password,
            object writeResPassword,
            bool ignoreReadOnlyRecommended,
            object origin,
            object delimiter,
            object editable,
            object notify,
            object converter,
            bool addToMru)
        {
            OpenCalled = true;
            Count = countAfterOpen ?? Count + 1;
            return new FakeOpenedWorkbook(workbookPath, readOnly);
        }
    }

    private sealed class CancellingCleanupDispatcherFactory(
        List<string> events,
        CancellationTokenSource cancellation) : IStaComDispatcherFactory
    {
        public IStaComDispatcher Create()
            => new CancellingCleanupDispatcher(events, cancellation);
    }

    private sealed class CancellingCleanupDispatcher(
        List<string> events,
        CancellationTokenSource cancellation) : IStaComDispatcher
    {
        private int invocationCount;

        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            invocationCount++;
            if (invocationCount == 5)
            {
                cancellation.Cancel();
            }

            return Task.FromResult(operation());
        }

        public ValueTask DisposeAsync()
        {
            events.Add("dispatcher-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PausingInvocationDispatcherFactory(
        List<string> events,
        TaskCompletionSource inspectionPaused,
        int invocationToPause) : IStaComDispatcherFactory
    {
        public IStaComDispatcher Create()
            => new PausingInvocationDispatcher(
                events,
                inspectionPaused,
                invocationToPause);
    }

    private sealed class PausingInvocationDispatcher(
        List<string> events,
        TaskCompletionSource inspectionPaused,
        int invocationToPause) : IStaComDispatcher
    {
        private int invocationCount;

        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            invocationCount++;
            return invocationCount == invocationToPause
                ? WaitForCancellationAsync<T>(cancellationToken)
                : Task.FromResult(operation());
        }

        public ValueTask DisposeAsync()
        {
            events.Add("dispatcher-dispose");
            return ValueTask.CompletedTask;
        }

        private async Task<T> WaitForCancellationAsync<T>(
            CancellationToken cancellationToken)
        {
            inspectionPaused.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation wait unexpectedly completed.");
        }
    }

    private sealed class RecordingDispatcher(List<string> events) : IStaComDispatcher
    {
        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(operation());
        }

        public ValueTask DisposeAsync()
        {
            events.Add("dispatcher-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingHostClassLifecycle(
        List<string> events,
        string sourceTemplatePath,
        bool includeSecondClass = false,
        bool includeThirdClass = false,
        bool makeSecondInspectionUntrusted = false,
        bool throwAfterProvedReleaseOnDisposeHost = false,
        bool includeDuplicateIdentity = false,
        bool failClassEnumeration = false,
        bool failFirstClassInspection = false,
        bool loseProcessDuringClassEnumeration = false,
        bool loseProcessDuringSecondClassInspection = false,
        bool failReleaseProof = false,
        bool failSafePrivateCopyPreflight = false,
        bool failPrivateWorkbookOpen = false) : IExcelComHostClassInspectionLifecycle
    {
        private readonly RecordingOwnedProcess owner = new(events, failReleaseProof);
        private int inspectedClassCount;

        public string OpenedWorkbookPath { get; private set; } = string.Empty;

        public string ObservedPrivateCopyContents { get; private set; } = string.Empty;

        public void ValidateSafePrivateCopy(string workbookPath)
        {
            events.Add("safe-open-preflight");
            if (failSafePrivateCopyPreflight)
            {
                throw new InvalidOperationException(
                    "The private copy failed the safe-open preflight.");
            }
        }

        public object Start(
            OwnedExcelTerminationController terminationController,
            CancellationToken cancellationToken)
        {
            events.Add("start");
            terminationController.Attach(owner);
            File.WriteAllText(
                sourceTemplatePath,
                "mutated after the start-time copy",
                new UTF8Encoding(false));
            return new object();
        }

        public void ForceDisableAutomationSecurity(object host)
            => events.Add("security-force-disable");

        public void DisableExcelEvents(object host)
            => events.Add("events-off");

        public object OpenPrivateWorkbookReadOnly(object host, string workbookPath)
        {
            events.Add("open-copy-readonly");
            if (failPrivateWorkbookOpen)
            {
                throw new InvalidOperationException(
                    "The private workbook could not be opened.");
            }

            OpenedWorkbookPath = Path.GetFullPath(workbookPath);
            ObservedPrivateCopyContents = File.ReadAllText(workbookPath, Encoding.UTF8);
            return new object();
        }

        public HostClassIdentityEnumeration EnumerateClasses(object host, object workbook)
        {
            events.Add("enumerate-classes");
            if (loseProcessDuringClassEnumeration)
            {
                owner.Complete();
                throw new WorkbookAutomationProcessLostException(
                    new WorkbookAutomationStage(
                        WorkbookAutomationStageKind.HostClassEnumeration));
            }

            if (failClassEnumeration)
            {
                throw new InvalidOperationException(
                    "The intrinsic host-class collection could not be read.");
            }

            if (includeDuplicateIdentity)
            {
                return HostClassIdentityEnumeration.CreateComplete(
                    [
                        new HostClassComponentDescriptor(
                            1,
                            new HostClassIdentity(
                                "Sheet1",
                                HostClassComponentKind.Document)),
                        new HostClassComponentDescriptor(
                            2,
                            new HostClassIdentity(
                                "sheet1",
                                HostClassComponentKind.Document)),
                        new HostClassComponentDescriptor(
                            3,
                            new HostClassIdentity(
                                "UserForm1",
                                HostClassComponentKind.Form))
                    ]) with
                {
                    VbaProjectName = "BookProject"
                };
            }

            var components = new List<HostClassComponentDescriptor>
            {
                new(
                    1,
                    new HostClassIdentity(
                        "ThisWorkbook",
                        HostClassComponentKind.Document))
            };
            if (includeSecondClass)
            {
                components.Add(new HostClassComponentDescriptor(
                    2,
                    new HostClassIdentity(
                        "Sheet1",
                        HostClassComponentKind.Document)));
            }

            if (includeThirdClass)
            {
                components.Add(new HostClassComponentDescriptor(
                    3,
                    new HostClassIdentity(
                        "UserForm1",
                        HostClassComponentKind.Form)));
            }

            return HostClassIdentityEnumeration.CreateComplete(components) with
            {
                VbaProjectName = "BookProject"
            };
        }

        public HostClassInspectionEntry InspectClass(
            object host,
            object workbook,
            HostClassComponentDescriptor component)
        {
            events.Add($"inspect:{component.Identity.Name}");
            inspectedClassCount++;
            if (loseProcessDuringSecondClassInspection && inspectedClassCount == 2)
            {
                owner.Complete();
                throw new WorkbookAutomationProcessLostException(
                    new WorkbookAutomationStage(
                        WorkbookAutomationStageKind.HostClassInspection,
                        component.Identity.Name));
            }

            if (failFirstClassInspection && inspectedClassCount == 1)
            {
                throw new InvalidOperationException(
                    "The intrinsic host class could not be inspected.");
            }

            if (makeSecondInspectionUntrusted && inspectedClassCount == 2)
            {
                throw new HostClassInspectionStateUntrustedException(
                    "The private CodeModule could not be restored exactly.");
            }

            return new ResolvedHostClassInspectionEntry(
                component.Identity,
                "Workbook",
                []);
        }

        public void CloseWorkbookWithoutSave(object workbook)
            => events.Add("close:false");

        public void DisposeHost(object host, TimeSpan cleanupGrace)
        {
            events.Add("quit");
            if (!failReleaseProof)
            {
                owner.Complete();
            }

            if (throwAfterProvedReleaseOnDisposeHost)
            {
                throw new InvalidOperationException(
                    "Excel quit reported an error after the exact process exited.");
            }
        }
    }

    private sealed class RecordingOwnedProcess(
        List<string> events,
        bool failReleaseProof = false)
        : IOwnedExcelProcessControl
    {
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasExited => completion.Task.IsCompleted;

        public Task Completion => completion.Task;

        public Task TerminateAsync()
        {
            if (!failReleaseProof)
            {
                Complete();
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Complete()
        {
            if (completion.TrySetResult())
            {
                events.Add("process-exit-proved");
            }
        }
    }

    private sealed class RecordingWorkspaceFileSystem(
        List<string> events,
        int deleteFailures = 0)
        : IHostClassInspectionWorkspaceFileSystem
    {
        private int deleteAttempts;
        public bool FileExists(string path) => File.Exists(path);

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public void CopyFile(string sourcePath, string destinationPath)
        {
            events.Add("copy");
            File.Copy(sourcePath, destinationPath, overwrite: false);
        }

        public void DeleteDirectory(string path)
        {
            events.Add("delete");
            deleteAttempts++;
            if (deleteAttempts <= deleteFailures)
            {
                throw new IOException("The workspace is temporarily locked.");
            }

            Directory.Delete(path, recursive: true);
        }

        public void Delay(TimeSpan delay)
        {
        }
    }
}
