#!/usr/bin/env bash
# Prints the version label for a fork build: the newest upstream stable release the
# commit contains, plus the fork commit, e.g. `1.6.0+fork.3dc8cccb`.
#
# Usage: scripts/fork-version.sh [commit]   (default HEAD)
# Env:   UPSTREAM_REMOTE (default `upstream`, https://github.com/infinidysk/infinidysk)
#
# Upstream tags are fetched into refs/upstream-tags/ so they never collide with fork tags.
# The label is not a plain semver and carries no `main-<sha>` commit, so the UI's update
# check stays quiet instead of comparing fork commits against upstream history.
set -euo pipefail

rev="${1:-HEAD}"
remote="${UPSTREAM_REMOTE:-upstream}"
sha="$(git rev-parse --verify "${rev}^{commit}")"

if ! git remote get-url "$remote" >/dev/null 2>&1; then
  echo "No '$remote' remote; add it with: git remote add upstream https://github.com/infinidysk/infinidysk.git" >&2
  exit 1
fi
git fetch -q --no-tags "$remote" '+refs/tags/v*:refs/upstream-tags/v*'

release=""
while read -r tag; do
  case "$tag" in *-*) continue ;; esac # skip release candidates
  if git merge-base --is-ancestor "refs/upstream-tags/$tag" "$sha"; then
    release="$tag"
    break
  fi
done < <(git for-each-ref --sort=-v:refname --format='%(refname:lstrip=2)' refs/upstream-tags/)

if [ -z "$release" ]; then
  echo "No upstream stable release is contained in $sha" >&2
  exit 1
fi
echo "${release#v}+fork.${sha:0:8}"
