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

- `common-module add <modules>...` has one-or-more cardinality;
- Doctor and capabilities accept `-f` as the alias of `--format`;
- snapshot Build accepts `-o` as the alias of `--output`;
- Import `--from` and `--to` are required and nonempty;
- Build source snapshot and output are `AllOrNone`;
- Test procedure `Requires` module, while source snapshot `Conflicts` with
  no-build;
- Reference available `Conflicts` with no-resolve;
- Export from `Conflicts` with project and document; and
- Doctor environment scope `Conflicts` with project.

Shared relationships are limited to the closed internal forms `Requires`,
`Conflicts`, and `AllOrNone`. They attach actual symbols to the one graph and
do not become a general validation DSL. A grammar-valid parse binds once to a
shell-neutral closed command intent before domain resolution or side effects.

## Grammar-failure contract

A grammar failure exits `1`, writes nothing to standard output, and writes
exactly one canonical human-facing diagnostic followed by one short
command-local help hint to standard error. The diagnostic names canonical
command, argument, and option spellings rather than echoing an alias as the
contract identity. The hint directs the user to the command-local `--help`
invocation, or root help when no command path was admitted. No full help or
usage document is appended. The text remains human-facing; the stable contract
is the exit status, stream separation, two-item shape, and canonical symbol
identity rather than a machine-readable error schema.

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
resolution or side effects. A grammar-valid Test result containing failed tests
and a grammar-valid Doctor result containing failed checks remain ordinary
command results with their established output schemas and exit rules; the
grammar router does not reinterpret them.

## Consequences

- Parsing, help, completion, binding, action routing, and capability identity
  cannot drift between separate command descriptions.
- Capability additions require an explicit actual-leaf registration and a
  completed-graph invariant check; adding another leaf does not advertise it
  implicitly.
- The CLI gains the additive capabilities `-f` spelling without changing its
  machine output or existing long spelling.
- Later command-family slices can move declarations behind small internal
  modules while preserving one root and one invocation boundary.
- Deterministic grammar diagnostics can replace library-dependent mixed output
  without changing grammar-valid command results or introducing a serialized
  failure schema.
- VbaDev remains independently buildable and has no dependency on a product
  that consumes its public process contract.
