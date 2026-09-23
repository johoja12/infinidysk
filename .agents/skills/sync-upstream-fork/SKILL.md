---
name: sync-upstream-fork
description: Sync upstream InfiniDysk main into the johoja12/infinidysk fork through a reviewed pull request, preserving fork changes. Use for requests to bring upstream changes into this fork; merge only when explicitly requested.
---

# Sync upstream into the fork

Use this skill only in the InfiniDysk checkout. The target is `johoja12/infinidysk`, and the source is `infinidysk/infinidysk` `main`. Read the repository's `AGENTS.md` when available; its issue, PR, commit, and cleanup rules still apply.

## Prepare

1. Check the current branch and working tree. Preserve unrelated edits and untracked files. Verify `origin` points to `johoja12/infinidysk` and `upstream` points to `infinidysk/infinidysk`; correct a missing or wrong remote before fetching.
2. Fetch `origin/main` and `upstream/main`. Record both SHAs, their merge base, and the upstream-only commits. If `upstream/main` is already an ancestor of `origin/main`, report that the fork is current and stop without a PR.
3. Create an isolated branch from the current `origin/main`. Merge the full `upstream/main` history; do not cherry-pick a release tag or replace the fork's tree with upstream's tree. Resolve conflicts by checking both sides' intent and preserving fork-only capabilities. Never use a blanket `ours` or `theirs` strategy.

## Validate and hand off

1. Inspect the merged diff for semantic conflicts, version changes, and migrations. For additive migrations, include a `/config` backup note in the PR. Mark irreversible or destructive migrations as breaking according to `AGENTS.md`.
2. Verify `upstream/main` is an ancestor of the branch tip and the branch has no unresolved conflicts. Run focused checks where they add evidence; use the PR's CI for its covered lanes. Investigate failures, including failures already present upstream, before treating the result as ready.
3. Commit with a Conventional Commit message, push the branch to `origin`, and create a PR against `main` with `gh pr create --repo johoja12/infinidysk`. Read the PR back to confirm its URL, head branch, and head SHA. Report the PR and the exact upstream SHA included.

## Merge when authorized

An instruction to check or sync upstream alone does not authorize merging. An explicit request to **sync and merge** this fork, or to merge the resulting PR, authorizes that PR's merge. Before merging, re-fetch both remotes, ensure the PR still has the expected head SHA, verify that all upstream commits intended for this sync are included, and inspect required checks and review state. Resolve any new upstream or fork changes on the PR branch first.

After merging, verify the PR is `MERGED` and `origin/main` contains the recorded upstream SHA. Follow the repository's merged-branch cleanup rules and return the main checkout to up-to-date `main`, preserving unrelated files. Report any branch or worktree that cannot be removed safely; never force-remove a submodule-containing worktree.

Deploy only when separately requested. If deploying, confirm the image tree matches the merged commit, back up persistent state before migrations, and verify the live health endpoints after switching images.
