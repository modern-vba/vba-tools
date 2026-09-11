using System.Diagnostics;
using VbaDev.App.Build;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.References;
using VbaTools.TypeLibRegistry;
using Xunit;

namespace VbaDev.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
public sealed class OfficeClickToRunTypeLibWindowsIntegrationTests
{
    private const string ReferenceName = "Microsoft Windows Common Controls 6.0 (SP6)";
    private const string ReferenceGuid = "831fdd16-0c5c-11d2-a9fc-0000f8754da1";
    private const string ReferenceNamespace = "MSComctlLib";

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public Task InstalledClickToRunCommonControlsResolvesAndLoadsItsPhysicalTypeLib()
    {
        Assert.True(OperatingSystem.IsWindows(),
            "The opted-in Office Click-to-Run integration test requires Windows.");
        var initialProcesses = CaptureExcelProcessIds();
        var evidence = new RegistryOfficeClickToRunTypeLibEvidenceReader().Read();

        Assert.True(evidence.Complete, evidence.Diagnostic);
        var match = Assert.Single(
            evidence.Installations.SelectMany(installation =>
                installation.Registrations.Select(registration => (installation, registration))),
            item => item.registration.ReferenceName.Equals(ReferenceName, StringComparison.OrdinalIgnoreCase)
                && item.registration.Guid.Equals(ReferenceGuid, StringComparison.OrdinalIgnoreCase)
                && item.registration.Major == 2
                && item.registration.Minor == 2);
        Assert.Equal("x64", match.installation.Platform, ignoreCase: true);
        Assert.Equal(0, match.registration.Lcid);
        Assert.Equal("win64", match.registration.Platform, ignoreCase: true);
        Assert.Equal(@"C:\Windows\system32\MSCOMCTL.OCX", match.registration.VirtualPath, ignoreCase: true);
        Assert.False(File.Exists(match.registration.VirtualPath));

        var ordinaryRegistry = new RegistryTypeLibRegistryCatalogReader().Read();
        Assert.True(ordinaryRegistry.Complete, ordinaryRegistry.Diagnostic?.Message);
        var ordinaryExactVersions = ordinaryRegistry.Find(ReferenceName)?.Lineages
            .Where(lineage => lineage.Guid.Equals(ReferenceGuid, StringComparison.OrdinalIgnoreCase))
            .SelectMany(lineage => lineage.Versions)
            .Where(version => version.Major == 2 && version.Minor == 2)
            .ToArray() ?? [];
        Assert.Empty(ordinaryExactVersions);

        var resolution = OfficeClickToRunTypeLibPathResolver.Resolve(
            evidence,
            new ResolvedVbaProjectReference(ReferenceName, ReferenceGuid, 2, 2),
            match.registration.VirtualPath);

        Assert.True(resolution.Applicable);
        Assert.Null(resolution.Diagnostic);
        var candidate = Assert.IsType<OfficeClickToRunTypeLibPathCandidate>(resolution.Candidate);
        var expectedPath = Path.Combine(
            match.installation.InstallationPath,
            "root",
            "vfs",
            "System",
            "MSCOMCTL.OCX");
        Assert.Equal(Path.GetFullPath(expectedPath), candidate.Path, ignoreCase: true);
        Assert.Equal(0, candidate.Lcid);
        Assert.True(File.Exists(candidate.Path),
            $"The authoritative Office Click-to-Run TypeLib does not exist at '{candidate.Path}'.");

        var acquired = new ComTypeLibCatalogMetadataReader()
            .ReadMetadataFromPath(ReferenceName, candidate.Path);

        Assert.Equal(ReferenceName, acquired.Identity.ReferenceName);
        Assert.Equal(ReferenceGuid, acquired.Identity.Guid, ignoreCase: true);
        Assert.Equal(2, acquired.Identity.MajorVersion);
        Assert.Equal(2, acquired.Identity.MinorVersion);
        Assert.Equal(candidate.Lcid, acquired.Identity.Lcid);
        Assert.Equal(candidate.Path, acquired.Identity.Path, ignoreCase: true);
        Assert.Equal(ReferenceNamespace, acquired.Metadata.QualifierAlias);
        Assert.Equal(ReferenceNamespace, acquired.Metadata.ReferencedVbaProjectName);
        Assert.Contains(acquired.Metadata.Types, type => type.Name == "ListView");
        Assert.Contains(acquired.Metadata.Types, type => type.Name == "TreeView");
        Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()),
            "Reading Office Click-to-Run evidence and TypeLib metadata must not start Excel.");
        return Task.CompletedTask;
    }

    private static IReadOnlySet<int> CaptureExcelProcessIds()
    {
        var processIds = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("EXCEL"))
        {
            using (process)
            {
                try
                {
                    processIds.Add(process.Id);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        return processIds;
    }
}
