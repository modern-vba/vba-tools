# Host-class list and JSON schema 1.1

`vba-dev host-class list` is the document-scoped, read-only producer of
intrinsic form and document-class Event projections. It reports inspected Event
surfaces; it does not export generated Event source, mutate a manifest, cache a
projection, or schedule refreshes.

## Invocation

```text
vba-dev host-class list [--project <path>] [--document|-d <name>] [--format|-f <text|json>]
```

Project discovery and selection are the same as for other document-scoped
commands. A relative `--project` is resolved from the command working directory.
Without `--project`, discovery walks upward from that directory. Document names
are matched case-insensitively and emitted with manifest-declared casing. When
`--document` is omitted, the manifest `primaryDocument` is selected.

Text is the default output. Use `--format json` for the schema below. The
capability document advertises support as:

```json
{
  "featureVersions": {
    "hostClass.list": "1.0"
  },
  "commandSchemaVersions": {
    "host-class list": "1.1"
  }
}
```

## Exit behavior

| Exit code | Meaning |
| --- | --- |
| `0` | Enumeration is complete and every emitted class is resolved. A deletion-only workspace warning does not change this status. |
| `1` | The result is incomplete or unverified, or the invocation failed before a usable projection could be published. |
| `130` | Cooperative cancellation produced a released, schema-valid terminal partial result. |

An exit code alone does not determine whether stdout contains JSON. Once request
scope is valid and the owned Excel process is successfully released, class-local
and enumeration failures still produce one schema-valid JSON object and exit
`1`. Source preparation failure, failure to prove process release, and failures
before a terminal result emit no projection object. Diagnostics and warnings go
inside JSON; human-visible warning text is also written to stderr.

## JSON result

The top-level object has exactly these properties:

```json
{
  "schemaVersion": "1.1",
  "project": "C:\\absolute\\project",
  "document": "Book1",
  "sourceTemplate": "C:\\absolute\\project\\src\\Book1\\Book1.xlsm",
  "vbaProjectName": "VBAProject",
  "sourceTemplateFingerprint": "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
  "classEnumerationComplete": true,
  "complete": true,
  "classes": [],
  "diagnostics": [],
  "warnings": []
}
```

- `project` and `sourceTemplate` are canonical absolute paths fixed at
  invocation start.
- `document` is the manifest-resolved document name.
- `vbaProjectName` and `sourceTemplateFingerprint` are either both present or
  both absent. When present, they are the actual `VBProject.Name` observed from
  the inspected private copy and the uppercase SHA-256 fingerprint of the exact
  private-copy bytes. This is legacy observational transport for the current
  migration; Module Rename and project-name diagnostics ignore the pair and
  obtain containing-project authority from their own request-scoped static
  source-template read. Host Event and form-ownership consumers remain
  independent of the pair.
- `classEnumerationComplete` is true only for a complete, unambiguous intrinsic
  class identity set.
- `complete` is true only for a normally completed invocation with complete
  enumeration and only resolved class entries.

Diagnostics and warnings use `{ "code": string, "message": string }`.
Top-level diagnostic codes include `classEnumerationFailure`,
`operationCancelled`, and `inspectionStateUntrusted`. The housekeeping warning
code `inspectionWorkspaceRetained` includes the retained absolute path.

## Class entries

A resolved entry has this shape. `baseTypeProvenance` is omitted when it is not
available.

```json
{
  "identity": {
    "name": "Sheet1",
    "kind": "document"
  },
  "status": "resolved",
  "intrinsicEventSourceName": "Worksheet",
  "events": [],
  "baseTypeProvenance": {
    "name": "_Worksheet",
    "libraryGuid": "00020813-0000-0000-c000-000000000046",
    "majorVersion": 1,
    "minorVersion": 9,
    "lcid": 0
  }
}
```

`identity.kind` is `document` or `form`. Names preserve `VBComponent.Name`
casing and compare case-insensitively within a kind. Base-type provenance is
navigation-only evidence; its absence does not invalidate an inspected Event
surface. Required `intrinsicEventSourceName` is the inspected VBE Object-box
qualifier used in `<source>_<Event>` procedures. It is never inferred from the
component identity, kind, source file name, enumeration ordinal, temporary
path, or base-type provenance.

An unverified entry contains no partial Event data:

```json
{
  "identity": {
    "name": "Sheet1",
    "kind": "document"
  },
  "status": "unverified",
  "reasonCode": "signatureReadFailure",
  "message": "The complete Event signature could not be read."
}
```

The closed class-local reason vocabulary is:

- `eventEnumerationFailure`
- `intrinsicEventSourceNameReadFailure`
- `signatureReadFailure`
- `availabilityReadFailure`
- `inspectionTimeout`
- `inspectionAborted`
- `cancelled`
- `inspectionFailure`

Messages are human-readable context and are not a machine contract.

## Event signatures

```json
{
  "name": "BeforeClose",
  "parameters": [
    {
      "name": "Cancel",
      "type": {
        "kind": "intrinsic",
        "name": "Boolean"
      },
      "passing": "byRef",
      "arrayShape": "scalar",
      "optional": false,
      "paramArray": false
    }
  ],
  "documentation": "Occurs before the workbook closes.",
  "authoringAvailable": true,
  "existingHandlerRecognizable": true
}
```

`documentation` is omitted when no inspected documentation is available.
`passing` is `byVal` or `byRef`; `arrayShape` is `scalar` or `array`.
Parameters retain inspected ordinal order. Structural Event existence,
ordinary authoring availability, and existing-handler recognition are separate
facts. A resolved Event always has inspected boolean availability values;
unknown availability makes the class unverified instead of inventing a value.

Parameter `type` is one of three discriminated shapes:

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

Intrinsic types use canonical VBA names. TypeLib equality includes name, GUID,
major/minor version, and LCID. Unresolved display text remains opaque evidence;
consumers must not infer canonical equality from it. An unresolved type remains
valid evidence in a resolved class and can coexist with top-level
`complete: true`.

Same-name Event observations are not overloads. They coalesce only when the
parameter count, each canonical type, passing mechanism, array shape,
Optional/`ParamArray` metadata, and both availability values agree. Unresolved
types never establish canonical equality, including equal display text. A
callable or availability conflict makes the entire class unverified.
Presentation-only differences are resolved without combining fields: prefer a
whole observation with nonempty documentation, then choose the minimum complete
tuple of Event name, ordered parameter names, and documentation under
case-insensitive ordinal comparison followed by ordinal comparison.

An inspected `false`/`false` availability pair is a valid structural Event and
does not make its class unverified.

## Canonical ordering

Text and JSON use the same ordering:

1. document classes, then form classes;
2. class names by case-insensitive ordinal comparison, then casing-preserving
   ordinal comparison;
3. Event names by the same comparisons; and
4. parameters in inspected ordinal order.

Case-insensitive duplicate class identities of the same kind are omitted rather
than coalesced. Enumeration becomes incomplete, `classEnumerationFailure` is
reported, and other unique identities continue through inspection.
Resolved and unverified entries are not partitioned by status. The same name in
different component kinds remains two distinct identities.

## Partial and cancellation semantics

Class-local failure preserves independently completed classes and represents the
failed class as `unverified`. An ordinary identity-enumeration read failure sets
`classEnumerationComplete: false`. If a timeout, process loss, or another
failure makes shared Excel/VBIDE state untrustworthy, no replacement Excel
process is started: independently proven earlier results may remain, the causal
class uses its specific reason, known later classes use `inspectionAborted`, and
`inspectionStateUntrusted` is reported.

Cooperative cancellation keeps completed classes, marks known unfinished
classes `cancelled`, adds `operationCancelled`, and publishes only after process
release. Classes not discovered before an incomplete enumeration remain unknown
and absent.

## Safety and cleanup boundary

One invocation creates a unique GUID-named inspection workspace and copies the
selected source template once. After that copy, inspection does not reread or
rehash the original template. The private copy is package-preflighted to reject
XLM macro-sheet or dialog-sheet content and is opened read-only only after Excel
automation security is force-disabled and Excel Events are disabled. The
inspection imports no project source, changes no references, emits or persists
no generated host Event source, never saves the copy, and does not modify the
source set, manifest, or source template. Inspection may transiently ask VBE to
create Event procedures and add probe procedures inside the private copy; it
must restore the complete original CodeModule text exactly. A rollback failure
makes shared inspection state untrustworthy and prevents a complete result.

Excel ownership is process-exact. The process bootstrapper may use one generated
macro-free `.xlsx` solely to obtain the native window needed to bind the COM
`Application` to the exact newly Job-owned process. This is the only permitted
workbook before the source-template private copy: it contains no project bytes,
is not inspected, and is closed and deleted before the private copy is opened.
Force-disabled automation security and disabled Excel Events are verified after
that bootstrap is closed and before the private-copy `Workbooks.Open`. The
command also fails closed unless there are zero open workbooks immediately
before that open and exactly one afterward. The current binding path does not
establish that process-startup add-ins, XLSTART content, or startup Events could
not run before those controls were set.

Projection serialization is held until the owned process is proved released.
Failure to prove release invalidates the invocation and retains the workspace
for diagnosis. After successful release, workspace deletion is retried a
bounded number of times. If deletion is the only remaining failure, the
projection and exit status are preserved and the retained absolute path is
reported.

The process-start deadline is 30 seconds. Workbook open uses the ordinary
300-second deadline or its manifest override. Identity enumeration and each
complete class inspection receive separate 60-second deadlines. Cooperative
cleanup receives five seconds before owned-process termination and proof.

## Consumer ownership

The command owns one inspection invocation only. Schema `1.1` has no generation,
request ID, file modification time, or inspection timestamp. Its optional
project-name/fingerprint pair binds only the inspected VBA project name to the
exact inspected bytes; it is not consumer freshness state.
Consumers choose refresh triggers, retries, and scheduling; associate results
with their own request generation; and own any in-memory or durable
last-known-good state.

A consumer should reject a stale generation or mismatched `project`, `document`,
or `sourceTemplate` context as a whole. It may commit resolved entries
independently. An unverified identity retains only the same-identity
last-known-good projection, or remains indeterminate when none exists. When
`classEnumerationComplete` is true, an absent prior identity is authoritatively
deleted even if top-level `complete` is false. When enumeration is incomplete,
absence is unknown and retains last-known-good state. These decisions come from
the schema fields, never by interpreting diagnostic messages or codes.

Last-known-good data is advisory projection evidence. It does not authorize
semantic diagnostics or workbook/source mutation. Invocation failure, schema
mismatch, and process-release failure provide no authoritative projection or
identity-deletion state.
