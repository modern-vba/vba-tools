using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Tests;

internal static class VbaProjectManifestReconciliationTestExtensions
{
    internal static VbaProjectManifestBarrierSnapshot CaptureScopeBarriers(
        this VbaProjectManifestWorkspace workspace,
        string activeUri,
        VbaProjectResolution resolution)
        => workspace.CaptureScopeBarriers(
            Identify(activeUri),
            resolution);

    internal static VbaProjectManifestBarrierSnapshot
        CaptureDiskReconciliationBarriers(
            this VbaProjectManifestWorkspace workspace,
            string activeUri,
            VbaProjectResolution resolution)
        => workspace.CaptureDiskReconciliationBarriers(
            Identify(activeUri),
            resolution);

    internal static long GetReconciliationRevision(
        this VbaProjectManifestWorkspace workspace,
        string uri)
        => workspace.GetReconciliationRevision(Identify(uri));

    internal static VbaProjectDiskManifestBaseline GetReconciliationBaseline(
        this VbaProjectManifestWorkspace workspace,
        string uri)
        => workspace.GetReconciliationBaseline(Identify(uri));

    internal static VbaProjectManifestReconciliationCapture
        CaptureReconciliationState(
            this VbaProjectManifestWorkspace workspace,
            string uri)
        => workspace.CaptureReconciliationState(Identify(uri));

    internal static VbaProjectManifestReconciliationUpdate
        ReloadReconciledManifest(
            this VbaProjectManifestWorkspace workspace,
            string uri,
            string text,
            long capturedRevision)
        => workspace.ReloadReconciledManifest(
            Identify(uri),
            text,
            capturedRevision);

    internal static VbaProjectManifestReconciliationUpdate
        DeleteReconciledManifest(
            this VbaProjectManifestWorkspace workspace,
            string uri,
            long capturedRevision)
        => workspace.DeleteReconciledManifest(
            Identify(uri),
            capturedRevision);

    internal static VbaProjectManifestReconciliationTarget
        CreateReconciliationTarget(
            string uri,
            long capturedRevision)
        => new(Identify(uri), capturedRevision);

    private static VbaIdentifiedDocument Identify(string uri)
        => VbaProjectIdentityModel.TryIdentifyDocument(uri, out var identity)
            ? new VbaIdentifiedDocument(identity, uri)
            : throw new InvalidOperationException(
                "A manifest reconciliation test URI must have a typed identity.");
}
