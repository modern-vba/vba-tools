using System.Runtime.InteropServices;

namespace VbaDebugAdapter.Infrastructure;

/// <summary>
/// Releases late-bound COM objects and forces collection after Excel automation cleanup.
/// </summary>
internal static class ComObjectReleaser
{
    /// <summary>
    /// Final-releases a COM object when running on Windows.
    /// </summary>
    /// <param name="value">The possible COM object to release.</param>
    public static bool Release(object? value)
    {
        // Non-COM test and managed values do not acquire a runtime callable wrapper.
        return value is null || !OperatingSystem.IsWindows() || !Marshal.IsComObject(value)
            || Marshal.FinalReleaseComObject(value) == 0;
    }

    public static DebugFailureOutcome ReleaseScope(
        Exception? primaryFailure,
        int? processId,
        string? retainedPath,
        Func<object?, bool> release,
        params (string Resource, object? Value)[] resources)
    {
        var completion = new DebugFailureCompletion(primaryFailure);
        var observed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var (resource, value) in resources)
        {
            if (value is null || !observed.Add(value))
            {
                continue;
            }

            var released = false;
            try
            {
                released = release(value);
            }
            catch (Exception failure)
            {
                completion.AddFailure("local COM release", resource, DebugResourceKind.Com,
                    failure, processId, retainedPath);
            }

            completion.AddEvidence(new("local COM release", resource, DebugResourceKind.Com,
                released, released ? "The local COM reference was released."
                    : "The local COM release was not proved.", processId, retainedPath));
        }

        var outcome = completion.Complete();
        if (outcome.HasCleanupFailure)
        {
            outcome.Throw();
        }

        return outcome;
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
