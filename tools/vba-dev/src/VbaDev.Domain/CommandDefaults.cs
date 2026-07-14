namespace VbaDev.Domain;

/// <summary>
/// Stores project-level defaults that command invocations use when the caller omits an option.
/// </summary>
/// <param name="Test">The defaults for test command output and execution options.</param>
public sealed record CommandDefaults(TestCommandDefaults? Test = null);

/// <summary>
/// Stores default option values for the workbook-backed test command.
/// </summary>
/// <param name="Format">The default test result output format.</param>
public sealed record TestCommandDefaults(string? Format = null);
