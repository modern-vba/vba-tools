using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
public sealed class VbaProjectReferenceAmbiguityProbeWindowsExcelIntegrationTests
{
    private const string ScriptingGuid = "420b2830-e718-11cf-893d-00a0c9054228";
    private const string WindowsScriptHostGuid = "f935dc20-1cf0-11d0-adb9-00c04fd58a0b";
    private const string UnavailableGuid = "11111111-2222-3333-4444-555555555555";

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task UniqueRegistryIdentityDoesNotStartExcelOrCreateProbeWorkspace()
    {
        using var temp = TempDirectory.Create();
        var missingTemplatePath = Path.Combine(temp.Path, "MissingTemplate.xlsm");
        var initialProcesses = CaptureExcelProcessIds();
        var initialProbeWorkspaces = CaptureProbeWorkspaces();
        var lifecycle = new ObservingReferenceProbeLifecycle();
        var planner = new VbaProjectReferencePlanner(
            new RegistryVbaProjectReferenceResolver(),
            new VbaProjectReferenceAmbiguityProbe(
                new ExcelComVbaProjectReferenceProbeAutomation(
                    new StaComDispatcherFactory(),
                    lifecycle)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        VbaProjectReferenceResolutionBatch result;
        try
        {
            result = await planner.ResolveReferencesAsync(
                missingTemplatePath,
                ["Microsoft Scripting Runtime"],
                cancellation.Token);
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
        }

        Assert.True(result.Complete);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(ScriptingGuid, Assert.Single(Assert.Single(result.References).Matches).Guid);
        Assert.Equal(0, lifecycle.StartCalls);
        Assert.False(File.Exists(missingTemplatePath));
        Assert.True(initialProbeWorkspaces.SetEquals(CaptureProbeWorkspaces()));
        Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task RealRejectedCandidateClosesBeforeTheNextFreshReferenceBaseline()
    {
        var initialProcesses = CaptureExcelProcessIds();
        var initialProbeWorkspaces = CaptureProbeWorkspaces();
        var lifecycle = new ObservingReferenceProbeLifecycle();
        var probe = new VbaProjectReferenceAmbiguityProbe(
            new ExcelComVbaProjectReferenceProbeAutomation(
                new StaComDispatcherFactory(),
                lifecycle));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        VbaProjectReferenceResolutionBatch result;
        try
        {
            result = await probe.ResolveAsync(
                VbaProjectReferenceProbeBaseline.BlankWorkbook,
                CreateRegistryResolution(),
                cancellation.Token);
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
        }

        Assert.True(result.Complete);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(ScriptingGuid, Assert.Single(result.References[0].Matches).Guid);
        Assert.Equal([ScriptingGuid, WindowsScriptHostGuid], result.References[1].Matches.Select(match => match.Guid));
        Assert.Equal(1, lifecycle.StartCalls);
        Assert.Contains(UnavailableGuid, lifecycle.RejectedCandidateGuids);
        Assert.True(lifecycle.BaselineSignatures.Count >= 4);
        Assert.NotEmpty(lifecycle.BaselineSignatures[0]);
        Assert.All(lifecycle.BaselineSignatures, baseline =>
            Assert.Equal(lifecycle.BaselineSignatures[0], baseline));
        Assert.Equal(
            Enumerable.Range(0, lifecycle.BaselineSignatures.Count),
            lifecycle.CompletedClosesBeforeOpen);
        Assert.Equal(lifecycle.BaselineSignatures.Count, lifecycle.CompletedCloses);
        Assert.True(initialProbeWorkspaces.SetEquals(CaptureProbeWorkspaces()));
        Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task RealReferenceAdditionCancellationStopsLaterCandidatesAndReleasesOwnedExcel()
    {
        var initialProcesses = CaptureExcelProcessIds();
        var initialProbeWorkspaces = CaptureProbeWorkspaces();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var lifecycle = new ObservingReferenceProbeLifecycle
        {
            AfterReferenceAdded = cancellation.Cancel
        };
        var probe = new VbaProjectReferenceAmbiguityProbe(
            new ExcelComVbaProjectReferenceProbeAutomation(
                new StaComDispatcherFactory(),
                lifecycle));

        VbaProjectReferenceResolutionBatch result;
        try
        {
            result = await probe.ResolveAsync(
                VbaProjectReferenceProbeBaseline.BlankWorkbook,
                CreateTwoNameResolution(),
                cancellation.Token);
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
        }

        Assert.False(result.Complete);
        Assert.All(result.References, reference =>
        {
            Assert.Equal("cancelled", reference.UnverifiedReasonCode);
            Assert.Empty(reference.Matches);
        });
        Assert.Equal("operationCancelled", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(1, lifecycle.StartCalls);
        Assert.Equal(1, lifecycle.AddCalls);
        Assert.Equal(1, lifecycle.CompletedAdds);
        Assert.True(initialProbeWorkspaces.SetEquals(CaptureProbeWorkspaces()));
        Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task RealProbeAttemptTimeoutStopsLaterCandidatesAndReleasesOwnedExcel()
    {
        var initialProcesses = CaptureExcelProcessIds();
        var initialProbeWorkspaces = CaptureProbeWorkspaces();
        var lifecycle = new ObservingReferenceProbeLifecycle
        {
            // Bound the test-only lifecycle stall; real startup, workbook creation,
            // and baseline reference inspection have already completed.
            BeforeReferenceAdded = () => Thread.Sleep(TimeSpan.FromSeconds(1))
        };
        var probe = new VbaProjectReferenceAmbiguityProbe(
            new ExcelComVbaProjectReferenceProbeAutomation(
                new StaComDispatcherFactory(),
                lifecycle),
            WorkbookAutomationTimeouts.Default with
            {
                ReferenceAttempt = TimeSpan.FromMilliseconds(100)
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        VbaProjectReferenceResolutionBatch result;
        try
        {
            result = await probe.ResolveAsync(
                VbaProjectReferenceProbeBaseline.BlankWorkbook,
                CreateTwoNameResolution(),
                cancellation.Token);
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
        }

        Assert.False(result.Complete);
        Assert.Equal("probeTimeout", result.References[0].UnverifiedReasonCode);
        Assert.Contains("reference attempt", result.References[0].Message, StringComparison.Ordinal);
        Assert.Equal("probeAborted", result.References[1].UnverifiedReasonCode);
        Assert.All(result.References, reference => Assert.Empty(reference.Matches));
        Assert.Equal("probeProcessUntrusted", Assert.Single(result.Diagnostics).Code);
        Assert.False(cancellation.IsCancellationRequested);
        Assert.Equal(1, lifecycle.StartCalls);
        Assert.Equal(1, lifecycle.AddCalls);
        Assert.NotEmpty(Assert.Single(lifecycle.BaselineSignatures));
        Assert.True(initialProbeWorkspaces.SetEquals(CaptureProbeWorkspaces()));
        Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task RealCandidateRejectionCannotContinueAfterBaselineReleaseBecomesUncertain()
    {
        var initialProcesses = CaptureExcelProcessIds();
        var initialProbeWorkspaces = CaptureProbeWorkspaces();
        var cleanupFaults = 0;
        var lifecycle = new ObservingReferenceProbeLifecycle
        {
            AfterWorkbookClosed = () =>
            {
                // Native close and COM release really ran. Inject only the
                // uncertainty reported at that lifecycle boundary.
                if (++cleanupFaults == 1)
                {
                    throw new COMException("Test-only baseline COM-release uncertainty.");
                }
            }
        };
        var probe = new VbaProjectReferenceAmbiguityProbe(
            new ExcelComVbaProjectReferenceProbeAutomation(
                new StaComDispatcherFactory(),
                lifecycle));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        VbaProjectReferenceResolutionBatch result;
        try
        {
            result = await probe.ResolveAsync(
                VbaProjectReferenceProbeBaseline.BlankWorkbook,
                CreateTwoNameResolution(firstCandidateGuid: UnavailableGuid),
                cancellation.Token);
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
        }

        Assert.False(result.Complete);
        Assert.Equal("cleanupFailure", result.References[0].UnverifiedReasonCode);
        Assert.Equal("probeAborted", result.References[1].UnverifiedReasonCode);
        Assert.All(result.References, reference => Assert.Empty(reference.Matches));
        Assert.Equal("probeProcessUntrusted", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(1, lifecycle.StartCalls);
        Assert.Equal(1, lifecycle.AddCalls);
        Assert.Equal(UnavailableGuid, Assert.Single(lifecycle.RejectedCandidateGuids));
        Assert.Equal(1, lifecycle.CompletedCloses);
        Assert.Equal(1, cleanupFaults);
        Assert.NotEmpty(Assert.Single(lifecycle.BaselineSignatures));
        Assert.True(initialProbeWorkspaces.SetEquals(CaptureProbeWorkspaces()));
        Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task RealVbeCoversFallbackAmbiguityAndOwnedCleanupFromOneSelectedTemplate()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        CreateEmptyMacroEnabledWorkbook(templatePath);
        var originalTemplate = File.ReadAllBytes(templatePath);
        var initialProcesses = CaptureExcelProcessIds();
        var initialProbeWorkspaces = CaptureProbeWorkspaces();
        var probe = new VbaProjectReferenceAmbiguityProbe(
            new ExcelComVbaProjectReferenceProbeAutomation());
        var registryResolution = CreateRegistryResolution();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        VbaProjectReferenceResolutionBatch result;
        try
        {
            result = await probe.ResolveAsync(
                templatePath,
                registryResolution,
                cancellation.Token);
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
        }

        Assert.True(result.Complete);
        Assert.Empty(result.Diagnostics);
        var selected = Assert.Single(result.References[0].Matches);
        Assert.Equal(ScriptingGuid, selected.Guid);
        Assert.Equal(1, selected.Major);
        Assert.Equal(0, selected.Minor);
        Assert.Equal(
            [
                (ScriptingGuid, 1, 0),
                (WindowsScriptHostGuid, 1, 0)
            ],
            result.References[1].Matches
                .Select(identity => (identity.Guid, identity.Major, identity.Minor)));
        Assert.Equal(originalTemplate, File.ReadAllBytes(templatePath));
        Assert.True(initialProbeWorkspaces.SetEquals(CaptureProbeWorkspaces()));
        Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task RealVbeResolvesAmbiguityFromFreshBlankWorkbooksWithoutProbeFiles()
    {
        var initialProcesses = CaptureExcelProcessIds();
        var initialProbeWorkspaces = CaptureProbeWorkspaces();
        var probe = new VbaProjectReferenceAmbiguityProbe(
            new ExcelComVbaProjectReferenceProbeAutomation());
        var registryResolution = CreateRegistryResolution();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        VbaProjectReferenceResolutionBatch result;
        try
        {
            result = await probe.ResolveAsync(
                VbaProjectReferenceProbeBaseline.BlankWorkbook,
                registryResolution,
                cancellation.Token);
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
        }

        Assert.True(result.Complete);
        Assert.Empty(result.Diagnostics);
        var selected = Assert.Single(result.References[0].Matches);
        Assert.Equal(ScriptingGuid, selected.Guid);
        Assert.Equal(1, selected.Major);
        Assert.Equal(0, selected.Minor);
        Assert.True(initialProbeWorkspaces.SetEquals(CaptureProbeWorkspaces()));
        Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()));
    }

    private static VbaProjectReferenceResolutionBatch CreateRegistryResolution()
    {
        var scriptingHigh = new ResolvedVbaProjectReference(
            "Microsoft Scripting Runtime",
            ScriptingGuid,
            ushort.MaxValue,
            0);
        var scriptingInstalled = scriptingHigh with { Major = 1 };
        var unavailable = new ResolvedVbaProjectReference(
            "Microsoft Scripting Runtime",
            UnavailableGuid,
            1,
            0);
        var scriptingAmbiguity = new ResolvedVbaProjectReference(
            "Synthetic Probe Ambiguity",
            ScriptingGuid,
            1,
            0);
        var windowsScriptHost = new ResolvedVbaProjectReference(
            "Synthetic Probe Ambiguity",
            WindowsScriptHostGuid,
            1,
            0);
        return new VbaProjectReferenceResolutionBatch(
            true,
            [],
            null,
            [
                new VbaProjectReferenceNameResolution(
                    "Microsoft Scripting Runtime",
                    "Microsoft Scripting Runtime",
                    true,
                    [scriptingHigh, unavailable],
                    [
                        new VbaProjectReferenceCandidateLineage(
                            ScriptingGuid,
                            [scriptingHigh, scriptingInstalled]),
                        new VbaProjectReferenceCandidateLineage(
                            UnavailableGuid,
                            [unavailable])
                    ]),
                new VbaProjectReferenceNameResolution(
                    "Synthetic Probe Ambiguity",
                    "Synthetic Probe Ambiguity",
                    true,
                    [scriptingAmbiguity, windowsScriptHost])
            ]);
    }

    private static VbaProjectReferenceResolutionBatch CreateTwoNameResolution(
        string firstCandidateGuid = ScriptingGuid)
        => new(
            true,
            [],
            null,
            new[] { "Native fault probe", "Later native probe" }
                .Select((name, index) => new VbaProjectReferenceNameResolution(
                    name,
                    name,
                    true,
                    [
                        new ResolvedVbaProjectReference(name, index == 0 ? firstCandidateGuid : ScriptingGuid, 1, 0),
                        new ResolvedVbaProjectReference(name, WindowsScriptHostGuid, 1, 0)
                    ]))
                .ToArray());

    private sealed class ObservingReferenceProbeLifecycle : IExcelComVbaProjectReferenceProbeLifecycle
    {
        private readonly ExcelComVbaProjectReferenceProbeAutomation.ExcelComVbaProjectReferenceProbeLifecycle inner = new();

        public int StartCalls { get; private set; }

        public int CompletedCloses { get; private set; }

        public int AddCalls { get; private set; }

        public int CompletedAdds { get; private set; }

        public Action? AfterReferenceAdded { get; init; }

        public Action? BeforeReferenceAdded { get; init; }

        public Action? AfterWorkbookClosed { get; init; }

        public List<int> CompletedClosesBeforeOpen { get; } = [];

        public List<string[]> BaselineSignatures { get; } = [];

        public List<string> RejectedCandidateGuids { get; } = [];

        public object Start(
            OwnedExcelTerminationController terminationController,
            CancellationToken cancellationToken)
        {
            StartCalls++;
            return inner.Start(terminationController, cancellationToken);
        }

        public object OpenWorkbook(object host, string workbookPath)
            => inner.OpenWorkbook(host, workbookPath);

        public object CreateBlankWorkbook(object host)
        {
            CompletedClosesBeforeOpen.Add(CompletedCloses);
            var workbook = inner.CreateBlankWorkbook(host);
            try
            {
                BaselineSignatures.Add(CaptureReferenceSignature(workbook));
                return workbook;
            }
            catch
            {
                inner.CloseWorkbookWithoutSave(workbook);
                throw;
            }
        }

        public object? FindReference(object workbook, string referenceName)
            => inner.FindReference(workbook, referenceName);

        public object AddReference(object workbook, ResolvedVbaProjectReference candidate)
        {
            AddCalls++;
            BeforeReferenceAdded?.Invoke();
            try
            {
                var reference = inner.AddReference(workbook, candidate);
                CompletedAdds++;
                AfterReferenceAdded?.Invoke();
                return reference;
            }
            catch (VbaProjectReferenceCandidateRejectedException)
            {
                RejectedCandidateGuids.Add(candidate.Guid);
                throw;
            }
        }

        public ResolvedVbaProjectReference ReadIdentity(object reference, string referenceName)
            => inner.ReadIdentity(reference, referenceName);

        public void ReleaseReference(object? reference)
            => inner.ReleaseReference(reference);

        public void CloseWorkbookWithoutSave(object workbook)
        {
            inner.CloseWorkbookWithoutSave(workbook);
            CompletedCloses++;
            AfterWorkbookClosed?.Invoke();
        }

        public void DisposeHost(object host, TimeSpan cleanupGrace)
            => inner.DisposeHost(host, cleanupGrace);

        private static string[] CaptureReferenceSignature(object workbook)
        {
            object? project = null;
            object? references = null;
            try
            {
                project = ((dynamic)workbook).VBProject;
                references = ((dynamic)project).References;
                var signatures = new List<string>();
                var count = (int)((dynamic)references).Count;
                for (var index = 1; index <= count; index++)
                {
                    object? reference = null;
                    try
                    {
                        reference = ((dynamic)references).Item(index);
                        dynamic value = reference;
                        signatures.Add($"{Guid.Parse((string)value.Guid):D}:{(int)value.Major}:{(int)value.Minor}");
                    }
                    finally
                    {
                        ComObjectReleaser.Release(reference);
                    }
                }

                return signatures.Order(StringComparer.Ordinal).ToArray();
            }
            finally
            {
                ComObjectReleaser.Release(references);
                ComObjectReleaser.Release(project);
            }
        }
    }

    private static void CreateEmptyMacroEnabledWorkbook(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(
            archive,
            "[Content_Types].xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.ms-excel.sheet.macroEnabled.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);
        WriteEntry(
            archive,
            "_rels/.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        WriteEntry(
            archive,
            "xl/workbook.xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        WriteEntry(
            archive,
            "xl/_rels/workbook.xml.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);
        WriteEntry(
            archive,
            "xl/worksheets/sheet1.xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData/></worksheet>
            """);
    }

    private static void WriteEntry(
        ZipArchive archive,
        string name,
        string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
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

    private static IReadOnlySet<string> CaptureProbeWorkspaces()
    {
        var root = Path.Combine(Path.GetTempPath(), "vba-dev-reference-probe");
        return Directory.Exists(root)
            ? Directory.EnumerateDirectories(root)
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task WaitForProcessSetAsync(
        IReadOnlySet<int> expected,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (CaptureExcelProcessIds().SetEquals(expected))
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Equal(
            expected.Order().ToArray(),
            CaptureExcelProcessIds().Order().ToArray());
    }
}
