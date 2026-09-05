using VbaDev.App.Projects;

namespace VbaDev.App.Build;

internal abstract class WorkbookMaterializationIntent
{
    private WorkbookMaterializationIntent()
    {
    }

    internal sealed class ProjectBuild(ResolvedProjectContext context)
        : WorkbookMaterializationIntent
    {
        internal ResolvedProjectContext Context { get; } = context;
    }

    internal sealed class Publish(ResolvedProjectContext context)
        : WorkbookMaterializationIntent
    {
        internal ResolvedProjectContext Context { get; } = context;
    }
}

internal sealed record WorkbookMaterializationResult(
    string CommittedArtifactPath,
    int ImportedSourceCount,
    IReadOnlyList<string> Warnings,
    VbaDev.App.Workbooks.VbeImportVerificationReport VerificationReport);
