using System.Collections.Immutable;
using System.Text;
using VbaDev.App.Cli;
using VbaDev.App.Debugging;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using Xunit;

namespace VbaDev.Tests;

public sealed class BuildCommandDebugWorkbookBuilderTests
{
    [Fact]
    public async Task ExactSnapshotUsesNormalPlannerOrderAndStagedSourceFiles()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp);
        var alphaPath = CreateSource(context, "Alpha.bas", "Attribute VB_Name = \"Alpha\"\r\n");
        var zetaPath = CreateSource(context, "Zeta.bas", "Attribute VB_Name = \"Zeta\"\r\n");
        var plannedSources = new[]
        {
            Source(zetaPath),
            Source(alphaPath)
        };
        var snapshot = Snapshot(
            (alphaPath, File.ReadAllText(alphaPath, Encoding.UTF8)),
            (zetaPath, File.ReadAllText(zetaPath, Encoding.UTF8)));
        var buildCalls = 0;
        var builder = new BuildCommandDebugWorkbookBuilder(
            resolveBuildSources: _ => plannedSources,
            runBuild: (_, stagedSources) =>
            {
                buildCalls++;
                Assert.Equal(["Zeta.bas", "Alpha.bas"], stagedSources.Select(source => source.FileName));
                Assert.All(stagedSources, source =>
                {
                    Assert.True(File.Exists(source.SourcePath));
                    Assert.DoesNotContain(
                        Path.GetFullPath(context.DocumentSourceSetPath),
                        Path.GetFullPath(source.SourcePath),
                        StringComparison.OrdinalIgnoreCase);
                });
                return CommandResult.Success("Built staged sources.");
            });

        var result = await builder.BuildAsync(context, snapshot, CancellationToken.None);

        Assert.Equal(1, buildCalls);
        Assert.Equal(["Built staged sources."], result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotInventoryMismatchFailsBeforeBuild(bool includeUnexpectedSnapshotSource)
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp);
        var alphaPath = CreateSource(context, "Alpha.bas", "Attribute VB_Name = \"Alpha\"\r\n");
        var betaPath = CreateSource(context, "Beta.bas", "Attribute VB_Name = \"Beta\"\r\n");
        var unexpectedPath = Path.Combine(context.DocumentSourceSetPath, "Unexpected.bas");
        var plannedSources = new[] { Source(alphaPath), Source(betaPath) };
        var snapshot = includeUnexpectedSnapshotSource
            ? Snapshot(
                (alphaPath, File.ReadAllText(alphaPath, Encoding.UTF8)),
                (betaPath, File.ReadAllText(betaPath, Encoding.UTF8)),
                (unexpectedPath, "Attribute VB_Name = \"Unexpected\"\r\n"))
            : Snapshot((alphaPath, File.ReadAllText(alphaPath, Encoding.UTF8)));
        var buildCalls = 0;
        var builder = new BuildCommandDebugWorkbookBuilder(
            resolveBuildSources: _ => plannedSources,
            runBuild: (_, _) =>
            {
                buildCalls++;
                return CommandResult.Success(string.Empty);
            });

        var error = await Assert.ThrowsAsync<DebugSetupException>(() =>
            builder.BuildAsync(context, snapshot, CancellationToken.None));

        Assert.Contains("snapshot", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            includeUnexpectedSnapshotSource ? unexpectedPath : betaPath,
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, buildCalls);
    }

    [Fact]
    public async Task SnapshotContentMismatchFailsBeforeBuild()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp);
        var sourcePath = CreateSource(
            context,
            "Module1.bas",
            "Attribute VB_Name = \"Module1\"\r\nPublic Sub CurrentCode()\r\nEnd Sub\r\n");
        var snapshot = Snapshot((
            sourcePath,
            "Attribute VB_Name = \"Module1\"\r\nPublic Sub StaleCode()\r\nEnd Sub\r\n"));
        var buildCalls = 0;
        var builder = new BuildCommandDebugWorkbookBuilder(
            resolveBuildSources: _ => [Source(sourcePath)],
            runBuild: (_, _) =>
            {
                buildCalls++;
                return CommandResult.Success(string.Empty);
            });

        var error = await Assert.ThrowsAsync<DebugSetupException>(() =>
            builder.BuildAsync(context, snapshot, CancellationToken.None));

        Assert.Contains("snapshot", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sourcePath, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, buildCalls);
    }

    [Fact]
    public async Task BuildUsesTheBytesReadForValidationWhenTheOriginalChangesBeforeImport()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp932 = Encoding.GetEncoding(932);
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "Module1.bas");
        Directory.CreateDirectory(context.DocumentSourceSetPath);
        var snapshotText =
            "Attribute VB_Name = \"Module1\"\r\nPublic Sub RunTarget()\r\n    Debug.Print \"保存済み\"\r\nEnd Sub\r\n";
        var capturedBytes = cp932.GetBytes(snapshotText);
        File.WriteAllBytes(sourcePath, capturedBytes);
        var builder = new BuildCommandDebugWorkbookBuilder(
            resolveBuildSources: _ => [Source(sourcePath)],
            runBuild: (_, stagedSources) =>
            {
                File.WriteAllBytes(sourcePath, cp932.GetBytes("changed after validation"));
                var staged = Assert.Single(stagedSources);
                Assert.NotEqual(Path.GetFullPath(sourcePath), Path.GetFullPath(staged.SourcePath));
                Assert.Equal(capturedBytes, File.ReadAllBytes(staged.SourcePath));
                return CommandResult.Success("Built immutable input.");
            });

        var result = await builder.BuildAsync(
            context,
            Snapshot((sourcePath, snapshotText)),
            CancellationToken.None);

        Assert.Equal(["Built immutable input."], result.Output);
    }

    [Fact]
    public async Task FormAndFrxSidecarAreStagedAsOneAdjacentSourceUnit()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp);
        var formText =
            "VERSION 5.00\r\nBegin VB.Form Dialog\r\nEnd\r\nAttribute VB_Name = \"Dialog\"\r\n";
        var formPath = CreateSource(context, "forms/Dialog.frm", formText);
        var frxPath = Path.ChangeExtension(formPath, ".frx");
        var frxBytes = new byte[] { 0x00, 0x7f, 0x80, 0xff };
        File.WriteAllBytes(frxPath, frxBytes);
        var planned = new VbaSourceFile(formPath, VbaSourceKind.Form, frxPath);
        var builder = new BuildCommandDebugWorkbookBuilder(
            resolveBuildSources: _ => [planned],
            runBuild: (_, stagedSources) =>
            {
                var staged = Assert.Single(stagedSources);
                Assert.Equal(VbaSourceKind.Form, staged.Kind);
                Assert.NotNull(staged.BinaryPath);
                Assert.Equal(
                    Path.GetDirectoryName(staged.SourcePath),
                    Path.GetDirectoryName(staged.BinaryPath));
                Assert.Equal(
                    Path.GetFileNameWithoutExtension(staged.SourcePath),
                    Path.GetFileNameWithoutExtension(staged.BinaryPath));
                Assert.Equal(frxBytes, File.ReadAllBytes(staged.BinaryPath!));
                return CommandResult.Success("Built staged form.");
            });

        var result = await builder.BuildAsync(
            context,
            Snapshot((formPath, formText)),
            CancellationToken.None);

        Assert.Equal(["Built staged form."], result.Output);
    }

    [Fact]
    public async Task SuccessfulBuildOutputIsPreservedForDebugLifecycleReporting()
    {
        var builder = new BuildCommandDebugWorkbookBuilder(_ => CommandResult.Success(
            "Built C:\\project\\bin\\Book.xlsm" + Environment.NewLine +
            "WARN Book/Protected reference remains." + Environment.NewLine));

        var result = await builder.BuildAsync(null!, CancellationToken.None);

        Assert.Equal(
            [
                "Built C:\\project\\bin\\Book.xlsm",
                "WARN Book/Protected reference remains."
            ],
            result.Output);
    }

    [Fact]
    public async Task FailedBuildBecomesADebugSetupErrorBeforeVisibleExcelCanStart()
    {
        var builder = new BuildCommandDebugWorkbookBuilder(_ =>
            CommandResult.UsageError("The workbook could not be built."));

        var error = await Assert.ThrowsAsync<DebugSetupException>(() =>
            builder.BuildAsync(null!, CancellationToken.None));

        Assert.Equal("The workbook could not be built.", error.Message);
    }

    private static ResolvedProjectContext CreateContext(TempDirectory temp)
    {
        var root = temp.CreateDirectory("DebugProject");
        var manifest = ProjectManifest.CreateDefault("DebugProject", "Book1", root, null);
        var document = manifest.Documents["Book1"];
        return new ResolvedProjectContext(
            root,
            Path.Combine(root, ProjectManifest.ManifestFileName),
            manifest,
            "Book1",
            document,
            Path.GetFullPath(Path.Combine(root, document.SourcePath)),
            Path.GetFullPath(Path.Combine(root, document.TemplatePath)),
            Path.GetFullPath(Path.Combine(root, document.BinPath)),
            Path.GetFullPath(Path.Combine(root, document.PublishPath)),
            null);
    }

    private static string CreateSource(
        ResolvedProjectContext context,
        string relativePath,
        string text)
    {
        var path = Path.GetFullPath(Path.Combine(context.DocumentSourceSetPath, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static VbaSourceFile Source(string path)
        => new(path, VbaSourceKind.StandardModule, null);

    private static DebugSourceSnapshot Snapshot(
        params (string Path, string Text)[] sources)
        => new(
            DebugSourceSnapshot.CurrentSchemaVersion,
            sources
                .OrderBy(source => source.Path, StringComparer.Ordinal)
                .Select(source => new DebugSourceFileSnapshot(source.Path, source.Text))
                .ToImmutableArray(),
            null);
}
