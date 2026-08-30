using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

internal enum VbaInterfaceAccessorContractKind
{
    Sub,
    Function,
    Get,
    Let,
    Set
}

internal sealed record VbaInterfaceContractParameter(
    string Name,
    VbaCallableContractType? Type,
    bool IsArray,
    bool? IsByRef,
    VbaCallableContractParameterRole Role,
    VbaCallableContractDefault Default)
{
    public VbaCallableContractParameter ToCallableContractParameter()
        => new(
            Type,
            IsArray,
            IsByRef,
            Role,
            Default);
}

internal sealed record VbaSourceImplementsRelationship(
    VbaSourceDocument ImplementingDocument,
    VbaTypeReference InterfaceType,
    VbaRange InterfaceTypeRange,
    VbaConditionalCompilationBranchPath? ConditionalCompilationPath,
    VbaResolvedNameTarget InterfaceTarget);

internal sealed record VbaInterfaceVariableAccessorContract(
    VbaSourceImplementsRelationship Relationship,
    VbaSourceDefinition OwningVariable,
    string ImplementedName,
    VbaInterfaceAccessorContractKind Kind,
    string EffectiveTypeName,
    object? EffectiveTypeIdentity,
    string? EffectiveTypeReferenceQualifiedName,
    bool IsConditional)
{
    public string Signature => Kind switch
    {
        VbaInterfaceAccessorContractKind.Get =>
            $"Property Get {ImplementedName}() As {EffectiveTypeName}",
        VbaInterfaceAccessorContractKind.Let =>
            $"Property Let {ImplementedName}(ByVal AssignedValue As {EffectiveTypeName})",
        VbaInterfaceAccessorContractKind.Set =>
            $"Property Set {ImplementedName}(ByVal AssignedValue As {EffectiveTypeName})",
        _ => throw new InvalidOperationException("Unknown interface accessor kind.")
    };

    public string RequiredKind => Kind switch
    {
        VbaInterfaceAccessorContractKind.Get => "Property Get",
        VbaInterfaceAccessorContractKind.Let => "Property Let",
        VbaInterfaceAccessorContractKind.Set => "Property Set",
        _ => throw new InvalidOperationException("Unknown interface accessor kind.")
    };

    public VbaInterfaceContractVariant ToContractVariant()
    {
        var type = new VbaCallableContractType(
            EffectiveTypeName,
            EffectiveTypeIdentity,
            EffectiveTypeReferenceQualifiedName);
        var valueParameter = Kind == VbaInterfaceAccessorContractKind.Get
            ? null
            : new VbaInterfaceContractParameter(
                "AssignedValue",
                type,
                IsArray: false,
                IsByRef: false,
                VbaCallableContractParameterRole.Required,
                VbaCallableContractDefault.Absent);
        var result = Kind == VbaInterfaceAccessorContractKind.Get
            ? new VbaCallableContractResult(type, IsArray: false)
            : null;
        return new VbaInterfaceContractVariant(
            Relationship,
            OwningVariable,
            ImplementedName,
            Kind,
            [],
            valueParameter,
            result,
            Signature,
            IsConditional,
            IsDerivedVariableAccessor: true);
    }
}

internal sealed record VbaInterfaceVariableAccessorContractSet(
    VbaSourceImplementsRelationship Relationship,
    string ImplementedName,
    VbaInterfaceAccessorContractKind Kind,
    IReadOnlyList<VbaInterfaceVariableAccessorContract> Variants);

internal sealed record VbaInterfaceContractVariant(
    VbaSourceImplementsRelationship Relationship,
    VbaSourceDefinition OriginDefinition,
    string ImplementedName,
    VbaInterfaceAccessorContractKind Kind,
    IReadOnlyList<VbaInterfaceContractParameter> Parameters,
    VbaInterfaceContractParameter? PropertyValueParameter,
    VbaCallableContractResult? Result,
    string Signature,
    bool IsConditional,
    bool IsDerivedVariableAccessor,
    bool IsSignatureComplete = true)
{
    public string RequiredKind => Kind switch
    {
        VbaInterfaceAccessorContractKind.Sub => "Sub",
        VbaInterfaceAccessorContractKind.Function => "Function",
        VbaInterfaceAccessorContractKind.Get => "Property Get",
        VbaInterfaceAccessorContractKind.Let => "Property Let",
        VbaInterfaceAccessorContractKind.Set => "Property Set",
        _ => throw new InvalidOperationException("Unknown interface contract kind.")
    };
}

internal sealed record VbaInterfaceContractSet(
    VbaSourceImplementsRelationship Relationship,
    string ImplementedName,
    VbaInterfaceAccessorContractKind Kind,
    IReadOnlyList<VbaInterfaceContractVariant> Variants);

internal sealed record VbaInterfaceImplementationAssociation(
    VbaSourceImplementsRelationship Relationship,
    VbaInterfaceContractVariant Contract,
    VbaSourceDefinition Implementation,
    VbaResolvedNameTarget MemberTarget,
    VbaResolvedNameTarget ImplementationTarget,
    VbaCallableContractComparisonState CompatibilityState,
    VbaRange InterfacePrefixRange,
    VbaRange SeparatorRange,
    VbaRange MemberSuffixRange,
    string InterfacePrefix,
    string MemberSuffix);

internal sealed record VbaInterfaceImplementationAssociationAnalysis(
    IReadOnlyList<VbaInterfaceImplementationAssociation> Associations,
    IReadOnlyList<VbaResolvedNameTarget> IncompleteUpstreamTargets,
    IReadOnlyList<VbaResolvedNameTarget> IncompleteDependentTargets);

internal sealed record VbaDependentRenameTarget(
    VbaResolvedNameTarget Target,
    IReadOnlyList<VbaInterfaceImplementationAssociation> Associations);

/// <summary>
/// Projects source Implements relationships and source-interface variable accessor contracts
/// without adding synthetic Property definitions to ordinary name or call resolution.
/// </summary>
internal sealed class VbaInterfaceSemanticModel
{
    private readonly VbaNameResolutionService nameResolution;
    private readonly object sourceImplementationAssociationCacheGate = new();
    private readonly Dictionary<
        string,
        VbaInterfaceImplementationAssociationAnalysis>
        sourceImplementationAssociationCache =
            new(StringComparer.OrdinalIgnoreCase);

    public VbaInterfaceSemanticModel(VbaNameResolutionService nameResolution)
    {
        this.nameResolution = nameResolution;
    }

    internal IReadOnlyList<VbaContractPrefixCompletionOrigin>
        GetDeclarationNameCompletionOrigins(
            VbaSourceDocument implementingDocument,
            VbaCallableDeclarationNameKind declarationKind)
    {
        if (!TryMapDeclarationKind(declarationKind, out var contractKind))
        {
            return [];
        }

        var origins = new List<VbaContractPrefixCompletionOrigin>();
        foreach (var contractSet in GetContractSets(implementingDocument).Where(set =>
                     set.Kind == contractKind
                     && !HasIndeterminateConditionalCompilationOwnership(
                         set.Relationship)))
        {
            foreach (var variant in contractSet.Variants.Where(variant =>
                         !HasIndeterminateConditionalCompilationOwnership(variant)))
            {
                var memberName = variant.OriginDefinition.Name;
                if (!variant.ImplementedName.EndsWith(
                        memberName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var prefixLength = variant.ImplementedName.Length
                    - memberName.Length;
                if (prefixLength == 0
                    || variant.ImplementedName[prefixLength - 1] != '_')
                {
                    continue;
                }

                var documentation =
                    variant.OriginDefinition.Signature?.Documentation
                    ?? variant.OriginDefinition.Documentation;
                origins.Add(new VbaContractPrefixCompletionOrigin(
                    variant.ImplementedName[..prefixLength],
                    VbaContractCompletionDomain.Interface,
                    contractSet.Relationship.ConditionalCompilationPath
                        is { IsEmpty: false },
                    [
                        new VbaContractMemberCompletionOrigin(
                            memberName,
                            VbaContractCompletionDomain.Interface,
                            variant.IsConditional,
                            CreateCompletionContractSignature(
                                variant,
                                documentation),
                            documentation,
                            variant)
                    ]));
            }
        }

        return origins;
    }

    private static VbaCallableSignature? CreateCompletionContractSignature(
        VbaInterfaceContractVariant contract,
        string? documentation)
    {
        if (!contract.IsSignatureComplete)
        {
            return null;
        }

        var contractParameters = contract.PropertyValueParameter is null
            ? contract.Parameters
            : [.. contract.Parameters, contract.PropertyValueParameter];
        var sourceParameters = contract.OriginDefinition.Signature?.Parameters
            ?? [];
        var parameters = contractParameters
            .Select((parameter, index) => new VbaCallableParameter(
                parameter.Name,
                Documentation: index < sourceParameters.Count
                    ? sourceParameters[index].Documentation
                    : null,
                IsOptional: parameter.Role
                    == VbaCallableContractParameterRole.Optional,
                DisplayLabel: contract.IsDerivedVariableAccessor
                        && ReferenceEquals(
                            parameter,
                            contract.PropertyValueParameter)
                    ? $"ByVal {parameter.Name} As {parameter.Type!.Name}"
                    : CreateParameterLabel(parameter),
                TypeReference: parameter.Type is null
                    ? null
                    : new VbaTypeReference(parameter.Type.Name),
                IsByRef: parameter.IsByRef,
                IsParamArray: parameter.Role
                    == VbaCallableContractParameterRole.ParamArray,
                IsArray: parameter.IsArray))
            .ToArray();
        var callableKind = contract.Kind switch
        {
            VbaInterfaceAccessorContractKind.Sub => VbaCallableKind.Sub,
            VbaInterfaceAccessorContractKind.Function => VbaCallableKind.Function,
            VbaInterfaceAccessorContractKind.Get
                or VbaInterfaceAccessorContractKind.Let
                or VbaInterfaceAccessorContractKind.Set => VbaCallableKind.Property,
            _ => throw new InvalidOperationException(
                "Unknown interface contract kind.")
        };
        return new VbaCallableSignature(
            contract.Signature,
            parameters,
            documentation,
            callableKind,
            SupportsNamedArguments: true);
    }

    private static bool TryMapDeclarationKind(
        VbaCallableDeclarationNameKind declarationKind,
        out VbaInterfaceAccessorContractKind contractKind)
    {
        contractKind = declarationKind switch
        {
            VbaCallableDeclarationNameKind.Sub =>
                VbaInterfaceAccessorContractKind.Sub,
            VbaCallableDeclarationNameKind.Function =>
                VbaInterfaceAccessorContractKind.Function,
            VbaCallableDeclarationNameKind.PropertyGet =>
                VbaInterfaceAccessorContractKind.Get,
            VbaCallableDeclarationNameKind.PropertyLet =>
                VbaInterfaceAccessorContractKind.Let,
            VbaCallableDeclarationNameKind.PropertySet =>
                VbaInterfaceAccessorContractKind.Set,
            _ => default
        };
        return declarationKind is VbaCallableDeclarationNameKind.Sub
            or VbaCallableDeclarationNameKind.Function
            or VbaCallableDeclarationNameKind.PropertyGet
            or VbaCallableDeclarationNameKind.PropertyLet
            or VbaCallableDeclarationNameKind.PropertySet;
    }

    public IReadOnlyList<VbaProjectValidationDiagnostic> GetDiagnostics(
        VbaSourceDocument implementingDocument)
    {
        var diagnostics = new List<VbaProjectValidationDiagnostic>();
        var contractSets = GetContractSets(implementingDocument)
            .Where(contractSet =>
                !HasIndeterminateConditionalCompilationOwnership(
                    contractSet.Relationship))
            .ToArray();
        var suppressedContractSets = new HashSet<VbaInterfaceContractSet>();
        var syntaxTree = implementingDocument.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(
                implementingDocument.Uri,
                implementingDocument.Text);
        foreach (var implementedMember in contractSets.GroupBy(contractSet => new
                 {
                     contractSet.Relationship,
                     ImplementedNameKey =
                         contractSet.ImplementedName.ToUpperInvariant()
                 }))
        {
            var conclusiveContractSets = implementedMember
                .Where(contractSet => contractSet.Variants.Any(contract =>
                    !HasIndeterminateConditionalCompilationOwnership(contract)))
                .ToArray();
            if (conclusiveContractSets.Length == 0)
            {
                foreach (var contractSet in implementedMember)
                {
                    suppressedContractSets.Add(contractSet);
                }

                continue;
            }

            var implementedName = implementedMember
                .SelectMany(contractSet => contractSet.Variants)
                .OrderBy(
                    contract => contract.OriginDefinition.Uri,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(
                    contract => contract.OriginDefinition.Uri,
                    StringComparer.Ordinal)
                .ThenBy(contract => contract.OriginDefinition.Range.Start.Line)
                .ThenBy(contract => contract.OriginDefinition.Range.Start.Character)
                .ThenBy(contract => contract.Kind)
                .First()
                .ImplementedName;
            var requiredKinds = conclusiveContractSets
                .Select(contractSet => contractSet.Kind)
                .Distinct()
                .OrderBy(kind => kind)
                .ToArray();
            var allowedKinds = implementedMember
                .Select(contractSet => contractSet.Kind)
                .Distinct()
                .ToArray();
            var allPhysicalMembers = implementingDocument.Definitions
                .Where(definition => definition.Kind is
                        VbaSourceDefinitionKind.Procedure
                            or VbaSourceDefinitionKind.Property
                    && definition.Name.Equals(
                        implementedName,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var physicalMembers = allPhysicalMembers;
            var allowedMembers = physicalMembers.Where(definition =>
                    allowedKinds.Any(kind => HasSameKindImplementation(
                        definition,
                        implementedName,
                        kind)))
                .ToArray();
            var wrongKindMembers = physicalMembers
                .Where(definition => !allowedMembers.Contains(definition)
                    && !nameResolution
                        .HasIndeterminateConditionalCompilationOwnership(definition))
                .ToArray();
            foreach (var wrongKindMember in wrongKindMembers)
            {
                var callable = FindCallable(syntaxTree, wrongKindMember);
                if (callable is null)
                {
                    continue;
                }

                var actualKind = GetPhysicalMemberKind(wrongKindMember);
                diagnostics.Add(new VbaProjectValidationDiagnostic(
                    "validation.interfaceMemberKindMismatch",
                    $"Interface member '{implementedName}' "
                        + $"requires {FormatRequiredKinds(requiredKinds)}, "
                        + $"not {actualKind}.",
                    callable.DeclarationKeywordRange is { } keywordRange
                        ? ToRange(keywordRange)
                        : wrongKindMember.Range,
                    Details: conclusiveContractSets
                        .OrderBy(contractSet => contractSet.Kind)
                        .SelectMany(contractSet => contractSet.Variants)
                        .Where(contract =>
                            !HasIndeterminateConditionalCompilationOwnership(contract))
                        .OrderBy(contract => contract.Kind)
                        .ThenBy(
                            contract => contract.OriginDefinition.Uri,
                            StringComparer.OrdinalIgnoreCase)
                        .ThenBy(
                            contract => contract.OriginDefinition.Uri,
                            StringComparer.Ordinal)
                        .ThenBy(contract => contract.OriginDefinition.Range.Start.Line)
                        .ThenBy(contract => contract.OriginDefinition.Range.Start.Character)
                        .Select(contract =>
                        {
                            var conditionalMarker = contract.IsConditional
                                ? " [#If]"
                                : "";
                            var message =
                                $"Required contract: {contract.Signature}"
                                + $"{conditionalMarker}.";
                            return new VbaDiagnosticDetail(
                                CreateContractDiagnosticLocation(contract),
                                message,
                                message);
                        })
                        .ToArray()));
            }

            if (wrongKindMembers.Length > 0 && allowedMembers.Length == 0)
            {
                foreach (var contractSet in implementedMember)
                {
                    suppressedContractSets.Add(contractSet);
                }
            }
        }

        foreach (var contractSet in contractSets.Where(contractSet =>
                     !suppressedContractSets.Contains(contractSet)))
        {
            var implementations = GetSameKindImplementations(
                implementingDocument,
                contractSet.ImplementedName,
                contractSet.Kind);
            if (implementations.Count > 0)
            {
                var comparisonMatrix = contractSet.Variants
                    .SelectMany(contract => implementations.Select(implementation => new
                    {
                        Contract = contract,
                        Implementation = implementation,
                        Compatibility = CompareContract(
                            implementingDocument,
                            contract,
                            implementation)
                    }))
                    .ToArray();
                foreach (var implementation in implementations)
                {
                    var comparisons = comparisonMatrix
                        .Where(comparison => comparison.Implementation
                            == implementation)
                        .ToArray();
                    if (comparisons.Any(comparison => comparison.Compatibility.State
                            == VbaCallableContractComparisonState.Compatible)
                        || comparisons.Any(comparison => comparison.Compatibility.State
                            == VbaCallableContractComparisonState.Indeterminate))
                    {
                        continue;
                    }

                    var callable = FindCallable(syntaxTree, implementation);
                    if (callable is null)
                    {
                        continue;
                    }

                    diagnostics.Add(new VbaProjectValidationDiagnostic(
                        "validation.incompatibleInterfaceMemberSignature",
                        $"Interface member '{contractSet.ImplementedName}' signature "
                            + $"does not match any required "
                            + $"{contractSet.Variants[0].RequiredKind} contract.",
                        ToRange(callable.SignatureRange ?? callable.Range),
                        Details: comparisons.Select(comparison =>
                        {
                            var conditionalMarker = comparison.Contract.IsConditional
                                ? " [#If]"
                                : "";
                            var mismatchText = string.Join(
                                "; ",
                                VbaCallableContractComparisonFormatter
                                    .FormatMismatchReasons(
                                        comparison.Compatibility));
                            var message =
                                $"Required contract: {comparison.Contract.Signature}"
                                + $"{conditionalMarker}. Mismatches: {mismatchText}.";
                            var fallbackMessage =
                                $"Expected signature: {comparison.Contract.Signature}"
                                + $"{conditionalMarker}.\n"
                                + $"Mismatches: {mismatchText}.";
                            return new VbaDiagnosticDetail(
                                CreateContractDiagnosticLocation(
                                    comparison.Contract),
                                message,
                                fallbackMessage);
                        }).ToArray()));
                }

                var coveredContracts = contractSet.Variants.Where(contract =>
                        comparisonMatrix.Any(comparison =>
                            comparison.Contract == contract
                            && comparison.Compatibility.State
                                == VbaCallableContractComparisonState.Compatible))
                    .ToArray();
                var conclusivelyUncoveredContracts = contractSet.Variants
                    .Where(contract => !comparisonMatrix.Any(comparison =>
                            comparison.Contract == contract
                            && comparison.Compatibility.State is
                                VbaCallableContractComparisonState.Compatible
                                    or VbaCallableContractComparisonState.Indeterminate))
                    .ToArray();
                if (coveredContracts.Length > 0
                    && conclusivelyUncoveredContracts.Length > 0)
                {
                    var partialPresentation = contractSet.Variants[0];
                    diagnostics.Add(new VbaProjectValidationDiagnostic(
                        "validation.interfaceMemberContractNotFullyImplemented",
                        $"Interface member '{contractSet.ImplementedName}' does not "
                            + $"implement every required {partialPresentation.RequiredKind} "
                            + "contract.",
                        contractSet.Relationship.InterfaceTypeRange,
                        Details: conclusivelyUncoveredContracts
                            .Select(contract =>
                            {
                                var conditionalMarker = contract.IsConditional
                                    ? " [#If]"
                                    : "";
                                var message =
                                    $"Required contract: {contract.Signature}"
                                    + $"{conditionalMarker}.";
                                return new VbaDiagnosticDetail(
                                    CreateContractDiagnosticLocation(contract),
                                    message,
                                    message);
                            })
                            .ToArray()));
                }

                continue;
            }

            var variants = contractSet.Variants;
            var conclusiveVariants = variants
                .Where(contract =>
                    !HasIndeterminateConditionalCompilationOwnership(contract))
                .ToArray();
            if (conclusiveVariants.Length == 0)
            {
                continue;
            }

            var presentation = conclusiveVariants[0];
            diagnostics.Add(new VbaProjectValidationDiagnostic(
                "validation.interfaceMemberNotImplemented",
                $"Interface member '{presentation.ImplementedName}' requires a "
                    + $"{presentation.RequiredKind} implementation.",
                contractSet.Relationship.InterfaceTypeRange,
                Details: conclusiveVariants
                    .Select(contract =>
                    {
                        var conditionalMarker = contract.IsConditional
                            ? " [#If]"
                            : "";
                        var message =
                            $"Required contract: {contract.Signature}{conditionalMarker}.";
                        return new VbaDiagnosticDetail(
                            CreateContractDiagnosticLocation(contract),
                            message,
                            message);
                    })
                    .ToArray()));
        }

        return diagnostics;
    }

    private bool HasIndeterminateConditionalCompilationOwnership(
        VbaInterfaceContractVariant contract)
        => nameResolution.HasIndeterminateConditionalCompilationOwnership(
            contract.OriginDefinition);

    private static bool HasIndeterminateConditionalCompilationOwnership(
        VbaSourceImplementsRelationship relationship)
    {
        if (relationship.ConditionalCompilationPath is not null)
        {
            return false;
        }

        var syntaxTree = relationship.ImplementingDocument.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(
                relationship.ImplementingDocument.Uri,
                relationship.ImplementingDocument.Text);
        return syntaxTree.Diagnostics.Any(diagnostic => diagnostic.Code.StartsWith(
            "syntax.malformedPreprocessor",
            StringComparison.Ordinal));
    }

    private static string GetPhysicalMemberKind(VbaSourceDefinition definition)
        => definition.PropertyAccessorKind switch
        {
            VbaPropertyAccessorKind.Get => "Property Get",
            VbaPropertyAccessorKind.Let => "Property Let",
            VbaPropertyAccessorKind.Set => "Property Set",
            _ => definition.CallableKind switch
            {
                VbaCallableKind.Sub => "Sub",
                VbaCallableKind.Function => "Function",
                _ => "Property"
            }
        };

    private static string FormatRequiredKinds(
        IReadOnlyList<VbaInterfaceAccessorContractKind> kinds)
    {
        var presentations = kinds.Select(kind => kind switch
            {
                VbaInterfaceAccessorContractKind.Sub => "Sub",
                VbaInterfaceAccessorContractKind.Function => "Function",
                VbaInterfaceAccessorContractKind.Get => "Property Get",
                VbaInterfaceAccessorContractKind.Let => "Property Let",
                VbaInterfaceAccessorContractKind.Set => "Property Set",
                _ => throw new InvalidOperationException(
                    "Unknown interface accessor kind.")
            })
            .ToArray();
        return presentations.Length switch
        {
            0 => "an accessor",
            1 => presentations[0],
            2 => $"{presentations[0]} or {presentations[1]}",
            _ => $"{string.Join(", ", presentations[..^1])}, or {presentations[^1]}"
        };
    }

    public IReadOnlyList<VbaSourceDefinition> ResolveAccessorContractDefinitions(
        VbaSourceDocument implementingDocument,
        int line,
        int character)
    {
        var syntaxTree = implementingDocument.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(
                implementingDocument.Uri,
                implementingDocument.Text);
        var identifier = syntaxTree.GetPositionSyntax(line, character).Identifier;
        if (identifier is null)
        {
            return [];
        }

        var implementation = implementingDocument.Definitions.SingleOrDefault(
            definition => definition.Kind == VbaSourceDefinitionKind.Property
                && definition.Range.Start.Line == identifier.Range.Start.Line
                && definition.Range.Start.Character == identifier.Range.Start.Character
                && definition.Range.End.Line == identifier.Range.End.Line
                && definition.Range.End.Character == identifier.Range.End.Character);
        if (implementation?.PropertyAccessorKind is not { } accessorKind)
        {
            return [];
        }

        var contractKind = accessorKind switch
        {
            VbaPropertyAccessorKind.Get => VbaInterfaceAccessorContractKind.Get,
            VbaPropertyAccessorKind.Let => VbaInterfaceAccessorContractKind.Let,
            VbaPropertyAccessorKind.Set => VbaInterfaceAccessorContractKind.Set,
            _ => throw new InvalidOperationException("Unknown Property accessor kind.")
        };
        var contractSets = GetContractSets(implementingDocument);
        foreach (var contractSet in contractSets.Where(set =>
                     set.Kind == contractKind
                     && set.Variants.Any(contract =>
                         contract.IsDerivedVariableAccessor)
                     && set.ImplementedName.Equals(
                         implementation.Name,
                         StringComparison.OrdinalIgnoreCase)))
        {
            var variableContracts = contractSet.Variants
                .Where(contract => contract.IsDerivedVariableAccessor)
                .ToArray();
            var suffixLength = variableContracts[0].OriginDefinition.Name.Length;
            var suffixStart = implementation.Range.End.Character - suffixLength;
            if (line != implementation.Range.Start.Line
                || character < suffixStart
                || implementation.Range.End.Character < character)
            {
                continue;
            }

            var owningVariable = variableContracts[0].OriginDefinition;
            return nameResolution.GetLogicalDefinitions(owningVariable)
                .Where(definition =>
                    definition.Kind == VbaSourceDefinitionKind.Variable
                    && definition.ParentProcedureName is null
                    && definition.Name.Equals(
                        owningVariable.Name,
                        StringComparison.OrdinalIgnoreCase))
                .DistinctBy(definition => definition.Identity)
                .OrderBy(definition => definition.Uri, StringComparer.OrdinalIgnoreCase)
                .ThenBy(definition => definition.Uri, StringComparer.Ordinal)
                .ThenBy(definition => definition.Range.Start.Line)
                .ThenBy(definition => definition.Range.Start.Character)
                .ToArray();
        }

        return [];
    }

    internal IReadOnlyList<VbaInterfaceImplementationAssociation>
        GetConclusiveSourceImplementationAssociations(
            VbaSourceDocument implementingDocument)
        => AnalyzeSourceImplementationAssociations(implementingDocument)
            .Associations;

    internal VbaInterfaceImplementationAssociationAnalysis
        AnalyzeSourceImplementationAssociations(
            VbaSourceDocument implementingDocument)
    {
        lock (sourceImplementationAssociationCacheGate)
        {
            if (sourceImplementationAssociationCache.TryGetValue(
                implementingDocument.Uri,
                out var cached))
            {
                return cached;
            }

            var analysis = AnalyzeSourceImplementationAssociationsCore(
                implementingDocument);
            sourceImplementationAssociationCache[implementingDocument.Uri] =
                analysis;
            return analysis;
        }
    }

    private VbaInterfaceImplementationAssociationAnalysis
        AnalyzeSourceImplementationAssociationsCore(
            VbaSourceDocument implementingDocument)
    {
        var associations = new List<VbaInterfaceImplementationAssociation>();
        var incompleteUpstreamTargets = new List<VbaResolvedNameTarget>();
        var incompleteDependentTargets = new List<VbaResolvedNameTarget>();
        CollectIncompleteSourceContractCandidates(
            implementingDocument,
            incompleteUpstreamTargets,
            incompleteDependentTargets);
        foreach (var contractSet in GetContractSets(implementingDocument).Where(set =>
                     set.Relationship.InterfaceTarget.PhysicalDefinitions.All(
                         definition => definition.Identity.Origin
                                 == VbaDefinitionOrigin.Source
                             && definition.Kind == VbaSourceDefinitionKind.Class)))
        {
            var implementations = GetSameKindImplementations(
                    implementingDocument,
                    contractSet.ImplementedName,
                    contractSet.Kind)
                .ToArray();
            var comparisons = implementations
                .SelectMany(implementation => contractSet.Variants.Select(
                    contract => new
                    {
                        Contract = contract,
                        Implementation = implementation,
                        Compatibility = CompareContract(
                            implementingDocument,
                            contract,
                            implementation)
                    }))
                .ToArray();
            var hasIncompleteCoverage =
                HasIndeterminateConditionalCompilationOwnership(
                    contractSet.Relationship)
                || contractSet.Variants.Any(
                    HasIndeterminateConditionalCompilationOwnership)
                || implementations.Any(implementation =>
                    nameResolution
                        .HasIndeterminateConditionalCompilationOwnership(
                            implementation))
                || comparisons.Any(comparison => comparison.Compatibility.State
                    == VbaCallableContractComparisonState.Indeterminate)
                || comparisons.Any(comparison =>
                    comparison.Compatibility.HasIndeterminateEvidence);
            if (hasIncompleteCoverage)
            {
                incompleteUpstreamTargets.Add(
                    contractSet.Relationship.InterfaceTarget);
                if (implementations.Length > 0)
                {
                    incompleteUpstreamTargets.AddRange(
                        contractSet.Variants.Select(variant =>
                            nameResolution.CreateNameTarget(
                                variant.OriginDefinition)));
                    incompleteDependentTargets.AddRange(
                        implementations.Select(
                            nameResolution.CreateNameTarget));
                }

                continue;
            }

            foreach (var comparison in comparisons)
            {
                var implementation = comparison.Implementation;
                var contract = comparison.Contract;
                var memberName = contract.OriginDefinition.Name;
                var separatorOffset = contract.ImplementedName.Length
                    - memberName.Length
                    - 1;
                if (separatorOffset <= 0
                    || separatorOffset >= implementation.Name.Length - 1
                    || implementation.Name[separatorOffset] != '_'
                    || !implementation.Name[(separatorOffset + 1)..].Equals(
                        memberName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var line = implementation.Range.Start.Line;
                var startCharacter = implementation.Range.Start.Character;
                var separatorCharacter = startCharacter + separatorOffset;
                associations.Add(new VbaInterfaceImplementationAssociation(
                    contractSet.Relationship,
                    contract,
                    implementation,
                    nameResolution.CreateNameTarget(
                        contract.OriginDefinition),
                    nameResolution.CreateNameTarget(implementation),
                    comparison.Compatibility.State,
                    new VbaRange(
                        new VbaPosition(line, startCharacter),
                        new VbaPosition(line, separatorCharacter)),
                    new VbaRange(
                        new VbaPosition(line, separatorCharacter),
                        new VbaPosition(line, separatorCharacter + 1)),
                    new VbaRange(
                        new VbaPosition(line, separatorCharacter + 1),
                        implementation.Range.End),
                    implementation.Name[..separatorOffset],
                    implementation.Name[(separatorOffset + 1)..]));
            }
        }

        return new VbaInterfaceImplementationAssociationAnalysis(
            associations,
            incompleteUpstreamTargets
                .DistinctBy(target => target.Identity)
                .ToArray(),
            incompleteDependentTargets
                .DistinctBy(target => target.Identity)
                .ToArray());
    }

    private void CollectIncompleteSourceContractCandidates(
        VbaSourceDocument implementingDocument,
        ICollection<VbaResolvedNameTarget> incompleteUpstreamTargets,
        ICollection<VbaResolvedNameTarget> incompleteDependentTargets)
    {
        void Record(
            VbaSourceImplementsRelationship relationship,
            VbaSourceDefinition origin,
            IEnumerable<VbaSourceDefinition> implementations)
        {
            var candidates = implementations.ToArray();
            if (candidates.Length == 0)
            {
                return;
            }

            incompleteUpstreamTargets.Add(relationship.InterfaceTarget);
            incompleteUpstreamTargets.Add(
                nameResolution.CreateNameTarget(origin));
            foreach (var implementation in candidates)
            {
                incompleteDependentTargets.Add(
                    nameResolution.CreateNameTarget(implementation));
            }
        }

        foreach (var relationship in GetRelationships(implementingDocument)
                     .Where(candidate => candidate.InterfaceTarget
                         .PhysicalDefinitions.All(definition =>
                             definition.Identity.Origin
                                 == VbaDefinitionOrigin.Source
                             && definition.Kind
                                 == VbaSourceDefinitionKind.Class)))
        {
            foreach (var interfaceDefinition in relationship.InterfaceTarget
                         .PhysicalDefinitions)
            {
                var interfaceDocument = nameResolution.FindDocument(
                    interfaceDefinition.Uri);
                if (interfaceDocument is null)
                {
                    continue;
                }

                var syntaxTree = interfaceDocument.SyntaxTree
                    ?? VbaSyntaxTree.ParseModule(
                        interfaceDocument.Uri,
                        interfaceDocument.Text);
                foreach (var variable in interfaceDocument.Definitions.Where(
                             definition => definition.Kind
                                     == VbaSourceDefinitionKind.Variable
                                 && definition.Visibility
                                     == VbaSourceDefinitionVisibility.Public
                                 && definition.ParentProcedureName is null
                                 && !definition.IsArray
                                 && !definition.IsFixedLengthString))
                {
                    var effectiveType = GetEffectiveType(
                        interfaceDocument,
                        variable);
                    if (effectiveType is { Identity: not null })
                    {
                        continue;
                    }

                    var implementedName =
                        $"{interfaceDefinition.Name}_{variable.Name}";
                    Record(
                        relationship,
                        variable,
                        implementingDocument.Definitions.Where(definition =>
                            definition.Name.Equals(
                                implementedName,
                                StringComparison.OrdinalIgnoreCase)
                            && definition.Kind
                                == VbaSourceDefinitionKind.Property));
                }

                foreach (var definition in interfaceDocument.Definitions.Where(
                             candidate => candidate.ParentProcedureName is null
                                 && candidate.Visibility
                                     == VbaSourceDefinitionVisibility.Public
                                 && candidate.Kind is
                                     VbaSourceDefinitionKind.Procedure
                                         or VbaSourceDefinitionKind.Property))
                {
                    if (!TryGetContractKind(definition, out var kind))
                    {
                        continue;
                    }

                    var callable = FindCallable(syntaxTree, definition);
                    var hasIncompleteEvidence = definition.Signature is null
                        || callable is null;
                    if (!hasIncompleteEvidence && callable is not null)
                    {
                        hasIncompleteEvidence = callable.Parameters.Any(
                            parameter => GetEffectiveType(
                                interfaceDocument,
                                parameter.Name,
                                parameter.TypeReference is null
                                    ? null
                                    : new VbaTypeReference(
                                        parameter.TypeReference.Name,
                                        parameter.TypeReference.Qualifier),
                                definition.ConditionalCompilationPath)
                                is not { Identity: not null });
                    }

                    if (!hasIncompleteEvidence
                        && kind is VbaInterfaceAccessorContractKind.Function
                            or VbaInterfaceAccessorContractKind.Get)
                    {
                        hasIncompleteEvidence = GetEffectiveType(
                            interfaceDocument,
                            definition.Name,
                            definition.TypeReference,
                            definition.ConditionalCompilationPath)
                            is not { Identity: not null };
                    }

                    if (!hasIncompleteEvidence)
                    {
                        continue;
                    }

                    var implementedName =
                        $"{interfaceDefinition.Name}_{definition.Name}";
                    Record(
                        relationship,
                        definition,
                        GetSameKindImplementations(
                            implementingDocument,
                            implementedName,
                            kind));
                }
            }
        }
    }

    internal bool IsPotentialInterfaceImplementationDeclaration(
        VbaSourceDocument implementingDocument,
        VbaSourceDefinition declaration)
    {
        if (declaration.Kind is not VbaSourceDefinitionKind.Procedure
            and not VbaSourceDefinitionKind.Property)
        {
            return false;
        }

        var syntaxTree = implementingDocument.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(
                implementingDocument.Uri,
                implementingDocument.Text);
        return syntaxTree.Module.ImplementsRelationships
            .Where(relationship =>
        {
            var interfaceName = relationship.InterfaceType.Name;
            return declaration.Name.Length > interfaceName.Length + 1
                && declaration.Name.StartsWith(
                    interfaceName,
                    StringComparison.OrdinalIgnoreCase)
                && declaration.Name[interfaceName.Length] == '_';
        })
            .Any(relationship =>
            {
                var outcome = nameResolution.ResolveTypeDefinitionOutcome(
                    implementingDocument,
                    new VbaTypeReference(
                        relationship.InterfaceType.Name,
                        relationship.InterfaceType.Qualifier));
                return outcome.Kind != VbaNameResolutionKind.Resolved
                    || outcome.Target is null
                    || !outcome.Target.PhysicalDefinitions.All(definition =>
                        definition.Identity.Origin
                            == VbaDefinitionOrigin.Source
                        && definition.Kind == VbaSourceDefinitionKind.Class);
            });
    }

    internal VbaSourceDefinition ProjectSourceInterfaceDocumentation(
        VbaSourceDefinition definition)
    {
        if (definition.Documentation is not null
            || definition.Identity.Origin != VbaDefinitionOrigin.Source
            || nameResolution.FindDocument(definition.Uri) is not { }
                implementingDocument)
        {
            return definition;
        }

        var associations = GetConclusiveSourceImplementationAssociations(
                implementingDocument)
            .Where(association => association.Implementation.Identity
                == definition.Identity)
            .ToArray();
        if (associations.Length == 0)
        {
            return definition;
        }

        var firstContract = associations[0].Contract.OriginDefinition;
        if (associations.Any(association => !string.Equals(
                association.Contract.OriginDefinition.Documentation,
                firstContract.Documentation,
                StringComparison.Ordinal)))
        {
            return definition;
        }

        var projectedSignature = definition.Signature;
        if (definition.Signature is { } implementationSignature)
        {
            var contractSignatures = associations
                .Select(association =>
                    association.Contract.OriginDefinition.Signature)
                .ToArray();
            if (contractSignatures.Any(signature => signature is null
                    || signature.Parameters.Count
                        != implementationSignature.Parameters.Count))
            {
                return definition;
            }

            var firstSignature = contractSignatures[0]!;
            if (contractSignatures.Skip(1).Any(signature =>
                    !string.Equals(
                        signature!.Documentation,
                        firstSignature.Documentation,
                        StringComparison.Ordinal)
                    || !signature.Parameters
                        .Select(parameter => parameter.Documentation)
                        .SequenceEqual(
                            firstSignature.Parameters.Select(parameter =>
                                parameter.Documentation),
                            StringComparer.Ordinal)))
            {
                return definition;
            }

            projectedSignature = implementationSignature with
            {
                Documentation = firstSignature.Documentation,
                Parameters = implementationSignature.Parameters
                    .Select((parameter, index) => parameter with
                    {
                        Documentation = firstSignature.Parameters[index]
                            .Documentation
                    })
                    .ToArray()
            };
        }

        return definition with
        {
            Documentation = firstContract.Documentation,
            Signature = projectedSignature
        };
    }

    internal bool TryResolveSourceInterfaceDeclarationPrefix(
        VbaSourceDocument implementingDocument,
        VbaSourceDefinition declaration,
        out VbaResolvedNameTarget interfaceTarget,
        out int prefixLength)
    {
        var candidates = GetContractSets(implementingDocument)
            .Where(contract =>
                !HasIndeterminateConditionalCompilationOwnership(
                    contract.Relationship)
                && contract.Variants.All(variant =>
                    !HasIndeterminateConditionalCompilationOwnership(
                        variant)))
            .Where(contract => contract.ImplementedName.Equals(
                declaration.Name,
                StringComparison.OrdinalIgnoreCase))
            .Select(contract => contract.Relationship)
            .Where(relationship => relationship.InterfaceTarget
                .PhysicalDefinitions.All(definition =>
                    definition.Identity.Origin == VbaDefinitionOrigin.Source
                    && definition.Kind == VbaSourceDefinitionKind.Class))
            .Select(relationship => new
            {
                relationship.InterfaceTarget,
                Prefix = relationship.InterfaceTarget.SelectedDefinition.Name
            })
            .Where(candidate => declaration.Name.StartsWith(
                    candidate.Prefix,
                    StringComparison.OrdinalIgnoreCase)
                && declaration.Name.Length > candidate.Prefix.Length
                && declaration.Name[candidate.Prefix.Length] == '_')
            .DistinctBy(candidate => candidate.InterfaceTarget.Identity)
            .ToArray();
        if (candidates.Length != 1)
        {
            interfaceTarget = null!;
            prefixLength = 0;
            return false;
        }

        interfaceTarget = candidates[0].InterfaceTarget;
        prefixLength = candidates[0].Prefix.Length;
        return true;
    }

    public VbaSignatureHelp? GetAccessorSignatureHelp(
        VbaSourceDocument implementingDocument,
        int line,
        int character,
        VbaSignaturePresentationIdentity? retriggerIdentity)
    {
        var syntaxTree = implementingDocument.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(
                implementingDocument.Uri,
                implementingDocument.Text);
        var position = new VbaSyntaxPosition(line, character, 0);
        var callable = syntaxTree.Module.CallableDeclarations.FirstOrDefault(
            candidate => candidate.ParameterListRange is { } parameterListRange
                && Contains(parameterListRange, position));
        if (callable?.PropertyAccessorKind is not { } accessorKind)
        {
            return null;
        }

        var implementation = implementingDocument.Definitions.FirstOrDefault(
            definition => definition.Kind == VbaSourceDefinitionKind.Property
                && definition.Range.Start.Line == callable.Range.Start.Line
                && definition.Name.Equals(
                    callable.Name,
                    StringComparison.OrdinalIgnoreCase)
                && definition.PropertyAccessorKind == accessorKind);
        if (implementation is null)
        {
            return null;
        }

        var contractKind = accessorKind switch
        {
            VbaPropertyAccessorKind.Get => VbaInterfaceAccessorContractKind.Get,
            VbaPropertyAccessorKind.Let => VbaInterfaceAccessorContractKind.Let,
            VbaPropertyAccessorKind.Set => VbaInterfaceAccessorContractKind.Set,
            _ => throw new InvalidOperationException("Unknown Property accessor kind.")
        };
        var contractSets = GetContractSets(implementingDocument)
            .Where(set => set.Kind == contractKind
                && set.ImplementedName.Equals(
                    implementation.Name,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (contractSets.Length == 0)
        {
            return null;
        }

        var physicalParameterIndex = GetPhysicalParameterIndex(
            syntaxTree,
            callable,
            position);
        var variants = contractSets
            .SelectMany(set => set.Variants)
            .Where(contract => contract.IsSignatureComplete)
            .Select(contract =>
            {
                var contractParameters = contract.PropertyValueParameter is null
                    ? contract.Parameters
                    : [.. contract.Parameters, contract.PropertyValueParameter];
                var parameters = contractParameters
                    .Select(parameter => new VbaCallableParameter(
                        parameter.Name,
                        IsOptional: parameter.Role
                            == VbaCallableContractParameterRole.Optional,
                        DisplayLabel: contract.IsDerivedVariableAccessor
                                && ReferenceEquals(
                                    parameter,
                                    contract.PropertyValueParameter)
                            ? $"ByVal {parameter.Name} As {parameter.Type!.Name}"
                            : CreateParameterLabel(parameter),
                        TypeReference: parameter.Type is null
                            ? null
                            : new VbaTypeReference(parameter.Type.Name),
                        IsByRef: parameter.IsByRef,
                        IsParamArray: parameter.Role
                            == VbaCallableContractParameterRole.ParamArray,
                        IsArray: parameter.IsArray))
                    .ToArray();
                var signature = new VbaCallableSignature(
                    contract.Signature,
                    parameters,
                    CallableKind: VbaCallableKind.Property,
                    SupportsNamedArguments: true);
                return new VbaSignatureHelpVariant(
                    signature,
                    physicalParameterIndex is int parameterIndex
                            && parameterIndex < parameters.Length
                        ? parameterIndex
                        : null,
                    contract.IsConditional);
            })
            .ToArray();
        if (variants.Length == 0)
        {
            return null;
        }

        var activeSignature = 0;
        if (retriggerIdentity is not null)
        {
            var retainedIndex = Array.FindIndex(
                variants,
                variant => variant.PresentationIdentity.Matches(retriggerIdentity));
            if (retainedIndex >= 0)
            {
                activeSignature = retainedIndex;
            }
        }

        var active = variants[activeSignature];
        return new VbaSignatureHelp(
            active.Signature,
            active.ActiveParameter,
            variants,
            activeSignature);
    }

    internal static int? GetPhysicalParameterIndex(
        VbaSyntaxTree syntaxTree,
        VbaCallableDeclarationSyntax callable,
        VbaSyntaxPosition position)
    {
        if (callable.Parameters.Count == 0
            || callable.ParameterListRange is not { } parameterListRange)
        {
            return null;
        }

        var depth = 0;
        var parameterIndex = 0;
        foreach (var token in syntaxTree.TokenStream.Tokens)
        {
            if (!Contains(parameterListRange, token.Range.Start))
            {
                continue;
            }

            if (token.Text == "(")
            {
                depth++;
                continue;
            }

            if (token.Text == ")")
            {
                depth--;
                continue;
            }

            if (token.Text == ","
                && depth == 1
                && IsAtOrBefore(token.Range.End, position))
            {
                parameterIndex++;
            }
        }

        return Math.Min(parameterIndex, callable.Parameters.Count - 1);
    }

    private static bool IsAtOrBefore(
        VbaSyntaxPosition left,
        VbaSyntaxPosition right)
        => left.Line < right.Line
            || left.Line == right.Line
                && left.Character <= right.Character;

    private IReadOnlyList<VbaInterfaceContractSet> GetContractSets(
        VbaSourceDocument implementingDocument)
        => GetRelationships(implementingDocument)
            .SelectMany(relationship => GetVariableAccessorContractSets(relationship)
                .SelectMany(set => set.Variants)
                .Select(contract => contract.ToContractVariant())
                .Concat(GetSourceCallableContracts(relationship))
                .Concat(GetProjectReferenceCallableContracts(relationship))
                .GroupBy(
                    contract => contract.ImplementedName,
                    StringComparer.OrdinalIgnoreCase)
                .SelectMany(nameGroup => nameGroup
                    .GroupBy(contract => contract.Kind)
                    .Select(kindGroup =>
                    {
                        var variants = kindGroup
                            .OrderBy(
                                contract => contract.OriginDefinition.Uri,
                                StringComparer.OrdinalIgnoreCase)
                            .ThenBy(
                                contract => contract.OriginDefinition.Uri,
                                StringComparer.Ordinal)
                            .ThenBy(contract =>
                                contract.OriginDefinition.Range.Start.Line)
                            .ThenBy(contract =>
                                contract.OriginDefinition.Range.Start.Character)
                            .ToArray();
                        return new VbaInterfaceContractSet(
                            relationship,
                            variants[0].ImplementedName,
                            kindGroup.Key,
                            variants);
                    })))
            .ToArray();

    private IReadOnlyList<VbaInterfaceVariableAccessorContractSet>
        GetVariableAccessorContractSets(
            VbaSourceImplementsRelationship relationship)
        => GetVariableAccessorContracts(relationship)
            .GroupBy(
                contract => contract.ImplementedName,
                StringComparer.OrdinalIgnoreCase)
            .SelectMany(nameGroup => nameGroup
                .GroupBy(contract => contract.Kind)
                .Select(kindGroup =>
                {
                    var variants = kindGroup
                        .OrderBy(
                            contract => contract.OwningVariable.Uri,
                            StringComparer.OrdinalIgnoreCase)
                        .ThenBy(
                            contract => contract.OwningVariable.Uri,
                            StringComparer.Ordinal)
                        .ThenBy(contract =>
                            contract.OwningVariable.Range.Start.Line)
                        .ThenBy(contract =>
                            contract.OwningVariable.Range.Start.Character)
                        .ToArray();
                    return new VbaInterfaceVariableAccessorContractSet(
                        relationship,
                        variants[0].ImplementedName,
                        kindGroup.Key,
                        variants);
                }))
            .ToArray();

    private IReadOnlyList<VbaSourceImplementsRelationship> GetRelationships(
        VbaSourceDocument implementingDocument)
    {
        var syntaxTree = implementingDocument.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(
                implementingDocument.Uri,
                implementingDocument.Text);
        if (syntaxTree.Module.Kind != VbaModuleKind.ClassModule)
        {
            return [];
        }

        var relationships = new List<VbaSourceImplementsRelationship>();
        foreach (var relationshipSyntax in
                 syntaxTree.Module.ImplementsRelationships)
        {
            var typeReference = new VbaTypeReference(
                relationshipSyntax.InterfaceType.Name,
                relationshipSyntax.InterfaceType.Qualifier);
            var outcome = nameResolution.ResolveTypeDefinitionOutcome(
                implementingDocument,
                typeReference);
            if (outcome.Kind != VbaNameResolutionKind.Resolved
                || outcome.Target is null)
            {
                continue;
            }


            var interfaceDefinitions = outcome.Target.PhysicalDefinitions;
            var isSourceInterface = interfaceDefinitions.All(definition =>
                definition.Identity.Origin == VbaDefinitionOrigin.Source
                && definition.Kind == VbaSourceDefinitionKind.Class);
            var isProjectReferenceInterface = interfaceDefinitions.All(definition =>
                definition.Identity.Origin
                    == VbaDefinitionOrigin.ProjectReference
                && definition.Kind == VbaSourceDefinitionKind.Class
                && definition.IsAuthoringAvailable
                && nameResolution.GetTypeLibEventSurface(
                        definition.ModuleName,
                        definition.Name).RawTypeKind is
                    TypeLibCatalogRawTypeKind.Interface
                        or TypeLibCatalogRawTypeKind.Dispatch);
            if (!isSourceInterface && !isProjectReferenceInterface)
            {
                continue;
            }

            var conditionalPath =
                VbaConditionalCompilationBranchFacts.TryGetPath(
                    syntaxTree,
                    relationshipSyntax.InterfaceTypeRange,
                    requireCompleteStructure: true,
                    out var path)
                    ? path
                    : null;
            relationships.Add(new VbaSourceImplementsRelationship(
                implementingDocument,
                typeReference,
                ToRange(relationshipSyntax.InterfaceTypeRange),
                conditionalPath,
                outcome.Target));
        }

        return relationships;
    }

    private IReadOnlyList<VbaInterfaceVariableAccessorContract>
        GetVariableAccessorContracts(VbaSourceImplementsRelationship relationship)
    {
        var contracts = new List<VbaInterfaceVariableAccessorContract>();
        foreach (var interfaceDefinition in relationship.InterfaceTarget.PhysicalDefinitions)
        {
            var interfaceDocument = nameResolution.FindDocument(interfaceDefinition.Uri);
            if (interfaceDocument is null)
            {
                continue;
            }

            foreach (var variable in interfaceDocument.Definitions.Where(definition =>
                         definition.Kind == VbaSourceDefinitionKind.Variable
                         && definition.Visibility == VbaSourceDefinitionVisibility.Public
                         && definition.ParentProcedureName is null
                         && !definition.IsArray
                         && !definition.IsFixedLengthString))
            {
                var effectiveType = GetEffectiveType(interfaceDocument, variable);
                if (effectiveType is null)
                {
                    continue;
                }

                var implementedName = $"{interfaceDefinition.Name}_{variable.Name}";
                var isConditional =
                    relationship.ConditionalCompilationPath is { IsEmpty: false }
                    || variable.ConditionalCompilationPath is { IsEmpty: false };
                foreach (var kind in GetRequiredAccessorKinds(effectiveType.Value.Category))
                {
                    contracts.Add(new VbaInterfaceVariableAccessorContract(
                        relationship,
                        variable,
                        implementedName,
                        kind,
                        effectiveType.Value.Name,
                        effectiveType.Value.Identity,
                        effectiveType.Value.ReferenceQualifiedName,
                        isConditional));
                }
            }
        }

        return contracts;
    }

    private IReadOnlyList<VbaInterfaceContractVariant> GetSourceCallableContracts(
        VbaSourceImplementsRelationship relationship)
    {
        var contracts = new List<VbaInterfaceContractVariant>();
        foreach (var interfaceDefinition in relationship.InterfaceTarget.PhysicalDefinitions)
        {
            var interfaceDocument = nameResolution.FindDocument(interfaceDefinition.Uri);
            if (interfaceDocument is null)
            {
                continue;
            }

            var syntaxTree = interfaceDocument.SyntaxTree
                ?? VbaSyntaxTree.ParseModule(
                    interfaceDocument.Uri,
                    interfaceDocument.Text);

            foreach (var definition in interfaceDocument.Definitions.Where(definition =>
                         definition.ParentProcedureName is null
                         && definition.Visibility == VbaSourceDefinitionVisibility.Public
                         && definition.Signature is not null
                         && definition.Kind is VbaSourceDefinitionKind.Procedure
                             or VbaSourceDefinitionKind.Property))
            {
                if (!TryGetContractKind(definition, out var kind))
                {
                    continue;
                }

                var callable = FindCallable(syntaxTree, definition);
                if (callable is null)
                {
                    continue;
                }

                var parameters = callable.Parameters
                    .Select(parameter => CreateParameterContract(
                        interfaceDocument,
                        parameter,
                        definition.ConditionalCompilationPath))
                    .ToArray();
                VbaInterfaceContractParameter? valueParameter = null;
                IReadOnlyList<VbaInterfaceContractParameter> ordinaryParameters =
                    parameters;
                if (kind is VbaInterfaceAccessorContractKind.Let
                        or VbaInterfaceAccessorContractKind.Set
                    && parameters.Length > 0)
                {
                    ordinaryParameters = parameters[..^1];
                    valueParameter = parameters[^1] with { IsByRef = false };
                }

                VbaCallableContractResult? result = null;
                if (kind is VbaInterfaceAccessorContractKind.Function
                    or VbaInterfaceAccessorContractKind.Get)
                {
                    var effectiveType = GetEffectiveType(
                        interfaceDocument,
                        definition.Name,
                        definition.TypeReference,
                        definition.ConditionalCompilationPath);
                    if (effectiveType is null)
                    {
                        continue;
                    }

                    result = new VbaCallableContractResult(
                        new VbaCallableContractType(
                            effectiveType.Value.Name,
                            effectiveType.Value.Identity,
                            effectiveType.Value.ReferenceQualifiedName),
                        callable.IsReturnArray);
                }

                var implementedName = $"{interfaceDefinition.Name}_{definition.Name}";
                var isConditional =
                    relationship.ConditionalCompilationPath is { IsEmpty: false }
                    || definition.ConditionalCompilationPath is { IsEmpty: false };
                contracts.Add(new VbaInterfaceContractVariant(
                    relationship,
                    definition,
                    implementedName,
                    kind,
                    ordinaryParameters,
                    valueParameter,
                    result,
                    CreateContractSignature(
                        kind,
                        implementedName,
                        ordinaryParameters,
                        valueParameter,
                        result),
                    isConditional,
                    IsDerivedVariableAccessor: false));
            }
        }

        return contracts;
    }

    private IReadOnlyList<VbaInterfaceContractVariant>
        GetProjectReferenceCallableContracts(
            VbaSourceImplementsRelationship relationship)
    {
        var contracts = new List<VbaInterfaceContractVariant>();
        foreach (var interfaceDefinition in relationship.InterfaceTarget
                     .PhysicalDefinitions
                     .Where(definition => definition.Identity.Origin
                             == VbaDefinitionOrigin.ProjectReference
                         && definition.Kind == VbaSourceDefinitionKind.Class
                         && definition.IsAuthoringAvailable)
                     .DistinctBy(definition => definition.Identity))
        {
            foreach (var member in nameResolution
                         .GetProjectReferencePhysicalMembers(
                             interfaceDefinition.ModuleName,
                             interfaceDefinition.Name))
            {
                if (!TryGetContractKind(member, out var kind))
                {
                    continue;
                }

                var parameters = member.Signature?.Parameters
                    .Select(parameter => CreateProjectReferenceParameterContract(
                        member,
                        parameter))
                    .ToArray()
                    ?? [];
                VbaInterfaceContractParameter? valueParameter = null;
                IReadOnlyList<VbaInterfaceContractParameter> ordinaryParameters =
                    parameters;
                if (kind is VbaInterfaceAccessorContractKind.Let
                        or VbaInterfaceAccessorContractKind.Set
                    && parameters.Length > 0)
                {
                    ordinaryParameters = parameters[..^1];
                    valueParameter = parameters[^1] with { IsByRef = false };
                }

                VbaCallableContractResult? result = null;
                if (kind is VbaInterfaceAccessorContractKind.Function
                    or VbaInterfaceAccessorContractKind.Get)
                {
                    var effectiveType = GetProjectReferenceEffectiveType(
                        member,
                        member.TypeReference);
                    result = new VbaCallableContractResult(
                        effectiveType is null
                            ? null
                            : new VbaCallableContractType(
                                effectiveType.Value.Name,
                                effectiveType.Value.Identity,
                                effectiveType.Value.ReferenceQualifiedName),
                        member.IsReturnArray);
                }

                var implementedName =
                    $"{interfaceDefinition.Name}_{member.Name}";
                var isConditional = relationship.ConditionalCompilationPath
                    is { IsEmpty: false };
                var isSignatureComplete = member.IsCallableMetadataComplete
                    && (kind is not (VbaInterfaceAccessorContractKind.Function
                            or VbaInterfaceAccessorContractKind.Get)
                        || member.IsReturnArray is not null);
                var contractSignature = isSignatureComplete
                    ? CreateContractSignature(
                        kind,
                        implementedName,
                        ordinaryParameters,
                        valueParameter,
                        result)
                    : CreateContractNamePresentation(kind, implementedName);
                contracts.Add(new VbaInterfaceContractVariant(
                    relationship,
                    member,
                    implementedName,
                    kind,
                    ordinaryParameters,
                    valueParameter,
                    result,
                    contractSignature,
                    isConditional,
                    IsDerivedVariableAccessor: false,
                    IsSignatureComplete: isSignatureComplete));
            }
        }

        return contracts;
    }

    private VbaInterfaceContractParameter
        CreateProjectReferenceParameterContract(
            VbaSourceDefinition owner,
            VbaCallableParameter parameter)
    {
        var effectiveType = GetProjectReferenceEffectiveType(
            owner,
            parameter.TypeReference);
        var defaultEvidence = parameter.DefaultExpression is null
            ? parameter.IsOptional
                ? VbaCallableContractDefault.Indeterminate
                : VbaCallableContractDefault.Absent
            : VbaCallableContractDefault.FromExpression(
                parameter.DefaultExpression);
        return new VbaInterfaceContractParameter(
            parameter.Name,
            effectiveType is null
                ? null
                : new VbaCallableContractType(
                    effectiveType.Value.Name,
                    effectiveType.Value.Identity,
                    effectiveType.Value.ReferenceQualifiedName),
            parameter.IsArray,
            parameter.IsByRef,
            parameter.IsParamArray
                ? VbaCallableContractParameterRole.ParamArray
                : parameter.IsOptional
                    ? VbaCallableContractParameterRole.Optional
                    : VbaCallableContractParameterRole.Required,
            defaultEvidence);
    }

    private VbaInterfaceContractParameter CreateParameterContract(
        VbaSourceDocument declarationDocument,
        VbaCallableParameterSyntax parameter,
        VbaConditionalCompilationBranchPath? declarationPath)
        => CreateParameterContract(
            declarationDocument,
            new VbaCallableParameter(
                parameter.Name,
                parameter.Documentation,
                parameter.IsOptional,
                TypeReference: parameter.TypeReference is null
                    ? null
                    : new VbaTypeReference(
                        parameter.TypeReference.Name,
                        parameter.TypeReference.Qualifier),
                IsByRef: parameter.IsByRef,
                IsParamArray: parameter.IsParamArray,
                IsArray: parameter.IsArray)
            {
                DefaultExpression = parameter.DefaultExpression
            },
            declarationPath);

    private VbaInterfaceContractParameter CreateParameterContract(
        VbaSourceDocument declarationDocument,
        VbaCallableParameter parameter,
        VbaConditionalCompilationBranchPath? declarationPath)
    {
        var effectiveType = GetEffectiveType(
            declarationDocument,
            parameter.Name,
            parameter.TypeReference,
            declarationPath)
            ?? (
                "Variant",
                EffectiveTypeCategory.Variant,
                (object?)"Variant",
                (string?)null);
        var defaultEvidence = parameter.DefaultExpression is null
            ? VbaCallableContractDefault.Absent
            : VbaCallableContractDefault.FromExpression(
                parameter.DefaultExpression);
        return new VbaInterfaceContractParameter(
            parameter.Name,
            new VbaCallableContractType(
                effectiveType.Name,
                effectiveType.Identity,
                effectiveType.ReferenceQualifiedName),
            parameter.IsArray,
            parameter.IsByRef ?? true,
            parameter.IsParamArray
                ? VbaCallableContractParameterRole.ParamArray
                : parameter.IsOptional
                    ? VbaCallableContractParameterRole.Optional
                    : VbaCallableContractParameterRole.Required,
            defaultEvidence);
    }

    private static bool TryGetContractKind(
        VbaSourceDefinition definition,
        out VbaInterfaceAccessorContractKind kind)
    {
        if (definition.Kind == VbaSourceDefinitionKind.Property)
        {
            kind = definition.PropertyAccessorKind switch
            {
                VbaPropertyAccessorKind.Get => VbaInterfaceAccessorContractKind.Get,
                VbaPropertyAccessorKind.Let => VbaInterfaceAccessorContractKind.Let,
                VbaPropertyAccessorKind.Set => VbaInterfaceAccessorContractKind.Set,
                _ => default
            };
            return definition.PropertyAccessorKind is not null;
        }

        kind = definition.CallableKind switch
        {
            VbaCallableKind.Sub => VbaInterfaceAccessorContractKind.Sub,
            VbaCallableKind.Function => VbaInterfaceAccessorContractKind.Function,
            _ => default
        };
        return definition.CallableKind is VbaCallableKind.Sub
            or VbaCallableKind.Function;
    }

    private static string CreateContractSignature(
        VbaInterfaceAccessorContractKind kind,
        string implementedName,
        IReadOnlyList<VbaInterfaceContractParameter> parameters,
        VbaInterfaceContractParameter? valueParameter,
        VbaCallableContractResult? result)
    {
        var allParameters = valueParameter is null
            ? parameters
            : [.. parameters, valueParameter];
        var kindPresentation = GetContractKindPresentation(kind);
        var signature = $"{kindPresentation} {implementedName}("
            + string.Join(", ", allParameters.Select(CreateParameterLabel))
            + ")";
        return result is null
            ? signature
            : result.Type is null
                ? signature
                : $"{signature} As {result.Type.Name}{(result.IsArray == true ? "()" : "")}";
    }

    private static string CreateContractNamePresentation(
        VbaInterfaceAccessorContractKind kind,
        string implementedName)
        => $"{GetContractKindPresentation(kind)} {implementedName}";

    private static string GetContractKindPresentation(
        VbaInterfaceAccessorContractKind kind)
        => kind switch
        {
            VbaInterfaceAccessorContractKind.Sub => "Sub",
            VbaInterfaceAccessorContractKind.Function => "Function",
            VbaInterfaceAccessorContractKind.Get => "Property Get",
            VbaInterfaceAccessorContractKind.Let => "Property Let",
            VbaInterfaceAccessorContractKind.Set => "Property Set",
            _ => throw new InvalidOperationException("Unknown interface contract kind.")
        };

    private static string CreateParameterLabel(
        VbaInterfaceContractParameter parameter)
    {
        var parts = new List<string>();
        if (parameter.Role == VbaCallableContractParameterRole.ParamArray)
        {
            parts.Add("ParamArray");
        }
        else if (parameter.IsByRef == true)
        {
            parts.Add("ByRef");
        }

        parts.Add(parameter.IsArray
            ? $"{parameter.Name}()"
            : parameter.Name);
        if (parameter.Type is not null)
        {
            parts.Add($"As {parameter.Type.Name}");
        }
        var label = string.Join(" ", parts);
        return parameter.Role == VbaCallableContractParameterRole.Optional
            ? $"[{label}]"
            : label;
    }

    private (
        string Name,
        EffectiveTypeCategory Category,
        object? Identity,
        string? ReferenceQualifiedName)?
        GetEffectiveType(
        VbaSourceDocument interfaceDocument,
        VbaSourceDefinition variable)
        => GetEffectiveType(
            interfaceDocument,
            variable.Name,
            variable.TypeReference,
            variable.ConditionalCompilationPath);

    private (
        string Name,
        EffectiveTypeCategory Category,
        object? Identity,
        string? ReferenceQualifiedName)?
        GetEffectiveType(
        VbaSourceDocument interfaceDocument,
        string declaredName,
        VbaTypeReference? typeReference,
        VbaConditionalCompilationBranchPath? declarationPath = null)
    {
        if (typeReference is null)
        {
            var syntaxTree = interfaceDocument.SyntaxTree
                ?? VbaSyntaxTree.ParseModule(
                    interfaceDocument.Uri,
                    interfaceDocument.Text);
            var initial = char.ToUpperInvariant(declaredName[0]);
            var effectiveDeclarationPath = declarationPath
                ?? VbaConditionalCompilationBranchPath.Root;
            var defType = syntaxTree.Module.DefTypeDirectives
                .Where(directive => VbaConditionalCompilationBranchFacts
                    .TryGetPath(
                        syntaxTree,
                        directive.Range,
                        requireCompleteStructure: true,
                        out var directivePath)
                    && directivePath.IsPrefixOf(effectiveDeclarationPath))
                .LastOrDefault(directive => directive.LetterRanges.Any(range =>
                    range.Start <= initial && initial <= range.End));
            if (defType is null)
            {
                return (
                    "Variant",
                    EffectiveTypeCategory.Variant,
                    "Variant",
                    null);
            }

            return defType.TypeName switch
            {
                "Variant" => (
                    defType.TypeName,
                    EffectiveTypeCategory.Variant,
                    defType.TypeName,
                    null),
                "Object" => (
                    defType.TypeName,
                    EffectiveTypeCategory.Object,
                    defType.TypeName,
                    null),
                _ => (
                    defType.TypeName,
                    EffectiveTypeCategory.Value,
                    defType.TypeName,
                    null)
            };
        }

        if (typeReference.Qualifier is null
            && VbaLanguageVocabulary.TryGetCanonicalTypeName(
                typeReference.Name,
                out var canonicalName))
        {
            return canonicalName switch
            {
                "Variant" => (
                    canonicalName,
                    EffectiveTypeCategory.Variant,
                    canonicalName,
                    null),
                "Object" => (
                    canonicalName,
                    EffectiveTypeCategory.Object,
                    canonicalName,
                    null),
                _ => (
                    canonicalName,
                    EffectiveTypeCategory.Value,
                    canonicalName,
                    null)
            };
        }

        var outcome = nameResolution.ResolveTypeDefinitionOutcome(
            interfaceDocument,
            typeReference);
        if (outcome.Kind != VbaNameResolutionKind.Resolved
            || outcome.Target is null)
        {
            return (
                GetTypePresentation(typeReference),
                EffectiveTypeCategory.UnresolvedNamed,
                null,
                null);
        }

        var definitions = outcome.Target.PhysicalDefinitions;
        var selectedDefinition = outcome.Target.SelectedDefinition;
        var canonicalQualifier = typeReference.Qualifier is null
            ? null
            : nameResolution.GetCanonicalQualifierName(
                selectedDefinition,
                typeReference.Qualifier) ?? typeReference.Qualifier;
        var effectiveTypeName = canonicalQualifier is null
            ? outcome.Target.CanonicalName
            : $"{canonicalQualifier}.{outcome.Target.CanonicalName}";
        var preferredReferenceQualifier =
            nameResolution.GetPreferredReferenceQualifierName(selectedDefinition);
        var referenceQualifiedName = string.IsNullOrEmpty(
                preferredReferenceQualifier)
            ? null
            : $"{preferredReferenceQualifier}.{outcome.Target.CanonicalName}";
        if (definitions.All(definition => definition.Kind is
                VbaSourceDefinitionKind.Class or VbaSourceDefinitionKind.Form))
        {
            return (
                effectiveTypeName,
                EffectiveTypeCategory.Object,
                outcome.Target.Identity,
                referenceQualifiedName);
        }

        if (definitions.All(definition => definition.Kind is
                VbaSourceDefinitionKind.Enum or VbaSourceDefinitionKind.Type))
        {
            return (
                effectiveTypeName,
                EffectiveTypeCategory.Value,
                outcome.Target.Identity,
                referenceQualifiedName);
        }

        return null;
    }

    private (
        string Name,
        EffectiveTypeCategory Category,
        object? Identity,
        string? ReferenceQualifiedName)?
        GetProjectReferenceEffectiveType(
            VbaSourceDefinition owner,
            VbaTypeReference? typeReference)
    {
        if (typeReference is null)
        {
            return null;
        }

        if (typeReference.Qualifier is null
            && VbaLanguageVocabulary.TryGetCanonicalTypeName(
                typeReference.Name,
                out var canonicalName))
        {
            return canonicalName switch
            {
                "Variant" => (
                    canonicalName,
                    EffectiveTypeCategory.Variant,
                    canonicalName,
                    null),
                "Object" => (
                    canonicalName,
                    EffectiveTypeCategory.Object,
                    canonicalName,
                    null),
                _ => (
                    canonicalName,
                    EffectiveTypeCategory.Value,
                    canonicalName,
                    null)
            };
        }

        var definition = nameResolution.ResolveProjectReferenceTypeDefinition(
            owner.Identity.ReferenceName ?? owner.ModuleName,
            typeReference);
        if (definition is null)
        {
            return (
                GetTypePresentation(typeReference),
                EffectiveTypeCategory.UnresolvedNamed,
                null,
                null);
        }

        var canonicalQualifier = typeReference.Qualifier is null
            ? null
            : nameResolution.GetCanonicalQualifierName(
                definition,
                typeReference.Qualifier) ?? typeReference.Qualifier;
        var effectiveTypeName = canonicalQualifier is null
            ? definition.Name
            : $"{canonicalQualifier}.{definition.Name}";
        var preferredReferenceQualifier =
            nameResolution.GetPreferredReferenceQualifierName(definition);
        var referenceQualifiedName = string.IsNullOrEmpty(
                preferredReferenceQualifier)
            ? null
            : $"{preferredReferenceQualifier}.{definition.Name}";
        var identity = new VbaDefinitionNameTargetIdentity(definition.Identity);
        return definition.Kind switch
        {
            VbaSourceDefinitionKind.Class or VbaSourceDefinitionKind.Form => (
                effectiveTypeName,
                EffectiveTypeCategory.Object,
                identity,
                referenceQualifiedName),
            VbaSourceDefinitionKind.Enum or VbaSourceDefinitionKind.Type => (
                effectiveTypeName,
                EffectiveTypeCategory.Value,
                identity,
                referenceQualifiedName),
            _ => null
        };
    }

    private static IReadOnlyList<VbaInterfaceAccessorContractKind>
        GetRequiredAccessorKinds(EffectiveTypeCategory category)
        => category switch
        {
            EffectiveTypeCategory.Variant =>
                [
                    VbaInterfaceAccessorContractKind.Get,
                    VbaInterfaceAccessorContractKind.Let,
                    VbaInterfaceAccessorContractKind.Set
                ],
            EffectiveTypeCategory.Object =>
                [
                    VbaInterfaceAccessorContractKind.Get,
                    VbaInterfaceAccessorContractKind.Set
                ],
            EffectiveTypeCategory.Value =>
                [
                    VbaInterfaceAccessorContractKind.Get,
                    VbaInterfaceAccessorContractKind.Let
                ],
            _ => [VbaInterfaceAccessorContractKind.Get]
        };

    private static bool HasSameKindImplementation(
        VbaSourceDefinition definition,
        string implementedName,
        VbaInterfaceAccessorContractKind kind)
        => definition.Name.Equals(
                implementedName,
                StringComparison.OrdinalIgnoreCase)
            && kind switch
            {
                VbaInterfaceAccessorContractKind.Sub =>
                    definition.Kind == VbaSourceDefinitionKind.Procedure
                    && definition.CallableKind == VbaCallableKind.Sub,
                VbaInterfaceAccessorContractKind.Function =>
                    definition.Kind == VbaSourceDefinitionKind.Procedure
                    && definition.CallableKind == VbaCallableKind.Function,
                VbaInterfaceAccessorContractKind.Get =>
                    definition.Kind == VbaSourceDefinitionKind.Property
                    && definition.PropertyAccessorKind == VbaPropertyAccessorKind.Get,
                VbaInterfaceAccessorContractKind.Let =>
                    definition.Kind == VbaSourceDefinitionKind.Property
                    && definition.PropertyAccessorKind == VbaPropertyAccessorKind.Let,
                VbaInterfaceAccessorContractKind.Set =>
                    definition.Kind == VbaSourceDefinitionKind.Property
                    && definition.PropertyAccessorKind == VbaPropertyAccessorKind.Set,
                _ => false
            };

    private static IReadOnlyList<VbaSourceDefinition> GetSameKindImplementations(
        VbaSourceDocument implementingDocument,
        string implementedName,
        VbaInterfaceAccessorContractKind kind)
        => implementingDocument.Definitions.Where(definition =>
            HasSameKindImplementation(definition, implementedName, kind))
            .ToArray();

    private VbaCallableContractComparisonResult CompareContract(
        VbaSourceDocument implementingDocument,
        VbaInterfaceContractVariant contract,
        VbaSourceDefinition implementation)
    {
        if (!contract.IsSignatureComplete
            || HasIndeterminateConditionalCompilationOwnership(contract)
            || nameResolution.HasIndeterminateConditionalCompilationOwnership(
                implementation))
        {
            return VbaCallableContractComparisonResult
                .UnavailableContractEvidence();
        }

        var implementationSignature = GetFulfillmentSignature(
            implementingDocument,
            implementation);
        if (implementationSignature is null)
        {
            return VbaCallableContractComparisonResult
                .UnavailableContractEvidence();
        }

        var syntaxTree = implementingDocument.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(
                implementingDocument.Uri,
                implementingDocument.Text);
        var callable = FindCallable(syntaxTree, implementation);
        var foundParameters = callable?.ParameterListRange is not null
            ? callable.Parameters
                .Select(parameter => CreateParameterContract(
                    implementingDocument,
                    parameter,
                    implementation.ConditionalCompilationPath))
                .ToArray()
            : implementationSignature.Parameters
                .Select(parameter => CreateParameterContract(
                    implementingDocument,
                    parameter,
                    implementation.ConditionalCompilationPath))
                .ToArray();
        VbaInterfaceContractParameter? foundValueParameter = null;
        IReadOnlyList<VbaInterfaceContractParameter> foundOrdinaryParameters =
            foundParameters;
        if (contract.Kind is VbaInterfaceAccessorContractKind.Let
                or VbaInterfaceAccessorContractKind.Set
            && foundParameters.Length > 0)
        {
            foundOrdinaryParameters = foundParameters[..^1];
            foundValueParameter = foundParameters[^1] with { IsByRef = false };
        }

        VbaCallableContractResult? foundResult = null;
        if (contract.Result is not null)
        {
            var foundEffectiveType = GetEffectiveType(
                implementingDocument,
                implementation.Name,
                implementation.TypeReference,
                implementation.ConditionalCompilationPath);
            foundResult = new VbaCallableContractResult(
                foundEffectiveType is null
                    ? null
                    : new VbaCallableContractType(
                        foundEffectiveType.Value.Name,
                        foundEffectiveType.Value.Identity,
                        foundEffectiveType.Value.ReferenceQualifiedName),
                callable?.IsReturnArray ?? implementation.IsArray);
        }

        var expectedContract = new VbaCallableContract(
            contract.Parameters
                .Select(parameter => parameter.ToCallableContractParameter())
                .ToArray(),
            contract.PropertyValueParameter?.ToCallableContractParameter(),
            contract.Result);
        var foundContract = new VbaCallableContract(
            foundOrdinaryParameters
                .Select(parameter => parameter.ToCallableContractParameter())
                .ToArray(),
            foundValueParameter?.ToCallableContractParameter(),
            foundResult);
        return VbaCallableContractComparison.Compare(
            expectedContract,
            foundContract,
            VbaCallableContractComparisonPolicy.InterfaceFulfillment);
    }

    private static VbaCallableSignature? GetFulfillmentSignature(
        VbaSourceDocument implementingDocument,
        VbaSourceDefinition implementation)
    {
        if (implementation.Signature is not null)
        {
            return implementation.Signature;
        }

        var syntaxTree = implementingDocument.SyntaxTree
            ?? VbaSyntaxTree.ParseModule(
                implementingDocument.Uri,
                implementingDocument.Text);
        var callable = FindCallable(syntaxTree, implementation);
        if (callable?.ParameterListRange is null)
        {
            return null;
        }

        return new VbaCallableSignature(
            callable.Signature.Label,
            callable.Parameters.Select(parameter => new VbaCallableParameter(
                    parameter.Name,
                    parameter.Documentation,
                    parameter.IsOptional,
                    TypeReference: parameter.TypeReference is null
                        ? null
                        : new VbaTypeReference(
                            parameter.TypeReference.Name,
                            parameter.TypeReference.Qualifier),
                    IsByRef: parameter.IsByRef,
                    IsParamArray: parameter.IsParamArray,
                    IsArray: parameter.IsArray)
                {
                    DefaultExpression = parameter.DefaultExpression
                })
                .ToArray(),
            CallableKind: VbaCallableKind.Property,
            SupportsNamedArguments: true);
    }

    private static string GetTypePresentation(VbaTypeReference? typeReference)
        => typeReference?.Qualifier is { Length: > 0 } qualifier
            ? $"{qualifier}.{typeReference.Name}"
            : typeReference?.Name ?? "Variant";

    private static VbaCallableDeclarationSyntax? FindCallable(
        VbaSyntaxTree syntaxTree,
        VbaSourceDefinition definition)
        => syntaxTree.Module.CallableDeclarations.FirstOrDefault(candidate =>
            candidate.Range.Start.Line == definition.Range.Start.Line
            && candidate.Name.Equals(
                definition.Name,
                StringComparison.OrdinalIgnoreCase)
            && candidate.PropertyAccessorKind == definition.PropertyAccessorKind);

    private static VbaRange ToRange(VbaSyntaxRange range)
        => new(
            new VbaPosition(range.Start.Line, range.Start.Character),
            new VbaPosition(range.End.Line, range.End.Character));

    private static VbaDiagnosticLocation? CreateContractDiagnosticLocation(
        VbaInterfaceContractVariant contract)
        => VbaProjectReferenceCatalogSet.IsExternalDefinition(
            contract.OriginDefinition)
                ? null
                : new VbaDiagnosticLocation(
                    contract.OriginDefinition.Uri,
                    contract.OriginDefinition.Range);

    private static bool Contains(
        VbaSyntaxRange range,
        VbaSyntaxPosition position)
        => range.Start.Line < position.Line
            || range.Start.Line == position.Line
                && range.Start.Character <= position.Character
            ? position.Line < range.End.Line
                || position.Line == range.End.Line
                    && position.Character <= range.End.Character
            : false;

    private enum EffectiveTypeCategory
    {
        Variant,
        Object,
        Value,
        UnresolvedNamed
    }
}
