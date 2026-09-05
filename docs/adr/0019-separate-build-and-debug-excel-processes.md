---
status: accepted
---

# Separate build and debug Excel processes

A `VbeDebugSession` uses the existing dedicated hidden Excel automation for
building on an invocation-scoped private desktop, closes that process and its
desktop after the build completes, and then creates a fresh visible
`DebugExcelProcess` for native VBE breakpoint setup and procedure execution.
Reusing the build process risks preventing break mode after programmatic VBIDE
changes, while attaching to a user's Excel session would make breakpoint state,
ownership, and debug-session termination ambiguous.

Cancellation during build terminates the hidden build Excel process and removes
only `vba-dev build` invocation scratch. ADRs 0025 and 0027 supersede the earlier
persistent-bin output and ownership contract: the separate debug component asks
`VbaDev` to build a caller-owned session workbook from an ephemeral source
snapshot, leaves the previous completed bin workbook unchanged, and owns cleanup
of the successful session workbook after the build invocation exits.

The workbook-generation adapter used by build, publish, test, import, export,
and workbook diagnostics delegates its lifecycle to the sealed internal
`AutomationExcelProcessRuntime`. The runtime retains the proven native sequence:
create the private desktop, launch suspended with atomic Job ownership, capture
and observe the exact PID, resume, bind on the STA, execute bounded workbook
operations, and complete cooperative or forced cleanup, message draining, COM
release, exact process-tree release, desktop release, and dispatcher retirement.
Its native lifecycle seam is a platform test boundary, not a scenario API.
Scenario code receives only a bounded workbook-operation session and cannot
launch, attach to, terminate, or dispose an Excel process. That session is
retired before runtime cleanup starts.

An immutable internal outcome retains the last operation stage, the original
operation, cleanup and dispatcher failures, separate process and dispatcher
release proofs, isolation diagnostics, and cancellation first observed during
cleanup. The operation value is unavailable through the outcome until terminal
failures and both release proofs have been checked. An unproved process release
or dispatcher retirement takes precedence over success, cancellation, and weaker
errors. Dispatcher retirement has a bounded observation period even when the
scenario itself succeeded; a stalled dispatcher is never silently accepted.

The runtime does not decide whether cleanup-time cancellation overrides a
scenario's commitment. The generation adapter preserves its existing
pre-commit cancellation behavior only after mandatory cleanup verification;
other scenarios can classify the same released evidence against their own
commitment boundaries. This migration preserves the public command and workbook
generation interfaces and adds no dependency outside VbaDev. Reference probing,
initial workbook creation, and Host Event catalog discovery now use the same
sealed runtime lifecycle as workbook generation. The Host Event scenario owns
only automation security and Event configuration, one unsaved blank workbook,
one temporary UserForm, catalog projection, component removal, and close without
save. It does not attach to a user process or workbook, and it publishes no
catalog until both process release and dispatcher retirement are proved.
Timeout, cancellation, catalog failure, cooperative cleanup failure, and either
lifecycle uncertainty remain distinct terminal evidence. Only the intentionally
visible `DebugExcelProcess` and debug-session lifetime remain separate.

The initial-workbook scenario establishes and verifies the existing identity
baseline and saves its staging workbook. The creator, not the process runtime,
owns pending artifact observation, release-gated receipt completion, and
create-only final materialization. A stalled or failed dispatcher retirement
withholds successful creation even after Excel has saved and its process has
exited. Unproved process release never grants cleanup authority. The scenario
uses the runtime's existing process-loss result for unexpected exit between
the final operation and cleanup, rather than accepting saved work alone.
This reuses the existing lifecycle observation without adding a watcher.
The scenario has no project-commit capability: `NewProjectCommand` retains
manifest commitment, exact receipt-based rollback, and its before/after-commit
cancellation policy.
This consolidation adds no independent process loop, automatic restart, new
ownership primitive, or concurrent-editor guarantee.
