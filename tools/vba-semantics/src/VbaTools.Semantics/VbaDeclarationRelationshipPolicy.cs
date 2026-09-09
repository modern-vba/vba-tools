using VbaTools.Syntax;

namespace VbaTools.Semantics;

internal sealed record VbaProspectiveDeclaration(
    string Uri,
    VbaSourceDefinitionKind Kind,
    VbaPropertyAccessorKind? PropertyAccessorKind,
    VbaConditionalCompilationBranchPath? ConditionalCompilationPath,
    VbaDefinitionIdentity? EditedDefinitionIdentity = null,
    VbaRange? Range = null,
    VbaCallableKind? CallableKind = null);

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
            && ShareDeclarationSpace(left, right)
            && HaveCompatiblePropertyAccessorKinds(left, right);

    public static bool ShareDeclarationSpace(
        VbaSourceDefinition left,
        VbaSourceDefinition right)
        => HaveSameDeclarationScopeAndNamespace(left, right, null)
            || HaveProcedureResultRelationship(left, right);

    private static bool HaveProcedureResultRelationship(
        VbaSourceDefinition left,
        VbaSourceDefinition right)
    {
        if (!VbaDocumentIdentityPolicy.SameDocument(left.Uri, right.Uri)
            || (left.Visibility == VbaSourceDefinitionVisibility.Local)
                == (right.Visibility == VbaSourceDefinitionVisibility.Local))
        {
            return false;
        }
        var local = left.Visibility == VbaSourceDefinitionVisibility.Local ? left : right;
        var callable = ReferenceEquals(local, left) ? right : left;
        if (local.ParentProcedureRange is not { } range
            || (callable.Range.Start.Line, callable.Range.Start.Character)
                .CompareTo((range.Start.Line, range.Start.Character)) < 0
            || (callable.Range.Start.Line, callable.Range.Start.Character)
                .CompareTo((range.End.Line, range.End.Character)) > 0)
        {
            return false;
        }
        return callable.Kind == VbaSourceDefinitionKind.Procedure
                && (callable.CallableKind ?? callable.Signature?.CallableKind) == VbaCallableKind.Function
            || local.Kind != VbaSourceDefinitionKind.Parameter
                && callable.Kind == VbaSourceDefinitionKind.Property
                && callable.PropertyAccessorKind == VbaPropertyAccessorKind.Get;
    }

    public static bool IsProspectiveDeclarationAvailable(
        VbaProspectiveDeclaration prospective,
        string candidateName,
        IEnumerable<VbaSourceDefinition> definitions)
    {
        var peers = definitions
            .Where(definition => prospective.EditedDefinitionIdentity is null
                || definition.Identity != prospective.EditedDefinitionIdentity)
            .Where(definition => definition.Name.Equals(
                candidateName,
                StringComparison.OrdinalIgnoreCase))
            .Where(definition => definition.ConditionalCompilationPath is not null)
            .Where(definition => AreProspectiveCollisionPeers(
                prospective,
                definition))
            .ToArray();
        if (peers.Length == 0)
        {
            return true;
        }

        return prospective.ConditionalCompilationPath is { IsEmpty: false }
            && peers.All(definition =>
                definition.ConditionalCompilationPath is { IsEmpty: false });
    }

    public static string CreateFamilyScope(
        IReadOnlyList<VbaSourceDefinition> variants,
        IReadOnlyDictionary<VbaDefinitionIdentity, string>? logicalMemberScopes = null)
    {
        var first = variants[0];
        var firstDocumentKey =
            VbaDocumentIdentityPolicy.GetDocumentStableKey(first.Uri);
        if (variants.All(definition =>
                definition.Visibility == VbaSourceDefinitionVisibility.Local))
        {
            return string.Join(
                KeySeparator,
                "procedure",
                firstDocumentKey,
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
                firstDocumentKey,
                first.ParentTypeName ?? string.Empty);
        }

        if (variants.All(IsDeclaredType)
            && (variants.Select(definition =>
                        VbaDocumentIdentityPolicy.GetDocumentStableKey(
                            definition.Uri))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Skip(1)
                    .Any()
                || variants.Any(IsProjectVisibleType)))
        {
            return "project-type";
        }

        return string.Join(
            KeySeparator,
            "module",
            firstDocumentKey);
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
                && VbaDocumentIdentityPolicy.SameDocument(
                    left.Uri,
                    right.Uri)
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

        if (!VbaDocumentIdentityPolicy.SameDocument(
                left.Uri,
                right.Uri))
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

    internal static bool HaveSameLogicalMemberScope(
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
        => HaveCompatiblePropertyAccessorKinds(
            left.Kind,
            left.PropertyAccessorKind,
            right.Kind,
            right.PropertyAccessorKind);

    private static bool HaveCompatiblePropertyAccessorKinds(
        VbaSourceDefinitionKind leftKind,
        VbaPropertyAccessorKind? leftAccessorKind,
        VbaSourceDefinitionKind rightKind,
        VbaPropertyAccessorKind? rightAccessorKind)
    {
        if (leftKind != VbaSourceDefinitionKind.Property
            && rightKind != VbaSourceDefinitionKind.Property)
        {
            return true;
        }

        if (leftKind != VbaSourceDefinitionKind.Property
            || rightKind != VbaSourceDefinitionKind.Property)
        {
            return leftKind == VbaSourceDefinitionKind.Property
                ? leftAccessorKind is not null
                : rightAccessorKind is not null;
        }

        return leftAccessorKind is not null
            && leftAccessorKind == rightAccessorKind;
    }

    private static bool AreProspectiveCollisionPeers(
        VbaProspectiveDeclaration prospective,
        VbaSourceDefinition definition)
    {
        // This query-only shape never enters the definition inventory or forms
        // a family. It lets completion use the same declaration relationships.
        var range = prospective.Range ?? new VbaRange(new VbaPosition(0, 0), new VbaPosition(0, 0));
        var candidate = new VbaSourceDefinition(
            VbaDefinitionIdentity.ForSource(prospective.Uri, definition.Name, range),
            new VbaDefinitionLocation(prospective.Uri, range),
            definition.Name, prospective.Kind, VbaSourceDefinitionVisibility.Private,
            ModuleName: string.Empty, PropertyAccessorKind: prospective.PropertyAccessorKind,
            CallableKind: prospective.CallableKind);
        return ShareDeclarationSpace(candidate, definition)
            && HaveCompatiblePropertyAccessorKinds(candidate, definition);
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

    private static string CreateRangeKey(VbaRange? range)
        => range is null
            ? string.Empty
            : $"{range.Start.Line}:{range.Start.Character}:"
                + $"{range.End.Line}:{range.End.Character}";
}
