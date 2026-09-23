---
name: sync-upstream-fork
description: Sync upstream InfiniDysk main into the johoja12/infinidysk fork, auto-merge the validated PR, and deploy the merged image to the project's host. Use when asked to bring upstream changes into this fork.
---

# Sync upstream into the fork

Use this skill only in the InfiniDysk checkout. The target is `johoja12/infinidysk`, and the source is `infinidysk/infinidysk` `main`. Read the repository's `AGENTS.md` when available; its issue, PR, commit, and cleanup rules still apply.

The user authorized auto-merge and deployment as part of this workflow. A request to **run the upstream sync** authorizes auto-merging its resulting PR and deploying its merged commit, without another approval request. This authorization applies only to that sync PR and deployment. A read-only question about upstream status does not start this workflow.

## Prepare

1. Check the current branch and working tree. Preserve unrelated edits and untracked files. Verify `origin` points to `johoja12/infinidysk` and `upstream` points to `infinidysk/infinidysk`; correct a missing or wrong remote before fetching.
2. Fetch `origin/main` and `upstream/main`. Record both SHAs, their merge base, and the upstream-only commits. If `upstream/main` is already an ancestor of `origin/main`, report that the fork is current and stop without a PR.
3. Create an isolated branch from the current `origin/main`. Merge the full `upstream/main` history; do not cherry-pick a release tag or replace the fork's tree with upstream's tree. Resolve conflicts by checking both sides' intent and preserving fork-only capabilities. Never use a blanket `ours` or `theirs` strategy.

## Validate and create the PR

1. Inspect the merged diff for semantic conflicts, version changes, and migrations. For additive migrations, include a `/config` backup note in the PR. Mark irreversible or destructive migrations as breaking according to `AGENTS.md`.
2. Verify `upstream/main` is an ancestor of the branch tip and the branch has no unresolved conflicts. Run focused checks where they add evidence; use the PR's CI for its covered lanes. Investigate failures, including failures already present upstream, before treating the result as ready.
3. Commit with a Conventional Commit message, push the branch to `origin`, and create a PR against `main` with `gh pr create --repo johoja12/infinidysk`. Read the PR back to confirm its URL, head branch, and head SHA.

## Auto-merge, then deploy

Wait for the relevant PR build, test, migration, contract, security, and runtime checks to finish. Check failures against the logs; do not let a repository with no required checks merge an untested PR. Before merging, re-fetch both remotes, ensure the PR still has the expected head SHA, verify that the latest upstream tip is included, and inspect checks and review state. Resolve new upstream or fork changes on the PR branch and rerun checks first. Once validated, merge that exact PR without another user prompt (`gh pr merge <number> --repo johoja12/infinidysk --merge`) and wait until it reports `MERGED`. Do not depend on GitHub's repository-wide auto-merge setting; merge automatically after this workflow's own validation. Never bypass failed checks or review requirements.

Verify `origin/main` contains the upstream tip and the merged source tree matches the image source. Then follow [the nuc-1 deployment procedure](references/deploy-nuc-1.md). Deployment is complete only after live frontend and backend health checks pass. If deployment fails, restore the previous image and report the failure.

Follow the repository's merged-branch cleanup rules and return the main checkout to up-to-date `main`, preserving unrelated files. Report any branch or worktree that cannot be removed safely; never force-remove a submodule-containing worktree. Report the upstream SHA, PR, merge commit, deployed image/commit, health result, and cleanup result.
