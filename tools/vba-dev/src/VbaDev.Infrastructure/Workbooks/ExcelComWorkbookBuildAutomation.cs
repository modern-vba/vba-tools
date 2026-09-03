using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using VbaDev.App.Workbooks;
using VbaDev.App.Testing;

namespace VbaDev.Infrastructure.Workbooks;

/// <summary>
/// Implements workbook build automation through Excel COM and VBIDE.
/// </summary>
public sealed partial class ExcelComWorkbookBuildAutomation : IWorkbookBuildAutomation
{
    private const int VbextComponentTypeStandardModule = 1;
    private const int VbextComponentTypeClassModule = 2;
    private const int VbextComponentTypeForm = 3;
    private const int VbextComponentTypeDocument = 100;

    /// <summary>
    /// Opens an Excel workbook for VBA project build operations.
    /// </summary>
    /// <param name="workbookPath">The workbook path to open.</param>
    /// <returns>An Excel COM-backed workbook build session.</returns>
    public IWorkbookBuildSession OpenWorkbook(string workbookPath)
        => OpenWorkbook(workbookPath, CancellationToken.None);

    /// <summary>
    /// Opens an Excel workbook in a strongly owned, cancellable build process.
    /// </summary>
    public IWorkbookBuildSession OpenWorkbook(
        string workbookPath,
        CancellationToken cancellationToken)
        => new ExcelComWorkbookBuildSession(ExcelComWorkbookSession.OpenOwnedForBuild(
            workbookPath,
            cancellationToken));

    private sealed class ExcelComWorkbookBuildSession :
        IWorkbookBuildSession,
        IExcelComWorkbookTestSession
    {
        private const int TypeLibNotRegistered = unchecked((int)0x8002801D);
        private readonly ExcelComWorkbookSession session;
        private readonly List<(VbeImportVerification Expected, string ImportedComponentName)>
            pendingImportVerifications = [];

        /// <summary>
        /// Initializes a build session over an Excel application and workbook COM object.
        /// </summary>
        /// <param name="session">The Excel COM workbook session.</param>
        public ExcelComWorkbookBuildSession(ExcelComWorkbookSession session)
        {
            this.session = session;
        }

        /// <summary>
        /// Reads the actual VBA project name adopted by the open workbook.
        /// </summary>
        public string GetProjectName()
        {
            dynamic workbook = session.WorkbookObject;
            object? vbProjectObject = null;
            try
            {
                vbProjectObject = workbook.VBProject;
                dynamic vbProject = vbProjectObject;
                return (string)vbProject.Name;
            }
            finally
            {
                ComObjectReleaser.Release(vbProjectObject);
            }
        }

        /// <summary>
        /// Reads the VBA components currently present in the workbook.
        /// </summary>
        /// <returns>The workbook module descriptors.</returns>
        public IReadOnlyList<WorkbookModule> GetModules()
        {
            dynamic workbook = session.WorkbookObject;
            object? vbProjectObject = null;
            object? componentsObject = null;
            var modules = new List<WorkbookModule>();
            try
            {
                vbProjectObject = workbook.VBProject;
                dynamic vbProject = vbProjectObject;
                componentsObject = vbProject.VBComponents;
                dynamic components = componentsObject;
                var componentCount = (int)components.Count;
                for (var index = 1; index <= componentCount; index++)
                {
                    object? componentObject = null;
                    try
                    {
                        componentObject = components.Item(index);
                        dynamic component = componentObject;
                        modules.Add(new WorkbookModule((string)component.Name, MapComponentType((int)component.Type)));
                    }
                    finally
                    {
                        ComObjectReleaser.Release(componentObject);
                    }
                }
            }
            finally
            {
                ComObjectReleaser.Release(componentsObject);
                ComObjectReleaser.Release(vbProjectObject);
            }

            return modules;
        }

        /// <summary>
        /// Exports one VBA component from the open workbook.
        /// </summary>
        public void ExportModule(string moduleName, string destinationPath)
        {
            dynamic workbook = session.WorkbookObject;
            object? vbProjectObject = null;
            object? componentsObject = null;
            object? componentObject = null;
            try
            {
                vbProjectObject = workbook.VBProject;
                dynamic vbProject = vbProjectObject;
                componentsObject = vbProject.VBComponents;
                dynamic components = componentsObject;
                componentObject = components.Item(moduleName);
                dynamic component = componentObject;
                component.Export(destinationPath);
            }
            finally
            {
                ComObjectReleaser.Release(componentObject);
                ComObjectReleaser.Release(componentsObject);
                ComObjectReleaser.Release(vbProjectObject);
            }
        }

        /// <summary>
        /// Reads the workbook's VBA project references.
        /// </summary>
        /// <returns>The reference names and whether each reference can be removed.</returns>
        public IReadOnlyList<WorkbookReference> GetReferences()
        {
            dynamic workbook = session.WorkbookObject;
            object? vbProjectObject = null;
            object? referencesObject = null;
            var result = new List<WorkbookReference>();
            try
            {
                vbProjectObject = workbook.VBProject;
                dynamic vbProject = vbProjectObject;
                referencesObject = vbProject.References;
                dynamic references = referencesObject;
                var referenceCount = (int)references.Count;
                for (var index = 1; index <= referenceCount; index++)
                {
                    object? referenceObject = null;
                    try
                    {
                        referenceObject = references.Item(index);
                        dynamic reference = referenceObject;
                        var description = (string?)reference.Description;
                        var namespaceName = (string?)reference.Name;
                        var isBuiltIn = (bool)reference.BuiltIn;
                        var humanVisibleName = !string.IsNullOrWhiteSpace(description)
                            ? description.Trim()
                            : !string.IsNullOrWhiteSpace(namespaceName)
                                ? namespaceName
                                : $"Reference #{index}";
                        result.Add(new WorkbookReference(
                            humanVisibleName,
                            IsRemovable: !isBuiltIn,
                            NamespaceName: namespaceName));
                    }
                    finally
                    {
                        ComObjectReleaser.Release(referenceObject);
                    }
                }
            }
            finally
            {
                ComObjectReleaser.Release(referencesObject);
                ComObjectReleaser.Release(vbProjectObject);
            }

            return result;
        }

        /// <summary>
        /// Removes a matching non-built-in VBA project reference from the workbook.
        /// </summary>
        /// <param name="referenceName">The reference description to remove.</param>
        /// <returns><see langword="true"/> when a reference was removed; otherwise, <see langword="false"/>.</returns>
        public bool RemoveReference(string referenceName)
        {
            dynamic workbook = session.WorkbookObject;
            object? vbProjectObject = null;
            object? referencesObject = null;
            try
            {
                vbProjectObject = workbook.VBProject;
                dynamic vbProject = vbProjectObject;
                referencesObject = vbProject.References;
                dynamic references = referencesObject;
                var referenceCount = (int)references.Count;
                for (var index = 1; index <= referenceCount; index++)
                {
                    object? referenceObject = null;
                    try
                    {
                        referenceObject = references.Item(index);
                        dynamic reference = referenceObject;
                        var description = (string)reference.Description;
                        if (!referenceName.Equals(description, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if ((bool)reference.BuiltIn)
                        {
                            return false;
                        }

                        references.Remove(referenceObject);
                        return true;
                    }
                    finally
                    {
                        ComObjectReleaser.Release(referenceObject);
                    }
                }

                return false;
            }
            finally
            {
                ComObjectReleaser.Release(referencesObject);
                ComObjectReleaser.Release(vbProjectObject);
            }
        }

        /// <summary>
        /// Adds a VBA project reference to the workbook from a resolved type library selection.
        /// </summary>
        /// <param name="reference">The resolved reference to add.</param>
        public void AddReference(ResolvedVbaProjectReference reference)
        {
            dynamic workbook = session.WorkbookObject;
            object? vbProjectObject = null;
            object? referencesObject = null;
            object? referenceObject = null;
            try
            {
                vbProjectObject = workbook.VBProject;
                dynamic vbProject = vbProjectObject;
                referencesObject = vbProject.References;
                dynamic references = referencesObject;
                referenceObject = references.AddFromGuid(
                    Guid.Parse(reference.Guid).ToString("B"),
                    reference.Major,
                    reference.Minor);
            }
            finally
            {
                ComObjectReleaser.Release(referenceObject);
                ComObjectReleaser.Release(referencesObject);
                ComObjectReleaser.Release(vbProjectObject);
            }
        }

        /// <summary>
        /// Probes one reference candidate against the current in-memory workbook state and rolls it back.
        /// </summary>
        public VbaProjectReferenceProbeAttemptResult TryResolveReference(
            string referenceName,
            ResolvedVbaProjectReference candidate)
        {
            dynamic workbook = session.WorkbookObject;
            object? vbProjectObject = null;
            object? referencesObject = null;
            object? existingReferenceObject = null;
            object? candidateReferenceObject = null;
            IReadOnlyList<ReferenceIdentity>? baselineIdentities = null;
            ReferenceIdentity? candidateIdentity = null;
            VbaProjectReferenceProbeAttemptResult? result = null;
            Exception? operationError = null;
            try
            {
                vbProjectObject = workbook.VBProject;
                dynamic vbProject = vbProjectObject;
                referencesObject = vbProject.References;
                dynamic references = referencesObject;
                try
                {
                    baselineIdentities = ReadReferenceIdentities(references);
                }
                catch (Exception exception)
                {
                    throw new VbaProjectReferenceProbeAttemptException(
                        "identityReadFailure",
                        "The active reference identities could not be read before probing.",
                        processTrusted: true,
                        exception);
                }

                try
                {
                    existingReferenceObject = FindReference(references, referenceName);
                    if (existingReferenceObject is not null)
                    {
                        result = VbaProjectReferenceProbeAttemptResult.Accepted(
                            ReadReferenceIdentity(existingReferenceObject, referenceName));
                    }
                }
                catch (Exception exception)
                {
                    throw new VbaProjectReferenceProbeAttemptException(
                        "identityReadFailure",
                        "The active reference identity could not be inspected before probing.",
                        processTrusted: true,
                        exception);
                }

                if (existingReferenceObject is null)
                {
                    try
                    {
                        candidateReferenceObject = references.AddFromGuid(
                            Guid.Parse(candidate.Guid).ToString("B"),
                            candidate.Major,
                            candidate.Minor);
                    }
                    catch (COMException exception)
                        when (exception.HResult == TypeLibNotRegistered)
                    {
                        result = VbaProjectReferenceProbeAttemptResult.Rejected();
                    }
                    catch (Exception exception)
                    {
                        throw new VbaProjectReferenceProbeAttemptException(
                            "excelVbeFailure",
                            $"VBE could not probe reference '{referenceName}'.",
                            processTrusted: false,
                            exception);
                    }

                    if (result is null)
                    {
                        try
                        {
                            candidateIdentity = ReadReferenceIdentityValue(
                                candidateReferenceObject!);
                            result = VbaProjectReferenceProbeAttemptResult.Accepted(
                                CreateResolvedReference(
                                    candidateIdentity,
                                    referenceName));
                        }
                        catch (Exception exception)
                        {
                            throw new VbaProjectReferenceProbeAttemptException(
                                "identityReadFailure",
                                "The concrete identity returned by VBE could not be read.",
                                processTrusted: true,
                                exception);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                operationError = exception is VbaProjectReferenceProbeAttemptException
                    ? exception
                    : new VbaProjectReferenceProbeAttemptException(
                        "excelVbeFailure",
                        $"VBE could not probe reference '{referenceName}'.",
                        processTrusted: false,
                        exception);
            }

            var cleanupErrors = new List<Exception>();
            if (baselineIdentities is not null && referencesObject is not null)
            {
                try
                {
                    dynamic references = referencesObject;
                    if (candidateReferenceObject is not null &&
                        candidateIdentity is not null &&
                        !baselineIdentities.Contains(candidateIdentity))
                    {
                        references.Remove(candidateReferenceObject);
                    }
                }
                catch (Exception exception)
                {
                    cleanupErrors.Add(exception);
                }
            }

            TryRelease(candidateReferenceObject, cleanupErrors);
            TryRelease(existingReferenceObject, cleanupErrors);
            candidateReferenceObject = null;
            existingReferenceObject = null;

            if (baselineIdentities is not null && referencesObject is not null)
            {
                try
                {
                    dynamic references = referencesObject;
                    var finalIdentities = ReadReferenceIdentities(references);
                    if (!ReferenceIdentitiesEqual(
                            baselineIdentities,
                            finalIdentities))
                    {
                        throw new InvalidOperationException(
                            "The active reference inventory changed during an in-session ambiguity probe.");
                    }
                }
                catch (Exception exception)
                {
                    cleanupErrors.Add(exception);
                }
            }

            TryRelease(referencesObject, cleanupErrors);
            TryRelease(vbProjectObject, cleanupErrors);

            if (cleanupErrors.Count > 0)
            {
                throw new VbaProjectReferenceProbeAttemptException(
                    "cleanupFailure",
                    "The in-session reference probe could not restore the cleaned workbook state.",
                    processTrusted: false,
                    operationError is null
                        ? new AggregateException(cleanupErrors)
                        : new AggregateException([operationError, .. cleanupErrors]));
            }

            if (operationError is not null)
            {
                ExceptionDispatchInfo.Capture(operationError).Throw();
            }

            return result
                ?? throw new InvalidOperationException(
                    "The in-session reference probe returned no outcome.");
        }

        /// <summary>
        /// Removes a VBA component from the workbook by module name.
        /// </summary>
        /// <param name="moduleName">The module name to remove.</param>
        public void RemoveModule(string moduleName)
        {
            dynamic workbook = session.WorkbookObject;
            object? vbProjectObject = null;
            object? componentsObject = null;
            object? componentObject = null;
            try
            {
                vbProjectObject = workbook.VBProject;
                dynamic vbProject = vbProjectObject;
                componentsObject = vbProject.VBComponents;
                dynamic components = componentsObject;
                componentObject = components.Item(moduleName);
                components.Remove(componentObject);
            }
            finally
            {
                ComObjectReleaser.Release(componentObject);
                ComObjectReleaser.Release(componentsObject);
                ComObjectReleaser.Release(vbProjectObject);
            }
        }

        /// <summary>
        /// Imports a VBA source file into the workbook.
        /// </summary>
        /// <param name="sourceFile">The source file to import.</param>
        public void ImportModule(VbeImportSourceFile sourceFile)
        {
            dynamic workbook = session.WorkbookObject;
            object? vbProjectObject = null;
            object? componentsObject = null;
            object? importedComponent = null;
            try
            {
                vbProjectObject = workbook.VBProject;
                dynamic vbProject = vbProjectObject;
                componentsObject = vbProject.VBComponents;
                dynamic components = componentsObject;
                importedComponent = components.Import(sourceFile.SourcePath);
                dynamic component = importedComponent;
                pendingImportVerifications.Add((
                    sourceFile.ImportVerification,
                    (string)component.Name));
            }
            finally
            {
                ComObjectReleaser.Release(importedComponent);
                ComObjectReleaser.Release(componentsObject);
                ComObjectReleaser.Release(vbProjectObject);
            }
        }

        /// <summary>
        /// Verifies every imported component captured by this session.
        /// </summary>
        public VbeImportVerificationReport VerifyImportedModules()
        {
            dynamic workbook = session.WorkbookObject;
            object? vbProjectObject = null;
            object? componentsObject = null;
            var warnings = new List<VbeIdentifierRecasingWarning>();
            try
            {
                vbProjectObject = workbook.VBProject;
                dynamic vbProject = vbProjectObject;
                componentsObject = vbProject.VBComponents;
                dynamic components = componentsObject;
                foreach (var verification in pendingImportVerifications)
                {
                    var warning = VerifyImportedModule(components, verification);
                    if (warning is not null)
                    {
                        warnings.Add(warning);
                    }
                }
            }
            finally
            {
                ComObjectReleaser.Release(componentsObject);
                ComObjectReleaser.Release(vbProjectObject);
            }

            return new VbeImportVerificationReport(warnings);
        }

        private static VbeIdentifierRecasingWarning? VerifyImportedModule(
            dynamic components,
            (VbeImportVerification Expected, string ImportedComponentName) verification)
        {
            object? componentObject = null;
            object? codeModuleObject = null;
            try
            {
                componentObject = components.Item(verification.ImportedComponentName);
                dynamic component = componentObject;
                codeModuleObject = component.CodeModule;
                dynamic codeModule = codeModuleObject;
                var lineCount = (int)codeModule.CountOfLines;
                var codeModuleLines = new string[lineCount];
                for (var line = 1; line <= lineCount; line++)
                {
                    codeModuleLines[line - 1] = (string)codeModule.Lines(line, 1);
                }

                return VbeImportedComponentVerifier.Verify(
                    verification.Expected,
                    new VbeImportedComponent(
                        (string)component.Name,
                        MapImportedComponentType((int)component.Type),
                        codeModuleLines));
            }
            finally
            {
                ComObjectReleaser.Release(codeModuleObject);
                ComObjectReleaser.Release(componentObject);
            }
        }

        private static object? FindReference(
            dynamic references,
            string referenceName)
        {
            var count = (int)references.Count;
            for (var index = 1; index <= count; index++)
            {
                object? referenceObject = null;
                try
                {
                    referenceObject = references.Item(index);
                    dynamic reference = referenceObject;
                    var description = Convert.ToString(reference.Description);
                    if (referenceName.Equals(
                            description?.Trim(),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var result = referenceObject;
                        referenceObject = null;
                        return result;
                    }
                }
                finally
                {
                    ComObjectReleaser.Release(referenceObject);
                }
            }

            return null;
        }

        private static IReadOnlyList<ReferenceIdentity> ReadReferenceIdentities(
            dynamic references)
        {
            var identities = new List<ReferenceIdentity>();
            var count = (int)references.Count;
            for (var index = 1; index <= count; index++)
            {
                object? referenceObject = null;
                try
                {
                    referenceObject = references.Item(index);
                    identities.Add(ReadReferenceIdentityValue(referenceObject));
                }
                finally
                {
                    ComObjectReleaser.Release(referenceObject);
                }
            }

            return identities
                .OrderBy(identity => identity.Guid, StringComparer.Ordinal)
                .ThenBy(identity => identity.Major)
                .ThenBy(identity => identity.Minor)
                .ThenBy(identity => identity.Name, StringComparer.Ordinal)
                .ThenBy(identity => identity.Description, StringComparer.Ordinal)
                .ThenBy(identity => identity.BuiltIn)
                .ThenBy(identity => identity.IsBroken)
                .ToArray();
        }

        private static ResolvedVbaProjectReference ReadReferenceIdentity(
            object referenceObject,
            string referenceName)
            => CreateResolvedReference(
                ReadReferenceIdentityValue(referenceObject),
                referenceName);

        private static ResolvedVbaProjectReference CreateResolvedReference(
            ReferenceIdentity identity,
            string referenceName)
            => new(
                referenceName,
                identity.Guid,
                identity.Major,
                identity.Minor);

        private static ReferenceIdentity ReadReferenceIdentityValue(
            object referenceObject)
        {
            dynamic reference = referenceObject;
            var guidText = Convert.ToString(reference.Guid)
                ?? throw new InvalidOperationException(
                    "The VBE reference did not expose a GUID.");
            var guid = Guid.Parse(guidText).ToString("D").ToLowerInvariant();
            var major = Convert.ToInt32(reference.Major);
            var minor = Convert.ToInt32(reference.Minor);
            var name = Convert.ToString(reference.Name) ?? string.Empty;
            var description = Convert.ToString(reference.Description) ?? string.Empty;
            var builtIn = Convert.ToBoolean(reference.BuiltIn);
            bool? isBroken;
            try
            {
                isBroken = Convert.ToBoolean(reference.IsBroken);
            }
            catch
            {
                isBroken = null;
            }

            return new ReferenceIdentity(
                guid,
                major,
                minor,
                name,
                description,
                builtIn,
                isBroken);
        }

        private static bool ReferenceIdentitiesEqual(
            IReadOnlyList<ReferenceIdentity> expected,
            IReadOnlyList<ReferenceIdentity> actual)
            => expected.SequenceEqual(actual);

        private static void TryRelease(
            object? value,
            List<Exception> cleanupErrors)
        {
            try
            {
                ComObjectReleaser.Release(value);
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(exception);
            }
        }

        /// <summary>
        /// Saves the workbook through Excel automation.
        /// </summary>
        public void Save()
        {
            dynamic workbook = session.WorkbookObject;
            workbook.Save();
        }

        public IReadOnlyList<WorkbookTestResultRow> RunTests(WorkbookTestSelector selector)
            => ExcelComWorkbookTestRunner.RunTests(session, selector);

        /// <summary>
        /// Closes the workbook, quits Excel, and releases collected COM references.
        /// </summary>
        public void Dispose()
            => session.Dispose();

        internal void DisposeOwnedGeneration(TimeSpan cleanupGrace)
            => session.DisposeOwnedGeneration(cleanupGrace);

        private static WorkbookModuleKind MapComponentType(int type)
            => type switch
            {
                VbextComponentTypeStandardModule => WorkbookModuleKind.StandardModule,
                VbextComponentTypeClassModule => WorkbookModuleKind.ClassModule,
                VbextComponentTypeForm => WorkbookModuleKind.Form,
                VbextComponentTypeDocument => WorkbookModuleKind.Document,
                _ => WorkbookModuleKind.Other
            };

        private static VbaSourceKind MapImportedComponentType(int type)
            => type switch
            {
                VbextComponentTypeStandardModule => VbaSourceKind.StandardModule,
                VbextComponentTypeClassModule => VbaSourceKind.ClassModule,
                VbextComponentTypeForm => VbaSourceKind.Form,
                _ => throw new InvalidOperationException(
                    $"VBIDE imported an unsupported component type '{type}'.")
            };

        private sealed record ReferenceIdentity(
            string Guid,
            int Major,
            int Minor,
            string Name,
            string Description,
            bool BuiltIn,
            bool? IsBroken);
    }

}
