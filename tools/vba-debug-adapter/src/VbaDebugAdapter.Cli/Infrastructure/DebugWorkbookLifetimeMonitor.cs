using Microsoft.CSharp.RuntimeBinder;
using System.Runtime.InteropServices;
using VbaDebugAdapter.Debugging;

namespace VbaDebugAdapter.Infrastructure;

internal interface IDebugWorkbookLifetimeMonitor
{
    Task WaitForCloseAsync(
        object workbookObject,
        IStaComDispatcher dispatcher,
        Task<DebugProcessExit> processCompletion);
}

internal sealed class DebugWorkbookLifetimeMonitor : IDebugWorkbookLifetimeMonitor
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public async Task WaitForCloseAsync(
        object workbookObject,
        IStaComDispatcher dispatcher,
        Task<DebugProcessExit> processCompletion)
    {
        ArgumentNullException.ThrowIfNull(workbookObject);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(processCompletion);

        while (!processCompletion.IsCompleted)
        {
            var delay = Task.Delay(PollInterval);
            if (ReferenceEquals(
                    await Task.WhenAny(processCompletion, delay).ConfigureAwait(false),
                    processCompletion))
            {
                _ = await processCompletion.ConfigureAwait(false);
                return;
            }

            try
            {
                var isOpen = await dispatcher.InvokeAsync(
                    () => IsWorkbookOpen(workbookObject),
                    CancellationToken.None).ConfigureAwait(false);
                if (!isOpen)
                {
                    return;
                }
            }
            catch (COMException exception) when (IsTransientComBusy(exception.HResult))
            {
            }
            catch (Exception exception) when (
                exception is COMException or RuntimeBinderException or InvalidCastException)
            {
                return;
            }
        }
    }

    private static bool IsWorkbookOpen(object workbookObject)
    {
        dynamic workbook = workbookObject;
        var fullName = (string?)workbook.FullName;
        return !string.IsNullOrWhiteSpace(fullName);
    }

    private static bool IsTransientComBusy(int hResult)
        => unchecked((uint)hResult) is
            0x80010001 or // RPC_E_CALL_REJECTED
            0x8001010A or // RPC_E_SERVERCALL_RETRYLATER
            0x800AC472;   // VBA_E_IGNORE
}
