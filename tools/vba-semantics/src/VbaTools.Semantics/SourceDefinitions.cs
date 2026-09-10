using System.Text.Json.Serialization;
using VbaTools.Syntax;

namespace VbaTools.Semantics;

/// <summary>
/// Identifies the editor-facing kind of a source definition.
/// </summary>
public enum VbaSourceDefinitionKind
{
    /// <summary>
    /// A standard module definition.
    /// </summary>
    Module,

    /// <summary>
    /// A class module definition.
    /// </summary>
    Class,

    /// <summary>
    /// A form module definition.
    /// </summary>
    Form,

    /// <summary>
    /// A Sub or Function procedure definition.
    /// </summary>
    Procedure,

    /// <summary>
    /// A property procedure definition.
    /// </summary>
    Property,

    /// <summary>
    /// A constant definition.
    /// </summary>
    Constant,

    /// <summary>
    /// A variable or field definition.
    /// </summary>
    Variable,

    /// <summary>
    /// A callable parameter definition.
    /// </summary>
    Parameter,

    /// <summary>
    /// An enum type definition.
    /// </summary>
    Enum,

    /// <summary>
    /// An enum member definition.
    /// </summary>
    EnumMember,

    /// <summary>
    /// A user-defined type definition.
    /// </summary>
    Type,

    /// <summary>
    /// A member of a user-defined type.
    /// </summary>
    TypeMember,

    /// <summary>
    /// An event definition.
    /// </summary>
    Event
}

/// <summary>
/// Identifies the independent syntax reasons that keep an Event declaration in recovery.
/// </summary>
[Flags]
public enum VbaEventRecoveryReason
{
    /// <summary>
    /// The Event declaration has no known recovery reason.
    /// </summary>
    None = 0,

    /// <summary>
    /// The Event is outside module level in a class-module code section.
    /// </summary>
    InvalidPlacement = 1 << 0,

    /// <summary>
    /// The Event has an explicitly invalid visibility modifier.
    /// </summary>
    InvalidVisibility = 1 << 1,

    /// <summary>
    /// The Event identifier is invalid for an Event declaration.
    /// </summary>
    InvalidName = 1 << 2,

    /// <summary>
    /// An Event parameter is declared Optional.
    /// </summary>
    OptionalParameter = 1 << 3,

    /// <summary>
    /// An Event parameter is declared ParamArray.
    /// </summary>
    ParamArrayParameter = 1 << 4,

    /// <summary>
    /// The written declaration lacks complete callable-signature evidence.
    /// </summary>
    MissingOrInvalidSignature = 1 << 5
}

/// <summary>
/// Identifies the independent syntax reasons that keep a WithEvents variable in recovery.
/// </summary>
[Flags]
public enum VbaWithEventsRecoveryReason
{
    /// <summary>
    /// The WithEvents variable has no known recovery reason.
    /// </summary>
    None = 0,

    /// <summary>
    /// The declaration is outside module level in a class-module code section.
    /// </summary>
    InvalidPlacement = 1 << 0,

    /// <summary>
    /// The WithEvents declarator includes an array designator.
    /// </summary>
    Array = 1 << 1,

    /// <summary>
    /// The WithEvents declarator uses As New.
    /// </summary>
    New = 1 << 2,

    /// <summary>
    /// The WithEvents identifier has a type-declaration character.
    /// </summary>
    TypeDeclarationCharacter = 1 << 3,

    /// <summary>
    /// The WithEvents declarator lacks an explicit As type.
    /// </summary>
    TypeRequired = 1 << 4,

    /// <summary>
    /// The declarator contains unexpected syntax outside the recognized WithEvents shape.
    /// </summary>
    MalformedDeclarator = 1 << 5
}

/// <summary>
/// Represents the visibility scope of a source definition.
/// </summary>
public enum VbaSourceDefinitionVisibility
{
    /// <summary>
    /// Visible outside the declaring module.
    /// </summary>
    Public,

    /// <summary>
    /// Visible throughout the current VBA project.
    /// </summary>
    Friend,

    /// <summary>
    /// Visible only inside the declaring module.
    /// </summary>
    Private,

    /// <summary>
    /// Visible only inside the declaring procedure.
    /// </summary>
    Local
}

internal static class VbaSourceDefinitionVisibilityFacts
{
    public static bool IsProjectVisible(this VbaSourceDefinitionVisibility visibility)
        => visibility is VbaSourceDefinitionVisibility.Public
            or VbaSourceDefinitionVisibility.Friend;
}

/// <summary>
/// Represents a resolved or parsed type annotation used by semantic features.
/// </summary>
/// <param name="Name">The type name.</param>
/// <param name="Qualifier">The optional module or reference qualifier.</param>
public sealed record VbaTypeReference(string Name, string? Qualifier = null);

/// <summary>
/// Identifies where a definition originates without relying on its editor presentation.
/// </summary>
public enum VbaDefinitionOrigin
{
    /// <summary>
    /// The default value, which is not a valid definition origin.
    /// </summary>
    Unknown,

    /// <summary>
    /// A declaration parsed from VBA source text.
    /// </summary>
    Source,

    /// <summary>
    /// A definition projected from an active VBA project reference catalog.
    /// </summary>
    ProjectReference
}

/// <summary>
/// Identifies a source or project-reference definition independently from its display location.
/// </summary>
public readonly struct VbaDefinitionIdentity : IEquatable<VbaDefinitionIdentity>
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    private VbaDefinitionIdentity(
        VbaDefinitionOrigin origin,
        string name,
        string? sourceUri,
        VbaRange? declarationRange,
        string? referenceName,
        string? parentTypeName,
        VbaSourceDefinitionKind? kind,
        VbaPropertyAccessorKind? propertyAccessorKind)
    {
        Origin = origin;
        Name = name;
        SourceUri = sourceUri;
        DeclarationRange = declarationRange;
        ReferenceName = referenceName;
        ParentTypeName = parentTypeName;
        Kind = kind;
        PropertyAccessorKind = propertyAccessorKind;
    }

    /// <summary>
    /// Gets the definition origin.
    /// </summary>
    public VbaDefinitionOrigin Origin { get; }

    /// <summary>
    /// Gets the definition name.
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// Gets the source URI for a source definition.
    /// </summary>
    public string? SourceUri { get; }

    /// <summary>
    /// Gets the declaration range for a source definition.
    /// </summary>
    public VbaRange? DeclarationRange { get; }

    /// <summary>
    /// Gets the reference name for a project-reference definition.
    /// </summary>
    public string? ReferenceName { get; }

    /// <summary>
    /// Gets the containing type name for a project-reference member.
    /// </summary>
    public string? ParentTypeName { get; }

    /// <summary>
    /// Gets the definition kind for a project-reference definition.
    /// </summary>
    public VbaSourceDefinitionKind? Kind { get; }

    /// <summary>
    /// Gets the physical Property accessor kind for a project-reference definition.
    /// </summary>
    public VbaPropertyAccessorKind? PropertyAccessorKind { get; }

    /// <summary>
    /// Creates an identity for a source declaration.
    /// </summary>
    public static VbaDefinitionIdentity ForSource(string uri, string name, VbaRange declarationRange)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0)
        {
            throw new ArgumentException("The source definition name cannot be empty.", nameof(name));
        }
        ArgumentNullException.ThrowIfNull(declarationRange);
        return new VbaDefinitionIdentity(
            VbaDefinitionOrigin.Source,
            name,
            uri,
            declarationRange,
            null,
            null,
            null,
            null);
    }

    /// <summary>
    /// Creates an identity for a project-reference definition.
    /// </summary>
    public static VbaDefinitionIdentity ForProjectReference(
        string referenceName,
        string? parentTypeName,
        VbaSourceDefinitionKind kind,
        string name)
        => ForProjectReference(
            referenceName,
            parentTypeName,
            kind,
            name,
            propertyAccessorKind: null);

    /// <summary>
    /// Creates an identity for a physical project-reference Property accessor.
    /// </summary>
    internal static VbaDefinitionIdentity ForProjectReference(
        string referenceName,
        string? parentTypeName,
        VbaSourceDefinitionKind kind,
        string name,
        VbaPropertyAccessorKind? propertyAccessorKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceName);
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0)
        {
            throw new ArgumentException(
                "The project-reference definition name cannot be empty.",
                nameof(name));
        }

        if (parentTypeName is not null && parentTypeName.Length == 0)
        {
            throw new ArgumentException(
                "The project-reference parent type name cannot be empty.",
                nameof(parentTypeName));
        }

        return new VbaDefinitionIdentity(
            VbaDefinitionOrigin.ProjectReference,
            name,
            null,
            null,
            referenceName,
            parentTypeName,
            kind,
            propertyAccessorKind);
    }

    /// <inheritdoc />
    public bool Equals(VbaDefinitionIdentity other)
    {
        if (Origin != other.Origin || !NameComparer.Equals(Name, other.Name))
        {
            return false;
        }

        return Origin switch
        {
            VbaDefinitionOrigin.Source =>
                NameComparer.Equals(SourceUri, other.SourceUri)
                && Equals(DeclarationRange, other.DeclarationRange),
            VbaDefinitionOrigin.ProjectReference =>
                NameComparer.Equals(ReferenceName, other.ReferenceName)
                && NameComparer.Equals(ParentTypeName, other.ParentTypeName)
                && Kind == other.Kind
                && PropertyAccessorKind == other.PropertyAccessorKind,
            _ => true
        };
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj is VbaDefinitionIdentity other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Origin);
        hash.Add(Name, NameComparer);
        switch (Origin)
        {
            case VbaDefinitionOrigin.Source:
                hash.Add(SourceUri, NameComparer);
                hash.Add(DeclarationRange);
                break;
            case VbaDefinitionOrigin.ProjectReference:
                hash.Add(ReferenceName, NameComparer);
                hash.Add(ParentTypeName, NameComparer);
                hash.Add(Kind);
                hash.Add(PropertyAccessorKind);
                break;
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Determines whether two definition identities are equal.
    /// </summary>
    public static bool operator ==(VbaDefinitionIdentity left, VbaDefinitionIdentity right)
        => left.Equals(right);

    /// <summary>
    /// Determines whether two definition identities differ.
    /// </summary>
    public static bool operator !=(VbaDefinitionIdentity left, VbaDefinitionIdentity right)
        => !left.Equals(right);
}

/// <summary>
/// Represents one source-defined or reference-catalog definition used by editor features.
/// </summary>
/// <param name="Identity">The logical identity used for definition equality.</param>
/// <param name="Location">The editor-facing definition location.</param>
/// <param name="Name">The definition name.</param>
/// <param name="Kind">The editor-facing definition kind.</param>
/// <param name="Visibility">The definition visibility.</param>
/// <param name="ModuleName">The module or reference root that owns the definition.</param>
/// <param name="ParentProcedureName">The containing procedure for local definitions.</param>
/// <param name="ParentProcedureRange">The containing procedure range for local definitions.</param>
/// <param name="Documentation">The documentation text shown by hover.</param>
/// <param name="Signature">The callable signature, when the definition is callable.</param>
/// <param name="ParentTypeName">The containing enum or user-defined type name for members.</param>
/// <param name="TypeReference">The explicit result or variable type reference.</param>
/// <param name="IsWithEvents">Whether the definition declares WithEvents.</param>
/// <param name="DeclarationLabel">The editor-facing declaration summary for hover display.</param>
/// <param name="PropertyAccess">The supported property operations, or Unknown when unavailable.</param>
/// <param name="IsCreatable">Whether the type can be used as the target of a New expression.</param>
/// <param name="PropertyAccessorKind">The source accessor kind, or null for a logical or reference property.</param>
/// <param name="IsArray">Whether the source declaration carries a VBA array marker.</param>
/// <param name="ReferenceGlobalExposure">The explicit public root exposure for a reference definition.</param>
/// <param name="ConditionalCompilationPath">The declaration's structural conditional-compilation branch path, or null when ownership is indeterminate.</param>
/// <param name="EventRecoveryReasons">The independent recovery reasons retained for a source Event declaration.</param>
/// <param name="WithEventsRecoveryReasons">The independent recovery reasons retained for a WithEvents variable declaration.</param>
/// <param name="TypeReferenceRange">The complete source range of an explicit declared type reference.</param>
/// <param name="CallableKind">The written callable declaration kind, retained independently from signature completeness.</param>
/// <param name="IsAuthoringAvailable">Whether ordinary completion may offer this definition.</param>
/// <param name="IsCallableMetadataComplete">Whether a foreign catalog supplied a complete callable signature.</param>
public sealed record VbaSourceDefinition(
    VbaDefinitionIdentity Identity,
    VbaDefinitionLocation Location,
    string Name,
    VbaSourceDefinitionKind Kind,
    VbaSourceDefinitionVisibility Visibility,
    string ModuleName,
    string? ParentProcedureName = null,
    VbaRange? ParentProcedureRange = null,
    string? Documentation = null,
    VbaCallableSignature? Signature = null,
    string? ParentTypeName = null,
    VbaTypeReference? TypeReference = null,
    bool IsWithEvents = false,
    string? DeclarationLabel = null,
    VbaPropertyAccess PropertyAccess = VbaPropertyAccess.Unknown,
    bool IsCreatable = false,
    VbaPropertyAccessorKind? PropertyAccessorKind = null,
    bool IsArray = false,
    ReferenceDefinitionGlobalExposure ReferenceGlobalExposure = ReferenceDefinitionGlobalExposure.None,
    VbaConditionalCompilationBranchPath? ConditionalCompilationPath = null,
    VbaEventRecoveryReason EventRecoveryReasons = VbaEventRecoveryReason.None,
    VbaWithEventsRecoveryReason WithEventsRecoveryReasons = VbaWithEventsRecoveryReason.None,
    VbaRange? TypeReferenceRange = null,
    VbaCallableKind? CallableKind = null,
    bool IsAuthoringAvailable = true,
    bool IsCallableMetadataComplete = true)
{
    /// <summary>
    /// Gets whether authoritative metadata identifies this callable as its type's default member.
    /// </summary>
    public bool IsDefaultMember { get; init; }

    /// <summary>
    /// Gets whether a foreign callable result is an array, or null when unavailable.
    /// </summary>
    public bool? IsReturnArray { get; init; }

    /// <summary>
    /// Gets whether the source variable writes an As String * length clause.
    /// </summary>
    public bool IsFixedLengthString { get; init; }

    // Source-only declaration evidence; callable catalog DTOs remain unchanged.
    internal bool IsExternal { get; init; }

    /// <summary>
    /// Gets the editor-facing definition URI.
    /// </summary>
    public string Uri => Location.Uri;

    /// <summary>
    /// Gets the editor-facing definition range.
    /// </summary>
    public VbaRange Range => Location.Range;

    /// <summary>
    /// Gets whether this definition is a recovered Event declaration.
    /// </summary>
    public bool IsRecoveredEventDeclaration
        => Kind == VbaSourceDefinitionKind.Event
            && EventRecoveryReasons != VbaEventRecoveryReason.None;

    /// <summary>
    /// Gets whether this Event name can participate in name-authoring and handler surfaces.
    /// </summary>
    public bool IsEventNameProjectionEligible
        => Kind == VbaSourceDefinitionKind.Event
            && (EventRecoveryReasons
                & (VbaEventRecoveryReason.InvalidPlacement
                    | VbaEventRecoveryReason.InvalidVisibility
                    | VbaEventRecoveryReason.InvalidName)) == 0;

    /// <summary>
    /// Gets whether this Event name can be offered by RaiseEvent completion.
    /// </summary>
    public bool IsEventNameCompletionEligible => IsEventNameProjectionEligible;

    /// <summary>
    /// Gets whether this variable retained a written but syntax-invalid WithEvents modifier.
    /// </summary>
    public bool IsRecoveredWithEventsVariableDeclaration
        => Kind == VbaSourceDefinitionKind.Variable
            && IsWithEvents
            && WithEventsRecoveryReasons != VbaWithEventsRecoveryReason.None;
}

/// <summary>
/// Identifies the direction explicitly retained for a TypeLib parameter.
/// </summary>
public enum VbaTypeLibParameterDirection
{
    /// <summary>No conclusive input or output direction is available.</summary>
    Unknown,

    /// <summary>The parameter supplies input to the external callable.</summary>
    Input,

    /// <summary>The parameter receives output from the external callable.</summary>
    Output,

    /// <summary>The parameter supplies input and receives output.</summary>
    InputOutput
}

/// <summary>
/// Retains external parameter direction independently from ABI pointer shape.
/// </summary>
/// <param name="Direction">The direction established by TypeLib FIN and FOUT flags.</param>
/// <param name="AbiPointerDepth">The number of enclosing VT_PTR descriptors, or null when unavailable.</param>
public sealed record VbaTypeLibParameterPassing(
    VbaTypeLibParameterDirection Direction,
    int? AbiPointerDepth);

/// <summary>
/// Identifies external calling semantics independently from signature presentation.
/// </summary>
public enum VbaCallablePassingConvention
{
    /// <summary>No external calling convention is established.</summary>
    Unknown,

    /// <summary>The callable has a conclusive Automation-compatible dispatch or vtable contract.</summary>
    AutomationDispatch,

    /// <summary>The callable uses other external calling semantics.</summary>
    OtherExternal
}

/// <summary>
/// Represents one callable parameter in editor-facing signature metadata.
/// </summary>
/// <param name="Name">The parameter name.</param>
/// <param name="Documentation">The parameter documentation text.</param>
/// <param name="IsOptional">Whether the parameter is optional when the source metadata provides it.</param>
/// <param name="DisplayLabel">The displayed parameter segment in the containing signature label.</param>
/// <param name="TypeReference">The parameter type reference, when supplied by the source or catalog.</param>
/// <param name="IsByRef">Whether the parameter is known to be passed ByRef. Null means the metadata is unavailable.</param>
/// <param name="IsParamArray">Whether the parameter is declared ParamArray.</param>
/// <param name="IsArray">Whether the parameter name carries a VBA array marker.</param>
public sealed record VbaCallableParameter(
    string Name,
    string? Documentation = null,
    bool IsOptional = false,
    string? DisplayLabel = null,
    VbaTypeReference? TypeReference = null,
    bool? IsByRef = null,
    bool IsParamArray = false,
    bool IsArray = false)
{
    /// <summary>
    /// Gets the written source Optional default expression, or null when absent.
    /// </summary>
    public string? DefaultExpression { get; init; }

    /// <summary>
    /// Gets retained TypeLib direction and ABI evidence, or null when unavailable.
    /// </summary>
    public VbaTypeLibParameterPassing? TypeLibPassing { get; init; }

    /// <summary>
    /// Gets the parameter segment shown inside its callable signature.
    /// </summary>
    [JsonIgnore]
    public string Label => DisplayLabel ?? Name;
}

/// <summary>
/// Identifies the declared callable form without inferring it from return metadata.
/// </summary>
public enum VbaCallableKind
{
    /// <summary>
    /// A Sub procedure that does not return a value.
    /// </summary>
    Sub,

    /// <summary>
    /// A Function procedure that returns a value.
    /// </summary>
    Function,

    /// <summary>
    /// A Property accessor exposed as one callable property.
    /// </summary>
    Property,

    /// <summary>
    /// An Event declaration.
    /// </summary>
    Event
}

/// <summary>
/// Represents callable signature metadata used by hover and signature help.
/// </summary>
/// <param name="Label">The full signature label.</param>
/// <param name="Parameters">The ordered parameter metadata.</param>
/// <param name="Documentation">The callable documentation retained for semantic consumers but omitted from LSP Signature Help.</param>
/// <param name="CallableKind">The explicit callable kind when supplied by source or catalog metadata.</param>
/// <param name="SupportsNamedArguments">
/// Whether metadata establishes support or rejection for named arguments; null means unknown.
/// </param>
public sealed record VbaCallableSignature(
    string Label,
    IReadOnlyList<VbaCallableParameter> Parameters,
    string? Documentation = null,
    VbaCallableKind? CallableKind = null,
    bool? SupportsNamedArguments = null)
{
    /// <summary>
    /// Gets the evidenced external calling convention, independently from displayed passing modes.
    /// </summary>
    public VbaCallablePassingConvention PassingConvention { get; init; }
}
