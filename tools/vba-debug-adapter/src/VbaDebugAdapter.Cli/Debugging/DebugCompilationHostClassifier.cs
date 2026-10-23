namespace VbaDebugAdapter.Debugging;

// Classification has no acquisition, workbook, or saved-package authority.
internal static class DebugCompilationHostClassifier
{
    internal static DebugCompilationHostFacts Classify(
        string excelVersion,
        string vbeVersion,
        string operatingSystem,
        DebugExcelProcessArchitecture processArchitecture)
    {
        var excelGeneration = ResolveExcelGeneration(excelVersion);
        var vbeGeneration = ResolveVbeGeneration(vbeVersion);
        var operatingSystemBitness = ResolveOperatingSystemBitness(operatingSystem);
        var processBitness = ResolveProcessBitness(processArchitecture);

        if (operatingSystemBitness == HostBitness.Bit32
            && processBitness == HostBitness.Bit64)
        {
            return UnprovedHostFacts(
                DebugCompilationHostFactsStatus.Mismatch,
                "Excel Application.OperatingSystem bitness contradicts the exact Excel process architecture.");
        }

        if (excelGeneration != VbaGeneration.Unknown
            && vbeGeneration != VbaGeneration.Unknown
            && excelGeneration != vbeGeneration)
        {
            return UnprovedHostFacts(
                DebugCompilationHostFactsStatus.Mismatch,
                "Excel Application.Version and VBE.Version identify contradictory VBA generations.");
        }

        if (processBitness == HostBitness.Unknown)
        {
            return UnprovedHostFacts(
                DebugCompilationHostFactsStatus.Unknown,
                "The exact Excel process architecture is unknown.");
        }

        if (operatingSystemBitness == HostBitness.Unknown)
        {
            return UnprovedHostFacts(
                DebugCompilationHostFactsStatus.Unknown,
                "Excel Application.OperatingSystem does not identify a supported Windows bitness.");
        }

        if (excelGeneration == VbaGeneration.Unknown
            || vbeGeneration == VbaGeneration.Unknown)
        {
            return UnprovedHostFacts(
                DebugCompilationHostFactsStatus.Unknown,
                "Excel Application.Version or VBE.Version does not identify a supported VBA generation.");
        }

        return new DebugCompilationHostFacts(
            excelVersion,
            vbeVersion,
            operatingSystem,
            processArchitecture,
            DebugCompilationHostFactsStatus.Verified,
            new DebugCompilerBuiltInConstants(
                Vba6: true,
                Vba7: vbeGeneration == VbaGeneration.Vba7,
                Win16: false,
                Win32: true,
                Win64: processBitness == HostBitness.Bit64,
                Mac: false),
            UnavailableReason: null);

        DebugCompilationHostFacts UnprovedHostFacts(
            DebugCompilationHostFactsStatus status,
            string reason)
            => new(
                excelVersion,
                vbeVersion,
                operatingSystem,
                processArchitecture,
                status,
                BuiltInConstants: null,
                reason);
    }

    private static VbaGeneration ResolveExcelGeneration(string version)
    {
        if (!Version.TryParse(version, out var parsedVersion))
        {
            return VbaGeneration.Unknown;
        }

        return parsedVersion.Major switch
        {
            >= 9 and <= 12 => VbaGeneration.Vba6,
            >= 14 => VbaGeneration.Vba7,
            _ => VbaGeneration.Unknown
        };
    }

    private static VbaGeneration ResolveVbeGeneration(string version)
    {
        if (!Version.TryParse(version, out var parsedVersion))
        {
            return VbaGeneration.Unknown;
        }

        return parsedVersion.Major switch
        {
            6 => VbaGeneration.Vba6,
            7 => VbaGeneration.Vba7,
            _ => VbaGeneration.Unknown
        };
    }

    private static HostBitness ResolveOperatingSystemBitness(string operatingSystem)
    {
        if (operatingSystem.StartsWith(
                "Windows (32-bit)",
                StringComparison.OrdinalIgnoreCase))
        {
            return HostBitness.Bit32;
        }

        return operatingSystem.StartsWith(
            "Windows (64-bit)",
            StringComparison.OrdinalIgnoreCase)
            ? HostBitness.Bit64
            : HostBitness.Unknown;
    }

    private static HostBitness ResolveProcessBitness(
        DebugExcelProcessArchitecture architecture)
        => architecture switch
        {
            DebugExcelProcessArchitecture.X86 => HostBitness.Bit32,
            DebugExcelProcessArchitecture.X64 or
                DebugExcelProcessArchitecture.Arm64 => HostBitness.Bit64,
            _ => HostBitness.Unknown
        };

    private enum VbaGeneration
    {
        Unknown,
        Vba6,
        Vba7
    }

    private enum HostBitness
    {
        Unknown,
        Bit32,
        Bit64
    }

}
