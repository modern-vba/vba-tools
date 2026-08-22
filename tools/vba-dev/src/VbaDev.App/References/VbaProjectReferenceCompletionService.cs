using VbaDev.App.Projects;
using VbaDev.App.Workbooks;

namespace VbaDev.App.References;

/// <summary>
/// Supplies quiet, shell-neutral reference-name completion candidates.
/// </summary>
public sealed class VbaProjectReferenceCompletionService
{
    private const string VisualBasicForApplications = "Visual Basic For Applications";
    private readonly ProjectContextResolver projectContextResolver;
    private readonly VbaProjectReferencePlanner referencePlanner;

    /// <summary>
    /// Creates the reference completion service.
    /// </summary>
    /// <param name="projectContextResolver">The selected-document context resolver.</param>
    /// <param name="referencePlanner">The registry-backed reference planner.</param>
    public VbaProjectReferenceCompletionService(
        ProjectContextResolver projectContextResolver,
        VbaProjectReferencePlanner referencePlanner)
    {
        this.projectContextResolver = projectContextResolver;
        this.referencePlanner = referencePlanner;
    }

    /// <summary>
    /// Returns registered descriptions not selected by the manifest or current invocation.
    /// </summary>
    /// <param name="request">The project and document selection request.</param>
    /// <param name="suppliedNames">Reference names already supplied to the command.</param>
    /// <returns>Canonical candidates, or an empty list when completion authority is unavailable.</returns>
    public IReadOnlyList<string> CompleteAdd(
        ProjectResolutionRequest request,
        IReadOnlyList<string> suppliedNames)
    {
        try
        {
            var context = projectContextResolver.Resolve(request);
            var excludedNames = context.Document.References
                .Select(reference => reference.Name)
                .Concat(suppliedNames)
                .ToArray();
            var batch = referencePlanner.ResolveAvailableReferences(excludedNames);
            if (!batch.Complete || batch.Diagnostics.Count != 0)
            {
                return [];
            }

            return CanonicalRegisteredNames(batch.References);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Returns selected-document manifest names not already supplied to the invocation.
    /// </summary>
    /// <param name="request">The project and document selection request.</param>
    /// <param name="suppliedNames">Reference names already supplied to the command.</param>
    /// <returns>Canonical manifest candidates, or an empty list when context is unavailable.</returns>
    public IReadOnlyList<string> CompleteRemove(
        ProjectResolutionRequest request,
        IReadOnlyList<string> suppliedNames)
    {
        try
        {
            var context = projectContextResolver.Resolve(request);
            var excludedNames = suppliedNames
                .Select(name => name.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return context.Document.References
                .Select(reference => reference.Name.Trim())
                .Where(name => !name.Equals(
                    VisualBasicForApplications,
                    StringComparison.OrdinalIgnoreCase))
                .Where(name => !excludedNames.Contains(name))
                .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Order(StringComparer.Ordinal).First())
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> CanonicalRegisteredNames(
        IReadOnlyList<VbaProjectReferenceNameResolution> references)
        => references
            .Where(reference => reference.IsRegistered &&
                                reference.Matches.Count > 0 &&
                                !string.IsNullOrWhiteSpace(reference.RegisteredName))
            .Select(reference => reference.RegisteredName!.Trim())
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Order(StringComparer.Ordinal).First())
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(name => name, StringComparer.Ordinal)
            .ToArray();
}
