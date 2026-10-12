namespace VbaLanguageServer.Lsp;

/// <summary>
/// Waits for automatic reference work using fresh coordinator snapshots.
/// </summary>
internal static class ReferenceCatalogIdleWait
{
    public static async Task WaitAsync(
        Func<(Task[] BackgroundTasks, Task MailboxIdle)> capture)
    {
        while (true)
        {
            var (tasks, mailboxIdle) = capture();
            // Completion is authoritative; removal continuations may still be
            // waiting to run. Awaiting completed tasks cannot yield to them.
            if (tasks.All(task => task.IsCompletedSuccessfully)
                && mailboxIdle.IsCompletedSuccessfully)
            {
                return;
            }

            await Task.WhenAll(tasks.Append(mailboxIdle));
        }
    }
}
