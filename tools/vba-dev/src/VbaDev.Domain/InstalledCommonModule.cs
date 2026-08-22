using System.Text.Json.Serialization;

namespace VbaDev.Domain;

/// <summary>
/// Tracks a CommonModules source entry installed into a document source set.
/// </summary>
/// <param name="Name">The extensionless CommonModuleName stored in the project manifest.</param>
/// <param name="ModuleFile">The flat exported source file name stored in the document source set.</param>
/// <param name="Requested">Whether the module was explicitly requested rather than installed as a dependency.</param>
/// <param name="TestOnly">Whether publish excludes the installed source while build imports it normally.</param>
public sealed record InstalledCommonModule(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string ModuleFile,
    [property: JsonRequired] bool Requested,
    [property: JsonRequired] bool TestOnly);
