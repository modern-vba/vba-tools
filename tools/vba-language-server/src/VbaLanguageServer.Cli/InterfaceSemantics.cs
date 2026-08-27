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

internal enum VbaInterfaceContractParameterRole
{
    Required,
    Optional,
    ParamArray
}

internal sealed record VbaInterfaceContractType(
    string Name,
    object? Identity);

internal enum VbaInterfaceContractDefaultState
{
    Absent,
    Evaluated,
    Indeterminate
}

internal sealed record VbaInterfaceContractDefault(
    VbaInterfaceContractDefaultState State,
    VbaConstantValue? Value = null)
{
    public static VbaInterfaceContractDefault Absent { get; } = new(
        VbaInterfaceContractDefaultState.Absent);

    public string Presentation => State == VbaInterfaceContractDefaultState.Absent
        ? "no default"
        : Value?.Presentation ?? "unknown default";
}

internal sealed record VbaInterfaceContractParameter(
    string Name,
    VbaInterfaceContractType? Type,
    bool IsArray,
    bool? IsByRef,
    VbaInterfaceContractParameterRole Role,
    VbaInterfaceContractDefault? Default = null)
{
    public VbaInterfaceContractDefault EffectiveDefault
        => Default ?? VbaInterfaceContractDefault.Absent;
}

internal sealed record VbaInterfaceContractResult(
    VbaInterfaceContractType? Type,
    bool? IsArray = false);

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
        var type = new VbaInterfaceContractType(
            EffectiveTypeName,
            EffectiveTypeIdentity);
        var valueParameter = Kind == VbaInterfaceAccessorContractKind.Get
            ? null
            : new VbaInterfaceContractParameter(
                "AssignedValue",
                type,
                IsArray: false,
                IsByRef: false,
                VbaInterfaceContractParameterRole.Required);
        var result = Kind == VbaInterfaceAccessorContractKind.Get
            ? new VbaInterfaceContractResult(type)
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
    VbaInterfaceContractResult? Result,
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

internal enum VbaInterfaceContractCompatibilityState
{
    Compatible,
    Incompatible,
    Indeterminate
}

internal sealed record VbaInterfaceContractCompatibility(
    VbaInterfaceContractCompatibilityState State,
    IReadOnlyList<string> Mismatches);

/// <summary>
/// Projects source Implements relationships and source-interface variable accessor contracts
/// without adding synthetic Property definitions to ordinary name or call resolution.
/// </summary>
internal sealed class VbaInterfaceSemanticModel
{
    private readonly VbaNameResolutionService nameResolution;

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
                    == VbaInterfaceContractParameterRole.Optional,
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
                    == VbaInterfaceContractParameterRole.ParamArray,
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
                            == VbaInterfaceContractCompatibilityState.Compatible)
                        || comparisons.Any(comparison => comparison.Compatibility.State
                            == VbaInterfaceContractCompatibilityState.Indeterminate))
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
                                comparison.Compatibility.Mismatches);
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
                                == VbaInterfaceContractCompatibilityState.Compatible))
                    .ToArray();
                var conclusivelyUncoveredContracts = contractSet.Variants
                    .Where(contract => !comparisonMatrix.Any(comparison =>
                            comparison.Contract == contract
                            && comparison.Compatibility.State is
                                VbaInterfaceContractCompatibilityState.Compatible
                                    or VbaInterfaceContractCompatibilityState.Indeterminate))
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
                            == VbaInterfaceContractParameterRole.Optional,
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
                            == VbaInterfaceContractParameterRole.ParamArray,
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

                VbaInterfaceContractResult? result = null;
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

                    result = new VbaInterfaceContractResult(
                        new VbaInterfaceContractType(
                            effectiveType.Value.Name,
                            effectiveType.Value.Identity),
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

                VbaInterfaceContractResult? result = null;
                if (kind is VbaInterfaceAccessorContractKind.Function
                    or VbaInterfaceAccessorContractKind.Get)
                {
                    var effectiveType = GetProjectReferenceEffectiveType(
                        member,
                        member.TypeReference);
                    result = new VbaInterfaceContractResult(
                        effectiveType is null
                            ? null
                            : new VbaInterfaceContractType(
                                effectiveType.Value.Name,
                                effectiveType.Value.Identity),
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
                ? new VbaInterfaceContractDefault(
                    VbaInterfaceContractDefaultState.Indeterminate)
                : VbaInterfaceContractDefault.Absent
            : VbaConstantExpressionEvaluator.Evaluate(
                parameter.DefaultExpression) is { Succeeded: true } evaluation
                ? new VbaInterfaceContractDefault(
                    VbaInterfaceContractDefaultState.Evaluated,
                    evaluation.Value)
                : new VbaInterfaceContractDefault(
                    VbaInterfaceContractDefaultState.Indeterminate);
        return new VbaInterfaceContractParameter(
            parameter.Name,
            effectiveType is null
                ? null
                : new VbaInterfaceContractType(
                    effectiveType.Value.Name,
                    effectiveType.Value.Identity),
            parameter.IsArray,
            parameter.IsByRef,
            parameter.IsParamArray
                ? VbaInterfaceContractParameterRole.ParamArray
                : parameter.IsOptional
                    ? VbaInterfaceContractParameterRole.Optional
                    : VbaInterfaceContractParameterRole.Required,
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
                (object?)"Variant");
        var defaultEvidence = parameter.DefaultExpression is null
            ? VbaInterfaceContractDefault.Absent
            : VbaConstantExpressionEvaluator.Evaluate(
                parameter.DefaultExpression) is { Succeeded: true } evaluation
                ? new VbaInterfaceContractDefault(
                    VbaInterfaceContractDefaultState.Evaluated,
                    evaluation.Value)
                : new VbaInterfaceContractDefault(
                    VbaInterfaceContractDefaultState.Indeterminate);
        return new VbaInterfaceContractParameter(
            parameter.Name,
            new VbaInterfaceContractType(
                effectiveType.Name,
                effectiveType.Identity),
            parameter.IsArray,
            parameter.IsByRef ?? true,
            parameter.IsParamArray
                ? VbaInterfaceContractParameterRole.ParamArray
                : parameter.IsOptional
                    ? VbaInterfaceContractParameterRole.Optional
                    : VbaInterfaceContractParameterRole.Required,
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
        VbaInterfaceContractResult? result)
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
        if (parameter.Role == VbaInterfaceContractParameterRole.ParamArray)
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
        return parameter.Role == VbaInterfaceContractParameterRole.Optional
            ? $"[{label}]"
            : label;
    }

    private (string Name, EffectiveTypeCategory Category, object? Identity)?
        GetEffectiveType(
        VbaSourceDocument interfaceDocument,
        VbaSourceDefinition variable)
        => GetEffectiveType(
            interfaceDocument,
            variable.Name,
            variable.TypeReference,
            variable.ConditionalCompilationPath);

    private (string Name, EffectiveTypeCategory Category, object? Identity)?
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
                    "Variant");
            }

            return defType.TypeName switch
            {
                "Variant" => (
                    defType.TypeName,
                    EffectiveTypeCategory.Variant,
                    defType.TypeName),
                "Object" => (
                    defType.TypeName,
                    EffectiveTypeCategory.Object,
                    defType.TypeName),
                _ => (
                    defType.TypeName,
                    EffectiveTypeCategory.Value,
                    defType.TypeName)
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
                    canonicalName),
                "Object" => (
                    canonicalName,
                    EffectiveTypeCategory.Object,
                    canonicalName),
                _ => (
                    canonicalName,
                    EffectiveTypeCategory.Value,
                    canonicalName)
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
        if (definitions.All(definition => definition.Kind is
                VbaSourceDefinitionKind.Class or VbaSourceDefinitionKind.Form))
        {
            return (
                effectiveTypeName,
                EffectiveTypeCategory.Object,
                outcome.Target.Identity);
        }

        if (definitions.All(definition => definition.Kind is
                VbaSourceDefinitionKind.Enum or VbaSourceDefinitionKind.Type))
        {
            return (
                effectiveTypeName,
                EffectiveTypeCategory.Value,
                outcome.Target.Identity);
        }

        return null;
    }

    private (string Name, EffectiveTypeCategory Category, object? Identity)?
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
                    canonicalName),
                "Object" => (
                    canonicalName,
                    EffectiveTypeCategory.Object,
                    canonicalName),
                _ => (
                    canonicalName,
                    EffectiveTypeCategory.Value,
                    canonicalName)
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
        return definition.Kind switch
        {
            VbaSourceDefinitionKind.Class or VbaSourceDefinitionKind.Form => (
                effectiveTypeName,
                EffectiveTypeCategory.Object,
                definition.Identity),
            VbaSourceDefinitionKind.Enum or VbaSourceDefinitionKind.Type => (
                effectiveTypeName,
                EffectiveTypeCategory.Value,
                definition.Identity),
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

    private VbaInterfaceContractCompatibility CompareContract(
        VbaSourceDocument implementingDocument,
        VbaInterfaceContractVariant contract,
        VbaSourceDefinition implementation)
    {
        if (!contract.IsSignatureComplete
            || HasIndeterminateConditionalCompilationOwnership(contract)
            || nameResolution.HasIndeterminateConditionalCompilationOwnership(
                implementation))
        {
            return new VbaInterfaceContractCompatibility(
                VbaInterfaceContractCompatibilityState.Indeterminate,
                []);
        }

        var implementationSignature = GetFulfillmentSignature(
            implementingDocument,
            implementation);
        if (implementationSignature is null)
        {
            return new VbaInterfaceContractCompatibility(
                VbaInterfaceContractCompatibilityState.Indeterminate,
                []);
        }

        var mismatches = new List<string>();
        var hasIndeterminateEvidence = false;
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

        var expectedParameterCount = contract.Parameters.Count
            + (contract.PropertyValueParameter is null ? 0 : 1);
        if (foundParameters.Length != expectedParameterCount)
        {
            mismatches.Add(
                $"parameter count: expected {expectedParameterCount}, "
                + $"found {foundParameters.Length}");
        }

        var comparableOrdinaryParameterCount = Math.Min(
            contract.Parameters.Count,
            foundOrdinaryParameters.Count);
        for (var index = 0; index < comparableOrdinaryParameterCount; index++)
        {
            CompareParameter(
                contract.Parameters[index],
                foundOrdinaryParameters[index],
                $"parameter {index + 1}",
                normalizePassing: false,
                mismatches,
                ref hasIndeterminateEvidence);
        }

        if (contract.PropertyValueParameter is not null
            && foundValueParameter is not null)
        {
            CompareParameter(
                contract.PropertyValueParameter,
                foundValueParameter,
                "value parameter",
                normalizePassing: true,
                mismatches,
                ref hasIndeterminateEvidence);
        }

        if (contract.Result is not null)
        {
            var foundEffectiveType = GetEffectiveType(
                implementingDocument,
                implementation.Name,
                implementation.TypeReference,
                implementation.ConditionalCompilationPath);
            if (contract.Result.Type is null || foundEffectiveType is null)
            {
                hasIndeterminateEvidence = true;
            }
            else
            {
                CompareTypeDimension(
                    contract.Result.Type,
                    new VbaInterfaceContractType(
                        foundEffectiveType.Value.Name,
                        foundEffectiveType.Value.Identity),
                    "return type",
                    mismatches,
                    ref hasIndeterminateEvidence);
            }

            var foundIsArray = callable?.IsReturnArray ?? implementation.IsArray;
            if (contract.Result.IsArray is not { } expectedIsArray)
            {
                hasIndeterminateEvidence = true;
            }
            else if (expectedIsArray != foundIsArray)
            {
                mismatches.Add(
                    "return array shape: expected "
                    + $"{(expectedIsArray ? "array" : "scalar")}, found "
                    + $"{(foundIsArray ? "array" : "scalar")}");
            }
        }

        return new VbaInterfaceContractCompatibility(
            mismatches.Count > 0
                ? VbaInterfaceContractCompatibilityState.Incompatible
                : hasIndeterminateEvidence
                    ? VbaInterfaceContractCompatibilityState.Indeterminate
                    : VbaInterfaceContractCompatibilityState.Compatible,
            mismatches);
    }

    private static void CompareParameter(
        VbaInterfaceContractParameter expected,
        VbaInterfaceContractParameter found,
        string subject,
        bool normalizePassing,
        ICollection<string> mismatches,
        ref bool hasIndeterminateEvidence)
    {
        if (expected.Type is null || found.Type is null)
        {
            hasIndeterminateEvidence = true;
        }
        else
        {
            CompareTypeDimension(
                expected.Type,
                found.Type,
                $"{subject} type",
                mismatches,
                ref hasIndeterminateEvidence);
        }
        if (expected.IsArray != found.IsArray)
        {
            mismatches.Add(
                $"{subject} array shape: expected "
                    + $"{(expected.IsArray ? "array" : "scalar")}, found "
                    + $"{(found.IsArray ? "array" : "scalar")}");
        }

        if (!normalizePassing
            && (expected.IsByRef is null || found.IsByRef is null))
        {
            hasIndeterminateEvidence = true;
        }
        else if (!normalizePassing && expected.IsByRef != found.IsByRef)
        {
            mismatches.Add(
                $"{subject} passing: expected "
                    + $"{(expected.IsByRef == true ? "ByRef" : "ByVal")}, found "
                    + $"{(found.IsByRef == true ? "ByRef" : "ByVal")}");
        }

        if (expected.Role != found.Role)
        {
            mismatches.Add(
                $"{subject} role: expected "
                    + $"{GetRolePresentation(expected.Role)}, found "
                    + $"{GetRolePresentation(found.Role)}");
        }

        var expectedDefault = expected.EffectiveDefault;
        var foundDefault = found.EffectiveDefault;
        if (expectedDefault.State == VbaInterfaceContractDefaultState.Indeterminate
            || foundDefault.State == VbaInterfaceContractDefaultState.Indeterminate)
        {
            hasIndeterminateEvidence = true;
        }
        else if (!HaveEquivalentDefaults(expectedDefault, foundDefault))
        {
            mismatches.Add(
                $"{subject} default: expected {expectedDefault.Presentation}, "
                    + $"found {foundDefault.Presentation}");
        }
    }

    private static bool HaveEquivalentDefaults(
        VbaInterfaceContractDefault expected,
        VbaInterfaceContractDefault found)
    {
        if (expected.State != found.State)
        {
            return false;
        }

        if (expected.State != VbaInterfaceContractDefaultState.Evaluated)
        {
            return true;
        }

        return expected.Value is { } expectedValue
            && found.Value is { } foundValue
            && expectedValue.HasSameEvaluatedValue(foundValue);
    }

    private static void CompareTypeDimension(
        VbaInterfaceContractType expected,
        VbaInterfaceContractType found,
        string subject,
        ICollection<string> mismatches,
        ref bool hasIndeterminateEvidence)
    {
        var comparison = CompareType(expected, found);
        if (comparison.State == VbaInterfaceContractCompatibilityState.Indeterminate)
        {
            hasIndeterminateEvidence = true;
        }
        else if (comparison.State
            == VbaInterfaceContractCompatibilityState.Incompatible)
        {
            mismatches.Add(
                $"{subject}: expected {expected.Name}, found {found.Name}");
        }
    }

    private static string GetRolePresentation(
        VbaInterfaceContractParameterRole role)
        => role switch
        {
            VbaInterfaceContractParameterRole.Required => "required",
            VbaInterfaceContractParameterRole.Optional => "Optional",
            VbaInterfaceContractParameterRole.ParamArray => "ParamArray",
            _ => throw new InvalidOperationException("Unknown parameter role.")
        };

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

    private static VbaInterfaceContractCompatibility CompareType(
        VbaInterfaceContractType expected,
        VbaInterfaceContractType found)
    {
        if (expected.Identity is null || found.Identity is null)
        {
            return new VbaInterfaceContractCompatibility(
                VbaInterfaceContractCompatibilityState.Indeterminate,
                []);
        }

        var expectedIntrinsic = expected.Identity as string;
        var foundIntrinsic = found.Identity as string;
        if (expectedIntrinsic is not null || foundIntrinsic is not null)
        {
            return new VbaInterfaceContractCompatibility(
                expectedIntrinsic is not null
                    && foundIntrinsic is not null
                    && expectedIntrinsic.Equals(
                        foundIntrinsic,
                        StringComparison.OrdinalIgnoreCase)
                    ? VbaInterfaceContractCompatibilityState.Compatible
                    : VbaInterfaceContractCompatibilityState.Incompatible,
                []);
        }

        return new VbaInterfaceContractCompatibility(
            expected.Identity.Equals(found.Identity)
                ? VbaInterfaceContractCompatibilityState.Compatible
                : VbaInterfaceContractCompatibilityState.Incompatible,
            []);
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
