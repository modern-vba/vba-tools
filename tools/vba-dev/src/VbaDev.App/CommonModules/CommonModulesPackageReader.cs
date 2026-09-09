using System.Text;
using System.Text.RegularExpressions;
using VbaTools.Syntax;

namespace VbaDev.App.CommonModules;

/// <summary>
/// Validates the closed, flat CommonModules package boundary before installation planning.
/// </summary>
public sealed class CommonModulesPackageReader
{
    private static readonly Lazy<Encoding> CanonicalSourceEncoding = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(
            932,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    });
    private static readonly Regex CanonicalSourceIdentityPattern = new(
        @"^\p{L}[\p{L}\p{Nd}_]*$",
        RegexOptions.CultureInvariant);

    private readonly CommonModulesManifestReader manifestReader;

    /// <summary>
    /// Creates a package reader backed by the canonical manifest reader.
    /// </summary>
    public CommonModulesPackageReader(CommonModulesManifestReader manifestReader)
    {
        this.manifestReader = manifestReader
            ?? throw new ArgumentNullException(nameof(manifestReader));
    }

    /// <summary>
    /// Reads and completely validates one canonical package root.
    /// </summary>
    public CommonModulesPackage Load(string commonModulesRepositoryPath)
        => CommonModulesPackage.AdmitLive(this, commonModulesRepositoryPath);

    internal IReadOnlyList<CommonModuleManifestEntry> ReadValidatedLiveEntries(
        string commonModulesRepositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonModulesRepositoryPath);
        return ReadValidatedEntries(new LivePackageInput(commonModulesRepositoryPath));
    }

    internal CommonModulesPackage LoadCaptured(
        string displayRootPath,
        IReadOnlyDictionary<string, byte[]> capturedFiles)
        => CommonModulesPackage.AdmitCaptured(this, displayRootPath, capturedFiles);

    internal IReadOnlyList<CommonModuleManifestEntry> ReadValidatedCapturedEntries(
        string displayRootPath,
        IReadOnlyDictionary<string, byte[]> capturedFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayRootPath);
        ArgumentNullException.ThrowIfNull(capturedFiles);
        return ReadValidatedEntries(new CapturedPackageInput(displayRootPath, capturedFiles));
    }

    private IReadOnlyList<CommonModuleManifestEntry> ReadValidatedEntries(PackageInput input)
    {
        RequireExactFile(input, CommonModulesManifestReader.ManifestFileName);
        var manifestEntries = manifestReader.LoadCaptured(
            input.ReadBytes(CommonModulesManifestReader.ManifestFileName));
        var expectedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CommonModulesManifestReader.ManifestFileName] = CommonModulesManifestReader.ManifestFileName
        };
        var commonNames = new Dictionary<string, CommonModuleManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifestEntry in manifestEntries)
        {
            if (!commonNames.TryAdd(manifestEntry.Name, manifestEntry))
            {
                var prior = commonNames[manifestEntry.Name];
                throw new CommonModulesManifestException(
                    $"CommonModules package contains duplicate CommonModuleName '{manifestEntry.Name}': "
                    + $"'{prior.ModuleFile}' and '{manifestEntry.ModuleFile}'.");
            }

            expectedNames.Add(manifestEntry.ModuleFile, manifestEntry.ModuleFile);
            if (manifestEntry.ModuleFile.EndsWith(".frm", StringComparison.Ordinal))
            {
                var sidecarName = Path.ChangeExtension(manifestEntry.ModuleFile, ".frx");
                if (input.ActualNames.ContainsKey(sidecarName))
                {
                    expectedNames.Add(sidecarName, sidecarName);
                }
            }
        }

        foreach (var expectedName in expectedNames.Values)
        {
            RequireExactFile(input, expectedName);
        }

        foreach (var manifestEntry in manifestEntries)
        {
            ValidateSourceMetadata(
                manifestEntry,
                input.ReadBytes(manifestEntry.ModuleFile),
                input.GetFilePath(manifestEntry.ModuleFile));
        }

        foreach (var actualName in input.ActualNames.Values.Order(StringComparer.Ordinal))
        {
            if (!expectedNames.ContainsKey(actualName))
            {
                throw new CommonModulesManifestException(
                    $"CommonModules package contains unexpected package entry '{actualName}'.");
            }
        }

        return manifestEntries;
    }

    private static void ValidateSourceMetadata(
        CommonModuleManifestEntry manifestEntry,
        byte[] sourceBytes,
        string sourcePath)
    {
        if (!VbaIdentifier.IsIdentifier(manifestEntry.Name)
            || !CanonicalSourceIdentityPattern.IsMatch(manifestEntry.Name)
            || manifestEntry.Name.EnumerateRunes().Count() > 31)
        {
            throw new CommonModulesManifestException(
                $"CommonModules ModuleIdentity '{manifestEntry.Name}' is invalid.");
        }

        string sourceText;
        try
        {
            sourceText = CanonicalSourceEncoding.Value.GetString(sourceBytes);
            if (!sourceBytes.AsSpan().SequenceEqual(
                    CanonicalSourceEncoding.Value.GetBytes(sourceText)))
            {
                throw new InvalidOperationException(
                    $"CommonModules source '{sourcePath}' cannot reproduce its canonical Windows-932 bytes.");
            }
        }
        catch (Exception ex) when (ex is DecoderFallbackException
                                   or EncoderFallbackException
                                   or InvalidOperationException)
        {
            throw new CommonModulesManifestException(
                $"CommonModules source '{sourcePath}' must use strict Windows-932 text. {ex.Message}");
        }

        var extension = Path.GetExtension(manifestEntry.ModuleFile);
        var expectedKind = extension switch
        {
            ".bas" => CanonicalSourceKind.StandardModule,
            ".cls" => CanonicalSourceKind.ClassModule,
            ".frm" => CanonicalSourceKind.FormModule,
            _ => throw new CommonModulesManifestException(
                $"CommonModules source kind is unsupported: {manifestEntry.ModuleFile}")
        };
        var actualKind = ReadSourceKind(sourceText);
        if (actualKind != expectedKind)
        {
            throw new CommonModulesManifestException(
                $"CommonModules source '{manifestEntry.ModuleFile}' declares source kind '{actualKind}' "
                + $"instead of '{expectedKind}'.");
        }

        var metadata = VbaModuleIdentityMetadataReader.Read(
            sourceText,
            expectedKind == CanonicalSourceKind.StandardModule
                ? VbaModuleIdentitySourceKind.StandardModule
                : VbaModuleIdentitySourceKind.ObjectModule);
        if (!metadata.IsAuthoritative)
        {
            throw new CommonModulesManifestException(
                $"CommonModules source '{manifestEntry.ModuleFile}' has invalid ModuleIdentity metadata: "
                + metadata.Failure);
        }

        if (metadata.Records.Count != 1)
        {
            throw new CommonModulesManifestException(
                $"CommonModules source '{manifestEntry.ModuleFile}' has invalid ModuleIdentity metadata: "
                + "contains duplicate ModuleIdentity metadata.");
        }

        if (!metadata.Name!.Equals(manifestEntry.Name, StringComparison.Ordinal))
        {
            throw new CommonModulesManifestException(
                $"CommonModules source '{manifestEntry.ModuleFile}' declares ModuleIdentity "
                + $"'{metadata.Name}' instead of exact manifest identity '{manifestEntry.Name}'.");
        }
    }

    private static CanonicalSourceKind ReadSourceKind(string sourceText)
    {
        using var reader = new StringReader(sourceText);
        string? line;
        do
        {
            line = reader.ReadLine();
        }
        while (line is not null && VbaIdentifier.IsWhitespaceOnly(line));

        var firstLine = line is null ? string.Empty : VbaIdentifier.TrimWhitespace(line);
        if (firstLine.Equals("VERSION 1.0 CLASS", StringComparison.OrdinalIgnoreCase))
        {
            return CanonicalSourceKind.ClassModule;
        }

        if (firstLine.Equals("VERSION 5.00", StringComparison.OrdinalIgnoreCase))
        {
            return CanonicalSourceKind.FormModule;
        }

        return CanonicalSourceKind.StandardModule;
    }

    private static void RequireExactFile(PackageInput input, string expectedName)
    {
        if (!input.ActualNames.TryGetValue(expectedName, out var actualName))
        {
            throw new CommonModulesManifestException(
                $"CommonModules package source file was not found: {Path.Combine(input.DisplayRootPath, expectedName)}");
        }

        if (!actualName.Equals(expectedName, StringComparison.Ordinal))
        {
            throw new CommonModulesManifestException(
                $"CommonModules package entry '{actualName}' must use exact spelling '{expectedName}'.");
        }

        input.EnsureReadable(actualName);
    }

    private abstract class PackageInput
    {
        protected PackageInput(string displayRootPath, IEnumerable<string> actualNames)
        {
            DisplayRootPath = displayRootPath;
            ActualNames = actualNames.ToDictionary(
                name => name,
                name => name,
                StringComparer.OrdinalIgnoreCase);
        }

        public string DisplayRootPath { get; }

        public IReadOnlyDictionary<string, string> ActualNames { get; }

        public virtual string GetFilePath(string fileName)
            => Path.Combine(DisplayRootPath, fileName);

        public abstract void EnsureReadable(string fileName);

        public abstract byte[] ReadBytes(string fileName);
    }

    private sealed class LivePackageInput : PackageInput
    {
        private readonly string absoluteRepositoryPath;

        public LivePackageInput(string repositoryPath)
            : base(
                repositoryPath,
                CommonModulesPackageInventory.ReadLive(repositoryPath).Select(entry => entry.Name))
        {
            absoluteRepositoryPath = Path.GetFullPath(repositoryPath);
        }

        public override string GetFilePath(string fileName)
            => Path.Combine(absoluteRepositoryPath, fileName);

        public override void EnsureReadable(string fileName)
        {
            var path = GetFilePath(fileName);
            try
            {
                using var stream = File.OpenRead(path);
                stream.CopyTo(Stream.Null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw ReadFailure(path);
            }
        }

        public override byte[] ReadBytes(string fileName)
        {
            var path = GetFilePath(fileName);
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw ReadFailure(path);
            }
        }

        private static CommonModulesManifestException ReadFailure(string path)
            => new($"CommonModules package entry could not be read: {path}");
    }

    private sealed class CapturedPackageInput : PackageInput
    {
        private readonly IReadOnlyDictionary<string, byte[]> capturedFiles;

        public CapturedPackageInput(
            string displayRootPath,
            IReadOnlyDictionary<string, byte[]> capturedFiles)
            : base(
                displayRootPath,
                CommonModulesPackageInventory.NormalizeCapturedNames(capturedFiles.Keys))
        {
            this.capturedFiles = capturedFiles;
        }

        public override void EnsureReadable(string fileName)
        {
            // Snapshot capture has already obtained these bytes; never reopen its display path.
        }

        public override byte[] ReadBytes(string fileName)
            => capturedFiles[fileName];
    }

    private enum CanonicalSourceKind
    {
        StandardModule,
        ClassModule,
        FormModule
    }
}
