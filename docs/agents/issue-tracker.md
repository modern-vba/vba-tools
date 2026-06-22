# Issue Tracker: GitHub

Issues and PRDs for this repository are managed in GitHub Issues. The target repository is `tkmr-akhs/VBA-LanguageServer`.
When a mattpocock/skills workflow says to publish to the issue tracker, create a GitHub issue with the `gh` CLI.

## Language

Issue titles, issue bodies, PRDs, comments, labels created for VBA-LanguageServer-specific workflow, and GitHub Projects text must be written in English.

## Repository

- GitHub remote: `https://github.com/tkmr-akhs/VBA-LanguageServer.git`
- Run `gh` from this clone. Prefer `--repo tkmr-akhs/VBA-LanguageServer` when issuing commands from outside the repository root.

## Conventions

- Create issue: `gh issue create --repo tkmr-akhs/VBA-LanguageServer --title "..." --body "..."`
- View issue: `gh issue view <number> --repo tkmr-akhs/VBA-LanguageServer --comments`
- List issues: `gh issue list --repo tkmr-akhs/VBA-LanguageServer --state open --json number,title,body,labels,comments`
- Add comment: `gh issue comment <number> --repo tkmr-akhs/VBA-LanguageServer --body "..."`
- Add label: `gh issue edit <number> --repo tkmr-akhs/VBA-LanguageServer --add-label "..."`
- Remove label: `gh issue edit <number> --repo tkmr-akhs/VBA-LanguageServer --remove-label "..."`
- Close issue: `gh issue close <number> --repo tkmr-akhs/VBA-LanguageServer --comment "..."`

## Relationships

When issue creation, PRD publication, or issue splitting identifies blocking or parent-child relationships, set GitHub Issues Relationships in addition to documenting the relationship in issue text.

- Use `gh api` first. Do not use external HTTP clients such as `curl` unless `gh` cannot perform the operation.
- Fetch the REST integer issue id with `gh api repos/tkmr-akhs/VBA-LanguageServer/issues/NUMBER --jq .id`.
- For parent-child relationships, use sub-issues. To add `CHILD_NUMBER` as a child of `PARENT_NUMBER`, fetch the child's integer id and run `gh api --method POST repos/tkmr-akhs/VBA-LanguageServer/issues/PARENT_NUMBER/sub_issues -f sub_issue_id=CHILD_ISSUE_ID`.
- For blocking relationships, use issue dependencies `blocked_by`. If `BLOCKING_NUMBER` blocks `BLOCKED_NUMBER`, fetch the blocking issue's integer id and run `gh api --method POST repos/tkmr-akhs/VBA-LanguageServer/issues/BLOCKED_NUMBER/dependencies/blocked_by -f issue_id=BLOCKING_ISSUE_ID`.
- If permissions, GitHub limitations, or issue creation order prevent relationship setup, add the relationship to the issue body or a comment and report which relationship was not set.

## When a skill says "publish to the issue tracker"

Create a GitHub issue.

## When a skill says "fetch the relevant ticket"

Run `gh issue view <number> --repo tkmr-akhs/VBA-LanguageServer --comments`.

## GitHub Projects

Issues in this repository are managed with both GitHub Issues and GitHub Projects v2.
Use the `tkmr-akhs` user project #7, `VBA-LanguageServer main project`.
Use the `Status` project field and keep it aligned with the issue lifecycle described in the parent repository `AGENTS.md`.

| Issue state / label | Project `Status` | Agent action |
| --- | --- | --- |
| `ready-for-agent` | `Ready` | Mark the issue as ready for agent work. |
| Active implementation | `In progress` | Show that an agent is working on the issue. |
| Implementation complete + waiting for review + `ready-for-human` | `In review` | Mark the issue as ready for human review. |
| Review passed + acceptance criteria verified | `Done` | Remove `ready-for-human`, close the issue, and move the project item to `Done`. |

Rules:

- If an issue gets the `ready-for-agent` label, set project `Status` to `Ready`.
- When implementation starts, set project `Status` to `In progress`.
- When implementation is complete, remove `ready-for-agent`, add `ready-for-human`, and set project `Status` to `In review`.
- After review passes, verify acceptance criteria. If all pass, or if the maintainer explicitly declares unmet criteria out of scope, remove `ready-for-human`, close the issue, and set project `Status` to `Done`.
- Use `ready-for-human` to mean that a human can review the current state. Do not restrict it to implementation handoff only.
- If acceptance criteria remain unmet and the maintainer has not explicitly marked them out of scope, do not close the issue or move it to `Done`.
- Use `gh api graphql` for GitHub Projects v2 operations. If scope is missing, run `gh auth refresh -h github.com -s project` to add the Projects scope.
