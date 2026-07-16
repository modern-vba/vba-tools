---
status: accepted
---

# Use a guarded Enter override for block skeleton insertion

`VscodeExtension` triggers `BlockSkeletonInsertion` through a guarded
VBA Enter command instead of LSP on-type formatting or static VS Code enter
rules.
This permits one undoable editor transaction to keep the cursor on the empty
body line without depending on `editor.formatOnType`.

The C# language server remains the authority for VBA syntax and returns a
version-bound insertion plan. The TypeScript adapter may use only a permissive
prefilter to avoid delaying clearly unrelated Enter keypresses; it must be a
superset of all eligible modifiers, header forms, and continuation layouts, so
false positives are acceptable but eligible headers must reach the server. It
must not make the final grammar, validation, block-pairing, indentation, or
terminator decision. A stale plan is never applied.

The custom `vba/blockSkeletonInsertion` request identifies the document URI,
post-native document version, original header-end position, and resolved
indentation options. Its
options contain `insertSpaces`, optional numeric `indentSize`, and `tabSize` as
the compatibility fallback when `indentSize` is absent. The server must derive
the line ending and all syntax facts from one atomic workspace snapshot
containing the matching version, source text, and syntax tree; a missing or
mismatched snapshot returns no plan and does not perform disk, project-catalog,
or reference resolution.
The successful response repeats the exact document version and insertion
position and returns literal text before and after the body cursor; an
ineligible header returns `null`. The adapter must revalidate the active editor,
version, and empty selection before applying the response.

The contributed Enter keybinding invokes VS Code's built-in `runCommands` with
the native non-recursive `lineBreakInsert` command first and a post-native
extension command second. Native Enter therefore begins in the editor before
control crosses into the extension planner path. An activation-scoped recorder
accepts only one empty-range edit containing exactly one line ending followed
by spaces or tabs. It records that insertion range, resulting cursor, and
post-native version V+1.

The post-native command consumes that receipt and starts the plan request
against version V+1. The request position remains the start of the recorded
native insertion, which is the physical end of the original header. The server
therefore plans from a snapshot that already contains native Enter. A declined,
failed, cancelled, timed-out, stale, or otherwise inconclusive transaction
performs no further edit, leaving the native result as the fallback.

For a fresh accepted plan only, the adapter replaces that recorded native range
with literal snippet text around one final tabstop. Editor, document, selection,
configuration, and version invalidation are latched while the response is
pending, so returning to a previously valid state cannot revive a stale plan.
The replacement disables a leading undo stop, enables a trailing undo stop, and
preserves whitespace, so the native edit and skeleton replacement remain one
undo operation.
Server-provided text must never be interpreted as snippet syntax or reindented
by VS Code. `lineBreakInsert` is dispatched exactly once for every intercepted
candidate, including the accepted path; a second native fallback is forbidden.

The override applies only to a single empty cursor at a physical VBA line end
when suggestion acceptance, snippet navigation, IME composition, and other
special editor states do not own Enter. Multiple cursors, disabled behavior,
server unavailability, cancellation, timeout, stale document state, or any
inconclusive result delegate to the standard Enter behavior. The resource-scoped
`vbaLanguageServer.blockSkeletonInsertion.enabled` setting controls the feature
and defaults to `true`. Candidate requests use an initial internal deadline of
100 milliseconds. A timeout leaves the already-inserted standard Enter in
place, cancels or ignores the late plan, and records the fallback reason only in
verbose trace output; the deadline is not a user setting and may change only in
response to integration measurements.

## Considered Options

- LSP `textDocument/onTypeFormatting` keeps the trigger protocol-standard and
  parser-aware, but depends on `editor.formatOnType` and cannot express snippet
  cursor placement or guarantee the required undo experience.
- VS Code `onEnterRules` provides fast native indentation, but its regular
  expressions cannot validate a complete VBA block header, detect an existing
  terminator, or fail closed across malformed and conditional-compilation
  structure.
- An unguarded Enter override provides full control but would route ordinary
  editing, suggestion acceptance, snippets, IME input, and multiple cursors
  through extension code unnecessarily.
- Overriding the global `type` command could buffer characters while the native
  command is pending, but it would route ordinary typing, IME commits, and
  snippet input through the Extension Host and still would not serialize every
  editor command. The native-first keybinding avoids owning character input.

## Consequences

Acceptance required a VS Code Extension Host feasibility gate to prove that
rapid input preserves `Enter`-then-text ordering, fallback
retains the exactly-once non-recursive native `lineBreakInsert` baseline, one
undo operation restores the complete pre-Enter state, and no late plan is
applied after timeout or cancellation. A Windows Japanese IME manual smoke test
must also prove that composition-confirmation Enter is not intercepted and the
subsequent editing Enter triggers at most one skeleton. Parser and feature
implementation must not proceed if any gate check fails; the trigger
architecture must be reconsidered instead.

If the gate passes, the extension must keep the standard Enter path reliable
and latency-bounded, while the C# planner must expose all insertion facts
without leaking VBA grammar into TypeScript. VS Code integration tests must
cover cursor placement, a single undo operation, disabled and fallback
behavior, suggestion and snippet states, multiple cursors, server failure,
cancellation, timeout, rapid edits, and document-version races. OS-level IME
composition remains a required manual smoke test rather than an automated
Extension Host test.

## Feasibility Evidence

Automated checks were repeated on 2026-07-16 after a workstation restart and
the native-first redesign:

- `npm run test:extension`: 99 passed, including deadline, cancellation,
  late-response, state-latching, contribution, fixed-runtime, and receipt
  contracts.
- `npm run test:extension-host`: 16 passed on the pinned minimum supported
  VS Code 1.125.0 runtime. The real extension command covered native
  indentation, literal accepted-plan insertion, body cursor placement, one-step
  Undo, rapid follow-up input without an explicit delay after request dispatch,
  apply-versus-type races, disabled, declined, failed, cancelled, timed-out,
  document-stale, monotonic cursor/editor invalidation, cold activation,
  non-VBA, selection, multiple-cursor, non-line-end, and read-only behavior.
- `npm run test:packaging`: 8 passed. The VSIX file-list contract excludes
  Extension Host runners, compiled tests, source maps, and temporary smoke-test
  artifacts. `npm run verify:guarded-enter` is the aggregate automated gate.

The manual smoke gate passed on 2026-07-16 with the packaged and installed
`modern-vba.vba-tools@0.0.1` extension on Windows 11 Pro build 26200, VS Code
1.128.1, and Microsoft Japanese IME:

- Repeated physical Enter followed immediately by `x` preserved newline-first
  ordering without duplicate or delayed edits.
- Composition-confirmation Enter committed the active Japanese IME composition
  without inserting a newline.
- The subsequent editing Enter followed immediately by `x` inserted exactly one
  native newline and one `x`, without a delayed edit.

All feasibility criteria passed, so this ADR is accepted and issue #198 may
proceed after issue #197 is committed and closed.
