using System.Collections.Immutable;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Build;

/// <summary>Reads actual project and reference identities from an owned captured-template copy.</summary>
public interface IWorkbookProjectIdentityProbe
{
    Task<WorkbookProjectIdentity> ReadAsync(CapturedWorkbookTemplate template,
        IReadOnlyList<string> requiredReferenceNames, CancellationToken cancellationToken);
}

/// <summary>Immutable evidence observed before importing or normalizing any source or reference.</summary>
public sealed class WorkbookProjectIdentity(string projectName, IReadOnlyList<WorkbookReference> references)
{
    public string ProjectName { get; } = projectName;
    public ImmutableArray<WorkbookReference> References { get; } = references.ToImmutableArray();
}
