using VbaTools.Syntax;
using VbaTools.SourceIdentities;

namespace VbaDebugAdapter.Debugging;

public sealed record DebugSourcePosition(
    string SourceUri,
    int Line,
    int Character);

public sealed record DebugSourceBreakpoint
{
    private readonly DebugSourceUri uri;

    public DebugSourceBreakpoint(string SourceUri, int EditorLine)
        : this(DebugSourceUri.Create(SourceUri), EditorLine)
    {
    }

    private DebugSourceBreakpoint(DebugSourceUri uri, int editorLine)
    {
        this.uri = uri;
        EditorLine = editorLine;
    }

    public string SourceUri
    {
        get => uri.OriginalUri!;
        init => uri = DebugSourceUri.Create(value);
    }

    public int EditorLine { get; init; }

    internal SourceIdentity? Identity => uri.Identity;

    internal static DebugSourceBreakpoint FromAdmitted(DebugSourceUri uri, int editorLine)
        => new(uri, editorLine);

    public void Deconstruct(out string SourceUri, out int EditorLine)
    {
        SourceUri = this.SourceUri;
        EditorLine = this.EditorLine;
    }
}

public sealed record DebugTargetProcedure(
    string ModuleName,
    string ProcedureName)
{
    public VbaConditionalCompilationBranchPath ConditionalCompilationPath
    {
        get;
        init;
    } = VbaConditionalCompilationBranchPath.Root;
}
