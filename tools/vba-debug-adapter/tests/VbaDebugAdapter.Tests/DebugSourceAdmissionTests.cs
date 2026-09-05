using VbaDebugAdapter.Build;
using VbaDebugAdapter.Cli;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using VbaTools.Syntax;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class DebugSourceAdmissionTests
{
    [Fact]
    public void AdmissionIsAnInternalSealedDeepModuleWithoutASubstitutableInterface()
    {
        var assembly = typeof(StandaloneVbaDebugAdapterStdioRunner).Assembly;
        var admission = assembly.GetType(
            "VbaDebugAdapter.Debugging.DebugSourceAdmission",
            throwOnError: false);
        var admittedSnapshot = assembly.GetType(
            "VbaDebugAdapter.Debugging.AdmittedDebugSourceSnapshot",
            throwOnError: false);

        Assert.NotNull(admission);
        Assert.False(admission.IsPublic);
        Assert.True(admission.IsSealed);
        Assert.Empty(admission.GetInterfaces());
        Assert.NotNull(admittedSnapshot);
        Assert.False(admittedSnapshot.IsPublic);
        Assert.True(admittedSnapshot.IsSealed);
        Assert.Empty(admittedSnapshot.GetConstructors());

        var launchServiceFieldTypes = typeof(StandaloneVbaDebugLaunchService)
            .GetFields(System.Reflection.BindingFlags.Instance |
                       System.Reflection.BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();
        Assert.Contains(typeof(DebugSourceAdmission), launchServiceFieldTypes);
        Assert.DoesNotContain(
            typeof(TransportedDebugSourceSnapshotValidator),
            launchServiceFieldTypes);
        Assert.Null(assembly.GetType(
            "VbaDebugAdapter.Debugging.DebugLaunchRequestResolver",
            throwOnError: false));
        Assert.Null(assembly.GetType(
            "VbaDebugAdapter.Debugging.IBreakpointSourceMapper",
            throwOnError: false));
        Assert.Null(assembly.GetType(
            "VbaDebugAdapter.Debugging.DebugConditionalCompilationPreflight",
            throwOnError: false));
        Assert.Null(assembly.GetType(
            "VbaDebugAdapter.Debugging.DebugLaunchRequest",
            throwOnError: false));
    }

    [Fact]
    public void OneAdmissionParsesEachTextSourceExactlyOnceAcrossAllDerivedFacts()
    {
        const string module1Uri = "file:///C:/persistent/Module1.bas";
        const string module2Uri = "file:///C:/persistent/Module2.bas";
        var parseCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var admission = new DebugSourceAdmission(
            932,
            (sourceUri, text) =>
            {
                parseCounts[sourceUri] = parseCounts.GetValueOrDefault(sourceUri) + 1;
                return VbaSyntaxTree.ParseModule(sourceUri, text);
            });
        var generation = DebugGenerationId.FromValue(7);
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                TextSource(
                    "Module1.bas",
                    module1Uri,
                    "Attribute VB_Name = \"Module1\"\r\n" +
                    "#If VBA7 Then\r\n" +
                    "Public Sub Run()\r\n" +
                    "    Debug.Print \"target\"\r\n" +
                    "End Sub\r\n" +
                    "#End If\r\n"),
                TextSource(
                    "Module2.bas",
                    module2Uri,
                    "Attribute VB_Name = \"Module2\"\r\n" +
                    "Public Sub Other()\r\n" +
                    "    Debug.Print \"other\"\r\n" +
                    "End Sub\r\n")
            ])
        {
            Breakpoints =
            [
                new TransportedDebugSourceBreakpoint(module2Uri, 2),
                new TransportedDebugSourceBreakpoint(module1Uri, 3)
            ]
        };

        var admitted = admission.Admit(snapshot, "Module1", "Run", generation);
        admitted.VerifyConditionalCompilation(CreateVba7Environment());

        Assert.Equal(generation, admitted.GenerationId);
        Assert.Equal("Module1", admitted.Target.ModuleName);
        Assert.Equal("Run", admitted.Target.ProcedureName);
        Assert.Equal(
            [module2Uri, module1Uri],
            admitted.MappedBreakpoints.Select(breakpoint => breakpoint.Source.SourceUri));
        Assert.True(admitted.RequiresConditionalCompilationVerification);
        Assert.Equal(2, admitted.BuildSources.Count);
        Assert.Equal(2, parseCounts.Count);
        Assert.All(parseCounts.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public void WorkbookBuilderAcceptsOnlyTheOpaqueAdmittedBuildSourceSet()
    {
        var builderType = typeof(VbaDevSnapshotWorkbookBuilder);
        var builderFieldTypes = builderType
            .GetFields(System.Reflection.BindingFlags.Instance |
                       System.Reflection.BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();
        Assert.False(builderType.IsPublic);
        Assert.DoesNotContain(
            typeof(TransportedDebugSourceSnapshotValidator),
            builderFieldTypes);

        var requestType = typeof(VbaDevSnapshotBuildRequest);
        var requestPropertyTypes = requestType
            .GetProperties(System.Reflection.BindingFlags.Instance |
                           System.Reflection.BindingFlags.Public |
                           System.Reflection.BindingFlags.NonPublic)
            .Select(property => property.PropertyType)
            .ToArray();
        Assert.False(requestType.IsPublic);
        Assert.True(requestType.IsSealed);
        Assert.Empty(requestType.GetConstructors());
        Assert.Contains(typeof(AdmittedDebugBuildSourceSet), requestPropertyTypes);
        Assert.DoesNotContain(typeof(TransportedDebugSourceSnapshot), requestPropertyTypes);
        Assert.DoesNotContain(typeof(VbaSyntaxTree), requestPropertyTypes);
        var generationIdProperty = requestType.GetProperty(
            "GenerationId",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(generationIdProperty);
        Assert.Null(generationIdProperty.SetMethod);
    }

    [Fact]
    public void AdmissionUsesTheSharedIdentifierAuthorityForCodePageModuleNames()
    {
        const string sourceUri = "file:///C:/persistent/CodePage.bas";
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                TextSource(
                    "CodePage.bas",
                    sourceUri,
                    "Attribute VB_Name = \"A・\"\r\n" +
                    "Public Sub Run()\r\n" +
                    "    Debug.Print \"ready\"\r\n" +
                    "End Sub\r\n")
            ])
        {
            Breakpoints = [new TransportedDebugSourceBreakpoint(sourceUri, 2)]
        };

        var admitted = new DebugSourceAdmission(932).Admit(
            snapshot,
            "A・",
            "Run",
            DebugGenerationId.Initial);

        Assert.Equal("A・", admitted.Target.ModuleName);
        Assert.Equal("A・", Assert.Single(admitted.MappedBreakpoints).ModuleName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BreakpointSourceIdentityFailurePrecedesItsLineFailure(bool duplicateIdentity)
    {
        const string brokenUri = "file:///C:/persistent/Broken.bas";
        var brokenIdentity = duplicateIdentity
            ? "Attribute VB_Name = \"Broken\"\r\n" +
              "Attribute VB_Name = \"Duplicate\"\r\n"
            : string.Empty;
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                TextSource(
                    "Broken.bas",
                    brokenUri,
                    brokenIdentity +
                    "Public Sub Other()\r\n" +
                    "End Sub\r\n"),
                TextSource(
                    "Target.bas",
                    "file:///C:/persistent/Target.bas",
                    "Attribute VB_Name = \"Target\"\r\n" +
                    "Public Sub Run()\r\n" +
                    "End Sub\r\n")
            ])
        {
            Breakpoints = [new TransportedDebugSourceBreakpoint(brokenUri, 100)]
        };

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugSourceAdmission(932).Admit(
                snapshot,
                "Target",
                "Run",
                DebugGenerationId.Initial));

        Assert.Equal(
            $"Debug breakpoint source '{brokenUri}' does not contain exactly one " +
            "valid exported module identity.",
            error.Message);
    }

    [Theory]
    [InlineData(".bas")]
    [InlineData(".cls")]
    [InlineData(".frm")]
    public void AdmissionRejectsCaseInsensitiveModuleIdentityCollisionsAcrossSourceKinds(
        string conflictingExtension)
    {
        const string alphaUri = "file:///C:/persistent/Alpha.bas";
        var conflictingPath = $"Beta{conflictingExtension}";
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                TextSource(
                    "Alpha.bas",
                    alphaUri,
                    ExportedSource(".bas", "Worker")),
                TextSource(
                    conflictingPath,
                    $"file:///C:/persistent/{conflictingPath}",
                    ExportedSource(conflictingExtension, "worker")),
                TextSource(
                    "Target.bas",
                    "file:///C:/persistent/Target.bas",
                    "Attribute VB_Name = \"Target\"\r\n" +
                    "Public Sub Run()\r\n" +
                    "End Sub\r\n")
            ])
        {
            Breakpoints = [new TransportedDebugSourceBreakpoint(alphaUri, 100)]
        };

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugSourceAdmission(932).Admit(
                snapshot,
                "Target",
                "Run",
                DebugGenerationId.Initial));

        Assert.Equal(
            "Invalid breakpoint setup: exported module identity 'Worker' is ambiguous " +
            "in the source snapshot.",
            error.Message);
    }

    [Fact]
    public void TransportFailureRemainsAuthoritativeBeforeTargetFailure()
    {
        var snapshot = new TransportedDebugSourceSnapshot(2, []);

        var error = Assert.Throws<InvalidOperationException>(() =>
            new DebugSourceAdmission(932).Admit(
                snapshot,
                "Missing",
                "Run",
                DebugGenerationId.Initial));

        Assert.Equal(
            "The transported source snapshot must contain a complete source inventory.",
            error.Message);
    }

    [Fact]
    public void BreakpointFailurePrecedesAnUnrelatedIncompleteSourceIdentity()
    {
        const string breakpointUri = "file:///C:/persistent/Beta.bas";
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                TextSource(
                    "Alpha.bas",
                    "file:///C:/persistent/Alpha.bas",
                    "Attribute VB_Name = \"Alpha\"\r\n" +
                    "Public Sub Run()\r\n" +
                    "End Sub\r\n"),
                TextSource(
                    "Beta.bas",
                    breakpointUri,
                    "Attribute VB_Name = \"Beta\"\r\n" +
                    "Public Sub Other()\r\n" +
                    "' comment\r\n" +
                    "End Sub\r\n"),
                TextSource(
                    "Broken.bas",
                    "file:///C:/persistent/Broken.bas",
                    "Public Sub Unused()\r\n" +
                    "End Sub\r\n")
            ])
        {
            Breakpoints = [new TransportedDebugSourceBreakpoint(breakpointUri, 2)]
        };

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugSourceAdmission(932).Admit(
                snapshot,
                "Alpha",
                "Run",
                DebugGenerationId.Initial));

        Assert.Equal(
            $"Invalid breakpoint at '{breakpointUri}:3': " +
            "the physical source line is comment-only. The breakpoint was not relocated.",
            error.Message);
    }

    [Fact]
    public void AdmissionRejectsBytesThatDoNotStrictlyMatchTheDeclaredEncoding()
    {
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                new TransportedDebugSource(
                    "Module1.bas",
                    "file:///C:/persistent/Module1.bas",
                    "utf8bom",
                    Convert.ToBase64String([0xef, 0xbb, 0xbf, 0xff]))
            ]);

        var error = Assert.Throws<InvalidOperationException>(() =>
            new DebugSourceAdmission(932).Admit(
                snapshot,
                "Module1",
                "Run",
                DebugGenerationId.Initial));

        Assert.Contains("utf8bom", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nested:stream/Module1.bas")]
    [InlineData("CON/Module1.bas")]
    [InlineData("trailing-dot./Module1.bas")]
    [InlineData("trailing-space /Module1.bas")]
    public void AdmissionRejectsWindowsAmbiguousPathComponents(string relativePath)
    {
        var sourceUri = "file:///C:/persistent/" +
            relativePath.Replace(" ", "%20", StringComparison.Ordinal);
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                TextSource(
                    relativePath,
                    sourceUri,
                    "Attribute VB_Name = \"Module1\"\r\n" +
                    "Public Sub Run()\r\n" +
                    "End Sub\r\n")
            ]);

        var error = Assert.Throws<InvalidOperationException>(() =>
            new DebugSourceAdmission(932).Admit(
                snapshot,
                "Module1",
                "Run",
                DebugGenerationId.Initial));

        Assert.Contains(
            "unambiguous Windows path components",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AdmissionRejectsAnUnrelatedInvalidModuleIdentityBeforeBuildPreparation()
    {
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                TextSource(
                    "Broken.bas",
                    "file:///C:/persistent/Broken.bas",
                    "Attribute VB_Name = \"Broken\"\r\n" +
                    "Attribute VB_Name = \"Duplicate\"\r\n" +
                    "Public Sub Unused()\r\n" +
                    "End Sub\r\n"),
                TextSource(
                    "Module1.bas",
                    "file:///C:/persistent/Module1.bas",
                    "Attribute VB_Name = \"Module1\"\r\n" +
                    "Public Sub Run()\r\n" +
                    "End Sub\r\n")
            ]);

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugSourceAdmission(932).Admit(
                snapshot,
                "Module1",
                "Run",
                DebugGenerationId.Initial));

        Assert.Contains("exactly one valid exported module identity", error.Message);
    }

    [Fact]
    public void AdmissionReportsTheFirstInvalidBreakpointInRequestOrder()
    {
        const string alphaUri = "file:///C:/persistent/Alpha.bas";
        const string betaUri = "file:///C:/persistent/Beta.bas";
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                TextSource(
                    "Alpha.bas",
                    alphaUri,
                    "Attribute VB_Name = \"Alpha\"\r\n" +
                    "Public Sub Run()\r\n" +
                    "\r\n" +
                    "End Sub\r\n"),
                TextSource(
                    "Beta.bas",
                    betaUri,
                    "Attribute VB_Name = \"Beta\"\r\n" +
                    "Public Sub Other()\r\n" +
                    "' comment\r\n" +
                    "End Sub\r\n")
            ])
        {
            Breakpoints =
            [
                new TransportedDebugSourceBreakpoint(betaUri, 2),
                new TransportedDebugSourceBreakpoint(alphaUri, 2)
            ]
        };

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugSourceAdmission(932).Admit(
                snapshot,
                "Alpha",
                "Run",
                DebugGenerationId.Initial));

        Assert.Equal(
            $"Invalid breakpoint at '{betaUri}:3': the physical source line is comment-only. " +
            "The breakpoint was not relocated.",
            error.Message);
    }

    [Fact]
    public void TargetFailureRemainsAuthoritativeBeforeBreakpointAndUnrelatedSourceFailures()
    {
        const string alphaUri = "file:///C:/persistent/Alpha.bas";
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                TextSource(
                    "Alpha.bas",
                    alphaUri,
                    "Attribute VB_Name = \"Alpha\"\r\n" +
                    "Public Sub Run()\r\n" +
                    "\r\n" +
                    "End Sub\r\n"),
                TextSource(
                    "Broken.bas",
                    "file:///C:/persistent/Broken.bas",
                    "Attribute VB_Name = \"Broken\"\r\n" +
                    "Attribute VB_Name = \"Duplicate\"\r\n")
            ])
        {
            Breakpoints = [new TransportedDebugSourceBreakpoint(alphaUri, 2)]
        };

        var error = Assert.Throws<DebugSetupException>(() =>
            new DebugSourceAdmission(932).Admit(
                snapshot,
                "Missing",
                "Run",
                DebugGenerationId.Initial));

        Assert.Equal(
            "VBA debug module 'Missing' was not found in the selected document source snapshot.",
            error.Message);
    }

    [Fact]
    public async Task AdmittedBytesAndBreakpointsRemainStableAfterCallerTransportMutation()
    {
        const string sourceUri = "file:///C:/persistent/Module1.bas";
        var originalBytes = DebugSnapshotTestEncoding.Utf8BomBytes(
            "Attribute VB_Name = \"Module1\"\r\n" +
            "Public Sub Run()\r\n" +
            "    Debug.Print \"original\"\r\n" +
            "End Sub\r\n");
        var sources = new List<TransportedDebugSource>
        {
            new(
                "Module1.bas",
                sourceUri,
                "utf8bom",
                Convert.ToBase64String(originalBytes))
        };
        var breakpoints = new List<TransportedDebugSourceBreakpoint>
        {
            new(sourceUri, 2)
        };
        var generation = DebugGenerationId.FromValue(3);
        var admitted = new DebugSourceAdmission(932).Admit(
            new TransportedDebugSourceSnapshot(2, sources)
            {
                Breakpoints = breakpoints
            },
            "Module1",
            "Run",
            generation);

        sources[0] = TextSource(
            "Module1.bas",
            sourceUri,
            "Attribute VB_Name = \"Module1\"\r\n" +
            "Public Sub Changed()\r\n" +
            "End Sub\r\n");
        breakpoints.Clear();

        using var temp = TempDirectory.Create();
        await using var lease = await new VbaDebugSessionWorkspaceManager(temp.Path)
            .ClaimAsync(
                DebugSessionId.Parse("0123456789abcdef0123456789abcdef"),
                CancellationToken.None);
        await using var workspace = lease.CreateGenerationWorkspace(
            generation,
            "Book1.xlsm");
        admitted.BuildSources.MaterializeInto(workspace);

        Assert.Equal(
            originalBytes,
            await File.ReadAllBytesAsync(Path.Combine(
                workspace.SourceSnapshotPath,
                "Module1.bas")));
        Assert.Single(admitted.MappedBreakpoints);
        Assert.Equal("Module1", admitted.Target.ModuleName);
        Assert.Equal("Run", admitted.Target.ProcedureName);
    }

    [Fact]
    public async Task AdmittedLaunchGenerationsKeepTheirOwnFactsMappingsAndBuildBytes()
    {
        const string sourceUri = "file:///C:/persistent/Module.bas";
        const string initialText =
            "Attribute VB_Name = \"InitialModule\"\r\n" +
            "Public Sub RunInitial()\r\n" +
            "    Debug.Print \"initial\"\r\n" +
            "End Sub\r\n";
        const string restartedText =
            "Attribute VB_Name = \"RestartedModule\"\r\n" +
            "Option Explicit\r\n" +
            "Public Sub RunRestarted()\r\n" +
            "    Debug.Print \"restarted\"\r\n" +
            "End Sub\r\n";
        var initialGeneration = DebugGenerationId.Initial;
        var restartedGeneration = DebugGenerationId.FromValue(1);
        var admission = new DebugSourceAdmission(932);
        var initial = admission.Admit(
            new TransportedDebugSourceSnapshot(
                2,
                [TextSource("Module.bas", sourceUri, initialText)])
            {
                Breakpoints = [new TransportedDebugSourceBreakpoint(sourceUri, 2)]
            },
            "InitialModule",
            "RunInitial",
            initialGeneration);
        var restarted = admission.Admit(
            new TransportedDebugSourceSnapshot(
                2,
                [TextSource("Module.bas", sourceUri, restartedText)])
            {
                Breakpoints = [new TransportedDebugSourceBreakpoint(sourceUri, 3)]
            },
            "RestartedModule",
            "RunRestarted",
            restartedGeneration);

        Assert.Equal(initialGeneration, initial.GenerationId);
        Assert.Equal(initialGeneration, initial.BuildSources.GenerationId);
        Assert.Equal("InitialModule", initial.Target.ModuleName);
        Assert.Equal("RunInitial", initial.Target.ProcedureName);
        var initialBreakpoint = Assert.Single(initial.MappedBreakpoints);
        Assert.Equal(sourceUri, initialBreakpoint.Source.SourceUri);
        Assert.Equal("InitialModule", initialBreakpoint.ModuleName);
        Assert.Equal(2, initialBreakpoint.VbideLine);
        Assert.Equal("    Debug.Print \"initial\"", initialBreakpoint.ExpectedCodeLine);

        Assert.Equal(restartedGeneration, restarted.GenerationId);
        Assert.Equal(restartedGeneration, restarted.BuildSources.GenerationId);
        Assert.Equal("RestartedModule", restarted.Target.ModuleName);
        Assert.Equal("RunRestarted", restarted.Target.ProcedureName);
        var restartedBreakpoint = Assert.Single(restarted.MappedBreakpoints);
        Assert.Equal(sourceUri, restartedBreakpoint.Source.SourceUri);
        Assert.Equal("RestartedModule", restartedBreakpoint.ModuleName);
        Assert.Equal(3, restartedBreakpoint.VbideLine);
        Assert.Equal("    Debug.Print \"restarted\"", restartedBreakpoint.ExpectedCodeLine);

        using var temp = TempDirectory.Create();
        await using var lease = await new VbaDebugSessionWorkspaceManager(temp.Path)
            .ClaimAsync(
                DebugSessionId.Parse("0123456789abcdef0123456789abcdef"),
                CancellationToken.None);
        await using var initialWorkspace = lease.CreateGenerationWorkspace(
            initialGeneration,
            "Book1.xlsm");
        await using var restartedWorkspace = lease.CreateGenerationWorkspace(
            restartedGeneration,
            "Book1.xlsm");

        initial.BuildSources.MaterializeInto(initialWorkspace);
        restarted.BuildSources.MaterializeInto(restartedWorkspace);

        Assert.Throws<InvalidOperationException>(
            () => initial.BuildSources.MaterializeInto(restartedWorkspace));
        Assert.Throws<InvalidOperationException>(
            () => restarted.BuildSources.MaterializeInto(initialWorkspace));
        Assert.Equal(
            DebugSnapshotTestEncoding.Utf8BomBytes(initialText),
            await File.ReadAllBytesAsync(Path.Combine(
                initialWorkspace.SourceSnapshotPath,
                "Module.bas")));
        Assert.Equal(
            DebugSnapshotTestEncoding.Utf8BomBytes(restartedText),
            await File.ReadAllBytesAsync(Path.Combine(
                restartedWorkspace.SourceSnapshotPath,
                "Module.bas")));
    }

    private static TransportedDebugSource TextSource(
        string relativePath,
        string sourceUri,
        string text)
        => new(
            relativePath,
            sourceUri,
            "utf8bom",
            Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(text)));

    private static string ExportedSource(string extension, string moduleName)
        => extension switch
        {
            ".bas" =>
                $"Attribute VB_Name = \"{moduleName}\"\r\n" +
                "Public Sub Other()\r\n" +
                "End Sub\r\n",
            ".cls" =>
                "VERSION 1.0 CLASS\r\n" +
                "BEGIN\r\n" +
                "  MultiUse = -1  'True\r\n" +
                "END\r\n" +
                $"Attribute VB_Name = \"{moduleName}\"\r\n" +
                "Public Sub Other()\r\n" +
                "End Sub\r\n",
            ".frm" =>
                "VERSION 5.00\r\n" +
                $"Begin VB.Form {moduleName}\r\n" +
                "End\r\n" +
                $"Attribute VB_Name = \"{moduleName}\"\r\n" +
                "Public Sub Other()\r\n" +
                "End Sub\r\n",
            _ => throw new ArgumentOutOfRangeException(
                nameof(extension),
                extension,
                null)
        };

    private static VbaConditionalCompilationEnvironment CreateVba7Environment()
    {
        var constants = new Dictionary<string, VbaConditionalCompilationValue>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["VBA6"] = VbaConditionalCompilationValue.FromBoolean(true),
            ["VBA7"] = VbaConditionalCompilationValue.FromBoolean(true),
            ["Win16"] = VbaConditionalCompilationValue.FromBoolean(false),
            ["Win32"] = VbaConditionalCompilationValue.FromBoolean(true),
            ["Win64"] = VbaConditionalCompilationValue.FromBoolean(true),
            ["Mac"] = VbaConditionalCompilationValue.FromBoolean(false)
        };
        return new VbaConditionalCompilationEnvironment(
            constants,
            constants.Keys,
            supportsLongLong: true);
    }
}
