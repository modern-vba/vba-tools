# 0071: Separate early debug authority from final completion

## Status

Accepted. Refines ADRs 0044, 0048 and 0062.

## Decision

All session consumers use one typed final receipt carrying the initial trigger,
actual terminal reason, optional observed exit code and retained failure/release
outcome. A successful Task does not imply a successful outcome. Normal ending
facts survive cleanup failures; observation loss is never successful detachment.

The existing session owner synchronously revokes execution and Restart liveness
before signaling internal raw termination, retiring modal observation, and doing
its existing bounded cleanup. Currentness fences read that early state, not the
scheduling of final completion callbacks. Raw ending remains internal to the
owner to avoid a modal/final-cleanup cycle. Sub completion remains live.

Stop, Dispose and observation loss converge on one terminal path. Source Stop
requests one exact-project Reset; plain binding Dispose detaches. The winner's
trigger remains authoritative. Repeated terminal operations replay one retained
failure with original dispatch and cleanup evidence. Cancelling a caller's wait
cannot cancel shared cleanup. Observed process facts may be collected through
sealing, but no owner waits solely for an exit that may never occur. Late faults
are observed without changing sealed evidence or releasing retained recovery.

An explicit Source-versus-owned lifetime policy preserves cleanup behavior
without a second optional completion task or fabricated zero exit code. DAP
emits termination once and exit only with observed code. Planned Restart cutover
already consumed its one-shot authority; retiring that old session must not hide
a replacement failure behind an unrelated-ended message.

## Verification

Production Source tests stall COM release while proving immediate execution and
Restart rejection and independent wait cancellation. They cover unknown/known
exit codes, workbook close plus cleanup failure, cached concurrent Stop/Dispose,
one native Reset, and retained recovery guidance. Existing owned-session races,
modal observation, late release, cancellation, DAP and real Excel lifecycle tests
exercise the same migration together.
