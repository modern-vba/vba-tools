namespace VbaDev.App.CommonModules;

/// <summary>Keeps a canonical declaration with its exact snapshot-owned source and optional sidecar.</summary>
public sealed class CommonModulesCapturedSourceUnit
{
    private readonly CommonModulesPackageSnapshot snapshot;
    private readonly CommonModuleManifestEntry entry;
    private readonly string? sidecarFileName;

    private CommonModulesCapturedSourceUnit(
        CommonModulesPackageSnapshot snapshot,
        CommonModuleManifestEntry entry,
        string? sidecarFileName)
    {
        this.snapshot = snapshot;
        this.entry = entry;
        this.sidecarFileName = sidecarFileName;
    }

    /// <summary>Gets the immutable canonical declaration.</summary>
    public CommonModuleManifestEntry Entry
    {
        get
        {
            _ = snapshot.Package;
            return entry;
        }
    }

    /// <summary>Gets an independent copy of the exact captured source bytes.</summary>
    public byte[] SourceBytes => snapshot.ReadFileBytes(entry.ModuleFile);

    /// <summary>Gets the exact matching sidecar filename, or null if none was captured.</summary>
    public string? SidecarFileName
    {
        get
        {
            _ = snapshot.Package;
            return sidecarFileName;
        }
    }

    /// <summary>Gets an independent copy of captured sidecar bytes, or null for an absent sidecar.</summary>
    public byte[]? SidecarBytes
    {
        get
        {
            _ = snapshot.Package;
            return sidecarFileName is null ? null : snapshot.ReadFileBytes(sidecarFileName);
        }
    }

    internal static CommonModulesCapturedSourceUnit Create(
        CommonModulesPackageSnapshot snapshot,
        string moduleFile)
    {
        var entry = snapshot.Package.GetEntryByFileName(moduleFile);
        string? sidecarName = null;
        if (entry.ModuleFile.EndsWith(".frm", StringComparison.Ordinal))
        {
            var candidate = Path.ChangeExtension(entry.ModuleFile, ".frx");
            if (snapshot.ContainsCapturedFile(candidate))
            {
                sidecarName = candidate;
            }
        }

        return new CommonModulesCapturedSourceUnit(snapshot, entry, sidecarName);
    }
}
