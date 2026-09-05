---
status: accepted
---

# Admit each debug launch from one parsed source generation

ADRs 0020, 0025, 0027, and 0037 require an immutable debug source snapshot,
exact breakpoint mapping, an independently validating `VbaDev` provider, and a
generation-bound Restart lifecycle. This decision consolidates the adapter-side
analysis that implements those contracts. It does not change the DAP schema,
the source-encoding matrix, the native breakpoint semantics, or the public
`vba-dev` command contract.

For every initial or restarted launch, `VbaDebugAdapter` invokes one internal
sealed `DebugSourceAdmission`. Admission first freezes the caller-owned
transport, validates its complete schema and source inventory once, and parses
each `.bas`, `.cls`, and `.frm` text exactly once. `.frx` remains opaque binary
content. A successful call returns one opaque `AdmittedDebugSourceSnapshot`
bound to the requested `DebugGenerationId`; a failure returns no partial
admitted value.

One private admission index derives the target, active source, request-ordered
breakpoint mappings, module-identity facts, deferred conditional-compilation
evidence, and opaque exact-byte build source set from that generation. Source
projection is lazy but reuses the generation's parsed tree. The adapter has no
parallel resolver, raw-source breakpoint mapper, conditional preflight parser,
or builder-local transport validator.

Failure authority remains ordered. Runner-owned DAP request and breakpoint
category policies remain separate authorities and preserve their existing
ordering. Within admission, transport failures precede target resolution.
Target failures precede semantic breakpoint failures. Breakpoints are checked
in request order, and complete-source identity failures that do not participate
in either target or breakpoint resolution follow those existing authorities.
Admission adds no source rescan, retry, editor coordination, closing stability
check, lock, or compare-and-swap protocol.

The builder accepts only the admitted generation's opaque build source set. It
materializes the retained exact bytes into that generation's owned workspace
and cannot accept the DAP transport, parsed trees, target facts, or conditional
proof. Caller mutation after admission therefore cannot alter either launch
facts or build bytes, and a source set cannot be materialized into another
generation.

The generated workbook remains authoritative for its actual
`DebugCompilationContext`. After build and workbook open establish that
context, the adapter verifies the generation-bound deferred proof before any
native breakpoint command or target execution. An inactive or unprovable target
or participating breakpoint fails closed without relocation.

`VbaDev` remains a standalone upstream provider. The adapter invokes its public
`build --source-snapshot ... --output ...` process contract, and `VbaDev`
independently admits the materialized bytes before starting its own Excel work.
No adapter validation result, parsed syntax object, runtime DTO, implementation
assembly, or test dependency crosses that process seam. This decision requires
no `VbaDev` production or test-project change and creates no reverse dependency
from `VbaDev` to the adapter.

Initial launch and Restart retain build-before-swap ordering, stale-generation
rejection, cancellation authority, one-shot prepared-plan commitment, workspace
ownership, and cleanup. A failed restarted admission or build leaves the still
usable current session intact.

## Consequences

- `N` transported text sources cause exactly `N` parser calls per launch
  generation, independent of participating breakpoint count.
- Target, breakpoint, conditional, active-source, and exact-byte build facts
  cannot be mixed across source revisions or generations.
- Source-derived debug behavior extends the sealed admission module instead of
  adding another source walk or public substitution seam.
- Adapter-side consolidation does not weaken `VbaDev` admission or make one
  product depend on its consumer.
