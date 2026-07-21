using System.Collections.Immutable;

namespace VbaDev.App.Debugging;

/// <summary>
/// Identifies one unsupported breakpoint feature discovered for the active launch.
/// </summary>
public enum UnsupportedDebugBreakpointKind
{
    Conditional,
    HitCondition,
    Logpoint,
    Column,
    Mode,
    Function,
    Exception,
    Data
}

/// <summary>
/// Describes one unsupported breakpoint that participates in the active launch policy.
/// </summary>
/// <param name="Kind">The unsupported breakpoint category or feature.</param>
/// <param name="Description">An actionable description of the requested breakpoint.</param>
public sealed record UnsupportedDebugBreakpoint(
    UnsupportedDebugBreakpointKind Kind,
    string Description);

/// <summary>
/// Contains the breakpoint participation decision frozen before target execution.
/// </summary>
/// <param name="Participating">Enabled ordinary source breakpoints admitted to transfer.</param>
/// <param name="Unsupported">Unsupported breakpoints that invalidate the launch.</param>
public sealed record DebugBreakpointPlan(
    ImmutableArray<DebugSourceBreakpoint> Participating,
    ImmutableArray<UnsupportedDebugBreakpoint> Unsupported);
