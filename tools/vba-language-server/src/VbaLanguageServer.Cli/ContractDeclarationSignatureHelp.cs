using VbaTools.Syntax;

namespace VbaLanguageServer.SourceModel;

internal abstract record VbaDeclarationSignatureHelp
{
    private VbaDeclarationSignatureHelp() { }
    internal sealed record NotDeclaration : VbaDeclarationSignatureHelp;
    internal sealed record NoHelp : VbaDeclarationSignatureHelp;
    internal sealed record Help(VbaSignatureHelp Value) : VbaDeclarationSignatureHelp;
}

/// <summary>
/// Resolves declaration guidance from existing contract authorities in one inventory.
/// </summary>
internal sealed class VbaContractDeclarationSignatureHelp(VbaProjectSemanticResolution core)
{
    public VbaDeclarationSignatureHelp Resolve(
        VbaSourceDocument currentDocument,
        VbaSyntaxTree syntaxTree,
        int line,
        int character,
        VbaSignaturePresentationIdentity? retriggerIdentity)
    {
        var position = new VbaSyntaxPosition(line, character, 0);
        var context = FindParameterContext(syntaxTree, position);
        if (context is not { } parameterContext)
        {
            return new VbaDeclarationSignatureHelp.NotDeclaration();
        }
        if (parameterContext.Slot is not { } activeParameterIndex)
        {
            return new VbaDeclarationSignatureHelp.NoHelp();
        }
        var callable = parameterContext.Callable;

        var declaration = currentDocument.Definitions.FirstOrDefault(definition =>
            definition.Kind is VbaSourceDefinitionKind.Procedure
                or VbaSourceDefinitionKind.Property
            && definition.Range.Start.Line == callable.Range.Start.Line
            && definition.Name.Equals(
                callable.Name,
                StringComparison.OrdinalIgnoreCase)
            && definition.PropertyAccessorKind == callable.PropertyAccessorKind);
        if (declaration is null
            || !TryGetCallableDeclarationNameKind(
                declaration,
                out var declarationKind))
        {
            return new VbaDeclarationSignatureHelp.NoHelp();
        }

        var variants = new List<VbaSignatureHelpVariant>();
        var intrinsicAnalysis = core.IntrinsicHostEvents.AnalyzeIntrinsicHandler(
            currentDocument,
            declaration);
        if (intrinsicAnalysis?.EventTarget.EventContract.Signature is not null)
        {
            var signature = VbaIntrinsicHostEventSemanticModel
                .CreateHandlerSignature(
                    intrinsicAnalysis.Surface,
                    intrinsicAnalysis.HostEvent);
            variants.Add(CreateContractSignatureHelpVariant(
                signature,
                activeParameterIndex,
                isConditional: false));
        }

        var eventSignatures = core.AnalyzeWithEventsHandler(
                currentDocument,
                declaration)
            ?.BindingSet.ResolvedEventSignatures;
        if (eventSignatures is not null)
        {
            variants.AddRange(eventSignatures.Contracts
                .Where(contract => contract.Signature is not null)
                .Select(contract => CreateContractSignatureHelpVariant(
                    contract.Signature!,
                    activeParameterIndex,
                    contract.IsConditionalContract)));
        }

        variants.AddRange(core.InterfaceSemantics
            .GetDeclarationNameCompletionOrigins(
                currentDocument,
                declarationKind)
            .SelectMany(origin => origin.Members.Select(member => new
            {
                FullName = origin.Prefix + member.Name,
                Member = member
            }))
            .Where(candidate => candidate.Member.Signature is not null
                && candidate.FullName.Equals(
                    declaration.Name,
                    StringComparison.OrdinalIgnoreCase))
            .Select(candidate => CreateContractSignatureHelpVariant(
                candidate.Member.Signature!,
                activeParameterIndex,
                candidate.Member.IsConditionalContract)));
        variants = CoalesceContractSignatureHelpVariants(variants);
        if (variants.Count == 0)
        {
            return new VbaDeclarationSignatureHelp.NoHelp();
        }

        var activeSignature = 0;
        if (retriggerIdentity is not null)
        {
            var retainedIndex = variants.FindIndex(
                variant => variant.PresentationIdentity.Matches(
                    retriggerIdentity));
            if (retainedIndex >= 0)
            {
                activeSignature = retainedIndex;
            }
        }

        var active = variants[activeSignature];
        return new VbaDeclarationSignatureHelp.Help(new VbaSignatureHelp(
            active.Signature,
            active.ActiveParameter,
            variants,
            activeSignature));
    }

    private static List<VbaSignatureHelpVariant> CoalesceContractSignatureHelpVariants(
        IEnumerable<VbaSignatureHelpVariant> variants)
    {
        var result = new List<VbaSignatureHelpVariant>();
        foreach (var variant in variants)
        {
            var identity = variant.PresentationIdentity;
            if (result.Any(existing =>
                    existing.PresentationIdentity.Matches(identity)))
            {
                continue;
            }

            result.Add(variant);
        }

        return result;
    }

    private static VbaSignatureHelpVariant CreateContractSignatureHelpVariant(
        VbaCallableSignature signature,
        int? physicalParameterIndex,
        bool isConditional)
        => new(
            signature,
            physicalParameterIndex is int parameterIndex
                    && parameterIndex < signature.Parameters.Count
                ? parameterIndex
                : null,
            isConditional);

    private static bool TryGetCallableDeclarationNameKind(
        VbaSourceDefinition declaration,
        out VbaCallableDeclarationNameKind declarationKind)
    {
        if (declaration.Kind == VbaSourceDefinitionKind.Property)
        {
            declarationKind = declaration.PropertyAccessorKind switch
            {
                VbaPropertyAccessorKind.Get =>
                    VbaCallableDeclarationNameKind.PropertyGet,
                VbaPropertyAccessorKind.Let =>
                    VbaCallableDeclarationNameKind.PropertyLet,
                VbaPropertyAccessorKind.Set =>
                    VbaCallableDeclarationNameKind.PropertySet,
                _ => default
            };
            return declaration.PropertyAccessorKind is not null;
        }

        declarationKind = declaration.CallableKind switch
        {
            VbaCallableKind.Sub => VbaCallableDeclarationNameKind.Sub,
            VbaCallableKind.Function => VbaCallableDeclarationNameKind.Function,
            _ => default
        };
        return declaration.Kind == VbaSourceDefinitionKind.Procedure
            && declaration.CallableKind is VbaCallableKind.Sub
                or VbaCallableKind.Function;
    }

    private readonly record struct ParameterContext(
        VbaCallableDeclarationSyntax Callable, int? Slot);

    private static ParameterContext? FindParameterContext(
        VbaSyntaxTree tree, VbaSyntaxPosition position)
    {
        var callable = tree.Module.CallableDeclarations
            .Where(candidate => Compare(candidate.Range.End, position) <= 0)
            .MaxBy(candidate => (candidate.Range.Start.Line, candidate.Range.Start.Character));
        if (callable is null) return null;

        var opened = false;
        var continued = false;
        var depth = 0;
        var slot = 0;
        var suppressed = false;
        foreach (var token in tree.TokenStream.Tokens)
        {
            if (Compare(token.Range.Start, callable.Range.End) < 0) continue;
            if (token.Kind == VbaTokenKind.Whitespace) continue;
            if (token.Kind == VbaTokenKind.LineContinuation)
            {
                continued = true;
                continue;
            }
            if (token.Kind == VbaTokenKind.NewLine)
            {
                if (continued)
                {
                    continued = false;
                    continue;
                }
                return opened && Compare(position, token.Range.Start) <= 0
                    ? new(callable, suppressed ? null : slot) : null;
            }
            if (!opened)
            {
                if (token.Text is "$" or "%" or "&" or "^" or "!" or "#" or "@") continue;
                if (token.Text != "(" || Compare(position, token.Range.End) < 0) return null;
                opened = true;
                depth = 1;
                continue;
            }
            if (token.Kind == VbaTokenKind.Comment)
            {
                return Compare(position, token.Range.Start) >= 0
                    && Compare(position, token.Range.End) <= 0
                    ? new(callable, null)
                    : Compare(position, token.Range.Start) < 0
                        ? new(callable, suppressed ? null : slot) : null;
            }
            if (token.Kind == VbaTokenKind.StringLiteral
                && Compare(position, token.Range.Start) >= 0
                && Compare(position, token.Range.End) < 0) suppressed = true;
            if (token.Kind != VbaTokenKind.Punctuation) continue;
            if (token.Text == "(") depth++;
            if (token.Text == ")" && --depth == 0)
                return Compare(position, token.Range.Start) <= 0
                    ? new(callable, suppressed ? null : slot) : null;
            if (token.Text == "," && depth == 1
                && Compare(token.Range.End, position) <= 0) slot++;
        }
        return opened ? new(callable, suppressed ? null : slot) : null;
    }

    private static int Compare(VbaSyntaxPosition left, VbaSyntaxPosition right)
        => left.Line != right.Line ? left.Line.CompareTo(right.Line)
            : left.Character.CompareTo(right.Character);
}
