using System.Text.Json.Serialization;

namespace VbaDev.Domain;

/// <summary>
/// Stores project-level defaults that command invocations use when the caller omits an option.
/// </summary>
/// <param name="Test">The defaults for test command output and execution options.</param>
/// <param name="ExcelAutomation">The defaults for bounded Excel automation stages.</param>
public sealed record CommandDefaults(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TestCommandDefaults? Test = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExcelAutomationCommandDefaults? ExcelAutomation = null);

/// <summary>
/// Stores default option values for the workbook-backed test command.
/// </summary>
/// <param name="Format">The default test result output format.</param>
public sealed record TestCommandDefaults(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Format = null);

/// <summary>
/// Stores default timeout values for Excel automation stages.
/// </summary>
/// <param name="WorkbookOpenTimeoutSeconds">The workbook-open timeout in positive whole seconds.</param>
/// <param name="WorkbookSaveTimeoutSeconds">The workbook-save timeout in positive whole seconds.</param>
public sealed record ExcelAutomationCommandDefaults(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? WorkbookOpenTimeoutSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? WorkbookSaveTimeoutSeconds = null);
