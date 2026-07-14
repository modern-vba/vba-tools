using System.Runtime.InteropServices;

namespace VbaDev.Infrastructure.Workbooks;

/// <summary>
/// Releases late-bound COM objects and forces collection after Excel automation cleanup.
/// </summary>
internal static class ComObjectReleaser
{
    /// <summary>
    /// Final-releases a COM object when running on Windows.
    /// </summary>
    /// <param name="value">The possible COM object to release.</param>
    public static void Release(object? value)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    /// <summary>
    /// Runs garbage collection passes needed to complete COM finalizer cleanup.
    /// </summary>
    public static void CollectReleasedComObjects()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
