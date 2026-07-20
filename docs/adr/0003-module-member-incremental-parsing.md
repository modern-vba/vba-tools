# Module member incremental parsing

The language server will support incremental AST updates at the `ModuleMember` range level, with full module rebuilds reserved for initial indexing, file changes outside known member ranges, and parser recovery. This balances interactive latency with implementation complexity: full rebuilds on every edit are too coarse for responsive editor features, while token-level or expression-level incremental parsing would be disproportionately complex for the first VBA AST implementation.

Safe callable-body edits are parsed from a direct source window that starts at the member's attached Doxygen documentation comment, when present, and ends at the member terminator. The parser indexes that window with local coordinates, then the incremental parser projects tokens, declarations, callable metadata, statements, expressions, argument lists, block facts, line labels, preprocessor facts, and diagnostics back onto the owning document coordinates before merging them with the unchanged prefix and suffix syntax.

The source window carries the owning module facts from the previous exact document analysis: module kind, module identity, module attributes, options, and code-start line. The window parser does not synthesize a masked full document, so parsing cost is proportional to the changed member window rather than to the document length.

The reusable parser Interface accepts the complete URI and source text plus an
optional previous `VbaSyntaxTree`, then returns a `SyntaxChangeSet`. Its closed
variants expose only consumer-safe proofs: exact whole-tree reuse, one replaced
`ModuleMember`, or required module recomputation. The parser route, changed
line ranges, fallback reason, source-window dimensions, and segment counters
remain internal observations for friend tests. The public proof therefore does
not promise that a `ModuleMember` result came from the direct source-window
implementation.

The incremental path must fail closed to a full module parse when the previous tree was not produced by the parser or has had a core property replaced, has recovery diagnostics, the module kind is unsupported, the URI differs, the change is not localized to one previous member, the edit touches a member header, a continued header line, or terminator, the computed window is invalid, a conditional-compilation block crosses the member boundary, the source-window parse reports diagnostics, or the projected member shape no longer matches the previous member. This ADR intentionally keeps the scope at `ModuleMember`; expression-level, statement-level, and token-level incremental parsing are out of scope.

After the direct source-window parser is established, unchanged syntax is represented internally as segmented `IReadOnlyList<T>` instances. A changed member contributes a replacement segment, while unchanged prefix and suffix members retain their existing collection storage. When a member edit shifts later document coordinates, the suffix segment carries a private projection function instead of eagerly cloning every suffix token, declaration, statement, expression, argument list, block, label, or preprocessor range. Public parser and Language Server callers still observe ordinary absolute line and UTF-16 offset coordinates through the existing list interfaces and `SyntaxChangeSet`.

The direct source-window parser remains an independently usable rollback point. If segmented syntax cannot safely represent an update, the implementation may conservatively use the Stage A flat merge or a full module parse. Segment counters and benchmarks are implementation diagnostics only; they do not change the full-text LSP synchronization contract.
