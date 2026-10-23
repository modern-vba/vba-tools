---
status: accepted
---

# Share verified native breakpoint transfer across Source Debug and Doctor

## Context

Source Debug and Doctor independently validated source maps, selected VBE panes,
and invoked Toggle Breakpoint. Module-name limits and command failures differed,
and an initial content check did not prove currentness after UI activation.

## Decision

One exact-binding native-operation Module owns the complete breakpoint path for
both callers, including a closed non-executing inspection operation. It checks
the complete requested map set before any toggle, using exact component kind,
identifier identity and 31-Unicode-scalar limit, whole contents, row bounds, and
repeated-module consistency.

After preparing the native context, it reacquires the intended component/code
membership and verifies whole contents, mode, pane, selection, workbook binding
and built-in command identity/enabled state before invocation. Foreground
activation remains best effort under ADR-0065. A final cancellation fence invokes
no command. Request order is preserved; a later failure can leave earlier
toggles applied and never triggers retry, compensation or rollback.

Typed internal failure facts distinguish binding, map, pane, selection, missing,
wrong, disabled and failed invocation. They retain the operation, original source
URI, zero-based editor line and one-based VBE line without changing protocol
payloads. Existing DebugSetupException behavior and retained cleanup evidence
remain compatible.

Source and Doctor are concrete Adapters at the existing COM Seam. They retain
their own STA, workbook/process and reference-release policies. The shared
Module acquires no Excel lifetime. Reacquired component/code aliases remain in
the outer transfer scope, avoiding intermediate Doctor final-release of RCWs
still needed by later commands. Fixture import reuses content validation while
retaining its own acquisition scope.

## Consequences

The former breakpoint preparation recipes are removed from both callers.
Production-path tests apply the same failure matrix to both, and Windows/Excel
verification exercises exact selection among multiple owned workbooks.
ADR-0020's content authority and ADR-0062's Source ownership remain unchanged.
Target eligibility, physical target selection and launch admission order are
outside this change.
