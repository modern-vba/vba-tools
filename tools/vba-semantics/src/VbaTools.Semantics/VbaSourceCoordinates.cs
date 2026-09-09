namespace VbaTools.Semantics;

/// <summary>
/// Represents a zero-based LSP-compatible document position.
/// </summary>
/// <param name="Line">The zero-based line.</param>
/// <param name="Character">The zero-based character.</param>
public sealed record VbaPosition(int Line, int Character);

/// <summary>
/// Represents a half-open LSP-compatible source range.
/// </summary>
/// <param name="Start">The inclusive start position.</param>
/// <param name="End">The exclusive end position.</param>
public sealed record VbaRange(VbaPosition Start, VbaPosition End);

public sealed record VbaDiagnosticLocation(string Uri, VbaRange Range);

public sealed record VbaDiagnosticDetail(
    VbaDiagnosticLocation? Location,
    string RelatedMessage,
    string FallbackText);
