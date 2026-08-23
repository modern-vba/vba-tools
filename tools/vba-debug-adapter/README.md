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

Each stdio session uses a random 32-character lowercase hexadecimal ID and a
create-new lease beneath the adapter-owned temporary root. Restart keeps that
session ID, validates a fresh snapshot for the originally bound target, and
uses a separate generation workspace before replacing the owned Excel process.
A failed or stale preparation leaves the current session running.

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
