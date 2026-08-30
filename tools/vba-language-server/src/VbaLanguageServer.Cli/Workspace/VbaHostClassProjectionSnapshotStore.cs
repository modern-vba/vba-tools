using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;

namespace VbaLanguageServer.Workspace;

internal sealed record VbaHostClassProjectionSelectionState(
    long Revision,
    VbaHostClassProjectionContext? Context,
    VbaHostClassProjectionSnapshot? Snapshot);

internal sealed record VbaHostClassProjectionSnapshotUpdate(
    VbaHostClassProjectionContext Context,
    long Revision,
    VbaHostClassProjectionSnapshot? Snapshot)
{
    internal bool TryGetAuthority(
        out VbaProjectAuthorityIdentity authority)
        => VbaProjectIdentityModel.TryIdentifyAuthority(
            new VbaProjectResolution(
                VbaProjectResolutionKind.ManifestDocument,
                RootPath: Context.Project,
                ManifestPath: Path.Combine(
                    Context.Project,
                    "vba-project.json"),
                DocumentName: Context.Document),
            out authority);
}

internal sealed class VbaHostClassProjectionSnapshotStore
{
    private readonly object gate = new();
    private readonly Dictionary<
        VbaProjectAuthorityIdentity,
        VbaHostClassProjectionSelectionState> states = new();

    public VbaHostClassProjectionSelectionState CaptureSelectionState(
        VbaProjectResolution resolution)
    {
        if (!TryCreateContext(resolution, out var context)
            || !VbaProjectIdentityModel.TryIdentifyAuthority(
                resolution,
                out var authority))
        {
            return new VbaHostClassProjectionSelectionState(0, null, null);
        }

        lock (gate)
        {
            if (!states.TryGetValue(authority, out var state))
            {
                return new VbaHostClassProjectionSelectionState(0, null, null);
            }

            return ContextsEqual(state.Context!, context)
                ? state
                : state with { Snapshot = null };
        }
    }

    public bool TryApply(VbaHostClassProjectionSnapshotUpdate update)
    {
        var captured = CaptureUpdate(update);
        if (!captured.TryGetAuthority(out var authority))
        {
            return false;
        }

        lock (gate)
        {
            if (states.TryGetValue(authority, out var current)
                && current.Revision >= captured.Revision)
            {
                return false;
            }

            states[authority] = new VbaHostClassProjectionSelectionState(
                captured.Revision,
                captured.Context,
                captured.Snapshot);
            return true;
        }
    }

    public bool TryApplyRetainedClear(
        VbaHostClassProjectionSnapshotUpdate update)
    {
        if (update.Snapshot is not null)
        {
            return false;
        }

        if (!update.TryGetAuthority(out var authority))
        {
            return false;
        }

        lock (gate)
        {
            if (!states.TryGetValue(authority, out var current)
                || current.Context is null
                || !ContextsEqual(current.Context, update.Context)
                || current.Revision >= update.Revision)
            {
                return false;
            }

            states[authority] = new VbaHostClassProjectionSelectionState(
                update.Revision,
                update.Context,
                Snapshot: null);
            return true;
        }
    }

    public static bool Matches(
        VbaProjectResolution resolution,
        VbaHostClassProjectionContext context)
        => TryCreateContext(resolution, out var current)
            && PathsEqual(current.Project, context.Project)
            && current.Document.Equals(context.Document, StringComparison.Ordinal)
            && PathsEqual(current.SourceTemplate, context.SourceTemplate);

    private static bool TryCreateContext(
        VbaProjectResolution resolution,
        out VbaHostClassProjectionContext context)
    {
        context = default!;
        if (resolution.Kind != VbaProjectResolutionKind.ManifestDocument
            || string.IsNullOrWhiteSpace(resolution.ManifestPath)
            || string.IsNullOrWhiteSpace(resolution.DocumentName)
            || string.IsNullOrWhiteSpace(resolution.SourceTemplatePath))
        {
            return false;
        }

        var project = Path.GetDirectoryName(resolution.ManifestPath);
        if (string.IsNullOrWhiteSpace(project)
            || !TryNormalizePath(project, out var normalizedProject)
            || !TryNormalizePath(
                resolution.SourceTemplatePath,
                out var normalizedSourceTemplate))
        {
            return false;
        }

        context = new VbaHostClassProjectionContext(
            normalizedProject,
            resolution.DocumentName,
            normalizedSourceTemplate);
        return true;
    }

    private static bool ContextsEqual(
        VbaHostClassProjectionContext left,
        VbaHostClassProjectionContext right)
        => PathsEqual(left.Project, right.Project)
            && left.Document.Equals(right.Document, StringComparison.Ordinal)
            && PathsEqual(left.SourceTemplate, right.SourceTemplate);

    private static bool PathsEqual(string left, string right)
        => TryNormalizePath(left, out var normalizedLeft)
            && TryNormalizePath(right, out var normalizedRight)
            && normalizedLeft.Equals(
                normalizedRight,
                StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
        => Path.GetFullPath(path)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);

    private static bool TryNormalizePath(string path, out string normalizedPath)
    {
        normalizedPath = "";
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            normalizedPath = NormalizePath(path);
            return normalizedPath.Length > 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    private static VbaHostClassProjectionSnapshotUpdate CaptureUpdate(
        VbaHostClassProjectionSnapshotUpdate update)
    {
        var context = new VbaHostClassProjectionContext(
            update.Context.Project,
            update.Context.Document,
            update.Context.SourceTemplate);
        return new VbaHostClassProjectionSnapshotUpdate(
            context,
            update.Revision,
            update.Snapshot is null
                ? null
                : new VbaHostClassProjectionSnapshot(
                    update.Snapshot.Revision,
                    context,
                    update.Snapshot.ClassEnumerationComplete,
                    FreezeList(update.Snapshot.Classes.Select(CaptureEntry)),
                    update.Snapshot.VbaProjectName,
                    update.Snapshot.SourceTemplateFingerprint));
    }

    private static VbaHostClassProjectionEntry CaptureEntry(
        VbaHostClassProjectionEntry entry)
    {
        var identity = new VbaHostClassIdentity(
            entry.Identity.Name,
            entry.Identity.Kind);
        return entry switch
        {
            VbaCurrentHostClassProjectionEntry current =>
                new VbaCurrentHostClassProjectionEntry(
                    identity,
                    CaptureProjection(current.Projection)),
            VbaLastKnownGoodHostClassProjectionEntry lastKnownGood =>
                new VbaLastKnownGoodHostClassProjectionEntry(
                    identity,
                    CaptureProjection(lastKnownGood.Projection)),
            VbaIndeterminateHostClassProjectionEntry =>
                new VbaIndeterminateHostClassProjectionEntry(identity),
            _ => throw new InvalidOperationException(
                $"Unsupported host-class projection entry {entry.GetType().Name}.")
        };
    }

    private static VbaHostClassProjection CaptureProjection(
        VbaHostClassProjection projection)
        => new(
            projection.IntrinsicEventSourceName,
            FreezeList(projection.Events.Select(hostEvent => new VbaHostEventSignature(
                hostEvent.Name,
                FreezeList(hostEvent.Parameters.Select(parameter => parameter with { })),
                hostEvent.Documentation,
                hostEvent.AuthoringAvailable,
                hostEvent.ExistingHandlerRecognizable))),
            projection.BaseTypeProvenance is null
                ? null
                : projection.BaseTypeProvenance with { });

    private static IReadOnlyList<T> FreezeList<T>(IEnumerable<T> values)
        => Array.AsReadOnly(values.ToArray());
}
