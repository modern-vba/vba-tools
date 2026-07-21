using Microsoft.CSharp.RuntimeBinder;
using System.Reflection;
using System.Runtime.InteropServices;
using VbaDev.App.Debugging;
using VbaDev.Infrastructure.Workbooks;

namespace VbaDev.Infrastructure.Debugging;

internal interface IExcelDebugWorkbookOpener
{
    object OpenVerified(object excelApplication, string expectedWorkbookPath);
}

/// <summary>
/// Contains the Excel-specific safety policy for opening one generated debug workbook.
/// </summary>
internal sealed class ExcelComDebugWorkbookOpener : IExcelDebugWorkbookOpener
{
    private const int MsoAutomationSecurityLow = 1;

    public object OpenVerified(object excelApplication, string expectedWorkbookPath)
    {
        object? workbooksObject = null;
        object? workbookObject = null;
        object? projectObject = null;
        object? componentsObject = null;
        var succeeded = false;

        try
        {
            dynamic excel = excelApplication;
            excel.EnableEvents = false;
            workbooksObject = excel.Workbooks;
            dynamic workbooks = workbooksObject;
            workbookObject = OpenWithScopedMacroEnablement(
                excel,
                workbooks,
                expectedWorkbookPath);
            dynamic workbook = workbookObject;

            var actualWorkbookPath = Path.GetFullPath((string)workbook.FullName);
            if (!string.Equals(
                    expectedWorkbookPath,
                    actualWorkbookPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new DebugSetupException(
                    "Excel opened a workbook other than the exact generated debug workbook.");
            }

            int projectMode;
            try
            {
                projectObject = workbook.VBProject;
                if (projectObject is null)
                {
                    throw new COMException("Excel returned no VBA project object.");
                }

                dynamic project = projectObject;
                if ((int)project.Protection != 0)
                {
                    throw new DebugSetupException(
                        "VBE debugging cannot continue because the generated workbook's VBA " +
                        "project is locked for viewing. Unlock the VBA project, rebuild the " +
                        "generated workbook, then retry.");
                }

                componentsObject = project.VBComponents;
                dynamic components = componentsObject;
                _ = (int)components.Count;
                projectMode = (int)project.Mode;
            }
            catch (Exception ex) when (IsVbProjectAccessFailure(ex))
            {
                throw new DebugSetupException(
                    "VBE debugging could not access the generated workbook's VBA project. " +
                    "Enable 'Trust access to the VBA project object model' in Excel Trust Center > " +
                    "Macro Settings, ensure the VBA project is not locked for viewing, then retry.",
                    ex);
            }

            if (projectMode != 2)
            {
                throw new DebugSetupException(
                    "The generated workbook VBA project is not in design mode.");
            }

            succeeded = true;
            return workbookObject;
        }
        finally
        {
            ComObjectReleaser.Release(componentsObject);
            ComObjectReleaser.Release(projectObject);
            if (!succeeded)
            {
                ComObjectReleaser.Release(workbookObject);
            }

            ComObjectReleaser.Release(workbooksObject);
        }
    }

    private static object OpenWithScopedMacroEnablement(
        dynamic excel,
        dynamic workbooks,
        string expectedWorkbookPath)
    {
        var previousAutomationSecurity = (int)excel.AutomationSecurity;
        excel.AutomationSecurity = MsoAutomationSecurityLow;
        Exception? openError = null;
        try
        {
            return workbooks.Open(expectedWorkbookPath);
        }
        catch (Exception ex)
        {
            openError = ex;
            throw;
        }
        finally
        {
            try
            {
                excel.AutomationSecurity = previousAutomationSecurity;
            }
            catch when (openError is not null)
            {
                // Preserve the primary open/cancellation failure. The owned process is
                // terminated by the session failure path if its COM server is unavailable.
            }
        }
    }

    private static bool IsVbProjectAccessFailure(Exception error)
        => error is COMException or RuntimeBinderException or InvalidCastException or
            ArgumentException or TargetParameterCountException;
}
