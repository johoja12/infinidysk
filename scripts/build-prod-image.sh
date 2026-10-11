#!/usr/bin/env bash
# Builds `local/infinidysk:<sha8>` for the self-hosted prod instance from a clean, detached
# worktree of the given commit, stamped with the upstream + fork version label
# (scripts/fork-version.sh) so the UI and backend report which build is running.
#
# Usage: scripts/build-prod-image.sh <commit>
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"
sha="$(git rev-parse --verify "${1:?usage: $0 <commit>}^{commit}")"
short="${sha:0:8}"
version="$(scripts/fork-version.sh "$sha")"
# A unique directory: other sessions may be building the same commit concurrently.
tree="$(mktemp -d "/tmp/infinidysk-deploy-$short.XXXXXX")"
git worktree add -q --detach "$tree" "$sha"
# `git worktree remove` refuses worktrees holding the rapidyenc submodule.
trap 'rm -rf "$tree"; git worktree prune' EXIT
git -C "$tree" submodule update -q --init libs/rapidyenc

# Host networking: NuGet restore fails on DNS inside the default build network.
docker build --network=host --build-arg NZBDAV_VERSION="$version" -t "local/infinidysk:$short" "$tree"
echo "Built local/infinidysk:$short with NZBDAV_VERSION=$version"
