# Contributing to VBA Tools

This repository uses [GitHub Flow](https://docs.github.com/en/get-started/using-github/github-flow)
with one approval for ordinary pull requests and narrowly scoped ruleset-bypass
options for its single maintainer.
`main` is the only permanent integration branch and must remain releasable.
The rationale is recorded in
[ADR 0034](docs/adr/0034-use-github-flow-with-maintainer-authorized-direct-integration.md).

## Work branches

- Start each issue from the current `origin/main` and use one short-lived branch
  for that issue.
- Agent-created issue branches use `codex/issue-<number>-<slug>`. A descriptive
  `codex/<slug>` branch is allowed for maintainer-requested repository
  housekeeping that has no issue.
- Treat maintainer-requested housekeeping without an issue as one work item. It
  follows the same default pull-request and explicit direct-integration rules;
  only issue labels, comments, closure, and Project status transitions are
  omitted. Record direct-integration evidence in a maintainer-designated review
  record.
- Do not base one issue or work-item branch on another unfinished branch. Finish
  its integration before starting the next branch from the updated `origin/main`.
- Push unique commits before pausing or transferring work. Do not leave the only
  copy of work in uncommitted changes or local-only commits.
- A request to implement an issue authorizes commits and pushes to that issue
  branch and creation or updates of its pull request. It does not authorize
  integration into `main`.

## Selecting the integration path

Each issue or non-issue work item uses exactly one of these paths:

1. the normal pull-request path; or
2. maintainer-authorized direct integration.

For a single issue or non-issue work item, use a pull request unless the
maintainer explicitly authorizes integration into `main` without a pull
request. Do not ask which path to use merely because one item was requested.

Before implementing two or more issues consecutively, determine whether the
maintainer selected an integration path for the series. Read-only inventory may
be performed first to identify and freeze the issue set. If no integration
method was specified, ask once before creating a branch, changing a file,
committing, or changing issue status:

> May I integrate each verified issue directly into `main` without a pull
> request so that I can continue through the series?

If direct integration is authorized, apply it to every issue in that finite
series without asking again. If it is declined or pull requests are selected,
complete the first issue through a pull request and wait for it to be merged
before starting the next issue. Never stack later work on the unmerged branch.

For a query-defined series such as all current `ready-for-agent` issues, freeze
the issue set when it is inventoried. Issues discovered or created later are not
included unless the maintainer explicitly expands the scope.

Direct-integration authorization must unambiguously permit integration into
`main` without a pull request. Instructions such as `commit`, `push`, `sync with
origin`, `finish`, `close`, `move to Done`, or a general grant of GitHub write
access authorize neither a merge nor any ruleset bypass. An explicit instruction
to merge a specifically identified pull request authorizes only that pull
request's merge. When that pull request was authored by the maintainer, the
instruction may also authorize the Repository Admin pull-request-only bypass
for that exact pull request; it never authorizes pull-request-less integration
or the Organization Admin always-bypass path.

An unqualified instruction to `merge` is ambiguous when it identifies neither
a pull request nor integration into `main` without one. Clarify it before
integrating.

Authorization applies only to the named work item or finite issue series. It
survives an ordinary pause or resumed session for that same work, but expires
when the work is completed, cancelled, replaced, or materially expanded. It
never carries forward to unrelated work.

## Repository settings contract

Repository settings must support this workflow:

- require a pull request and one approving review for every ordinary update to
  `main`, including every non-maintainer-authored pull request;
- configure Repository Admin bypass as pull-request-only so a specifically
  identified maintainer-authored pull request can be merged without mechanical
  self-approval only after an explicit merge instruction;
- configure Organization Admin as always-bypass solely for explicitly
  authorized pull-request-less direct integration;
- protect `main` from deletion, reject force pushes, and require linear history
  through rules that have no bypass actors;
- allow squash merge only; and
- automatically delete remote pull-request branches after successful merge.

If the live settings differ, report the drift instead of self-approving, using
an unauthorized bypass, or selecting another merge method. Administration
rights do not convert configuration drift into workflow authorization. No
authorization waives the non-bypass deletion, force-push, or linear-history
invariants.

## Pull-request path

1. Fetch `origin` and create the work branch from the exact current
   `origin/main`.
2. Implement and verify the work item on that branch.
3. Push the branch at the first durable checkpoint and open a draft pull request
   to `main`.
4. For issue work, link the issue with a non-closing reference such as
   `Refs #123`. For non-issue work, identify the maintainer-requested work item
   in the pull request. Do not use a closing keyword because issue closure occurs
   only after post-merge verification satisfies the `Done` gate.
5. Keep the pull request focused on one work item. Rebase onto `origin/main`,
   consolidate it into one logical English Conventional Commit, and keep the
   details section within 200 characters. Do not merge `main` into the issue
   branch. If the branch was already published, update only that branch with
   `--force-with-lease`; never force `main`.
6. Make the pull-request title match the final Conventional Commit subject.
7. For issue work, when branch verification is complete, move the issue to
   `In review` and mark it `ready-for-human` according to the issue tracker
   workflow.
8. Ordinary pull requests, including every non-maintainer-authored pull request,
   require one approving review. Never create or solicit a mechanical
   self-approval from its author. The approval exception is a maintainer-authored
   pull request for which the maintainer explicitly
   instructs the agent to merge that specifically identified pull request. Only
   then may the Repository Admin pull-request-only bypass replace the approval
   for that pull request. Record the instruction and bypass in the pull-request
   evidence.
9. Immediately before merge, refresh `origin/main`. If it moved since the last
   branch verification, rebase, update only the work branch with
   `--force-with-lease`, repeat affected verification, and update the pull
   request evidence. If conflict resolution materially changes the reviewed
   diff, obtain a renewed merge instruction.
10. Do not merge until the required approval is present and the maintainer
    merges it or explicitly instructs the agent to merge that pull request. The
    only approval exception is the explicitly instructed Repository Admin
    pull-request-only bypass for a specifically identified maintainer-authored
    pull request described above.
11. Use squash merge. Do not create a merge commit or use rebase-and-merge.
12. Verify the integrated commit on `origin/main`, recheck the acceptance criteria
   against the integrated state, and post the verification note.
13. For issue work, only then close the issue and move it to `Done`. For
    non-issue work, record completion in the pull request or another
    maintainer-designated record.
14. Allow GitHub to auto-delete the remote pull-request branch after the
    successful squash merge. After post-merge verification, delete the local
    branch and prune stale remote-tracking references.

An issue with an open or unmerged pull request remains `In review`; any work
item represented only by an unmerged pull request is incomplete.

A pull-request-only bypass does not convert the work to direct integration. The
pull request, reviewed diff, squash merge, audit trail, branch verification,
post-merge verification, and completion evidence all remain required.

## Maintainer-authorized direct integration

Direct integration skips only the pull request and the per-issue human review.
It does not waive verification, acceptance criteria, the verification note, or
the `Done` gate. Work still takes place on a short-lived issue branch, never
directly on a checked-out `main`.

Use the Organization Admin always-bypass only while valid direct-integration
authorization exists for the issue being integrated. The Repository Admin
bypass is pull-request-only and must not be used for this path. Record the
authorization and always-bypass path in the verification note; organization or
repository administration rights alone are not permission to use either bypass.

For each authorized issue or work item, in order:

1. Create the work branch from the exact current `origin/main`.
2. Implement the work item, run the relevant checks, and verify every acceptance
   criterion.
3. Commit and push the work branch to protect the work before integration.
4. Rebase onto the latest `origin/main` and consolidate the work item into one
   logical English Conventional Commit. If a published work branch must be
   rewritten, update only that branch with `--force-with-lease`; never force
   `main`. Repeat affected verification after conflict resolution or a material
   rewrite.
5. Update `main` with a non-force fast-forward. If `origin/main` moved, fetch,
   rebase, repeat affected verification, and retry.
6. Verify that the remote update succeeded and the current `origin/main`
   contains the intended commit.
7. Post a verification note on the issue or maintainer-designated review record,
   recording the direct-integration authorization, integrated commit SHA,
   acceptance-criteria result, commands run and their results, and any criterion
   the maintainer explicitly declared out of scope.
8. For issue work, close the issue and move it to `Done` only after the `Done`
   gate passes.
9. Delete the integrated remote and local work branches and prune stale
   remote-tracking references.
10. Refresh from the resulting `origin/main` before starting the next issue.

The direct path may move an issue from `In progress` directly to `Done`; do not
add `ready-for-human` or use `In review` as a mechanical transient. If
implementation, verification, or integration fails, preserve the branch on
origin, leave the issue open, report the blocker, and do not start the next
issue.

## Done gate

An issue may be closed and moved to `Done` only when all of the following are
true:

- its intended change is present on the verified current `origin/main`;
- it arrived through a merged pull request or valid scoped direct-integration
  authorization;
- all acceptance criteria pass, except criteria the maintainer explicitly
  declared out of scope;
- all required verification completed; and
- the issue contains a verification note with the integration path, `main`
  commit SHA, acceptance result, and verification results.

A commit that exists only locally, only on an issue branch, or only in an
unmerged pull request does not satisfy this gate.

Non-issue housekeeping is complete only after the same integration and
verification conditions pass; record the evidence in its pull request or the
maintainer-designated direct-integration record.

## Cleanup and recovery

- Delete remote pull-request head branches automatically after a successful
  merge. This is the exception to verification-first deletion because GitHub
  retains the pull-request record and the integrated `main` commit. Delete the
  local branch after post-merge verification.
- Delete directly integrated remote and local branches only after verifying
  their commit on `origin/main`.
- Never delete the only reference to unique unintegrated content. Before
  rewriting a published WIP branch, confirm that the replacement tree preserves
  the intended content or create a documented recovery tag. Remove that tag
  only after safe integration and the agreed recovery period.
- Do not retain permanent `ready`, `implementation`, aggregate, or personal
  backup branches.
- Name temporary recovery tags for their work item and purpose unless the
  maintainer explicitly designates them as permanent history.
- Automation branches such as Dependabot branches may remain while their pull
  requests are open and must be removed after merge or closure.

## Release branches and tags

Release tags are created only from verified `main` commits. Do not create a
long-lived `develop` or `release/vX.Y` branch. Supporting more than one
maintenance line requires a later ADR and an explicit revision of this policy
before any release branch is introduced.

Release preparation has additional review and verification requirements in
`docs/release.md`. Those stricter requirements remain in force unless the
maintainer explicitly overrides the release-preparation path as well as
authorizing direct integration.
