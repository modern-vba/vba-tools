using System.Diagnostics;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaDev.Infrastructure.FileSystem;

namespace VbaDev.Infrastructure.Workbooks;

/// <summary>Finds an exact already-open workbook on the caller desktop.</summary>
internal sealed class WindowsSourceWorkbookOpenLocator : ISourceWorkbookOpenLocator
{
    private readonly IFileSystemPathIdentityResolver pathIdentityResolver =
        new FileSystemPathIdentityResolver();
    private readonly WindowsExcelNativeObjectModelBinder binder = new();

    public SourceWorkbookBorrowedBinding? TryAttach(string workbookPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Excel source workbooks require Windows.");
        }

        var selectedIdentity = pathIdentityResolver.Resolve(workbookPath);
        SourceWorkbookBorrowedBinding? selected = null;
        try
        {
            foreach (var process in Process.GetProcessesByName("EXCEL"))
            {
                using (process)
                {
                    object? application = binder.TryBindApplicationOnCallerDesktopOnce(process.Id);
                    if (application is null) continue;
                    object? workbooksObject = null;
                    try
                    {
                        dynamic excel = application;
                        workbooksObject = excel.Workbooks;
                        dynamic workbooks = workbooksObject;
                        var count = (int)workbooks.Count;
                        for (var index = 1; index <= count; index++)
                        {
                            object? workbookObject = null;
                            try
                            {
                                workbookObject = workbooks.Item(index);
                                dynamic workbook = workbookObject;
                                var fullName = Convert.ToString(workbook.FullName);
                                if (string.IsNullOrWhiteSpace(fullName) || !Path.IsPathRooted(fullName))
                                    continue;
                                var candidateIdentity = pathIdentityResolver.Resolve(fullName);
                                if (!FileSystemPathIdentityRelations.Same(selectedIdentity, candidateIdentity))
                                    continue;
                                if (selected is not null)
                                {
                                    throw new InvalidOperationException(
                                        $"The selected source workbook is open in more than one Excel process: {workbookPath}");
                                }

                                var session = ExcelComWorkbookSession.Borrow(application!, workbookObject, process.Id);
                                var buildSession = new ExcelComWorkbookBuildSession(session);
                                var selectedWorkbookObject = workbookObject;
                                selected = new SourceWorkbookBorrowedBinding(
                                    buildSession, buildSession.IsSaved, buildSession.ReleaseBorrowed,
                                    () => SourceWorkbookPathVerification.IsSelectedWorkbookPath(
                                        selectedWorkbookObject, workbookPath));
                                application = null;
                                workbookObject = null;
                            }
                            finally
                            {
                                ComObjectReleaser.Release(workbookObject);
                            }
                        }
                    }
                    finally
                    {
                        ComObjectReleaser.Release(workbooksObject);
                        ComObjectReleaser.Release(application);
                    }
                }
            }

            return selected;
        }
        catch
        {
            selected?.Dispose();
            throw;
        }
    }
}
