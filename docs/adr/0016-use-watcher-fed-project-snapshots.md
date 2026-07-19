# Use watcher-fed project snapshots

Warm language-server queries should capture immutable project-scope snapshots that are fed by accepted document revisions, manifest/reference revisions, and watched-file events. For manifest-backed projects, the snapshot identity is the canonical project root, manifest path, manifest document name and kind, and active reference selection. The active document URI is not part of that identity, so different open documents in the same manifest document can share one committed snapshot.

Open buffers remain authoritative over equivalent disk sources. A watched reload, delete, rename, or close transitions the affected source identity and invalidates only project snapshots whose boundary contains that source or whose committed source set already includes it. Unknown source relationships fail closed by rebuilding the affected project scope, not the entire workspace.

Warm snapshot reuse does not stat known source files or reread disk. Raw disk writes that do not arrive through a watcher may therefore remain stale until a later reconciliation or explicit watched reload admits the change. This is an intentional watcher-first freshness model and preserves interactive latency. The full-text LSP synchronization contract remains unchanged.
