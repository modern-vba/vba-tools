# Host-event list and JSON schema 1.0

`vba-dev host-event list` is the environment-scoped, read-only producer of the
locally installed built-in UserForm Event catalog. It does not inspect a
project, open a user workbook, import source, mutate a manifest, save a
workbook, cache a catalog, or schedule refreshes.

## Invocation

```text
vba-dev host-event list [--format|-f <text|json>]
```

The command accepts no `--project` or `--document` selector and performs no
project discovery. Text is the default output. Use `--format json` for the
closed schema below. The capability document advertises:

```json
{
  "featureVersions": {
    "hostEvent.list": "1.0"
  },
  "commandSchemaVersions": {
    "host-event list": "1.0"
  }
}
```

## Exit behavior

| Exit code | Meaning |
| --- | --- |
| `0` | One complete catalog was produced after all owned Excel resources were released. |
| `1` | Discovery, validation, serialization, or required cleanup failed. No catalog is emitted. |
| `130` | Cooperative cancellation completed required cleanup. No catalog is emitted. |

The catalog is all-or-nothing. There are no partial, unverified, or
last-known-good entries. Diagnostics use stderr and never share stdout with a
partial JSON object.

## JSON result

The top-level object has exactly these properties; `baseTypeProvenance` is
omitted when unavailable:

```json
{
  "schemaVersion": "1.0",
  "sourceKind": "userForm",
  "intrinsicEventSourceName": "UserForm",
  "events": [
    {
      "identity": {
        "sourceName": "UserForm",
        "name": "Initialize"
      },
      "signature": {
        "parameters": []
      },
      "authoringAvailable": true,
      "existingHandlerRecognizable": true
    }
  ],
  "baseTypeProvenance": {
    "name": "_UserForm",
    "libraryGuid": "0d452ee1-e08f-101a-852e-02608c4d0bb4",
    "majorVersion": 2,
    "minorVersion": 0,
    "lcid": 0
  }
}
```

The result contains no project, document, source-template, workbook, component,
form-module, `VBProject.Name`, fingerprint, request, generation, or revision
identity. `sourceKind` is exactly `userForm`, and every Event identity's
`sourceName` is exactly the top-level `intrinsicEventSourceName` `UserForm`.

## Event signatures

Each Event has one case-insensitive identity in the generic UserForm source,
one structured signature, and both required availability flags. Events are
ordered by name using `OrdinalIgnoreCase` followed by `Ordinal`; parameters
retain observed ordinal order. Conflicting duplicate identities invalidate the
whole invocation.

One parameter has this shape:

```json
{
  "name": "Cancel",
  "type": {
    "kind": "intrinsic",
    "name": "Integer"
  },
  "passing": "byRef",
  "arrayShape": "scalar",
  "optional": false,
  "paramArray": false
}
```

`passing` is `byVal` or `byRef`; `arrayShape` is `scalar` or `array`.
`documentation` is an optional property of `signature`. Parameter `type` is one
of these closed shapes:

```json
{ "kind": "intrinsic", "name": "Long" }
```

```json
{
  "kind": "typeLib",
  "name": "Range",
  "libraryGuid": "00020813-0000-0000-c000-000000000046",
  "majorVersion": 1,
  "minorVersion": 9,
  "lcid": 0
}
```

```json
{ "kind": "unresolved", "displayName": "Vendor.Widget" }
```

Intrinsic types use canonical VBA names. TypeLib equality includes the type
name, library GUID, major and minor version, and LCID. Unresolved display text
is opaque and does not establish canonical equality.

`authoringAvailable` controls ordinary UserForm handler completion.
`existingHandlerRecognizable` controls association, navigation, and signature
guidance for an already-written handler. Structural Event presence and these
two behaviors are separate facts, and both flags must be known.

## Safety and cleanup boundary

One invocation owns exactly one hidden Excel process on a unique private Windows
desktop. The process is created suspended with atomic Job ownership; exact-PID
observation starts before its primary thread resumes, and native object-model
binding enumerates only that private desktop. There is no caller-desktop or
best-effort fallback. The shared binding path may first create and open one
generated macro-free `.xlsx` bootstrap artifact solely to expose the native
object model for the owned PID. That artifact contains no user bytes, is neither
inspected nor mutated, and is closed and deleted before catalog discovery
begins. Catalog discovery fails closed unless the owned process then has zero
open workbooks.

The catalog phase creates exactly one unsaved generated blank workbook and one
temporary empty UserForm. It verifies that the workbook inventory changes from
zero to one and that the generated workbook has no path. It adds no controls,
imports no source, enumerates no project worksheets or user controls, opens no
source template, and saves nothing. The temporary component, workbook, COM
references, process tree, and bootstrap artifact are released deterministically
on success, failure, and cancellation. The bootstrap workbook and catalog
workbook never overlap. A catalog is serialized only after the owned process is
proved released.

The command requires desktop Excel and trusted access to the VBA project object
model. A discovery failure is environment-level unavailable state; callers must
not fall back to a user template, reveal the private desktop, or manufacture an
empty Event surface. An unexpected dialog remains private and is converted to a
bounded failure with available exact-PID window and lifecycle-phase evidence.

## Consumer ownership

The command owns one discovery attempt only. The VS Code extension owns
trusted-activation scheduling, explicit refresh, status, cancellation, and one
in-session current catalog. It sends a versioned full-catalog replacement or
clear to the language server with its own monotonic revision and replays the
current state after a language-client restart. It persists no catalog across
extension activations. A failed explicit refresh retains an already-current
catalog, while a failed startup leaves the catalog unavailable.
