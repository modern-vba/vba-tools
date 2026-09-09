using VbaDev.App.Workbooks;

namespace VbaDev.App.Build;

internal interface IAdmittedWorkbookGenerationSourceInput : IDisposable
{
    AdmittedVbaSourceSet Admission { get; }
}

/// <summary>Retains admitted workbook-output authority and its ordered display provenance.</summary>
internal sealed class AdmittedWorkbookGenerationSourceInput : IAdmittedWorkbookGenerationSourceInput
{
    private readonly IDisposable? sourceOwner;

    internal AdmittedWorkbookGenerationSourceInput(AdmittedVbaSourceSet admission, IDisposable? sourceOwner = null)
    {
        Admission = admission;
        this.sourceOwner = sourceOwner;
    }

    public AdmittedVbaSourceSet Admission { get; }

    public void Dispose()
    {
        sourceOwner?.Dispose();
    }
}
