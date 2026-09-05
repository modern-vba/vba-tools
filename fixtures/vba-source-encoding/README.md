# VBA source encoding conformance fixtures

This repository-neutral corpus is static test data for the source-byte rules in
[ADR 0037](../../docs/adr/0037-centralize-vba-source-admission.md) and issue #335.
Products read the same vectors through their own test loaders. This directory
contains no production API, DTO, executable helper, or shared test assembly.

The 32 cases cover ASCII under ACP 932, 1252, and 65001; ambiguous BOM-less
UTF-8 bytes interpreted as ACP; ordinary ACP text; canonical BOM-less UTF-8
under ACP 65001; each supported BOM under all three ACPs; unsupported UTF-32
and all four UTF-7 signature prefixes; truncated and malformed Unicode;
invalid ACP bytes; a noncanonical CP932 mapping; and authoring text that cannot
be projected losslessly into the operation-fixed ACP.

## Schema and assertions

`cases.json` has `schemaVersion: 1` and a `cases` array. Each case contains:

- `id`: stable case name for test discovery and failure reports.
- `activeCodePage`: the operation-fixed ACP, explicitly 932, 1252, or 65001.
  It is test input and must not be replaced by the machine's current ACP.
- `fileName`: the selected source file name.
- `bytesBase64`: authoritative, exact source bytes, including any BOM.
- For a successful authoring-byte decode, `expectedText` and
  `expectedEncoding`: exact decoded Unicode and the canonical transport token.
  Tokens are `windows-932`, `windows-1252`, `utf8`, `utf8bom`,
  `utf16le`, and `utf16be`. ACP 65001 uses `utf8`, never
  `windows-65001`.
- For authoring-byte rejection, `expectedFailure: true`, with no expected
  text or encoding. No alternate decoder may make the source admissible.
- Optional `expectedProjectionFailure: true` on an otherwise decodable case:
  authoring bytes and Unicode are valid, but lossless VBE projection through
  the stated ACP must fail. A byte-decoding-only consumer still admits that
  Unicode; a materializing consumer rejects it before target mutation.

Successful decoding must reproduce `bytesBase64` exactly when the expected
text is encoded in its declared authoring encoding, including the BOM.
Successful VBE projection must preserve exact Unicode when encoded and decoded
through the fixed ACP. The corpus does not define exception types, message text,
source-provider interfaces, or product-specific result objects.

Successful cases use a complete minimal `Module1.bas` module with
`Attribute VB_Name = "Module1"`, `Option Explicit`, and a comment. The
embedded VBA text uses CRLF, preserved exactly in JSON string escapes.
Most rejected payloads also retain the ASCII module prefix so fallback cannot
be justified by unrelated syntax. The truncated UTF-8 signature intentionally
contains only `EF BB`: signature rejection must precede module parsing.
Binary `.frx` copying, `.cls`/`.frm` structure, inventory capture,
read counts, ACP capture, and workbook mutation are product-local integration
test concerns rather than encoding-vector fields.

## Byte provenance and assumptions

These are hand-selected conformance vectors, not captured user files or samples
copied from another product. Base64 was calculated once with .NET encoders and
persisted as static JSON. The JSON and this README are UTF-8 without BOM, use LF,
and end with a newline. No runtime generator is needed or supplied.

Verification used PowerShell 7.6.5 on Windows with .NET 10.0.11.
Windows code pages 932 and 1252 used explicit exception fallbacks for both
encoding and decoding. UTF-8 and UTF-16 used strict Unicode decoders. A product
must use Windows CP932 mappings, not assume every codec named Shift_JIS has
identical mappings.

The discriminating bytes and failure reasons are:

- The ambiguous source comments contain `C3 A9`. UTF-8 interprets those bytes
  as U+00E9, while ACP 1252 yields U+00C3 U+00A9 (`Ã©`) and ACP 932 yields
  U+FF83 U+FF69 (`ﾃｩ`). Both ACP interpretations reproduce the original
  bytes and must win when the file has no BOM.
- UTF-8, UTF-16 LE, and UTF-16 BE use `EF BB BF`, `FF FE`, and
  `FE FF`. The expected text omits that initial signature.
- Unsupported UTF-32 uses `FF FE 00 00` or `00 00 FE FF`. The complete
  UTF-32 LE signature must not be treated as UTF-16 LE.
- Unsupported UTF-7 starts with `2B 2F 76` followed by one of
  `38`, `39`, `2B`, or `2F`. All four prefixes must be rejected.
- Malformed UTF-8 payloads contain `C3 28`; malformed UTF-16 LE ends in
  one unmatched byte; malformed UTF-16 BE ends in the lone high surrogate
  `D8 00`. The ACP-932 failure contains a lead byte `81` followed by CR.
- The noncanonical CP932 comment contains `87 90`. A permissive Windows
  CP932 decoder maps it to U+2252 (`≒`), whose canonical encoding is
  `81 E0`. That conversion would violate exact-byte round-trip equality.
  On the verification runtime, the strict decoder already rejects `87 90`.
  Either strict rejection or an exact-byte round-trip rejection satisfies this
  case; accepting and canonicalizing the original bytes does not.
- Supported BOMs can admit `😀` under ACP 932 or `日本語` under ACP 1252
  as authoring Unicode, but neither text can pass strict projection to that ACP.
  These are projection failures, not alternate-encoding detection cases.

This is a focused corpus, not an exhaustive code-page mapping table or a list
of every historical Unicode signature. New edge cases should add data here
when multiple products need the same semantic proof, while their loaders and
behavior remain product-owned.
