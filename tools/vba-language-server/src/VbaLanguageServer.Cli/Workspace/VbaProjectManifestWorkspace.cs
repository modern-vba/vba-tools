using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Describes whether a versioned manifest overlay update changed effective project state.
/// </summary>
internal sealed record VbaProjectManifestOverlayUpdate(
    bool Accepted,
    bool EffectiveChanged,
    VbaProjectManifestException? Error);

/// <summary>
/// Tracks open project-manifest overlays and watched disk authority for language-server resolution.
/// </summary>
internal sealed class VbaProjectManifestWorkspace
{
    private const string ManifestFileName = "vba-project.json";
    private readonly object gate = new();
    private readonly Dictionary<string, ManifestState> states = new(StringComparer.OrdinalIgnoreCase);
    private long version;

    /// <summary>
    /// Gets the manifest-state version used by project snapshot caches.
    /// </summary>
    public long Version
    {
        get
        {
            lock (gate)
            {
                return version;
            }
        }
    }

    /// <summary>
    /// Opens a versioned manifest overlay that takes precedence over disk state.
    /// </summary>
    public VbaProjectManifestOverlayUpdate OpenManifest(string uri, int documentVersion, string text)
    {
        if (!TryGetManifestPath(uri, out var manifestPath))
        {
            return new VbaProjectManifestOverlayUpdate(false, false, null);
        }

        var overlayIsValid = TryCreateEffectiveManifest(
            manifestPath,
            uri,
            text,
            out var overlayManifest,
            out var error);
        lock (gate)
        {
            states.TryGetValue(manifestPath, out var existing);
            var effectiveManifest = overlayManifest ?? existing?.OpenManifest?.EffectiveManifest;
            if (effectiveManifest is null && existing?.DiskDeleted != true)
            {
                effectiveManifest = TryReadValidDiskManifest(manifestPath);
            }

            states[manifestPath] = new ManifestState(
                new OpenManifestState(documentVersion, effectiveManifest),
                existing?.DiskDeleted == true);
            version++;
            return new VbaProjectManifestOverlayUpdate(
                Accepted: true,
                EffectiveChanged: overlayIsValid,
                Error: error);
        }
    }

    /// <summary>
    /// Changes an open manifest only when the incoming version is newer.
    /// </summary>
    public VbaProjectManifestOverlayUpdate ChangeManifest(string uri, int documentVersion, string text)
    {
        if (!TryGetManifestPath(uri, out var manifestPath))
        {
            return new VbaProjectManifestOverlayUpdate(false, false, null);
        }

        var overlayIsValid = TryCreateEffectiveManifest(
            manifestPath,
            uri,
            text,
            out var overlayManifest,
            out var error);
        lock (gate)
        {
            if (!states.TryGetValue(manifestPath, out var existing)
                || existing.OpenManifest is null
                || documentVersion <= existing.OpenManifest.Version)
            {
                return new VbaProjectManifestOverlayUpdate(false, false, null);
            }

            states[manifestPath] = existing with
            {
                OpenManifest = new OpenManifestState(
                    documentVersion,
                    overlayManifest ?? existing.OpenManifest.EffectiveManifest)
            };
            version++;
            return new VbaProjectManifestOverlayUpdate(
                Accepted: true,
                EffectiveChanged: overlayIsValid,
                Error: error);
        }
    }

    /// <summary>
    /// Closes an open manifest overlay and restores effective disk or deletion state.
    /// </summary>
    public bool CloseManifest(string uri)
    {
        if (!TryGetManifestPath(uri, out var manifestPath))
        {
            return false;
        }

        lock (gate)
        {
            if (!states.TryGetValue(manifestPath, out var existing)
                || existing.OpenManifest is null)
            {
                return false;
            }

            if (existing.DiskDeleted)
            {
                states[manifestPath] = existing with { OpenManifest = null };
            }
            else
            {
                states.Remove(manifestPath);
            }

            version++;
            return true;
        }
    }

    /// <summary>
    /// Records a watched manifest create or change without replacing an open overlay.
    /// </summary>
    /// <returns>True when disk state is authoritative and should be processed.</returns>
    public bool ReloadManifest(string uri)
    {
        if (!TryGetManifestPath(uri, out var manifestPath))
        {
            return false;
        }

        lock (gate)
        {
            if (states.TryGetValue(manifestPath, out var existing)
                && existing.OpenManifest is not null)
            {
                if (existing.DiskDeleted)
                {
                    states[manifestPath] = existing with { DiskDeleted = false };
                    version++;
                }

                return false;
            }

            states.Remove(manifestPath);
            version++;
            return true;
        }
    }

    /// <summary>
    /// Records a watched manifest deletion without removing an open overlay.
    /// </summary>
    /// <returns>True when the effective manifest was deleted; false when an overlay remains or state was unchanged.</returns>
    public bool DeleteManifest(string uri)
    {
        if (!TryGetManifestPath(uri, out var manifestPath))
        {
            return false;
        }

        lock (gate)
        {
            states.TryGetValue(manifestPath, out var existing);
            if (existing?.DiskDeleted == true)
            {
                return false;
            }

            states[manifestPath] = new ManifestState(existing?.OpenManifest, DiskDeleted: true);
            version++;
            return existing?.OpenManifest is null;
        }
    }

    /// <summary>
    /// Gets the effective open or disk manifest text for one manifest URI.
    /// </summary>
    public bool TryGetEffectiveManifest(
        string uri,
        out string effectiveUri,
        out string text,
        out VbaProjectManifestException? error)
    {
        effectiveUri = "";
        text = "";
        error = null;
        if (!TryGetManifestPath(uri, out var manifestPath))
        {
            return false;
        }

        ManifestState? state;
        lock (gate)
        {
            states.TryGetValue(manifestPath, out state);
        }

        try
        {
            if (!TryReadEffectiveManifest(manifestPath, state, out var effectiveManifest))
            {
                return false;
            }

            effectiveUri = effectiveManifest.Uri;
            text = effectiveManifest.Text;
            return true;
        }
        catch (VbaProjectManifestException ex)
        {
            error = ex;
            return false;
        }
    }

    /// <summary>
    /// Resolves a source URI against effective manifest overlays and watched deletion state.
    /// </summary>
    public VbaProjectResolution Resolve(string activeUri)
    {
        var activePath = VbaProjectResolver.TryGetLocalPath(activeUri);
        if (activePath is null)
        {
            return new VbaProjectResolution(VbaProjectResolutionKind.AdHoc, "");
        }

        var activeDirectory = Path.GetDirectoryName(activePath) ?? Directory.GetCurrentDirectory();
        var stateSnapshot = CopyStates();
        for (var directory = new DirectoryInfo(activeDirectory); directory is not null; directory = directory.Parent)
        {
            var manifestPath = Path.Combine(directory.FullName, ManifestFileName);
            stateSnapshot.TryGetValue(manifestPath, out var state);
            if (!TryReadEffectiveManifest(manifestPath, state, out var effectiveManifest))
            {
                continue;
            }

            foreach (var (documentName, document) in effectiveManifest.Manifest.Documents)
            {
                var sourceRoot = effectiveManifest.SourceRoots[documentName];
                if (VbaProjectResolver.IsPathUnder(activePath, sourceRoot))
                {
                    return new VbaProjectResolution(
                        VbaProjectResolutionKind.ManifestDocument,
                        sourceRoot,
                        manifestPath,
                        documentName,
                        document.Kind,
                        document.References ?? []);
                }
            }

            return new VbaProjectResolution(VbaProjectResolutionKind.AdHoc, activeDirectory);
        }

        return new VbaProjectResolution(VbaProjectResolutionKind.AdHoc, activeDirectory);
    }

    private Dictionary<string, ManifestState> CopyStates()
    {
        lock (gate)
        {
            return new Dictionary<string, ManifestState>(states, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool TryReadEffectiveManifest(
        string manifestPath,
        ManifestState? state,
        out EffectiveManifest effectiveManifest)
    {
        if (state?.OpenManifest is not null)
        {
            effectiveManifest = state.OpenManifest.EffectiveManifest!;
            return state.OpenManifest.EffectiveManifest is not null;
        }

        if (state?.DiskDeleted == true || !File.Exists(manifestPath))
        {
            effectiveManifest = default!;
            return false;
        }

        effectiveManifest = ReadDiskManifest(manifestPath);
        return true;
    }

    private static EffectiveManifest? TryReadValidDiskManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            return ReadDiskManifest(manifestPath);
        }
        catch (VbaProjectManifestException)
        {
            return null;
        }
    }

    private static EffectiveManifest ReadDiskManifest(string manifestPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new VbaProjectManifestException(
                $"Project manifest could not be read: {manifestPath}",
                ex);
        }

        return CreateEffectiveManifest(
            manifestPath,
            new Uri(manifestPath).AbsoluteUri,
            text);
    }

    private static bool TryCreateEffectiveManifest(
        string manifestPath,
        string uri,
        string text,
        out EffectiveManifest? effectiveManifest,
        out VbaProjectManifestException? error)
    {
        try
        {
            effectiveManifest = CreateEffectiveManifest(manifestPath, uri, text);
            error = null;
            return true;
        }
        catch (VbaProjectManifestException ex)
        {
            effectiveManifest = null;
            error = ex;
            return false;
        }
    }

    private static EffectiveManifest CreateEffectiveManifest(
        string manifestPath,
        string uri,
        string text)
    {
        var manifest = ProjectManifestReader.Parse(text, uri);
        var manifestDirectory = Path.GetDirectoryName(manifestPath) ?? Directory.GetCurrentDirectory();
        var sourceRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (documentName, document) in manifest.Documents)
        {
            try
            {
                sourceRoots[documentName] = Path.GetFullPath(
                    Path.Combine(manifestDirectory, document.SourcePath));
            }
            catch (Exception ex) when (ex is ArgumentException
                or NotSupportedException
                or PathTooLongException
                or System.Security.SecurityException)
            {
                throw new VbaProjectManifestException(
                    $"Document '{documentName}' has an invalid sourcePath in project manifest: {uri}",
                    ex);
            }
        }

        return new EffectiveManifest(uri, text, manifest, sourceRoots);
    }

    private static bool TryGetManifestPath(string uri, out string manifestPath)
    {
        manifestPath = "";
        var localPath = VbaProjectResolver.TryGetLocalPath(uri);
        if (localPath is null
            || !Path.GetFileName(localPath).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        manifestPath = Path.GetFullPath(localPath);
        return true;
    }

    private sealed record ManifestState(
        OpenManifestState? OpenManifest,
        bool DiskDeleted);

    private sealed record OpenManifestState(
        int Version,
        EffectiveManifest? EffectiveManifest);

    private sealed record EffectiveManifest(
        string Uri,
        string Text,
        ProjectManifest Manifest,
        IReadOnlyDictionary<string, string> SourceRoots);
}
