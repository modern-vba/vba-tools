using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VbaDev.App.CommonModules;
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
            var repositoryPath = temp.CreateDirectory($"case-{index:D2}");
            File.WriteAllBytes(
                Path.Combine(repositoryPath, CommonModulesManifestReader.ManifestFileName),
                Convert.FromBase64String(fixture.ManifestBase64));

            if (!fixture.Valid)
            {
                var error = Record.Exception(() => reader.Load(repositoryPath));
                Assert.IsType<CommonModulesManifestException>(error);
                continue;
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
            }
        }

        var packageReader = new CommonModulesPackageReader(new CommonModulesManifestReader());
        for (var index = 0; index < fixtureSet.PackageCases.Length; index++)
        {
            var fixture = fixtureSet.PackageCases[index];
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

            var error = Record.Exception(() => packageReader.Load(repositoryPath));
            if (!fixture.Valid)
            {
                Assert.IsType<CommonModulesManifestException>(error);
                continue;
            }

            Assert.Null(error);
            var package = packageReader.Load(repositoryPath);
            Assert.Equal(
                fixture.ExpectedModuleFiles,
                package.Entries.Select(entry => entry.ModuleFile));
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
