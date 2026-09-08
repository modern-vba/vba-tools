using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace VbaDebugAdapter.Infrastructure;

/// <summary>Reports release at the native owner boundary instead of treating disposal as proof.</summary>
internal sealed class DebugNativeHandleRelease(SafeHandle handle)
{
    private readonly object gate = new();
    private bool attempted;
    private bool verified;
    private ExceptionDispatchInfo? failure;

    public bool IsVerified { get { lock (gate) { return verified; } } }

    public void Release()
    {
        lock (gate)
        {
            if (attempted)
            {
                failure?.Throw();
                return;
            }
            attempted = true;
            try
            {
                if (handle.IsClosed || handle.IsInvalid)
                {
                    throw new InvalidOperationException("The owned handle was already closed without native release evidence.");
                }
                // The resource owner must first finish operations using this handle.
                // In particular, an unsettled pipe read is not eligible for this path.
                var retained = false;
                try
                {
                    handle.DangerousAddRef(ref retained);
                    if (!CloseHandle(handle.DangerousGetHandle()))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    verified = true;
                }
                finally
                {
                    // Never close the numeric value again, including after an ambiguous
                    // native failure: Windows can reuse handle values for other resources.
                    handle.SetHandleAsInvalid();
                    if (retained) { handle.DangerousRelease(); }
                }
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
                throw;
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
