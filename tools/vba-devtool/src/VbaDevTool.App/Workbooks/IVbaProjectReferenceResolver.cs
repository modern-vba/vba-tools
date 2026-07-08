namespace VbaDevTools.App.Workbooks;

public interface IVbaProjectReferenceResolver
{
    IReadOnlyList<ResolvedVbaProjectReference> Resolve(string referenceName);
}
