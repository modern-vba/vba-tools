---
status: accepted
---

# Version the extension and companion CLI separately

ADR 0036 supersedes only the `host-class list`, `hostClass.list`, and
`vba/hostClassProjectionSnapshot` command-contract examples below. Their
replacement is the environment-scoped `host-event list` catalog contract; the
decision to version the extension, CLI, and command schemas independently
remains accepted.

The VS Code extension and `vba-dev` should have separate release versions
because UI, language-server, Test Explorer, and workbook automation changes do
not always move together. The extension must declare the CLI command contract it
can use and bundle a CLI version tested against that contract. When an explicit
`vbaTools.devtool.path` override is missing or incompatible, the extension emits
an actionable warning and falls back to the compatible bundled CLI. It pins that
effective path for every extension consumer in the session and reports the
configured/effective difference through Doctor. The command contract is
identified separately from the CLI tool version, such as a `contractVersion`
and per-command output schema versions returned by
`vba-dev capabilities --format json`. Capabilities inspection must be fast and
side-effect free, so it is separate from `doctor`, which may inspect the local
Office, VBIDE, workbook, or project environment.
The resolved `reference list --format json` contract is independently advertised
with schema version `1.0`; both configured and `--available` modes use that
schema and identify their mode in the payload.

Read-only Host Event inspection is independently advertised as
`featureVersions["hostClass.list"] == "1.0"` and CLI spelling
`commandSchemaVersions["host-class list"] == "1.1"`. The extension consumes
that complete invocation result, owns refresh generations and retained state,
and folds it into the separate `vba/hostClassProjectionSnapshot` notification
schema `2`; CLI schema `1.1`, LSP schema `2`, document-local snapshot revision,
and extension refresh generation are distinct compatibility and freshness
values.

The extension-owned `vba-dev-contract.json` also requires top-level
`featureVersions["invocation.stdinCancellation"] == "1.0"` and
`featureVersions["projectCreation.pathValidation"] == "1.0"`, plus
`commandSchemaVersions["new excel"] == "1.0"`. The feature is an
executable-wide invocation capability rather than a per-command output field:
it lets every extension-managed ordinary command receive the hidden
`stdin-v1` cooperative cancellation request, while each command retains its
own transaction, cleanup, exit, and result contract. `new excel`,
`common-module add`, `common-module update`, and `host-class list` additionally
retain their command-specific prohibition on caller force-kill fallback.
Host-class replacement waits for the CLI to exit after releasing its owned
Excel process before the extension scheduler starts another inspection. A configured CLI
that omits or mismatches the required feature is incompatible, so the extension
issues the established actionable warning and selects a compatible bundled CLI
for the whole session rather than mixing executables by command. If the bundled
CLI is also incompatible, no managed command is started. Unknown additional
feature keys remain compatible.

When a configured CLI fails resolution but the bundled CLI satisfies the complete required contract, the extension proceeds with that session-pinned bundled executable and shows at most once per window activation: `The configured vba-dev executable is unavailable or incompatible. VBA Tools is using its bundled vba-dev for this window.` The ordered actions are Open Settings and Show Output. Output records the configured candidate and its failure, the selected bundled path, and the required contract; changing the setting does not replace the already pinned executable during that activation. If neither candidate resolves compatibly, guided creation stops before Doctor preflight or user input and shows exactly one error, `VBA Tools could not find a compatible vba-dev executable.`, with Open Settings and Show Output. Output records both candidate paths and their independent failures. There is no Run Anyway, PATH search, download, third executable, or automatic retry. The single-flight operation ends, no preflight result is cached or retained, and the user may invoke the command again after correcting the installation or setting.

The project-creation validation feature independently versions the input
contract shared by guided creation and `new excel`: exact project-name
preservation and rejection, Excel bracket handling, the 218-UTF-16-code-unit
limit, stable reason precedence, and the shared validation corpus. It is not an
output-schema alias and does not version guided UI wording or project mutation;
`commandSchemaVersions["new excel"]` continues to version the successful result.
The same configured-to-bundled fallback applies to either required feature so
the extension never validates a request under rules different from the CLI it
will invoke.

Companion executable resolution is itself gated by VS Code Workspace Trust. The
extension declares limited Restricted Mode support and lists
`vbaTools.devtool.path` and `vbaTools.debugAdapter.path` as restricted
configurations, allowing safe language assistance to remain available without
reading untrusted workspace executable overrides.
No managed CLI, Excel/VBIDE, Doctor, debug-adapter, or vba-dev-terminal process
is resolved or launched while the current window is untrusted. Detecting that
state at command entry discards the window's prior resolution and any guided
creation preflight pass; granting trust never resumes a command automatically,
and the next invocation performs a fresh resolution and preflight. VS Code
exposes an in-process trust-grant event but no revocation event, so invocation
entry is the active gate; a reload, deactivation, or process loss associated
with removing trust remains abrupt caller loss under each command's existing
recovery contract rather than a fabricated cooperative cancellation.

The extension supplies the exact resolved absolute `vba-dev.exe` path to the
language server through `--vba-dev`. The language server independently runs
side-effect-free capability inspection against that path once at startup and
requires `reference list` JSON schema `1.0`. This second validation supports
standalone server startup and detects a supplied executable that changed after
extension validation; it is not repeated for each catalog refresh. Failure
disables CLI-backed reference resolution and records a warning, while the
language server continues with registry-only, fail-closed discovery. It does
not re-resolve or replace the tool from `PATH`, VS Code settings, or its own
installation directory.

ADR 0027 adds a third independently versioned component and contract for
`vba-debug-adapter.exe`. The extension validates an explicit
`vbaTools.debugAdapter.path` independently from `vba-dev`, then supplies the
resolved CLI path to the adapter. The extension-owned
`vba-debug-adapter-contract.json` pins adapter contract `1.0`, DAP extension
protocol `1.1`, stdio transport, lowercase-hex-32 session IDs, cleanup and
Doctor commands, Doctor output schema `1.0`, and required
`build.sourceSnapshot` feature `1.0`. The CLI advertises that build primitive
under `featureVersions` rather than carrying a debug-adapter protocol version.
The adapter validates only the snapshot-build feature it consumes, not the CLI
tool version or complete command contract. An invalid debug-adapter override
remains a failed explicit selection and does not fall back. One debug session
pins both effective paths, and no component performs a silent fallback.
