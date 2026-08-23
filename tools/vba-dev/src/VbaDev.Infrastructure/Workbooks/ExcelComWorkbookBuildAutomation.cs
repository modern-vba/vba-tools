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
        => new ExcelComWorkbookBuildSession(ExcelComWorkbookSession.Open(workbookPath));

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
                        var description = (string)reference.Description;
                        var isBuiltIn = (bool)reference.BuiltIn;
                        if (!string.IsNullOrWhiteSpace(description))
                        {
                            result.Add(new WorkbookReference(description.Trim(), IsRemovable: !isBuiltIn));
                        }
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
        public void VerifyImportedModules()
        {
            dynamic workbook = session.WorkbookObject;
            object? vbProjectObject = null;
            object? componentsObject = null;
            try
            {
                vbProjectObject = workbook.VBProject;
                dynamic vbProject = vbProjectObject;
                componentsObject = vbProject.VBComponents;
                dynamic components = componentsObject;
                foreach (var verification in pendingImportVerifications)
                {
                    VerifyImportedModule(components, verification);
                }
            }
            finally
            {
                ComObjectReleaser.Release(componentsObject);
                ComObjectReleaser.Release(vbProjectObject);
            }
        }

        private static void VerifyImportedModule(
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

                VbeImportedComponentVerifier.Verify(
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
    }

}
