using System.Runtime.InteropServices;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Diagnostics;

/// <summary>
/// Adds manifest-defined VBA project reference diagnostics.
/// </summary>
public sealed class VbaProjectReferenceDiagnosticProvider : IDoctorProjectDiagnosticProvider
{
    private readonly VbaProjectReferencePlanner referencePlanner;
    private readonly IWorkbookBuildAutomation workbookBuildAutomation;

    /// <summary>
    /// Creates a VBA project reference diagnostic provider.
    /// </summary>
    /// <param name="referencePlanner">The planner used to resolve and diagnose references.</param>
    /// <param name="workbookBuildAutomation">The workbook automation port used to inspect source template references.</param>
    public VbaProjectReferenceDiagnosticProvider(
        VbaProjectReferencePlanner referencePlanner,
        IWorkbookBuildAutomation workbookBuildAutomation)
    {
        this.referencePlanner = referencePlanner;
        this.workbookBuildAutomation = workbookBuildAutomation;
    }

    /// <inheritdoc />
    public void AddDiagnostics(List<DiagnosticResult> results, ResolvedProject project)
    {
        foreach (var (documentName, document) in project.Manifest.Documents.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var consistencyDiagnostic = referencePlanner.CreateManifestReferenceConsistencyDiagnostic(documentName, document);
            if (consistencyDiagnostic is not null)
            {
                results.Add(consistencyDiagnostic);
            }

            results.AddRange(referencePlanner.CreateReferenceCatalogAvailabilityDiagnostics(documentName, document));
            var templateReferences = GetTemplateReferenceNames(results, project, documentName, document);
            foreach (var reference in document.References)
            {
                results.Add(referencePlanner.CreateReferenceResolutionDiagnostic(documentName, reference, templateReferences));
            }
        }
    }

    private IReadOnlySet<string> GetTemplateReferenceNames(
        List<DiagnosticResult> results,
        ResolvedProject project,
        string documentName,
        ProjectDocument document)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (document.References.Count == 0)
        {
            return names;
        }

        var templatePath = project.ResolvePath(document.TemplatePath);
        if (!File.Exists(templatePath))
        {
            return names;
        }

        try
        {
            using var session = workbookBuildAutomation.OpenWorkbook(templatePath);
            foreach (var reference in session.GetReferences())
            {
                names.Add(reference.Name);
            }
        }
        catch (COMException ex)
        {
            AddTemplateInspectionWarning(results, documentName, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            AddTemplateInspectionWarning(results, documentName, ex.Message);
        }
        catch (IOException ex)
        {
            AddTemplateInspectionWarning(results, documentName, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            AddTemplateInspectionWarning(results, documentName, ex.Message);
        }

        return names;
    }

    private static void AddTemplateInspectionWarning(
        List<DiagnosticResult> results,
        string documentName,
        string message)
    {
        results.Add(DiagnosticResult.Warn(
            $"VbaProjectReferences ({documentName})",
            $"Could not inspect source template references: {message}"));
    }
}
