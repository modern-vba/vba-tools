using VbaDev.App.Workbooks;

namespace VbaDev.App.Build;

internal interface IAdmittedWorkbookGenerationSourceInput : IDisposable
{
    AdmittedVbaSourceSet Admission { get; }
}

/// <summary>Retains admitted workbook-output authority and its ordered display provenance.</summary>
internal sealed class AdmittedWorkbookGenerationSourceInput : IAdmittedWorkbookGenerationSourceInput
{
    internal AdmittedWorkbookGenerationSourceInput(AdmittedVbaSourceSet admission)
    {
        Admission = admission;
    }

    public AdmittedVbaSourceSet Admission { get; }

    public void Dispose()
    {
    }
}
