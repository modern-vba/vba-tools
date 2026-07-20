namespace VbaLanguageServer.Syntax;

/// <summary>
/// Describes the semantic reuse proof produced while parsing a complete VBA source document.
/// </summary>
/// <remarks>
/// The variants intentionally do not expose the parser route, changed line ranges, source
/// window, or fallback reason. Consumers may rely only on the proof represented by the
/// variant and must otherwise recompute from <see cref="SyntaxTree"/>.
/// </remarks>
public abstract class VbaSyntaxTreeChangeSet
{
    private VbaSyntaxTreeChangeSet(VbaSyntaxTree syntaxTree)
    {
        SyntaxTree = syntaxTree;
    }

    /// <summary>
    /// Gets the syntax tree for the complete current source document.
    /// </summary>
    public VbaSyntaxTree SyntaxTree { get; }

    /// <summary>
    /// Proves that the URI and text are exactly unchanged and that the previous tree is reused.
    /// </summary>
    public sealed class Unchanged : VbaSyntaxTreeChangeSet
    {
        internal Unchanged(VbaSyntaxTree syntaxTree)
            : base(syntaxTree)
        {
        }
    }

    /// <summary>
    /// Requires consumers to recompute module-derived artifacts.
    /// </summary>
    public sealed class Module : VbaSyntaxTreeChangeSet
    {
        internal Module(VbaSyntaxTree syntaxTree)
            : base(syntaxTree)
        {
        }
    }

    /// <summary>
    /// Proves that one module member was safely replaced while syntax outside that member
    /// remains reusable.
    /// </summary>
    public sealed class ModuleMember : VbaSyntaxTreeChangeSet
    {
        internal ModuleMember(
            VbaSyntaxTree syntaxTree,
            VbaModuleMemberSyntax previousMember,
            VbaModuleMemberSyntax currentMember)
            : base(syntaxTree)
        {
            PreviousMember = previousMember;
            CurrentMember = currentMember;
        }

        /// <summary>
        /// Gets the member from the previous syntax tree.
        /// </summary>
        public VbaModuleMemberSyntax PreviousMember { get; }

        /// <summary>
        /// Gets the replacement member from <see cref="VbaSyntaxTreeChangeSet.SyntaxTree"/>.
        /// </summary>
        public VbaModuleMemberSyntax CurrentMember { get; }
    }
}
