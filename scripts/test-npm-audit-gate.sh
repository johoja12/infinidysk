#!/usr/bin/env bash
# Fixture checks for scripts/check-npm-audit.py
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GATE="${ROOT}/scripts/check-npm-audit.py"
FIXTURES="${ROOT}/scripts/fixtures"

python3 "${GATE}" "${FIXTURES}/npm-audit-clean.json"

if python3 "${GATE}" "${FIXTURES}/npm-audit-high.json"; then
  echo "expected high fixture to fail" >&2
  exit 1
fi

python3 "${GATE}" "${FIXTURES}/npm-audit-high.json" --allowlist "${FIXTURES}/npm-audit-allowlist.json"

if python3 "${GATE}" "${FIXTURES}/npm-audit-high.json" \
  --allowlist "${FIXTURES}/npm-audit-allowlist.json" --today 2099-01-02; then
  echo "expected expired allowlist entry to fail" >&2
  exit 1
fi

if python3 "${GATE}" "${FIXTURES}/npm-audit-high.json" --allowlist "${FIXTURES}/npm-audit-allowlist.json" \
  --level moderate; then
  echo "expected unlisted moderate advisory to fail at --level moderate" >&2
  exit 1
fi

echo "npm audit gate fixtures passed."
