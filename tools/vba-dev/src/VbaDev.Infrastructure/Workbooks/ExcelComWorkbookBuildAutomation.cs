using System.Runtime.InteropServices;
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
        object? workbookObject = null;

        try
        {
            excelObject = Activator.CreateInstance(excelType)
                ?? throw new InvalidOperationException("Excel COM automation could not be started.");
            dynamic excel = excelObject;
            excel.Visible = false;
            excel.DisplayAlerts = false;
            workbookObject = excel.Workbooks.Open(workbookPath, 0, false);
            return new ExcelComWorkbookBuildSession(excelObject, workbookObject);
        }
        catch
        {
            ReleaseComObject(workbookObject);
            ReleaseComObject(excelObject);
            throw;
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
            dynamic components = workbook.VBProject.VBComponents;
            var modules = new List<WorkbookModule>();
            foreach (var componentObject in components)
            {
                try
                {
                    dynamic component = componentObject;
                    modules.Add(new WorkbookModule((string)component.Name, MapComponentType((int)component.Type)));
                }
                finally
                {
                    ReleaseComObject(componentObject);
                }
            }

            ReleaseComObject(components);
            return modules;
        }

        public IReadOnlyList<WorkbookReference> GetReferences()
        {
            dynamic workbook = workbookObject;
            dynamic references = workbook.VBProject.References;
            var result = new List<WorkbookReference>();
            foreach (var referenceObject in references)
            {
                try
                {
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
                    ReleaseComObject(referenceObject);
                }
            }

            ReleaseComObject(references);
            return result;
        }

        public bool RemoveReference(string referenceName)
        {
            dynamic workbook = workbookObject;
            dynamic references = workbook.VBProject.References;
            object? referenceObject = null;
            try
            {
                foreach (var candidateObject in references)
                {
                    try
                    {
                        dynamic candidate = candidateObject;
                        var description = (string)candidate.Description;
                        if (!referenceName.Equals(description, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if ((bool)candidate.BuiltIn)
                        {
                            return false;
                        }

                        referenceObject = candidateObject;
                        references.Remove(referenceObject);
                        return true;
                    }
                    finally
                    {
                        if (!ReferenceEquals(referenceObject, candidateObject))
                        {
                            ReleaseComObject(candidateObject);
                        }
                    }
                }

                return false;
            }
            finally
            {
                ReleaseComObject(referenceObject);
                ReleaseComObject(references);
            }
        }

        public void AddReference(ResolvedVbaProjectReference reference)
        {
            dynamic workbook = workbookObject;
            dynamic references = workbook.VBProject.References;
            object? referenceObject = null;
            try
            {
                referenceObject = references.AddFromGuid(reference.Guid, reference.Major, reference.Minor);
            }
            finally
            {
                ReleaseComObject(referenceObject);
                ReleaseComObject(references);
            }
        }

        public void RemoveModule(string moduleName)
        {
            dynamic workbook = workbookObject;
            dynamic components = workbook.VBProject.VBComponents;
            object? componentObject = null;
            try
            {
                componentObject = components.Item(moduleName);
                components.Remove(componentObject);
            }
            finally
            {
                ReleaseComObject(componentObject);
                ReleaseComObject(components);
            }
        }

        public void ImportModule(VbaSourceFile sourceFile)
        {
            dynamic workbook = workbookObject;
            dynamic components = workbook.VBProject.VBComponents;
            object? importedComponent = null;
            try
            {
                importedComponent = components.Import(sourceFile.SourcePath);
            }
            finally
            {
                ReleaseComObject(importedComponent);
                ReleaseComObject(components);
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
                dynamic workbook = workbookObject;
                workbook.Close(false);
            }
            finally
            {
                try
                {
                    dynamic excel = excelObject;
                    excel.Quit();
                }
                finally
                {
                    ReleaseComObject(workbookObject);
                    ReleaseComObject(excelObject);
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

    private static void ReleaseComObject(object? value)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
