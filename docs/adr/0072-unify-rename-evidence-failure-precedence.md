# 0072: Unify Rename evidence failure precedence

## Status

Accepted. Refines ADRs 0029 and 0050.

## Decision

Ordinary and confirmed Rename share one evidence policy. Validate participating
source revisions, project/catalog authority, required template identity and
captured path/source-unit decision, in that order. Apply the same fences before
and after file preflight, including a failed preflight; a newly invalid proof
outranks the file failure. Only then check capability for final actual file
operations. Unexpected preflight exceptions remain internal errors.

Semantic rejection and NoChange must also validate applicable captured proof.
Conditional template requirements remain target-specific. A retained-path plan
requires no resource operation it does not perform. Unsupported confirmation
clients retain strict collision rejection and the existing LSP shape.

Pre-offer path retention may finalize against the same immutable baseline.
Confirmation validates the retained complete plan without changing its semantic
edit, closure or file operations. If revalidation requires a different plan,
reject and require a fresh user action. Form preflight is idempotent with respect
to already materialized sidecar operations. No retry or expansion is introduced.

Existing Capture/Execute lanes, immutable snapshots, pending ownership and
exactly-once lease release remain unchanged; ownership consolidation is a
separate issue.

## Verification

Paired ordinary/confirmed protocol execution exercises individual and concurrent
proof failures, successful/failed preflight mutations, unexpected inspection
errors and lease release. A shared evidence table includes required versus
inapplicable template facts, final file capability and stored-closure changes.
Process tests combine source/template changes and confirm the complete form
sidecar plan exactly once. Existing Rename process coverage remains applicable.
