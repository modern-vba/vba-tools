---
status: accepted
---

# Use System.CommandLine as the vba-dev command model

`vba-dev` uses `System.CommandLine` as the single model for commands,
subcommands, arguments, options, parsing, validation, help, version output, and
completion. This follows the CLI structure of
[FirewallRuleToolkit](https://github.com/akhs-tkmr/FirewallRuleToolkit/tree/main),
which was the intended implementation baseline. The current
`ToolingCommandCatalog` and `CommandLineApplication` custom parser are
transitional code and must be replaced before release rather than extended with
a second completion grammar.

The `RootCommand` graph and its command factory belong to the executable-facing
CLI layer. Application and domain projects remain independent of
`System.CommandLine`; command actions adapt `ParseResult` values to application
requests, while application services return shell-neutral results. Existing
machine contracts such as `capabilities --format json` and command-specific
output schemas remain explicit application behavior rather than being inferred
from help output.

`vba-dev` keeps System.CommandLine process-termination handling enabled but
sets `InvocationConfiguration.ProcessTerminationTimeout` to
`Timeout.InfiniteTimeSpan` instead of accepting the library's two-second
default. Ctrl+C and other supported termination requests therefore reach async
command actions as cooperative cancellation without letting the invocation
pipeline manufacture exit `130` before a command has proved its own cleanup or
transaction outcome. Command-specific stage and owned-process deadlines remain
the bounded recovery mechanisms. An abrupt caller, extension, terminal, or
operating-system loss remains the established crash-recovery path rather than a
second cooperative timeout hidden in the parsing framework.

An extension-spawned ordinary command may opt into the hidden, caller-neutral
`--cancellation-transport stdin-v1` control transport. Only in that mode does
`vba-dev` dedicate standard input to cancellation control: the caller writes
the exact UTF-8 frame `cancel\n` once, and receipt idempotently cancels the same
token observed by the asynchronous command action. End-of-file by itself is
not a cancellation request because it can also mean caller shutdown or loss;
it therefore cannot authorize exit `130`. Without the option, `vba-dev` does
not read standard input and direct terminal use continues to rely on Ctrl+C.
The version-`1.0` grammar recognizes only the BOM-less bytes for `cancel`
followed by one LF. Repeated valid frames have the same one-time effect. EOF,
`cancel` without LF, CRLF, a BOM, and unknown, incomplete, or oversized input
do not request cancellation. The reader bounds retained input and discards
invalid data without turning a control-protocol mistake into a project-command
failure or writing a protocol diagnostic to command result streams.
An integrating caller sends its one request by ending the child standard-input
stream with `cancel\n`; no acknowledgement frame is added. Completion of that
write means only that the caller finished its transport operation, not that the
command observed cancellation. A write failure such as `EPIPE` likewise does
not establish cancellation or replace the command's result. Callers that are
not allowed to force-terminate the command continue waiting for its terminal
outcome. A Node.js caller accumulates standard output and standard error and
classifies the result only after the child `close` event, not merely `exit`, so
all process-owned streams have closed before JSON or cleanup diagnostics are
trusted.
For ordinary managed commands, the VS Code caller permits ten seconds after
the local request before terminating the CLI. This exceeds the CLI's five-second
owned-Excel cleanup grace plus its one-second release-observation allowance and
leaves time for request delivery, terminal classification, output drain, and
child close. `new excel`, CommonModules Add, and CommonModules Update have no
caller force-termination timer.
The VS Code caller records a failed write in VBA Tools Output and changes its
running progress message to
`Cancellation request could not be delivered; waiting for vba-dev to finish.`
without opening a separate notification. Exit `130` remains silent, and a
failure or untrusted result retains its one established error notification. A
trusted success that would otherwise be reported to the user instead uses one
warning notification, appends `Cancellation request could not be delivered.`,
and offers Show Output. This caller-local transport condition does not enter or
increment the CLI result's warning array and never produces a second terminal
toast.
Control frames never appear in command standard output or standard error. The
transport is advertised once at the capability root as
`featureVersions["invocation.stdinCancellation"]: "1.0"`, rather than being
repeated on every command. Version `1.0` covers the hidden option, `stdin-v1`
transport selection, exact frame grammar, idempotent token delivery, invalid
input and EOF neutrality, bounded reading, and separation from result streams;
it does not redefine a command's transaction boundary, exit status, or output
schema. Integrating
callers require the exact feature version before enabling the transport and
tolerate unknown additional feature keys. The option remains hidden from
ordinary help and completion. Its name and behavior are independent of VS Code
so another automation caller may use the same contract. The debug adapter is
excluded because its standard input belongs to DAP. A named pipe remains a
possible future transport only if bidirectional or multi-controller control is
needed.

Static completion comes from the same command graph that performs parsing.
Dynamic candidates, including VBA project reference names, are exposed through
`System.CommandLine.Completions` and shell-neutral application services rather
than a custom command-line parser. Shell registration is a separate adapter and
must not duplicate command or candidate-selection rules. Because the CLI has not
been released, differences in custom-parser help text or parse-error wording do
not require compatibility support.

The initial supported shell integration is Windows PowerShell 5.1 and
PowerShell 7. `vba-dev completions script pwsh` writes a self-contained
registration script to standard output. The script uses PowerShell's native
argument-completer mechanism and asks `vba-dev` itself for dynamic candidates;
it does not require `dotnet-suggest`, a sidecar PowerShell module, VS Code
terminal initialization, or globally registered executable state. Users decide
whether and where to evaluate the generated script, and `vba-dev` does not edit
PowerShell profiles through install or uninstall commands.

The initial script may query `vba-dev` for every completion request. Its public
setup contract must allow a later hybrid implementation that embeds static
commands and options in the generated script while querying `vba-dev` only for
runtime values. Such a change must continue to derive both paths from the same
`RootCommand` graph and must not require users to change the
`vba-dev completions script pwsh` profile entry.

`reference add` completion remains a lightweight discovery operation. It reads
registered TypeLib descriptions, interprets registry version and LCID keys as
hexadecimal, groups trimmed descriptions with `OrdinalIgnoreCase`, and emits the
ordinal-minimum spelling of each group once.
On supported Windows versions it reads the merged, shared
`HKEY_CLASSES_ROOT\TypeLib` view once rather than unioning `Registry32` and
`Registry64` views. It does not filter names according to the x64 CLI process or
start Excel or VBE, and it does not promise that every discovered name will pass
final reference resolution. The explicit `reference add` invocation owns any
required VBE-equivalent ambiguity probe against temporary source template
copies. If that probe still finds multiple distinct usable identities, the
command fails and reports the candidate identities even though completion
previously offered the shared name.

Reference-name completion resolves the document selected by `--document`, or
the manifest's `primaryDocument` when that option is omitted. For
`reference add`, it removes names already present in that document's manifest
and names already supplied in the current invocation. For `reference remove`,
it reads only that document's manifest and removes names already supplied in the
current invocation; it does not read the registry or start Excel or VBE. All
comparisons use `OrdinalIgnoreCase`, and candidates are ordered with
`OrdinalIgnoreCase` plus an `Ordinal` tie-break. These completion filters do not
change the command semantics: manually adding an already-present name or
removing an absent name remains a successful no-op.

Dynamic reference completion fails quietly when project or document context
cannot be resolved. A missing or unreadable manifest, an invalid `--project`,
or an unknown `--document` produces no dynamic candidates and writes no
diagnostic to standard output or standard error during completion. In
particular, `reference add` does not fall back to an unfiltered global TypeLib
list. Static command and option completion remains available, and an explicit
command invocation reports the normal actionable project-resolution error.
The registry reader skips an individually malformed or unusable TypeLib
registration. If a catalog-level failure means the registry scan may be
incomplete, completion discards every dynamic reference-name candidate and
remains equally quiet instead of presenting a partial catalog. Explicit
`reference add`, `reference list --available`, and `doctor` invocations report
the actionable registry failure normally.

Help generation does not evaluate dynamic completion sources. `reference add`
and `reference remove` display a stable argument placeholder such as
`<references...>` and a static description rather than enumerating registry or
manifest values. Dynamic names are calculated only for an actual completion
request, so help output and latency do not depend on project, manifest, or
registry state. This remains a presentation policy over the same
`RootCommand` graph rather than a separate help grammar.

The generated PowerShell registration script queries the initial implementation
through `System.CommandLine`'s standard `[suggest:<cursor-position>]` directive.
No public `vba-dev complete` command or second completion-result schema is
introduced. The initial protocol returns newline-delimited candidate labels,
which are sufficient because reference display names and inserted values are
the same. The PowerShell adapter converts them to `CompletionResult` values and
owns shell quoting. Contract tests cover spaces, apostrophes, non-ASCII names,
incomplete quoting, and a cursor positioned within rather than after the command
line on Windows PowerShell 5.1 and PowerShell 7. A future richer internal
protocol may replace this transport without changing the public
`vba-dev completions script pwsh` setup command.

The generated script embeds the absolute path of the `vba-dev.exe` process that
produced it and queries that exact executable for suggestions rather than
resolving `PATH` again on every Tab press. It registers native completion for
`vba-dev`, `vba-dev.exe`, and the generating executable's absolute path, with
PowerShell-safe escaping for spaces and apostrophes. Re-evaluating the profile
entry after an update or relocation regenerates the script from the newly
resolved executable. The script does not read VS Code settings or the
extension's bundled-tool path; a process moved after registration requires a
new shell or profile reload.
