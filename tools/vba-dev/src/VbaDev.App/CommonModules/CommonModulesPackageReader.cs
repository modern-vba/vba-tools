using System.Text;
using System.Text.RegularExpressions;
using VbaLanguageServer.Syntax;

namespace VbaDev.App.CommonModules;

/// <summary>
/// Represents one completely validated canonical CommonModules package.
/// </summary>
/// <param name="Entries">The manifest entries in canonical declaration order.</param>
public sealed record CommonModulesPackage(
    IReadOnlyList<CommonModuleManifestEntry> Entries);

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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonModulesRepositoryPath);
        var repository = new DirectoryInfo(commonModulesRepositoryPath);
        if (!repository.Exists)
        {
            throw new CommonModulesManifestException(
                $"CommonModulesRepository was not found: {commonModulesRepositoryPath}");
        }

        if (repository.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new CommonModulesManifestException(
                $"CommonModules package root must be an ordinary directory: {commonModulesRepositoryPath}");
        }

        var actualEntries = ReadFlatInventory(repository);
        RequireExactOrdinaryFile(
            actualEntries,
            CommonModulesManifestReader.ManifestFileName,
            commonModulesRepositoryPath);

        var manifestEntries = manifestReader.Load(commonModulesRepositoryPath);
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
                if (actualEntries.ContainsKey(sidecarName))
                {
                    expectedNames.Add(sidecarName, sidecarName);
                }
            }
        }

        foreach (var expectedName in expectedNames.Values)
        {
            RequireExactOrdinaryFile(
                actualEntries,
                expectedName,
                commonModulesRepositoryPath);
        }

        foreach (var manifestEntry in manifestEntries)
        {
            ValidateSourceMetadata(
                manifestEntry,
                (FileInfo)actualEntries[manifestEntry.ModuleFile]);
        }

        foreach (var actualEntry in actualEntries.Values)
        {
            if (!expectedNames.ContainsKey(actualEntry.Name))
            {
                throw new CommonModulesManifestException(
                    $"CommonModules package contains unexpected package entry '{actualEntry.Name}'.");
            }
        }

        return new CommonModulesPackage(manifestEntries);
    }

    private static void ValidateSourceMetadata(
        CommonModuleManifestEntry manifestEntry,
        FileInfo sourceFile)
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
            var sourceBytes = File.ReadAllBytes(sourceFile.FullName);
            sourceText = CanonicalSourceEncoding.Value.GetString(sourceBytes);
            if (!sourceBytes.AsSpan().SequenceEqual(
                    CanonicalSourceEncoding.Value.GetBytes(sourceText)))
            {
                throw new InvalidOperationException(
                    $"CommonModules source '{sourceFile.FullName}' cannot reproduce its canonical Windows-932 bytes.");
            }
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or DecoderFallbackException
                                   or EncoderFallbackException
                                   or InvalidOperationException)
        {
            throw new CommonModulesManifestException(
                $"CommonModules source '{sourceFile.FullName}' must use strict Windows-932 text. {ex.Message}");
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

    private static IReadOnlyDictionary<string, FileSystemInfo> ReadFlatInventory(
        DirectoryInfo repository)
    {
        try
        {
            var inventory = new Dictionary<string, FileSystemInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in repository.EnumerateFileSystemInfos(
                "*",
                new EnumerationOptions
                {
                    AttributesToSkip = 0,
                    IgnoreInaccessible = false,
                    RecurseSubdirectories = false,
                    ReturnSpecialDirectories = false
                }))
            {
                if (!inventory.TryAdd(entry.Name, entry))
                {
                    throw new CommonModulesManifestException(
                        $"CommonModules package contains case-insensitive duplicate entry '{entry.Name}'.");
                }
            }

            return inventory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CommonModulesManifestException(
                $"CommonModules package inventory could not be read: {repository.FullName}");
        }
    }

    private static void RequireExactOrdinaryFile(
        IReadOnlyDictionary<string, FileSystemInfo> inventory,
        string expectedName,
        string repositoryPath)
    {
        if (!inventory.TryGetValue(expectedName, out var entry))
        {
            throw new CommonModulesManifestException(
                $"CommonModules package source file was not found: {Path.Combine(repositoryPath, expectedName)}");
        }

        if (!entry.Name.Equals(expectedName, StringComparison.Ordinal))
        {
            throw new CommonModulesManifestException(
                $"CommonModules package entry '{entry.Name}' must use exact spelling '{expectedName}'.");
        }

        if (entry is not FileInfo || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new CommonModulesManifestException(
                $"CommonModules package entry must be an ordinary file: {entry.FullName}");
        }

        try
        {
            using var stream = File.OpenRead(entry.FullName);
            stream.CopyTo(Stream.Null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CommonModulesManifestException(
                $"CommonModules package entry could not be read: {entry.FullName}");
        }
    }

    private enum CanonicalSourceKind
    {
        StandardModule,
        ClassModule,
        FormModule
    }
}
