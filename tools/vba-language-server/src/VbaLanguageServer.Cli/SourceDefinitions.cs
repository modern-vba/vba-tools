using System.Text.Json.Serialization;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

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
    /// Gets whether a foreign callable result is an array, or null when unavailable.
    /// </summary>
    public bool? IsReturnArray { get; init; }

    /// <summary>
    /// Gets whether the source variable writes an As String * length clause.
    /// </summary>
    public bool IsFixedLengthString { get; init; }

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
    bool? SupportsNamedArguments = null);

public sealed record VbaSignaturePresentationIdentity(
    string Label,
    IReadOnlyList<string> ParameterLabels)
{
    public bool Matches(VbaSignaturePresentationIdentity other)
        => Label.Equals(other.Label, StringComparison.Ordinal)
            && ParameterLabels.SequenceEqual(
                other.ParameterLabels,
                StringComparer.Ordinal);
}

/// <summary>
/// Represents one physical callable signature retained by signature help.
/// </summary>
/// <param name="Signature">The physical callable signature.</param>
/// <param name="ActiveParameter">The zero-based active parameter index, or null when no parameter maps.</param>
/// <param name="IsConditionalVariant">Whether the signature is one variant of a conditional family.</param>
public sealed record VbaSignatureHelpVariant(
    VbaCallableSignature Signature,
    int? ActiveParameter,
    bool IsConditionalVariant = false)
{
    public string DisplayLabel => IsConditionalVariant
        ? $"{Signature.Label} [#If]"
        : Signature.Label;

    public VbaSignaturePresentationIdentity PresentationIdentity => new(
        DisplayLabel,
        Signature.Parameters.Select(parameter => parameter.Label).ToArray());
}

/// <summary>
/// Represents the signature help result for a call site.
/// </summary>
/// <param name="Signature">The active callable signature retained for compatibility with editor-neutral consumers.</param>
/// <param name="ActiveParameter">The active signature's zero-based parameter index, or null when no parameter maps.</param>
/// <param name="PhysicalSignatures">Every physical signature retained for presentation.</param>
/// <param name="ActiveSignature">The zero-based active signature index.</param>
public sealed record VbaSignatureHelp(
    VbaCallableSignature Signature,
    int? ActiveParameter,
    IReadOnlyList<VbaSignatureHelpVariant>? PhysicalSignatures = null,
    int ActiveSignature = 0)
{
    /// <summary>
    /// Gets every physical signature, including the ordinary single-signature fallback.
    /// </summary>
    public IReadOnlyList<VbaSignatureHelpVariant> Signatures { get; } =
        PhysicalSignatures
        ?? [new VbaSignatureHelpVariant(Signature, ActiveParameter)];
}

/// <summary>
/// Represents one editor-neutral Hover result and every physical declaration
/// retained by its logical semantic target.
/// </summary>
/// <param name="CanonicalName">The stable presentation name for the logical target.</param>
/// <param name="Definitions">The physical declarations retained for presentation.</param>
/// <param name="IsConditionalFamily">Whether the target is a conditional declaration family.</param>
/// <param name="Range">The source range of the identifier occurrence being hovered.</param>
internal sealed record VbaHoverResult(
    string CanonicalName,
    IReadOnlyList<VbaSourceDefinition> Definitions,
    bool IsConditionalFamily,
    VbaRange Range,
    VbaResolvedEventContract? ProjectedEventContract = null,
    IReadOnlyList<VbaResolvedEventContract>? ProjectedEventContracts = null)
{
    public IReadOnlyList<VbaResolvedEventContract> ResolvedProjectedEventContracts { get; } =
        ProjectedEventContracts
        ?? (ProjectedEventContract is null ? [] : [ProjectedEventContract]);
}

internal enum VbaCompletionInvocationKind
{
    Explicit,
    TriggerCharacter,
    Retrigger
}

internal sealed record VbaCompletionInvocation(
    VbaCompletionInvocationKind Kind,
    string? TriggerCharacter = null)
{
    public static VbaCompletionInvocation Explicit { get; } = new(
        VbaCompletionInvocationKind.Explicit);
}

/// <summary>
/// Identifies the semantic origin of a completed editor-neutral completion candidate.
/// </summary>
public enum VbaCompletionCandidateKind
{
    /// <summary>
    /// A source or project-reference definition admitted by semantic resolution.
    /// </summary>
    Definition,

    /// <summary>
    /// A fixed word from the VBA language vocabulary.
    /// </summary>
    LanguageVocabulary,

    /// <summary>
    /// An unused callable parameter that can be inserted as a named argument.
    /// </summary>
    NamedArgument,

    /// <summary>
    /// A statement supplied by the enclosing grammar context.
    /// </summary>
    ContextualStatement,

    /// <summary>
    /// A callable-owned line label or a special label destination.
    /// </summary>
    Label,

    /// <summary>
    /// A qualifier alias for an active project reference catalog.
    /// </summary>
    ReferenceQualifier,

    /// <summary>
    /// A qualifier for a source module.
    /// </summary>
    SourceQualifier,

    /// <summary>
    /// A semantic contract prefix for a callable declaration name.
    /// </summary>
    ContractPrefix,

    /// <summary>
    /// A semantic contract member for a callable declaration name.
    /// </summary>
    ContractMemberName
}

/// <summary>
/// Represents one signature and its retained documentation variants in completion detail.
/// </summary>
public sealed record VbaCompletionSignaturePresentation(
    string Label,
    bool IsConditional,
    IReadOnlyList<string> DocumentationVariants)
{
    /// <summary>
    /// Gets the signature label displayed by the editor.
    /// </summary>
    public string DisplayLabel => IsConditional ? $"{Label} [#If]" : Label;
}

/// <summary>
/// Represents one complete completion candidate before editor projection.
/// </summary>
/// <param name="Label">The label displayed by the editor.</param>
/// <param name="Kind">The semantic origin of the candidate.</param>
/// <param name="InsertText">The text inserted when it differs from the label.</param>
/// <param name="FilterText">The text used to filter the candidate.</param>
/// <param name="Definition">The admitted source or project-reference definition.</param>
/// <param name="TextEdit">The explicit replacement edit, when syntax supplied a replacement range.</param>
/// <param name="IsConditionalFamily">Whether the candidate represents a conditional declaration family.</param>
public sealed record VbaCompletionCandidate(
    string Label,
    VbaCompletionCandidateKind Kind,
    string? InsertText = null,
    string? FilterText = null,
    VbaSourceDefinition? Definition = null,
    VbaTextEdit? TextEdit = null,
    bool IsConditionalFamily = false)
{
    /// <summary>
    /// Gets the compact editor-facing detail independent of a definition.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Gets whether an editor may immediately request completion again after insertion.
    /// </summary>
    public bool RetriggerCompletion { get; init; }

    /// <summary>
    /// Gets every distinct contract signature presentation retained by this candidate.
    /// </summary>
    public IReadOnlyList<VbaCompletionSignaturePresentation>
        SignaturePresentations { get; init; } = [];

    /// <summary>
    /// Gets the request-relative name-resolution rank used by editor projection.
    /// </summary>
    public int? SortRank { get; init; }
}

/// <summary>
/// Represents the complete editor-neutral candidates valid at a source position.
/// </summary>
/// <param name="Candidates">The context-filtered completion candidates.</param>
public sealed record VbaCompletionResult(IReadOnlyList<VbaCompletionCandidate> Candidates)
{
    /// <summary>
    /// Gets the definition-backed candidates for compatibility with semantic consumers.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<VbaSourceDefinition> Definitions
        => Candidates
            .Where(candidate => candidate.Definition is not null)
            .Select(candidate => candidate.Definition!)
            .ToArray();
}

/// <summary>
/// Represents one parsed source document used by semantic inventory construction.
/// </summary>
/// <param name="Uri">The document URI.</param>
/// <param name="Text">The complete source text.</param>
/// <param name="ModuleName">The parsed module identity.</param>
/// <param name="Definitions">The definitions declared by the document.</param>
/// <param name="SyntaxTree">The parsed syntax tree for features that need structured syntax.</param>
public sealed record VbaSourceDocument(
    string Uri,
    string Text,
    string ModuleName,
    IReadOnlyList<VbaSourceDefinition> Definitions,
    VbaSyntaxTree? SyntaxTree = null)
{
    internal VbaSourceDocumentProjection? Projection { get; init; }
}

internal sealed record VbaSourceDocumentProjection(
    VbaSyntaxTree SyntaxTree,
    IReadOnlyList<VbaSourceDefinition> Definitions);

/// <summary>
/// Represents a definition or reference location.
/// </summary>
/// <param name="Uri">The document URI.</param>
/// <param name="Range">The source range.</param>
public sealed record VbaDefinitionLocation(string Uri, VbaRange Range);

/// <summary>
/// Represents one text edit in LSP-compatible coordinates.
/// </summary>
/// <param name="Range">The source range to replace.</param>
/// <param name="NewText">The replacement text.</param>
public sealed record VbaTextEdit(VbaRange Range, string NewText);

/// <summary>
/// Represents the exact semantic occurrence offered by Prepare Rename.
/// </summary>
/// <param name="Range">The occurrence range under the request cursor.</param>
/// <param name="Placeholder">The target declaration's canonical name.</param>
public sealed record VbaPrepareRenameResult(
    VbaRange Range,
    string Placeholder);

internal sealed record VbaPrepareRenameOutcome(
    VbaPrepareRenameResult? Result,
    VbaRenameFailure? Failure);

/// <summary>
/// Represents a validated rename operation and its resulting edits.
/// </summary>
/// <param name="TargetRange">The captured target declaration range used to identify the plan.</param>
/// <param name="Changes">The source edits keyed by document URI.</param>
public sealed record VbaRenamePlan(
    VbaRange TargetRange,
    IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> Changes)
{
    internal IReadOnlyList<VbaRenameFileOperation> FileRenames { get; init; } = [];

    internal IReadOnlyList<VbaFormSourceUnit> FormSourceUnits
        { get; init; } = [];

    internal VbaRenameTargetCorrespondence? TargetCorrespondence { get; init; }
}

internal sealed record VbaFormSourceUnit(
    string FormUri,
    string SidecarUri,
    string SidecarDestinationUri,
    bool SidecarRequired,
    bool SidecarPathFollowsIdentity);

internal sealed record VbaRenameFileOperation(
    string OldUri,
    string NewUri,
    bool Overwrite = false);

internal sealed record VbaRenameFilePreflightResult(
    VbaRenamePlan Plan,
    VbaRenameFailure? Failure);

internal sealed record VbaRenamePhysicalDefinitionCorrespondence(
    VbaSourceDefinition BeforeDefinition,
    VbaSourceDefinition AfterDefinition);

internal sealed record VbaRenameCallVariantCorrespondence(
    VbaRenamePhysicalDefinitionCorrespondence Definition,
    VbaCallCompatibilityState BeforeState,
    VbaCallCompatibilityState AfterState);

internal sealed record VbaRenameCallCompatibilityCorrespondence(
    string Uri,
    VbaRange BeforeRange,
    VbaRange AfterRange,
    VbaCallContext BeforeContext,
    VbaCallContext AfterContext,
    IReadOnlyList<VbaRenameCallVariantCorrespondence> Variants);

internal sealed record VbaRenameOccurrenceTargetCorrespondence(
    string Uri,
    VbaRange BeforeRange,
    VbaRange AfterRange,
    VbaResolvedNameTarget BeforeTarget,
    VbaResolvedNameTarget AfterTarget,
    IReadOnlyList<VbaRenamePhysicalDefinitionCorrespondence>
        PossibleDefinitions);

internal sealed record VbaRenameTargetCorrespondence(
    VbaResolvedNameTarget BeforeTarget,
    VbaResolvedNameTarget AfterTarget,
    IReadOnlyList<VbaRenamePhysicalDefinitionCorrespondence>
        PhysicalDefinitions)
{
    public IReadOnlyList<VbaRenameCallCompatibilityCorrespondence>
        CallCompatibilities { get; init; } = [];

    public IReadOnlyList<VbaRenameOccurrenceTargetCorrespondence>
        OccurrenceTargets { get; init; } = [];
}

internal sealed record VbaRenameFailure(
    string Reason,
    string Message,
    IReadOnlyList<VbaRenameConflict>? Conflicts = null,
    string? Condition = null,
    string? Path = null,
    string? Guidance = null);

internal sealed record VbaRenameConflict(
    string CollisionKind,
    string Name,
    string? Uri,
    VbaRange? Range,
    string? ReferenceName = null);

internal sealed record VbaRenameResult(
    VbaRenamePlan? Plan,
    VbaRenameFailure? Failure);

/// <summary>
/// Represents a workspace symbol projected from a source definition.
/// </summary>
/// <param name="Name">The symbol name.</param>
/// <param name="Kind">The symbol definition kind.</param>
/// <param name="Uri">The owning document URI.</param>
/// <param name="Range">The symbol source range.</param>
public sealed record VbaWorkspaceSymbol(
    string Name,
    VbaSourceDefinitionKind Kind,
    string Uri,
    VbaRange Range);

/// <summary>
/// Represents one semantic token before LSP delta encoding.
/// </summary>
/// <param name="Range">The source range covered by the token.</param>
/// <param name="Text">The source text covered by the token.</param>
/// <param name="TokenType">The semantic token type name.</param>
/// <param name="TokenModifiers">The semantic token modifier names.</param>
public sealed record VbaSemanticToken(
    VbaRange Range,
    string Text,
    string TokenType,
    IReadOnlyList<string> TokenModifiers);

/// <summary>
/// Projects parsed VBA syntax into immutable source definitions and safely
/// reuses unchanged definitions after a member-local parse.
/// </summary>
internal static class VbaSourceDocumentProjector
{
    public static VbaSourceDocument Project(string uri, VbaSyntaxTree syntaxTree)
    {
        var definitions = new List<VbaSourceDefinition>();
        var moduleDefinition = CreateModuleDefinition(uri, syntaxTree.Module);
        definitions.Add(moduleDefinition);
        definitions.AddRange(syntaxTree.Module.Declarations.Select(declaration =>
            CreateSourceDefinition(uri, moduleDefinition.Name, syntaxTree, declaration)));

        return CreateProjectedDocument(
            uri,
            syntaxTree,
            moduleDefinition.Name,
            definitions);
    }

    public static VbaSourceDocument Project(
        string uri,
        VbaSyntaxTreeChangeSet changeSet,
        VbaSourceDocument? previousDocument)
    {
        var syntaxTree = changeSet.SyntaxTree;
        if (changeSet is VbaSyntaxTreeChangeSet.Unchanged
            && IsOwnedProjection(
                uri,
                syntaxTree,
                previousDocument))
        {
            return previousDocument!;
        }

        if (changeSet is not VbaSyntaxTreeChangeSet.ModuleMember memberChange
            || !TryCreateReusableDefinitionMap(
                uri,
                syntaxTree,
                previousDocument,
                memberChange,
                out var moduleDefinition,
                out var reusableDefinitions))
        {
            return Project(uri, syntaxTree);
        }

        var definitions = new List<VbaSourceDefinition>(
            syntaxTree.Module.Declarations.Count + 1)
        {
            moduleDefinition
        };
        foreach (var declaration in syntaxTree.Module.Declarations)
        {
            definitions.Add(
                reusableDefinitions.TryGetValue(declaration, out var definition)
                    ? definition
                    : CreateSourceDefinition(
                        uri,
                        moduleDefinition.Name,
                        syntaxTree,
                        declaration));
        }

        return CreateProjectedDocument(
            uri,
            syntaxTree,
            moduleDefinition.Name,
            definitions);
    }

    private static bool IsOwnedProjection(
        string uri,
        VbaSyntaxTree syntaxTree,
        VbaSourceDocument? previousDocument)
        => previousDocument is not null
            && uri.Equals(syntaxTree.Uri, StringComparison.Ordinal)
            && uri.Equals(previousDocument.Uri, StringComparison.Ordinal)
            && ReferenceEquals(previousDocument.SyntaxTree, syntaxTree)
            && previousDocument.Text.Equals(syntaxTree.Text, StringComparison.Ordinal)
            && previousDocument.Projection is { } previousProjection
            && ReferenceEquals(previousProjection.SyntaxTree, syntaxTree)
            && ReferenceEquals(
                previousProjection.Definitions,
                previousDocument.Definitions);

    private static bool TryCreateReusableDefinitionMap(
        string uri,
        VbaSyntaxTree syntaxTree,
        VbaSourceDocument? previousDocument,
        VbaSyntaxTreeChangeSet.ModuleMember memberChange,
        out VbaSourceDefinition moduleDefinition,
        out Dictionary<VbaDeclarationSyntax, VbaSourceDefinition> reusableDefinitions)
    {
        moduleDefinition = default!;
        reusableDefinitions = default!;
        var previousSyntaxTree = previousDocument?.SyntaxTree;
        if (previousSyntaxTree is null
            || previousDocument is null
            || !uri.Equals(syntaxTree.Uri, StringComparison.Ordinal)
            || !uri.Equals(previousSyntaxTree.Uri, StringComparison.Ordinal)
            || !uri.Equals(previousDocument.Uri, StringComparison.Ordinal)
            || !ReferenceEquals(previousDocument.SyntaxTree, previousSyntaxTree)
            || !previousDocument.Text.Equals(previousSyntaxTree.Text, StringComparison.Ordinal)
            || previousSyntaxTree.Module.Kind != syntaxTree.Module.Kind
            || !previousSyntaxTree.Module.Identity.Name.Equals(
                syntaxTree.Module.Identity.Name,
                StringComparison.OrdinalIgnoreCase)
            || previousDocument.Projection is not { } previousProjection
            || !ReferenceEquals(previousProjection.SyntaxTree, previousSyntaxTree)
            || !ReferenceEquals(
                previousProjection.Definitions,
                previousDocument.Definitions)
            || previousDocument.Definitions.Count
                != previousSyntaxTree.Module.Declarations.Count + 1
            || !ContainsReference(
                previousSyntaxTree.Module.Members,
                memberChange.PreviousMember)
            || !ContainsReference(
                syntaxTree.Module.Members,
                memberChange.CurrentMember))
        {
            return false;
        }

        var candidateModuleDefinition = previousDocument.Definitions[0];
        if (!DefinitionMatchesModule(
                candidateModuleDefinition,
                uri,
                syntaxTree.Module))
        {
            return false;
        }

        var candidates = new Dictionary<VbaDeclarationSyntax, VbaSourceDefinition>(
            previousSyntaxTree.Module.Declarations.Count,
            ReferenceEqualityComparer.Instance);
        for (var index = 0;
            index < previousSyntaxTree.Module.Declarations.Count;
            index++)
        {
            var declaration = previousSyntaxTree.Module.Declarations[index];
            var definition = previousDocument.Definitions[index + 1];
            if (!DefinitionMatchesDeclaration(
                    definition,
                    uri,
                    candidateModuleDefinition.Name,
                    previousSyntaxTree,
                    declaration))
            {
                return false;
            }

            candidates.Add(declaration, definition);
        }

        moduleDefinition = candidateModuleDefinition;
        reusableDefinitions = candidates;
        return true;
    }

    private static VbaSourceDocument CreateProjectedDocument(
        string uri,
        VbaSyntaxTree syntaxTree,
        string moduleName,
        IReadOnlyList<VbaSourceDefinition> definitions)
    {
        IReadOnlyList<VbaSourceDefinition> frozenDefinitions =
            Array.AsReadOnly(definitions.ToArray());
        return new VbaSourceDocument(
            uri,
            syntaxTree.Text,
            moduleName,
            frozenDefinitions,
            syntaxTree)
        {
            Projection = new VbaSourceDocumentProjection(
                syntaxTree,
                frozenDefinitions)
        };
    }

    private static bool DefinitionMatchesModule(
        VbaSourceDefinition definition,
        string uri,
        VbaModuleSyntax module)
        => definition.Identity.Origin == VbaDefinitionOrigin.Source
            && uri.Equals(definition.Identity.SourceUri, StringComparison.Ordinal)
            && uri.Equals(definition.Uri, StringComparison.Ordinal)
            && definition.Name.Equals(module.Identity.Name, StringComparison.Ordinal)
            && definition.ModuleName.Equals(module.Identity.Name, StringComparison.Ordinal)
            && definition.Kind == MapModuleKind(module.Kind)
            && RangeMatches(definition.Range, module.Identity.Range);

    private static bool DefinitionMatchesDeclaration(
        VbaSourceDefinition definition,
        string uri,
        string moduleName,
        VbaSyntaxTree syntaxTree,
        VbaDeclarationSyntax declaration)
        => definition.Identity.Origin == VbaDefinitionOrigin.Source
            && uri.Equals(definition.Identity.SourceUri, StringComparison.Ordinal)
            && uri.Equals(definition.Uri, StringComparison.Ordinal)
            && definition.Name.Equals(declaration.Name, StringComparison.Ordinal)
            && definition.ModuleName.Equals(moduleName, StringComparison.Ordinal)
            && definition.Kind == MapDeclarationKind(declaration.Kind)
            && definition.Visibility == MapVisibility(declaration.Visibility)
            && RangeMatches(definition.Range, declaration.Range)
            && definition.CallableKind == (declaration.CallableKind is null
                ? null
                : GetCallableKind(declaration))
            && definition.EventRecoveryReasons
                == GetEventRecoveryReasons(syntaxTree, declaration)
            && definition.WithEventsRecoveryReasons
                == GetWithEventsRecoveryReasons(syntaxTree, declaration)
            && definition.IsFixedLengthString == declaration.IsFixedLengthString
            && Equals(
                definition.TypeReferenceRange,
                declaration.WithEventsTypeReferenceRange is null
                    ? null
                    : MapRange(declaration.WithEventsTypeReferenceRange))
            && Equals(
                definition.ConditionalCompilationPath,
                GetConditionalCompilationPath(syntaxTree, declaration));

    private static bool RangeMatches(VbaRange definitionRange, VbaSyntaxRange syntaxRange)
        => definitionRange.Start.Line == syntaxRange.Start.Line
            && definitionRange.Start.Character == syntaxRange.Start.Character
            && definitionRange.End.Line == syntaxRange.End.Line
            && definitionRange.End.Character == syntaxRange.End.Character;

    private static bool ContainsReference<T>(
        IReadOnlyList<T> items,
        T candidate)
        where T : class
    {
        foreach (var item in items)
        {
            if (ReferenceEquals(item, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static VbaSourceDefinition CreateModuleDefinition(string uri, VbaModuleSyntax module)
    {
        var range = MapRange(module.Identity.Range);
        return new VbaSourceDefinition(
            VbaDefinitionIdentity.ForSource(uri, module.Identity.Name, range),
            new VbaDefinitionLocation(uri, range),
            module.Identity.Name,
            MapModuleKind(module.Kind),
            VbaSourceDefinitionVisibility.Public,
            module.Identity.Name,
            IsCreatable: module.Kind is VbaModuleKind.ClassModule or VbaModuleKind.FormModule,
            ConditionalCompilationPath: VbaConditionalCompilationBranchPath.Root);
    }

    private static VbaSourceDefinition CreateSourceDefinition(
        string uri,
        string moduleName,
        VbaSyntaxTree syntaxTree,
        VbaDeclarationSyntax declaration)
    {
        var range = MapRange(declaration.Range);
        var eventRecoveryReasons = GetEventRecoveryReasons(syntaxTree, declaration);
        var withEventsRecoveryReasons = GetWithEventsRecoveryReasons(syntaxTree, declaration);
        return new VbaSourceDefinition(
            Identity: VbaDefinitionIdentity.ForSource(uri, declaration.Name, range),
            Location: new VbaDefinitionLocation(uri, range),
            Name: declaration.Name,
            Kind: MapDeclarationKind(declaration.Kind),
            Visibility: MapVisibility(declaration.Visibility),
            ModuleName: moduleName,
            ParentProcedureName: declaration.ParentProcedureName,
            ParentProcedureRange: declaration.ParentProcedureRange is null ? null : MapRange(declaration.ParentProcedureRange),
            Documentation: declaration.Documentation,
            Signature: declaration.Signature is null
                || !HasValidSourceCallableSignature(syntaxTree, declaration)
                    ? null
                    : MapSignature(declaration),
            ParentTypeName: declaration.ParentTypeName,
            TypeReference: declaration.TypeReference is null ? null : MapTypeReference(declaration.TypeReference),
            IsWithEvents: declaration.IsWithEvents,
            DeclarationLabel: declaration.DeclarationLabel,
            PropertyAccess: MapPropertyAccess(declaration.PropertyAccessorKind),
            PropertyAccessorKind: declaration.PropertyAccessorKind,
            IsArray: declaration.IsArray,
            ConditionalCompilationPath: GetConditionalCompilationPath(
                syntaxTree,
                declaration),
            EventRecoveryReasons: eventRecoveryReasons,
            WithEventsRecoveryReasons: withEventsRecoveryReasons,
            TypeReferenceRange: declaration.WithEventsTypeReferenceRange is null
                ? null
                : MapRange(declaration.WithEventsTypeReferenceRange),
            CallableKind: declaration.CallableKind is null
                ? null
                : GetCallableKind(declaration))
        {
            IsFixedLengthString = declaration.IsFixedLengthString
        };
    }

    private static VbaWithEventsRecoveryReason GetWithEventsRecoveryReasons(
        VbaSyntaxTree syntaxTree,
        VbaDeclarationSyntax declaration)
    {
        if (!declaration.IsWithEvents
            || declaration.WithEventsKeywordRange is not { } withEventsRange)
        {
            return VbaWithEventsRecoveryReason.None;
        }

        var reasons = syntaxTree.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "syntax.withEventsDeclarationNotAllowedHere"
                && diagnostic.Range == withEventsRange)
            ? VbaWithEventsRecoveryReason.InvalidPlacement
            : VbaWithEventsRecoveryReason.None;
        if (declaration.WithEventsArrayDesignatorRange is not null)
        {
            reasons |= VbaWithEventsRecoveryReason.Array;
        }

        if (declaration.WithEventsNewKeywordRange is not null)
        {
            reasons |= VbaWithEventsRecoveryReason.New;
        }

        if (declaration.WithEventsTypeDeclarationCharacterRange is not null)
        {
            reasons |= VbaWithEventsRecoveryReason.TypeDeclarationCharacter;
        }

        if (declaration.WithEventsTypeRequiredRange is not null)
        {
            reasons |= VbaWithEventsRecoveryReason.TypeRequired;
        }

        if (!declaration.HasRecognizableWithEventsDeclaratorShape)
        {
            reasons |= VbaWithEventsRecoveryReason.MalformedDeclarator;
        }

        return reasons;
    }

    private static VbaEventRecoveryReason GetEventRecoveryReasons(
        VbaSyntaxTree syntaxTree,
        VbaDeclarationSyntax declaration)
    {
        if (declaration.Kind != VbaDeclarationKind.Event)
        {
            return VbaEventRecoveryReason.None;
        }

        var reasons = VbaEventRecoveryReason.None;
        if (syntaxTree.Module.Kind is not (
                VbaModuleKind.ClassModule or VbaModuleKind.FormModule)
            || declaration.ParentProcedureName is not null
            || declaration.IsInvalidEventPlacement)
        {
            reasons |= VbaEventRecoveryReason.InvalidPlacement;
        }

        if (declaration.Visibility != VbaDeclarationVisibility.Public)
        {
            reasons |= VbaEventRecoveryReason.InvalidVisibility;
        }

        if (declaration.Name.Contains("_", StringComparison.Ordinal)
            || !VbaIdentifier.IsLexIdentifier(declaration.Name))
        {
            reasons |= VbaEventRecoveryReason.InvalidName;
        }

        if (declaration.HasOptionalEventParameter)
        {
            reasons |= VbaEventRecoveryReason.OptionalParameter;
        }

        if (declaration.HasParamArrayEventParameter)
        {
            reasons |= VbaEventRecoveryReason.ParamArrayParameter;
        }

        if (declaration.Signature is null
            || !declaration.HasCompleteEventSignatureShape)
        {
            reasons |= VbaEventRecoveryReason.MissingOrInvalidSignature;
        }

        return reasons;
    }

    private static VbaConditionalCompilationBranchPath? GetConditionalCompilationPath(
        VbaSyntaxTree syntaxTree,
        VbaDeclarationSyntax declaration)
        => VbaConditionalCompilationBranchFacts.TryGetPath(
            syntaxTree,
            declaration.Range,
            requireCompleteStructure: true,
            out var path)
                ? path
                : null;

    private static VbaSourceDefinitionKind MapModuleKind(VbaModuleKind kind)
        => kind switch
        {
            VbaModuleKind.ClassModule => VbaSourceDefinitionKind.Class,
            VbaModuleKind.FormModule => VbaSourceDefinitionKind.Form,
            _ => VbaSourceDefinitionKind.Module
        };

    private static VbaSourceDefinitionKind MapDeclarationKind(VbaDeclarationKind kind)
        => kind switch
        {
            VbaDeclarationKind.Procedure => VbaSourceDefinitionKind.Procedure,
            VbaDeclarationKind.Property => VbaSourceDefinitionKind.Property,
            VbaDeclarationKind.Constant => VbaSourceDefinitionKind.Constant,
            VbaDeclarationKind.Variable => VbaSourceDefinitionKind.Variable,
            VbaDeclarationKind.Parameter => VbaSourceDefinitionKind.Parameter,
            VbaDeclarationKind.Enum => VbaSourceDefinitionKind.Enum,
            VbaDeclarationKind.EnumMember => VbaSourceDefinitionKind.EnumMember,
            VbaDeclarationKind.Type => VbaSourceDefinitionKind.Type,
            VbaDeclarationKind.TypeMember => VbaSourceDefinitionKind.TypeMember,
            VbaDeclarationKind.Event => VbaSourceDefinitionKind.Event,
            _ => VbaSourceDefinitionKind.Variable
        };

    private static VbaSourceDefinitionVisibility MapVisibility(VbaDeclarationVisibility visibility)
        => visibility switch
        {
            VbaDeclarationVisibility.Public => VbaSourceDefinitionVisibility.Public,
            VbaDeclarationVisibility.Friend => VbaSourceDefinitionVisibility.Friend,
            VbaDeclarationVisibility.Local => VbaSourceDefinitionVisibility.Local,
            _ => VbaSourceDefinitionVisibility.Private
        };

    private static VbaRange MapRange(VbaSyntaxRange range)
        => new(
            new VbaPosition(range.Start.Line, range.Start.Character),
            new VbaPosition(range.End.Line, range.End.Character));

    private static VbaCallableSignature MapSignature(VbaDeclarationSyntax declaration)
    {
        var signature = declaration.Signature!;
        var parameterLabels = signature.Parameters.Select(CreateSignatureParameterLabel).ToArray();
        var callableKind = GetCallableKind(declaration);
        var declarePrefix = declaration.IsExternal ? "Declare " : "";
        var label = $"{declarePrefix}{callableKind} {declaration.Name}({string.Join(", ", parameterLabels)})";
        if (declaration.TypeReference is not null)
        {
            label = $"{label} As {declaration.TypeReference.Name}";
        }

        return new VbaCallableSignature(
            label,
            signature.Parameters
                .Select((parameter, index) => new VbaCallableParameter(
                    Name: parameter.Name,
                    Documentation: parameter.Documentation,
                    IsOptional: parameter.IsOptional,
                    DisplayLabel: parameterLabels[index],
                    TypeReference: parameter.TypeReference is null
                        ? new VbaTypeReference("Variant")
                        : MapTypeReference(parameter.TypeReference),
                    IsByRef: parameter.IsByRef,
                    IsParamArray: parameter.IsParamArray,
                    IsArray: parameter.IsArray)
                {
                    DefaultExpression = parameter.DefaultExpression
                })
                .ToArray(),
            signature.Documentation,
            CallableKind: callableKind,
            SupportsNamedArguments: true);
    }

    private static bool HasValidSourceCallableSignature(
        VbaSyntaxTree syntaxTree,
        VbaDeclarationSyntax declaration)
    {
        var callableDeclaration = syntaxTree.Module.CallableDeclarations
            .SingleOrDefault(candidate =>
                candidate.LineIndex == declaration.LineIndex
                && candidate.Name.Equals(
                    declaration.Name,
                    StringComparison.OrdinalIgnoreCase)
                && candidate.IsExternal == declaration.IsExternal
                && candidate.PropertyAccessorKind == declaration.PropertyAccessorKind);
        if (callableDeclaration is not null)
        {
            var tokens = GetSignificantTokens(callableDeclaration.OriginalLine);
            return callableDeclaration.IsExternal
                ? VbaBlockHeaderSyntax.HasCompleteExternalCallableShape(
                    tokens,
                    syntaxTree.Module.Kind,
                    declaration.ParentProcedureName is null)
                : VbaBlockHeaderSyntax.TryGetCompleteCallableShape(
                    tokens,
                    syntaxTree.Module.Kind,
                    out _);
        }

        if (declaration.Kind != VbaDeclarationKind.Event)
        {
            return false;
        }

        if (syntaxTree.Module.Kind is not (
                VbaModuleKind.ClassModule or VbaModuleKind.FormModule)
            || declaration.ParentProcedureName is not null
            || declaration.IsInvalidEventPlacement
            || declaration.Visibility != VbaDeclarationVisibility.Public
            || declaration.Name.Contains("_", StringComparison.Ordinal)
            || !VbaIdentifier.IsLexIdentifier(declaration.Name)
            || declaration.Signature is null
            || declaration.Signature.Parameters.Any(parameter =>
                parameter.IsOptional || parameter.IsParamArray))
        {
            return false;
        }

        return declaration.HasCompleteEventSignatureShape;
    }

    private static IReadOnlyList<VbaToken> GetSignificantTokens(string text)
        => VbaTokenStream.FromText(text).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.Comment
                and not VbaTokenKind.LineContinuation)
            .ToArray();

    private static VbaCallableKind GetCallableKind(VbaDeclarationSyntax declaration)
        => declaration.CallableKind?.ToUpperInvariant() switch
        {
            "SUB" => VbaCallableKind.Sub,
            "FUNCTION" => VbaCallableKind.Function,
            "PROPERTY" => VbaCallableKind.Property,
            "EVENT" => VbaCallableKind.Event,
            _ => declaration.Kind switch
            {
                VbaDeclarationKind.Property => VbaCallableKind.Property,
                VbaDeclarationKind.Event => VbaCallableKind.Event,
                _ => declaration.TypeReference is null ? VbaCallableKind.Sub : VbaCallableKind.Function
            }
        };

    private static VbaPropertyAccess MapPropertyAccess(VbaPropertyAccessorKind? accessorKind)
        => accessorKind switch
        {
            VbaPropertyAccessorKind.Get => VbaPropertyAccess.Readable,
            VbaPropertyAccessorKind.Let or VbaPropertyAccessorKind.Set => VbaPropertyAccess.Writable,
            _ => VbaPropertyAccess.Unknown
        };

    private static string CreateSignatureParameterLabel(VbaCallableParameterInfoSyntax parameter)
    {
        var parts = new List<string>();
        if (parameter.IsParamArray)
        {
            parts.Add("ParamArray");
        }
        else if (parameter.IsByRef)
        {
            parts.Add("ByRef");
        }

        parts.Add(parameter.IsArray ? $"{parameter.Name}()" : parameter.Name);
        if (parameter.TypeReference is not null)
        {
            parts.Add($"As {parameter.TypeReference.Name}");
        }

        var label = string.Join(" ", parts);
        return parameter.IsOptional ? $"[{label}]" : label;
    }

    private static VbaTypeReference MapTypeReference(VbaTypeReferenceSyntax typeReference)
        => new(typeReference.Name, typeReference.Qualifier);
}
