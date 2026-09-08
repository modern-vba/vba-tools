---
status: accepted
---

# Retain debug failure completion and owner release evidence

## Context

Debug startup, build, prepared-plan execution, and termination caught cleanup
exceptions independently. Some catches discarded later failures, while a later
Dispose could replace the original cause or appear successful after an earlier
failed cleanup. Restart could not reliably distinguish retained temporary files
from unproved process, COM, or handle release.

## Decision

One internal DAP-owned DebugFailureCompletion Module retains the first causal
exception through ExceptionDispatchInfo, subsequent distinct cleanup failures,
and structured resource-release observations. Each observation identifies its
lifecycle stage, resource kind, reason, and available PID or retained path.
Repeated observations of the same exception do not duplicate it. Completion
produces one retained outcome; concurrent or repeated disposal observes the same
owner completion task, including its failure.

The Module records facts and composes diagnostics. Existing process, COM,
workspace, generation, and session owners still acquire, transfer, sequence, and
release their resources. There is no callback registry, additional process
owner, or cross-product cleanup coordinator. Only the responsible owner can
report positive release evidence. A Dispose call, disposed flag, attempted
Restart release, exception type, or caller-composed path is not such evidence.
Absent or ambiguous owner evidence remains unproved.

Process completion, COM release, native handle release, and filesystem deletion
remain separate facts. Windows generation cleanup attempts descendant-handle
release, owned-tree deletion, and remaining-handle release in the existing order
even after a failure. A deletion fault followed by an unproved handle release is
not a file-only failure. Native close results are retained by the existing
owner, without interpreting a closed managed wrapper as native release proof.

At ownership boundaries, even a failure with proved cleanup carries its owner
evidence and original causal exception with its stack. Admission records when
no resources have been acquired; absent build-owner evidence remains unproved.
Proved ordinary cancellation remains cancellation. Additional cleanup faults
or unproved release carry both the original cause and the retained cleanup
outcome as a cleanup failure. Notification failure cannot
replace cleanup authority. The existing DAP output failure latch remains final;
the runner does not retry that stream or emit duplicate terminal notifications.

Session completion retains a setup failure even after successful resource
release. Doctor therefore reads the session owner's release evidence when
classifying cleanup; rethrowing the retained setup cause does not by itself mean
that Excel or its handles remain live.

Restart keeps build-before-swap and one-shot generation ownership from ADR 0041.
A failed preparation can retain the old session only before replacement
authority is consumed, while that same session is still current and live, and
when cleanup is proved or the remaining fault is solely isolated temporary-file
deletion. It reports the failure and retained paths and preserves completion
monitoring. Unproved process, COM, or handle release revokes replacement
authority and uses ADR 0044's existing terminal path. Stop, disconnect, and root
cancellation terminate the old session even for file-only cleanup faults. A
session ended during build or released for swap is never revived.

ADR 0043's companion-process cleanup budget remains separate from visible Excel
session completion and unbounded interactive prompts. This decision adds no
automatic build retry, relaunch, cleanup retry policy, or generation reuse.

## Consequences

Failure reporting describes the original cause and every observed cleanup fault
without losing stack evidence. Restart retention depends on owner facts instead
of catching particular exception messages. Deterministic tests exercise public
owner, launch, and DAP paths with multiple faults, cancellation, repeated
completion, and terminal races. Domain and debugging architecture documentation
use the same release classifications.
