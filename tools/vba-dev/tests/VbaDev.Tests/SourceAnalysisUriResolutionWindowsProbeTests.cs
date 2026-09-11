using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using VbaDev.App.Build;
using VbaDev.App.HostEvents;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Projects;
using VbaDev.Infrastructure.References;
using VbaDev.Infrastructure.Workbooks;
using VbaTools.Semantics;
using VbaTools.Syntax;
using VbaTools.TypeLibRegistry;
using Xunit;
using Xunit.Abstractions;

namespace VbaDev.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
public sealed class SourceAnalysisUriResolutionWindowsProbeTests(ITestOutputHelper output)
{
    private const string StandardLibraryGuid = "000204ef-0000-0000-c000-000000000046";
    private const string ProjectEnvironmentVariable =
        "VBA_TOOLS_SOURCE_ANALYSIS_URI_PROBE_PROJECT";
    private const string TrialsEnvironmentVariable =
        "VBA_TOOLS_SOURCE_ANALYSIS_URI_PROBE_TRIALS";

    [SourceAnalysisUriResolutionProbeFact]
    [Trait("Category", "SourceAnalysisUriResolutionProbe")]
    public async Task Exact_project_and_installed_TypeLibs_complete_shared_analysis()
    {
        Assert.True(OperatingSystem.IsWindows(), "The opted-in probe requires Windows.");
        Assert.Equal(Architecture.X64, RuntimeInformation.ProcessArchitecture);
        var projectRoot = RequireProjectRoot();
        var trials = ReadTrials();
        var beforeFiles = CaptureTree(projectRoot);
        var beforeExcel = CaptureExcelProcessIds();
        var attempted = 0;
        var completed = 0;
        var nonReproductions = 0;
        ExceptionDispatchInfo? probeFailure = null;
        var protectionFailures = new List<Exception>();
        try
        {
            var context = new ProjectContextResolver(new JsonProjectManifestStore()).Resolve(
                new ProjectResolutionRequest(projectRoot, null, projectRoot));
            var (_, analysis, projectFatal) = new VbaSourceAdmission(() => 932)
                .ReadSnapshotAnalysis(context.DocumentSourceSetPath, CancellationToken.None);
            var admissionReport = analysis.ToReport();
            Assert.False(projectFatal);
            Assert.True(admissionReport.Complete, string.Join(
                Environment.NewLine,
                admissionReport.Failures.Select(failure => failure.Message)));
            var syntaxTrees = analysis.CapturedSyntaxTrees;
            Assert.Equal(35, syntaxTrees.Length);

            var template = CapturedWorkbookTemplate.Capture(context.TemplateDocumentPath);
            var packageMetadata = template.ReadMetadata(CancellationToken.None).Metadata;
            Assert.NotNull(packageMetadata);
            var referencePlanner = new VbaProjectReferencePlanner(
                new RegistryVbaProjectReferenceResolver());
            var observedStandard = ReadInstalledStandardLibrary();
            var inputs = await new ProjectSemanticInputProvider(
                referencePlanner,
                new RegistryTypeLibRegistryCatalogReader(),
                new ComTypeLibCatalogMetadataReader(),
                new RegistryOfficeClickToRunTypeLibEvidenceReader(),
                new RejectingHostEventCatalogAutomation(),
                new CapturedProjectIdentityProbe(packageMetadata.ProjectName, [observedStandard]))
                .AcquireAsync(context, template, syntaxTrees, CancellationToken.None);
            Assert.Equal(6, inputs.ReferenceCatalogIdentities.Count);
            Assert.Empty(inputs.ReferenceCatalogs.GetMissingCatalogReferenceNames(
                Assert.IsType<VbaReferenceSelection>(inputs.ReferenceSelection)));

            WriteEnvironment(context, syntaxTrees.Length, trials, beforeFiles);
            WriteAssembly("syntax", typeof(VbaSyntaxTree).Assembly);
            WriteAssembly("semantics", typeof(VbaProjectSourceAnalysis).Assembly);
            WriteAssembly("typelib-reader", typeof(ComTypeLibCatalogMetadataReader).Assembly);
            foreach (var identity in inputs.ReferenceCatalogIdentities.Values
                         .OrderBy(identity => identity.ReferenceName, StringComparer.Ordinal))
            {
                output.WriteLine(
                    $"typelib name={identity.ReferenceName}, guid={identity.Guid}, version={identity.MajorVersion}.{identity.MinorVersion}, lcid={identity.Lcid}, path={identity.Path}, fileSha256={TryHashTypeLibFile(identity.Path)}");
            }
            output.WriteLine(
                $"activeDefinitions={inputs.ReferenceCatalogs.GetActiveDefinitions(inputs.ReferenceSelection).Count}");

            string[]? expectedDiagnostics = null;
            for (var trial = 1; trial <= trials; trial++)
            {
                var started = Stopwatch.GetTimestamp();
                attempted++;
                var diagnostics = VbaProjectSourceAnalysis.Analyze(
                    syntaxTrees,
                    inputs,
                    CancellationToken.None);
                completed++;
                var signatures = diagnostics.Select(diagnostic =>
                        $"{diagnostic.SourceUri}|{diagnostic.Code}|{diagnostic.Range.Start.Line}:{diagnostic.Range.Start.Character}")
                    .ToArray();
                expectedDiagnostics ??= signatures;
                Assert.Equal(expectedDiagnostics, signatures);
                nonReproductions++;
                output.WriteLine(
                    $"trial={trial}, elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}, diagnostics={signatures.Length}");
            }

            foreach (var signature in expectedDiagnostics ?? [])
            {
                output.WriteLine($"diagnostic={signature}");
            }
        }
        catch (Exception error)
        {
            probeFailure = ExceptionDispatchInfo.Capture(error);
            output.WriteLine($"probeException={error}");
        }
        finally
        {
            output.WriteLine(
                $"trials requested={trials}, attempted={attempted}, completed={completed}, nonReproductions={nonReproductions}");
            CapturePostState(projectRoot, beforeFiles, beforeExcel, protectionFailures);
        }

        if (probeFailure is not null)
        {
            for (var index = 0; index < protectionFailures.Count; index++)
            {
                var protectionFailure = protectionFailures[index];
                output.WriteLine($"protectionFailure[{index}]={protectionFailure}");
                probeFailure.SourceException.Data[$"protectionFailure[{index}]"] = protectionFailure.ToString();
            }
            probeFailure.Throw();
        }

        if (protectionFailures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(protectionFailures[0]).Throw();
        }
        if (protectionFailures.Count > 1)
        {
            throw new AggregateException(
                "The source-analysis probe violated multiple read-only protection invariants.",
                protectionFailures);
        }
    }

    private void CapturePostState(
        string projectRoot,
        TreeIdentity beforeFiles,
        IReadOnlySet<int> beforeExcel,
        ICollection<Exception> failures)
    {
        try
        {
            var afterExcel = CaptureExcelProcessIds();
            var unchanged = beforeExcel.SetEquals(afterExcel);
            output.WriteLine(
                $"excelProcessIds before={FormatProcessIds(beforeExcel)}, after={FormatProcessIds(afterExcel)}, unchanged={unchanged}");
            if (!unchanged)
            {
                failures.Add(new InvalidOperationException(
                    "The source-analysis probe must not start or stop Excel."));
            }
        }
        catch (Exception error)
        {
            output.WriteLine($"excelPostStateException={error}");
            failures.Add(new InvalidOperationException(
                "The source-analysis probe could not verify the post-run Excel process state.",
                error));
        }

        try
        {
            var afterFiles = CaptureTree(projectRoot);
            var unchanged = beforeFiles == afterFiles;
            output.WriteLine(
                $"projectTreeAfter files={afterFiles.FileCount}, bytes={afterFiles.Bytes}, sha256={afterFiles.Sha256}, unchanged={unchanged}");
            if (!unchanged)
            {
                failures.Add(new InvalidOperationException(
                    "The source-analysis probe must not modify the target project tree."));
            }
        }
        catch (Exception error)
        {
            output.WriteLine($"projectTreePostStateException={error}");
            failures.Add(new InvalidOperationException(
                "The source-analysis probe could not verify the post-run project tree.",
                error));
        }
    }

    private static string FormatProcessIds(IEnumerable<int> processIds)
        => string.Join(',', processIds.Order());

    private void WriteEnvironment(
        ResolvedProjectContext context,
        int sourceCount,
        int trials,
        TreeIdentity files)
    {
        output.WriteLine($"project={context.ProjectRoot}");
        output.WriteLine($"manifest={context.ManifestPath}, sha256={HashFile(context.ManifestPath)}");
        output.WriteLine($"sourceRoot={context.DocumentSourceSetPath}, codePage=932, sources={sourceCount}");
        output.WriteLine($"template={context.TemplateDocumentPath}, sha256={HashFile(context.TemplateDocumentPath)}");
        output.WriteLine($"projectTree files={files.FileCount}, bytes={files.Bytes}, sha256={files.Sha256}");
        output.WriteLine($"os={RuntimeInformation.OSDescription}, osArchitecture={RuntimeInformation.OSArchitecture}");
        output.WriteLine(
            $"framework={RuntimeInformation.FrameworkDescription}, runtime={Environment.Version}, processArchitecture={RuntimeInformation.ProcessArchitecture}, target={AppContext.TargetFrameworkName}");
        output.WriteLine(
            $"invocation=dotnet test VbaDev.Tests.csproj -c Release --filter FullyQualifiedName~{nameof(SourceAnalysisUriResolutionWindowsProbeTests)}, trials={trials}");
    }

    private void WriteAssembly(string role, Assembly assembly)
        => output.WriteLine(
            $"assembly role={role}, fullName={assembly.FullName}, mvid={assembly.ManifestModule.ModuleVersionId}, path={assembly.Location}, sha256={HashFile(assembly.Location)}, informationalVersion={assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown"}");

    private static string RequireProjectRoot()
    {
        var configured = Environment.GetEnvironmentVariable(ProjectEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(configured),
            $"Set {ProjectEnvironmentVariable} to the affected vba-dev project root.");
        var root = Path.GetFullPath(configured!);
        Assert.True(Directory.Exists(root), $"Configured project root does not exist: {root}");
        return root;
    }

    private static int ReadTrials()
    {
        var configured = Environment.GetEnvironmentVariable(TrialsEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return 3;
        }
        Assert.True(int.TryParse(configured, out var trials) && trials is >= 1 and <= 10,
            $"{TrialsEnvironmentVariable} must be an integer from 1 through 10.");
        return trials;
    }

    private static WorkbookReference ReadInstalledStandardLibrary()
    {
        var reference = new ResolvedVbaProjectReference(
            VbaProjectReferenceCatalogSet.StandardLibraryReferenceName,
            StandardLibraryGuid,
            4,
            2);
        var evidence = new RegistryOfficeClickToRunTypeLibEvidenceReader().Read();
        Assert.True(evidence.Complete, evidence.Diagnostic);
        var match = Assert.Single(
            evidence.Installations.SelectMany(installation =>
                installation.Registrations.Select(registration => (installation, registration))),
            item => item.installation.Platform.Equals("x64", StringComparison.OrdinalIgnoreCase)
                && item.registration.Platform.Equals("win64", StringComparison.OrdinalIgnoreCase)
                && item.registration.Guid.Equals(StandardLibraryGuid, StringComparison.OrdinalIgnoreCase)
                && item.registration.Major == 4
                && item.registration.Minor == 2);
        var physicalPath = Path.Combine(
            match.installation.InstallationPath,
            "root",
            "vfs",
            "ProgramFilesCommonX64",
            "Microsoft Shared",
            "VBA",
            "VBA7.1",
            "VBE7.DLL");
        Assert.True(File.Exists(physicalPath),
            $"The installed VBA TypeLib does not exist at '{physicalPath}'.");
        return new WorkbookReference(
            reference.Name,
            IsRemovable: false,
            NamespaceName: "VBA",
            Guid: reference.Guid,
            Major: reference.Major,
            Minor: reference.Minor,
            FullPath: physicalPath);
    }

    private static TreeIdentity CaptureTree(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new FileInfo(path))
            .ToArray();
        var entries = files.Select(file =>
                $"{Path.GetRelativePath(root, file.FullName)}\0{file.Length}\0{file.LastWriteTimeUtc.Ticks}\0{HashFile(file.FullName)}")
            .ToArray();
        return new(files.Length, files.Sum(file => file.Length),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries)))));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string TryHashTypeLibFile(string path)
    {
        var physicalPath = File.Exists(path) ? path : Path.GetDirectoryName(path);
        return physicalPath is not null && File.Exists(physicalPath)
            ? HashFile(physicalPath)
            : "unavailable";
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

    private sealed record TreeIdentity(int FileCount, long Bytes, string Sha256);

    private sealed class CapturedProjectIdentityProbe(
        string projectName,
        IReadOnlyList<WorkbookReference> references)
        : IWorkbookProjectIdentityProbe
    {
        public Task<WorkbookProjectIdentity> ReadAsync(
            CapturedWorkbookTemplate template,
            IReadOnlyList<string> requiredReferenceNames,
            CancellationToken cancellationToken)
            => Task.FromResult(new WorkbookProjectIdentity(projectName, references));
    }

    private sealed class RejectingHostEventCatalogAutomation : IHostEventCatalogAutomation
    {
        public Task<IntrinsicHostEventCatalog> ReadAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "This read-only probe cannot acquire an Excel-backed host Event catalog.");
    }
}

public sealed class SourceAnalysisUriResolutionProbeFactAttribute : FactAttribute
{
    private const string OptInEnvironmentVariable =
        "VBA_TOOLS_RUN_SOURCE_ANALYSIS_URI_PROBE";

    public SourceAnalysisUriResolutionProbeFactAttribute()
    {
        Timeout = 360_000;
        if (!string.Equals(
                Environment.GetEnvironmentVariable(OptInEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            Skip = $"Set {OptInEnvironmentVariable}=1 to run the installed-TypeLib source-analysis probe.";
        }
    }
}
