using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Describes which call arguments remain valid before editor projection.
/// </summary>
/// <param name="CallableDefinition">The resolved callable definition, when available.</param>
/// <param name="Signature">The resolved callable signature, when available.</param>
/// <param name="AllowsPositionalExpression">Whether the active argument can be a positional expression.</param>
/// <param name="RemainingNamedParameters">
/// The source parameters that can still be supplied by name for a CallArgument expectation.
/// A NamedArgumentValue expectation uses expression candidates instead and does not consume its active argument.
/// </param>
internal sealed record VbaCallArgumentAvailability(
    VbaSourceDefinition? CallableDefinition,
    VbaCallableSignature? Signature,
    bool AllowsPositionalExpression,
    IReadOnlyList<VbaCallableParameter> RemainingNamedParameters,
    IReadOnlySet<string>? ConditionalNamedParameterNames = null,
    VbaCallContextCompatibility ContextCompatibility =
        VbaCallContextCompatibility.Indeterminate)
{
    public static VbaCallArgumentAvailability None { get; } = new(null, null, false, []);

    public bool IsConditionalNamedParameter(string name)
        => ConditionalNamedParameterNames?.Contains(name) == true;
}

/// <summary>
/// Resolves callable targets and active arguments from structured position syntax.
/// </summary>
internal sealed class VbaCallSiteResolution
{
    private readonly VbaNameResolutionService nameResolution;
    private readonly VbaMemberChainResolution memberChainResolution;
    private readonly VbaResolutionPolicy resolutionPolicy;

    public VbaCallSiteResolution(
        VbaNameResolutionService nameResolution,
        VbaMemberChainResolution memberChainResolution,
        VbaResolutionPolicy resolutionPolicy)
    {
        this.nameResolution = nameResolution;
        this.memberChainResolution = memberChainResolution;
        this.resolutionPolicy = resolutionPolicy;
    }

    public VbaSignatureHelp? GetSignatureHelp(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaPositionSyntax positionSyntax,
        VbaSignaturePresentationIdentity? retriggerIdentity = null,
        Func<VbaSourceDefinition, VbaSourceDefinition>?
            definitionProjector = null)
    {
        var callSite = positionSyntax.CallSite;
        if (callSite is null
            || !TryResolveCallableNameTarget(
                currentDocument,
                line,
                character,
                callSite,
                positionSyntax.EnclosingWithScopes,
                out var target)
            || target is null)
        {
            return null;
        }

        var callContext = GetCallContext(currentDocument, callSite);
        var rankingCallSite = CreateSignatureRankingCallSite(callSite);
        var variants = new List<(
            VbaSourceDefinition Definition,
            VbaSignatureHelpVariant Variant,
            VbaCallArgumentMapping Mapping,
            VbaCallArgumentMapping RankingMapping)>();
        foreach (var unprojectedDefinition in
                 GetCallableUseSiteDefinitions(currentDocument, target))
        {
            var definition = definitionProjector?.Invoke(
                    unprojectedDefinition)
                ?? unprojectedDefinition;
            var physicalSignature = definition.Signature;
            if (physicalSignature is null)
            {
                continue;
            }

            var invocationSignature = physicalSignature;
            if (definition.PropertyAccessorKind
                    is VbaPropertyAccessorKind.Let or VbaPropertyAccessorKind.Set
                && !TryCreateSetterInvocationSignature(
                    physicalSignature,
                    out invocationSignature))
            {
                continue;
            }

            var allowNamedArguments = definition.Kind != VbaSourceDefinitionKind.Event
                && physicalSignature.CallableKind != VbaCallableKind.Event;
            var contextCompatibility = VbaCallArgumentMapper.GetContextCompatibility(
                definition,
                physicalSignature,
                callContext);
            var mapping = VbaCallArgumentMapper.MapInProgress(
                invocationSignature,
                callSite,
                allowNamedArguments,
                contextCompatibility);
            if (!mapping.HasValidSignatureShape)
            {
                continue;
            }

            var rankingMapping = VbaCallArgumentMapper.MapInProgress(
                invocationSignature,
                rankingCallSite,
                allowNamedArguments,
                contextCompatibility);

            variants.Add((
                definition,
                new VbaSignatureHelpVariant(
                    invocationSignature,
                    mapping.ActiveParameter,
                    definition.ConditionalCompilationPath is
                        { IsEmpty: false }),
                mapping,
                rankingMapping));
        }

        if (variants.Count == 0)
        {
            return null;
        }

        var retriggerMatchCount = retriggerIdentity is null
            ? 0
            : variants.Count(candidate =>
                candidate.Variant.PresentationIdentity.Matches(retriggerIdentity));
        var activeIndex = variants
            .Select((candidate, index) => new
            {
                index,
                contextRank = GetContextRank(candidate.Mapping.ContextCompatibility),
                namedArgumentRank = GetNamedArgumentRank(
                    candidate.Variant.Signature,
                    rankingCallSite),
                arityRank = GetArityRank(
                    candidate.Variant.Signature,
                    candidate.RankingMapping,
                    rankingCallSite),
                typeCompatibilityRank = GetTypeCompatibilityRank(
                    currentDocument,
                    candidate.Definition,
                    candidate.Variant.Signature,
                    candidate.RankingMapping,
                    rankingCallSite),
                retriggerRank = retriggerMatchCount == 1
                    && retriggerIdentity is not null
                    && candidate.Variant.PresentationIdentity.Matches(retriggerIdentity)
                        ? 1
                        : 0
            })
            .OrderByDescending(candidate => candidate.contextRank)
            .ThenByDescending(candidate => candidate.namedArgumentRank)
            .ThenByDescending(candidate => candidate.arityRank)
            .ThenByDescending(candidate => candidate.typeCompatibilityRank)
            .ThenByDescending(candidate => candidate.retriggerRank)
            .ThenBy(candidate => candidate.index)
            .First()
            .index;
        var active = variants[activeIndex].Variant;
        return new VbaSignatureHelp(
            active.Signature,
            active.ActiveParameter,
            Array.AsReadOnly(variants.Select(candidate => candidate.Variant).ToArray()),
            activeIndex);
    }

    private static int GetNamedArgumentRank(
        VbaCallableSignature signature,
        VbaCallSiteSyntax callSite)
    {
        if (signature.SupportsNamedArguments != true
            || signature.Parameters.Any(parameter => parameter.IsParamArray))
        {
            return 0;
        }

        var suppliedNames = callSite.Arguments
            .Where(argument => argument.Name is not null)
            .Select(argument => argument.Name!)
            .ToArray();
        return suppliedNames.Length > 0
            && suppliedNames.All(name => signature.Parameters.Any(parameter =>
                parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    ? 1
                    : 0;
    }

    private static int GetArityRank(
        VbaCallableSignature signature,
        VbaCallArgumentMapping mapping,
        VbaCallSiteSyntax callSite)
    {
        var hasArgumentCapacity = signature.Parameters.LastOrDefault()?.IsParamArray == true
            || callSite.Arguments.Count <= signature.Parameters.Count;
        return !hasArgumentCapacity
            ? 0
            : mapping.HasStructuralMismatch
                ? 1
                : 2;
    }

    private static VbaCallSiteSyntax CreateSignatureRankingCallSite(
        VbaCallSiteSyntax callSite)
    {
        var arguments = callSite.Arguments
            .Concat(callSite.TrailingArguments ?? [])
            .ToArray();
        return callSite with
        {
            Arguments = Array.AsReadOnly(arguments),
            ActiveArgumentIndex = arguments.Length,
            ActiveNamedArgument = null,
            TrailingArguments = []
        };
    }

    public VbaConditionalCallCompatibility AnalyzeCompleteCall(
        VbaSourceDocument currentDocument,
        VbaArgumentListSyntax argumentList,
        VbaResolvedNameTarget target)
    {
        var callSite = CreateCompleteCallSite(argumentList);
        var callContext = GetCallContext(currentDocument, callSite);
        var variants = new List<VbaCallVariantCompatibility>();
        foreach (var definition in GetCallableUseSiteDefinitions(currentDocument, target))
        {
            var signature = definition.Signature;
            if (signature is null)
            {
                variants.Add(new VbaCallVariantCompatibility(
                    definition,
                    null,
                    null,
                    null,
                    VbaCallCompatibilityState.Indeterminate));
                continue;
            }

            var invocationSignature = signature;
            if (definition.PropertyAccessorKind
                    is VbaPropertyAccessorKind.Let or VbaPropertyAccessorKind.Set
                && !TryCreateSetterInvocationSignature(signature, out invocationSignature))
            {
                variants.Add(new VbaCallVariantCompatibility(
                    definition,
                    signature,
                    null,
                    null,
                    VbaCallCompatibilityState.Indeterminate));
                continue;
            }

            var contextCompatibility = VbaCallArgumentMapper.GetContextCompatibility(
                definition,
                signature,
                callContext);
            var mapping = VbaCallArgumentMapper.MapComplete(
                invocationSignature,
                callSite,
                allowNamedArguments: definition.Kind != VbaSourceDefinitionKind.Event
                    && signature.CallableKind != VbaCallableKind.Event,
                contextCompatibility);
            mapping = ApplyCompleteTypeCompatibility(
                currentDocument,
                definition,
                invocationSignature,
                callSite,
                mapping);
            variants.Add(new VbaCallVariantCompatibility(
                definition,
                signature,
                invocationSignature,
                mapping,
                mapping.State));
        }

        return new VbaConditionalCallCompatibility(
            target,
            callContext,
            Array.AsReadOnly(variants.ToArray()));
    }

    internal bool TryResolveRaiseEventTarget(
        VbaSourceDocument currentDocument,
        VbaArgumentListSyntax argumentList,
        out VbaResolvedNameTarget? target)
    {
        var callSite = CreateCompleteCallSite(argumentList);
        return TryResolveRaiseEventTarget(currentDocument, callSite, out target);
    }

    internal bool TryResolveRaiseEventTarget(
        VbaSourceDocument currentDocument,
        VbaCallSiteSyntax? callSite,
        out VbaResolvedNameTarget? target)
    {
        if (callSite is null
            || GetCallContext(currentDocument, callSite) != VbaCallContext.RaiseEvent)
        {
            target = null;
            return false;
        }

        target = ResolveCurrentDocumentEventTarget(currentDocument, callSite);
        return true;
    }

    internal bool IsRaiseEventCall(
        VbaSourceDocument currentDocument,
        VbaCallSiteSyntax? callSite)
        => callSite is not null
            && GetCallContext(currentDocument, callSite) == VbaCallContext.RaiseEvent;

    private static bool HasRaiseEventPlacementDiagnostic(
        VbaSourceDocument currentDocument,
        VbaCallSiteSyntax callSite)
    {
        var syntaxTree = currentDocument.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(currentDocument.Uri, currentDocument.Text);
        var raiseEventKeyword = syntaxTree.TokenStream.Tokens.LastOrDefault(token =>
            token.Kind == VbaTokenKind.Keyword
            && token.Text.Equals("RaiseEvent", StringComparison.OrdinalIgnoreCase)
            && token.Range.End.Offset <= callSite.Callee.Range.Start.Offset);
        return raiseEventKeyword is null
            || syntaxTree.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "syntax.raiseEventStatementNotAllowedHere"
                && diagnostic.Range == raiseEventKeyword.Range);
    }

    private VbaCompleteCallArgumentMapping ApplyCompleteTypeCompatibility(
        VbaSourceDocument currentDocument,
        VbaSourceDefinition callableDefinition,
        VbaCallableSignature signature,
        VbaCallSiteSyntax callSite,
        VbaCompleteCallArgumentMapping completeMapping)
    {
        if (!completeMapping.Mapping.HasValidSignatureShape)
        {
            return completeMapping;
        }

        if (callSite.IsIncomplete)
        {
            return completeMapping;
        }

        var byRefMismatches = new List<string>();
        var valueMismatches = new List<string>();
        var hasIndeterminateEvidence = false;
        foreach (var mappedArgument in completeMapping.Mapping.Arguments)
        {
            if (mappedArgument.ParameterIndex is not int parameterIndex)
            {
                continue;
            }

            var argument = callSite.Arguments.First(argument =>
                argument.Index == mappedArgument.SourceIndex);
            if (argument.IsOmitted)
            {
                continue;
            }

            if (mappedArgument.IsParamArrayElement)
            {
                continue;
            }

            var parameter = signature.Parameters[parameterIndex];
            if (!TryGetArgumentTypeEvidence(
                    currentDocument,
                    argument,
                    out var evidence))
            {
                hasIndeterminateEvidence = true;
                continue;
            }

            VbaCanonicalTypeEvidence? parameterType = null;
            if (parameter.TypeReference is null
                || !TryGetCanonicalTypeEvidence(
                    currentDocument,
                    callableDefinition,
                    parameter.TypeReference,
                    out parameterType))
            {
                hasIndeterminateEvidence = true;
            }

            if (parameter.IsByRef is null)
            {
                hasIndeterminateEvidence = true;
                continue;
            }

            var argumentSubject = argument.Name is { } writtenName
                ? $"argument {mappedArgument.SourceIndex + 1} ('{writtenName}')"
                : $"argument {mappedArgument.SourceIndex + 1}";
            var parameterSubject = VbaCallDiagnosticText.GetParameterSubject(
                parameter,
                parameterIndex);
            if (parameter.IsByRef == true
                && evidence.Storage == VbaCallArgumentStorage.DirectStorage)
            {
                if (parameterType is not null && evidence.Type is not null
                    && !HasSameCanonicalType(parameterType, evidence.Type))
                {
                    var (expectedType, foundType) = GetDiagnosticTypeNames(
                        parameterType,
                        evidence.Type);
                    byRefMismatches.Add(
                        $"{argumentSubject} for {parameterSubject} ByRef type: expected {expectedType}, found {foundType}");
                }
                else if (parameterType is null || evidence.Type is null)
                {
                    hasIndeterminateEvidence = true;
                }

                if (evidence.IsArray is bool argumentIsArray)
                {
                    if (parameter.IsArray != argumentIsArray)
                    {
                        byRefMismatches.Add(
                            $"{argumentSubject} for {parameterSubject} ByRef array shape: expected {GetArrayShape(parameter.IsArray)}, found {GetArrayShape(argumentIsArray)}");
                    }
                }
                else
                {
                    hasIndeterminateEvidence = true;
                }

                continue;
            }

            if (parameterType is not null && evidence.Type is not null)
            {
                var valueCompatibility = ClassifyValueCompatibility(
                    parameterType,
                    evidence.Type);
                if (valueCompatibility == VbaValueTypeCompatibility.Incompatible)
                {
                    var (expectedType, foundType) = GetDiagnosticTypeNames(
                        parameterType,
                        evidence.Type);
                    valueMismatches.Add(
                        $"{argumentSubject} for {parameterSubject} type: expected {expectedType}, found {foundType}");
                }

                if (valueCompatibility == VbaValueTypeCompatibility.Indeterminate)
                {
                    hasIndeterminateEvidence = true;
                }
            }
            else
            {
                hasIndeterminateEvidence = true;
            }

            if (evidence.IsArray is bool valueIsArray)
            {
                if (parameter.IsArray != valueIsArray)
                {
                    valueMismatches.Add(
                        $"{argumentSubject} for {parameterSubject} array shape: expected {GetArrayShape(parameter.IsArray)}, found {GetArrayShape(valueIsArray)}");
                }
            }
            else
            {
                hasIndeterminateEvidence = true;
            }
        }

        var mismatches = byRefMismatches.Concat(valueMismatches).ToArray();
        var state = completeMapping.State == VbaCallCompatibilityState.Inapplicable
            || mismatches.Length > 0
            ? VbaCallCompatibilityState.Inapplicable
            : hasIndeterminateEvidence
                || completeMapping.Mapping.HasIndeterminateMapping
                || completeMapping.Mapping.ContextCompatibility
                    != VbaCallContextCompatibility.Compatible
                ? VbaCallCompatibilityState.Indeterminate
                : VbaCallCompatibilityState.Applicable;
        return completeMapping with
        {
            State = state,
            TypeMismatchReasons = Array.AsReadOnly(mismatches)
        };
    }

    private bool TryGetArgumentTypeEvidence(
        VbaSourceDocument currentDocument,
        VbaCallArgumentSyntax argument,
        out VbaCallArgumentTypeEvidence evidence)
    {
        var value = argument.ValueText?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            evidence = default!;
            return false;
        }

        var storage = VbaCallArgumentStorage.ValueTemporary;
        var isOuterParenthesized = false;
        while (IsOuterParenthesized(value))
        {
            isOuterParenthesized = true;
            value = value[1..^1].Trim();
        }

        var isDeclaredName = VbaIdentifier.IsIdentifier(value);
        if (!isDeclaredName
            && TryGetTypeCharacterDeclaredName(value, out var declaredName))
        {
            value = declaredName;
            isDeclaredName = true;
        }

        if (!isOuterParenthesized && isDeclaredName)
        {
            storage = VbaCallArgumentStorage.DirectStorage;
        }

        if (!isDeclaredName && GetKnownLiteralType(value) is { } literalType)
        {
            evidence = new VbaCallArgumentTypeEvidence(
                CreateIntrinsicTypeEvidence(literalType),
                IsArray: false,
                VbaCallArgumentStorage.ValueTemporary);
            return true;
        }

        if (!isDeclaredName
            && TryGetCompleteCallableResultTypeEvidence(
                currentDocument,
                argument,
                out evidence))
        {
            return true;
        }

        if (!isDeclaredName
            && TryResolveSimpleMemberTarget(
                currentDocument,
                argument,
                isOuterParenthesized,
                stripEmptyCallSuffix: true,
                out var memberCallableTarget))
        {
            return TryGetConvergedCallableResultTypeEvidence(
                currentDocument,
                memberCallableTarget,
                out evidence);
        }

        if (!isDeclaredName
            && TryResolveSimpleMemberTarget(
                currentDocument,
                argument,
                isOuterParenthesized,
                stripEmptyCallSuffix: false,
                out var memberTarget))
        {
            if (TryGetConvergedCallableResultTypeEvidence(
                    currentDocument,
                    memberTarget,
                    out evidence))
            {
                return true;
            }

            return TryGetConvergedStorageTypeEvidence(
                currentDocument,
                memberTarget,
                isOuterParenthesized
                    ? VbaCallArgumentStorage.ValueTemporary
                    : VbaCallArgumentStorage.DirectStorage,
                forceScalar: false,
                requireArrayStorage: false,
                out evidence);
        }

        if (!isDeclaredName
            && TryResolveIndexedArrayStorageTarget(
                currentDocument,
                argument,
                isOuterParenthesized,
                out var indexedTarget))
        {
            return TryGetConvergedStorageTypeEvidence(
                currentDocument,
                indexedTarget,
                isOuterParenthesized
                    ? VbaCallArgumentStorage.ValueTemporary
                    : VbaCallArgumentStorage.DirectStorage,
                forceScalar: true,
                requireArrayStorage: true,
                out evidence);
        }

        if (!isDeclaredName)
        {
            evidence = default!;
            return false;
        }

        var range = argument.ValueRange ?? argument.Range;
        var target = nameResolution.ResolveValueOutcome(
                currentDocument.Uri,
                new VbaPosition(range.Start.Line, range.Start.Character),
                qualifier: null,
                value)
            .Target;
        if (target is null || target.PhysicalDefinitions.Count == 0)
        {
            evidence = default!;
            return false;
        }

        if (TryGetConvergedCallableResultTypeEvidence(
                currentDocument,
                target,
                out evidence))
        {
            return true;
        }

        return TryGetConvergedStorageTypeEvidence(
            currentDocument,
            target,
            storage,
            forceScalar: false,
            requireArrayStorage: false,
            out evidence);
    }

    private bool TryGetConvergedCallableResultTypeEvidence(
        VbaSourceDocument currentDocument,
        VbaResolvedNameTarget target,
        out VbaCallArgumentTypeEvidence evidence)
    {
        var hasDefinition = false;
        VbaCanonicalTypeEvidence? convergedType = null;
        var expectedDefinitions = target.PhysicalDefinitions.ToArray();
        var definitions = GetCallableUseSiteDefinitions(currentDocument, target).ToArray();
        if (target is VbaPropertyNameTarget)
        {
            expectedDefinitions = expectedDefinitions
                .Where(definition => definition.PropertyAccessorKind is not (
                    VbaPropertyAccessorKind.Let or VbaPropertyAccessorKind.Set))
                .ToArray();
            definitions = definitions
                .Where(definition => definition.PropertyAccessorKind is not (
                    VbaPropertyAccessorKind.Let or VbaPropertyAccessorKind.Set))
                .ToArray();
        }

        var expectedIdentities = expectedDefinitions
            .Select(definition => definition.Identity)
            .ToHashSet();
        var definitionIdentities = definitions
            .Select(definition => definition.Identity)
            .ToHashSet();
        if (expectedDefinitions.Length == 0
            || definitions.Length != expectedDefinitions.Length
            || expectedIdentities.Count != expectedDefinitions.Length
            || definitionIdentities.Count != definitions.Length
            || !expectedIdentities.SetEquals(definitionIdentities))
        {
            evidence = default!;
            return false;
        }

        foreach (var definition in definitions)
        {
            var signature = definition.Signature;
            if (signature is null
                || VbaCallArgumentMapper.MapCompleteZeroArgument(
                        signature,
                        VbaCallArgumentMapper.GetContextCompatibility(
                            definition,
                            signature,
                            VbaCallContext.ValueRead)).State
                    != VbaCallCompatibilityState.Applicable
                || definition.TypeReference is null
                || !TryGetCanonicalTypeEvidence(
                    currentDocument,
                    definition,
                    definition.TypeReference,
                    out var definitionType))
            {
                evidence = default!;
                return false;
            }

            hasDefinition = true;
            if (convergedType is null)
            {
                convergedType = definitionType;
            }
            else if (!HasSameCanonicalType(definitionType, convergedType))
            {
                evidence = default!;
                return false;
            }
        }

        if (!hasDefinition || convergedType is null)
        {
            evidence = default!;
            return false;
        }

        evidence = new VbaCallArgumentTypeEvidence(
            convergedType,
            IsArray: false,
            VbaCallArgumentStorage.ValueTemporary);
        return true;
    }

    private bool TryGetCompleteCallableResultTypeEvidence(
        VbaSourceDocument currentDocument,
        VbaCallArgumentSyntax argument,
        out VbaCallArgumentTypeEvidence evidence)
    {
        evidence = default!;
        var syntaxTree = currentDocument.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(currentDocument.Uri, currentDocument.Text);
        var range = argument.ValueRange ?? argument.Range;
        var tokens = syntaxTree.TokenStream.Tokens
            .Where(token => range.Start.Offset <= token.Range.Start.Offset
                && token.Range.End.Offset <= range.End.Offset)
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.Comment
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.LineContinuation)
            .ToArray();
        while (HasCompleteOuterParenthesisPair(tokens))
        {
            tokens = tokens[1..^1];
        }

        if (tokens.Length < 3)
        {
            return false;
        }

        var nestedCall = syntaxTree.Module.ArgumentLists
            .Where(candidate => candidate.Form == VbaCallSyntaxForm.Parenthesized
                && !candidate.IsIncomplete
                && candidate.CalleeRange is not null)
            .Where(candidate => candidate.CalleeRange!.Start.Offset
                    == tokens[0].Range.Start.Offset
                && candidate.Range.End.Offset == tokens[^1].Range.End.Offset)
            .OrderByDescending(candidate => candidate.Range.Start.Offset)
            .FirstOrDefault();
        if (nestedCall?.CalleeRange is not { } calleeRange)
        {
            return false;
        }

        var calleeArgument = new VbaCallArgumentSyntax(
            Index: 0,
            Name: null,
            IsOmitted: false,
            calleeRange,
            nestedCall.Callee,
            calleeRange);
        if (!TryResolveSimpleMemberTarget(
                currentDocument,
                calleeArgument,
                isOuterParenthesized: false,
                stripEmptyCallSuffix: false,
                out var target))
        {
            return false;
        }

        var compatibility = AnalyzeCompleteCall(currentDocument, nestedCall, target);
        return TryGetConvergedCallableResultTypeEvidence(
            currentDocument,
            compatibility,
            out evidence);
    }

    private bool TryGetConvergedCallableResultTypeEvidence(
        VbaSourceDocument currentDocument,
        VbaConditionalCallCompatibility compatibility,
        out VbaCallArgumentTypeEvidence evidence)
    {
        var expectedDefinitions = compatibility.Target.PhysicalDefinitions.ToArray();
        var variants = compatibility.Variants.ToArray();
        if (compatibility.Target is VbaPropertyNameTarget)
        {
            expectedDefinitions = expectedDefinitions
                .Where(definition => definition.PropertyAccessorKind is not (
                    VbaPropertyAccessorKind.Let or VbaPropertyAccessorKind.Set))
                .ToArray();
            variants = variants
                .Where(variant => variant.Definition.PropertyAccessorKind is not (
                    VbaPropertyAccessorKind.Let or VbaPropertyAccessorKind.Set))
                .ToArray();
        }

        var expectedIdentities = expectedDefinitions
            .Select(definition => definition.Identity)
            .ToHashSet();
        var variantIdentities = variants
            .Select(variant => variant.Definition.Identity)
            .ToHashSet();
        if (expectedDefinitions.Length == 0
            || variants.Length != expectedDefinitions.Length
            || expectedIdentities.Count != expectedDefinitions.Length
            || variantIdentities.Count != variants.Length
            || !expectedIdentities.SetEquals(variantIdentities))
        {
            evidence = default!;
            return false;
        }

        VbaCanonicalTypeEvidence? convergedType = null;
        foreach (var variant in variants)
        {
            var signature = variant.Signature;
            if (variant.State != VbaCallCompatibilityState.Applicable
                || signature is null
                || (signature.CallableKind != VbaCallableKind.Function
                    && !(signature.CallableKind == VbaCallableKind.Property
                        && variant.Definition.PropertyAccess.HasFlag(
                            VbaPropertyAccess.Readable)))
                || variant.Definition.TypeReference is null
                || !TryGetCanonicalTypeEvidence(
                    currentDocument,
                    variant.Definition,
                    variant.Definition.TypeReference,
                    out var variantType))
            {
                evidence = default!;
                return false;
            }

            if (convergedType is not null
                && !HasSameCanonicalType(variantType, convergedType))
            {
                evidence = default!;
                return false;
            }

            convergedType = variantType;
        }

        if (convergedType is null)
        {
            evidence = default!;
            return false;
        }

        evidence = new VbaCallArgumentTypeEvidence(
            convergedType,
            IsArray: false,
            VbaCallArgumentStorage.ValueTemporary);
        return true;
    }

    private bool TryResolveSimpleMemberTarget(
        VbaSourceDocument currentDocument,
        VbaCallArgumentSyntax argument,
        bool isOuterParenthesized,
        bool stripEmptyCallSuffix,
        out VbaResolvedNameTarget target)
    {
        target = default!;
        var syntaxTree = currentDocument.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(currentDocument.Uri, currentDocument.Text);
        var range = argument.ValueRange ?? argument.Range;
        var tokens = syntaxTree.TokenStream.Tokens
            .Where(token => range.Start.Offset <= token.Range.Start.Offset
                && token.Range.End.Offset <= range.End.Offset)
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.Comment
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.LineContinuation)
            .ToArray();
        if (isOuterParenthesized)
        {
            while (HasCompleteOuterParenthesisPair(tokens))
            {
                tokens = tokens[1..^1];
            }
        }

        if (stripEmptyCallSuffix)
        {
            if (tokens.Length < 3
                || tokens[^2].Text != "("
                || tokens[^1].Text != ")")
            {
                return false;
            }

            tokens = tokens[..^2];
        }

        if (tokens.Length >= 2
            && VbaIdentifierSyntaxFacts.IsValidDeclaredName(tokens[^2])
            && tokens[^1].Range.Start.Offset == tokens[^2].Range.End.Offset
            && VbaLanguageVocabulary.TryGetTypeDeclarationCharacterTypeName(
                tokens[^1].Text,
                out _))
        {
            tokens = tokens[..^1];
        }

        var isLeadingDot = tokens.Length >= 2 && tokens[0].Text == ".";
        var nameStart = isLeadingDot ? 1 : 0;
        if (nameStart >= tokens.Length
            || (tokens.Length - nameStart) % 2 == 0)
        {
            return false;
        }

        var segments = new List<VbaPositionIdentifierSyntax>();
        for (var index = nameStart; index < tokens.Length; index++)
        {
            if ((index - nameStart) % 2 == 0)
            {
                if (!VbaIdentifierSyntaxFacts.IsValidDeclaredName(tokens[index]))
                {
                    return false;
                }

                segments.Add(new VbaPositionIdentifierSyntax(
                    tokens[index].Text,
                    tokens[index].Range,
                    tokens[index].Kind == VbaTokenKind.Keyword));
            }
            else if (tokens[index].Kind != VbaTokenKind.Punctuation
                || tokens[index].Text != ".")
            {
                return false;
            }
        }

        if (!isLeadingDot && segments.Count == 1)
        {
            var identifier = segments[0];
            var resolved = nameResolution.ResolveValueOutcome(
                    currentDocument.Uri,
                    new VbaPosition(
                        identifier.Range.Start.Line,
                        identifier.Range.Start.Character),
                    qualifier: null,
                    identifier.Name)
                .Target;
            if (resolved is null)
            {
                return false;
            }

            target = resolved;
            return true;
        }

        var memberAccess = new VbaMemberAccessSyntax(
            Array.AsReadOnly(segments.ToArray()),
            segments.Count - 1,
            IsLeadingDot: isLeadingDot,
            IsIncomplete: false,
            HasTrailingWhitespace: false,
            new VbaSyntaxRange(tokens[0].Range.Start, tokens[^1].Range.End));
        var withScopes = syntaxTree
            .GetPositionSyntax(range.Start.Line, range.Start.Character)
            .EnclosingWithScopes;
        if (memberChainResolution.TryResolveMemberChainDefinition(
                currentDocument,
                range.Start.Line,
                range.Start.Character,
                memberAccess,
                withScopes,
                out var memberDefinition))
        {
            if (memberDefinition is null)
            {
                return false;
            }

            target = resolutionPolicy.CreateNameTarget(memberDefinition);
            return true;
        }

        if (isLeadingDot || segments.Count < 2)
        {
            return false;
        }

        var qualifier = string.Join(
            '.',
            segments.Take(segments.Count - 1).Select(segment => segment.Name));
        var resolvedQualifiedTarget = nameResolution.ResolveValueOutcome(
                currentDocument.Uri,
                new VbaPosition(range.Start.Line, range.Start.Character),
                qualifier,
                segments[^1].Name)
            .Target;
        if (resolvedQualifiedTarget is null)
        {
            return false;
        }

        target = resolvedQualifiedTarget;
        return true;
    }

    private static bool HasCompleteOuterParenthesisPair(IReadOnlyList<VbaToken> tokens)
    {
        if (tokens.Count < 2 || tokens[0].Text != "(" || tokens[^1].Text != ")")
        {
            return false;
        }

        var depth = 0;
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Text == "(")
            {
                depth++;
            }
            else if (tokens[index].Text == ")")
            {
                depth--;
                if (depth == 0 && index != tokens.Count - 1)
                {
                    return false;
                }
            }
        }

        return depth == 0;
    }

    private bool TryResolveIndexedArrayStorageTarget(
        VbaSourceDocument currentDocument,
        VbaCallArgumentSyntax argument,
        bool isOuterParenthesized,
        out VbaResolvedNameTarget target)
    {
        target = default!;
        var syntaxTree = currentDocument.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(currentDocument.Uri, currentDocument.Text);
        var range = argument.ValueRange ?? argument.Range;
        var tokens = syntaxTree.TokenStream.Tokens
            .Where(token => range.Start.Offset <= token.Range.Start.Offset
                && token.Range.End.Offset <= range.End.Offset)
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.Comment
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.LineContinuation)
            .ToArray();
        if (isOuterParenthesized
            && tokens.Length >= 2
            && tokens[0].Text == "("
            && tokens[^1].Text == ")")
        {
            tokens = tokens[1..^1];
        }

        if (tokens.Length < 4)
        {
            return false;
        }

        var openParenthesisIndex = Array.FindIndex(
            tokens,
            token => token.Text == "(");

        if (openParenthesisIndex <= 0
            || openParenthesisIndex >= tokens.Length - 2
            || tokens[openParenthesisIndex].Text != "("
            || tokens[^1].Text != ")"
            || !HasCompleteIndexedArraySubscripts(
                tokens,
                openParenthesisIndex + 1,
                tokens.Length - 1,
                syntaxTree.Module.Kind))
        {
            return false;
        }

        var depth = 0;
        for (var index = openParenthesisIndex; index < tokens.Length; index++)
        {
            if (tokens[index].Text == "(")
            {
                depth++;
            }
            else if (tokens[index].Text == ")")
            {
                depth--;
                if (depth == 0 && index != tokens.Length - 1)
                {
                    return false;
                }
            }

            if (depth < 0)
            {
                return false;
            }
        }

        if (depth != 0)
        {
            return false;
        }

        var baseRange = new VbaSyntaxRange(
            tokens[0].Range.Start,
            tokens[openParenthesisIndex - 1].Range.End);
        if (!TryResolveSimpleMemberTarget(
                currentDocument,
                argument with
                {
                    Range = baseRange,
                    ValueRange = baseRange
                },
                isOuterParenthesized: false,
                stripEmptyCallSuffix: false,
                out var resolved)
            || resolved.PhysicalDefinitions.Count == 0)
        {
            return false;
        }

        target = resolved;
        return true;
    }

    private static bool HasCompleteIndexedArraySubscripts(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end,
        VbaModuleKind moduleKind)
    {
        var depth = 0;
        var subscriptStart = start;
        for (var index = start; index < end; index++)
        {
            if (tokens[index].Text == "(")
            {
                depth++;
            }
            else if (tokens[index].Text == ")")
            {
                depth--;
                if (depth < 0)
                {
                    return false;
                }
            }
            else if (tokens[index].Text == "," && depth == 0)
            {
                if (!VbaExecutableExpressionSyntax.IsComplete(
                        tokens,
                        subscriptStart,
                        index,
                        moduleKind,
                        allowLeadingMemberAccess: false))
                {
                    return false;
                }

                subscriptStart = index + 1;
            }
        }

        return depth == 0
            && VbaExecutableExpressionSyntax.IsComplete(
                tokens,
                subscriptStart,
                end,
                moduleKind,
                allowLeadingMemberAccess: false);
    }

    private bool TryGetConvergedStorageTypeEvidence(
        VbaSourceDocument currentDocument,
        VbaResolvedNameTarget target,
        VbaCallArgumentStorage storage,
        bool forceScalar,
        bool requireArrayStorage,
        out VbaCallArgumentTypeEvidence evidence)
    {
        var hasDefinition = false;
        var hasCompleteTypeEvidence = true;
        VbaCanonicalTypeEvidence? convergedType = null;
        bool? convergedShape = null;
        var hasConvergedShape = true;
        foreach (var definition in GetUseSiteDefinitions(currentDocument, target))
        {
            if (definition.Kind is not (
                    VbaSourceDefinitionKind.Variable
                    or VbaSourceDefinitionKind.Parameter
                    or VbaSourceDefinitionKind.TypeMember)
                || requireArrayStorage && !definition.IsArray)
            {
                evidence = default!;
                return false;
            }

            hasDefinition = true;
            var definitionShape = forceScalar ? false : definition.IsArray;
            if (convergedShape is null)
            {
                convergedShape = definitionShape;
            }
            else if (convergedShape != definitionShape)
            {
                hasConvergedShape = false;
            }

            if (definition.TypeReference is null
                || !TryGetCanonicalTypeEvidence(
                    currentDocument,
                    definition,
                    definition.TypeReference,
                    out var definitionType))
            {
                hasCompleteTypeEvidence = false;
                continue;
            }

            if (convergedType is null)
            {
                convergedType = definitionType;
            }
            else if (!HasSameCanonicalType(definitionType, convergedType))
            {
                hasCompleteTypeEvidence = false;
            }
        }

        evidence = new VbaCallArgumentTypeEvidence(
            hasCompleteTypeEvidence ? convergedType : null,
            hasConvergedShape ? convergedShape : null,
            storage);
        return hasDefinition;
    }

    private bool TryGetCanonicalTypeEvidence(
        VbaSourceDocument currentDocument,
        VbaSourceDefinition owner,
        VbaTypeReference typeReference,
        out VbaCanonicalTypeEvidence evidence)
    {
        if (typeReference.Qualifier is null
            && VbaLanguageVocabulary.TryGetCanonicalTypeName(
                typeReference.Name,
                out var canonicalName))
        {
            evidence = CreateIntrinsicTypeEvidence(canonicalName);
            return true;
        }

        VbaResolvedNameTarget? target;
        if (owner.Identity.Origin == VbaDefinitionOrigin.ProjectReference)
        {
            target = nameResolution.ResolveProjectReferenceTypeDefinition(
                    owner.Identity.ReferenceName ?? owner.ModuleName,
                    typeReference) is { } referenceTypeDefinition
                ? new VbaDefinitionNameTarget(referenceTypeDefinition)
                : null;
        }
        else
        {
            var ownerDocument = nameResolution.FindDocument(owner.Uri);
            target = ownerDocument is null
                ? null
                : nameResolution.ResolveTypeDefinitionOutcome(
                        ownerDocument,
                        typeReference)
                    .Target;
        }
        if (target is null)
        {
            evidence = default!;
            return false;
        }

        var definition = target.SelectedDefinition;
        var qualifier = typeReference.Qualifier is null
            ? null
            : nameResolution.GetCanonicalQualifierName(
                definition,
                typeReference.Qualifier) ?? typeReference.Qualifier;
        var preferredReferenceQualifier =
            nameResolution.GetPreferredReferenceQualifierName(definition);
        if (owner.Identity.Origin == VbaDefinitionOrigin.ProjectReference
            && typeReference.Qualifier is not null
            && nameResolution.IsReferenceQualifierAmbiguous(typeReference.Qualifier)
            && !string.IsNullOrEmpty(preferredReferenceQualifier))
        {
            qualifier = preferredReferenceQualifier;
        }

        evidence = new VbaCanonicalTypeEvidence(
            qualifier is null
                ? target.CanonicalName
                : $"{qualifier}.{target.CanonicalName}",
            !string.IsNullOrEmpty(preferredReferenceQualifier)
                ? $"{preferredReferenceQualifier}.{target.CanonicalName}"
                : null,
            IntrinsicName: null,
            target.Identity,
            GetCanonicalTypeCategory(definition.Kind));
        return true;
    }

    private static VbaCanonicalTypeEvidence CreateIntrinsicTypeEvidence(
        string typeName)
    {
        var canonicalName = VbaLanguageVocabulary.TryGetCanonicalTypeName(
            typeName,
            out var normalizedName)
                ? normalizedName
                : typeName;
        return new VbaCanonicalTypeEvidence(
            canonicalName,
            ReferenceQualifiedDisplayName: null,
            canonicalName,
            Identity: null,
            canonicalName.Equals("Variant", StringComparison.OrdinalIgnoreCase)
                ? VbaCanonicalTypeCategory.Variant
                : canonicalName.Equals("Object", StringComparison.OrdinalIgnoreCase)
                    ? VbaCanonicalTypeCategory.Object
                    : VbaCanonicalTypeCategory.IntrinsicScalar);
    }

    private static VbaCanonicalTypeCategory GetCanonicalTypeCategory(
        VbaSourceDefinitionKind kind)
        => kind switch
        {
            VbaSourceDefinitionKind.Class or VbaSourceDefinitionKind.Form =>
                VbaCanonicalTypeCategory.Class,
            VbaSourceDefinitionKind.Enum => VbaCanonicalTypeCategory.Enum,
            VbaSourceDefinitionKind.Type => VbaCanonicalTypeCategory.UserDefinedType,
            _ => VbaCanonicalTypeCategory.Other
        };

    private static bool HasSameCanonicalType(
        VbaCanonicalTypeEvidence left,
        VbaCanonicalTypeEvidence right)
        => left.IntrinsicName is not null && right.IntrinsicName is not null
            ? left.IntrinsicName.Equals(
                right.IntrinsicName,
                StringComparison.OrdinalIgnoreCase)
            : left.Identity is not null
                && right.Identity is not null
                && left.Identity.Equals(right.Identity);

    private static (string Expected, string Found) GetDiagnosticTypeNames(
        VbaCanonicalTypeEvidence expected,
        VbaCanonicalTypeEvidence found)
    {
        if (!expected.DisplayName.Equals(found.DisplayName, StringComparison.OrdinalIgnoreCase)
            || HasSameCanonicalType(expected, found))
        {
            return (expected.DisplayName, found.DisplayName);
        }

        return (
            expected.ReferenceQualifiedDisplayName ?? expected.DisplayName,
            found.ReferenceQualifiedDisplayName ?? found.DisplayName);
    }

    private static bool IsOuterParenthesized(string value)
    {
        var tokens = VbaTokenStream.FromText(value).Tokens
            .Where(token => token.Kind is not (
                VbaTokenKind.Whitespace
                or VbaTokenKind.NewLine
                or VbaTokenKind.LineContinuation
                or VbaTokenKind.Comment))
            .ToArray();
        if (tokens.Length < 2
            || tokens[0].Text != "("
            || tokens[^1].Text != ")")
        {
            return false;
        }

        var depth = 0;
        for (var index = 0; index < tokens.Length; index++)
        {
            if (tokens[index].Text == "(")
            {
                depth++;
            }
            else if (tokens[index].Text == ")")
            {
                depth--;
                if (depth == 0 && index != tokens.Length - 1)
                {
                    return false;
                }
            }

            if (depth < 0)
            {
                return false;
            }
        }

        return depth == 0;
    }

    private static bool TryGetTypeCharacterDeclaredName(
        string value,
        out string declaredName)
    {
        var tokens = VbaTokenStream.FromText(value).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.LineContinuation
                and not VbaTokenKind.Comment)
            .ToArray();
        if (tokens.Length == 2
            && VbaIdentifier.IsIdentifier(tokens[0].Text)
            && tokens[1].Range.Start.Offset == tokens[0].Range.End.Offset
            && VbaLanguageVocabulary.TryGetTypeDeclarationCharacterTypeName(
                tokens[1].Text,
                out _))
        {
            declaredName = tokens[0].Text;
            return true;
        }

        declaredName = string.Empty;
        return false;
    }

    private static string GetArrayShape(bool isArray)
        => isArray ? "array" : "scalar";

    private static VbaValueTypeCompatibility ClassifyValueCompatibility(
        VbaCanonicalTypeEvidence expectedType,
        VbaCanonicalTypeEvidence actualType)
    {
        if (HasSameCanonicalType(expectedType, actualType))
        {
            return VbaValueTypeCompatibility.Exact;
        }

        if (expectedType.Category == VbaCanonicalTypeCategory.Variant
            && actualType.Category is
                VbaCanonicalTypeCategory.IntrinsicScalar
                or VbaCanonicalTypeCategory.Enum
                or VbaCanonicalTypeCategory.Object
                or VbaCanonicalTypeCategory.Class)
        {
            return VbaValueTypeCompatibility.Coercion;
        }

        if (expectedType.Category == VbaCanonicalTypeCategory.Object
            && actualType.Category == VbaCanonicalTypeCategory.Class)
        {
            return VbaValueTypeCompatibility.Assignment;
        }

        if (expectedType.Category is VbaCanonicalTypeCategory.Object
                or VbaCanonicalTypeCategory.Class
            && actualType.Category is VbaCanonicalTypeCategory.IntrinsicScalar
                or VbaCanonicalTypeCategory.Enum
                or VbaCanonicalTypeCategory.UserDefinedType)
        {
            return VbaValueTypeCompatibility.Incompatible;
        }

        if (IsSafeNumericWidening(expectedType, actualType))
        {
            return VbaValueTypeCompatibility.Coercion;
        }

        return VbaValueTypeCompatibility.Indeterminate;
    }

    private static bool IsSafeNumericWidening(
        VbaCanonicalTypeEvidence expectedType,
        VbaCanonicalTypeEvidence actualType)
        => (actualType.IntrinsicName, expectedType.IntrinsicName) switch
        {
            ("Byte", "Integer" or "Long" or "LongLong" or "LongPtr"
                or "Single" or "Double" or "Currency") => true,
            ("Integer", "Long" or "LongLong" or "LongPtr"
                or "Single" or "Double" or "Currency") => true,
            ("Long", "LongLong" or "LongPtr" or "Single" or "Double"
                or "Currency") => true,
            ("LongPtr", "LongLong") => true,
            ("Single", "Double") => true,
            ("Currency", "Single" or "Double") => true,
            _ => false
        };

    private enum VbaValueTypeCompatibility
    {
        Exact,
        Assignment,
        Coercion,
        Incompatible,
        Indeterminate
    }

    private enum VbaCanonicalTypeCategory
    {
        IntrinsicScalar,
        Variant,
        Object,
        Class,
        Enum,
        UserDefinedType,
        Other
    }

    private enum VbaCallArgumentStorage
    {
        DirectStorage,
        ValueTemporary
    }

    private sealed record VbaCallArgumentTypeEvidence(
        VbaCanonicalTypeEvidence? Type,
        bool? IsArray,
        VbaCallArgumentStorage Storage);

    private sealed record VbaCanonicalTypeEvidence(
        string DisplayName,
        string? ReferenceQualifiedDisplayName,
        string? IntrinsicName,
        VbaResolvedNameTargetIdentity? Identity,
        VbaCanonicalTypeCategory Category);

    private static VbaCallSiteSyntax CreateCompleteCallSite(
        VbaArgumentListSyntax argumentList)
    {
        var calleeRange = argumentList.CalleeRange ?? argumentList.Range;
        var calleeIdentifier = new VbaPositionIdentifierSyntax(
            argumentList.Callee,
            calleeRange,
            IsKeyword: false);
        var callee = new VbaMemberAccessSyntax(
            [calleeIdentifier],
            TargetSegmentIndex: 0,
            IsLeadingDot: argumentList.Callee.StartsWith(".", StringComparison.Ordinal),
            IsIncomplete: false,
            HasTrailingWhitespace: false,
            calleeRange);
        var arguments = argumentList.Arguments
            .Select((argument, index) => new VbaCallArgumentSyntax(
                index,
                argument.Name,
                argument.Kind == VbaArgumentKind.Omitted,
                argument.Range,
                argument.ValueText,
                argument.ValueRange))
            .ToArray();
        return new VbaCallSiteSyntax(
            argumentList.Form,
            callee,
            arguments,
            ActiveArgumentIndex: arguments.Length,
            ActiveNamedArgument: null,
            IsIncomplete: argumentList.IsIncomplete);
    }

    private static int GetContextRank(VbaCallContextCompatibility compatibility)
        => compatibility switch
        {
            VbaCallContextCompatibility.Compatible => 2,
            VbaCallContextCompatibility.Indeterminate => 1,
            _ => 0
        };

    private int GetTypeCompatibilityRank(
        VbaSourceDocument currentDocument,
        VbaSourceDefinition callableDefinition,
        VbaCallableSignature signature,
        VbaCallArgumentMapping mapping,
        VbaCallSiteSyntax callSite)
    {
        var rank = 0;
        foreach (var mappedArgument in mapping.Arguments)
        {
            if (mappedArgument.ParameterIndex is int parameterIndex
                && callSite.Arguments.FirstOrDefault(argument =>
                    argument.Index == mappedArgument.SourceIndex) is { } argument
                && IsPreferredKnownTypeCompatibility(
                    currentDocument,
                    callableDefinition,
                    argument,
                    signature.Parameters[parameterIndex]))
            {
                rank++;
            }
        }

        if (mapping.ActiveParameter is int activeParameter
            && callSite.Arguments.FirstOrDefault(argument =>
                argument.Index == callSite.ActiveArgumentIndex) is { } activeArgument
            && IsPreferredKnownTypeCompatibility(
                currentDocument,
                callableDefinition,
                activeArgument,
                signature.Parameters[activeParameter]))
        {
            rank++;
        }

        return rank;
    }

    private bool IsPreferredKnownTypeCompatibility(
        VbaSourceDocument currentDocument,
        VbaSourceDefinition callableDefinition,
        VbaCallArgumentSyntax argument,
        VbaCallableParameter parameter)
    {
        var parameterType = parameter.TypeReference;
        if (parameterType is null
            || parameter.IsByRef is null
            || !TryGetCanonicalTypeEvidence(
                currentDocument,
                callableDefinition,
                parameterType,
                out var canonicalParameterType)
            || !TryGetArgumentTypeEvidence(
                currentDocument,
                argument,
                out var evidence)
            || evidence.Type is not { } argumentType
            || evidence.IsArray is not bool argumentIsArray
            || parameter.IsArray != argumentIsArray)
        {
            return false;
        }

        var compatibility = ClassifyValueCompatibility(
            canonicalParameterType,
            argumentType);
        return compatibility == VbaValueTypeCompatibility.Exact
            || (compatibility == VbaValueTypeCompatibility.Assignment
                && (parameter.IsByRef == false
                    || evidence.Storage == VbaCallArgumentStorage.ValueTemporary));
    }

    private static string? GetKnownLiteralType(string? valueText)
    {
        var value = valueText?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var significantTokens = VbaTokenStream.FromText(value)
            .Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.Comment
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.LineContinuation)
            .ToArray();
        if (significantTokens.Length == 1
            && significantTokens[0].Kind == VbaTokenKind.StringLiteral
            && significantTokens[0].Text.Length >= 2
            && significantTokens[0].Text[^1] == '"')
        {
            return "String";
        }

        if (significantTokens.Length == 1
            && (significantTokens[0].Text.Equals("True", StringComparison.OrdinalIgnoreCase)
                || significantTokens[0].Text.Equals("False", StringComparison.OrdinalIgnoreCase)))
        {
            return "Boolean";
        }

        if (significantTokens.Length == 1
            && significantTokens[0].Kind == VbaTokenKind.DateLiteral
            && significantTokens[0].Text.Length >= 2
            && significantTokens[0].Text[^1] == '#')
        {
            return "Date";
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(
                value,
                @"\A(?:(?:&[Hh][0-9A-Fa-f]+|&[Oo]?[0-7]+)[%&^]|(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[DdEe][+-]?[0-9]+)?[%&^!#@])\z",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            return null;
        }

        return value[^1] switch
        {
            '&' => "Long",
            '%' => "Integer",
            '@' => "Currency",
            '!' => "Single",
            '#' => "Double",
            '^' => "LongLong",
            _ => null
        };
    }

    private static VbaCallContext GetCallContext(
        VbaSourceDocument currentDocument,
        VbaCallSiteSyntax callSite)
    {
        var syntaxTree = currentDocument.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(currentDocument.Uri, currentDocument.Text);
        var prefix = GetLogicalTokensBefore(
            syntaxTree.TokenStream.Tokens,
            callSite.Callee.Range.Start.Offset);
        if (prefix.LastOrDefault()?.Text.Equals(
                "RaiseEvent",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            return VbaCallContext.RaiseEvent;
        }

        if (prefix.Count == 1
            && prefix[0].Text.Equals("Set", StringComparison.OrdinalIgnoreCase))
        {
            return VbaCallContext.PropertySetAssignment;
        }

        if (prefix.Count == 1
            && prefix[0].Text.Equals("Let", StringComparison.OrdinalIgnoreCase))
        {
            return VbaCallContext.PropertyLetAssignment;
        }

        if (IsPropertyAssignmentTargetCall(currentDocument, callSite))
        {
            return VbaCallContext.PropertyLetAssignment;
        }

        if (callSite.Form == VbaCallSyntaxForm.BareValueRead)
        {
            return VbaCallContext.ValueRead;
        }

        if (prefix.Count == 0
            && callSite.Form == VbaCallSyntaxForm.Parenthesized
            && callSite.IsIncomplete)
        {
            return VbaCallContext.Indeterminate;
        }

        if (callSite.Form == VbaCallSyntaxForm.Statement
            || prefix.Count == 0
            || (prefix.Count == 1
                && prefix[0].Text.Equals("Call", StringComparison.OrdinalIgnoreCase)))
        {
            return VbaCallContext.StatementInvocation;
        }

        return VbaCallContext.ValueRead;
    }

    /// <summary>
    /// Gets the positional and named argument forms still available at the active argument.
    /// </summary>
    public VbaCallArgumentAvailability GetCallArgumentAvailability(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaPositionSyntax positionSyntax)
    {
        var callSite = positionSyntax.CallSite;
        if (callSite is null)
        {
            return VbaCallArgumentAvailability.None;
        }

        var isRaiseEventCall = IsRaiseEventCall(currentDocument, callSite);
        if (isRaiseEventCall
            && HasRaiseEventPlacementDiagnostic(currentDocument, callSite))
        {
            return VbaCallArgumentAvailability.None;
        }

        if (!TryResolveCallableNameTarget(
                currentDocument,
                line,
                character,
                callSite,
                positionSyntax.EnclosingWithScopes,
                out var target)
            || target is null)
        {
            if (isRaiseEventCall)
            {
                var raiseEventHasPriorNamedArgument = GetPriorArguments(callSite)
                    .Any(argument => argument.Name is not null);
                return new VbaCallArgumentAvailability(
                    null,
                    null,
                    !raiseEventHasPriorNamedArgument,
                    []);
            }

            if (TryCreateStandardLibrarySignature(callSite, out var intrinsicSignature))
            {
                return AnalyzeResolvedArguments(
                    definition: null,
                    intrinsicSignature,
                    callSite);
            }

            var hasPriorNamedArgument = GetPriorArguments(callSite)
                .Any(argument => argument.Name is not null);
            return new VbaCallArgumentAvailability(
                null,
                null,
                !hasPriorNamedArgument,
                []);
        }

        var definition = target.SelectedDefinition;
        var callContext = GetCallContext(currentDocument, callSite);
        if (target.IsConditionalFamily)
        {
            return AnalyzeConditionalArguments(
                currentDocument,
                target,
                callSite);
        }

        var signature = definition.Signature;
        if (signature is null)
        {
            if (definition.IsArray)
            {
                var hasInvalidPriorIndex = GetPriorArguments(callSite)
                    .Any(argument => argument.Name is not null || argument.IsOmitted);
                return new VbaCallArgumentAvailability(
                    definition,
                    signature,
                    !hasInvalidPriorIndex,
                    []);
            }

            // A resolved scalar, default-member ambiguity, or callable without signature
            // metadata must not inherit the permissive unresolved-name behavior.
            return new VbaCallArgumentAvailability(definition, signature, false, []);
        }

        if (IsExplicitlyWriteOnlyProperty(definition))
        {
            if (callContext != VbaCallContext.Indeterminate
                    && !IsPropertyAssignmentTargetCall(currentDocument, callSite)
                || !TryCreateSetterInvocationSignature(signature, out signature))
            {
                return new VbaCallArgumentAvailability(definition, signature, false, []);
            }
        }
        else if (!CanSupplyCallArguments(definition))
        {
            return new VbaCallArgumentAvailability(definition, signature, false, []);
        }

        return AnalyzeResolvedArguments(
            definition,
            signature,
            callSite,
            VbaCallArgumentMapper.GetContextCompatibility(definition, signature, callContext));
    }

    private static VbaCallArgumentAvailability AnalyzeConditionalArguments(
        VbaSourceDocument currentDocument,
        VbaResolvedNameTarget target,
        VbaCallSiteSyntax callSite)
    {
        var variantAvailability = new List<VbaCallArgumentAvailability>();
        var callContext = GetCallContext(currentDocument, callSite);
        foreach (var definition in GetUseSiteDefinitions(currentDocument, target))
        {
            var signature = definition.Signature;
            if (signature is null)
            {
                continue;
            }

            if (IsExplicitlyWriteOnlyProperty(definition))
            {
                if (callContext != VbaCallContext.Indeterminate
                        && !IsPropertyAssignmentTargetCall(currentDocument, callSite)
                    || !TryCreateSetterInvocationSignature(signature, out signature))
                {
                    continue;
                }
            }
            else if (!CanSupplyCallArguments(definition))
            {
                continue;
            }

            variantAvailability.Add(AnalyzeResolvedArguments(
                definition,
                signature,
                callSite,
                VbaCallArgumentMapper.GetContextCompatibility(definition, signature, callContext)));
        }

        if (variantAvailability.Count == 0)
        {
            return new VbaCallArgumentAvailability(
                target.SelectedDefinition,
                null,
                false,
                []);
        }

        var eligibleAvailability = variantAvailability
            .Where(availability => availability.ContextCompatibility
                != VbaCallContextCompatibility.Incompatible)
            .ToArray();
        if (eligibleAvailability.Length == 0)
        {
            return new VbaCallArgumentAvailability(
                target.SelectedDefinition,
                null,
                false,
                []);
        }

        var remainingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remainingNameCounts = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        var remainingParameters = new List<VbaCallableParameter>();
        foreach (var availability in eligibleAvailability)
        {
            foreach (var parameter in availability.RemainingNamedParameters
                .DistinctBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase))
            {
                remainingNameCounts[parameter.Name] =
                    remainingNameCounts.GetValueOrDefault(parameter.Name) + 1;
                if (remainingNames.Add(parameter.Name))
                {
                    remainingParameters.Add(parameter);
                }
            }
        }

        var selected = eligibleAvailability.FirstOrDefault(availability =>
                availability.CallableDefinition?.Identity
                    == target.SelectedDefinition.Identity)
            ?? eligibleAvailability[0];
        return new VbaCallArgumentAvailability(
            selected.CallableDefinition,
            selected.Signature,
            eligibleAvailability.Any(availability =>
                availability.AllowsPositionalExpression),
            Array.AsReadOnly(remainingParameters.ToArray()),
            remainingNameCounts
                .Where(pair => pair.Value < eligibleAvailability.Length)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            selected.ContextCompatibility);
    }

    private static bool CanSupplyCallArguments(VbaSourceDefinition definition)
        => definition.Kind != VbaSourceDefinitionKind.Property
            || definition.PropertyAccess.HasFlag(VbaPropertyAccess.Readable);

    private static bool IsExplicitlyWriteOnlyProperty(VbaSourceDefinition definition)
        => definition.Kind == VbaSourceDefinitionKind.Property
            && definition.PropertyAccess.HasFlag(VbaPropertyAccess.Writable)
            && !definition.PropertyAccess.HasFlag(VbaPropertyAccess.Readable);

    private static bool TryCreateSetterInvocationSignature(
        VbaCallableSignature signature,
        out VbaCallableSignature invocationSignature)
    {
        invocationSignature = signature;
        if (signature.Parameters.Count == 0)
        {
            return false;
        }

        var openParenthesis = signature.Label.IndexOf('(');
        var closeParenthesis = signature.Label.LastIndexOf(')');
        if (openParenthesis < 0 || closeParenthesis <= openParenthesis)
        {
            return false;
        }

        var invocationParameters = signature.Parameters
            .Take(signature.Parameters.Count - 1)
            .ToArray();
        var invocationLabel = signature.Label[..(openParenthesis + 1)]
            + string.Join(", ", invocationParameters.Select(parameter => parameter.Label))
            + signature.Label[closeParenthesis..];
        invocationSignature = signature with
        {
            Label = invocationLabel,
            Parameters = invocationParameters
        };
        return true;
    }

    private static bool IsPropertyAssignmentTargetCall(
        VbaSourceDocument currentDocument,
        VbaCallSiteSyntax callSite)
    {
        if (callSite.Form == VbaCallSyntaxForm.PropertyAssignment)
        {
            return true;
        }

        if (callSite.Form != VbaCallSyntaxForm.Parenthesized)
        {
            return false;
        }

        var syntaxTree = currentDocument.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(currentDocument.Uri, currentDocument.Text);
        var prefix = GetLogicalTokensBefore(
            syntaxTree.TokenStream.Tokens,
            callSite.Callee.Range.Start.Offset);
        if (prefix.Count == 1
            && (prefix[0].Text.Equals("Set", StringComparison.OrdinalIgnoreCase)
                || prefix[0].Text.Equals("Let", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!HasAssignmentTargetPrefix(
                syntaxTree.TokenStream.Tokens,
                callSite.Callee.Range.Start.Offset))
        {
            return false;
        }

        var tokens = GetLogicalTokensAfter(
            syntaxTree.TokenStream.Tokens,
            callSite.Callee.Range.End.Offset);
        if (tokens.Count == 0
            || tokens[0].Kind != VbaTokenKind.Punctuation
            || tokens[0].Text != "(")
        {
            return false;
        }

        var depth = 0;
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Kind != VbaTokenKind.Punctuation)
            {
                continue;
            }

            if (token.Text == "(")
            {
                depth++;
                continue;
            }

            if (token.Text != ")")
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return index + 1 < tokens.Count
                    && tokens[index + 1].Kind == VbaTokenKind.Operator
                    && tokens[index + 1].Text == "=";
            }
        }

        return false;
    }

    private static bool HasAssignmentTargetPrefix(
        IReadOnlyList<VbaToken> tokens,
        int offset)
    {
        var prefix = GetLogicalTokensBefore(tokens, offset);
        return prefix.Count == 0
            || (prefix.Count == 1
                && prefix[0].Kind == VbaTokenKind.Keyword
                && prefix[0].Text.Equals("Let", StringComparison.OrdinalIgnoreCase))
            || (prefix.Count == 1
                && prefix[0].Kind == VbaTokenKind.Keyword
                && prefix[0].Text.Equals("Set", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<VbaToken> GetLogicalTokensBefore(
        IReadOnlyList<VbaToken> tokens,
        int offset)
    {
        var prefix = new List<VbaToken>();
        var continuesOnNextLine = false;
        var parenthesisDepth = 0;
        foreach (var token in tokens)
        {
            if (token.Range.Start.Offset >= offset)
            {
                break;
            }

            if (token.Kind == VbaTokenKind.Whitespace)
            {
                continue;
            }

            if (token.Kind == VbaTokenKind.LineContinuation)
            {
                continuesOnNextLine = true;
                continue;
            }

            if (token.Kind == VbaTokenKind.NewLine)
            {
                if (continuesOnNextLine)
                {
                    continuesOnNextLine = false;
                    continue;
                }

                prefix.Clear();
                parenthesisDepth = 0;
                continue;
            }

            if (token.Kind == VbaTokenKind.Comment)
            {
                prefix.Clear();
                parenthesisDepth = 0;
                continue;
            }

            continuesOnNextLine = false;
            if (token.Kind == VbaTokenKind.Punctuation)
            {
                if (token.Text == ":" && parenthesisDepth == 0)
                {
                    prefix.Clear();
                    continue;
                }

                if (token.Text == "(")
                {
                    parenthesisDepth++;
                }
                else if (token.Text == ")" && parenthesisDepth > 0)
                {
                    parenthesisDepth--;
                }
            }

            if (parenthesisDepth == 0
                && token.Kind == VbaTokenKind.Keyword
                && (token.Text.Equals("Then", StringComparison.OrdinalIgnoreCase)
                    || token.Text.Equals("Else", StringComparison.OrdinalIgnoreCase)))
            {
                prefix.Clear();
                continue;
            }

            prefix.Add(token);
        }

        return prefix;
    }

    private static IReadOnlyList<VbaToken> GetLogicalTokensAfter(
        IReadOnlyList<VbaToken> tokens,
        int offset)
    {
        var result = new List<VbaToken>();
        var continuesOnNextLine = false;
        foreach (var token in tokens.Where(token => token.Range.End.Offset > offset))
        {
            if (token.Kind == VbaTokenKind.Whitespace)
            {
                continue;
            }

            if (token.Kind == VbaTokenKind.LineContinuation)
            {
                continuesOnNextLine = true;
                continue;
            }

            if (token.Kind == VbaTokenKind.NewLine)
            {
                if (continuesOnNextLine)
                {
                    continuesOnNextLine = false;
                    continue;
                }

                break;
            }

            if (token.Kind == VbaTokenKind.Comment)
            {
                break;
            }

            continuesOnNextLine = false;
            result.Add(token);
        }

        return result;
    }

    private bool TryResolveCallableTarget(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaCallSiteSyntax callSite,
        IReadOnlyList<VbaWithScopeSyntax> withScopes,
        out VbaSourceDefinition? definition)
    {
        if (TryResolveCallableNameTarget(
                currentDocument,
                line,
                character,
                callSite,
                withScopes,
                out var target))
        {
            definition = target?.SelectedDefinition;
            return true;
        }

        definition = null;
        return false;
    }

    private bool TryResolveCallableNameTarget(
        VbaSourceDocument currentDocument,
        int line,
        int character,
        VbaCallSiteSyntax callSite,
        IReadOnlyList<VbaWithScopeSyntax> withScopes,
        out VbaResolvedNameTarget? target)
    {
        if (GetCallContext(currentDocument, callSite) == VbaCallContext.RaiseEvent)
        {
            target = ResolveCurrentDocumentEventTarget(currentDocument, callSite);
            return true;
        }

        if ((callSite.Callee.IsLeadingDot || callSite.Callee.Segments.Count > 1)
            && memberChainResolution.TryResolveMemberChainDefinition(
                currentDocument,
                line,
                character,
                callSite.Callee,
                withScopes,
                out var memberDefinition))
        {
            target = memberDefinition is null
                ? null
                : resolutionPolicy.CreateNameTarget(memberDefinition);
            return true;
        }

        var targetSyntax = callSite.Callee.Target;
        if (targetSyntax is null)
        {
            target = null;
            return false;
        }

        var qualifier = callSite.Callee.TargetSegmentIndex > 0
            ? string.Join(
                '.',
                callSite.Callee.Segments
                    .Take(callSite.Callee.TargetSegmentIndex)
                    .Select(segment => segment.Name))
            : null;
        target = nameResolution.ResolveValueOutcome(
                currentDocument.Uri,
                new VbaPosition(line, character),
                qualifier,
                targetSyntax.Name)
            .Target;
        return true;
    }

    private VbaResolvedNameTarget? ResolveCurrentDocumentEventTarget(
        VbaSourceDocument currentDocument,
        VbaCallSiteSyntax callSite)
    {
        var eventTargetSyntax = callSite.Callee.Target;
        return eventTargetSyntax is not null
            && !callSite.Callee.IsLeadingDot
            && callSite.Callee.Segments.Count == 1
                ? nameResolution.ResolveCurrentDocumentEventOutcome(
                    currentDocument.Uri,
                    eventTargetSyntax.Name).Target
                : null;
    }

    private static bool TryCreateStandardLibrarySignature(
        VbaCallSiteSyntax callSite,
        out VbaCallableSignature signature)
    {
        var segments = callSite.Callee.Segments;
        VbaStandardLibraryPotentialReceiverMemberSyntaxFact? member = null;
        if (segments.Count == 1)
        {
            VbaStandardLibrarySyntaxFacts.TryGetGlobalPotentialReceiverMember(
                segments[0].Name,
                out member!);
        }
        else if (segments.Count == 2
            && segments[0].Name.Equals("VBA", StringComparison.OrdinalIgnoreCase))
        {
            VbaStandardLibrarySyntaxFacts.TryGetGlobalPotentialReceiverMember(
                segments[1].Name,
                out member!);
        }
        else if (segments.Count == 2)
        {
            VbaStandardLibrarySyntaxFacts.TryGetOwnedPotentialReceiverMember(
                segments[0].Name,
                segments[1].Name,
                out member!);
        }
        else if (segments.Count == 3
            && segments[0].Name.Equals("VBA", StringComparison.OrdinalIgnoreCase))
        {
            VbaStandardLibrarySyntaxFacts.TryGetOwnedPotentialReceiverMember(
                segments[1].Name,
                segments[2].Name,
                out member!);
        }

        if (member is null)
        {
            signature = null!;
            return false;
        }

        var parameters = member.Parameters
            .Select(parameter => new VbaCallableParameter(
                parameter.Name,
                IsOptional: parameter.IsOptional,
                IsParamArray: parameter.IsParamArray))
            .ToArray();
        signature = new VbaCallableSignature(
            $"{member.MemberName}({string.Join(", ", parameters.Select(parameter => parameter.Name))})",
            parameters,
            CallableKind: member.Kind == VbaStandardLibraryPotentialReceiverMemberKind.Function
                ? VbaCallableKind.Function
                : VbaCallableKind.Property,
            SupportsNamedArguments: member.Kind ==
                VbaStandardLibraryPotentialReceiverMemberKind.Function);
        return true;
    }

    private static VbaCallArgumentAvailability AnalyzeResolvedArguments(
        VbaSourceDefinition? definition,
        VbaCallableSignature signature,
        VbaCallSiteSyntax callSite,
        VbaCallContextCompatibility contextCompatibility =
            VbaCallContextCompatibility.Indeterminate)
    {
        var mapping = VbaCallArgumentMapper.MapInProgress(
            signature,
            callSite,
            allowNamedArguments: definition?.Kind != VbaSourceDefinitionKind.Event
                && signature.CallableKind != VbaCallableKind.Event,
            contextCompatibility);
        return new VbaCallArgumentAvailability(
            definition,
            signature,
            mapping.AllowsPositionalExpression,
            mapping.RemainingNamedParameters,
            ContextCompatibility: mapping.ContextCompatibility);
    }

    private static IEnumerable<VbaSourceDefinition> GetUseSiteDefinitions(
        VbaSourceDocument currentDocument,
        VbaResolvedNameTarget target)
        => target.PhysicalDefinitions.Where(definition =>
            VbaProjectIdentityModel.SameDocument(
                definition.Uri,
                currentDocument.Uri)
            || definition.Visibility.IsProjectVisible());

    private static IEnumerable<VbaSourceDefinition> GetCallableUseSiteDefinitions(
        VbaSourceDocument currentDocument,
        VbaResolvedNameTarget target)
        => (target is VbaPropertyNameTarget propertyTarget
                ? propertyTarget.Property.PropertyDefinitions
                : target.PhysicalDefinitions)
            .Where(definition =>
                VbaProjectIdentityModel.SameDocument(
                    definition.Uri,
                    currentDocument.Uri)
                || definition.Visibility.IsProjectVisible());

    private static IReadOnlyList<VbaCallArgumentSyntax> GetPriorArguments(
        VbaCallSiteSyntax callSite)
    {
        var count = Math.Clamp(callSite.ActiveArgumentIndex, 0, callSite.Arguments.Count);
        return callSite.Arguments.Take(count).ToArray();
    }

}
