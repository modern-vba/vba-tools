namespace VbaDev.App.CommonModules;

public sealed record CommonModuleManifestEntry(
    string ModuleFile,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Dependencies)
{
    public bool HasCategory(string category)
        => Categories.Any(value => string.Equals(value, category, StringComparison.OrdinalIgnoreCase));
}
