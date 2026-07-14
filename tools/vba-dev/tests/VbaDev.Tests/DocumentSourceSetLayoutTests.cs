using System.Text;
using VbaDev.App.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class DocumentSourceSetLayoutTests
{
    [Fact]
    public void SourceIdentityFindsCaseInsensitiveFlatFileNameCollisions()
    {
        using var temp = TempDirectory.Create();
        var sourceSet = temp.CreateDirectory("src");
        WriteText(Path.Combine(sourceSet, "root", "Feature.bas"), "root");
        WriteText(Path.Combine(sourceSet, "nested", "feature.bas"), "nested");

        var sourceFiles = DocumentSourceSetLayout.EnumerateVbaSourceFiles(sourceSet);
        var error = Assert.Throws<InvalidOperationException>(
            () => DocumentSourceSetLayout.ThrowIfDuplicateSourceFileNames(sourceSet, sourceFiles));

        Assert.Contains("Duplicate VBA source file names", error.Message, StringComparison.Ordinal);
        Assert.Contains("Feature.bas", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RestoreExportedSourceLayoutKeepsExistingFormSidecarPlacement()
    {
        using var temp = TempDirectory.Create();
        var destination = temp.CreateDirectory("destination");
        var temporary = temp.CreateDirectory("temporary");
        WriteText(Path.Combine(destination, "forms", "Dialog.frm"), "old form");
        WriteBytes(Path.Combine(destination, "forms", "Dialog.frx"), [9, 9, 9]);
        WriteText(Path.Combine(temporary, "Dialog.frm"), "new form");
        WriteBytes(Path.Combine(temporary, "Dialog.frx"), [1, 2, 3]);

        var existingLayout = DocumentSourceSetLayout.CaptureExistingSourceLayout(destination);
        DocumentSourceSetLayout.DeleteVbaSourceAndSidecars(destination);
        DocumentSourceSetLayout.RestoreExportedSourceLayout(temporary, destination, existingLayout);

        Assert.Equal("new form", File.ReadAllText(Path.Combine(destination, "forms", "Dialog.frm"), Encoding.UTF8));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(destination, "forms", "Dialog.frx")));
        Assert.False(File.Exists(Path.Combine(destination, "Dialog.frx")));
    }

    [Fact]
    public void SourceIdentityDiagnosticsReportCollisionsAndDisplacedSidecars()
    {
        using var temp = TempDirectory.Create();
        var sourceSet = temp.CreateDirectory("src");
        WriteText(Path.Combine(sourceSet, "first", "Feature.bas"), "first");
        WriteText(Path.Combine(sourceSet, "second", "feature.bas"), "second");
        WriteText(Path.Combine(sourceSet, "forms", "Dialog.frm"), "form");
        WriteBytes(Path.Combine(sourceSet, "legacy", "Dialog.frx"), [1, 2, 3]);
        WriteBytes(Path.Combine(sourceSet, "Orphan.frx"), [9, 9, 9]);

        var diagnostics = DocumentSourceSetLayout.InspectSourceIdentity("Book1", sourceSet);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Status == DocumentSourceSetLayoutDiagnosticStatus.Fail &&
            diagnostic.Name.Equals("Document source identity (Book1/Feature.bas)", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Status == DocumentSourceSetLayoutDiagnosticStatus.Warn &&
            diagnostic.Name.Equals("Form sidecar (Book1/Dialog.frx)", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Message.Contains("Orphan.frx", StringComparison.Ordinal));
    }

    private static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static void WriteBytes(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }
}
