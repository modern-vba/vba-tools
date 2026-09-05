using System.Runtime.ExceptionServices;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Diagnostics;

/// <summary>Retains one Doctor run's document source evidence.</summary>
public sealed class DoctorProjectSourceInspection
{
    private readonly IReadOnlyDictionary<string, CapturedDoctorSourceSet> documents;

    private DoctorProjectSourceInspection(
        IReadOnlyDictionary<string, CapturedDoctorSourceSet> documents)
    {
        this.documents = documents;
    }

    internal static DoctorProjectSourceInspection Capture(
        ResolvedProject project,
        VbaSourceAdmission admission,
        CancellationToken cancellationToken)
    {
        var run = admission.BeginDoctorRun(cancellationToken);
        var documents = new Dictionary<string, CapturedDoctorSourceSet>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, document) in project.Manifest.Documents.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            documents.Add(name, run.CaptureDocument(project.ResolvePath(document.SourcePath), cancellationToken));
        }

        return new DoctorProjectSourceInspection(documents);
    }

    internal CapturedDoctorSourceSet GetDocument(string documentName) => documents[documentName];

    internal IReadOnlyList<string> GetInventory(string documentName)
    {
        var document = GetDocument(documentName);
        if (document.SourceDirectoryExists && document.CaptureFailure is { } failure)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return document.InventoryPaths;
    }
}

internal interface IDoctorSourceDiagnosticProvider
{
    void AddDiagnostics(
        List<DiagnosticResult> results,
        ResolvedProject project,
        DoctorProjectSourceInspection sources);
}
