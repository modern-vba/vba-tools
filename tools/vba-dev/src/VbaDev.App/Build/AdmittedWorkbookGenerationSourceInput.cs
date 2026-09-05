using VbaDev.App.Workbooks;

namespace VbaDev.App.Build;

internal interface IAdmittedWorkbookGenerationSourceInput : IWorkbookGenerationSourceInput
{
    AdmittedVbaSourceSet Admission { get; }
}

/// <summary>Retains admitted workbook-output authority and its ordered display provenance.</summary>
internal sealed class AdmittedWorkbookGenerationSourceInput : IAdmittedWorkbookGenerationSourceInput
{
    internal AdmittedWorkbookGenerationSourceInput(AdmittedVbaSourceSet admission)
    {
        Admission = admission;
        SourceFiles = Array.AsReadOnly(admission.Sources
            .Select(source => new VbaSourceFile(source.SourcePath, source.Kind, source.BinaryPath))
            .ToArray());
    }

    public AdmittedVbaSourceSet Admission { get; }

    public IReadOnlyList<VbaSourceFile> SourceFiles { get; }

    public void Dispose()
    {
    }
}
