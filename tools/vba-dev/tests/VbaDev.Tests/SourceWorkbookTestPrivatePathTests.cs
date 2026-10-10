using System.Text;
using Xunit;

namespace VbaDev.Tests;

public sealed class SourceWorkbookTestPrivatePathTests
{
    [Fact]
    public async Task SnapshotImportFailureRedactsItsActualPrivateMirrorWithoutLosingCallerProvenance()
    {
        using var fixture = new SourceTestFixture();
        using var temp = TempDirectory.Create();
        var snapshot = temp.CreateDirectory("caller-snapshot");
        var callerSource = Path.Combine(snapshot, "Local.bas");
        File.Copy(fixture.SourcePath, callerSource);
        var callerBytes = File.ReadAllBytes(callerSource);
        var workbookBytes = File.ReadAllBytes(fixture.WorkbookPath);
        string? privateMirror = null;
        string? privateMirrorUri = null;
        fixture.Automation.Session.BeforeImportSource = source =>
        {
            privateMirror = source.SourcePath;
            privateMirrorUri = new Uri(source.SourcePath).AbsoluteUri;
            throw new IOException(
                $"Import of module '{source.ImportVerification.ComponentName}' failed for caller source '{source.DiagnosticSourcePath}' ({new Uri(source.DiagnosticSourcePath).AbsoluteUri}). Private mirror: {privateMirror}. Private URI: {privateMirrorUri}.");
        };

        var result = await fixture.Application.RunAsync(
            ["test", "--source-snapshot", snapshot, "--format", "ndjson", "--interactive", "false"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.NotNull(privateMirror);
        Assert.NotNull(privateMirrorUri);
        Assert.DoesNotContain(privateMirror, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(privateMirrorUri, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vba-dev-vbe-import", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Import of module 'Local' failed", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(callerSource, result.StandardError, StringComparison.Ordinal);
        Assert.Contains(new Uri(callerSource).AbsoluteUri, result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("retained", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("test", fixture.Automation.Session.Events);
        Assert.DoesNotContain("save", fixture.Automation.Session.Events);
        Assert.False(File.Exists(privateMirror));
        Assert.Equal(callerBytes, File.ReadAllBytes(callerSource));
        Assert.Equal(workbookBytes, File.ReadAllBytes(fixture.WorkbookPath));
    }

    [Fact]
    public async Task SnapshotImportFailureKeepsTheActualRetainedMirrorPathInItsCleanupWarning()
    {
        using var fixture = new SourceTestFixture();
        using var temp = TempDirectory.Create();
        var snapshot = temp.CreateDirectory("caller-snapshot");
        var callerSource = Path.Combine(snapshot, "Local.bas");
        File.Copy(fixture.SourcePath, callerSource);
        var callerBytes = File.ReadAllBytes(callerSource);
        var workbookBytes = File.ReadAllBytes(fixture.WorkbookPath);
        string? privateMirror = null;
        string? privateMirrorUri = null;
        fixture.Automation.Session.BeforeImportSource = source =>
        {
            privateMirror = source.SourcePath;
            privateMirrorUri = new Uri(source.SourcePath).AbsoluteUri;
            File.AppendAllText(privateMirror, "\n' Foreign bytes introduced by the test.\n", Encoding.UTF8);
            throw new IOException(
                $"Import of module '{source.ImportVerification.ComponentName}' failed for caller source '{source.DiagnosticSourcePath}'. Private mirror: {privateMirror}. Private URI: {privateMirrorUri}.");
        };

        try
        {
            var result = await fixture.Application.RunAsync(
                ["test", "--source-snapshot", snapshot, "--format", "ndjson", "--interactive", "false"]);

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.NotNull(privateMirror);
            Assert.NotNull(privateMirrorUri);
            var warningIndex = result.StandardError.IndexOf(
                "Warning: Test staging cleanup failed; inspect retained paths:", StringComparison.Ordinal);
            Assert.True(warningIndex > 0, result.StandardError);
            var operationText = result.StandardError[..warningIndex];
            var cleanupWarning = result.StandardError[warningIndex..];
            Assert.DoesNotContain(privateMirror, operationText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(privateMirrorUri, operationText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Import of module 'Local' failed", operationText, StringComparison.Ordinal);
            Assert.Contains(callerSource, operationText, StringComparison.Ordinal);
            Assert.Contains("Retained paths:", cleanupWarning, StringComparison.Ordinal);
            Assert.Contains(privateMirror, cleanupWarning, StringComparison.Ordinal);
            Assert.Contains(Path.GetDirectoryName(privateMirror)!, cleanupWarning, StringComparison.Ordinal);
            Assert.True(File.Exists(privateMirror));
            Assert.DoesNotContain("test", fixture.Automation.Session.Events);
            Assert.DoesNotContain("save", fixture.Automation.Session.Events);
            Assert.Equal(callerBytes, File.ReadAllBytes(callerSource));
            Assert.Equal(workbookBytes, File.ReadAllBytes(fixture.WorkbookPath));
        }
        finally
        {
            if (privateMirror is not null) File.Delete(privateMirror);
        }
    }
}
