using System.Text.Json.Serialization;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaTools.Syntax;

namespace VbaLanguageServer.SourceModel;

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
    VbaRenameFailure? Failure,
    VbaRenameCollisionReview? CollisionReview = null);

internal enum VbaRenameCollisionMode
{
    Reject,
    PrepareConfirmation
}

internal enum VbaRenameImpactKind
{
    DeclarationCollision,
    TargetBindingChanged,
    NonTargetBindingChanged,
    ClassificationChanged,
    LogicalGroupingChanged,
    TypeNameResolutionChanged,
    DependentAssociationChanged
}

internal sealed record VbaRenameImpact(
    VbaRenameImpactKind Kind,
    string Message,
    string? Uri = null,
    VbaRange? Range = null)
{
    [JsonIgnore]
    public VbaRenameImpactEvidence? Evidence { get; init; }

    [JsonIgnore]
    public VbaRenameDeclaredTypeImpactEvidence? DeclaredTypeEvidence { get; init; }

    [JsonIgnore]
    public VbaRenameGroupingImpactEvidence? GroupingEvidence { get; init; }

    [JsonIgnore]
    public VbaRenameWithEventsImpactEvidence? WithEventsEvidence { get; init; }

    [JsonIgnore]
    public VbaRenameInterfaceImpactEvidence? InterfaceEvidence { get; init; }

    [JsonIgnore]
    public VbaRenameReferenceProjectImpactEvidence? ReferenceProjectEvidence { get; init; }
}

internal sealed record VbaRenameReferenceProjectImpactEvidence(
    string ReferenceName,
    string ReferencedVbaProjectName,
    VbaRenameCausalDefinitionCorrespondence SourceModule,
    VbaResolvedNameTarget BeforeMemberTarget,
    VbaResolvedNameTarget ControlMemberTarget,
    VbaResolvedNameTarget AfterMemberTarget);

internal sealed record VbaRenameInterfaceImpactEvidence(
    VbaInterfaceImplementationAssociation Before,
    VbaInterfaceImplementationAssociation Control,
    IReadOnlyList<VbaInterfaceImplementationAssociation> After,
    IReadOnlyList<VbaRenameCausalDefinitionCorrespondence> DeclarationCauses);

internal sealed record VbaRenameWithEventsImpactEvidence(
    VbaHandlerEventRenameConvergence Before,
    VbaHandlerEventRenameConvergence Control,
    VbaHandlerEventRenameConvergence? After,
    IReadOnlyList<VbaRenameCausalDefinitionCorrespondence> DeclarationCauses);

internal sealed record VbaRenameGroupingImpactEvidence(
    VbaResolvedNameTarget BeforeTarget,
    VbaResolvedNameTarget ControlTarget,
    VbaResolvedNameTarget AfterTarget,
    IReadOnlyList<VbaRenameCausalDefinitionCorrespondence> DeclarationCauses);

internal sealed record VbaRenameDeclaredTypeImpactEvidence(
    VbaRenameCausalDefinitionCorrespondence Declaration,
    VbaEffectiveDeclaredType BeforeType,
    VbaEffectiveDeclaredType ControlType,
    VbaEffectiveDeclaredType AfterType,
    IReadOnlyList<VbaRenameCausalDefinitionCorrespondence> TypeDeclarationCauses);

internal sealed record VbaRenameCausalDefinitionCorrespondence(
    VbaSourceDefinition BeforeDefinition,
    VbaSourceDefinition ControlDefinition,
    VbaSourceDefinition AfterDefinition);

internal sealed record VbaRenameImpactEvidence(
    VbaNameResolutionKind BeforeClassification,
    VbaNameResolutionKind ControlClassification,
    VbaNameResolutionKind AfterClassification,
    VbaRange ControlRange,
    VbaRange AfterRange,
    IReadOnlyList<VbaRenameCausalDefinitionCorrespondence> DeclarationCauses);

internal sealed record VbaRenameCollisionReview(
    string OriginalName,
    string RequestedName,
    IReadOnlyList<VbaRenameConflict> Conflicts,
    IReadOnlyList<VbaRenameImpact> Impacts);

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
