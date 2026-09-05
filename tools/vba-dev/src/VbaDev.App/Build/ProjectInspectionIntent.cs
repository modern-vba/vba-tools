using VbaDev.App.Projects;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Build;

internal sealed record ProjectInspectionIntent(
    ResolvedProjectContext Context,
    CapturedDoctorSourceSet SourceCapture);

internal enum ProjectInspectionProfile
{
    Build,
    Publish
}

internal enum ProjectInspectionStatus
{
    Pass,
    Fail,
    Unverified,
    Skip
}

internal sealed record ProjectInspectionProfileResult(
    ProjectInspectionProfile Profile,
    ProjectInspectionStatus Status,
    string Message);

internal sealed record ProjectInspectionResult(
    IReadOnlyList<ProjectInspectionProfileResult> Profiles,
    bool Complete = true,
    bool Canceled = false);
