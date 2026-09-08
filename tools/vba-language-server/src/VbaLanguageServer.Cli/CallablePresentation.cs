using VbaTools.Syntax;

namespace VbaLanguageServer.SourceModel;

internal enum VbaCallablePresentationRole
{
    Declaration,
    RequiredContract,
    Handler
}

/// <summary>
/// Transient declaration evidence, separate from persisted callable metadata.
/// </summary>
internal sealed record VbaCallablePresentationShape(
    string Name,
    VbaCallableKind? Kind,
    VbaTypeReference? ReturnType = null,
    VbaPropertyAccessorKind? Accessor = null,
    bool IsExternal = false,
    bool? IsReturnArray = null,
    VbaCallablePresentationRole Role = VbaCallablePresentationRole.Declaration);

/// <summary>
/// Assembles editor presentation without interpreting labels as semantic evidence.
/// </summary>
internal static partial class VbaCallablePresentation
{
    internal static VbaCallableSignature Assemble(
        VbaCallablePresentationShape shape,
        VbaCallableSignature metadata)
    {
        var parameters = metadata.Parameters.Select(PresentParameter).ToArray();
        var kind = shape.Role == VbaCallablePresentationRole.Handler ? null
            : shape.Role == VbaCallablePresentationRole.RequiredContract
                && shape.Accessor is { } accessor ? $"Property {accessor}"
            : shape.Kind?.ToString();
        var prefix = shape.IsExternal ? "Declare " : "";
        var heading = kind is null ? shape.Name : $"{kind} {shape.Name}";
        var label = $"{prefix}{heading}({string.Join(", ", parameters.Select(parameter => parameter.Label))})";
        if (shape.ReturnType is { } returnType)
        {
            label += $" As {returnType.Name}{(shape.IsReturnArray == true ? "()" : "")}";
        }

        return metadata with { Label = label, Parameters = parameters };
    }

    internal static VbaCallableParameter PresentParameter(VbaCallableParameter parameter)
    {
        var parts = new List<string>();
        if (parameter.IsParamArray)
        {
            parts.Add("ParamArray");
        }
        else if (parameter.IsByRef == true)
        {
            parts.Add("ByRef");
        }

        parts.Add(parameter.IsArray ? $"{parameter.Name}()" : parameter.Name);
        if (parameter.TypeReference is { } type)
        {
            parts.Add($"As {type.Name}");
        }

        var label = string.Join(" ", parts);
        return parameter with { DisplayLabel = parameter.IsOptional ? $"[{label}]" : label };
    }
}
