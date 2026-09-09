using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.App.HostEvents;
using VbaDev.Domain;
using VbaTools.Semantics;
using VbaTools.Syntax;
using VbaTools.TypeLibRegistry;
using System.Runtime.ExceptionServices;
using VbaTools.ProjectMetadata;

namespace VbaDev.App.Build;

/// <summary>Admits the required project evidence before ordinary workbook generation.</summary>
internal sealed class ProjectSemanticInputProvider : IProjectSemanticInputProvider
{
    private readonly VbaProjectReferencePlanner referencePlanner;
    private readonly ITypeLibRegistryCatalogReader registryReader;
    private readonly ITypeLibCatalogMetadataReader metadataReader;
    private readonly IHostEventCatalogAutomation hostEventCatalog;
    private readonly IWorkbookProjectIdentityProbe projectIdentityProbe;

    internal ProjectSemanticInputProvider(VbaProjectReferencePlanner referencePlanner,
        ITypeLibRegistryCatalogReader registryReader, ITypeLibCatalogMetadataReader metadataReader,
        IHostEventCatalogAutomation hostEventCatalog, IWorkbookProjectIdentityProbe projectIdentityProbe)
    {
        this.referencePlanner = referencePlanner;
        this.registryReader = registryReader;
        this.metadataReader = metadataReader;
        this.hostEventCatalog = hostEventCatalog;
        this.projectIdentityProbe = projectIdentityProbe;
    }

    public async Task<VbaProjectSemanticInputs> AcquireAsync(ResolvedProjectContext context,
        CapturedWorkbookTemplate template, IReadOnlyList<VbaSyntaxTree> sources,
        CancellationToken cancellationToken)
    {
        var identity = template.ReadMetadata(cancellationToken);
        if (identity.Metadata is null && identity.Failure?.Kind != VbaProjectPackageMetadataReadFailureKind.VbaProjectAbsent)
        {
            throw new InvalidOperationException(
                $"The containing VBA project identity could not be read from source template '{template.SourcePath}' "
                + $"({identity.Failure!.Kind}): {identity.Failure.Message} "
                + "Restore or re-export a valid supported unencrypted source template.");
        }
        var manifestSelection = VbaProjectReferenceSelection.Create(context.Document.Kind, context.Document.References);
        var selection = VbaReferenceSelection.Capture(manifestSelection.References.Select(reference => reference.Name),
            manifestSelection.MainVbaProjectReference?.Name);
        var requiredNames = new[] { VbaProjectReferenceCatalogSet.StandardLibraryReferenceName }
            .Concat(selection.References.Select(reference => reference.Name)).Distinct(VbaReferenceName.Comparer).ToArray();
        var observed = await projectIdentityProbe.ReadAsync(template, requiredNames, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var projectName = observed.ProjectName;
        if (string.IsNullOrWhiteSpace(projectName))
            throw new InvalidOperationException($"The owned project identity probe returned no project name for '{template.SourcePath}'. Repair or re-export the source template.");
        if (identity.Metadata is { } persisted && !persisted.ProjectName.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The captured source template's persisted project '{persisted.ProjectName}' differs from its observed project '{projectName}'. Repair or re-export the source template.");

        var observedReferences = SelectObservedReferences(observed, requiredNames);
        var missingNames = requiredNames.Where(name => !observedReferences.ContainsKey(name)).ToArray();
        var batch = await referencePlanner.ResolveReferencesAsync(template, missingNames, cancellationToken).ConfigureAwait(false);
        if (batch.OperationalFailure is { } failure)
        {
            if (failure is OperationCanceledException) ExceptionDispatchInfo.Capture(failure).Throw();
            throw new InvalidOperationException($"Required reference metadata acquisition failed: {failure.Message}", failure);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var missingReferences = referencePlanner.SelectManifestInputReferences(batch, missingNames)
            .ToDictionary(reference => reference.Name, VbaReferenceName.Comparer);
        var references = requiredNames.Select(name => observedReferences.TryGetValue(name, out var existing)
            ? new ResolvedVbaProjectReference(name, Guid.Parse(existing.Guid!).ToString(), existing.Major!.Value, existing.Minor!.Value)
            : missingReferences[name]);
        var registry = registryReader.Read();
        if (!registry.Complete)
        {
            throw new InvalidOperationException(registry.Diagnostic?.Message ?? "The required TypeLib registry snapshot is incomplete.");
        }

        var catalogs = VbaProjectReferenceCatalogSet.Empty;
        var identities = new Dictionary<string, VbaProjectReferenceCatalogIdentity>(VbaReferenceName.Comparer);
        var origins = new Dictionary<string, VbaProjectReferenceCatalogSource>(VbaReferenceName.Comparer);
        var namespaces = new List<VbaReferencedProjectNamespace>();
        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            observedReferences.TryGetValue(reference.Name, out var observedReference);
            var acquired = ReadCatalog(registry, reference, observedReference?.FullPath, cancellationToken);
            if (observedReferences.TryGetValue(reference.Name, out var existing)
                && !existing.NamespaceName!.Equals(acquired.Catalog.ReferencedVbaProjectName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Required TypeLib '{reference.Name}' exposes namespace '{acquired.Catalog.ReferencedVbaProjectName}', but the captured workbook exposes '{existing.NamespaceName}'. Repair the source-template reference or registered TypeLib.");
            catalogs = catalogs.WithCatalog(acquired.Catalog);
            identities.Add(reference.Name, acquired.Identity);
            origins.Add(reference.Name, VbaProjectReferenceCatalogSource.Generated);
            namespaces.Add(new(reference.Name, acquired.Catalog.ReferencedVbaProjectName!));
        }

        VbaIntrinsicHostEventCatalog? hostEvents = null;
        if (VbaProjectSourceAnalysis.MayRequireIntrinsicHostEventCatalog(sources))
        {
            var observedEvents = await hostEventCatalog.ReadAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            hostEvents = IntrinsicHostEventCatalogAdmission.CaptureForAnalysis(observedEvents);
        }
        return VbaProjectSemanticInputs.Capture(selection, catalogs, hostEvents,
            identities: identities, sources: origins,
            projectNamespaces: VbaProjectNamespaceIdentity.Capture(projectName, namespaces));
    }

    private static Dictionary<string, WorkbookReference> SelectObservedReferences(
        WorkbookProjectIdentity observed, IReadOnlyList<string> requiredNames)
    {
        var selected = new Dictionary<string, WorkbookReference>(VbaReferenceName.Comparer);
        foreach (var name in requiredNames)
        {
            var matches = observed.References.Where(reference => VbaReferenceName.Comparer.Equals(reference.Name, name)).ToArray();
            if (matches.Length == 0)
            {
                if (VbaReferenceName.Comparer.Equals(name, VbaProjectReferenceCatalogSet.StandardLibraryReferenceName))
                    throw new InvalidOperationException($"The captured workbook did not expose its required '{name}' identity. Repair or re-export the source template.");
                continue;
            }
            if (matches.Length != 1 || !Guid.TryParse(matches[0].Guid, out _)
                || matches[0].Major is not >= 0 || matches[0].Minor is not >= 0
                || string.IsNullOrWhiteSpace(matches[0].NamespaceName))
                throw new InvalidOperationException($"The captured workbook did not expose one complete identity for required reference '{name}'. Repair the source-template reference.");
            selected.Add(name, matches[0]);
        }
        return selected;
    }

    private (VbaProjectReferenceCatalogIdentity Identity, VbaProjectReferenceCatalog Catalog) ReadCatalog(
        TypeLibRegistryCatalog registry, ResolvedVbaProjectReference reference, string? observedPath, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(observedPath))
        {
            AcquiredTypeLibCatalogMetadata acquired;
            try { acquired = metadataReader.ReadMetadataFromPath(reference.Name, observedPath); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw new InvalidOperationException($"Required TypeLib '{reference.Name}' could not be read from observed library '{observedPath}': {error.Message}", error);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var identity = acquired.Identity;
            if (!VbaReferenceName.Comparer.Equals(identity.ReferenceName, reference.Name)
                || !Guid.TryParse(identity.Guid, out var loadedGuid) || loadedGuid != Guid.Parse(reference.Guid)
                || identity.MajorVersion != reference.Major || identity.MinorVersion != reference.Minor
                || !string.Equals(identity.Path, observedPath, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(acquired.Metadata.ReferencedVbaProjectName))
                throw new InvalidOperationException($"Required TypeLib at '{observedPath}' does not match the observed '{reference.Name}' identity ({reference.Guid}, {reference.Major}.{reference.Minor}). Repair the source-template reference or its library file.");
            return (identity, TypeLibReferenceCatalogBuilder.Build(reference.Name, acquired.Metadata));
        }
        var registered = registry.Find(reference.Name);
        var versions = registered?.Lineages.Where(lineage => lineage.Guid.Equals(reference.Guid, StringComparison.OrdinalIgnoreCase))
            .SelectMany(lineage => lineage.Versions)
            .Where(version => version.Major == reference.Major && version.Minor == reference.Minor).ToArray() ?? [];
        if (versions.Length != 1)
        {
            throw new InvalidOperationException($"Required TypeLib identity '{reference.Name}' ({reference.Guid}, {reference.Major}.{reference.Minor}) "
                + "is not uniquely present in the captured registry. Refresh or repair the registered reference.");
        }

        var errors = new List<Exception>();
        foreach (var location in versions[0].GetOrderedLocations())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = new VbaProjectReferenceCatalogIdentity(reference.Name, reference.Guid,
                reference.Major, reference.Minor, location.Lcid, location.Path);
            try
            {
                var metadata = metadataReader.ReadMetadata(identity);
                if (string.IsNullOrEmpty(metadata.ReferencedVbaProjectName))
                {
                    throw new InvalidOperationException("The loaded TypeLib did not supply an authoritative referenced VBA project name.");
                }
                return (identity, TypeLibReferenceCatalogBuilder.Build(reference.Name, metadata));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                errors.Add(error);
            }
        }

        throw new InvalidOperationException($"Required TypeLib metadata for '{reference.Name}' could not be read from its captured registered locations. "
            + string.Join(" ", errors.Select(error => error.Message).Distinct(StringComparer.Ordinal)),
            errors.Count switch { 0 => null, 1 => errors[0], _ => new AggregateException(errors) });
    }
}
