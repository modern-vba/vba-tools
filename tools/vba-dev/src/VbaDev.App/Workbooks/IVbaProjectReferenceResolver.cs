namespace VbaDev.App.Workbooks;

public interface IVbaProjectReferenceResolver
{
    IReadOnlyList<ResolvedVbaProjectReference> Resolve(string referenceName);
}
