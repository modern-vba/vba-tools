using VbaDev.Infrastructure.References;
using VbaDev.App.References;
using Xunit;

namespace VbaDev.Tests;

public sealed class OfficeClickToRunTypeLibEvidenceReaderTests
{
    [Fact]
    public void SnapshotDeeplyCapturesMutableEvidenceInputs()
    {
        var registrations = new List<OfficeClickToRunTypeLibRegistrationEvidence>
        {
            new("Library", "831fdd16-0c5c-11d2-a9fc-0000f8754da1", 2, 2, 0, "win64",
                "C:/Windows/System32/MSCOMCTL.OCX")
        };
        var installations = new List<OfficeClickToRunInstallationEvidence>
        {
            new("C:/Program Files/Microsoft Office", "x64", registrations)
        };
        var snapshot = new OfficeClickToRunTypeLibEvidenceSnapshot(true, installations, null);

        registrations.Clear();
        installations.Clear();

        Assert.Single(Assert.Single(snapshot.Installations).Registrations);
    }

    [Fact]
    public void ReaderCapturesOnlyTheScopedConfigurationAndVirtualTypeLibRegistration()
    {
        const string installationPath = "C:/Program Files/Microsoft Office";
        const string guid = "{831FDD16-0C5C-11D2-A9FC-0000F8754DA1}";
        const string virtualPath = "C:/Windows/system32/MSCOMCTL.OCX";
        var root = new RegistryKeyNode()
            .Add("Configuration", new RegistryKeyNode()
                .Value("InstallationPath", installationPath)
                .Value("Platform", "x64"))
            .Add("REGISTRY", new RegistryKeyNode()
                .Add("MACHINE", new RegistryKeyNode()
                    .Add("Software", new RegistryKeyNode()
                        .Add("Classes", new RegistryKeyNode()
                            .Add("TypeLib", new RegistryKeyNode()
                                .Add(guid, new RegistryKeyNode()
                                    .Add("2.2", new RegistryKeyNode()
                                        .Value(null, "Microsoft Windows Common Controls 6.0 (SP6)")
                                        .Add("0", new RegistryKeyNode()
                                            .Add("win64", new RegistryKeyNode()
                                                .Value(null, virtualPath))))))))));
        var provider = new RegistryRootProvider(root);
        var reader = new RegistryOfficeClickToRunTypeLibEvidenceReader(provider, () => true);

        var snapshot = reader.Read();

        Assert.True(snapshot.Complete, snapshot.Diagnostic);
        Assert.Null(snapshot.Diagnostic);
        var installation = Assert.Single(snapshot.Installations);
        Assert.Equal(installationPath, installation.InstallationPath);
        Assert.Equal("x64", installation.Platform);
        var registration = Assert.Single(installation.Registrations);
        Assert.Equal("Microsoft Windows Common Controls 6.0 (SP6)", registration.ReferenceName);
        Assert.Equal("831fdd16-0c5c-11d2-a9fc-0000f8754da1", registration.Guid);
        Assert.Equal(2, registration.Major);
        Assert.Equal(2, registration.Minor);
        Assert.Equal(0, registration.Lcid);
        Assert.Equal("win64", registration.Platform);
        Assert.Equal(virtualPath, registration.VirtualPath);
        Assert.Equal(1, provider.Reads);
        Assert.All(root.DescendantsAndSelf, key => Assert.Equal(1, key.DisposeCalls));
    }

    [Fact]
    public void X64ConfigurationDoesNotFlattenWow6432NodeTypeLibEvidence()
    {
        var misplaced = new RegistryKeyNode()
            .Add("{831FDD16-0C5C-11D2-A9FC-0000F8754DA1}", new RegistryKeyNode()
                .Add("2.2", new RegistryKeyNode()
                    .Value(null, "Microsoft Windows Common Controls 6.0 (SP6)")
                    .Add("0", new RegistryKeyNode()
                        .Add("win64", new RegistryKeyNode()
                            .Value(null, "C:/Windows/system32/MSCOMCTL.OCX")))));
        var root = new RegistryKeyNode()
            .Add("Configuration", new RegistryKeyNode()
                .Value("InstallationPath", "C:/Program Files/Microsoft Office")
                .Value("Platform", "x64"))
            .Add("REGISTRY", new RegistryKeyNode()
                .Add("MACHINE", new RegistryKeyNode()
                    .Add("Software", new RegistryKeyNode()
                        .Add("Classes", new RegistryKeyNode()
                            .Add("TypeLib", new RegistryKeyNode())
                            .Add("Wow6432Node", new RegistryKeyNode()
                                .Add("TypeLib", misplaced))))));
        var reader = new RegistryOfficeClickToRunTypeLibEvidenceReader(new RegistryRootProvider(root), () => true);

        var snapshot = reader.Read();

        Assert.True(snapshot.Complete, snapshot.Diagnostic);
        Assert.Empty(Assert.Single(snapshot.Installations).Registrations);
    }

    [Fact]
    public void X86ConfigurationOnX64CapturesOnlyWow6432NodeTypeLibEvidence()
    {
        var applicable = TypeLibWithRegistration(platform: "win32");
        var misplaced = TypeLibWithRegistration(platform: "win64");
        var root = CreateRoot(
            new RegistryKeyNode()
                .Value("InstallationPath", "C:/Program Files (x86)/Microsoft Office")
                .Value("Platform", "x86"),
            nativeTypeLib: misplaced,
            wow6432NodeTypeLib: applicable);
        var reader = new RegistryOfficeClickToRunTypeLibEvidenceReader(
            new RegistryRootProvider(root), () => true, () => true);

        var snapshot = reader.Read();

        Assert.True(snapshot.Complete, snapshot.Diagnostic);
        var registration = Assert.Single(Assert.Single(snapshot.Installations).Registrations);
        Assert.Equal("win32", registration.Platform);
        Assert.Equal(0, misplaced.DisposeCalls);
        Assert.All(applicable.DescendantsAndSelf, key => Assert.Equal(1, key.DisposeCalls));
    }

    [Fact]
    public void X86ConfigurationOnX86CapturesOnlyNativeTypeLibEvidence()
    {
        var applicable = TypeLibWithRegistration(platform: "win32");
        var misplaced = TypeLibWithRegistration(platform: "win64");
        var root = CreateRoot(
            new RegistryKeyNode()
                .Value("InstallationPath", "C:/Program Files/Microsoft Office")
                .Value("Platform", "x86"),
            nativeTypeLib: applicable,
            wow6432NodeTypeLib: misplaced);
        var reader = new RegistryOfficeClickToRunTypeLibEvidenceReader(
            new RegistryRootProvider(root), () => true, () => false);

        var snapshot = reader.Read();

        Assert.True(snapshot.Complete, snapshot.Diagnostic);
        var registration = Assert.Single(Assert.Single(snapshot.Installations).Registrations);
        Assert.Equal("win32", registration.Platform);
        Assert.Equal(0, misplaced.DisposeCalls);
        Assert.All(applicable.DescendantsAndSelf, key => Assert.Equal(1, key.DisposeCalls));
    }

    [Theory]
    [InlineData("missing-configuration")]
    [InlineData("missing-installation")]
    [InlineData("missing-platform")]
    [InlineData("unsupported-platform")]
    [InlineData("missing-typelib")]
    [InlineData("malformed-guid")]
    [InlineData("malformed-version")]
    [InlineData("malformed-lcid")]
    [InlineData("missing-path")]
    [InlineData("access-denied-subkey-names")]
    [InlineData("access-denied-value")]
    [InlineData("access-denied-open-subkey")]
    public void IncompleteOrMalformedRegistryEvidenceFailsClosedAndDisposesEveryOpenedKey(string scenario)
    {
        var configuration = new RegistryKeyNode()
            .Value("InstallationPath", scenario == "missing-installation" ? null : "C:/Program Files/Microsoft Office")
            .Value("Platform", scenario switch
            {
                "missing-platform" => null,
                "unsupported-platform" => "arm64",
                _ => "x64"
            });
        var typeLib = TypeLibWithRegistration(
            guid: scenario == "malformed-guid" ? "not-a-guid" : "{831FDD16-0C5C-11D2-A9FC-0000F8754DA1}",
            version: scenario == "malformed-version" ? "not-a-version" : "2.2",
            lcid: scenario == "malformed-lcid" ? "not-an-lcid" : "0",
            path: scenario == "missing-path" ? null : "C:/Windows/system32/MSCOMCTL.OCX");
        if (scenario == "access-denied-subkey-names")
        {
            typeLib.FailSubKeyNames(new UnauthorizedAccessException("Injected registry access denial."));
        }
        RegistryKeyNode root;
        if (scenario == "missing-configuration")
        {
            root = new RegistryKeyNode();
        }
        else
        {
            root = CreateRoot(configuration, scenario == "missing-typelib" ? null : typeLib);
        }
        if (scenario == "access-denied-value")
        {
            configuration.FailGetValue(new UnauthorizedAccessException("Injected registry value access denial."));
        }
        if (scenario == "access-denied-open-subkey")
        {
            root.FailOpenSubKey(new UnauthorizedAccessException("Injected registry subkey access denial."));
        }
        var reader = new RegistryOfficeClickToRunTypeLibEvidenceReader(
            new RegistryRootProvider(root), () => true, () => true);

        var snapshot = reader.Read();

        Assert.False(snapshot.Complete);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Diagnostic));
        Assert.Equal(1, root.DisposeCalls);
        Assert.All(root.DescendantsAndSelf.Where(key => key.DisposeCalls > 0),
            key => Assert.Equal(1, key.DisposeCalls));
    }

    [Fact]
    public void AbsentClickToRunRootIsAnAvailableEmptySnapshot()
    {
        var reader = new RegistryOfficeClickToRunTypeLibEvidenceReader(
            new RegistryRootProvider(null), () => true, () => true);

        var snapshot = reader.Read();

        Assert.True(snapshot.Complete);
        Assert.Empty(snapshot.Installations);
        Assert.Null(snapshot.Diagnostic);
    }

    private static RegistryKeyNode CreateRoot(
        RegistryKeyNode configuration,
        RegistryKeyNode? nativeTypeLib = null,
        RegistryKeyNode? wow6432NodeTypeLib = null)
    {
        var classes = new RegistryKeyNode();
        if (nativeTypeLib is not null) classes.Add("TypeLib", nativeTypeLib);
        if (wow6432NodeTypeLib is not null)
            classes.Add("Wow6432Node", new RegistryKeyNode().Add("TypeLib", wow6432NodeTypeLib));
        return new RegistryKeyNode()
            .Add("Configuration", configuration)
            .Add("REGISTRY", new RegistryKeyNode()
                .Add("MACHINE", new RegistryKeyNode()
                    .Add("Software", new RegistryKeyNode()
                        .Add("Classes", classes))));
    }

    private static RegistryKeyNode TypeLibWithRegistration(
        string guid = "{831FDD16-0C5C-11D2-A9FC-0000F8754DA1}",
        string version = "2.2",
        string lcid = "0",
        string platform = "win64",
        string? path = "C:/Windows/system32/MSCOMCTL.OCX")
        => new RegistryKeyNode()
            .Add(guid, new RegistryKeyNode()
                .Add(version, new RegistryKeyNode()
                    .Value(null, "Microsoft Windows Common Controls 6.0 (SP6)")
                    .Add(lcid, new RegistryKeyNode()
                        .Add(platform, new RegistryKeyNode().Value(null, path)))));

    private sealed class RegistryRootProvider(IOfficeClickToRunRegistryKey? root)
        : IOfficeClickToRunRegistryRootProvider
    {
        internal int Reads { get; private set; }

        public IOfficeClickToRunRegistryKey? OpenRoot()
        {
            Reads++;
            return root;
        }
    }

    private sealed class RegistryKeyNode : IOfficeClickToRunRegistryKey
    {
        private readonly Dictionary<string, RegistryKeyNode> children = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, object?> values = new(StringComparer.OrdinalIgnoreCase);
        private Exception? subKeyNamesError;
        private Exception? getValueError;
        private Exception? openSubKeyError;

        internal int DisposeCalls { get; private set; }

        internal IEnumerable<RegistryKeyNode> DescendantsAndSelf
            => new[] { this }.Concat(children.Values.SelectMany(child => child.DescendantsAndSelf));

        internal RegistryKeyNode Add(string name, RegistryKeyNode child)
        {
            children.Add(name, child);
            return this;
        }

        internal RegistryKeyNode Value(string? name, object? value)
        {
            values[name ?? string.Empty] = value;
            return this;
        }

        internal RegistryKeyNode FailSubKeyNames(Exception error)
        {
            subKeyNamesError = error;
            return this;
        }

        internal RegistryKeyNode FailGetValue(Exception error)
        {
            getValueError = error;
            return this;
        }

        internal RegistryKeyNode FailOpenSubKey(Exception error)
        {
            openSubKeyError = error;
            return this;
        }

        public IReadOnlyList<string> GetSubKeyNames()
        {
            if (subKeyNamesError is not null) throw subKeyNamesError;
            return children.Keys.ToArray();
        }

        public object? GetValue(string? name)
        {
            if (getValueError is not null) throw getValueError;
            return values.GetValueOrDefault(name ?? string.Empty);
        }

        public IOfficeClickToRunRegistryKey? OpenSubKey(string name)
        {
            if (openSubKeyError is not null) throw openSubKeyError;
            return children.GetValueOrDefault(name);
        }

        public void Dispose()
        {
            DisposeCalls++;
        }
    }
}
