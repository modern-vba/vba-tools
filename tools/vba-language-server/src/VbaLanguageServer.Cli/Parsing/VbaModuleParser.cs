using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.SourceModel;

namespace VbaLanguageServer.Parsing;

public sealed record VbaModuleSyntaxTree(
    string Uri,
    string Text,
    IReadOnlyList<string> Lines,
    VbaModuleIdentity Identity,
    IReadOnlyList<VbaModuleMember> Members,
    IReadOnlyList<VbaSourceDeclarationSyntax> Declarations,
    IReadOnlyList<VbaCallableDeclaration> CallableDeclarations,
    int CodeStartLine,
    VbaLanguageServer.Syntax.VbaSyntaxTree CoreSyntaxTree);

public sealed record VbaModuleIdentity(
    string Name,
    VbaSourceDefinitionKind Kind,
    VbaRange Range);

public sealed record VbaModuleMember(
    string Name,
    VbaSourceDefinitionKind Kind,
    VbaRange BlockRange);

public enum VbaModuleParseUpdateKind
{
    FullModule,
    ModuleMember
}

public sealed record VbaModuleParseResult(
    VbaModuleSyntaxTree SyntaxTree,
    VbaModuleParseUpdateKind UpdateKind);

public sealed record VbaCallableDeclaration(
    string Name,
    VbaSourceDefinitionKind Kind,
    VbaSourceDefinitionVisibility Visibility,
    VbaRange Range,
    VbaRange BlockRange,
    IReadOnlyList<VbaCallableParameterSyntax> Parameters,
    string? Documentation,
    VbaCallableSignature Signature,
    VbaTypeReference? TypeReference,
    int LineIndex,
    string OriginalLine);

public sealed record VbaCallableParameterSyntax(
    string Name,
    VbaRange Range,
    string? Documentation,
    VbaTypeReference? TypeReference);

public sealed record VbaSourceDeclarationSyntax(
    string Name,
    VbaSourceDefinitionKind Kind,
    VbaSourceDefinitionVisibility Visibility,
    VbaRange Range,
    int LineIndex,
    string? Documentation = null,
    VbaCallableSignature? Signature = null,
    string? ParentProcedureName = null,
    VbaRange? ParentProcedureRange = null,
    string? ParentTypeName = null,
    VbaTypeReference? TypeReference = null,
    bool IsWithEvents = false);

public static class VbaModuleParser
{
    public static VbaModuleSyntaxTree Parse(string uri, string text)
    {
        var syntaxTree = VbaLanguageServer.Syntax.VbaSyntaxTree.ParseModule(uri, text);
        return MapSyntaxTree(uri, text, syntaxTree);
    }

    private static VbaModuleSyntaxTree MapSyntaxTree(
        string uri,
        string text,
        VbaLanguageServer.Syntax.VbaSyntaxTree syntaxTree)
    {
        return new VbaModuleSyntaxTree(
            uri,
            text,
            SplitLines(text),
            new VbaModuleIdentity(
                syntaxTree.Module.Identity.Name,
                MapModuleKind(syntaxTree.Module.Kind),
                MapRange(syntaxTree.Module.Identity.Range)),
            syntaxTree.Module.Members.Select(MapMember).ToArray(),
            syntaxTree.Module.Declarations.Select(MapDeclaration).ToArray(),
            syntaxTree.Module.CallableDeclarations.Select(MapCallableDeclaration).ToArray(),
            syntaxTree.Module.CodeStartLine,
            syntaxTree);
    }

    private static VbaModuleMember MapMember(VbaLanguageServer.Syntax.VbaModuleMemberSyntax member)
        => new(
            member.Name,
            MapDeclarationKind(member.Kind),
            MapRange(member.BlockRange));

    private static VbaSourceDeclarationSyntax MapDeclaration(VbaLanguageServer.Syntax.VbaDeclarationSyntax declaration)
        => new(
            declaration.Name,
            MapDeclarationKind(declaration.Kind),
            MapVisibility(declaration.Visibility),
            MapRange(declaration.Range),
            declaration.LineIndex,
            Documentation: declaration.Documentation,
            Signature: declaration.Signature is null ? null : MapSignature(declaration.Signature),
            ParentProcedureName: declaration.ParentProcedureName,
            ParentProcedureRange: declaration.ParentProcedureRange is null ? null : MapRange(declaration.ParentProcedureRange),
            ParentTypeName: declaration.ParentTypeName,
            TypeReference: declaration.TypeReference is null ? null : MapTypeReference(declaration.TypeReference),
            IsWithEvents: declaration.IsWithEvents);

    private static VbaCallableDeclaration MapCallableDeclaration(VbaLanguageServer.Syntax.VbaCallableDeclarationSyntax declaration)
        => new(
            declaration.Name,
            MapDeclarationKind(declaration.Kind),
            MapVisibility(declaration.Visibility),
            MapRange(declaration.Range),
            MapRange(declaration.BlockRange),
            declaration.Parameters.Select(parameter => new VbaCallableParameterSyntax(
                parameter.Name,
                MapRange(parameter.Range),
                parameter.Documentation,
                parameter.TypeReference is null ? null : MapTypeReference(parameter.TypeReference))).ToArray(),
            declaration.Documentation,
            MapSignature(declaration.Signature),
            declaration.TypeReference is null ? null : MapTypeReference(declaration.TypeReference),
            declaration.LineIndex,
            declaration.OriginalLine);

    private static VbaSourceDefinitionKind MapModuleKind(VbaLanguageServer.Syntax.VbaModuleKind kind)
        => kind switch
        {
            VbaLanguageServer.Syntax.VbaModuleKind.ClassModule => VbaSourceDefinitionKind.Class,
            VbaLanguageServer.Syntax.VbaModuleKind.FormModule => VbaSourceDefinitionKind.Form,
            _ => VbaSourceDefinitionKind.Module
        };

    private static VbaSourceDefinitionKind MapDeclarationKind(VbaLanguageServer.Syntax.VbaDeclarationKind kind)
        => kind switch
        {
            VbaLanguageServer.Syntax.VbaDeclarationKind.Procedure => VbaSourceDefinitionKind.Procedure,
            VbaLanguageServer.Syntax.VbaDeclarationKind.Property => VbaSourceDefinitionKind.Property,
            VbaLanguageServer.Syntax.VbaDeclarationKind.Constant => VbaSourceDefinitionKind.Constant,
            VbaLanguageServer.Syntax.VbaDeclarationKind.Variable => VbaSourceDefinitionKind.Variable,
            VbaLanguageServer.Syntax.VbaDeclarationKind.Parameter => VbaSourceDefinitionKind.Parameter,
            VbaLanguageServer.Syntax.VbaDeclarationKind.Enum => VbaSourceDefinitionKind.Enum,
            VbaLanguageServer.Syntax.VbaDeclarationKind.EnumMember => VbaSourceDefinitionKind.EnumMember,
            VbaLanguageServer.Syntax.VbaDeclarationKind.Type => VbaSourceDefinitionKind.Type,
            VbaLanguageServer.Syntax.VbaDeclarationKind.TypeMember => VbaSourceDefinitionKind.TypeMember,
            VbaLanguageServer.Syntax.VbaDeclarationKind.Event => VbaSourceDefinitionKind.Event,
            _ => VbaSourceDefinitionKind.Variable
        };

    private static VbaSourceDefinitionVisibility MapVisibility(VbaLanguageServer.Syntax.VbaDeclarationVisibility visibility)
        => visibility switch
        {
            VbaLanguageServer.Syntax.VbaDeclarationVisibility.Public => VbaSourceDefinitionVisibility.Public,
            VbaLanguageServer.Syntax.VbaDeclarationVisibility.Local => VbaSourceDefinitionVisibility.Local,
            _ => VbaSourceDefinitionVisibility.Private
        };

    private static VbaRange MapRange(VbaLanguageServer.Syntax.VbaSyntaxRange range)
        => new(
            new VbaPosition(range.Start.Line, range.Start.Character),
            new VbaPosition(range.End.Line, range.End.Character));

    private static VbaCallableSignature MapSignature(VbaLanguageServer.Syntax.VbaCallableSignatureSyntax signature)
        => new(
            signature.Label,
            signature.Parameters.Select(parameter => new VbaCallableParameter(parameter.Name, parameter.Documentation)).ToArray(),
            signature.Documentation);

    private static VbaTypeReference MapTypeReference(VbaLanguageServer.Syntax.VbaTypeReferenceSyntax typeReference)
        => new(typeReference.Name, typeReference.Qualifier);

    public static VbaModuleParseResult ParseOrUpdate(
        string uri,
        string text,
        VbaModuleSyntaxTree? previousSyntaxTree)
    {
        var parseResult = VbaLanguageServer.Syntax.VbaSyntaxTree.ParseOrUpdate(
            uri,
            text,
            previousSyntaxTree?.CoreSyntaxTree);
        var syntaxTree = MapSyntaxTree(uri, text, parseResult.SyntaxTree);
        var updateKind = parseResult.UpdateKind == VbaLanguageServer.Syntax.VbaSyntaxTreeParseUpdateKind.ModuleMember
            ? VbaModuleParseUpdateKind.ModuleMember
            : VbaModuleParseUpdateKind.FullModule;
        return new VbaModuleParseResult(syntaxTree, updateKind);
    }

    private static string[] SplitLines(string source)
        => source.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
}
