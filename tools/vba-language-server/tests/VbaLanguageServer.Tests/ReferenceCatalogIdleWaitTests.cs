using VbaLanguageServer.Lsp;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class ReferenceCatalogIdleWaitTests
{
    [Fact]
    public async Task Completed_work_does_not_wait_for_tracking_cleanup()
    {
        var retained = true;
        var wait = Task.Run(() => ReferenceCatalogIdleWait.WaitAsync(() =>
            (Volatile.Read(ref retained) ? [Task.CompletedTask] : [], Task.CompletedTask)));
        try
        {
            await wait.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            // Also release the old implementation after a regression failure.
            Volatile.Write(ref retained, false);
            await wait.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Pending_work_and_mailbox_must_both_finish()
    {
        var work = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mailbox = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = ReferenceCatalogIdleWait.WaitAsync(() => ([work.Task], mailbox.Task));
        try
        {
            Assert.False(wait.IsCompleted);
            work.SetResult();
            Assert.False(wait.IsCompleted);
            mailbox.SetResult();
            await wait.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            work.TrySetResult();
            mailbox.TrySetResult();
            await wait.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Work_in_a_new_snapshot_is_observed_before_returning()
    {
        var firstWork = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var nextWork = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var nextSnapshot = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = ReferenceCatalogIdleWait.WaitAsync(() =>
        {
            if (!firstWork.Task.IsCompleted)
            {
                return ([firstWork.Task], Task.CompletedTask);
            }
            nextSnapshot.TrySetResult();
            return ([nextWork.Task], Task.CompletedTask);
        });
        try
        {
            firstWork.SetResult();
            await nextSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(wait.IsCompleted);
            nextWork.SetResult();
            await wait.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            firstWork.TrySetResult();
            nextWork.TrySetResult();
            await wait.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Faults_and_cancellation_are_not_reported_as_idle(
        bool mailboxFailure,
        bool cancelled)
    {
        var error = new InvalidOperationException("Expected reference work failure.");
        var cancellation = new CancellationToken(canceled: true);
        var failed = cancelled
            ? Task.FromCanceled(cancellation)
            : Task.FromException(error);
        var wait = ReferenceCatalogIdleWait.WaitAsync(() => mailboxFailure
            ? ([], failed)
            : ([failed], Task.CompletedTask));

        if (cancelled)
        {
            var observed = await Assert.ThrowsAsync<TaskCanceledException>(
                () => wait.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(cancellation, observed.CancellationToken);
        }
        else
        {
            var observed = await Assert.ThrowsAsync<InvalidOperationException>(
                () => wait.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(error, observed);
        }
    }
}
