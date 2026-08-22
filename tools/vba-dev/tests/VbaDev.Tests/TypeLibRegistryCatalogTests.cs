using VbaTools.TypeLibRegistry;
using Xunit;

namespace VbaDev.Tests;

public sealed class TypeLibRegistryCatalogTests
{
    [Fact]
    public void Registry_catalog_scans_the_merged_root_once_and_builds_hexadecimal_guid_lineages()
    {
        var typeLibRoot = new FakeRegistryKey();
        typeLibRoot.AddPath(
            ["{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}", "9.F"],
            " widget library ");
        typeLibRoot.AddPath(
            ["{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}", "9.F", "409", "win32"],
            @"C:\TypeLib\widget32.dll");
        typeLibRoot.AddPath(
            ["{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}", "9.F", "409", "win64"],
            @"C:\TypeLib\widget64.dll");
        typeLibRoot
            .AddPath(
                ["{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}", "A.10"],
                "WIDGET LIBRARY")
            .AddPath(["409", "win32"], @"C:\TypeLib\widget-new.dll");
        var roots = new FakeTypeLibRegistryRootProvider(typeLibRoot);
        var reader = new RegistryTypeLibRegistryCatalogReader(roots, () => true);

        var catalog = reader.Read();
        var firstLookup = catalog.Find(" widget library ");
        var secondLookup = catalog.Find("WIDGET LIBRARY");

        Assert.True(catalog.Complete);
        Assert.Equal(1, roots.OpenCount);
        Assert.Same(firstLookup, secondLookup);
        var name = Assert.Single(catalog.Names);
        Assert.Equal("WIDGET LIBRARY", name.Name);
        var lineage = Assert.Single(name.Lineages);
        Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", lineage.Guid);
        Assert.Collection(
            lineage.Versions,
            version =>
            {
                Assert.Equal(10, version.Major);
                Assert.Equal(16, version.Minor);
                var locale = Assert.Single(version.Locales);
                Assert.Equal(0x409, locale.Lcid);
                Assert.Equal(["win32"], locale.Paths.Select(path => path.Platform));
            },
            version =>
            {
                Assert.Equal(9, version.Major);
                Assert.Equal(15, version.Minor);
                var locale = Assert.Single(version.Locales);
                Assert.Equal(0x409, locale.Lcid);
                Assert.Equal(
                    ["win32", "win64"],
                    locale.Paths.Select(path => path.Platform));
            });
    }

    [Fact]
    public void Malformed_individual_registrations_keep_readable_names_and_aggregate_one_warning()
    {
        var typeLibRoot = new FakeRegistryKey();
        typeLibRoot
            .AddPath(["not-a-guid", "1.0"], "Broken Library")
            .AddPath(["409", "win32"], @"C:\TypeLib\broken.dll");
        typeLibRoot
            .AddPath(
                ["{BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB}", "10000.0"],
                "broken library")
            .AddPath(["409", "win64"], @"C:\TypeLib\overflow.dll");
        typeLibRoot
            .AddPath(["{CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC}", "1.0"], "   ")
            .AddPath(["409", "win32"], @"C:\TypeLib\nameless.dll");
        var reader = new RegistryTypeLibRegistryCatalogReader(
            new FakeTypeLibRegistryRootProvider(typeLibRoot),
            () => true);

        var catalog = reader.Read();

        Assert.True(catalog.Complete);
        var name = Assert.Single(catalog.Names);
        Assert.Equal("Broken Library", name.Name);
        Assert.Empty(name.Lineages);
        var warning = Assert.Single(catalog.Warnings);
        Assert.Equal("malformedRegistrationsSkipped", warning.Code);
        Assert.Equal(3, warning.Count);
    }

    [Fact]
    public void Catalog_level_enumeration_failure_is_incomplete_and_never_becomes_an_empty_catalog()
    {
        var roots = new FakeTypeLibRegistryRootProvider(
            new FakeRegistryKey { EnumerationException = new UnauthorizedAccessException("denied") });
        var reader = new RegistryTypeLibRegistryCatalogReader(roots, () => true);

        var catalog = reader.Read();

        Assert.False(catalog.Complete);
        Assert.Empty(catalog.Names);
        Assert.Equal("registryCatalogIncomplete", Assert.IsType<TypeLibRegistryCatalogDiagnostic>(catalog.Diagnostic).Code);
    }

    [Fact]
    public void Missing_enumerated_guid_key_fails_the_catalog_closed()
    {
        var typeLibRoot = new FakeRegistryKey();
        typeLibRoot.AddPath(
            ["{BCBCBCBC-BCBC-BCBC-BCBC-BCBCBCBCBCBC}", "1.0"],
            "Hidden Library");
        typeLibRoot.ReturnNullOnOpen = true;
        var reader = new RegistryTypeLibRegistryCatalogReader(
            new FakeTypeLibRegistryRootProvider(typeLibRoot),
            () => true);

        var catalog = reader.Read();

        Assert.False(catalog.Complete);
        Assert.Empty(catalog.Names);
        Assert.Equal(
            "registryCatalogIncomplete",
            Assert.IsType<TypeLibRegistryCatalogDiagnostic>(catalog.Diagnostic).Code);
    }

    [Fact]
    public void Unreadable_enumerated_version_is_skipped_as_one_malformed_registration()
    {
        var typeLibRoot = new FakeRegistryKey();
        var guidKey = typeLibRoot.AddPath(
            ["{CDCDCDCD-CDCD-CDCD-CDCD-CDCDCDCDCDCD}"]);
        guidKey.AddPath(["1.0"], "Unreadable Library");
        guidKey.OpenSubKeyException = new UnauthorizedAccessException("denied");
        var reader = new RegistryTypeLibRegistryCatalogReader(
            new FakeTypeLibRegistryRootProvider(typeLibRoot),
            () => true);

        var catalog = reader.Read();

        Assert.True(catalog.Complete);
        Assert.Empty(catalog.Names);
        var warning = Assert.Single(catalog.Warnings);
        Assert.Equal("malformedRegistrationsSkipped", warning.Code);
        Assert.Equal(1, warning.Count);
    }

    [Fact]
    public void Unreadable_identity_metadata_keeps_its_readable_name_and_is_aggregated_as_malformed()
    {
        var typeLibRoot = new FakeRegistryKey();
        var versionKey = typeLibRoot.AddPath(
            ["{DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD}", "1.0"],
            "Readable Library");
        versionKey.EnumerationException = new UnauthorizedAccessException("denied");
        var reader = new RegistryTypeLibRegistryCatalogReader(
            new FakeTypeLibRegistryRootProvider(typeLibRoot),
            () => true);

        var catalog = reader.Read();

        Assert.True(catalog.Complete);
        var name = Assert.Single(catalog.Names);
        Assert.Equal("Readable Library", name.Name);
        Assert.Empty(name.Lineages);
        var warning = Assert.Single(catalog.Warnings);
        Assert.Equal("malformedRegistrationsSkipped", warning.Code);
        Assert.Equal(1, warning.Count);
    }

    [Fact]
    public void Unreadable_locale_metadata_is_an_individual_malformed_registration()
    {
        var typeLibRoot = new FakeRegistryKey();
        var localeKey = typeLibRoot.AddPath(
            ["{ABABABAB-ABAB-ABAB-ABAB-ABABABABABAB}", "1.0", "409"]);
        typeLibRoot.AddPath(
            ["{ABABABAB-ABAB-ABAB-ABAB-ABABABABABAB}", "1.0"],
            "Locale Library");
        localeKey.EnumerationException = new UnauthorizedAccessException("denied");
        var reader = new RegistryTypeLibRegistryCatalogReader(
            new FakeTypeLibRegistryRootProvider(typeLibRoot),
            () => true);

        var catalog = reader.Read();

        Assert.True(catalog.Complete);
        Assert.Empty(Assert.Single(catalog.Names).Lineages);
        Assert.Equal(1, Assert.Single(catalog.Warnings).Count);
    }

    [Fact]
    public void Unknown_nonhex_identity_metadata_warns_while_known_metadata_is_ignored()
    {
        var typeLibRoot = new FakeRegistryKey();
        var versionKey = typeLibRoot.AddPath(
            ["{EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE}", "1.0"],
            "Metadata Library");
        versionKey.AddPath(["409", "win32"], @"C:\TypeLib\metadata.dll");
        versionKey.AddPath(["FLAGS"], "1");
        versionKey.AddPath(["HELPDIR"], @"C:\TypeLib");
        versionKey.AddPath(["not-an-lcid"], "invalid");
        var reader = new RegistryTypeLibRegistryCatalogReader(
            new FakeTypeLibRegistryRootProvider(typeLibRoot),
            () => true);

        var catalog = reader.Read();

        Assert.True(catalog.Complete);
        Assert.Single(Assert.Single(catalog.Names).Lineages);
        var warning = Assert.Single(catalog.Warnings);
        Assert.Equal("malformedRegistrationsSkipped", warning.Code);
        Assert.Equal(1, warning.Count);
    }

    private sealed class FakeTypeLibRegistryRootProvider(FakeRegistryKey root)
        : ITypeLibRegistryRootProvider
    {
        public int OpenCount { get; private set; }

        public ITypeLibRegistryKey? OpenTypeLibRoot()
        {
            OpenCount++;
            return root;
        }
    }

    private sealed class FakeRegistryKey : ITypeLibRegistryKey
    {
        private readonly Dictionary<string, FakeRegistryKey> children =
            new(StringComparer.OrdinalIgnoreCase);

        public object? DefaultValue { get; private set; }

        public Exception? EnumerationException { get; set; }

        public Exception? OpenSubKeyException { get; set; }

        public bool ReturnNullOnOpen { get; set; }

        public FakeRegistryKey AddPath(IReadOnlyList<string> names, object? defaultValue = null)
        {
            var current = this;
            foreach (var name in names)
            {
                if (!current.children.TryGetValue(name, out var child))
                {
                    child = new FakeRegistryKey();
                    current.children.Add(name, child);
                }

                current = child;
            }

            current.DefaultValue = defaultValue;
            return current;
        }

        public IReadOnlyList<string> GetSubKeyNames()
        {
            if (EnumerationException is not null)
            {
                throw EnumerationException;
            }

            return children.Keys.ToArray();
        }

        public ITypeLibRegistryKey? OpenSubKey(string name)
        {
            if (OpenSubKeyException is not null)
            {
                throw OpenSubKeyException;
            }

            if (ReturnNullOnOpen)
            {
                return null;
            }

            return children.GetValueOrDefault(name);
        }

        public object? GetDefaultValue() => DefaultValue;

        public void Dispose()
        {
        }
    }
}
