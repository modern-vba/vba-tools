---
status: accepted
---

# Run debug targets through the VBE

`VbeProcedureRun` selects the `DebugTargetProcedure` in its VBE code pane and
invokes the native `Run Sub/UserForm` command. This keeps execution inside the
VBE and permits an otherwise eligible public procedure in an
`Option Private Module`.

Native commands are resolved only after establishing `VbeCommandContext`.
Design mode, active-code-pane selection, code-window focus, and command
enablement are required; localized caption matching and `SendKeys` are
rejected.

External `Application.Run` and generated wrapper procedures are rejected
because project privacy blocks the former and the latter mutates the generated
VBA project with debug-only source. A missing, disabled, or failing native
command is a `DebugSetupError` with no fallback. `DebugEnvironmentDiagnostic`
therefore runs a harmless temporary procedure into an actual native breakpoint,
observes break mode, continues to verified completion, and clears the
breakpoint.

The adapter does not normalize the user-owned `VbeDebugEnvironment`. Error
trapping, compile-on-demand behavior, watches, and explicit `Stop` statements
remain VBE concerns and may stop execution independently of transferred
breakpoints.

A VBE compile error before target entry remains an interactive modal prompt
without a timeout. Dismissal is a `DebugSetupError`; cancellation
force-terminates Excel. The reusable parser does not replace the VBE compiler
or provide another execution route.
