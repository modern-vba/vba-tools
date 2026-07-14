---
status: superseded
superseded_by: 0008-migrate-language-server-to-csharp-before-reference-catalogs
---

# Host definition sources

This ADR described the removed TypeScript language-server host-application
configuration model. It is retained only as historical context.

The current C# language server does not expose host-application settings and
does not use the removed TypeScript host-catalog runtime. Future
reference-aware behavior is expected to come from `ProjectManifest`
`VbaProjectReferenceSelection` and reference catalogs, as described by the
C# migration plan.
