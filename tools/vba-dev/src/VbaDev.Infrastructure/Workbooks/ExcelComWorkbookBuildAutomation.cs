using VbaDev.App.Workbooks;

namespace VbaDev.Infrastructure.Workbooks;

public sealed class ExcelComWorkbookBuildAutomation : IWorkbookBuildAutomation
{
    private const int VbextComponentTypeStandardModule = 1;
    private const int VbextComponentTypeClassModule = 2;
    private const int VbextComponentTypeForm = 3;
    private const int VbextComponentTypeDocument = 100;

    public IWorkbookBuildSession OpenWorkbook(string workbookPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Excel COM build automation is supported only on Windows.");
        }

        var excelType = Type.GetTypeFromProgID("Excel.Application")
            ?? throw new InvalidOperationException("Excel COM automation is not available.");
        object? excelObject = null;
        object? workbooksObject = null;
        object? workbookObject = null;

        try
        {
            excelObject = Activator.CreateInstance(excelType)
                ?? throw new InvalidOperationException("Excel COM automation could not be started.");
            dynamic excel = excelObject;
            excel.Visible = false;
            excel.DisplayAlerts = false;
            workbooksObject = excel.Workbooks;
            dynamic workbooks = workbooksObject;
            workbookObject = workbooks.Open(workbookPath, 0, false);
            return new ExcelComWorkbookBuildSession(excelObject, workbookObject);
        }
        catch
        {
            try
            {
                CloseWorkbook(workbookObject);
                QuitExcel(excelObject);
            }
            finally
            {
                ComObjectReleaser.CollectReleasedComObjects();
            }

            throw;
        }
        finally
        {
            ComObjectReleaser.Release(workbooksObject);
            if (workbookObject is null || excelObject is null)
            {
                ComObjectReleaser.CollectReleasedComObjects();
            }
        }
    }

    private sealed class ExcelComWorkbookBuildSession : IWorkbookBuildSession
    {
        private readonly object excelObject;
        private readonly object workbookObject;

        public ExcelComWorkbookBuildSession(object excelObject, object workbookObject)
        {
            this.excelObject = excelObject;
            this.workbookObject = workbookObject;
        }

        public IReadOnlyList<WorkbookModule> GetModules()
        {
            dynamic workbook = workbookObject;
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

        public IReadOnlyList<WorkbookReference> GetReferences()
        {
            dynamic workbook = workbookObject;
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

        public bool RemoveReference(string referenceName)
        {
            dynamic workbook = workbookObject;
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

        public void AddReference(ResolvedVbaProjectReference reference)
        {
            dynamic workbook = workbookObject;
            object? vbProjectObject = null;
            object? referencesObject = null;
            object? referenceObject = null;
            try
            {
                vbProjectObject = workbook.VBProject;
                dynamic vbProject = vbProjectObject;
                referencesObject = vbProject.References;
                dynamic references = referencesObject;
                referenceObject = references.AddFromGuid(reference.Guid, reference.Major, reference.Minor);
            }
            finally
            {
                ComObjectReleaser.Release(referenceObject);
                ComObjectReleaser.Release(referencesObject);
                ComObjectReleaser.Release(vbProjectObject);
            }
        }

        public void RemoveModule(string moduleName)
        {
            dynamic workbook = workbookObject;
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

        public void ImportModule(VbaSourceFile sourceFile)
        {
            dynamic workbook = workbookObject;
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
            }
            finally
            {
                ComObjectReleaser.Release(importedComponent);
                ComObjectReleaser.Release(componentsObject);
                ComObjectReleaser.Release(vbProjectObject);
            }
        }

        public void Save()
        {
            dynamic workbook = workbookObject;
            workbook.Save();
        }

        public void Dispose()
        {
            try
            {
                CloseWorkbook(workbookObject);
            }
            finally
            {
                try
                {
                    QuitExcel(excelObject);
                }
                finally
                {
                    ComObjectReleaser.CollectReleasedComObjects();
                }
            }
        }

        private static WorkbookModuleKind MapComponentType(int type)
            => type switch
            {
                VbextComponentTypeStandardModule => WorkbookModuleKind.StandardModule,
                VbextComponentTypeClassModule => WorkbookModuleKind.ClassModule,
                VbextComponentTypeForm => WorkbookModuleKind.Form,
                VbextComponentTypeDocument => WorkbookModuleKind.Document,
                _ => WorkbookModuleKind.Other
            };
    }

    private static void CloseWorkbook(object? workbookObject)
    {
        if (workbookObject is null)
        {
            return;
        }

        try
        {
            dynamic workbook = workbookObject;
            workbook.Close(false);
        }
        finally
        {
            ComObjectReleaser.Release(workbookObject);
        }
    }

    private static void QuitExcel(object? excelObject)
    {
        if (excelObject is null)
        {
            return;
        }

        try
        {
            dynamic excel = excelObject;
            excel.Quit();
        }
        finally
        {
            ComObjectReleaser.Release(excelObject);
        }
    }
}
