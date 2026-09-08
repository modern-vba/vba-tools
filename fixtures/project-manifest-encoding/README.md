# ProjectManifest byte conformance

`cases.json` is a data-only corpus shared by the VbaDev, VbaLanguageServer,
and VscodeExtension tests. `base64` stores the exact disk bytes; `accepted`
classifies byte admission before JSON parsing. Accepted cases include the
exact decoded `text`, containing BMP, supplementary Unicode, and a literal
U+FFFD. Consumers must not treat that valid character as replacement fallback.

The cases cover UTF-8 with/without BOM, both BOM-marked UTF-16 byte orders,
UTF-32 prefix precedence, BOM-less UTF-16 including ASCII-only input,
malformed/truncated signatures, invalid UTF-8 sequences with/without BOM,
unpaired/reversed UTF-16 surrogates, and odd-length UTF-16 input. Rejected
payloads within a project name must fail even if replacement decoding would
produce valid JSON and a structurally valid manifest.

This corpus describes ProjectManifest bytes, not VBA source encoding or
editor snapshot transport. See ADR 0046.
