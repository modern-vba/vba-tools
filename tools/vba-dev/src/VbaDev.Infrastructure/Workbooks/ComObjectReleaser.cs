using System.Runtime.InteropServices;

namespace VbaDev.Infrastructure.Workbooks;

internal static class ComObjectReleaser
{
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
