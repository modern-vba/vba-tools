using VbaTools.Syntax;

namespace VbaTools.Semantics;

/// <summary>
/// Identifies one logical conditional declaration family inside one immutable
/// project-semantic snapshot.
/// </summary>
internal sealed class ConditionalFamilyIdentity
    : IEquatable<ConditionalFamilyIdentity>
{
    public ConditionalFamilyIdentity(
        object projectSnapshot,
        string declarationScope,
        string declarationNamespace,
        string name)
    {
        ProjectSnapshot = projectSnapshot;
        DeclarationScope = declarationScope;
        DeclarationNamespace = declarationNamespace;
        Name = name;
    }

    public object ProjectSnapshot { get; }

    public string DeclarationScope { get; }

    public string DeclarationNamespace { get; }

    public string Name { get; }

    public bool Equals(ConditionalFamilyIdentity? other)
        => other is not null
            && ReferenceEquals(ProjectSnapshot, other.ProjectSnapshot)
            && DeclarationScope.Equals(
                other.DeclarationScope,
                StringComparison.OrdinalIgnoreCase)
            && DeclarationNamespace.Equals(
                other.DeclarationNamespace,
                StringComparison.OrdinalIgnoreCase)
            && Name.Equals(other.Name, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj)
        => obj is ConditionalFamilyIdentity other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ProjectSnapshot, ReferenceEqualityComparer.Instance);
        hash.Add(DeclarationScope, StringComparer.OrdinalIgnoreCase);
        hash.Add(DeclarationNamespace, StringComparer.OrdinalIgnoreCase);
        hash.Add(Name, StringComparer.OrdinalIgnoreCase);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Retains every physical declaration that forms one conditional editor identity.
/// </summary>
internal sealed record ConditionalDeclarationFamily(
    ConditionalFamilyIdentity Identity,
    string CanonicalName,
    IReadOnlyList<VbaSourceDefinition> Variants);

internal sealed record PropertyNameTargetDescriptor(
    VbaPropertyNameTargetIdentity Identity,
    string CanonicalName,
    IReadOnlyList<VbaSourceDefinition> PropertyDefinitions,
    IReadOnlyList<VbaSourceDefinition> UnifiedPhysicalDefinitions,
    IReadOnlyList<VbaResolvedNameTarget> AccessorTargets,
    VbaSourceDefinition PresentationDefinition,
    bool IsUnifiedConditionalFamily);

/// <summary>
/// Builds conditional declaration relationships afresh for one semantic inventory.
/// </summary>
internal sealed class VbaConditionalDeclarationFamilyIndex
{
    private readonly IReadOnlyDictionary<VbaDefinitionIdentity, ConditionalDeclarationFamily>
        familiesByVariant;
    private readonly IReadOnlyDictionary<
        VbaDefinitionIdentity,
        PropertyNameTargetDescriptor> propertiesByVariant;
    private readonly IReadOnlyDictionary<VbaDefinitionIdentity, string>
        logicalMemberScopes;
    private readonly object projectSnapshot;

    public VbaConditionalDeclarationFamilyIndex(
        IReadOnlyList<VbaSourceDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        projectSnapshot = new object();
        var families = new Dictionary<VbaDefinitionIdentity, ConditionalDeclarationFamily>();
        logicalMemberScopes = CreateLogicalMemberScopes(documents);
        var propertyIdentitiesByVariant = CreatePropertyIdentities(documents);
        var declarations = documents
            .SelectMany(document => document.Definitions)
            .Where(VbaDeclarationRelationshipPolicy.IsFamilyCandidate)
            .Where(definition => definition.ConditionalCompilationPath is { IsEmpty: false })
            .OrderBy(definition => definition.Uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(definition => definition.Uri, StringComparer.Ordinal)
            .ThenBy(definition => definition.Range.Start.Line)
            .ThenBy(definition => definition.Range.Start.Character)
            .ToArray();
        var assigned = new HashSet<VbaDefinitionIdentity>();
        foreach (var declaration in declarations)
        {
            if (!assigned.Add(declaration.Identity))
            {
                continue;
            }

            var component = new List<VbaSourceDefinition> { declaration };
            for (var index = 0; index < component.Count; index++)
            {
                var current = component[index];
                foreach (var candidate in declarations)
                {
                    if (assigned.Contains(candidate.Identity)
                        || !VbaDeclarationRelationshipPolicy.AreFamilyPeers(
                            current,
                            candidate,
                            logicalMemberScopes)
                        || !CanJoinFamilyComponent(
                            component,
                            candidate,
                            propertyIdentitiesByVariant))
                    {
                        continue;
                    }

                    assigned.Add(candidate.Identity);
                    component.Add(candidate);
                }
            }

            var variants = component
                .OrderBy(definition => definition.Uri, StringComparer.OrdinalIgnoreCase)
                .ThenBy(definition => definition.Uri, StringComparer.Ordinal)
                .ThenBy(definition => definition.Range.Start.Line)
                .ThenBy(definition => definition.Range.Start.Character)
                .ThenBy(definition => definition.Range.End.Line)
                .ThenBy(definition => definition.Range.End.Character)
                .ToArray();
            var first = variants[0];
            var identity = new ConditionalFamilyIdentity(
                projectSnapshot,
                VbaDeclarationRelationshipPolicy.CreateFamilyScope(
                    variants,
                    logicalMemberScopes),
                VbaDeclarationRelationshipPolicy.CreateFamilyNamespace(variants),
                first.Name);
            var family = new ConditionalDeclarationFamily(
                identity,
                first.Name,
                Array.AsReadOnly(variants));
            foreach (var variant in variants)
            {
                families.Add(variant.Identity, family);
            }
        }

        familiesByVariant = families;
        propertiesByVariant = CreatePropertyTargets(
            documents,
            propertyIdentitiesByVariant);
    }

    private IReadOnlyDictionary<
        VbaDefinitionIdentity,
        VbaPropertyNameTargetIdentity> CreatePropertyIdentities(
            IReadOnlyList<VbaSourceDocument> documents)
    {
        var identities = new Dictionary<
            VbaDefinitionIdentity,
            VbaPropertyNameTargetIdentity>();
        foreach (var group in documents
            .SelectMany(document => document.Definitions)
            .Where(definition => definition.Kind == VbaSourceDefinitionKind.Property)
            .GroupBy(CreatePropertyOwnerKey, StringComparer.OrdinalIgnoreCase))
        {
            var identity = new VbaPropertyNameTargetIdentity(
                projectSnapshot,
                group.Key.ToUpperInvariant());
            foreach (var definition in group)
            {
                identities.Add(definition.Identity, identity);
            }
        }

        return identities;
    }

    private static bool CanJoinFamilyComponent(
        IReadOnlyList<VbaSourceDefinition> component,
        VbaSourceDefinition candidate,
        IReadOnlyDictionary<
            VbaDefinitionIdentity,
            VbaPropertyNameTargetIdentity> propertyIdentitiesByVariant)
    {
        if (candidate.Kind != VbaSourceDefinitionKind.Property
            || !propertyIdentitiesByVariant.TryGetValue(
                candidate.Identity,
                out var candidatePropertyIdentity))
        {
            return true;
        }

        return !component
            .Where(definition =>
                definition.Kind == VbaSourceDefinitionKind.Property)
            .Where(definition => propertyIdentitiesByVariant.TryGetValue(
                definition.Identity,
                out var componentPropertyIdentity)
                && componentPropertyIdentity == candidatePropertyIdentity)
            .Any(definition => definition.PropertyAccessorKind
                != candidate.PropertyAccessorKind);
    }

    private static IReadOnlyDictionary<VbaDefinitionIdentity, string>
        CreateLogicalMemberScopes(IReadOnlyList<VbaSourceDocument> documents)
    {
        var definitions = documents
            .SelectMany(document => document.Definitions)
            .ToArray();
        var guardedTypes = definitions
            .Where(definition => definition.Kind is VbaSourceDefinitionKind.Enum
                or VbaSourceDefinitionKind.Type)
            .Where(definition => definition.ConditionalCompilationPath is { IsEmpty: false })
            .ToArray();
        var scopes = new Dictionary<VbaDefinitionIdentity, string>();
        foreach (var member in definitions.Where(definition =>
            definition.Kind is VbaSourceDefinitionKind.EnumMember
                or VbaSourceDefinitionKind.TypeMember))
        {
            var parentKind = member.Kind == VbaSourceDefinitionKind.EnumMember
                ? VbaSourceDefinitionKind.Enum
                : VbaSourceDefinitionKind.Type;
            var parent = guardedTypes
                .Where(candidate => candidate.Kind == parentKind)
                .Where(candidate =>
                    VbaDocumentIdentityPolicy.SameDocument(
                        candidate.Uri,
                        member.Uri))
                .Where(candidate => candidate.Name.Equals(
                    member.ParentTypeName,
                    StringComparison.OrdinalIgnoreCase))
                .Where(candidate => IsAtOrBefore(candidate.Range.Start, member.Range.Start))
                .OrderByDescending(candidate => candidate.Range.Start.Line)
                .ThenByDescending(candidate => candidate.Range.Start.Character)
                .FirstOrDefault();
            if (parent is null)
            {
                continue;
            }

            scopes.Add(
                member.Identity,
                string.Join(
                    '\u001f',
                    "parent-family",
                    VbaDeclarationRelationshipPolicy.CreateFamilyScope([parent]),
                    VbaDeclarationRelationshipPolicy.CreateFamilyNamespace([parent]),
                    parent.Name));
        }

        return scopes;
    }

    private static bool IsAtOrBefore(VbaPosition left, VbaPosition right)
        => left.Line < right.Line
            || left.Line == right.Line && left.Character <= right.Character;

    public ConditionalDeclarationFamily? GetFamily(VbaSourceDefinition definition)
        => definition.Identity.Origin == VbaDefinitionOrigin.Source
            && familiesByVariant.TryGetValue(definition.Identity, out var family)
                ? family
                : null;

    public bool HaveSameLogicalMemberScope(
        VbaSourceDefinition left,
        VbaSourceDefinition right)
        => VbaDeclarationRelationshipPolicy.HaveSameLogicalMemberScope(
            left,
            right,
            logicalMemberScopes);

    public VbaResolvedNameTarget CreateNameTarget(
        VbaSourceDefinition selectedDefinition)
    {
        if (propertiesByVariant.TryGetValue(
                selectedDefinition.Identity,
                out var property))
        {
            return new VbaPropertyNameTarget(
                property,
                selectedDefinition);
        }

        return CreateDeclarationNameTarget(selectedDefinition);
    }

    private VbaResolvedNameTarget CreateDeclarationNameTarget(
        VbaSourceDefinition selectedDefinition)
    {
        var family = GetFamily(selectedDefinition);
        return family is null
            ? new VbaDefinitionNameTarget(selectedDefinition)
            : new VbaConditionalFamilyNameTarget(
                family,
                selectedDefinition);
    }

    private IReadOnlyDictionary<
        VbaDefinitionIdentity,
        PropertyNameTargetDescriptor> CreatePropertyTargets(
            IReadOnlyList<VbaSourceDocument> documents,
            IReadOnlyDictionary<
                VbaDefinitionIdentity,
                VbaPropertyNameTargetIdentity> propertyIdentitiesByVariant)
    {
        var targets = new Dictionary<
            VbaDefinitionIdentity,
            PropertyNameTargetDescriptor>();
        var properties = documents
            .SelectMany(document => document.Definitions)
            .Where(definition => definition.Kind == VbaSourceDefinitionKind.Property)
            .OrderBy(definition => definition.Uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(definition => definition.Uri, StringComparer.Ordinal)
            .ThenBy(definition => definition.Range.Start.Line)
            .ThenBy(definition => definition.Range.Start.Character)
            .ThenBy(definition => definition.Range.End.Line)
            .ThenBy(definition => definition.Range.End.Character)
            .ToArray();
        foreach (var group in properties.GroupBy(
            CreatePropertyOwnerKey,
            StringComparer.OrdinalIgnoreCase))
        {
            var propertyDefinitions = group.ToArray();
            var logicalAccessors = Coalesce(propertyDefinitions).ToArray();
            if (logicalAccessors.Length <= 1
                || VbaPropertyAccessorCoalescing.Coalesce(logicalAccessors).Count != 1)
            {
                continue;
            }

            var accessorTargets = logicalAccessors
                .Select(CreateDeclarationNameTarget)
                .ToArray();
            var expandedPhysicalDefinitions = accessorTargets
                .SelectMany(target => target.PhysicalDefinitions)
                .Concat(propertyDefinitions)
                .DistinctBy(definition => definition.Identity)
                .OrderBy(definition => definition.Uri, StringComparer.OrdinalIgnoreCase)
                .ThenBy(definition => definition.Uri, StringComparer.Ordinal)
                .ThenBy(definition => definition.Range.Start.Line)
                .ThenBy(definition => definition.Range.Start.Character)
                .ThenBy(definition => definition.Range.End.Line)
                .ThenBy(definition => definition.Range.End.Character)
                .ToArray();
            var presentationDefinition = VbaPropertyAccessorCoalescing
                .Coalesce(logicalAccessors)
                .Single();
            var isUnifiedConditionalFamily = accessorTargets.Any(
                    target => target.IsConditionalFamily)
                && expandedPhysicalDefinitions.All(definition =>
                    definition.ConditionalCompilationPath is
                        { IsEmpty: false });
            var descriptor = new PropertyNameTargetDescriptor(
                propertyIdentitiesByVariant[propertyDefinitions[0].Identity],
                isUnifiedConditionalFamily
                    ? expandedPhysicalDefinitions[0].Name
                    : presentationDefinition.Name,
                Array.AsReadOnly(propertyDefinitions),
                Array.AsReadOnly(expandedPhysicalDefinitions),
                Array.AsReadOnly(accessorTargets),
                presentationDefinition,
                isUnifiedConditionalFamily);
            var mappedDefinitions = isUnifiedConditionalFamily
                ? expandedPhysicalDefinitions
                : propertyDefinitions;
            foreach (var definition in mappedDefinitions)
            {
                targets[definition.Identity] = descriptor;
            }
        }

        return targets;
    }

    private static string CreatePropertyOwnerKey(VbaSourceDefinition definition)
    {
        const char separator = '\u001f';
        var owner = definition.Identity.Origin == VbaDefinitionOrigin.ProjectReference
            ? string.Join(
                separator,
                "reference",
                definition.ModuleName,
                definition.ParentTypeName ?? string.Empty)
            : string.Join(
                separator,
                "source",
                VbaDocumentIdentityPolicy.GetDocumentStableKey(
                    definition.Uri),
                definition.ModuleName);
        return string.Join(separator, owner, definition.Name);
    }

    public IReadOnlyList<VbaSourceDefinition> GetLogicalDefinitions(
        VbaSourceDefinition definition)
        => GetFamily(definition)?.Variants ?? [definition];

    public IReadOnlyList<VbaSourceDefinition> Coalesce(
        IEnumerable<VbaSourceDefinition> definitions)
    {
        var candidates = definitions.ToArray();
        var candidateIdentities = candidates
            .Select(definition => definition.Identity)
            .ToHashSet();
        var seenFamilies = new HashSet<ConditionalFamilyIdentity>();
        var result = new List<VbaSourceDefinition>(candidates.Length);
        foreach (var candidate in candidates)
        {
            var family = GetFamily(candidate);
            if (family is null)
            {
                result.Add(candidate);
                continue;
            }

            if (!seenFamilies.Add(family.Identity))
            {
                continue;
            }

            result.Add(family.Variants.First(variant =>
                candidateIdentities.Contains(variant.Identity)));
        }

        return result;
    }
}

/// <summary>
/// Indexes project-aware declaration collisions for one semantic snapshot.
/// </summary>
internal sealed class VbaSemanticDiagnosticIndex
{
    private readonly VbaNameCandidateInventory definitionCandidates;
    private readonly IReadOnlyDictionary<
        VbaDocumentIdentity,
        IReadOnlyList<VbaSemanticDiagnostic>> diagnosticsByIdentity;

    public VbaSemanticDiagnosticIndex(
        IReadOnlyList<VbaSourceDocument> documents,
        VbaProjectSemanticResolution semanticResolution,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        definitionCandidates = semanticResolution.DefinitionCandidates;
        var diagnostics = new Dictionary<
            VbaDocumentIdentity,
            List<VbaSemanticDiagnostic>>();
        foreach (var diagnostic in VbaDuplicateDeclarationDiagnostics.Collect(documents, cancellationToken))
        {
            AddProjectDiagnostic(diagnostics, diagnostic.SourceUri,
                new VbaSemanticDiagnostic(diagnostic.Code, diagnostic.Message,
                    diagnostic.Range, diagnostic.Severity));
        }

        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var syntaxTree = document.SyntaxTree
                ?? VbaSyntaxTree.ParseModule(document.Uri, document.Text);
            foreach (var diagnostic in semanticResolution
                .GetInterfaceContractDiagnostics(document))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddProjectDiagnostic(diagnostics, document.Uri, diagnostic);
            }

            foreach (var variable in document.Definitions.Where(definition =>
                         definition.IsWithEvents
                         && !definition.IsRecoveredWithEventsVariableDeclaration))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var eligibility = semanticResolution.GetWithEventsTypeEligibility(
                    document,
                    variable);
                var diagnostic = eligibility?.Kind switch
                {
                    VbaWithEventsTypeEligibilityKind.InvalidEnclosingClass =>
                        new VbaSemanticDiagnostic(
                            "validation.withEventsTypeCannotBeEnclosingClass",
                            "A WithEvents variable cannot use its enclosing class as its declared type.",
                            variable.TypeReferenceRange ?? variable.Range),
                    VbaWithEventsTypeEligibilityKind.InvalidNotClass =>
                        new VbaSemanticDiagnostic(
                            "validation.withEventsTypeMustBeClass",
                            "WithEvents variables must use a specific class type.",
                            variable.TypeReferenceRange ?? variable.Range),
                    VbaWithEventsTypeEligibilityKind.InvalidInaccessibleType =>
                        new VbaSemanticDiagnostic(
                            "validation.withEventsTypeMustBeAccessible",
                            "The declared WithEvents class must be accessible to VBA.",
                            variable.TypeReferenceRange ?? variable.Range),
                    VbaWithEventsTypeEligibilityKind.InvalidNoEvents =>
                        new VbaSemanticDiagnostic(
                            "validation.withEventsTypeMustExposeEvents",
                            "The declared WithEvents class must expose at least one Event.",
                            variable.TypeReferenceRange ?? variable.Range),
                    _ => null
                };
                if (diagnostic is null)
                {
                    continue;
                }

                AddProjectDiagnostic(
                    diagnostics,
                    document.Uri,
                    diagnostic);
            }

            foreach (var handler in document.Definitions.Where(definition =>
                         definition.Kind is VbaSourceDefinitionKind.Procedure
                             or VbaSourceDefinitionKind.Property))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var intrinsicAnalysis = semanticResolution
                    .AnalyzeIntrinsicHostHandler(document, handler);
                if (intrinsicAnalysis is not null)
                {
                    var intrinsicCallable = syntaxTree.Module
                            .CallableDeclarations.FirstOrDefault(candidate =>
                                candidate.Range.Start.Line
                                    == handler.Range.Start.Line
                                && candidate.Name.Equals(
                                    handler.Name,
                                    StringComparison.OrdinalIgnoreCase)
                                && candidate.PropertyAccessorKind
                                    == handler.PropertyAccessorKind);
                        if (intrinsicCallable?.DeclarationKeywordRange
                                is { } intrinsicKeywordRange
                            && intrinsicAnalysis.Recognition
                                == VbaIntrinsicHostHandlerRecognition
                                    .NonSubProcedureAssociation)
                        {
                            AddProjectDiagnostic(
                                diagnostics,
                                document.Uri,
                                new VbaSemanticDiagnostic(
                                    "validation.eventHandlerMustBeSub",
                                    "Event handlers must be declared as Sub procedures.",
                                    ToRange(intrinsicKeywordRange)));
                        }

                        else if (intrinsicCallable is not null
                            && intrinsicAnalysis.Recognition
                                == VbaIntrinsicHostHandlerRecognition
                                    .ResolvedHandler)
                        {
                            var intrinsicCompatibility = semanticResolution
                                .AnalyzeIntrinsicHostHandlerCompatibility(
                                    document,
                                    intrinsicAnalysis);
                            if (intrinsicCompatibility.ShouldReportDiagnostic)
                            {
                                AddProjectDiagnostic(
                                    diagnostics,
                                    document.Uri,
                                    new VbaSemanticDiagnostic(
                                        "validation.incompatibleEventHandlerSignature",
                                        "Event handler signature does not match any available Event signature.",
                                        intrinsicCallable.ParameterListRange
                                            is { } intrinsicParameterListRange
                                            ? ToRange(intrinsicParameterListRange)
                                            : handler.Range,
                                        Details: intrinsicCompatibility
                                            .CreateDiagnosticDetails()));
                            }
                        }

                    continue;
                }

                var analysis = semanticResolution.AnalyzeWithEventsHandler(
                    document,
                    handler);
                if (analysis is null
                    || semanticResolution
                        .HasIndeterminateConditionalCompilationOwnership(
                            handler)
                    || !analysis.BindingSet.IsFullyDiagnosticAuthoritative)
                {
                    continue;
                }

                var callable = syntaxTree.Module.CallableDeclarations.FirstOrDefault(
                    candidate => candidate.Range.Start.Line
                            == handler.Range.Start.Line
                        && candidate.Name.Equals(
                            handler.Name,
                            StringComparison.OrdinalIgnoreCase)
                        && candidate.PropertyAccessorKind
                            == handler.PropertyAccessorKind);
                if (callable?.DeclarationKeywordRange is not { } keywordRange)
                {
                    continue;
                }

                if (analysis.Recognition
                    == VbaWithEventsHandlerRecognition.NonSubProcedureAssociation)
                {
                    AddProjectDiagnostic(
                        diagnostics,
                        document.Uri,
                        new VbaSemanticDiagnostic(
                            "validation.eventHandlerMustBeSub",
                            "Event handlers must be declared as Sub procedures.",
                            ToRange(keywordRange)));
                    continue;
                }

                if (analysis.Recognition
                    != VbaWithEventsHandlerRecognition.ResolvedHandler)
                {
                    continue;
                }

                var compatibility = semanticResolution
                    .AnalyzeWithEventsHandlerCompatibility(document, analysis);
                if (!compatibility.ShouldReportDiagnostic)
                {
                    continue;
                }

                AddProjectDiagnostic(
                    diagnostics,
                    document.Uri,
                    new VbaSemanticDiagnostic(
                        "validation.incompatibleEventHandlerSignature",
                        "Event handler signature does not match any available Event signature.",
                        callable.ParameterListRange is { } parameterListRange
                            ? ToRange(parameterListRange)
                            : handler.Range,
                        Details: compatibility.CreateDiagnosticDetails()));
            }

            foreach (var argumentList in syntaxTree.Module.ArgumentLists)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isRaiseEventCall = IsRaiseEventCall(syntaxTree, argumentList);
                if (isRaiseEventCall
                        && HasRaiseEventPlacementDiagnostic(syntaxTree, argumentList))
                {
                    continue;
                }

                if (isRaiseEventCall
                    && semanticResolution.TryResolveRaiseEventTarget(
                        document.Uri,
                        argumentList,
                        out var raiseEventTarget)
                    && raiseEventTarget is null)
                {
                    AddRaiseEventTargetDiagnostic(
                        diagnostics,
                        document.Uri,
                        ToRange(GetRaiseEventTargetRange(syntaxTree, argumentList)));
                    continue;
                }

                if (argumentList.IsIncomplete)
                {
                    continue;
                }

                if (HasSpecificCallShapeDiagnostic(syntaxTree, argumentList))
                {
                    continue;
                }

                var compatibility = semanticResolution.AnalyzeCompleteCall(
                    document.Uri,
                    argumentList);
                if (compatibility is null
                    || compatibility.Variants.Count == 0
                    || compatibility.Variants.Any(variant =>
                        variant.State != VbaCallCompatibilityState.Inapplicable))
                {
                    continue;
                }

                AddProjectDiagnostic(
                    diagnostics,
                    document.Uri,
                    new VbaSemanticDiagnostic(
                        "validation.incompatibleCallArgumentList",
                        "No available callable signature accepts this argument list.",
                        ToRange(GetCallDiagnosticRange(syntaxTree, argumentList)),
                        Details: CreateCallDiagnosticDetails(
                            compatibility,
                            argumentList)));
            }

            foreach (var raiseEventKeyword in syntaxTree.TokenStream.Tokens.Where(token =>
                         token.Kind == VbaTokenKind.Keyword
                         && token.Text.Equals("RaiseEvent", StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceLine = syntaxTree.SourceText.Lines[raiseEventKeyword.Range.Start.Line];
                if (VbaLexicalFacts.IsPositionInComment(
                        sourceLine.Text,
                        raiseEventKeyword.Range.Start.Character)
                    || syntaxTree.Diagnostics.Any(diagnostic =>
                        diagnostic.Code == "syntax.raiseEventStatementNotAllowedHere"
                        && diagnostic.Range == raiseEventKeyword.Range))
                {
                    continue;
                }

                var codeLine = VbaIdentifier.TrimEndWhitespace(
                    VbaLexicalFacts.SplitCodeAndComment(sourceLine.Text).CodePart);
                var callSite = syntaxTree.GetPositionSyntax(
                    sourceLine.LineNumber,
                    codeLine.Length).CallSite;
                var targetSyntax = callSite?.Callee.Target;
                var owningRaiseEventKeyword = targetSyntax is null
                    ? null
                    : FindPrecedingTokenInLogicalStatement(
                        syntaxTree.TokenStream.Tokens,
                        targetSyntax.Range.Start.Offset);
                if (targetSyntax is null
                    || owningRaiseEventKeyword?.Range != raiseEventKeyword.Range
                    || !owningRaiseEventKeyword.Text.Equals(
                        "RaiseEvent",
                        StringComparison.OrdinalIgnoreCase)
                    || targetSyntax.Range.Start.Offset <= raiseEventKeyword.Range.End.Offset
                    || syntaxTree.Module.ArgumentLists.Any(argumentList =>
                        argumentList.CalleeRange == targetSyntax.Range)
                    || !semanticResolution.TryResolveRaiseEventTarget(
                        document.Uri,
                        callSite,
                        out var raiseEventTarget)
                    || raiseEventTarget is not null)
                {
                    continue;
                }

                AddRaiseEventTargetDiagnostic(
                    diagnostics,
                    document.Uri,
                    ToRange(targetSyntax.Range));
            }
        }

        diagnosticsByIdentity = diagnostics.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<VbaSemanticDiagnostic>)
                Array.AsReadOnly(pair.Value.ToArray()));
    }

    public IReadOnlyList<VbaSemanticDiagnostic> GetDiagnostics(string uri)
        => definitionCandidates.TryIdentifyDocument(
                uri,
                out var identity)
            && diagnosticsByIdentity.TryGetValue(identity, out var diagnostics)
            ? diagnostics
            : [];

    private void AddRaiseEventTargetDiagnostic(
        IDictionary<
            VbaDocumentIdentity,
            List<VbaSemanticDiagnostic>> diagnostics,
        string uri,
        VbaRange range)
    {
        AddProjectDiagnostic(
            diagnostics,
            uri,
            new VbaSemanticDiagnostic(
                "validation.raiseEventTargetNotDeclaredInEnclosingModule",
                "RaiseEvent target must be an Event declared in the enclosing class module.",
                range));
    }

    private void AddProjectDiagnostic(
        IDictionary<
            VbaDocumentIdentity,
            List<VbaSemanticDiagnostic>> diagnostics,
        string uri,
        VbaSemanticDiagnostic diagnostic)
    {
        if (!definitionCandidates.TryIdentifyDocument(
                uri,
                out var identity))
        {
            return;
        }

        if (!diagnostics.TryGetValue(identity, out var documentDiagnostics))
        {
            documentDiagnostics = [];
            diagnostics.Add(identity, documentDiagnostics);
        }

        documentDiagnostics.Add(diagnostic);
    }

    private static bool HasSpecificCallShapeDiagnostic(
        VbaSyntaxTree syntaxTree,
        VbaArgumentListSyntax argumentList)
    {
        if (HasRaiseEventPlacementDiagnostic(syntaxTree, argumentList))
        {
            return true;
        }

        if (IsRaiseEventCall(syntaxTree, argumentList)
            && syntaxTree.Diagnostics.Any(diagnostic =>
                diagnostic.Code is "syntax.raiseEventArgumentListRequiresParentheses"
                    or "syntax.raiseEventEmptyArgumentListNotAllowed"
                    or "syntax.raiseEventOmittedArgumentNotAllowed"
                && diagnostic.Range.Start.Offset <= argumentList.Range.End.Offset
                && argumentList.Range.Start.Offset <= diagnostic.Range.End.Offset))
        {
            return true;
        }

        if (IsRaiseEventCall(syntaxTree, argumentList)
            && argumentList.Arguments.Any(argument => argument.Name is not null))
        {
            return true;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasNamedArgument = false;
        foreach (var argument in argumentList.Arguments)
        {
            if (argument.Name is not null)
            {
                hasNamedArgument = true;
                if (!names.Add(argument.Name))
                {
                    return true;
                }
            }
            else if (hasNamedArgument)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasRaiseEventPlacementDiagnostic(
        VbaSyntaxTree syntaxTree,
        VbaArgumentListSyntax argumentList)
    {
        var raiseEventKeyword = FindRaiseEventKeyword(syntaxTree, argumentList);
        return raiseEventKeyword is not null
            && syntaxTree.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "syntax.raiseEventStatementNotAllowedHere"
                && diagnostic.Range == raiseEventKeyword.Range);
    }

    private static bool IsRaiseEventCall(
        VbaSyntaxTree syntaxTree,
        VbaArgumentListSyntax argumentList)
        => FindRaiseEventKeyword(syntaxTree, argumentList) is not null;

    private static VbaToken? FindRaiseEventKeyword(
        VbaSyntaxTree syntaxTree,
        VbaArgumentListSyntax argumentList)
    {
        if (argumentList.CalleeRange is not { } calleeRange)
        {
            return null;
        }

        if (VbaLexicalFacts.IsPositionInComment(
                syntaxTree.SourceText.Lines[calleeRange.Start.Line].Text,
                calleeRange.Start.Character))
        {
            return null;
        }

        var precedingToken = FindPrecedingTokenInLogicalStatement(
            syntaxTree.TokenStream.Tokens,
            calleeRange.Start.Offset);
        return precedingToken?.Text.Equals(
                "RaiseEvent",
                StringComparison.OrdinalIgnoreCase) == true
            ? precedingToken
            : null;
    }

    private static VbaToken? FindPrecedingTokenInLogicalStatement(
        IReadOnlyList<VbaToken> tokens,
        int offset)
    {
        var lower = 0;
        var upper = tokens.Count;
        while (lower < upper)
        {
            var middle = lower + ((upper - lower) / 2);
            if (tokens[middle].Range.Start.Offset < offset)
            {
                lower = middle + 1;
            }
            else
            {
                upper = middle;
            }
        }

        for (var index = lower - 1; index >= 0; index--)
        {
            var token = tokens[index];
            if (token.Range.End.Offset > offset
                || token.Kind == VbaTokenKind.Whitespace)
            {
                continue;
            }

            if (token.Kind == VbaTokenKind.Comment)
            {
                return null;
            }

            if (token.Kind == VbaTokenKind.NewLine)
            {
                index--;
                while (index >= 0 && tokens[index].Kind == VbaTokenKind.Whitespace)
                {
                    index--;
                }

                if (index >= 0 && tokens[index].Kind == VbaTokenKind.LineContinuation)
                {
                    continue;
                }

                return null;
            }

            if (token.Kind == VbaTokenKind.LineContinuation)
            {
                continue;
            }

            if (token.Kind == VbaTokenKind.Punctuation && token.Text == ":")
            {
                return null;
            }

            return token;
        }

        return null;
    }

    private static VbaRange ToRange(VbaSyntaxRange range)
        => new(
            new VbaPosition(range.Start.Line, range.Start.Character),
            new VbaPosition(range.End.Line, range.End.Character));

    private static VbaSyntaxRange GetCallDiagnosticRange(
        VbaSyntaxTree syntaxTree,
        VbaArgumentListSyntax argumentList)
    {
        if (argumentList.Arguments.Count > 0)
        {
            return argumentList.Form == VbaCallSyntaxForm.Statement
                ? new VbaSyntaxRange(
                    argumentList.Arguments[0].Range.Start,
                    argumentList.Range.End)
                : argumentList.Range;
        }

        var calleeRange = argumentList.CalleeRange ?? argumentList.Range;
        return syntaxTree.TokenStream.Tokens
            .Where(token => calleeRange.Start.Offset <= token.Range.Start.Offset
                && token.Range.End.Offset <= calleeRange.End.Offset)
            .LastOrDefault(token => token.Kind is VbaTokenKind.Identifier or VbaTokenKind.Keyword)
            ?.Range
            ?? calleeRange;
    }

    private static VbaSyntaxRange GetRaiseEventTargetRange(
        VbaSyntaxTree syntaxTree,
        VbaArgumentListSyntax argumentList)
    {
        var calleeRange = argumentList.CalleeRange ?? argumentList.Range;
        return syntaxTree.TokenStream.Tokens
            .Where(token => calleeRange.Start.Offset <= token.Range.Start.Offset
                && token.Range.End.Offset <= calleeRange.End.Offset)
            .LastOrDefault(token => token.Kind is VbaTokenKind.Identifier or VbaTokenKind.Keyword)
            ?.Range
            ?? calleeRange;
    }

    private static IReadOnlyList<VbaDiagnosticDetail> CreateCallDiagnosticDetails(
        VbaConditionalCallCompatibility compatibility,
        VbaArgumentListSyntax argumentList)
    {
        var details = new List<VbaDiagnosticDetail>();
        foreach (var variant in compatibility.Variants)
        {
            if (variant.Signature is null
                || variant.InvocationSignature is null
                || variant.Mapping is null)
            {
                continue;
            }

            var reasons = CreateCallMismatchReasons(
                variant.Definition,
                variant.Signature,
                variant.InvocationSignature,
                variant.Mapping,
                argumentList,
                compatibility.Context);
            if (reasons.Count == 0)
            {
                continue;
            }

            var label = variant.Definition.ConditionalCompilationPath is
                    { IsEmpty: false }
                ? $"{variant.Signature.Label} [#If]"
                : variant.Signature.Label;
            var reasonText = string.Join("; ", reasons);
            var location = variant.Definition.Identity.Origin == VbaDefinitionOrigin.Source
                ? new VbaDiagnosticLocation(
                    variant.Definition.Uri,
                    variant.Definition.Range)
                : null;
            details.Add(new VbaDiagnosticDetail(
                location,
                $"Candidate signature: {label}. Mismatches: {reasonText}.",
                $"Candidate signature: {label}.\nMismatches: {reasonText}."));
        }

        return Array.AsReadOnly(details.ToArray());
    }

    private static IReadOnlyList<string> CreateCallMismatchReasons(
        VbaSourceDefinition definition,
        VbaCallableSignature physicalSignature,
        VbaCallableSignature invocationSignature,
        VbaCompleteCallArgumentMapping completeMapping,
        VbaArgumentListSyntax argumentList,
        VbaCallContext context)
    {
        var reasons = new List<string>();
        if (completeMapping.Mapping.ContextCompatibility
            == VbaCallContextCompatibility.Incompatible)
        {
            reasons.Add(
                $"call context: expected {GetExpectedCallableKinds(context)}, "
                + $"found {GetPhysicalCallableKind(definition, physicalSignature)}");
        }

        foreach (var mismatch in completeMapping.Mapping.Mismatches)
        {
            if (mismatch.Kind == VbaCallMappingMismatchKind.DuplicateParameterAssignment
                && mismatch.ParameterIndex is int duplicateParameterIndex)
            {
                reasons.Add(
                    $"argument {mismatch.SourceIndex + 1} "
                    + $"('{argumentList.Arguments[mismatch.SourceIndex].Name}') mapping: "
                    + VbaCallDiagnosticText.GetParameterSubject(
                        invocationSignature.Parameters[duplicateParameterIndex],
                        duplicateParameterIndex)
                    + " "
                    + "is already supplied");
            }
            else if (mismatch.Kind
                == VbaCallMappingMismatchKind.NamedArgumentsNotAccepted)
            {
                var writtenName = argumentList.Arguments[mismatch.SourceIndex].Name;
                reasons.Add(
                    $"argument {mismatch.SourceIndex + 1} ('{writtenName}') mapping: "
                    + "named arguments are not accepted");
            }
            else if (mismatch.Kind == VbaCallMappingMismatchKind.UnknownNamedParameter)
            {
                var writtenName = argumentList.Arguments[mismatch.SourceIndex].Name;
                reasons.Add(
                    $"argument {mismatch.SourceIndex + 1} ('{writtenName}') mapping: "
                    + $"no parameter named '{writtenName}'");
            }
            else if (mismatch.Kind == VbaCallMappingMismatchKind.ExcessPositionalArgument)
            {
                reasons.Add(
                    $"argument {mismatch.SourceIndex + 1} mapping: "
                    + "no parameter accepts this argument");
            }
        }

        var requiredParameterIndexes = completeMapping.MissingRequiredParameterIndexes
            .Concat(completeMapping.Mapping.Mismatches
                .Where(mismatch => mismatch.Kind
                    == VbaCallMappingMismatchKind.RequiredArgumentOmitted)
                .Select(mismatch => mismatch.ParameterIndex)
                .OfType<int>())
            .Distinct()
            .OrderBy(parameterIndex => parameterIndex);
        foreach (var parameterIndex in requiredParameterIndexes)
        {
            reasons.Add(
                VbaCallDiagnosticText.GetParameterSubject(
                    invocationSignature.Parameters[parameterIndex],
                    parameterIndex)
                + ": required argument is missing");
        }

        reasons.AddRange(completeMapping.TypeMismatchReasons);

        return reasons.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string GetExpectedCallableKinds(VbaCallContext context)
        => context switch
        {
            VbaCallContext.StatementInvocation => "Sub or Function",
            VbaCallContext.ValueRead => "Function or Property Get",
            VbaCallContext.PropertyLetAssignment => "Property Let",
            VbaCallContext.PropertySetAssignment => "Property Set",
            VbaCallContext.RaiseEvent => "Event",
            _ => "a compatible callable"
        };

    private static string GetPhysicalCallableKind(
        VbaSourceDefinition definition,
        VbaCallableSignature signature)
    {
        if (definition.PropertyAccessorKind is { } accessorKind)
        {
            return accessorKind switch
            {
                VbaPropertyAccessorKind.Get => "Property Get",
                VbaPropertyAccessorKind.Let => "Property Let",
                VbaPropertyAccessorKind.Set => "Property Set",
                _ => "Property"
            };
        }

        var kind = signature.CallableKind switch
        {
            VbaCallableKind.Sub => "Sub",
            VbaCallableKind.Function => "Function",
            VbaCallableKind.Property => "Property",
            VbaCallableKind.Event => "Event",
            _ => "callable"
        };
        return signature.Label.StartsWith("Declare ", StringComparison.OrdinalIgnoreCase)
            ? $"Declare {kind}"
            : kind;
    }
}
