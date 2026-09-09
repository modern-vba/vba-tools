using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VbaDev.App.CommonModules;
using VbaDev.Infrastructure.FileSystem;
using Xunit;

namespace VbaDev.Tests;

public sealed class CommonModulesManifestFixtureTests
{
    private const string FixtureSha256 =
        "0cc8019d254a79753d3fc345aa98b7f4709d80e4d367168cc72cb0d24a75e402";

    [Fact]
    public void ConsumerAcceptsTheVersionedSharedManifestFixtureCorpus()
    {
        var fixturePath = FixturePath();
        var fixtureBytes = File.ReadAllBytes(fixturePath);
        Assert.Equal(FixtureSha256, Convert.ToHexString(SHA256.HashData(fixtureBytes)).ToLowerInvariant());
        var fixtureSet = ReadFixtureSet(fixtureBytes);
        Assert.Equal("1.1", fixtureSet.SchemaVersion);
        Assert.Contains(fixtureSet.Cases, fixture => fixture.Valid);
        Assert.Contains(fixtureSet.Cases, fixture => !fixture.Valid);
        Assert.Contains(fixtureSet.PackageCases, fixture => fixture.Valid);
        Assert.Contains(fixtureSet.PackageCases, fixture => !fixture.Valid);

        using var temp = TempDirectory.Create();
        var reader = new CommonModulesManifestReader();
        for (var index = 0; index < fixtureSet.Cases.Length; index++)
        {
            var fixture = fixtureSet.Cases[index];
            RunFixture(fixture.Name, () =>
            {
                var repositoryPath = temp.CreateDirectory($"case-{index:D2}");
                File.WriteAllBytes(
                    Path.Combine(repositoryPath, CommonModulesManifestReader.ManifestFileName),
                    Convert.FromBase64String(fixture.ManifestBase64));

                if (!fixture.Valid)
                {
                    var error = Record.Exception(() => reader.Load(repositoryPath));
                    Assert.IsType<CommonModulesManifestException>(error);
                    AssertPairedPackageInputs(
                        repositoryPath,
                        temp.CreateDirectory($"manifest-scratch-{index:D2}"),
                        valid: false);
                    return;
                }

                var entries = reader.Load(repositoryPath);
                Assert.Equal(fixture.ExpectedRecords.Length, entries.Count);
                for (var recordIndex = 0; recordIndex < fixture.ExpectedRecords.Length; recordIndex++)
                {
                    var expected = fixture.ExpectedRecords[recordIndex];
                    var actual = entries[recordIndex];
                    Assert.Equal(expected.ModuleFile, actual.ModuleFile);
                    Assert.Equal(expected.Categories, string.Join(',', actual.Categories));
                    Assert.Equal(expected.Dependencies, actual.Dependencies);
                    Assert.Equal(expected.RequiredReferences, actual.RequiredReferences);
                    WriteMinimalSource(repositoryPath, expected.ModuleFile);
                }

                // Manifest grammar permits Optional.bas, but complete package admission
                // must still reject Optional as a reserved VBA ModuleIdentity.
                var expectedPackageFailure = fixture.Name == "valid/categories-dependencies-cycle"
                    ? "CommonModules ModuleIdentity 'Optional' is invalid."
                    : null;
                var package = AssertPairedPackageInputs(
                    repositoryPath,
                    temp.CreateDirectory($"manifest-scratch-{index:D2}"),
                    valid: expectedPackageFailure is null,
                    expectedFailureMessage: expectedPackageFailure);
                if (expectedPackageFailure is not null)
                {
                    return;
                }

                Assert.NotNull(package);
                AssertEquivalentEntries(entries, package.Entries);
            });
        }

        for (var index = 0; index < fixtureSet.PackageCases.Length; index++)
        {
            var fixture = fixtureSet.PackageCases[index];
            RunFixture(fixture.Name, () =>
            {
                var repositoryPath = temp.CreateDirectory($"package-{index:D2}");
                foreach (var entry in fixture.Entries)
                {
                    var entryPath = Path.GetFullPath(Path.Combine(
                        repositoryPath,
                        entry.Path.Replace('/', Path.DirectorySeparatorChar)));
                    Assert.StartsWith(
                        Path.GetFullPath(repositoryPath) + Path.DirectorySeparatorChar,
                        entryPath,
                        StringComparison.OrdinalIgnoreCase);
                    switch (entry.Kind)
                    {
                        case "directory":
                            Directory.CreateDirectory(entryPath);
                            break;
                        case "file":
                            Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
                            File.WriteAllBytes(entryPath, Convert.FromBase64String(entry.ContentBase64));
                            break;
                        default:
                            throw new InvalidOperationException(
                                $"Shared package fixture '{fixture.Name}' has unsupported entry kind '{entry.Kind}'.");
                    }
                }

                var package = AssertPairedPackageInputs(
                    repositoryPath,
                    temp.CreateDirectory($"package-scratch-{index:D2}"),
                    fixture.Valid);
                if (fixture.Valid)
                {
                    Assert.NotNull(package);
                    Assert.Equal(
                        fixture.ExpectedModuleFiles,
                        package.Entries.Select(entry => entry.ModuleFile));
                }
            });
        }
    }

    private static CommonModulesPackage? AssertPairedPackageInputs(
        string repositoryPath,
        string scratchRoot,
        bool valid,
        string? expectedFailureMessage = null)
    {
        var reader = new CommonModulesPackageReader(new CommonModulesManifestReader());
        var factory = new CommonModulesPackageSnapshotFactory(
            new WindowsExactFileSystemObjectOwnershipFactory(), reader, scratchRoot);
        CommonModulesPackage? livePackage = null;
        CommonModulesPackageSnapshot? snapshot = null;
        var liveError = Record.Exception(() => livePackage = reader.Load(repositoryPath));
        var capturedError = Record.Exception(() => snapshot = factory.Capture(repositoryPath, CancellationToken.None));
        try
        {
            if (!valid)
            {
                var liveFailure = Assert.IsType<CommonModulesManifestException>(liveError);
                var capturedFailure = Assert.IsType<CommonModulesManifestException>(capturedError);
                Assert.Null(snapshot);
                Assert.Equal(
                    NormalizePackagePathContext(liveFailure.Message, repositoryPath, scratchRoot),
                    NormalizePackagePathContext(capturedFailure.Message, repositoryPath, scratchRoot));
                if (expectedFailureMessage is not null)
                {
                    Assert.Equal(expectedFailureMessage, liveFailure.Message);
                }
                return null;
            }

            Assert.Null(liveError);
            Assert.Null(capturedError);
            Assert.NotNull(livePackage);
            Assert.NotNull(snapshot);
            AssertEquivalentEntries(livePackage.Entries, snapshot.Package.Entries);
            foreach (var entry in livePackage.Entries)
            {
                foreach (var request in new[] { entry.Name, entry.ModuleFile })
                {
                    var livePlan = livePackage.ResolveRequestedPlan([request]);
                    var capturedPlan = snapshot.ResolveRequestedPlan([request]);
                    AssertEquivalentEntries(livePlan.Entries, capturedPlan.Entries);
                    Assert.Equal(livePlan.RequiredReferences, capturedPlan.RequiredReferences);
                    var sources = snapshot.SelectCapturedSources([request]);
                    AssertEquivalentEntries(livePlan.Entries, sources.Units.Select(unit => unit.Entry).ToArray());
                    Assert.Equal(livePlan.RequiredReferences, sources.RequiredReferences);
                    foreach (var unit in sources.Units)
                    {
                        Assert.Equal(
                            File.ReadAllBytes(Path.Combine(repositoryPath, unit.Entry.ModuleFile)),
                            unit.SourceBytes);
                        var sidecarName = unit.Entry.ModuleFile.EndsWith(".frm", StringComparison.Ordinal)
                            ? Path.ChangeExtension(unit.Entry.ModuleFile, ".frx")
                            : null;
                        if (sidecarName is not null && File.Exists(Path.Combine(repositoryPath, sidecarName)))
                        {
                            Assert.Equal(sidecarName, unit.SidecarFileName);
                            Assert.Equal(
                                File.ReadAllBytes(Path.Combine(repositoryPath, sidecarName)),
                                unit.SidecarBytes);
                        }
                        else
                        {
                            Assert.Null(unit.SidecarFileName);
                            Assert.Null(unit.SidecarBytes);
                        }
                    }
                }
            }

            return livePackage;
        }
        finally
        {
            snapshot?.Dispose();
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratchRoot));
        }
    }

    private static void AssertEquivalentEntries(
        IReadOnlyList<CommonModuleManifestEntry> expected,
        IReadOnlyList<CommonModuleManifestEntry> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].ModuleFile, actual[index].ModuleFile);
            Assert.Equal(expected[index].Name, actual[index].Name);
            Assert.Equal(expected[index].InstalledModuleFile, actual[index].InstalledModuleFile);
            Assert.Equal(expected[index].Categories, actual[index].Categories);
            Assert.Equal(expected[index].Dependencies, actual[index].Dependencies);
            Assert.Equal(expected[index].RequiredReferences, actual[index].RequiredReferences);
            Assert.Equal(expected[index].TestOnly, actual[index].TestOnly);
            Assert.Equal(expected[index].RuntimeRole, actual[index].RuntimeRole);
            Assert.Equal(expected[index].TestRole, actual[index].TestRole);
        }
    }

    private static string NormalizePackagePathContext(
        string message,
        string repositoryPath,
        string scratchRoot)
    {
        var separator = Regex.Escape(Path.DirectorySeparatorChar.ToString());
        var capturedRootPattern = Regex.Escape(scratchRoot + Path.DirectorySeparatorChar)
            + "[0-9a-f]{32}(?=" + separator + "|['.]|$)";
        return Regex.Replace(
                message,
                capturedRootPattern,
                "<package>",
                RegexOptions.CultureInvariant)
            .Replace(repositoryPath, "<package>", StringComparison.Ordinal);
    }

    private static void WriteMinimalSource(string repositoryPath, string moduleFile)
    {
        var name = Path.GetFileNameWithoutExtension(moduleFile);
        var kindHeader = Path.GetExtension(moduleFile) switch
        {
            ".bas" => string.Empty,
            ".cls" => "VERSION 1.0 CLASS\r\nBEGIN\r\nEND\r\n",
            ".frm" => "VERSION 5.00\r\n",
            _ => throw new InvalidOperationException($"Unsupported fixture module: {moduleFile}")
        };
        File.WriteAllText(
            Path.Combine(repositoryPath, moduleFile),
            kindHeader + $"Attribute VB_Name = \"{name}\"\r\n",
            Encoding.ASCII);
    }

    private static void RunFixture(string name, Action assertions)
    {
        try
        {
            assertions();
        }
        catch (Exception error)
        {
            throw new Xunit.Sdk.XunitException($"Shared fixture '{name}' failed.\n{error}");
        }
    }

    private static ManifestFixtureSet ReadFixtureSet(byte[] fixtureBytes)
    {
        using var compressed = new MemoryStream(fixtureBytes, writable: false);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return JsonSerializer.Deserialize<ManifestFixtureSet>(
                   reader.ReadToEnd(),
                   new JsonSerializerOptions
                   {
                       PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                   })
               ?? throw new InvalidOperationException("The shared manifest fixture set is empty.");
    }

    private static string FixturePath()
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "fixtures",
            "common-modules-manifest",
            "v1",
            "fixture-set.json.gz"));

    private sealed record ManifestFixtureSet(
        string SchemaVersion,
        ManifestFixtureCase[] Cases,
        PackageFixtureCase[] PackageCases);

    private sealed record ManifestFixtureCase(
        string Name,
        bool Valid,
        string ManifestBase64,
        ManifestFixtureRecord[] ExpectedRecords);

    private sealed record ManifestFixtureRecord(
        string ModuleFile,
        string Categories,
        string[] Dependencies,
        string[] RequiredReferences);

    private sealed record PackageFixtureCase(
        string Name,
        bool Valid,
        PackageFixtureEntry[] Entries,
        string[] ExpectedModuleFiles);

    private sealed record PackageFixtureEntry(
        string Path,
        string Kind,
        string ContentBase64);
}
