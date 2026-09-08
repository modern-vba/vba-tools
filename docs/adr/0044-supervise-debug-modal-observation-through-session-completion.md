---
status: accepted
---

# Supervise debug modal observation through session completion

Workbook-open, Run-time, and post-Run modal observation share one phase-owned
polling loop. Each phase captures its baseline before COM starts. WorkbookOpen
ends after opening; TargetStart survives Run's return with its baseline and
notification history intact. This preserves ADR 0024's unbounded interactive
prompt policy while leaving Doctor's finite stage deadlines independent.

The session supervises phase faults as terminal infrastructure failures.
Observation and notification errors after Run must reach Completion even while
DAP stdin stays open. Stop, workbook close, raw process exit, and monitor failure
use one idempotent terminal path that retains the first cause and later cleanup
evidence. The existing process owner advances termination and Job closure before
observation shutdown; observers await raw process completion so they cannot
wait cyclically on session cleanup. Completion includes COM and generation
release, and repeated termination/disposal observes the same outcome.

The runner reports runtime infrastructure faults as DebugSessionError and sends
terminated once over a healthy transport. A failed output stream is latched and
not retried, without preventing owned-resource cleanup. Normal planned Restart
cutover terminates only its old session. Unplanned lifecycle failure revokes the
replacement authority before a late build can commit another workbook.

This extends ADR 0021's ownership contract without introducing a second process
owner, a prompt timeout, or a separate post-Run monitor.
