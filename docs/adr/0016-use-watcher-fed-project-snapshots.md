# Use watcher-fed project snapshots

Warm language-server queries should capture immutable project-scope snapshots that are fed by accepted document revisions, manifest/reference revisions, and watched-file events. For manifest-backed projects, the snapshot identity is the canonical project root, manifest path, manifest document name and kind, and active reference selection. The active document URI is not part of that identity, so different open documents in the same manifest document can share one committed snapshot.

Open buffers remain authoritative over equivalent disk sources. A watched reload, delete, rename, or close transitions the affected source identity and invalidates only project snapshots whose boundary contains that source or whose committed source set already includes it. Unknown source relationships fail closed by rebuilding the affected project scope, not the entire workspace.

Warm snapshot reuse does not stat known source files or reread disk. Raw disk writes that do not arrive through a watcher may therefore remain stale until a later reconciliation or explicit watched reload admits the change. This is an intentional watcher-first freshness model and preserves interactive latency. The full-text LSP synchronization contract remains unchanged.

An accepted extension-owned `HostClassProjectionSnapshot` is another immutable
project-snapshot input. Its exact manifest-document context and document-local
revision are retained outside source text. Replacing or clearing it invalidates
only the matching project-document cache and project-aware diagnostics; an
unrelated project retains its warm snapshot. Interactive capture reads the
latest committed value and performs no synchronous Host Event inspection.

Cold snapshot materialization, watched source reloads, and background reconciliation use one shared `VbaProjectDiskInventory` instance. Its cold capture may reuse decoded text when stable file metadata and the source invalidation generation are unchanged. Its reconciliation observation always performs a stable byte read, even when length and last-write time match the previous observation, so a missed watcher event with unchanged metadata can still converge.

Closed disk source uses one `DiskSourceDecoding` contract. A recognized UTF-8
or UTF-16 BOM selects its strict decoder; BOM-less input tries strict UTF-8
first and then, on Windows only, the active ANSI code page captured once at
language-server process start. ACP 65001 is canonical UTF-8. A non-Windows
process does not assume CP932 or another legacy code page, and invalid input is
reported rather than decoded with replacement characters. Open LSP documents
are already Unicode and remain authoritative. The resulting Unicode text is
eligible for every `VbaIdentifierForm`; its disk encoding never selects or
limits identifier syntax.

`VbaProjectReconciler` depends on the inventory through a one-method reconciliation observation Seam. That Seam receives an immutable disk-only request containing the resolved project disk scope, ordered manifest probes, barrier overrides, and observed barrier URIs. Authority keys, authority generations, workspace, source, and manifest revisions, known-source baselines, and open-document state remain in the reconciler and workspace reconciliation scope; the inventory neither receives them nor decides whether an observation may commit. Reconciliation tests replace only this narrow observation Adapter, while production retains the shared filesystem inventory instance and cache.

A watched source reload uses the inventory's single-source capture rather than a cold project capture. That operation validates nested-manifest ownership, invalidates the prior decoded fact, and performs one stable source read without enumerating the project.

`VbaProjectDiskInventory` returns syntax-free source facts with an opaque `DiskContentIdentity`. Equal decoded text retains equal identity and changed decoded text changes identity. Parsing and source projection remain in `VbaProjectSourceDocumentCache`, which consumes those facts and performs no filesystem reads or decoding. Open buffers remain authoritative after disk capture. A warm committed project-snapshot hit does not call the disk inventory.

Closing an open source ends its buffer authority. If the URI remains in the
resolved project scope and its disk source still exists, the workspace captures
that disk source and makes it authoritative before rebuilding project-aware
diagnostics. Close alone does not remove diagnostics. Delete, project
membership departure, or loss of a tracked disk source clears the URI, and a
later reopen establishes a new open-buffer lifecycle.

`VbaProjectReconciler` is the deep Module that owns reconciliation cadence, parallel scans, authority-plan construction, ordered commit, follow-up policy, and effect dispatch. Its runtime Interface is `ReconcileAsync`; compatibility aliases for the former coordinator and trigger names are not retained.

Each scan becomes one `VbaProjectReconciliationScopePlan` carrying a captured manifest-barrier revision, authority generation, and ordered mutations. Plans commit in stable authority order, with exactly one required scheduler mutation per scope. The workspace reconciliation Seam is limited to `CaptureProjectReconciliation` and `TryCommitProjectReconciliationScope`. A stale fence rejects only its scope, while fresh peer scopes continue to commit. A manifest transition and snapshot-authority transfer occur in the same scheduler mutation; if that transition invalidates the captured authority, remaining source mutations wait for an immediate follow-up pass. Cancellation may release the capture during an in-flight scan, but after scanning the capture and its revision-watermark lease remain alive through ordered commits.

Commit results are ephemeral data. Each accepted scope dispatches its diagnostics, manifest lifecycle notifications, and project-authority-transfer notifications in stable order synchronously inside that scope's required scheduler mutation, after the workspace commit and before the ordered lane is released. An effect failure is reported to the reconciliation failure observer but does not roll back accepted authority state or block later effects and peer scopes. No reconciliation ledger or outbox is retained.
