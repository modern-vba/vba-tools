using System.Text.Json;
using VbaDebugAdapter.Build;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class VbaDevSourceWorkbookResolverTests
{
    [Fact]
    public async Task EmptyDescriptionOwnerEvidenceIsReportedAsUnprovedChildRelease()
    {
        using var temp = TempDirectory.Create();
        var project = Path.Combine(temp.Path, "Project");
        Directory.CreateDirectory(project);
        var executable = Path.Combine(temp.Path, "vba-dev.exe");
        File.WriteAllBytes(executable, []);
        var process = new DescriptionProcess(new(project, "Book",
            Path.Combine(project, "src", "Book", "Book.xlsm"))) { ReportEmptyCleanup = true };

        var failure = await Assert.ThrowsAsync<DebugFailureException>(() =>
            new VbaDevSourceWorkbookResolver(process).ResolveAsync(
                executable, project, "Book", CancellationToken.None));

        Assert.True(failure.FailureOutcome.HasUnprovedRelease);
        Assert.Contains(failure.FailureOutcome.Evidence,
            item => item.Kind == DebugResourceKind.Process && !item.Released);
        Assert.Contains(failure.FailureOutcome.Evidence,
            item => item.Kind == DebugResourceKind.Handle && !item.Released);
    }

    [Fact]
    public async Task ResolvesTheSelectedSourceViaTheReadOnlyPublicCommand()
    {
        using var temp = TempDirectory.Create();
        var project = Path.Combine(temp.Path, "Project");
        Directory.CreateDirectory(project);
        var workbook = Path.Combine(project, "src", "Book", "Book.xlsm");
        var executable = Path.Combine(temp.Path, "vba-dev.exe");
        File.WriteAllBytes(executable, []);
        var process = new DescriptionProcess(new(project, "Book", workbook));

        var description = await new VbaDevSourceWorkbookResolver(process).ResolveAsync(
            executable, project, "Book", CancellationToken.None);

        Assert.Equal(new(project, "Book", workbook), description);
        Assert.Equal(executable, process.Executable);
        Assert.Equal(new[] { "prepare-debug", "--describe", "--project", project,
            "--document", "Book" }, process.Arguments);
        Assert.DoesNotContain("build", process.Arguments!);
        Assert.DoesNotContain("--output", process.Arguments!);
    }

    private sealed class DescriptionProcess(DebugSourceWorkbookDescription description)
        : IVbaDevBuildProcess
    {
        internal string? Executable { get; private set; }
        internal IReadOnlyList<string>? Arguments { get; private set; }
        internal bool ReportEmptyCleanup { get; init; }
        public Task<VbaDevBuildProcessResult> RunAsync(string fileName,
            IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Executable = fileName;
            Arguments = arguments;
            var completion = new DebugFailureCompletion();
            if (!ReportEmptyCleanup)
            {
                completion.AddEvidence(new("companion-exit", "description", DebugResourceKind.Process,
                    true, "The read-only fixture process exited."));
                completion.AddEvidence(new("companion-handles", "description", DebugResourceKind.Handle,
                    true, "The fixture released its invocation handles."));
            }
            return Task.FromResult(new VbaDevBuildProcessResult(0, JsonSerializer.Serialize(new
            {
                type = "debugWorkbookDescription", schemaVersion = "1.0",
                projectRoot = description.ProjectRoot, documentName = description.DocumentName,
                workbookPath = description.WorkbookPath
            }), "") { CleanupOutcome = completion.Complete() });
        }
    }
}
