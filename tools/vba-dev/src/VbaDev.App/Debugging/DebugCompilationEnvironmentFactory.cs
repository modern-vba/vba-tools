using VbaLanguageServer.Syntax;

namespace VbaDev.App.Debugging;

/// <summary>
/// Combines generated-workbook settings and actual Excel host facts into one syntax environment.
/// </summary>
public sealed class DebugCompilationEnvironmentFactory
{
    private static readonly string[] BuiltInNames =
        ["VBA6", "VBA7", "Win16", "Win32", "Win64", "Mac"];

    /// <summary>
    /// Creates an evaluation environment only when the workbook and host facts agree exactly.
    /// </summary>
    public VbaConditionalCompilationEnvironment Create(
        DebugCompilationSettings settings,
        DebugCompilationHostFacts hostFacts)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(hostFacts);

        if (hostFacts.Status != DebugCompilationHostFactsStatus.Verified
            || hostFacts.BuiltInConstants is null)
        {
            throw new DebugSetupException(
                "The actual Excel/VBE compiler context could not be proved: " +
                (hostFacts.UnavailableReason ?? "required host facts are unavailable."));
        }

        var expectedSystemKind = hostFacts.ExcelProcessArchitecture switch
        {
            DebugExcelProcessArchitecture.X86 => VbaProjectSystemKind.Win32,
            DebugExcelProcessArchitecture.X64 or DebugExcelProcessArchitecture.Arm64 =>
                VbaProjectSystemKind.Win64,
            _ => throw new DebugSetupException(
                "The actual Excel process architecture is unavailable for conditional compilation.")
        };
        if (settings.SystemKind != expectedSystemKind)
        {
            throw new DebugSetupException(
                $"The generated workbook VBA project system kind '{settings.SystemKind}' does not match " +
                $"the exact Excel process architecture '{hostFacts.ExcelProcessArchitecture}'.");
        }

        var builtIns = hostFacts.BuiltInConstants;
        var is64Bit = expectedSystemKind == VbaProjectSystemKind.Win64;
        if (!builtIns.Vba6
            || builtIns.Win16
            || !builtIns.Win32
            || builtIns.Win64 != is64Bit
            || builtIns.Mac)
        {
            throw new DebugSetupException(
                "The actual Excel/VBE compiler built-ins contradict the verified Windows process context.");
        }

        var builtInSet = BuiltInNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var constants = new Dictionary<string, VbaConditionalCompilationValue>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["VBA6"] = VbaConditionalCompilationValue.FromBoolean(builtIns.Vba6),
            ["VBA7"] = VbaConditionalCompilationValue.FromBoolean(builtIns.Vba7),
            ["Win16"] = VbaConditionalCompilationValue.FromBoolean(builtIns.Win16),
            ["Win32"] = VbaConditionalCompilationValue.FromBoolean(builtIns.Win32),
            ["Win64"] = VbaConditionalCompilationValue.FromBoolean(builtIns.Win64),
            ["Mac"] = VbaConditionalCompilationValue.FromBoolean(builtIns.Mac)
        };
        foreach (var projectConstant in settings.ProjectConstants)
        {
            if (builtInSet.Contains(projectConstant.Key))
            {
                throw new DebugSetupException(
                    $"Generated workbook project constant '{projectConstant.Key}' conflicts with a " +
                    "VBA compiler built-in.");
            }

            constants.Add(
                projectConstant.Key,
                VbaConditionalCompilationValue.FromInteger(projectConstant.Value));
        }

        return new VbaConditionalCompilationEnvironment(
            constants,
            BuiltInNames,
            supportsLongLong: builtIns.Vba7 && builtIns.Win64);
    }
}
