namespace VbaDev.Domain;

/// <summary>
/// Tracks a CommonModules source entry installed into a document source set.
/// </summary>
/// <param name="Name">The extensionless CommonModuleName stored in the project manifest.</param>
/// <param name="Requested">Whether the module was explicitly requested rather than installed as a dependency.</param>
public sealed record InstalledCommonModule(string Name, bool Requested);
