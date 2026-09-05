# Use watcher-fed project snapshots

ADR 0036 supersedes only the document-scoped `HostClassProjectionSnapshot`
input and its template watcher below. One environment-scoped current UserForm
Event catalog is delivered as a full-catalog snapshot without project-document
identity or template watching. The watcher-fed source, manifest, reference,
CommonModules, invalidation, and reconciliation decisions remain accepted.

Warm language-server queries should capture immutable project-scope snapshots that are fed by accepted document revisions, manifest/reference revisions, and watched-file events. `VbaProjectSnapshotIdentity` is an opaque typed value composed from `VbaProjectAuthorityIdentity`, the canonical source root, selected document kind, ordered semantic reference selection, source-template selection, and CommonModules module-file membership. The active document URI and decoded source content are not part of that identity, so different open documents in the same manifest document can share one committed snapshot and a source edit can rebuild that scope without changing its identity.

Snapshot cache lookup, batch deduplication, supersession, invalidation, retirement, and reconciliation resolution comparison keep `VbaProjectSnapshotIdentity` typed end to end. `DiskContentIdentity` remains a separate equality identity for decoded exported-source text and never substitutes for document, authority, or snapshot identity.

Reference-catalog work does not reuse the full snapshot identity. Its cache and persistence scope combines project authority with the effective `ReferenceSelectionFingerprint`; refresh mutation authority combines optional project authority with one reference name and excludes the selection; automatic work combines the selection with project authority only for context-specific discovery. Scoped persistent-store implementations receive the typed scope and can derive an opaque versioned key through `CreatePersistentKey`.

Open buffers remain authoritative over equivalent disk sources. A watched reload, delete, rename, or close transitions the state associated with the affected source identity and invalidates only project snapshots whose boundary contains that source or whose committed source set already includes it. Unknown source relationships fail closed by rebuilding the affected project scope, not the entire workspace.

Warm snapshot reuse does not stat known source files or reread disk. Raw disk writes that do not arrive through a watcher may therefore remain stale until a later reconciliation or explicit watched reload admits the change. This is an intentional watcher-first freshness model and preserves interactive latency. The full-text LSP synchronization contract remains unchanged.

An accepted extension-owned `HostClassProjectionSnapshot` is another immutable
project-snapshot input. Its exact manifest-document context and document-local
revision are retained outside source text. Replacing or clearing it invalidates
only the matching project-document cache and project-aware diagnostics; an
unrelated project retains its warm snapshot. Interactive capture reads the
latest committed value and performs no synchronous Host Event inspection.

Cold snapshot materialization, watched source reloads, and background reconciliation use one shared `VbaProjectDiskInventory` instance. Its cold capture may reuse decoded text when stable file metadata and the source invalidation generation are unchanged. Its reconciliation observation always performs a stable byte read, even when length and last-write time match the previous observation, so a missed watcher event with unchanged metadata can still converge.

Closed disk source uses one `DiskSourceDecoding` contract. ADR 0037 and issue
#341 supersede the former UTF-8-first rule: supported UTF-8, UTF-16 LE, and
UTF-16 BE BOMs select strict Unicode decoding, while Windows BOM-less source
uses only the active ANSI code page obtained directly from `GetACP` once at
language-server process start. ACP 65001 is canonical UTF-8. Strict decoding
must reproduce the exact original bytes; there is no probing or fallback.
A non-Windows process rejects all BOM-less closed source, including ASCII and
empty input, because it has no Windows ACP authority. Open LSP documents are
already Unicode and remain authoritative. The resulting Unicode text is
eligible for every `VbaIdentifierForm`; its disk encoding never selects or
limits identifier syntax.

`VbaProjectReconciler` depends on the inventory through a one-method reconciliation observation Seam. That Seam receives an immutable disk-only request containing the resolved project disk scope, ordered typed manifest probes, typed barrier overrides, typed observed-barrier document identities, and typed open-source exclusion identities whose bytes must not be read or decoded. Open text, document versions, authority keys, authority generations, workspace and manifest revisions, and known-source baselines remain in the reconciler and workspace reconciliation scope; the inventory neither receives them nor decides whether an observation may commit. Reconciliation tests replace only this narrow observation Adapter, while production retains the shared filesystem inventory instance and cache.

A decoding failure is a syntax-free source fact, not source text. Cold capture, watched reload, and reconciliation exclude that file from parsing and semantic inventory, publish `invalid-disk-source-encoding` at its URI, and clear the diagnostic only after valid decoded text or deletion is accepted. No empty, replacement-character, best-fit, or last-known-good text is substituted into language features. Open Unicode text bypasses byte decoding while its existing path still participates in project ownership and reconciliation baselines.

`DiskSourceDecoding` is not the VBE import encoding contract. `vba-dev` separately owns operation-fixed ACP conversion and lossless verification for `VBComponents.Import`. Likewise, `VbaIdentifier` remains the MS-VBAL lexical authority after bytes become Unicode; neither the disk encoding nor the active ACP selects a `VbaIdentifierForm`.

A watched source reload uses the inventory's single-source capture rather than a cold project capture. That operation validates nested-manifest ownership, invalidates the prior decoded fact, and performs one stable source read without enumerating the project.

`VbaProjectDiskInventory` returns syntax-free source facts with an opaque `DiskContentIdentity`. Equal decoded text retains equal identity and changed decoded text changes identity. Parsing and source projection remain in `VbaProjectSourceDocumentCache`, which consumes those facts and performs no filesystem reads or decoding. Open buffers remain authoritative after disk capture. A warm committed project-snapshot hit does not call the disk inventory.

Snapshot cache and reconciliation Interfaces accept structural project, authority, and document identities. Active, tracked, open, revision, and manifest-barrier documents are projected once to `VbaDocumentIdentity` or `VbaIdentifiedDocument`; presentation URIs remain adjacent data used only where protocol reporting or filesystem I/O requires them. No delimiter-composed URI or path key crosses those boundaries.

Closing an open source ends its buffer authority. If the URI remains in the
resolved project scope and its disk source still exists, the workspace captures
that disk source and makes it authoritative before rebuilding project-aware
diagnostics. Close invalidates any prior disk-decoding failure hidden by the
buffer along with cached disk text. The fresh capture must publish an unchanged
failure again or accept repaired disk text; a hidden prior failure cannot
suppress that new lifecycle's diagnostic. Close alone does not remove
diagnostics. Delete, project
membership departure, or loss of a tracked disk source clears the URI, and a
later reopen establishes a new open-buffer lifecycle.

`VbaProjectReconciler` is the deep Module that owns reconciliation cadence, parallel scans, authority-plan construction, ordered commit, follow-up policy, and effect dispatch. Its runtime Interface is `ReconcileAsync`; compatibility aliases for the former coordinator and trigger names are not retained.

Each scan becomes one `VbaProjectReconciliationScopePlan` carrying a captured manifest-barrier revision, authority generation, and ordered mutations. Plans commit in stable authority order, with exactly one required scheduler mutation per scope. The workspace reconciliation Seam is limited to `CaptureProjectReconciliation` and `TryCommitProjectReconciliationScope`. A stale fence rejects only its scope, while fresh peer scopes continue to commit. A manifest transition and snapshot-authority transfer occur in the same scheduler mutation; if that transition invalidates the captured authority, remaining source mutations wait for an immediate follow-up pass. Cancellation may release the capture during an in-flight scan, but after scanning the capture and its revision-watermark lease remain alive through ordered commits.

Rejected-scope retry progress uses the structural `VbaProjectReconciliationRejectedProgressIdentity`: project authority, rejection and mutation kinds, captured revision fences, and ordered typed document/revision facts. Reconciliation does not construct a delimiter-based path or URI fingerprint for that boundary.

Commit results are ephemeral data. Each accepted scope dispatches its diagnostics, manifest lifecycle notifications, and project-authority-transfer notifications in stable order synchronously inside that scope's required scheduler mutation, after the workspace commit and before the ordered lane is released. An effect failure is reported to the reconciliation failure observer but does not roll back accepted authority state or block later effects and peer scopes. No reconciliation ledger or outbox is retained.
