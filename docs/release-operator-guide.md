# VBA Tools release operator guide

[日本語版](release-operator-guide-ja.md)

Use this guide to release VBA Tools yourself from Windows PowerShell 7.2 or later.
It covers preparation, review, verification on the integrated commit, clean Windows
smoke testing, tag authorization, and verification of the public release. No AI
assistant, private helper script, or previous chat session is required.

The extension is the release unit. Its version and Marketplace channel are
independent of the bundled CLI, language server, and debug adapter versions.
Release policy and the detailed smoke contract are in [Release](release.md);
branch and review rules are in [Contributing](https://github.com/modern-vba/vba-tools/blob/main/CONTRIBUTING.md).

## Release sequence

| Phase | What you do | Required result before continuing |
| --- | --- | --- |
| Prepare | Choose versions and generate the release change | Reviewed versions, changelog, and limitations |
| Review | Verify the candidate and merge its PR | Correct candidate, successful checks, permitted squash merge |
| Verify main | Run the Windows Excel gate on the integrated commit | Exact clean main commit, complete passing gate |
| Smoke | Build local assets and test them in a disposable Windows VM | Required editor, workbook, and debug checks pass |
| Authorize | Create and push the annotated release tag | Tag points to the exact verified main commit |
| Publish | Observe the tag workflow | Exact Marketplace version visible and GitHub Release published |
| Verify public assets | Download, check hashes, and verify attestations | Immutable release and exact signed artifact set |
| Accept | Install from Marketplace and check behavior | Verification note and acceptance criteria complete |

Pushing the protected tag authorizes publication. Do not push it merely to
test whether the release pipeline works. Do not move, delete, or reuse it.

## Before starting

Use a clean clone or worktree of `modern-vba/vba-tools`, not the parent Excel
workspace. Install Git, GitHub CLI, PowerShell 7.2 or later, the Node.js version in
`.node-version`, the npm version in `package.json` `packageManager`, and the
exact .NET SDK in `global.json`. Do not replace a locked restore with an unlocked
restore or edit generated outputs to make a release pass.

You need desktop Excel and trusted VBIDE access on the verification host, plus a
disposable Windows VM or equivalent clean machine for smoke testing. Reserve the
host's VS Code, Excel, and VBE for the native gate; do not edit or use its test
workbooks during that gate.

The publishing identity and repository protections are normally configured once,
not for each release. The repository uses GitHub Actions OIDC and an Entra
managed identity; you do not need to sign in to Azure locally or supply a
Marketplace PAT. See [Publishing identity setup](release.md#one-time-publishing-identity-setup)
when setting up a new repository or auditing that configuration.

Run commands one at a time. For external programs, check `$LASTEXITCODE` and
stop on any nonzero result. `$ErrorActionPreference = 'Stop'` alone does not make
every native command failure terminate PowerShell. Do not run the next phase in
parallel with a gate or artifact build.

```powershell
git status --short --branch
git remote get-url origin
gh auth status
node --version
npm --version
dotnet --version
gh release verify --help
gh attestation verify --help
```

Require a clean starting worktree, the intended GitHub account, the correct
repository, matching pinned tool versions, and a GitHub CLI that supports both
verification commands. Never copy access tokens into release notes or evidence.

## 1 Choose release inputs

Set these variables in the repository root. `0.1.7` is an example of a new
pre-release, not a request to publish it. Choose an unused version for your work.
Keep `$CliVersion` unchanged unless you intentionally release a new CLI version.

```powershell
$Repo = 'modern-vba/vba-tools'
$Version = '0.1.7'
$Channel = 'pre-release'
$CliVersion = '0.1.0'
$Tag = "vba-tools-v$Version"
$VsixName = "vba-tools-win32-x64-$Version.vsix"
$CliName = "vba-dev-win-x64-$CliVersion.zip"
$Branch = "codex/issue-246-release-$Version"
$ReleaseIssue = 246
$RunStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$Evidence = Join-Path (Get-Location) ".tmp/release-operator/$Tag-$RunStamp"
```

Use an odd minor version for `pre-release` and an even minor version for
`stable`. Stable and pre-release uploads must have different numeric versions.
Use a current release issue; do not assume issue 246 remains the right tracker
forever. The current PR verification workflow runs only for head branches
starting with `codex/issue-246-release-`. If that branch policy changes, update it
through review; a skipped verification job is not a passing gate. The prefix is
a workflow filter, not authorization to change or close an issue.

## 2 Prepare and review the candidate

Start from current main, with no unfinished work on this branch:

```powershell
git fetch origin main --tags
if ($LASTEXITCODE -ne 0) { throw 'Main and tag refresh failed.' }
git switch -c $Branch origin/main
npm ci --ignore-scripts
if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
npm run release:prepare -- --extension-version $Version --channel $Channel --vba-dev-version $CliVersion
if ($LASTEXITCODE -ne 0) { throw 'Release preparation failed.' }
git diff --check
git diff --stat
git diff
```

Review the generated diff. It should consistently update `package.json`,
`package-lock.json`, and the root changelog. CLI version and history changes
belong only to the intended independent CLI release. Check requirements,
breaking changes, known limitations, bundled tool versions, links, icon, and
support information. The generator does not commit, tag, push, or publish.

Stage only the reviewed release files, then commit them before running the
candidate gate. Do not stage local evidence, credentials, dumps, or unrelated
changes. For example, after reviewing these paths:

```powershell
git add package.json package-lock.json CHANGELOG.md tools/vba-dev/Directory.Build.props tools/vba-dev/CHANGELOG.md
git commit -m "chore(release): prepare $Version" -m "Prepare the reviewed extension version, channel, and release notes."
git status --short
npm run verify:release:windows-excel
if ($LASTEXITCODE -ne 0) { throw 'Candidate Windows Excel release gate failed.' }
git status --short
```

This full gate includes ordinary release verification and the native Excel
suites. It may take tens of minutes and use visible Excel/VBE windows. A missing
Excel prerequisite, skipped required native suite, incomplete output, or an
interrupted run is not a pass. Diagnostic profiles are not release gates.

Record the candidate SHA and results in the PR. Push and open the release PR:

```powershell
$CandidateCommit = (git rev-parse HEAD).Trim()
git push -u origin $Branch
gh pr create --repo $Repo --base main --head $Branch --draft --title "chore(release): prepare $Version" --body 'Release preparation. Add the reviewed versions, verification evidence, and a non-closing issue reference before review.'
```

In GitHub, add `Refs #<release issue>` and the gate evidence to the PR body.
Check that **Verify release preparation** actually ran on this candidate SHA and
passed. Mark the PR ready only after local and hosted checks pass. Follow
`CONTRIBUTING.md` for review and squash merge. Ordinary PRs need an eligible
approval; the narrow maintainer-authored admin-bypass route requires an explicit
decision for that exact PR. Do not self-approve or treat administrator access as
permission to bypass review.

## 3 Reverify the integrated main commit

After merge, use a clean checkout of current `origin/main`. A squash merge has a
different commit identity from its candidate, even when their trees are equal.

```powershell
git fetch origin main --tags
if ($LASTEXITCODE -ne 0) { throw 'Main and tag refresh failed.' }
git switch --detach origin/main
$VerifiedCommit = (git rev-parse HEAD).Trim()
git status --short
npm ci --ignore-scripts
if ($LASTEXITCODE -ne 0) { throw 'Main dependency restore failed.' }
New-Item -ItemType Directory -Path $Evidence -ErrorAction Stop | Out-Null
npm run verify:release:windows-excel 1> (Join-Path $Evidence 'main-gate.stdout.log') 2> (Join-Path $Evidence 'main-gate.stderr.log')
if ($LASTEXITCODE -ne 0) { throw 'Integrated main Windows Excel gate failed.' }
git status --short
git rev-parse HEAD
```

Read both logs and require all native stages to have run successfully. Keep the
full SHA and clean before/after status with the exit result. Do not infer success
from a final line in a partial log or from an overall workflow badge.

If you deliberately impose process-affinity or other environment conditions,
record them with the result and restore them afterward. Do not change Excel's
affinity as part of a product/test-process restriction.

## 4 Build the local smoke release set

Assemble once into a new local output directory. This command also runs its
ordinary non-Excel prerequisites; do not bypass those checks or run a second
assembly concurrently.

```powershell
$LocalAssets = Join-Path $Evidence 'local-assets'
npm run release:artifacts -- --extension-version $Version --channel $Channel --vba-dev-version $CliVersion --output $LocalAssets
if ($LASTEXITCODE -ne 0) { throw 'Local artifact assembly failed.' }
node scripts/releaseWorkflow.mjs validate-assets-static --directory $LocalAssets --extension-version $Version --channel $Channel --vba-dev-version $CliVersion
if ($LASTEXITCODE -ne 0) { throw 'Local artifact validation failed.' }
Get-ChildItem -LiteralPath $LocalAssets -File | Get-FileHash -Algorithm SHA256
```

Expect exactly `$VsixName`, `$CliName`, and `SHA256SUMS`. The validator checks
versions, target, channel, checksums, package contracts, and the exact identity of
the CLI bundled in the VSIX and the standalone ZIP.

These local files are for pre-publication smoke testing. The tag workflow builds
its own authoritative cloud release set once. Do not assume the local and cloud
VSIX hashes will match, and never upload the local smoke set over cloud assets.

## 5 Prepare the clean Windows fixture

Use only a disposable workbook-backed sample that you may import into, overwrite
with Build, and close without saving. Review its `vba-project.json` and identify
the source workbook selected by `templatePath`; an old `bin` workbook is not the
execution target. The sample must contain harmless tests and debug procedures
that do not explicitly save or perform external side effects.

1. Create a fresh kit containing the sample project and the three local assets.
   Copy the fixture; do not move or edit its original authoring source.
2. If the sample refers to adjacent `common_modules_repo`, include its reviewed
   closed flat package and canonical manifest in that exact relative location.
   Do not reconstruct it from arbitrary source files.
3. Exclude filesystem links, secrets, editor profiles, credentials, and unrelated
   workbooks. Record relative paths, sizes, and SHA-256 for source and copied
   files; confirm the original source inventory remains unchanged after copying.
4. In the kit only, preserve an existing `.vscode/launch.json` as
   `launch.saved.json` so dynamic F5 can be tested first. Do not overwrite an
   existing saved configuration on a name collision.
5. Preserve existing settings and disable Auto Save and encoding guessing in
   this fixture. For Shift_JIS exported VBA, the relevant settings are:

   ```json
   {
     "files.autoSave": "off",
     "files.autoGuessEncoding": false,
     "[vba]": { "files.encoding": "shiftjis" }
   }
   ```

   Use the fixture's actual encoding; do not force Shift_JIS on UTF-8 sources.
6. Copy the kit to the VM and compare every copied file's hash with the host
   inventory. Keep the kit root and adjacent package layout intact.
7. In the VM, install the exact copied VSIX using **Extensions: Install from
   VSIX**. Reload VS Code when requested and confirm the installed version.
8. Trust only the reviewed test folder. Enable Excel's trusted VBA project
   access and any document-specific content prompt only for this reviewed
   fixture. Do not disable security globally or trust all future folders.

Run subsequent VM commands in the VM, not on the build host. Keep VM evidence
separate from host evidence. No additional .NET runtime should be installed just
to make the self-contained companions run.

## 6 Run and record the VM smoke checks

Follow the complete [Clean Windows Smoke](release.md#clean-windows-smoke)
contract. Use the following checkpoints as the operator checklist:

| Check | Passing observation |
| --- | --- |
| Language features | Completion, diagnostics, and symbols work on exported VBA |
| Formatting | Keywords and indentation change as expected; comments and strings remain correct; original fixture source is preserved |
| Doctor and Host Events | Doctor's native checks pass; review warnings; Refresh UserForm Events reports its catalog without opening or saving the source template |
| Closed workbook Build | The manifest-selected source workbook is imported and saved, then its hidden session closes |
| Open workbook Build | The existing Excel PID/window is retained; import and Save occur, including a deliberate harmless unsaved cell marker after consent |
| Closed and open workbook Test | The exact source workbook is used; a borrowed open session remains open; Test itself does not Save |
| Test Explorer | Nodes are discovered; the default Run Tests profile succeeds without automatic workbook Save |
| Dynamic F5 | With no launch.json, a dirty source procedure and ordinary line breakpoint enter native VBE Break mode in the exact source workbook |
| Continue and Stop | Continue runs the harmless procedure; Stop performs native Reset while retaining the open workbook without Save |
| Restart and saved F5 | The same Excel PID/window and unsaved cell marker survive; Restart imports fresh dirty source; the saved target also works |
| Final comparison | After closing only the disposable workbook without saving and discarding editor changes, source/workbook hashes match the intended baseline; only intentional launch.json or separate evidence is added |
| Installed companions | All three executable hashes match entries in the exact installed VSIX; installed LSP runs directly and prints its independently defined version |

Record a new disk baseline **after Build**, since Build intentionally saves.
Compare Test against that baseline before another Build. For debugging, prepare
the harmless debug module with Build, record another baseline, and compare only
after the final no-save close. Keep the generated `launch.json` as an intentional
addition. Do not use it to excuse other unexpected changes.

Use a standard module with `Option Private Module` and a public parameterless
`Sub` for the native debug smoke. Dirty editor content must be captured without
automatic editor Save. Test can require dirty-workbook consent; Debug F5 and
Restart do not use that confirmation. VBA itself can still save or cause side
effects, so the fixture must not do so.

For installed executable identity, set `$VmVsix` to the copied VSIX and
`$ExtensionRoot` to the exact active installation directory shown in VS Code.
Do not select the newest directory by timestamp when multiple versions exist.

```powershell
$VmVsix = Read-Host 'Full path of the VSIX installed in this VM'
$ExtensionRoot = Read-Host 'Full path of the active modern-vba.vba-tools installation'
$InstalledPackage = Get-Content -LiteralPath (Join-Path $ExtensionRoot 'package.json') -Raw | ConvertFrom-Json
if ($InstalledPackage.version -ne $Version) { throw 'Wrong installed extension version.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$Archive = [IO.Compression.ZipFile]::OpenRead($VmVsix)
try {
    foreach ($Relative in @(
        'bin/vba-dev/win-x64/vba-dev.exe',
        'bin/vba-debug-adapter/win-x64/vba-debug-adapter.exe',
        'bin/vba-language-server/win-x64/vba-language-server.exe'
    )) {
        $Entry = $Archive.GetEntry("extension/$Relative")
        if ($null -eq $Entry) { throw "Missing VSIX entry: $Relative" }
        $Stream = $Entry.Open()
        $Hasher = [Security.Cryptography.SHA256]::Create()
        try { $ExpectedHash = [Convert]::ToHexString($Hasher.ComputeHash($Stream)) }
        finally { $Stream.Dispose(); $Hasher.Dispose() }
        $ActualHash = (Get-FileHash -LiteralPath (Join-Path $ExtensionRoot $Relative) -Algorithm SHA256).Hash
        if ($ActualHash -cne $ExpectedHash) { throw "Installed binary mismatch: $Relative" }
        "$Relative $ActualHash"
    }
}
finally { $Archive.Dispose() }
& (Join-Path $ExtensionRoot 'bin/vba-language-server/win-x64/vba-language-server.exe') --version
if ($LASTEXITCODE -ne 0) { throw 'Installed LSP direct execution failed.' }
```

Copy the selected `$Version` value into the VM shell first. Compare the LSP
output with its own release metadata, not the extension's version. To record the
clean-machine runtime conditions:

```powershell
Test-Path -LiteralPath 'C:\Program Files\dotnet\shared'
Test-Path -LiteralPath 'C:\Program Files (x86)\dotnet\shared'
Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\shared')
Get-Command dotnet.exe -ErrorAction SilentlyContinue | Select-Object Source
```

False for those standard paths and no `dotnet.exe` on PATH do not prove the
absence of every custom runtime location. Preserve the observations as stated.
Complete the required smoke before tagging. A permitted `not-required` smoke
needs the rationale allowed by release policy; it is not a substitute for an
incomplete required check.

## 7 Authorize and push the tag

Return to the host's exact verified main checkout. Check the publishing setup
without changing protections:

```powershell
node scripts/releaseWorkflow.mjs validate-protections
if ($LASTEXITCODE -ne 0) { throw 'Release protection preflight failed.' }
$ImmutableSetting = gh api "repos/$Repo/immutable-releases" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $ImmutableSetting.enabled -ne $true) { throw 'Immutable releases must be enabled.' }
git fetch origin main --tags
if ($LASTEXITCODE -ne 0) { throw 'Main and tag refresh failed.' }
if ((git rev-parse HEAD).Trim() -ne $VerifiedCommit) { throw 'Verified checkout changed.' }
if ((git rev-parse origin/main).Trim() -ne $VerifiedCommit) { throw 'Main moved; reverify the new exact commit before tagging.' }
if (git status --porcelain) { throw 'Tagging requires a clean checkout.' }
```

As an administrator, also audit the active release-tag creation and immutable-tag
rulesets in GitHub **Settings > Rules > Rulesets**, including their exact bypass
actors. Preserve deletion and force-update protections. Confirm the
`marketplace-release` Environment is limited to the intended protected tags.
Do not weaken protections to make this preflight pass.

Record the successful full gate and VM result against `$VerifiedCommit` in the
release PR or issue. Then deliberately authorize the public release:

```powershell
npm run release:tag -- --version $Version --channel $Channel --vba-dev-version $CliVersion --verified-commit $VerifiedCommit --windows-excel-result pass --clean-windows-smoke pass
if ($LASTEXITCODE -ne 0) { throw 'Release tag creation failed.' }
git cat-file -p $Tag
git rev-parse "$Tag^{commit}"
git push origin $Tag
if ($LASTEXITCODE -ne 0) { throw 'Tag push was not confirmed; inspect remote state before any further action.' }
```

Inspect the annotation before pushing: channel, full verification SHA, Windows
Excel result, and clean Windows smoke result must be correct. If policy permits
omitting smoke, use `--clean-windows-smoke not-required` together with
`--clean-windows-smoke-reason`; do not report `pass` for a check not performed.
The tag command creates only the local annotation; the push starts publication.

The release commit must contain the reviewed finalization scheduling correction
from [PR #464](https://github.com/modern-vba/vba-tools/pull/464), or its reviewed
equivalent. A tag runs its own workflow snapshot: updating main does not repair
an older tag's workflow. Never move an existing tag or dispatch from main to
substitute a newer workflow for that tag.

## 8 Observe publication

Find the exact tag-push run, compare its `headSha` with `$VerifiedCommit`, and
watch that run rather than starting another one:

```powershell
gh run list --repo $Repo --workflow release.yml --event push --branch $Tag --json databaseId,headSha,status,conclusion,url
$ReleaseRun = Read-Host 'Database ID of the exact tag-push run'
gh run watch $ReleaseRun --repo $Repo --interval 300 --exit-status
gh run view $ReleaseRun --repo $Repo --json headSha,event,status,conclusion,jobs,url
```

The workflow validates the tag, runs hosted verification, builds the cloud
release set, inspects and attests it, creates a draft, publishes to Marketplace,
and finalizes GitHub. The mutually exclusive initial/resume branch can be
skipped by design; the required publication and finalization work must actually
finish. An overall green badge alone is not release acceptance.

Check the job results and the finalization steps, not only the run conclusion:

| Job | Initial tag push | Authorized existing-draft resume |
| --- | --- | --- |
| `validate` | success | success |
| `build`, `inspect`, `attest`, `stage` | success | skipped by design |
| `resume_stage` | skipped by design | success |
| `publish` | success, exact Marketplace visibility verified | success; no upload if the exact version is already visible |
| `finalize` | success, publication and verification steps executed | success, publication and verification steps executed |

`finalize` depends directly on `validate` and `publish`. Its explicit condition
is `!cancelled() && needs.validate.result == 'success' && needs.publish.result == 'success'`.
This permits the intentional branch skips without admitting failed, cancelled,
skipped, or missing direct prerequisites. Do not remove the status guard or
replace it with unconditional `always()`. `finalize` rechecks the retained draft,
provenance, and Marketplace visibility before publishing, then verifies the
release attestation and freshly downloaded public assets.

If a required job fails, is skipped, stops, or needs action, retain the existing
tag, draft and assets. Do not start another build, re-push a tag, replace assets,
or repeatedly publish. Read the current state first. The conditional
[Existing draft finalization](#existing-draft-finalization) section covers only
the narrow case where Marketplace publication is already complete and the exact
verified GitHub draft still needs publication.

For an explicitly authorized workflow resume, first confirm that the retained
draft and original attestations are complete and the workflow in the exact tag
supports the corrected scheduling. Use the same tag for both inputs:

```powershell
gh workflow run release.yml --repo $Repo --ref $Tag -f "release_tag=$Tag"
if ($LASTEXITCODE -ne 0) { throw 'Resume dispatch was not confirmed; inspect runs before any further action.' }
gh run list --repo $Repo --workflow release.yml --event workflow_dispatch --branch $Tag --json databaseId,headSha,status,conclusion,url
```

Record and watch the new dispatch run as above; require the resume column's
results and repeat step 9. Resume validates existing bytes and attestations;
it must not build replacements or create missing attestations. It is not a
routine second publication step, an automatic retry, or a way to test the
workflow using an already published release. For an older immutable tag,
resuming still uses that older snapshot, not the fix on main. If that snapshot
cannot finalize, inspect it and obtain a separate decision for the narrowly
scoped manual fallback below; do not change its ref or protections.

## 9 Verify the public release independently

Run from the exact tagged source checkout with its locked dependencies. Use a
new directory; keep verification output outside the asset directory because the
validator requires exactly three files.

```powershell
node scripts/releaseWorkflow.mjs verify-live-marketplace --extension-version $Version --channel $Channel
if ($LASTEXITCODE -ne 0) { throw 'Exact Marketplace version, channel, and target are not visible.' }
$PublicRelease = gh api "repos/$Repo/releases/tags/$Tag" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Published GitHub Release was not found.' }
if ($PublicRelease.draft -or -not $PublicRelease.immutable -or $PublicRelease.tag_name -cne $Tag) { throw 'Release is not the expected published immutable release.' }
if ($PublicRelease.prerelease -ne ($Channel -eq 'pre-release')) { throw 'Published channel mismatch.' }
gh release verify $Tag --repo $Repo --format json
if ($LASTEXITCODE -ne 0) { throw 'Release attestation verification failed.' }
$PublicAssets = Join-Path $Evidence 'public-assets'
New-Item -ItemType Directory -Path $PublicAssets -ErrorAction Stop | Out-Null
gh release download $Tag --repo $Repo --dir $PublicAssets
if ($LASTEXITCODE -ne 0) { throw 'Public asset download failed.' }
node scripts/releaseWorkflow.mjs validate-assets-static --directory $PublicAssets --extension-version $Version --channel $Channel --vba-dev-version $CliVersion
if ($LASTEXITCODE -ne 0) { throw 'Public asset static validation failed.' }
foreach ($AssetName in @($VsixName, $CliName)) {
    gh attestation verify (Join-Path $PublicAssets $AssetName) --repo $Repo --signer-workflow modern-vba/vba-tools/.github/workflows/release.yml --source-ref "refs/tags/$Tag" --source-digest $VerifiedCommit --deny-self-hosted-runners
    if ($LASTEXITCODE -ne 0) { throw "Build provenance verification failed: $AssetName" }
}
Get-ChildItem -LiteralPath $PublicAssets -File | Get-FileHash -Algorithm SHA256
git ls-remote --tags origin "refs/tags/$Tag" "refs/tags/$Tag^{}"
```

Require the remote annotated tag object and peeled commit to match the tag you
pushed. In the verified release attestation, check the repository/tag identity,
Release ID, tag object digest, and the three asset digests against the release
metadata and your downloaded hashes. Build provenance additionally binds VSIX
and ZIP to `release.yml`, the exact tag ref, and `$VerifiedCommit`.

Save the commands' results, Release ID, asset IDs and digests, tag object SHA,
run URL, and public URL with the release evidence. Do not execute downloaded
EXEs as part of this static verification. Do not substitute an earlier VM hash
or a staging job's success for validation of the public files.

## 10 Install from Marketplace and complete acceptance

On the test machine, close VS Code first and install the exact public version
through Marketplace, not the local smoke VSIX:

```powershell
$InstallArguments = @('--install-extension', "modern-vba.vba-tools@$Version", '--force')
if ($Channel -eq 'pre-release') { $InstallArguments += '--pre-release' }
code @InstallArguments
if ($LASTEXITCODE -ne 0) { throw 'Marketplace installation failed.' }
code --list-extensions --show-versions
```

Reload VS Code, confirm the active version, and check activation, language
features, formatting, Doctor, Build, Test/Test Explorer, and debugging in the
disposable sample. Compare installed companions with the **public** VSIX using
the VM identity check above, setting `$VmVsix` to that downloaded VSIX. This
checks the public installation rather than assuming it equals the local build.
Review the Marketplace README and the GitHub Release links and downloads.

Record the result in the release issue and PR. Use a verification note with:

- versions, channel, platform, exact tag object and commit;
- integration route, PR, review or explicit scoped bypass authorization;
- candidate/hosted checks, exact-main native gate, and clean Windows smoke;
- published Release ID/URL, immutable state, three asset IDs/hashes,
  release attestation, and both build-provenance results;
- Marketplace exact-version installation and observed behavior;
- known limitations, any explicitly accepted exceptions, and remaining work.

Keep sensitive logs, private paths, credentials, and dumps out of public notes.
If a known risk is accepted, record the observation and the maintainer's decision
without claiming an unproved cause, fix, or unconditional stability. Reviewed
release-note context can be appended to the Release body without changing
assets, tag, channel, or protection settings.

Close the release issue and mark its Project item Done only when all acceptance
criteria are verified on current `origin/main`, or the maintainer explicitly
excludes each remaining criterion. Publication alone is not completion.

## Existing draft finalization

This is an exceptional, separately authorized manual recovery, not a required
step after a successful corrected workflow. Normally `finalize` publishes and
verifies the GitHub Release automatically. If it has already done so, do not
run this section.

Use this section only when the exact Marketplace version/channel/target is
already visible, no release job is still running, and the retained GitHub draft
contains the complete original attested release set. This publishes that draft
only; it does not rebuild, republish Marketplace, or change a tag or asset.
The maintainer must deliberately choose this action for the specific Release ID.

1. Resolve the unique draft and record its identity:

   ```powershell
   $DraftJson = Join-Path $Evidence 'draft-before.json'
   node scripts/releaseWorkflow.mjs resolve-draft --tag $Tag --release-json $DraftJson
   if ($LASTEXITCODE -ne 0) { throw 'Unique exact draft lookup failed.' }
   $Draft = Get-Content -LiteralPath $DraftJson -Raw | ConvertFrom-Json
   $Draft.id
   $Draft.assets | Select-Object id,name,size,digest
   $Draft.body
   $DraftSnapshotHash = (Get-FileHash -LiteralPath $DraftJson -Algorithm SHA256).Hash
   ```

   Keep the complete snapshot, not just its hash. Confirm the exact tag object
   and peeled commit with `git ls-remote`, the intended title/channel, and the
   unchanged asset IDs, sizes, and digests. Do not use the published-release
   `/releases/tags/{tag}` endpoint to look up a draft.
2. Download its three assets into a new `$DraftAssets` directory, then validate:

   ```powershell
   $DraftAssets = Join-Path $Evidence 'draft-assets'
   New-Item -ItemType Directory -Path $DraftAssets -ErrorAction Stop | Out-Null
   gh release download $Tag --repo $Repo --dir $DraftAssets
   if ($LASTEXITCODE -ne 0) { throw 'Draft asset download failed.' }
   node scripts/releaseWorkflow.mjs validate-draft --release-json $DraftJson --directory $DraftAssets --tag $Tag --title "VBA Tools $Version" --extension-version $Version --channel $Channel --vba-dev-version $CliVersion --vsix-name $VsixName --cli-archive-name $CliName
   if ($LASTEXITCODE -ne 0) { throw 'Draft metadata or artifact validation failed.' }
   foreach ($AssetName in @($VsixName, $CliName)) {
       gh attestation verify (Join-Path $DraftAssets $AssetName) --repo $Repo --signer-workflow modern-vba/vba-tools/.github/workflows/release.yml --source-ref "refs/tags/$Tag" --source-digest $VerifiedCommit --deny-self-hosted-runners
       if ($LASTEXITCODE -ne 0) { throw "Draft provenance verification failed: $AssetName" }
   }
   node scripts/releaseWorkflow.mjs verify-live-marketplace --extension-version $Version --channel $Channel
   if ($LASTEXITCODE -ne 0) { throw 'Exact Marketplace package is not visible.' }
   ```

   Compare downloaded hashes with each draft asset's API digest as well as
   `SHA256SUMS`. Recheck enabled immutable releases and protected-tag rules as
   in step 7. Missing or changed metadata, checksums, or provenance means stop;
   do not create replacement bytes or attestations.
3. Immediately reread `gh api "repos/$Repo/releases/$($Draft.id)"` and compare
   its ID, tag, title, draft/channel state, complete body, and asset inventory
   with the saved snapshot. Check again that the annotated tag target has not
   changed. Publish only if all guards still match and you authorize that ID:

   ```powershell
   $Confirmation = Read-Host "Type $($Draft.id) to publish only the verified $Tag draft"
   if ($Confirmation -cne [string]$Draft.id) { throw 'Draft publication not authorized.' }
   gh release edit $Tag --repo $Repo --draft=false
   if ($LASTEXITCODE -ne 0) { throw 'Publication outcome needs inspection; do not repeat it automatically.' }
   ```

   Leave existing pre-release status and body unchanged. If the request fails
   or its outcome is unclear, reread this exact Release ID before deciding what
   to do. Do not recreate the release or retry blindly.
4. Complete step 9 using fresh public downloads, and compare the published asset
   IDs/digests with the retained draft. Require non-draft and immutable state,
   the release attestation, both original build attestations, and unchanged tag
   binding. Continue to Marketplace installation and the acceptance note.

## Final checklist

- [ ] Reviewed version/channel and independently versioned companions.
- [ ] Candidate verified and integrated through the permitted review route.
- [ ] Exact integrated main SHA verified by the complete Windows Excel gate.
- [ ] Required clean Windows smoke completed using disposable fixtures.
- [ ] Annotated protected tag deliberately authorized and pushed once.
- [ ] Exact Marketplace version, target, and channel visible.
- [ ] Same GitHub Release published, immutable, and attestation verified.
- [ ] Fresh public VSIX, CLI ZIP, and SHA256SUMS validated with recorded hashes.
- [ ] Both build attestations bind the exact workflow, tag ref, and commit.
- [ ] Exact Marketplace installation and behavior accepted.
- [ ] Review record includes results, known limitations, and remaining work.
- [ ] Issue/Project completion follows acceptance, not merely publication.

## Reference commands and policies

- [Release policy and automation contracts](release.md)
- [Contribution and integration rules](https://github.com/modern-vba/vba-tools/blob/main/CONTRIBUTING.md)
- [GitHub immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases)
- [GitHub CLI release verification](https://cli.github.com/manual/gh_release_verify)
- [GitHub CLI build attestation verification](https://cli.github.com/manual/gh_attestation_verify)
- [VS Code command line installation](https://code.visualstudio.com/docs/configure/command-line#working-with-extensions)
