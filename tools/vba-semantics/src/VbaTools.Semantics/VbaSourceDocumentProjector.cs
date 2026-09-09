using VbaTools.Syntax;

namespace VbaTools.Semantics;

/// <summary>
/// Projects parsed VBA syntax into immutable source definitions and safely
/// reuses unchanged definitions after a member-local parse.
/// </summary>
internal static class VbaSourceDocumentProjector
{
    public static VbaSourceDocument Project(string uri, VbaSyntaxTree syntaxTree)
    {
        var definitions = new List<VbaSourceDefinition>();
        var moduleDefinition = CreateModuleDefinition(uri, syntaxTree.Module);
        definitions.Add(moduleDefinition);
        definitions.AddRange(syntaxTree.Module.Declarations.Select(declaration =>
            CreateSourceDefinition(uri, moduleDefinition.Name, syntaxTree, declaration)));

        return CreateProjectedDocument(
            uri,
            syntaxTree,
            moduleDefinition.Name,
            definitions);
    }

    public static VbaSourceDocument Project(
        string uri,
        VbaSyntaxTreeChangeSet changeSet,
        VbaSourceDocument? previousDocument)
    {
        var syntaxTree = changeSet.SyntaxTree;
        if (changeSet is VbaSyntaxTreeChangeSet.Unchanged
            && IsOwnedProjection(
                uri,
                syntaxTree,
                previousDocument))
        {
            return previousDocument!;
        }

        if (changeSet is not VbaSyntaxTreeChangeSet.ModuleMember memberChange
            || !TryCreateReusableDefinitionMap(
                uri,
                syntaxTree,
                previousDocument,
                memberChange,
                out var moduleDefinition,
                out var reusableDefinitions))
        {
            return Project(uri, syntaxTree);
        }

        var definitions = new List<VbaSourceDefinition>(
            syntaxTree.Module.Declarations.Count + 1)
        {
            moduleDefinition
        };
        foreach (var declaration in syntaxTree.Module.Declarations)
        {
            definitions.Add(
                reusableDefinitions.TryGetValue(declaration, out var definition)
                    ? definition
                    : CreateSourceDefinition(
                        uri,
                        moduleDefinition.Name,
                        syntaxTree,
                        declaration));
        }

        return CreateProjectedDocument(
            uri,
            syntaxTree,
            moduleDefinition.Name,
            definitions);
    }

    private static bool IsOwnedProjection(
        string uri,
        VbaSyntaxTree syntaxTree,
        VbaSourceDocument? previousDocument)
        => previousDocument is not null
            && uri.Equals(syntaxTree.Uri, StringComparison.Ordinal)
            && uri.Equals(previousDocument.Uri, StringComparison.Ordinal)
            && ReferenceEquals(previousDocument.SyntaxTree, syntaxTree)
            && previousDocument.Text.Equals(syntaxTree.Text, StringComparison.Ordinal)
            && previousDocument.Projection is { } previousProjection
            && ReferenceEquals(previousProjection.SyntaxTree, syntaxTree)
            && ReferenceEquals(
                previousProjection.Definitions,
                previousDocument.Definitions);

    private static bool TryCreateReusableDefinitionMap(
        string uri,
        VbaSyntaxTree syntaxTree,
        VbaSourceDocument? previousDocument,
        VbaSyntaxTreeChangeSet.ModuleMember memberChange,
        out VbaSourceDefinition moduleDefinition,
        out Dictionary<VbaDeclarationSyntax, VbaSourceDefinition> reusableDefinitions)
    {
        moduleDefinition = default!;
        reusableDefinitions = default!;
        var previousSyntaxTree = previousDocument?.SyntaxTree;
        if (previousSyntaxTree is null
            || previousDocument is null
            || !uri.Equals(syntaxTree.Uri, StringComparison.Ordinal)
            || !uri.Equals(previousSyntaxTree.Uri, StringComparison.Ordinal)
            || !uri.Equals(previousDocument.Uri, StringComparison.Ordinal)
            || !ReferenceEquals(previousDocument.SyntaxTree, previousSyntaxTree)
            || !previousDocument.Text.Equals(previousSyntaxTree.Text, StringComparison.Ordinal)
            || previousSyntaxTree.Module.Kind != syntaxTree.Module.Kind
            || !previousSyntaxTree.Module.Identity.Name.Equals(
                syntaxTree.Module.Identity.Name,
                StringComparison.OrdinalIgnoreCase)
            || previousDocument.Projection is not { } previousProjection
            || !ReferenceEquals(previousProjection.SyntaxTree, previousSyntaxTree)
            || !ReferenceEquals(
                previousProjection.Definitions,
                previousDocument.Definitions)
            || previousDocument.Definitions.Count
                != previousSyntaxTree.Module.Declarations.Count + 1
            || !ContainsReference(
                previousSyntaxTree.Module.Members,
                memberChange.PreviousMember)
            || !ContainsReference(
                syntaxTree.Module.Members,
                memberChange.CurrentMember))
        {
            return false;
        }

        var candidateModuleDefinition = previousDocument.Definitions[0];
        if (!DefinitionMatchesModule(
                candidateModuleDefinition,
                uri,
                syntaxTree.Module))
        {
            return false;
        }

        var candidates = new Dictionary<VbaDeclarationSyntax, VbaSourceDefinition>(
            previousSyntaxTree.Module.Declarations.Count,
            ReferenceEqualityComparer.Instance);
        for (var index = 0;
            index < previousSyntaxTree.Module.Declarations.Count;
            index++)
        {
            var declaration = previousSyntaxTree.Module.Declarations[index];
            var definition = previousDocument.Definitions[index + 1];
            if (!DefinitionMatchesDeclaration(
                    definition,
                    uri,
                    candidateModuleDefinition.Name,
                    previousSyntaxTree,
                    declaration))
            {
                return false;
            }

            candidates.Add(declaration, definition);
        }

        moduleDefinition = candidateModuleDefinition;
        reusableDefinitions = candidates;
        return true;
    }

    private static VbaSourceDocument CreateProjectedDocument(
        string uri,
        VbaSyntaxTree syntaxTree,
        string moduleName,
        IReadOnlyList<VbaSourceDefinition> definitions)
    {
        IReadOnlyList<VbaSourceDefinition> frozenDefinitions =
            Array.AsReadOnly(definitions.ToArray());
        return new VbaSourceDocument(
            uri,
            syntaxTree.Text,
            moduleName,
            frozenDefinitions,
            syntaxTree)
        {
            Projection = new VbaSourceDocumentProjection(
                syntaxTree,
                frozenDefinitions)
        };
    }

    private static bool DefinitionMatchesModule(
        VbaSourceDefinition definition,
        string uri,
        VbaModuleSyntax module)
        => definition.Identity.Origin == VbaDefinitionOrigin.Source
            && uri.Equals(definition.Identity.SourceUri, StringComparison.Ordinal)
            && uri.Equals(definition.Uri, StringComparison.Ordinal)
            && definition.Name.Equals(module.Identity.Name, StringComparison.Ordinal)
            && definition.ModuleName.Equals(module.Identity.Name, StringComparison.Ordinal)
            && definition.Kind == MapModuleKind(module.Kind)
            && RangeMatches(definition.Range, module.Identity.Range);

    private static bool DefinitionMatchesDeclaration(
        VbaSourceDefinition definition,
        string uri,
        string moduleName,
        VbaSyntaxTree syntaxTree,
        VbaDeclarationSyntax declaration)
        => definition.Identity.Origin == VbaDefinitionOrigin.Source
            && uri.Equals(definition.Identity.SourceUri, StringComparison.Ordinal)
            && uri.Equals(definition.Uri, StringComparison.Ordinal)
            && definition.Name.Equals(declaration.Name, StringComparison.Ordinal)
            && definition.ModuleName.Equals(moduleName, StringComparison.Ordinal)
            && definition.Kind == MapDeclarationKind(declaration.Kind)
            && definition.Visibility == MapVisibility(declaration.Visibility)
            && RangeMatches(definition.Range, declaration.Range)
            && definition.CallableKind == (declaration.CallableKind is null
                ? null
                : GetCallableKind(declaration))
            && definition.EventRecoveryReasons
                == GetEventRecoveryReasons(syntaxTree, declaration)
            && definition.WithEventsRecoveryReasons
                == GetWithEventsRecoveryReasons(syntaxTree, declaration)
            && definition.IsFixedLengthString == declaration.IsFixedLengthString
            && definition.IsExternal == declaration.IsExternal
            && Equals(
                definition.TypeReferenceRange,
                declaration.WithEventsTypeReferenceRange is null
                    ? null
                    : MapRange(declaration.WithEventsTypeReferenceRange))
            && Equals(
                definition.ConditionalCompilationPath,
                GetConditionalCompilationPath(syntaxTree, declaration));

    private static bool RangeMatches(VbaRange definitionRange, VbaSyntaxRange syntaxRange)
        => definitionRange.Start.Line == syntaxRange.Start.Line
            && definitionRange.Start.Character == syntaxRange.Start.Character
            && definitionRange.End.Line == syntaxRange.End.Line
            && definitionRange.End.Character == syntaxRange.End.Character;

    private static bool ContainsReference<T>(
        IReadOnlyList<T> items,
        T candidate)
        where T : class
    {
        foreach (var item in items)
        {
            if (ReferenceEquals(item, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static VbaSourceDefinition CreateModuleDefinition(string uri, VbaModuleSyntax module)
    {
        var range = MapRange(module.Identity.Range);
        return new VbaSourceDefinition(
            VbaDefinitionIdentity.ForSource(uri, module.Identity.Name, range),
            new VbaDefinitionLocation(uri, range),
            module.Identity.Name,
            MapModuleKind(module.Kind),
            VbaSourceDefinitionVisibility.Public,
            module.Identity.Name,
            IsCreatable: module.Kind is VbaModuleKind.ClassModule or VbaModuleKind.FormModule,
            ConditionalCompilationPath: VbaConditionalCompilationBranchPath.Root);
    }

    private static VbaSourceDefinition CreateSourceDefinition(
        string uri,
        string moduleName,
        VbaSyntaxTree syntaxTree,
        VbaDeclarationSyntax declaration)
    {
        var range = MapRange(declaration.Range);
        var eventRecoveryReasons = GetEventRecoveryReasons(syntaxTree, declaration);
        var withEventsRecoveryReasons = GetWithEventsRecoveryReasons(syntaxTree, declaration);
        return new VbaSourceDefinition(
            Identity: VbaDefinitionIdentity.ForSource(uri, declaration.Name, range),
            Location: new VbaDefinitionLocation(uri, range),
            Name: declaration.Name,
            Kind: MapDeclarationKind(declaration.Kind),
            Visibility: MapVisibility(declaration.Visibility),
            ModuleName: moduleName,
            ParentProcedureName: declaration.ParentProcedureName,
            ParentProcedureRange: declaration.ParentProcedureRange is null ? null : MapRange(declaration.ParentProcedureRange),
            Documentation: declaration.Documentation,
            Signature: declaration.Signature is null
                || !HasValidSourceCallableSignature(syntaxTree, declaration)
                    ? null
                    : MapSignature(declaration),
            ParentTypeName: declaration.ParentTypeName,
            TypeReference: declaration.TypeReference is null ? null : MapTypeReference(declaration.TypeReference),
            IsWithEvents: declaration.IsWithEvents,
            DeclarationLabel: declaration.DeclarationLabel,
            PropertyAccess: MapPropertyAccess(declaration.PropertyAccessorKind),
            PropertyAccessorKind: declaration.PropertyAccessorKind,
            IsArray: declaration.IsArray,
            ConditionalCompilationPath: GetConditionalCompilationPath(
                syntaxTree,
                declaration),
            EventRecoveryReasons: eventRecoveryReasons,
            WithEventsRecoveryReasons: withEventsRecoveryReasons,
            TypeReferenceRange: declaration.WithEventsTypeReferenceRange is null
                ? null
                : MapRange(declaration.WithEventsTypeReferenceRange),
            CallableKind: declaration.CallableKind is null
                ? null
                : GetCallableKind(declaration))
        {
            IsFixedLengthString = declaration.IsFixedLengthString,
            IsExternal = declaration.IsExternal
        };
    }

    private static VbaWithEventsRecoveryReason GetWithEventsRecoveryReasons(
        VbaSyntaxTree syntaxTree,
        VbaDeclarationSyntax declaration)
    {
        if (!declaration.IsWithEvents
            || declaration.WithEventsKeywordRange is not { } withEventsRange)
        {
            return VbaWithEventsRecoveryReason.None;
        }

        var reasons = syntaxTree.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "syntax.withEventsDeclarationNotAllowedHere"
                && diagnostic.Range == withEventsRange)
            ? VbaWithEventsRecoveryReason.InvalidPlacement
            : VbaWithEventsRecoveryReason.None;
        if (declaration.WithEventsArrayDesignatorRange is not null)
        {
            reasons |= VbaWithEventsRecoveryReason.Array;
        }

        if (declaration.WithEventsNewKeywordRange is not null)
        {
            reasons |= VbaWithEventsRecoveryReason.New;
        }

        if (declaration.WithEventsTypeDeclarationCharacterRange is not null)
        {
            reasons |= VbaWithEventsRecoveryReason.TypeDeclarationCharacter;
        }

        if (declaration.WithEventsTypeRequiredRange is not null)
        {
            reasons |= VbaWithEventsRecoveryReason.TypeRequired;
        }

        if (!declaration.HasRecognizableWithEventsDeclaratorShape)
        {
            reasons |= VbaWithEventsRecoveryReason.MalformedDeclarator;
        }

        return reasons;
    }

    private static VbaEventRecoveryReason GetEventRecoveryReasons(
        VbaSyntaxTree syntaxTree,
        VbaDeclarationSyntax declaration)
    {
        if (declaration.Kind != VbaDeclarationKind.Event)
        {
            return VbaEventRecoveryReason.None;
        }

        var reasons = VbaEventRecoveryReason.None;
        if (syntaxTree.Module.Kind is not (
                VbaModuleKind.ClassModule or VbaModuleKind.FormModule)
            || declaration.ParentProcedureName is not null
            || declaration.IsInvalidEventPlacement)
        {
            reasons |= VbaEventRecoveryReason.InvalidPlacement;
        }

        if (declaration.Visibility != VbaDeclarationVisibility.Public)
        {
            reasons |= VbaEventRecoveryReason.InvalidVisibility;
        }

        if (declaration.Name.Contains("_", StringComparison.Ordinal)
            || !VbaIdentifier.IsLexIdentifier(declaration.Name))
        {
            reasons |= VbaEventRecoveryReason.InvalidName;
        }

        if (declaration.HasOptionalEventParameter)
        {
            reasons |= VbaEventRecoveryReason.OptionalParameter;
        }

        if (declaration.HasParamArrayEventParameter)
        {
            reasons |= VbaEventRecoveryReason.ParamArrayParameter;
        }

        if (declaration.Signature is null
            || !declaration.HasCompleteEventSignatureShape)
        {
            reasons |= VbaEventRecoveryReason.MissingOrInvalidSignature;
        }

        return reasons;
    }

    private static VbaConditionalCompilationBranchPath? GetConditionalCompilationPath(
        VbaSyntaxTree syntaxTree,
        VbaDeclarationSyntax declaration)
        => VbaConditionalCompilationBranchFacts.TryGetPath(
            syntaxTree,
            declaration.Range,
            requireCompleteStructure: true,
            out var path)
                ? path
                : null;

    private static VbaSourceDefinitionKind MapModuleKind(VbaModuleKind kind)
        => kind switch
        {
            VbaModuleKind.ClassModule => VbaSourceDefinitionKind.Class,
            VbaModuleKind.FormModule => VbaSourceDefinitionKind.Form,
            _ => VbaSourceDefinitionKind.Module
        };

    private static VbaSourceDefinitionKind MapDeclarationKind(VbaDeclarationKind kind)
        => kind switch
        {
            VbaDeclarationKind.Procedure => VbaSourceDefinitionKind.Procedure,
            VbaDeclarationKind.Property => VbaSourceDefinitionKind.Property,
            VbaDeclarationKind.Constant => VbaSourceDefinitionKind.Constant,
            VbaDeclarationKind.Variable => VbaSourceDefinitionKind.Variable,
            VbaDeclarationKind.Parameter => VbaSourceDefinitionKind.Parameter,
            VbaDeclarationKind.Enum => VbaSourceDefinitionKind.Enum,
            VbaDeclarationKind.EnumMember => VbaSourceDefinitionKind.EnumMember,
            VbaDeclarationKind.Type => VbaSourceDefinitionKind.Type,
            VbaDeclarationKind.TypeMember => VbaSourceDefinitionKind.TypeMember,
            VbaDeclarationKind.Event => VbaSourceDefinitionKind.Event,
            _ => VbaSourceDefinitionKind.Variable
        };

    private static VbaSourceDefinitionVisibility MapVisibility(VbaDeclarationVisibility visibility)
        => visibility switch
        {
            VbaDeclarationVisibility.Public => VbaSourceDefinitionVisibility.Public,
            VbaDeclarationVisibility.Friend => VbaSourceDefinitionVisibility.Friend,
            VbaDeclarationVisibility.Local => VbaSourceDefinitionVisibility.Local,
            _ => VbaSourceDefinitionVisibility.Private
        };

    private static VbaRange MapRange(VbaSyntaxRange range)
        => new(
            new VbaPosition(range.Start.Line, range.Start.Character),
            new VbaPosition(range.End.Line, range.End.Character));

    private static VbaCallableSignature MapSignature(VbaDeclarationSyntax declaration)
    {
        var signature = declaration.Signature!;
        var callableKind = GetCallableKind(declaration);
        return VbaCallableSignaturePresentation.Assemble(
            new VbaCallablePresentationShape(
                declaration.Name,
                callableKind,
                declaration.TypeReference is null ? null : MapTypeReference(declaration.TypeReference),
                IsExternal: declaration.IsExternal,
                IsReturnArray: declaration.IsArray),
            new VbaCallableSignature(
                "",
                signature.Parameters
                    .Select(parameter => new VbaCallableParameter(
                        Name: parameter.Name,
                        Documentation: parameter.Documentation,
                        IsOptional: parameter.IsOptional,
                        TypeReference: parameter.TypeReference is null
                            ? null
                            : MapTypeReference(parameter.TypeReference),
                        IsByRef: parameter.IsByRef,
                        IsParamArray: parameter.IsParamArray,
                        IsArray: parameter.IsArray)
                    {
                        DefaultExpression = parameter.DefaultExpression
                    })
                    .ToArray(),
                signature.Documentation,
                CallableKind: callableKind,
                SupportsNamedArguments: true));
    }

    private static bool HasValidSourceCallableSignature(
        VbaSyntaxTree syntaxTree,
        VbaDeclarationSyntax declaration)
    {
        var callableDeclaration = syntaxTree.Module.CallableDeclarations
            .SingleOrDefault(candidate =>
                candidate.LineIndex == declaration.LineIndex
                && candidate.Name.Equals(
                    declaration.Name,
                    StringComparison.OrdinalIgnoreCase)
                && candidate.IsExternal == declaration.IsExternal
                && candidate.PropertyAccessorKind == declaration.PropertyAccessorKind);
        if (callableDeclaration is not null)
        {
            var tokens = GetSignificantTokens(callableDeclaration.OriginalLine);
            return callableDeclaration.IsExternal
                ? VbaBlockHeaderSyntax.HasCompleteExternalCallableShape(
                    tokens,
                    syntaxTree.Module.Kind,
                    declaration.ParentProcedureName is null)
                : VbaBlockHeaderSyntax.TryGetCompleteCallableShape(
                    tokens,
                    syntaxTree.Module.Kind,
                    out _);
        }

        if (declaration.Kind != VbaDeclarationKind.Event)
        {
            return false;
        }

        if (syntaxTree.Module.Kind is not (
                VbaModuleKind.ClassModule or VbaModuleKind.FormModule)
            || declaration.ParentProcedureName is not null
            || declaration.IsInvalidEventPlacement
            || declaration.Visibility != VbaDeclarationVisibility.Public
            || declaration.Name.Contains("_", StringComparison.Ordinal)
            || !VbaIdentifier.IsLexIdentifier(declaration.Name)
            || declaration.Signature is null
            || declaration.Signature.Parameters.Any(parameter =>
                parameter.IsOptional || parameter.IsParamArray))
        {
            return false;
        }

        return declaration.HasCompleteEventSignatureShape;
    }

    private static IReadOnlyList<VbaToken> GetSignificantTokens(string text)
        => VbaTokenStream.FromText(text).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.Comment
                and not VbaTokenKind.LineContinuation)
            .ToArray();

    private static VbaCallableKind GetCallableKind(VbaDeclarationSyntax declaration)
        => declaration.CallableKind?.ToUpperInvariant() switch
        {
            "SUB" => VbaCallableKind.Sub,
            "FUNCTION" => VbaCallableKind.Function,
            "PROPERTY" => VbaCallableKind.Property,
            "EVENT" => VbaCallableKind.Event,
            _ => declaration.Kind switch
            {
                VbaDeclarationKind.Property => VbaCallableKind.Property,
                VbaDeclarationKind.Event => VbaCallableKind.Event,
                _ => declaration.TypeReference is null ? VbaCallableKind.Sub : VbaCallableKind.Function
            }
        };

    private static VbaPropertyAccess MapPropertyAccess(VbaPropertyAccessorKind? accessorKind)
        => accessorKind switch
        {
            VbaPropertyAccessorKind.Get => VbaPropertyAccess.Readable,
            VbaPropertyAccessorKind.Let or VbaPropertyAccessorKind.Set => VbaPropertyAccess.Writable,
            _ => VbaPropertyAccess.Unknown
        };

    private static VbaTypeReference MapTypeReference(VbaTypeReferenceSyntax typeReference)
        => new(typeReference.Name, typeReference.Qualifier);
}
