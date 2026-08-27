using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.SourceModel;

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
    private readonly object projectSnapshot;

    public VbaConditionalDeclarationFamilyIndex(
        IReadOnlyList<VbaSourceDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        projectSnapshot = new object();
        var families = new Dictionary<VbaDefinitionIdentity, ConditionalDeclarationFamily>();
        var logicalMemberScopes = CreateLogicalMemberScopes(documents);
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
                .Where(candidate => candidate.Uri.Equals(
                    member.Uri,
                    StringComparison.OrdinalIgnoreCase))
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
                definition.Uri,
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

internal static class VbaDeclarationRelationshipPolicy
{
    private const char KeySeparator = '\u001f';

    public static bool IsFamilyCandidate(VbaSourceDefinition definition)
        => definition.Identity.Origin == VbaDefinitionOrigin.Source
            && definition.Kind is not (
                VbaSourceDefinitionKind.Module
                or VbaSourceDefinitionKind.Class
                or VbaSourceDefinitionKind.Form)
            && (definition.Visibility != VbaSourceDefinitionVisibility.Local
                || definition.ParentProcedureRange is not null)
            && (definition.Kind is not (
                    VbaSourceDefinitionKind.EnumMember
                    or VbaSourceDefinitionKind.TypeMember)
                || definition.ParentTypeName is not null)
            && (definition.Kind != VbaSourceDefinitionKind.Property
                || definition.PropertyAccessorKind is not null);

    public static bool AreFamilyPeers(
        VbaSourceDefinition left,
        VbaSourceDefinition right,
        IReadOnlyDictionary<VbaDefinitionIdentity, string> logicalMemberScopes)
        => HaveSameName(left, right)
            && HaveSameDeclarationScopeAndNamespace(
                left,
                right,
                logicalMemberScopes)
            && HaveCompatiblePropertyAccessorKinds(left, right);

    public static bool AreDirectCollisionPeers(
        VbaSourceDefinition left,
        VbaSourceDefinition right)
        => HaveSameName(left, right)
            && HaveSameDeclarationScopeAndNamespace(left, right, null)
            && HaveCompatiblePropertyAccessorKinds(left, right);

    public static string CreateFamilyScope(
        IReadOnlyList<VbaSourceDefinition> variants,
        IReadOnlyDictionary<VbaDefinitionIdentity, string>? logicalMemberScopes = null)
    {
        var first = variants[0];
        if (variants.All(definition =>
                definition.Visibility == VbaSourceDefinitionVisibility.Local))
        {
            return string.Join(
                KeySeparator,
                "procedure",
                first.Uri,
                first.ParentProcedureName ?? string.Empty,
                CreateRangeKey(first.ParentProcedureRange));
        }

        if (variants.All(definition =>
                definition.Kind is VbaSourceDefinitionKind.EnumMember
                    or VbaSourceDefinitionKind.TypeMember))
        {
            if (logicalMemberScopes is not null
                && variants
                    .Select(definition => logicalMemberScopes.TryGetValue(
                        definition.Identity,
                        out var scope)
                            ? scope
                            : null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray() is [not null and var logicalScope])
            {
                return logicalScope;
            }

            return string.Join(
                KeySeparator,
                "type",
                first.Uri,
                first.ParentTypeName ?? string.Empty);
        }

        if (variants.All(IsDeclaredType)
            && (variants.Select(definition => definition.Uri)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Skip(1)
                    .Any()
                || variants.Any(IsProjectVisibleType)))
        {
            return "project-type";
        }

        return string.Join(KeySeparator, "module", first.Uri);
    }

    public static string CreateFamilyNamespace(
        IReadOnlyList<VbaSourceDefinition> variants)
    {
        if (variants.All(IsDeclaredType))
        {
            return "type";
        }

        if (variants.All(definition =>
                definition.Kind == VbaSourceDefinitionKind.EnumMember))
        {
            return "enum-member";
        }

        if (variants.All(definition =>
                definition.Kind == VbaSourceDefinitionKind.TypeMember))
        {
            return "type-member";
        }

        if (variants.All(definition =>
                definition.Kind == VbaSourceDefinitionKind.Event))
        {
            return "event";
        }

        if (variants.All(definition =>
                definition.Kind == VbaSourceDefinitionKind.Property)
            && variants.Select(definition => definition.PropertyAccessorKind)
                .Distinct()
                .Count() == 1)
        {
            return $"value-property-{variants[0].PropertyAccessorKind}";
        }

        return "value";
    }

    private static bool HaveSameName(
        VbaSourceDefinition left,
        VbaSourceDefinition right)
        => left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase);

    private static bool HaveSameDeclarationScopeAndNamespace(
        VbaSourceDefinition left,
        VbaSourceDefinition right,
        IReadOnlyDictionary<VbaDefinitionIdentity, string>? logicalMemberScopes)
    {
        if (left.Visibility == VbaSourceDefinitionVisibility.Local
            || right.Visibility == VbaSourceDefinitionVisibility.Local)
        {
            return left.Visibility == VbaSourceDefinitionVisibility.Local
                && right.Visibility == VbaSourceDefinitionVisibility.Local
                && SameUri(left, right)
                && left.ParentProcedureRange is not null
                && left.ParentProcedureRange == right.ParentProcedureRange;
        }

        if (IsProjectNamespacePeer(left, right))
        {
            return true;
        }

        if (HaveSameLogicalMemberScope(left, right, logicalMemberScopes))
        {
            return true;
        }

        if (!SameUri(left, right))
        {
            return false;
        }

        if (left.Kind == VbaSourceDefinitionKind.TypeMember
            || right.Kind == VbaSourceDefinitionKind.TypeMember)
        {
            return left.Kind == VbaSourceDefinitionKind.TypeMember
                && right.Kind == VbaSourceDefinitionKind.TypeMember
                && left.ParentTypeName is not null
                && left.ParentTypeName.Equals(
                    right.ParentTypeName,
                    StringComparison.OrdinalIgnoreCase);
        }

        if (left.Kind == VbaSourceDefinitionKind.Event
            || right.Kind == VbaSourceDefinitionKind.Event)
        {
            return left.Kind == VbaSourceDefinitionKind.Event
                && right.Kind == VbaSourceDefinitionKind.Event;
        }

        if (left.Kind == VbaSourceDefinitionKind.EnumMember
            && right.Kind == VbaSourceDefinitionKind.EnumMember)
        {
            return left.ParentTypeName is not null
                && left.ParentTypeName.Equals(
                    right.ParentTypeName,
                    StringComparison.OrdinalIgnoreCase);
        }

        if (left.Kind == VbaSourceDefinitionKind.EnumMember
            || right.Kind == VbaSourceDefinitionKind.EnumMember)
        {
            var other = left.Kind == VbaSourceDefinitionKind.EnumMember
                ? right
                : left;
            return IsModuleValueDeclaration(other);
        }

        if (IsModuleValueDeclaration(left)
            || IsModuleValueDeclaration(right))
        {
            return IsModuleValueDeclaration(left)
                && IsModuleValueDeclaration(right);
        }

        return IsDeclaredType(left) && IsDeclaredType(right);
    }

    private static bool HaveSameLogicalMemberScope(
        VbaSourceDefinition left,
        VbaSourceDefinition right,
        IReadOnlyDictionary<VbaDefinitionIdentity, string>? logicalMemberScopes)
    {
        if (logicalMemberScopes is null
            || left.Kind != right.Kind
            || left.Kind is not (
                VbaSourceDefinitionKind.EnumMember
                or VbaSourceDefinitionKind.TypeMember)
            || !logicalMemberScopes.TryGetValue(left.Identity, out var leftScope)
            || !logicalMemberScopes.TryGetValue(right.Identity, out var rightScope))
        {
            return false;
        }

        return leftScope.Equals(rightScope, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HaveCompatiblePropertyAccessorKinds(
        VbaSourceDefinition left,
        VbaSourceDefinition right)
    {
        if (left.Kind != VbaSourceDefinitionKind.Property
            && right.Kind != VbaSourceDefinitionKind.Property)
        {
            return true;
        }

        if (left.Kind != VbaSourceDefinitionKind.Property
            || right.Kind != VbaSourceDefinitionKind.Property)
        {
            var property = left.Kind == VbaSourceDefinitionKind.Property
                ? left
                : right;
            return property.PropertyAccessorKind is not null;
        }

        return left.PropertyAccessorKind is not null
            && left.PropertyAccessorKind == right.PropertyAccessorKind;
    }

    private static bool IsProjectNamespacePeer(
        VbaSourceDefinition left,
        VbaSourceDefinition right)
    {
        var leftIsModule = IsModuleIdentity(left);
        var rightIsModule = IsModuleIdentity(right);
        var leftIsProjectVisibleType = IsProjectVisibleType(left);
        var rightIsProjectVisibleType = IsProjectVisibleType(right);
        return leftIsModule && (rightIsModule || rightIsProjectVisibleType)
            || rightIsModule && leftIsProjectVisibleType
            || leftIsProjectVisibleType && rightIsProjectVisibleType;
    }

    private static bool IsModuleIdentity(VbaSourceDefinition definition)
        => definition.Kind is VbaSourceDefinitionKind.Module
            or VbaSourceDefinitionKind.Class
            or VbaSourceDefinitionKind.Form;

    private static bool IsProjectVisibleType(VbaSourceDefinition definition)
        => definition.Visibility.IsProjectVisible()
            && IsDeclaredType(definition);

    private static bool IsDeclaredType(VbaSourceDefinition definition)
        => definition.Kind is VbaSourceDefinitionKind.Enum
            or VbaSourceDefinitionKind.Type;

    private static bool IsModuleValueDeclaration(VbaSourceDefinition definition)
        => definition.Kind is VbaSourceDefinitionKind.Procedure
            or VbaSourceDefinitionKind.Property
            or VbaSourceDefinitionKind.Constant
            or VbaSourceDefinitionKind.Variable;

    private static bool SameUri(
        VbaSourceDefinition left,
        VbaSourceDefinition right)
        => left.Uri.Equals(right.Uri, StringComparison.OrdinalIgnoreCase);

    private static string CreateRangeKey(VbaRange? range)
        => range is null
            ? string.Empty
            : $"{range.Start.Line}:{range.Start.Character}:"
                + $"{range.End.Line}:{range.End.Character}";
}

/// <summary>
/// Indexes project-aware declaration collisions for one semantic snapshot.
/// </summary>
internal sealed class VbaProjectValidationDiagnosticIndex
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<VbaProjectValidationDiagnostic>>
        diagnosticsByUri;

    public VbaProjectValidationDiagnosticIndex(
        IReadOnlyList<VbaSourceDocument> documents,
        VbaSemanticResolution semanticResolution)
    {
        var diagnostics = new Dictionary<string, List<VbaProjectValidationDiagnostic>>(
            StringComparer.OrdinalIgnoreCase);
        var definitions = documents
            .SelectMany(document => document.Definitions)
            .Where(definition => definition.Identity.Origin == VbaDefinitionOrigin.Source)
            .OrderBy(definition => definition.Uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(definition => definition.Uri, StringComparer.Ordinal)
            .ThenBy(definition => definition.Range.Start.Line)
            .ThenBy(definition => definition.Range.Start.Character)
            .ToArray();
        var declarationsWithPeers = new HashSet<VbaDefinitionIdentity>();
        for (var leftIndex = 0; leftIndex < definitions.Length; leftIndex++)
        {
            var left = definitions[leftIndex];
            for (var rightIndex = leftIndex + 1;
                rightIndex < definitions.Length;
                rightIndex++)
            {
                var right = definitions[rightIndex];
                if (!VbaDeclarationRelationshipPolicy.AreDirectCollisionPeers(
                        left,
                        right)
                    || left.ConditionalCompilationPath is not { } leftPath
                    || right.ConditionalCompilationPath is not { } rightPath
                    || !IsProvenCollision(leftPath, rightPath))
                {
                    continue;
                }

                declarationsWithPeers.Add(left.Identity);
                declarationsWithPeers.Add(right.Identity);
            }
        }

        foreach (var declaration in definitions.Where(definition =>
            declarationsWithPeers.Contains(definition.Identity)))
        {
            if (!diagnostics.TryGetValue(declaration.Uri, out var documentDiagnostics))
            {
                documentDiagnostics = [];
                diagnostics.Add(declaration.Uri, documentDiagnostics);
            }

            documentDiagnostics.Add(new VbaProjectValidationDiagnostic(
                "validation.duplicateDeclaration",
                $"Declaration '{declaration.Name}' conflicts with another "
                    + "declaration in this scope.",
                declaration.Range));
        }

        foreach (var document in documents)
        {
            var syntaxTree = document.SyntaxTree
                ?? VbaSyntaxTree.ParseModule(document.Uri, document.Text);
            foreach (var diagnostic in semanticResolution
                .GetInterfaceContractDiagnostics(document))
            {
                AddProjectDiagnostic(diagnostics, document.Uri, diagnostic);
            }

            foreach (var variable in document.Definitions.Where(definition =>
                         definition.IsWithEvents
                         && !definition.IsRecoveredWithEventsVariableDeclaration))
            {
                var eligibility = semanticResolution.GetWithEventsTypeEligibility(
                    document,
                    variable);
                var diagnostic = eligibility?.Kind switch
                {
                    VbaWithEventsTypeEligibilityKind.InvalidEnclosingClass =>
                        new VbaProjectValidationDiagnostic(
                            "validation.withEventsTypeCannotBeEnclosingClass",
                            "A WithEvents variable cannot use its enclosing class as its declared type.",
                            variable.TypeReferenceRange ?? variable.Range),
                    VbaWithEventsTypeEligibilityKind.InvalidNotClass =>
                        new VbaProjectValidationDiagnostic(
                            "validation.withEventsTypeMustBeClass",
                            "WithEvents variables must use a specific class type.",
                            variable.TypeReferenceRange ?? variable.Range),
                    VbaWithEventsTypeEligibilityKind.InvalidInaccessibleType =>
                        new VbaProjectValidationDiagnostic(
                            "validation.withEventsTypeMustBeAccessible",
                            "The declared WithEvents class must be accessible to VBA.",
                            variable.TypeReferenceRange ?? variable.Range),
                    VbaWithEventsTypeEligibilityKind.InvalidNoEvents =>
                        new VbaProjectValidationDiagnostic(
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
                var intrinsicAnalysis = semanticResolution
                    .AnalyzeIntrinsicHostHandler(document, handler);
                if (intrinsicAnalysis is not null)
                {
                    if (intrinsicAnalysis.Surface.Authority
                        == VbaHostClassEventAuthority.Current)
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
                                new VbaProjectValidationDiagnostic(
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
                                    new VbaProjectValidationDiagnostic(
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
                        new VbaProjectValidationDiagnostic(
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
                    new VbaProjectValidationDiagnostic(
                        "validation.incompatibleEventHandlerSignature",
                        "Event handler signature does not match any available Event signature.",
                        callable.ParameterListRange is { } parameterListRange
                            ? ToRange(parameterListRange)
                            : handler.Range,
                        Details: compatibility.CreateDiagnosticDetails()));
            }

            foreach (var argumentList in syntaxTree.Module.ArgumentLists)
            {
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

                if (!diagnostics.TryGetValue(document.Uri, out var documentDiagnostics))
                {
                    documentDiagnostics = [];
                    diagnostics.Add(document.Uri, documentDiagnostics);
                }

                documentDiagnostics.Add(new VbaProjectValidationDiagnostic(
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

        diagnosticsByUri = diagnostics.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<VbaProjectValidationDiagnostic>)
                Array.AsReadOnly(pair.Value.ToArray()),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<VbaProjectValidationDiagnostic> GetDiagnostics(string uri)
        => diagnosticsByUri.TryGetValue(uri, out var diagnostics)
            ? diagnostics
            : [];

    private static void AddRaiseEventTargetDiagnostic(
        IDictionary<string, List<VbaProjectValidationDiagnostic>> diagnostics,
        string uri,
        VbaRange range)
    {
        if (!diagnostics.TryGetValue(uri, out var documentDiagnostics))
        {
            documentDiagnostics = [];
            diagnostics.Add(uri, documentDiagnostics);
        }

        documentDiagnostics.Add(new VbaProjectValidationDiagnostic(
            "validation.raiseEventTargetNotDeclaredInEnclosingModule",
            "RaiseEvent target must be an Event declared in the enclosing class module.",
            range));
    }

    private static void AddProjectDiagnostic(
        IDictionary<string, List<VbaProjectValidationDiagnostic>> diagnostics,
        string uri,
        VbaProjectValidationDiagnostic diagnostic)
    {
        if (!diagnostics.TryGetValue(uri, out var documentDiagnostics))
        {
            documentDiagnostics = [];
            diagnostics.Add(uri, documentDiagnostics);
        }

        documentDiagnostics.Add(diagnostic);
    }

    private static bool IsProvenCollision(
        VbaConditionalCompilationBranchPath left,
        VbaConditionalCompilationBranchPath right)
        => left.IsEmpty
            || right.IsEmpty
            || left.IsPrefixOf(right)
            || right.IsPrefixOf(left);

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
