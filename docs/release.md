# Release

This document is the release runbook for VBA Tools. Follow it when preparing a
VS Code Marketplace release, the matching GitHub Release, and the standalone
`vba-dev` artifact.

## Release Policy

- Treat the VS Code Marketplace extension as the primary release unit.
- Use `package.json` `version` as the Marketplace extension version.
- Use GitHub Releases as the public version history and artifact archive.
- Tag extension releases as `vba-tools-vX.Y.Z`.
- Reserve `vba-dev-vX.Y.Z` tags for independently versioned standalone CLI
  releases so extension and CLI tag namespaces cannot collide.
- Publish `0.1.0` as the initial Marketplace pre-release. Use odd minor
  versions for pre-release channels and even minor versions for stable channels;
  the first planned stable line is `0.2.x`.
- Pass `--pre-release` when packaging and publishing a pre-release. Marketplace
  pre-release and stable uploads must always use distinct numeric versions.
- Publish the initial extension as a `win32-x64` platform-specific VSIX. Add a
  different Marketplace target only after its bundled runtimes have dedicated
  build and verification coverage.
- Treat the merged release preparation PR as the source of truth for the
  extension version and release notes. It must update both `package.json` and
  `package-lock.json` before the release tag is created.
- Prepare that PR with a repository-owned, secretless `npm run release:prepare`
  command rather than granting a release-notes bot write credentials. The
  command must accept the extension version and channel plus the bundled
  `vba-dev` version, enforce the version and tag policies in this document,
  update versioned files, and generate the draft changelog section. It must not
  create a commit, tag, or remote pull request.
- Use root `CHANGELOG.md` as the reviewed extension release-notes source and
  `tools/vba-dev/CHANGELOG.md` as the independently versioned CLI history.
  Generate later version sections from Conventional Commits since the previous
  tag in the matching namespace, then edit known limitations, requirements, and
  other user-facing context in the release preparation PR.
- Curate the initial `0.1.0` changelog sections as user-facing summaries because
  no prior namespace tags exist; do not dump the complete commit history.
- Do not let `vsce publish` increment the version or create a release commit.
  Publish the already-versioned, verified VSIX with `--packagePath`.
- Run non-Excel release verification on a standard GitHub-hosted Windows runner.
  Keep real Excel/VBIDE verification as a maintainer-run gate rather than
  maintaining a self-hosted runner for this public repository.
- Treat creation of a protected `vba-tools-vX.Y.Z` tag as the final human
  release authorization. After that tag is pushed, publish automatically
  without a second manual environment approval.
- Require release tags to be annotated. Their structured message records the
  Marketplace channel, exact commit verified by the maintainer, Windows Excel
  verification result, and clean Windows smoke result. Tag signing is not an
  initial requirement.
- Protect the `vba-tools-v*` namespace with a GitHub tag ruleset limited to
  authorized maintainers. Scope the Marketplace publishing job and its Entra
  federated credential to a release environment that accepts only those tags.
- Run release automation in GitHub Actions on standard GitHub-hosted runners
  while this repository is public. Do not opt in to paid larger runners or
  billable workflow services without explicit maintainer approval.
- Run release jobs on the explicit `windows-2025` standard runner label rather
  than `windows-latest` so runner-image migrations are reviewed changes.
- Declare Node.js 24 and npm 11 as the repository JavaScript toolchain. Pin the
  npm release through `packageManager`, restore dependencies with `npm ci`, and
  update the pin only through a reviewed dependency change.
- Pin the .NET 10 SDK through `global.json` to the selected feature band and
  allow patch roll-forward only. Keep JavaScript, .NET, and runner versions in
  release logs.
- Reference third-party and GitHub-authored Actions by full commit SHA. Use
  Dependabot pull requests to propose npm and GitHub Actions updates rather than
  consuming moving action tags in release workflows.
- Authenticate automated Marketplace publishing with Microsoft Entra ID and
  GitHub OIDC workload identity federation, then publish with
  `vsce --azure-credential`. Do not store an Azure DevOps global PAT in CI.
- Use a dedicated user-assigned managed identity named
  `vba-tools-marketplace-publisher` in the
  `modern-vba-release-identities` Azure resource group. Federate it only with
  the `marketplace-release` GitHub Environment for this repository and grant
  that identity Marketplace Contributor access to the `modern-vba` publisher.
- Enable GitHub immutable releases before the first publication. Publishing the
  staged draft must make its release assets and associated tag immutable and
  produce GitHub's release attestation.
- Generate GitHub build provenance attestations for the VSIX and standalone
  `vba-dev` ZIP, and retain `SHA256SUMS` as the simple offline integrity check.
- Do not require Authenticode signing for the initial release. Document that a
  directly downloaded standalone executable may display an unknown-publisher
  warning and show users how to verify its checksum and GitHub attestation.
- Keep `vba-dev` versioning and command contracts separate from the extension
  version, as described in ADR 0007. The extension release still bundles one
  tested `vba-dev.exe`.
- Attach a standalone ZIP containing the exact bundled `vba-dev` binary to
  every extension GitHub Release, even when its independent CLI version has not
  changed since the previous extension release. This keeps each extension
  release self-contained and preserves the tested extension-to-CLI pairing.
- When `vba-dev` changes independently of the extension, publish it under its
  reserved `vba-dev-vA.B.C` tag and GitHub Release as well.
- Keep `vba-language-server` bundled inside the extension for now. Record its
  version in release metadata, but do not publish a standalone archive or
  reserve an independent release tag until supporting external LSP clients is
  an explicit product commitment.
- Build the VSIX, standalone `vba-dev` ZIP, and `SHA256SUMS` once for each
  release tag, then stage those exact files on a draft GitHub Release. Treat
  the draft release as the artifact source of truth for Marketplace publishing
  and any retry; publishing jobs must not rebuild release artifacts.
- Serialize release workflows for the repository and do not cancel an active
  release when another run is requested. A protected tag starts a new release;
  manual dispatch may only resume an existing tagged draft release.
- The initial `vba-tools` `0.1.0` pre-release is blocked on GitHub issue #243
  and must include the independently versioned standalone `vba-dev` Windows x64
  ZIP after that issue's acceptance criteria pass.
- Block the initial Marketplace release until its listing metadata and packaged
  support documents pass the Marketplace readiness checks in this runbook.
- Do not move, delete, or reuse a release tag after it has triggered release
  automation, even when Marketplace publication has not completed. Fix release
  content with a new patch release.
- Do not automatically unpublish a Marketplace version or GitHub Release after
  publication. Correct ordinary defects with a new patch release. Reserve a
  manual maintainer-approved withdrawal for credential exposure, a legal
  requirement, artifact compromise, or a defect that risks user data.
- Keep `main` releasable. Introduce `release/vX.Y` branches only when hotfix
  maintenance requires them.

## Release Inputs

Before starting, decide:

- the extension version, such as `0.0.1`;
- the release tag, such as `vba-tools-v0.0.1`;
- the Marketplace channel, `pre-release` or `stable`;
- the bundled and standalone `vba-dev` version;
- known limitations that must appear in the GitHub Release notes;
- any breaking changes in the extension, `vba-dev` command contract, project
  manifest, or language-server behavior.

## Marketplace Readiness

Complete these checks before the initial release and verify them again whenever
listing metadata changes:

- Keep the existing 256-by-256 PNG icon in the packaged extension.
- Declare the MIT license in `package.json` and include the root `LICENSE`.
- Declare the repository homepage and GitHub Issues URL in `package.json`.
- Add concise Marketplace search keywords relevant to VBA, Excel, language
  tooling, testing, and debugging without exceeding Marketplace limits.
- Declare `pricing` as `Free`.
- Configure a dark `galleryBanner` color that matches the existing icon.
- Add `SUPPORT.md` with the supported contact and issue-reporting paths.
- Route bugs, feature requests, and general questions to GitHub Issues. Enable
  GitHub private vulnerability reporting and route security reports there
  instead of to public issues.
- Do not advertise a support email, paid support, GitHub Discussions, or a
  response-time commitment unless maintainers adopt one explicitly later.
- Include `README.md`, `CHANGELOG.md`, `LICENSE`, and `SUPPORT.md` in the VSIX.
- Validate packaged Markdown links and fail release verification on broken local
  links or references to files excluded from the VSIX.

## Reproducible Toolchain

The initial release automation uses:

- the `windows-2025` standard GitHub-hosted runner;
- Node.js 24 with npm 11, declared in `package.json`;
- `npm ci` with the committed `package-lock.json`;
- .NET SDK `10.0.300`, selected by `global.json` with patch-only roll-forward;
- full commit SHAs for every referenced GitHub Action.

Configure Dependabot for the `npm` and `github-actions` ecosystems. Toolchain
and action update pull requests must pass the same verification as product-code
changes before their pins are updated.

## One-Time Publishing Identity Setup

Create the release identity without client secrets or stored PATs:

1. Create the `modern-vba-release-identities` Azure resource group and the
   `vba-tools-marketplace-publisher` user-assigned managed identity in the
   maintainer's active Azure subscription.
2. Grant the managed identity only the Azure Reader scope required for login
   and identity discovery.
3. Create the `marketplace-release` GitHub Environment and restrict deployment
   branches and tags to protected `vba-tools-v*` tags.
4. Add a federated credential whose subject is
   `repo:modern-vba/vba-tools:environment:marketplace-release`.
5. Store the subscription ID, tenant ID, managed-identity client ID, and managed
   identity resource ID as non-secret environment variables.
6. Resolve the identity's Azure DevOps/Marketplace profile ID once and add that
   identity to the `modern-vba` Marketplace publisher with Contributor access.
7. Verify OIDC login and a read-only Marketplace identity probe before enabling
   tag-triggered publication.

The maintainer may need to complete interactive Azure and Marketplace sign-in,
MFA, consent, and the publisher-member assignment. Do not create a client secret
as a workaround for an incomplete interactive setup.

## Prepare the Release Change

Run the repository-owned release preparation generator with an explicit
extension version, Marketplace channel, and bundled `vba-dev` version. The
command-line syntax is part of the release-automation implementation and must
remain usable both locally and on a standard GitHub-hosted runner.

The generator must:

1. Require a clean worktree and validate the requested extension and CLI
   versions as SemVer.
2. Enforce odd extension minor versions for `pre-release`, even extension minor
   versions for `stable`, and the `vba-tools-vX.Y.Z` tag namespace.
3. Update `package.json` and `package-lock.json` to the same extension version.
4. Update `VbaDevReleaseVersion` in `tools/vba-dev/Directory.Build.props` when
   requested without coupling it to the extension version. Confirm that
   `vba-dev --version`, capabilities `toolVersion`, and .NET informational
   metadata all report the same canonical three-part SemVer.
5. Generate a root `CHANGELOG.md` section from Conventional Commits since the
   previous `vba-tools-v*` tag without overwriting maintainer-authored release
   context.
6. When the requested CLI version is new, generate
   `tools/vba-dev/CHANGELOG.md` from commits since the previous `vba-dev-v*`
   tag. Do not duplicate CLI changes when the bundled CLI version is unchanged.
7. Validate the expected VSIX, standalone ZIP, and checksum filenames before
   leaving the release preparation diff for review.

After generation:

1. Review and edit the changelog's known limitations, requirements, and other
   user-facing context.
2. Update `vba-language-server --version` output only when the language-server
   release identity changes.
3. Update user-facing docs when commands, settings, requirements, or known
   limitations change.
4. Open and review the release preparation PR through the normal repository
   workflow.
5. Commit the release preparation with an English Conventional Commit message.

## Local Verification

Run the normal client, C# devtool, language-server, syntax-core, and packaging
suite during development:

```powershell
npm test
```

Before preparing an artifact, run the non-Excel release verification surface:

```powershell
npm run verify:release
```

This runs the client, Extension Host, C# unit, language-server, explicit syntax
core, packaging, and compatibility suites, then republishes both bundled
executables and verifies the planned VSIX. It intentionally does not opt in to
real Excel automation.

On a configured Windows host with desktop Excel and trusted VBIDE access, run
the complete release verification including the serialized real Excel suite:

```powershell
npm run verify:release:windows-excel
```

The explicit `test:windows-excel-integration` step sets the required opt-in
environment variable and filters to `Category=WindowsExcelIntegration`. Do not
add it to ordinary `npm test`; the suite owns visible Excel/VBE processes and may
wait for interactive modal prompts.

Run this gate against the exact commit intended for the release tag. Record the
commit SHA, command result, and clean Windows smoke result in the release
preparation PR or its post-merge comment. If the tagged content changes, rerun
the gate before creating the tag.

Run package verification:

```powershell
npm run package:verify
```

`package:verify` publishes the Windows x64 CLI and language server into the
extension bundle layout, compiles the extension, checks the VSIX file list, and
runs bundled executable probes:

- `bin/vba-dev/win-x64/vba-dev.exe` must be present.
- `bin/vba-language-server/win-x64/vba-language-server.exe` must be present.
- `tools/vba-dev/**` must be absent from the VSIX file list.
- `tools/vba-language-server/**`, `server/**`, and `client/src/**` must be
  absent from the VSIX file list.
- bundled runtime sidecars such as `.dll`, `.deps.json`, `.runtimeconfig.json`,
  and `.pdb` files must be absent from `bin/**`.
- `package.json`, `client/out/extension.js`, and `vba-dev-contract.json` must be
  present.
- packaged metadata must point `main` at the compiled extension, activate
  dynamic VBA debug resolution, contribute the supported launch schema and user
  commands, and keep `module` and `procedure` atomic.
- the bundled `vba-dev.exe` must answer `capabilities --format json` with the
  command contract required by the extension.
- the advertised `debug-adapter --stdio` entry point must start successfully and
  match the required adapter protocol version.
- the bundled C# language server must run directly and answer `--version`.
- `VbaDev.Cli.csproj` must publish a Windows x64 self-contained single-file
  executable.
- `VbaLanguageServer.Cli.csproj` must publish a Windows x64 self-contained
  single-file executable.

Create the VSIX:

```powershell
npm run package -- --target win32-x64
```

`npm run package` runs the same verification before `vsce package`.

Probe bundled executables directly:

```powershell
bin/vba-dev/win-x64/vba-dev.exe --help
bin/vba-dev/win-x64/vba-dev.exe --version
bin/vba-dev/win-x64/vba-dev.exe capabilities --format json
bin/vba-language-server/win-x64/vba-language-server.exe --version
```

## Clean Windows Smoke

This smoke is required for a release that introduces or changes the native VBE
debug workflow. For a release that does not affect debugging, an unavailable
clean Windows environment may be recorded as `Clean Windows smoke: not run` in
the GitHub Release notes.

When the environment is available, run this smoke on Windows 11 with desktop
Excel installed and without a separately installed .NET runtime.

1. Install the generated VSIX.
2. Open a workspace containing exported `.bas`, `.cls`, or `.frm` files.
3. Confirm the extension activates without a platform-support error.
4. Confirm completion or document symbols are served by the bundled C# language
   server.
5. Run `Format Document` on a VBA file.
6. Open a workbook-backed sample workspace that contains `vba-project.json`.
7. Enable trusted access to the VBA project object model in Excel.
8. Run `VBA Tools: Doctor`.
9. Run `VBA Tools: Build`.
10. Run `VBA Tools: Test`.
11. Confirm Test Explorer shows workbook-backed test nodes.
12. Run the Test Explorer default `Run Tests` profile.
13. Create or open a debug sample whose standard module contains `Option Private
    Module` and a public parameterless `Sub` that records a harmless completion
    marker.
14. Set an enabled ordinary line breakpoint on an executable statement in that
    procedure.
15. Press F5 without a saved launch configuration. Confirm the packaged dynamic
    configuration builds the sample, opens a dedicated visible Excel/VBE
    process, transfers the breakpoint, and stops in native VBE Break mode.
16. Continue from the VBE. Confirm the completion marker is recorded and VS Code
    keeps the debug session active after procedure completion.
17. Exit the owned Excel process. Confirm VS Code displays the final process-exit
    termination message and no owned Excel process remains.
18. Repeat with a saved `launch.json` target. Confirm Restart performs a fresh
    save/build/open/transfer/run and Stop can terminate the session while an
    interactive prompt is visible without leaving an orphan.
19. Run the bundled language-server executable directly:

    ```powershell
    .\bin\vba-language-server\win-x64\vba-language-server.exe --version
    ```

Treat failures in the required native VBE smoke as release blockers. If an
unrelated release legitimately skips the smoke, record the reason and the last
successful `verify:release:windows-excel` result in the release notes.

## Commit, Tag, and Push

After verification passes:

```powershell
git status --short --branch
git fetch origin main
npm run release:tag -- --version X.Y.Z --channel pre-release
git push origin vba-tools-vX.Y.Z
```

`release:tag` must require a clean worktree, confirm that `HEAD` is the verified
commit and exactly matches `origin/main`, validate version and channel metadata,
reject an existing tag, and create an annotated tag with this message contract:

```text
VBA Tools X.Y.Z

Channel: pre-release
Windows-Excel-Verification-Commit: <full commit SHA>
Windows-Excel-Verification-Result: pass
Clean-Windows-Smoke: pass
```

The clean Windows smoke value may be `not-required` only when the runbook allows
the smoke to be skipped, and the tag message must then include a concise
`Clean-Windows-Smoke-Reason` trailer. The initial `0.1.0` release requires
`pass` for both checks.

The tag-triggered workflow must reject a lightweight tag, an unrecognized
trailer, a non-pass Windows Excel result, or a verification commit that differs
from the tag target.

Do not force-push, delete, or recreate a release tag after pushing it. A tag is
an immutable link between one commit, its verification evidence, and its
artifacts.

## GitHub Release

Create a draft GitHub Release from the release tag before Marketplace publish.
Build each release asset once, upload it to the draft, and verify every uploaded
file against `SHA256SUMS`. Keep the release as a draft until Marketplace
publishing succeeds.

The Marketplace job must download the VSIX from the draft release and verify
its checksum before calling `vsce publish`; it must not package another VSIX
from the source checkout. A retry or explicitly resumed release must use the
same draft asset and checksum. If the draft asset is missing or disagrees with
`SHA256SUMS`, stop the release instead of rebuilding it in the publishing job.

Generate GitHub build provenance attestations for the VSIX and standalone CLI
ZIP before publication. After Marketplace publication succeeds, publish the
draft as an immutable GitHub Release and verify the resulting release
attestation. The release workflow needs only the scoped `attestations: write`,
`id-token: write`, and release-content permissions required by these steps.

## Automated Release State Machine

The protected `vba-tools-v*` tag workflow performs these states in order:

1. Validate the tag, extension version, channel, bundled CLI version, exact
   commit, manual Excel-gate evidence, and absence of a conflicting release.
2. Run hosted verification and build the VSIX and standalone CLI ZIP once.
3. Create or validate the draft GitHub Release, upload the assets and
   `SHA256SUMS`, and create build provenance attestations.
4. Download the staged VSIX, verify its checksum, authenticate through the
   release environment, and publish it to the Marketplace.
5. Poll the Marketplace until the expected publisher, extension version,
   platform target, and pre-release/stable channel are visible.
6. Publish the draft as an immutable GitHub Release and verify its release
   attestation.

Use a repository-wide release concurrency group with `cancel-in-progress:
false`. The tag-triggered path is the only path allowed to create a new draft or
build artifacts.

Provide a manual `workflow_dispatch` resume path that accepts an existing
release tag. It must verify and reuse that tag's draft assets, skip Marketplace
publication when the exact expected version is already visible, and continue
post-publication verification and finalization. It must fail instead of
building when an expected asset or checksum is absent.

Release title:

```text
VBA Tools X.Y.Z
```

Recommended assets:

- `vba-tools-win32-x64-X.Y.Z.vsix`
- `vba-dev-win-x64-A.B.C.zip`, where `A.B.C` is the independently versioned CLI
  bundled in this extension release, even if that version is unchanged
- `SHA256SUMS`

`vba-language-server` is intentionally not a standalone release asset. Its
version remains listed in release notes so a published VSIX can be reproduced
and diagnosed.

Standalone `vba-dev` artifact:

```powershell
npm run package:devtool
```

The command emits `.tmp/release/vba-dev-win-x64-A.B.C.zip`, extracts it into a
clean temporary directory, and verifies its canonical version, capabilities,
command contract, exact executable identity, PDB set, CLI README, root MIT
license, and command contract document. Keep the ZIP unchanged when adding it
beside the VSIX to the release artifact set and `SHA256SUMS`.

Release notes should use this structure:

```md
## Added

## Changed

## Fixed

## Known Limitations

## Requirements

## Bundled Tools

- vba-dev:
- vba-dev contract:
- vba-language-server:

## Artifacts

- VSIX:
- Standalone vba-dev:
```

Include breaking changes under a clearly labeled `Breaking Changes` section.
Use the reviewed `CHANGELOG.md` version section as the core of these notes; add
artifact metadata and verification results without rewriting the change list.
Record the bundled CLI version and link its corresponding
`tools/vba-dev/CHANGELOG.md` section. Do not repeat CLI change entries when that
version was already published and has not changed.

## Marketplace Publish

Publish the verified VSIX to the VS Code Marketplace with the release
workload's Microsoft Entra ID credential:

```powershell
vsce publish --azure-credential --pre-release --packagePath vba-tools-win32-x64-X.Y.Z.vsix
```

The Marketplace publisher must grant the release workload identity the
Contributor role. Do not fall back to a global PAT or persist any Marketplace
publishing credential in CI. Omit `--pre-release` only for a stable release.

If Marketplace publish succeeds:

1. Publish the draft GitHub Release.
2. Confirm the Marketplace page shows the expected README and version.
3. Confirm the GitHub Releases page is no longer empty for the published
   version.

If Marketplace publish fails:

1. Keep the GitHub Release as a draft.
2. For a transient authentication or service failure, retry publication with
   the exact artifact already attached to the draft release.
3. If code, metadata, documentation, or artifact content must change, prepare a
   new patch version and release tag; do not move or reuse the failed tag.
4. Re-run local verification and clean-machine smoke for the new release commit.

## Post-Release Checks

- Install the Marketplace version into a normal VS Code profile.
- Open an exported VBA file and confirm language features activate.
- Open a workbook-backed sample and run Doctor.
- Confirm the GitHub Release assets are downloadable.
- Confirm the GitHub Release is marked immutable and its release attestation
  verifies.
- Run `gh attestation verify` for the downloaded VSIX and standalone CLI ZIP.
- Confirm README `Version History` points to GitHub Releases.
- Open an issue for any manual follow-up that was intentionally deferred.
