## Work Item

<!-- For issue work, add a non-closing reference such as `Refs #123`. For
non-issue work, identify the maintainer-requested work item. Do not use a
closing keyword: issue closure occurs only after post-merge verification on
origin/main satisfies the Done gate. -->

## Summary

<!-- Describe the focused change and its user-visible effect. -->

## Acceptance Criteria

- [ ] Every acceptance criterion is satisfied or explicitly declared out of
      scope by the maintainer.

## Verification

| Command or check | Result |
| --- | --- |
|  |  |

## Bypass Authorization

<!-- Required only for a specifically identified maintainer-authored pull request that has an explicit instruction to merge with Repository Admin pull-request-only bypass. Link or quote that instruction here. -->

Authorization:

## Integration Checklist

- [ ] This pull request contains one work item and targets `main`.
- [ ] The branch is based on the current `origin/main`.
- [ ] No later issue is stacked on this branch.
- [ ] The branch contains one logical English Conventional Commit, its details
      section is at most 200 characters, and the pull-request title matches its
      subject.
- [ ] User-facing and release impacts are documented where applicable.
- [ ] One eligible approval is present, or this is a specifically identified
      maintainer-authored pull request with an explicit merge instruction that
      authorizes Repository Admin pull-request-only bypass.
- [ ] No pull-request author was mechanically self-approved.
- [ ] This pull request will use squash merge and retain its audit and
      verification evidence even if the approved PR-only bypass is used.
- [ ] For issue-backed work, the issue will remain `In review` until the
      integrated `main` commit is verified.
