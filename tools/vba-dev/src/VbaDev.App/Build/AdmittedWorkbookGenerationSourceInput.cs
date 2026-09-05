using VbaDev.App.Workbooks;

namespace VbaDev.App.Build;

/// <summary>Retains the admitted Build authority and its ordered display provenance.</summary>
internal sealed class AdmittedWorkbookGenerationSourceInput : IWorkbookGenerationSourceInput
{
    internal AdmittedWorkbookGenerationSourceInput(AdmittedVbaSourceSet admission)
    {
        Admission = admission;
        SourceFiles = Array.AsReadOnly(admission.Sources
            .Select(source => new VbaSourceFile(source.SourcePath, source.Kind, source.BinaryPath))
            .ToArray());
    }

    internal AdmittedVbaSourceSet Admission { get; }

    public IReadOnlyList<VbaSourceFile> SourceFiles { get; }

    public void Dispose()
    {
    }
}
