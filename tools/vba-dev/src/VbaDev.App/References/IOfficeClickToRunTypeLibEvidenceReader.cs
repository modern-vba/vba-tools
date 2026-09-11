namespace VbaDev.App.References;

/// <summary>Captures authoritative Office Click-to-Run installation and virtual TypeLib registration evidence.</summary>
public interface IOfficeClickToRunTypeLibEvidenceReader
{
    /// <summary>Reads one immutable evidence snapshot for the current operation.</summary>
    OfficeClickToRunTypeLibEvidenceSnapshot Read();
}

/// <summary>An immutable snapshot of scoped Office Click-to-Run TypeLib evidence.</summary>
public sealed class OfficeClickToRunTypeLibEvidenceSnapshot
{
    public OfficeClickToRunTypeLibEvidenceSnapshot(
        bool complete,
        IReadOnlyList<OfficeClickToRunInstallationEvidence> installations,
        string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(installations);
        Complete = complete;
        Installations = Array.AsReadOnly(installations.Select(installation =>
            new OfficeClickToRunInstallationEvidence(
                installation.InstallationPath,
                installation.Platform,
                installation.Registrations)).ToArray());
        Diagnostic = diagnostic;
    }

    public bool Complete { get; }

    public IReadOnlyList<OfficeClickToRunInstallationEvidence> Installations { get; }

    public string? Diagnostic { get; }

    /// <summary>An available snapshot containing no Click-to-Run installation evidence.</summary>
    public static OfficeClickToRunTypeLibEvidenceSnapshot Empty { get; } = new(true, [], null);
}

/// <summary>One authoritative Office Click-to-Run installation and its virtual TypeLib registrations.</summary>
public sealed class OfficeClickToRunInstallationEvidence
{
    public OfficeClickToRunInstallationEvidence(
        string installationPath,
        string platform,
        IReadOnlyList<OfficeClickToRunTypeLibRegistrationEvidence> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        InstallationPath = installationPath;
        Platform = platform;
        Registrations = Array.AsReadOnly(registrations.Select(registration => registration with { }).ToArray());
    }

    public string InstallationPath { get; }

    public string Platform { get; }

    public IReadOnlyList<OfficeClickToRunTypeLibRegistrationEvidence> Registrations { get; }
}

/// <summary>One exact TypeLib registration captured from the Office Click-to-Run registry scope.</summary>
public sealed record OfficeClickToRunTypeLibRegistrationEvidence(
    string ReferenceName,
    string Guid,
    int Major,
    int Minor,
    int Lcid,
    string Platform,
    string VirtualPath);

/// <summary>Supplies an available snapshot with no Click-to-Run evidence.</summary>
public sealed class EmptyOfficeClickToRunTypeLibEvidenceReader : IOfficeClickToRunTypeLibEvidenceReader
{
    /// <inheritdoc />
    public OfficeClickToRunTypeLibEvidenceSnapshot Read()
        => OfficeClickToRunTypeLibEvidenceSnapshot.Empty;
}
