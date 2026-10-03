#!/usr/bin/env python3
"""Fail CI when high/critical npm advisories are present.

Reads `npm audit --json` output (npm 7+) and exits nonzero for High/Critical advisories that
are not covered by a non-expired allowlist entry. Mirrors check-nuget-vulnerabilities.py: an
exception names packageId, advisoryUrl, reason and expiresOn (YYYY-MM-DD); expired entries are
ignored, so the finding fails CI again.
"""

from __future__ import annotations

import argparse
import json
import sys
from dataclasses import dataclass
from datetime import date, datetime, timezone
from pathlib import Path
from typing import Any, Iterable
from urllib.parse import urlsplit, urlunsplit

SEVERITY_RANK = {"info": 0, "low": 1, "moderate": 2, "high": 3, "critical": 4}


@dataclass(frozen=True)
class Finding:
    package_id: str
    severity: str
    advisory_url: str
    title: str
    range: str


@dataclass(frozen=True)
class AllowlistEntry:
    package_id: str
    advisory_url: str
    reason: str
    expires_on: date


def normalize_advisory_url(url: str) -> str:
    parts = urlsplit((url or "").strip())
    return urlunsplit((parts.scheme.lower(), parts.netloc.lower(), parts.path.rstrip("/"), "", ""))


def parse_allowlist(path: Path | None, today: date) -> list[AllowlistEntry]:
    if path is None:
        return []
    data = json.loads(path.read_text(encoding="utf-8"))
    entries: list[AllowlistEntry] = []
    for raw in data.get("exceptions", []):
        expires_on = date.fromisoformat(str(raw["expiresOn"]))
        if expires_on < today:
            continue
        reason = str(raw.get("reason", "")).strip()
        if not reason:
            raise ValueError(f"Allowlist entry for {raw.get('packageId')} must explain why it is safe.")
        entries.append(AllowlistEntry(
            package_id=str(raw["packageId"]).strip(),
            advisory_url=normalize_advisory_url(str(raw["advisoryUrl"])),
            reason=reason,
            expires_on=expires_on,
        ))
    return entries


def collect_findings(report: dict[str, Any]) -> list[Finding]:
    """Advisories are the object entries in each package's `via`; string entries only point at
    the vulnerable dependency, which carries the advisory itself."""
    findings: dict[tuple[str, str], Finding] = {}
    for name, vulnerability in (report.get("vulnerabilities") or {}).items():
        for via in vulnerability.get("via") or []:
            if not isinstance(via, dict):
                continue
            package = str(via.get("name") or name)
            url = str(via.get("url") or "")
            findings[(package, normalize_advisory_url(url))] = Finding(
                package_id=package,
                severity=str(via.get("severity") or vulnerability.get("severity") or "").lower(),
                advisory_url=url,
                title=str(via.get("title") or ""),
                range=str(via.get("range") or ""),
            )
    return list(findings.values())


def is_allowed(finding: Finding, allowlist: Iterable[AllowlistEntry]) -> AllowlistEntry | None:
    advisory = normalize_advisory_url(finding.advisory_url)
    for entry in allowlist:
        if entry.package_id.lower() == finding.package_id.lower() and entry.advisory_url == advisory:
            return entry
    return None


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path, help="npm audit --json output")
    parser.add_argument("--allowlist", type=Path)
    parser.add_argument("--level", default="high", choices=sorted(SEVERITY_RANK))
    parser.add_argument("--today", type=date.fromisoformat, default=datetime.now(timezone.utc).date())
    args = parser.parse_args(argv)

    report = json.loads(args.report.read_text(encoding="utf-8"))
    threshold = SEVERITY_RANK[args.level]
    allowlist = parse_allowlist(args.allowlist, args.today)
    failures = 0
    for finding in sorted(collect_findings(report), key=lambda f: (f.package_id, f.advisory_url)):
        if SEVERITY_RANK.get(finding.severity, 0) < threshold:
            continue
        allowed = is_allowed(finding, allowlist)
        if allowed:
            print(f"allowed until {allowed.expires_on}: {finding.package_id} {finding.severity} "
                  f"{finding.advisory_url} ({allowed.reason})")
            continue
        failures += 1
        print(f"error: {finding.package_id} {finding.range} {finding.severity}: {finding.title} "
              f"{finding.advisory_url}", file=sys.stderr)
    if failures:
        print(f"npm audit gate failed: {failures} {args.level}+ advisory(ies). Fix the dependency, or add a "
              "justified, expiring entry to scripts/npm-audit-allowlist.json.", file=sys.stderr)
        return 1
    print("npm audit gate passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
