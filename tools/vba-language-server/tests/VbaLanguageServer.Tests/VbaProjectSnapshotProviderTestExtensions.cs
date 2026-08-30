using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Tests;

internal static class VbaProjectSnapshotProviderTestExtensions
{
    internal static VbaProjectSnapshot CreateProjectSnapshot(
        this VbaProjectSnapshotProvider provider,
        string activeUri,
        VbaWorkspaceSnapshotState workspaceState,
        CancellationToken cancellationToken)
        => provider.CreateProjectSnapshot(
            Identify(activeUri),
            workspaceState,
            cancellationToken);

    private static VbaIdentifiedDocument Identify(string uri)
        => VbaProjectIdentityModel.TryIdentifyDocument(uri, out var identity)
            ? new VbaIdentifiedDocument(identity, uri)
            : throw new InvalidOperationException(
                "A project snapshot test URI must have a typed identity.");
}
