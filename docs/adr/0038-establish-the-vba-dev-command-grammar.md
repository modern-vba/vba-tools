---
status: accepted
---

# Establish the vba-dev command grammar

ADR 0028 established `System.CommandLine` as the command model. This decision
defines the internal ownership and deterministic validation contract used to
complete that migration. ADR 0039 independently keeps VbaDev free of reverse
dependencies on the extension, language server, debug adapter, and other
products.

## Decision

The executable-facing CLI layer has one internal `VbaDevCommandGrammar` Deep
Module. Its single construction entry point creates one
`System.CommandLine.RootCommand` and returns one `VbaDevCommandGraph` containing
that root and the exact hidden cancellation-transport option attached to it.
Each `VbaDevCommandLine` owns one such graph for its supplied application
composition and generating executable path; the graph is not a process-wide
singleton. The same root instance is the only runtime model for command and
option symbols, parsing, validation, help, version output, static and dynamic
completion, typed binding, and action connection.

The graph also owns one `VbaDevGrammarFailureRouter`. Grammar construction
registers only the closed validation primitives admitted by this decision, then
the router copies those registrations into an immutable snapshot and validates
that every referenced command and symbol belongs to the completed root. Runtime
selection therefore cannot observe later registration changes or consult a
second command description.

`VbaDevCommandLine` owns only invocation, standard-stream configuration,
cooperative cancellation monitoring, `Timeout.InfiniteTimeSpan` process
termination configuration, and terminal dispatch through the constructed
graph. It does not reconstruct or search for command symbols by name.

Command-family modules may later localize groups of declarations, but they add
their actual symbols to this one graph. They do not create independently
invokable roots. The CLI introduces no serialized command schema, JSON or YAML
grammar, reflection binder, generated parser, second help or completion
grammar, generic command catalog, or public command-family interface.

## Capabilities and completion

Capability metadata is a narrow projection of the runtime graph, not a second
command model. Each advertised leaf registers its actual `Command` instance,
explicit canonical command path, and output schema version beside the leaf
declaration. After all symbols and actions are attached, grammar construction
walks the completed root without evaluating help formatters or completion
sources and proves that every registration:

- reaches the same command instance from the completed root;
- names that instance's ordinal-exact canonical path;
- identifies an actionable leaf; and
- is unique by command instance and by case-insensitive capability path.

Only the previously advertised fourteen leaves remain in the capabilities
projection. `check`, `capabilities`, and `completions script pwsh` remain valid
leaves but are not newly advertised. The default, long `--format json`, and
short `-f json` capabilities forms produce the same version-`1.0` JSON
contract, property ordering, values, line ending, exit status, and empty
standard error.

Static completion invokes the standard `System.CommandLine`
`[suggest:<cursor-position>]` directive against the same root graph and keeps
its dedicated newline-delimited standard-output protocol. Dynamic completion
sources remain attached to their actual argument symbols and are evaluated
only for an applicable completion request. Grammar construction, help, version,
capabilities metadata construction, and static completion perform no project
or manifest resolution, filesystem access, registry lookup, or Excel or VBIDE
automation. Terminal help, version, capabilities, and static completion never
execute an operational command action.

## Public grammar convergence

The graph converges on these command-local declarations without adding a
parallel compatibility grammar:

- `common-module add <modules>...` requires one or more requests and rejects
  an empty or whitespace-only supplied value before project, package, or
  filesystem resolution;
- Doctor and capabilities accept `-f` as the alias of `--format`;
- snapshot Build accepts `-o` as the alias of `--output`;
- Import `--from` and `--to` are required and nonempty;
- supplied Export `--project`, `--document`, `--from`, and `--to` values are
  nonempty, while omission retains the established defaults;
- supplied Build `--project`, `--document`, `--source-snapshot`, and `--output`
  values are nonempty, while omission retains the established selection and
  persistent-build defaults;
- supplied Publish `--project` and `--document` values are nonempty, while
  omission retains the established selection defaults;
- supplied Test `--project`, `--document`, and `--source-snapshot` values are
  nonempty, while omission retains the established selection and persistent-
  build defaults;
- an explicitly empty Test module or procedure does not collapse to an omitted
  selector, while exact nonempty VBA identifiers retain their existing
  Application validation;
- `reference add <references>...` and `reference remove <references>...`
  require one or more names and reject an empty or whitespace-only supplied
  name before project or registry resolution;
- Build source snapshot and output are `AllOrNone` actual symbols;
- Test procedure `Requires` module, while source snapshot `Conflicts` with
  no-build;
- Reference available `Conflicts` with no-resolve;
- Export from `Conflicts` with project and document; and
- Doctor environment scope `Conflicts` with project.

Shared relationships are limited to the closed internal forms `Requires`,
`Conflicts`, and `AllOrNone`. They attach actual symbols to the one graph and
do not become a general validation DSL. A grammar-valid parse binds once to a
shell-neutral closed command intent before domain resolution or side effects.

Issue #354 establishes the first sealed family module for Import and Export.
It declares their actual leaves on the shared root, reuses the canonical
project/document symbol constructor, and registers validation, binding,
actions, completion, and capability metadata beside those leaves. Import binds
one intent containing a non-null source directory and target workbook. Export
binds one closed union: either an explicit workbook source with an optional
destination, or optional project/document selection with an optional
destination. The action consumes only that bound intent; it does not inspect
option presence again.

The Application projection preserves the same distinction. Project Export and
explicit-workbook Export use separate request types, and Import receives
non-null source and target paths. Application commands therefore do not carry
option spellings or reinterpret nullable source paths as command modes.

Issue #355 establishes the sealed family module for Build and Publish. The
family attaches each leaf at its established root position, so Test remains
between them in help and completion order. Build binds one closed union: either
an optional project/document persistent build or an optional project/document
snapshot build with required non-null source and output paths. Publish binds
one project/document intent and declares neither output nor format. Actions
consume only these cached intents and never reconstruct option relationships.

Snapshot Build projects its paths through a
`SourceSnapshotBuildCommandRequest`; Application resolves both relative to the
request working directory before source capture or output safety validation.
The CLI family does not capture source, select a target, stage a workbook, or
commit output. Existing `WorkbookMaterializationIntent` variants and the
`WorkbookMaterializer` retain those responsibilities for persistent Build,
snapshot Build, and Publish.

Issue #356 establishes the sealed family module for Test at its established
root position between Build and Publish. Its single cached command intent
contains two independent closed unions: source selection is persistent build,
source-snapshot build with a non-null caller path, or existing-workbook no-
build; selector selection is all tests, one module, or one procedure with its
required module. The actual procedure and module symbols carry one `Requires`
relationship, the actual snapshot and no-build symbols carry one `Conflicts`
relationship, and an explicit timeout carries the positive-scalar rule.

The family retains `text` and `ndjson` as the only explicit formats and defers
omitted format and timeout defaults until the selected manifest is resolved.
Its action exhaustively projects the two closed unions to the established
`TestCommandRequest` and `WorkbookTestSelector` Application contracts; those
compatibility types do not become a second CLI-mode authority. Grammar failure
therefore precedes project resolution and workbook work, while a completed run
containing failed tests remains an ordinary text or NDJSON 1.2 result.

Issue #357 establishes the sealed family module for the Reference group and its
Add, List, and Remove leaves. List binds exactly one closed intent: selected
references with resolution by default, the stored selection without resolution
under `--no-resolve`, or the available catalog under `--available`. The actual
available and no-resolve symbols carry one `Conflicts` relationship. Add and
Remove bind ordered, one-or-more raw name lists and reject every empty or
whitespace-only supplied name through the shared argument-value rule before
project, registry, filesystem, or Excel work.

The family projects those intents to the established reference Application
services. Project and document selection therefore retains its ordinary
resolution, while bare available-catalog mode first tries implicit project
discovery and otherwise retains the environment inventory fallback. Dynamic
Add and Remove name completion remains attached to the actual variadic
arguments and keeps its existing candidates, ordering, and quiet failure
behavior, but is not evaluated for static command or option completion. No
registry scan is performed by graph construction, help, version, capabilities,
or grammar failure.

Issue #358 establishes the sealed family module for the CommonModules group and
its Add, List, and Update leaves. Add binds an ordered, one-or-more raw request
list as either an ordinary add or a force-authorized add; List binds optional
project/document selection and format, while Update binds optional project
selection and format. The actual variadic argument owns the shared nonempty-
value rule. `--force` is attached only to Add and selects a distinct closed
intent; it does not add a new general relationship form.

The family projects the cached intents to the established CommonModules
Application services. Application retains valid-request VBA-whitespace
normalization, package and required-reference planning, target-conflict policy,
source and manifest mutation, warnings, and result formatting. It no longer
filters an empty request list or reconstructs a command mode. Grammar failure
therefore precedes project, manifest, package, or filesystem access, while
`--force` remains the existing target authorization rather than an
unconditional overwrite or concurrency guarantee.

## Grammar-failure contract

A grammar failure exits `1`, writes nothing to standard output, and writes
exactly one canonical human-facing diagnostic followed by one short
command-local help hint to standard error. The diagnostic names canonical
command, argument, and option spellings rather than echoing an alias as the
contract identity. The hint directs the user to the command-local `--help`
invocation, or root help when no command path was admitted. No full help or
usage document is appended. Each item occupies one physical line and the second
line has the platform newline terminator. Supplied token text is escaped when
needed so it cannot add another physical line. The text remains human-facing;
the stable contract is the exit status, stream separation, two-line shape, and
canonical symbol identity rather than a machine-readable error schema.

Validation and execution use this strict phase order:

1. token and command/option parsing;
2. required-symbol and argument-cardinality checks;
3. value checks, including nonempty values, accepted values, and positive
   scalar bounds;
4. declared symbol relationships;
5. closed-intent binding;
6. domain resolution; and
7. side effects.

Only phases one through five can produce a grammar failure. If an earlier phase
has any defect, later phases do not run. Within one phase, the defect anchored
to the leftmost supplied token wins. A missing-symbol defect is anchored to its
leftmost triggering token; a command-wide missing requirement with no trigger
uses displayed help order. Remaining ties use displayed command, argument, and
option order. Relationship ties then use `Requires`, `Conflicts`, and
`AllOrNone` precedence in that order, followed by their declaration order.
A relationship is not evaluated when one of its symbols already has a parsing,
cardinality, or value defect.

Explicit valid help, the standalone version invocation, and completion remain
successful terminal modes on standard output. They do not enter domain
resolution or side effects. Explicit help suppresses only unsupplied required
symbols and incomplete command structure needed to render that help; defects in
supplied non-help input still use the grammar-failure contract, and help does not
bind a command intent. A grammar-valid Test result containing failed tests and a
grammar-valid Doctor result containing failed checks remain ordinary command
results with their established output schemas and exit rules; the grammar
router does not reinterpret them.

The central router and its closed cardinality, value, relationship, and intent-
binding primitives are established in issue #353. Import and Export migrate in
issue #354, and Build and Publish migrate in issue #355. The declarations in
issues #359 and #360 migrate onto the same primitives without creating
another router or compatibility grammar.

## Consequences

- Parsing, help, completion, binding, action routing, and capability identity
  cannot drift between separate command descriptions.
- Capability additions require an explicit actual-leaf registration and a
  completed-graph invariant check; adding another leaf does not advertise it
  implicitly.
- The CLI gains the additive `-f` aliases and Build `-o` alias without changing
  machine output or existing long spellings.
- Later command-family slices can move declarations behind small internal
  modules while preserving one root and one invocation boundary.
- Deterministic grammar diagnostics can replace library-dependent mixed output
  without changing grammar-valid command results or introducing a serialized
  failure schema.
- VbaDev remains independently buildable and has no dependency on a product
  that consumes its public process contract. The grammar graph, rule snapshot,
  router, and diagnostics are all VbaDev-owned; consumers may depend on the
  executable contract, but VbaDev never depends on those consumers.
