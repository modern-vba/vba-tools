---
status: accepted
---

# Use GitHub Flow with maintainer-authorized direct integration

## Context

The repository accumulated issue branches, aggregate implementation branches,
merged pull-request branches, and work that existed only in a local branch or
worktree. Some issues reached `Done` before their changes reached `main`, so the
formal integration source became ambiguous. Long-lived integration branches
also allowed later issues to build on changes that had never entered the
default branch.

VBA Tools has one maintainer and one supported mainline. Its release policy
already requires `main` to remain releasable and creates release tags from exact
verified `main` commits. A classic Gitflow or git.git-style multi-integration
workflow would add permanent branches without solving a current product need.
GitHub cannot treat approval from a pull request author as independent review.
Removing the approval rule for everyone would also remove a useful gate for
general contributors, so the single-maintainer case needs a narrower exception.

## Decision

VBA Tools uses GitHub Flow. `main` is the only permanent integration branch.
Each issue uses one short-lived branch created from the current `origin/main`,
and unfinished issue or work-item branches are not stacked.

The normal integration path is a focused pull request to `main`. Ordinary pull
requests, including every non-maintainer-authored pull request, require one
approving review. No pull request author is mechanically
self-approved. The only approval exception is a specifically identified
maintainer-authored pull request, which may use the Repository Admin
pull-request-only ruleset bypass only after an explicit instruction to merge
that pull request. The pull request still provides the diff, verification,
discussion, and audit record and still uses squash merge. Its remote branch is
automatically deleted on GitHub after a successful merge, and its local branch
is deleted after the integrated commit is verified.

The maintainer may explicitly authorize direct integration into `main` without
a pull request for one issue, one maintainer-requested non-issue work item, or a
finite issue series. This authorization skips only the pull request and
per-issue human review. Work remains on a short-lived branch until it is
verified, pushed for protection, rebased and consolidated, and integrated
through a non-force fast-forward. Verification, acceptance criteria, a
verification note, and confirmation of the resulting `origin/main` commit
remain mandatory.

Pull-request-less integration uses the Organization Admin always-bypass only
for a still-valid, explicitly scoped direct-integration authorization. The
Repository Admin bypass remains pull-request-only. Possession of either role is
not itself authorization to merge or bypass a rule, and a request to write,
commit, or push does not supply that authorization.

Protection from `main` deletion, force-push prevention, and required linear
history are non-bypass invariants. Repository settings allow squash merge only
and automatically delete remote pull-request branches after merge.

When two or more issues are requested consecutively and no integration method
is specified, the agent asks once before implementation whether direct
integration is authorized for the series. If it is not authorized, the agent
opens the first pull request and waits for its merge before starting later work.
Authorization is scoped to the frozen issue set and expires when that work is
completed, cancelled, replaced, or materially expanded.

An issue reaches `Done` only after its intended change is verified on
`origin/main`. Branch-only commits and unmerged pull requests are incomplete.
The complete operational contract lives in `CONTRIBUTING.md` and the issue
tracker workflow.

## Consequences

- The repository has one unambiguous integration source.
- Ordinary and non-maintainer-authored pull requests retain an independent
  approval gate without requiring meaningless self-approval from the maintainer.
- A maintainer-authored pull request can use a narrowly authorized PR-only
  bypass while retaining its squash merge, audit trail, and verification record.
- Consecutive autonomous work requires an explicit integration decision before
  implementation begins.
- Direct integration is auditable and cannot silently follow from a request to
  commit, push, close, or use general GitHub write access.
- WIP commits may be pushed safely and then squashed before integration.
- Branch cleanup is part of completion rather than a later maintenance task.
- Release preparation may impose stricter review requirements through
  `docs/release.md`; multiple maintained release lines require a later explicit
  decision before `release/vX.Y` branches are introduced.
