using System.Text;
using VbaLanguageServer.Syntax;

namespace VbaDev.App.HostClasses;

/// <summary>
/// Describes one document source template selected for host-class inspection.
/// </summary>
/// <param name="SourceTemplatePath">The canonical absolute source-template path fixed at invocation start.</param>
/// <param name="Timeouts">The bounded Excel and inspection stage deadlines.</param>
public sealed record HostClassInspectionRequest(
    string SourceTemplatePath,
    HostClassInspectionTimeouts Timeouts);

/// <summary>
/// Contains the independent bounded deadlines for one host-class inspection.
/// </summary>
public sealed record HostClassInspectionTimeouts(
    TimeSpan ExcelProcessStart,
    TimeSpan WorkbookOpen,
    TimeSpan CooperativeCleanup,
    TimeSpan ClassEnumeration,
    TimeSpan ClassInspection);

/// <summary>
/// Runs the owned Excel/VBIDE inspection boundary and returns only after owned process release is proven.
/// </summary>
public interface IHostClassInspectionAutomation
{
    /// <summary>
    /// Inspects a private source-template copy and returns the completed observation batch.
    /// </summary>
    Task<HostClassInspectionCompletion> InspectAsync(
        HostClassInspectionRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Contains publishable inspection state returned only after process release and workspace cleanup attempts.
/// </summary>
public sealed record HostClassInspectionCompletion(
    HostClassInspectionBatch Batch,
    IReadOnlyList<HostClassInspectionWarning> Warnings)
{
    /// <summary>
    /// Creates a completion without housekeeping warnings.
    /// </summary>
    public static HostClassInspectionCompletion Create(HostClassInspectionBatch batch)
        => new(batch, []);
}

/// <summary>
/// Describes non-invalidating inspection housekeeping degradation.
/// </summary>
public sealed record HostClassInspectionWarning(string Code, string Message);

/// <summary>
/// Represents one host-class observation returned by the inspection boundary.
/// </summary>
public abstract record HostClassInspectionEntry(HostClassIdentity Identity);

/// <summary>
/// Identifies one intrinsic class inside the selected project document.
/// </summary>
public sealed record HostClassIdentity
{
    /// <summary>Creates one exact intrinsic VBA class identity.</summary>
    public HostClassIdentity(string name, HostClassComponentKind kind)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!VbaIdentifier.IsIdentifier(name)
            || name.EnumerateRunes().Take(32).Count() > 31)
        {
            throw new InvalidOperationException(
                "Host-class name must be an exact VBA IDENTIFIER of 1 to 31 characters.");
        }

        Name = name;
        Kind = kind;
    }

    /// <summary>Gets the exact VBA class name.</summary>
    public string Name { get; }

    /// <summary>Gets the intrinsic component kind.</summary>
    public HostClassComponentKind Kind { get; }

    /// <summary>Deconstructs this identity for positional callers.</summary>
    public void Deconstruct(out string name, out HostClassComponentKind kind)
    {
        name = Name;
        kind = Kind;
    }
}

/// <summary>
/// Identifies the intrinsic VBComponent kinds projected by this command.
/// </summary>
public enum HostClassComponentKind
{
    Form,
    Document
}

/// <summary>
/// Contains a fully inspected intrinsic class projection.
/// </summary>
public sealed record ResolvedHostClassInspectionEntry(
    HostClassIdentity Identity,
    string IntrinsicEventSourceName,
    IReadOnlyList<HostEventSignature> Events,
    HostClassBaseTypeProvenance? BaseTypeProvenance = null)
    : HostClassInspectionEntry(Identity);

/// <summary>
/// Carries optional catalog-resolvable base host type provenance for navigation.
/// </summary>
public sealed record HostClassBaseTypeProvenance(
    string Name,
    Guid LibraryGuid,
    int MajorVersion,
    int MinorVersion,
    int Lcid);

/// <summary>
/// Identifies a class whose complete projection could not be established.
/// </summary>
public sealed record UnverifiedHostClassInspectionEntry(
    HostClassIdentity Identity,
    HostClassInspectionFailureReason Reason,
    string Message)
    : HostClassInspectionEntry(Identity);

/// <summary>
/// Identifies the stable reason for one unverified host class.
/// </summary>
public enum HostClassInspectionFailureReason
{
    EventEnumerationFailure,
    IntrinsicEventSourceNameReadFailure,
    SignatureReadFailure,
    AvailabilityReadFailure,
    InspectionTimeout,
    InspectionAborted,
    Cancelled,
    InspectionFailure
}

/// <summary>
/// Contains one complete inspected Event signature.
/// </summary>
public sealed record HostEventSignature(
    string Name,
    IReadOnlyList<HostEventParameter> Parameters,
    string? Documentation,
    bool AuthoringAvailable,
    bool ExistingHandlerRecognizable);

/// <summary>
/// Contains one ordered parameter in an inspected Event signature.
/// </summary>
public sealed record HostEventParameter(
    string Name,
    HostEventTypeReference Type,
    HostEventPassingMechanism Passing,
    HostEventArrayShape ArrayShape,
    bool Optional,
    bool ParamArray);

/// <summary>
/// Identifies how an Event parameter is passed.
/// </summary>
public enum HostEventPassingMechanism
{
    ByVal,
    ByRef
}

/// <summary>
/// Identifies whether an Event parameter is a scalar or array.
/// </summary>
public enum HostEventArrayShape
{
    Scalar,
    Array
}

/// <summary>
/// Carries portable type evidence for one Event parameter.
/// </summary>
public abstract record HostEventTypeReference;

/// <summary>
/// Carries one canonical intrinsic VBA type name.
/// </summary>
public sealed record IntrinsicHostEventTypeReference(string Name) : HostEventTypeReference;

/// <summary>
/// Carries a portable TypeLib type identity without registry-path or display-library coupling.
/// </summary>
public sealed record TypeLibHostEventTypeReference(
    string Name,
    Guid LibraryGuid,
    int MajorVersion,
    int MinorVersion,
    int Lcid)
    : HostEventTypeReference;

/// <summary>
/// Retains opaque type display text without establishing canonical equality.
/// </summary>
public sealed record UnresolvedHostEventTypeReference(string DisplayName) : HostEventTypeReference;

/// <summary>
/// Contains the post-release observations for one selected document.
/// </summary>
/// <param name="ClassEnumerationComplete">Whether the complete unambiguous class identity set was enumerated.</param>
/// <param name="Classes">The inspected class entries.</param>
public sealed record HostClassInspectionBatch(
    bool ClassEnumerationComplete,
    IReadOnlyList<HostClassInspectionEntry> Classes)
{
    /// <summary>
    /// Gets top-level diagnostics that describe enumeration or shared-state outcomes.
    /// </summary>
    public IReadOnlyList<HostClassInspectionDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// Gets the terminal operation outcome used for process exit classification.
    /// </summary>
    public HostClassInspectionOutcome Outcome { get; init; } = HostClassInspectionOutcome.Completed;

    /// <summary>
    /// Creates a successful complete observation batch.
    /// </summary>
    public static HostClassInspectionBatch CreateComplete(IReadOnlyList<HostClassInspectionEntry> classes)
        => new(true, classes);

    /// <summary>
    /// Creates a released, schema-valid terminal partial result for cooperative cancellation.
    /// </summary>
    public static HostClassInspectionBatch CreateCancelled(
        bool classEnumerationComplete,
        IReadOnlyList<HostClassInspectionEntry> classes,
        string message)
        => new(classEnumerationComplete, classes)
        {
            Outcome = HostClassInspectionOutcome.Cancelled,
            Diagnostics = [new HostClassInspectionDiagnostic("operationCancelled", message)]
        };

    /// <summary>
    /// Creates a released terminal partial result after shared inspection state loses trust.
    /// </summary>
    public static HostClassInspectionBatch CreateInspectionStateUntrusted(
        bool classEnumerationComplete,
        IReadOnlyList<HostClassInspectionEntry> classes,
        string message)
        => new(classEnumerationComplete, classes)
        {
            Outcome = HostClassInspectionOutcome.InspectionStateUntrusted,
            Diagnostics = [new HostClassInspectionDiagnostic("inspectionStateUntrusted", message)]
        };
}

/// <summary>
/// Describes one top-level host-class inspection diagnostic.
/// </summary>
public sealed record HostClassInspectionDiagnostic(string Code, string Message);

/// <summary>
/// Identifies how a post-release host-class inspection terminated.
/// </summary>
public enum HostClassInspectionOutcome
{
    Completed,
    Cancelled,
    InspectionStateUntrusted
}
