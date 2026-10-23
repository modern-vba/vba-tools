# 0073: Own Rename confirmation and response lifetimes

## Status

Accepted. Refines ADRs 0029/0050 and uses ADR 0072's evidence policy.

## Decision

One session RenameModule owns capture generations, planning, pending consent and
lease transfer. Its ordered Capture accepts decoded inputs and immutable
capabilities; bounded Execute uses captured evidence and returns a closed result.
The LSP Adapter retains wire decoding and WorkspaceEdit/error projection, not a
validation recipe or competing pending/capture owners.

A Rename reaching Capture invalidates unconsumed pending consent and prevents
old-generation offers, even for invalid parameters/name. Cancellation before
Capture and malformed envelopes do not. A successful offer transfers the whole
retained plan and capture lease into pending state before any response bytes are
published. No writer-success continuation creates ownership.

Matching confirmation Capture consumes the opaque ID once for either Continue
or Cancel, removes pending and transfers its lease to the captured operation.
New Rename invalidates unconsumed pending state only. It does not release a plan
already consumed by an in-flight confirmation. Common current-evidence checks
still apply; Continue returns only the stored complete plan without replanning.

A response failure/cancellation notification matches exact confirmation ID and
internal generation. It can discard only that unconsumed pending offer. Delayed
notifications cannot affect a newer pending plan or a consumed operation.
Successful challenge delivery keeps pending consent and cannot revive expired
state. Capture cleanup and response delivery are separate lifetimes. Response
cancellation revokes matching pending consent immediately; the framing owner
still completes an already-started atomic frame before cancellation is surfaced.
This preserves its non-cancelable in-flight write contract.

All lease transfers are one-way. Failed offers, explicit cancellation, newer
Rename, confirmation completion/rejection, exceptions and pending shutdown
release each lease once. The existing scheduler cancellation/drain owns in-flight
operations during shutdown. No protocol version or client behavior changes.

## Verification

Production executor tests gate response writes after challenge bytes are visible,
then exercise confirmation Capture before writer completion, late write failure
and cancellation, newer offers, consumed plans and delayed success. Other tests
cover invalid input versus pre-Capture cancellation/malformed envelopes, one-use
Continue/Cancel, old-generation offer rejection, stale evidence, shutdown and
exactly-once disposal. Existing process tests cover confirmation protocol reuse
and strict clients; the full language-server and architecture checks remain gates.
