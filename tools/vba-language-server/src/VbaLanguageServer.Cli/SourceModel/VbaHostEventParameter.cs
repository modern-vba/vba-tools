namespace VbaLanguageServer.SourceModel;

public enum VbaHostEventParameterPassing
{
    ByVal,
    ByRef
}

public enum VbaHostEventParameterArrayShape
{
    Scalar,
    Array
}

/// <summary>
/// Represents one parameter in an intrinsic host Event signature.
/// </summary>
public sealed record VbaHostEventParameter(
    string Name,
    VbaHostEventParameterType Type,
    VbaHostEventParameterPassing Passing,
    VbaHostEventParameterArrayShape ArrayShape,
    bool Optional,
    bool ParamArray);

public abstract record VbaHostEventParameterType;

public sealed record VbaIntrinsicHostEventParameterType(string Name)
    : VbaHostEventParameterType;

public sealed record VbaTypeLibraryHostEventParameterType(
    string Name,
    string LibraryGuid,
    int MajorVersion,
    int MinorVersion,
    int Lcid)
    : VbaHostEventParameterType;

public sealed record VbaUnresolvedHostEventParameterType(string DisplayName)
    : VbaHostEventParameterType;
