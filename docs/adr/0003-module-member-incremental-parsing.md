# Module member incremental parsing

The language server will support incremental AST updates at the `ModuleMember` range level, with full module rebuilds reserved for initial indexing, file changes outside known member ranges, and parser recovery. This balances interactive latency with implementation complexity: full rebuilds on every edit are too coarse for responsive editor features, while token-level or expression-level incremental parsing would be disproportionately complex for the first VBA AST implementation.
