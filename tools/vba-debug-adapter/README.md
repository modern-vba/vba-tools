# vba-debug-adapter

`vba-debug-adapter` is the separately versioned, self-contained Windows x64
debug companion bundled with the VBA Tools extension. It owns native VBE debug
sessions and delegates snapshot workbook creation to the exact compatible
`vba-dev` executable selected for that session.

The extension manages this executable for normal use. Run the following command
only when inspecting its machine-readable compatibility contract:

```text
vba-debug-adapter capabilities --format json
```

To diagnose native VBE debugging readiness independently of any VBA project,
run:

```text
vba-debug-adapter doctor --format json
```

Doctor creates one dedicated temporary Excel/VBE session and proves trusted
VBIDE access, the native breakpoint and Run/Continue command contexts, an
actual breakpoint stop, harmless procedure completion, exact process ownership,
and terminal cleanup. It accepts no project, document, or timeout input, does
not call `vba-dev doctor`, and does not change persistent project state.

Once command handling begins, stdout contains exactly one schema `1.0` JSON
object with ordered stable checks; diagnostic logs use stderr. A complete
overall `pass` or `warning` exits zero. A `fail`, `unverified`, or incomplete
result exits nonzero while preserving the valid JSON report. Each operation has
its own bounded deadline, and terminal cleanup still runs after a failed,
timed-out, or cancelled stage.

Each stdio session uses a random 32-character lowercase hexadecimal ID and a
create-new lease beneath the adapter-owned temporary root. Restart keeps that
session ID, validates a fresh snapshot for the originally bound target, and
completes the new generation build while the current owned Excel process remains
active. After build success, it rechecks the bound session and restart request,
then terminates the old process immediately before starting the replacement.
This build-before-swap ordering intentionally replaces the former
validation-before-swap behavior. A preparation, revalidation, build,
cancellation, or stale-request failure before the swap cleans any new generation
and leaves a still-live current session running. If the bound session exits
during the build, its completion cleans the new generation and starts no
replacement. Replacement-start failure after the swap cleans the new generation
without reviving or reusing the terminated process.

After an unexpected adapter exit, the extension invokes the session-ID-only
cleanup surface:

```text
vba-debug-adapter cleanup --session <lowercase-hex-32>
```

The command never accepts a filesystem path. It removes only a workspace whose
PID and process-start-time lease proves stale, retries locked-file deletion for
five seconds, and reports a retained adapter-owned path on stderr. A later
adapter startup also reaps provably stale canonical session workspaces.

See the root [Debug in the VBE](../../README.md#debug-in-the-vbe) guidance for
the supported user workflow.
