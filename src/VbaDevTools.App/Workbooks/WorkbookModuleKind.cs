namespace VbaDevTools.App.Workbooks;

public enum WorkbookModuleKind
{
    StandardModule,
    ClassModule,
    Form,
    Document,
    Other
}

public static class WorkbookModuleKindExtensions
{
    public static bool IsImportable(this WorkbookModuleKind kind)
        => kind is WorkbookModuleKind.StandardModule or WorkbookModuleKind.ClassModule or WorkbookModuleKind.Form;
}
