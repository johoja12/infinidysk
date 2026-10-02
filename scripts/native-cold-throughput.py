#!/usr/bin/env python3
"""Repeatable uncached-playback throughput test against a running InfiniDysk backend.

For each pinned DavItem it evicts the file from Native Cache, streams one byte range over
WebDAV exactly as a player would, then requires the same read to have committed that range
to the cache. Reusing the same item list on every run makes results comparable between
builds; evicting first keeps every run cold.

    INFINIDYSK_API_KEY=... WEBDAV_USER=... WEBDAV_PASS=... \\
      scripts/native-cold-throughput.py --base-url http://127.0.0.1:18080 \\
        --items items.txt --label main --json-out main.jsonl
    scripts/native-cold-throughput.py --compare main.jsonl candidate.jsonl

Only stdlib is used. Point it at a test instance: eviction removes cached bytes.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import statistics
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

BLOCK = 4 * 1024 * 1024


class Backend:
    def __init__(self, base_url: str) -> None:
        self.base = base_url.rstrip("/")
        self.api_key = os.environ["INFINIDYSK_API_KEY"]
        creds = f"{os.environ['WEBDAV_USER']}:{os.environ['WEBDAV_PASS']}".encode()
        self.basic = "Basic " + base64.b64encode(creds).decode()

    def api(self, path: str, body: dict | None = None) -> dict:
        data = None if body is None else json.dumps(body).encode()
        request = urllib.request.Request(f"{self.base}/api/{path}", data=data,
                                         headers={"x-api-key": self.api_key, "Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=60) as response:
            return json.load(response)

    def entries(self, item_id: str) -> list[dict]:
        page = self.api(f"native-cache/files?search={item_id.replace('-', '')}&limit=20")
        return [row for row in page.get("items", []) if row["itemId"] == item_id.replace("-", "")]

    def evict(self, item_id: str, attempts: int = 10) -> None:
        # An entry still leased by a just-finished read (write-behind, readers) refuses eviction;
        # retry until the lease is released.
        error = None
        for _ in range(attempts):
            rows = self.entries(item_id)
            if not rows:
                return
            for row in rows:
                job = self.api("native-cache/operations", {"folderId": row["folderId"], "operation": "evict",
                                                           "cacheKey": row["key"], "confirmCacheKey": row["key"]})
                deadline = time.time() + 120
                while time.time() < deadline:
                    jobs = {j["id"]: j for j in self.api("native-cache").get("jobs", [])}
                    current = jobs.get(job["id"], {})
                    if current.get("state", "").lower() in ("completed", "failed", "cancelled"):
                        error = current.get("error") or error
                        break
                    time.sleep(1)
            time.sleep(3)
        raise RuntimeError(f"{item_id}: native cache entry still present after eviction ({error})")

    def verified_bytes(self, item_id: str, start: int, end: int) -> int:
        total = 0
        for row in self.entries(item_id):
            after = max(-1, start - BLOCK)
            while after < end:
                page = self.api(f"native-cache/ranges?key={row['key']}&afterOffset={after}&limit=100")
                for block in page["ranges"]:
                    lo, hi = block["offset"], block["offset"] + block["count"]
                    total += max(0, min(hi, end) - max(lo, start))
                if page.get("nextAfter") is None:
                    break
                after = page["nextAfter"]
        return total

    def stream(self, item_id: str, start: int, length: int) -> dict:
        n = item_id.replace("-", "")
        path = f"/.ids/{n[0]}/{n[1]}/{n[2]}/{n[3]}/{n[4]}/{item_id}"
        request = urllib.request.Request(self.base + path, headers={
            "Authorization": self.basic, "Range": f"bytes={start}-{start + length - 1}"})
        sha, received, windows = hashlib.sha256(), 0, []
        began = time.monotonic()
        first = None
        window_start, window_bytes = began, 0
        try:
            with urllib.request.urlopen(request, timeout=120) as response:
                status = response.status
                while chunk := response.read(256 * 1024):
                    now = time.monotonic()
                    first = first if first is not None else now - began
                    sha.update(chunk)
                    received += len(chunk)
                    window_bytes += len(chunk)
                    if now - window_start >= 5:
                        windows.append(round(window_bytes / (now - window_start) / 1e6, 2))
                        window_start, window_bytes = now, 0
        except urllib.error.HTTPError as error:
            status = error.code
        elapsed = time.monotonic() - began
        return {"status": status, "bytes": received, "ttfb_s": round(first or 0, 3), "wall_s": round(elapsed, 3),
                "mb_s": round(received / max(elapsed, 1e-9) / 1e6, 3), "windows_mb_s": windows,
                "sha256": sha.hexdigest()}


def run(args: argparse.Namespace) -> int:
    backend = Backend(args.base_url)
    items = [line.split("|")[0].strip() for line in Path(args.items).read_text().splitlines()
             if line.strip() and not line.startswith("#")]
    results, failures = [], 0
    out = open(args.json_out, "a", encoding="utf-8") if args.json_out else None
    for repeat in range(args.repeat):
        for item in items:
            backend.evict(item)
            result = backend.stream(item, args.offset, args.length)
            end = args.offset + result["bytes"]
            # Write-behind commits trail the response slightly; whole blocks inside the range must land.
            expected = max(0, (end // BLOCK) * BLOCK - ((args.offset + BLOCK - 1) // BLOCK) * BLOCK)
            deadline, cached = time.time() + args.commit_wait, 0
            while time.time() < deadline:
                cached = backend.verified_bytes(item, args.offset, end)
                if cached >= expected:
                    break
                time.sleep(1)
            ok = result["status"] == 206 and result["bytes"] == args.length and cached >= expected
            failures += not ok
            row = {"label": args.label, "repeat": repeat, "item": item, "offset": args.offset,
                   "length": args.length, "cached_bytes": cached, "cache_expected": expected, "ok": ok, **result}
            results.append(row)
            if out:
                out.write(json.dumps(row) + "\n")
                out.flush()
            print(f"{args.label} r{repeat} {item[:8]} status={result['status']} {result['mb_s']:.2f} MB/s "
                  f"ttfb={result['ttfb_s']}s cached={cached}/{expected} {'ok' if ok else 'FAIL'}", flush=True)
    summarize(args.label, results)
    return 1 if failures else 0


def summarize(label: str, rows: list[dict]) -> dict:
    good = [r for r in rows if r["ok"]]
    speeds = [r["mb_s"] for r in good]
    summary = {"label": label, "runs": len(rows), "ok": len(good),
               "median_mb_s": round(statistics.median(speeds), 2) if speeds else None,
               "min_mb_s": min(speeds, default=None), "max_mb_s": max(speeds, default=None),
               "median_ttfb_s": round(statistics.median(r["ttfb_s"] for r in good), 3) if good else None}
    print(json.dumps(summary))
    return summary


def compare(paths: list[str]) -> int:
    by_label = {}
    for path in paths:
        rows = [json.loads(line) for line in Path(path).read_text().splitlines() if line.strip()]
        by_label[path] = rows
    summaries = {path: summarize(rows[0]["label"] if rows else path, rows) for path, rows in by_label.items()}
    base, *others = paths
    base_rows = {(r["item"], r["repeat"]): r for r in by_label[base] if r["ok"]}
    for other in others:
        ratios = [r["mb_s"] / base_rows[(r["item"], r["repeat"])]["mb_s"]
                  for r in by_label[other] if r["ok"] and (r["item"], r["repeat"]) in base_rows]
        if ratios:
            print(f"{other} vs {base}: paired median ratio {statistics.median(ratios):.2f}x over {len(ratios)} reads")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--base-url")
    parser.add_argument("--items", help="file of DavItem ids (one per line; '#' comments; extra '|' fields ignored)")
    parser.add_argument("--offset", type=int, default=1024 ** 3)
    parser.add_argument("--length", type=int, default=512 * 1024 ** 2)
    parser.add_argument("--repeat", type=int, default=1)
    parser.add_argument("--label", default="run")
    parser.add_argument("--json-out")
    parser.add_argument("--commit-wait", type=int, default=60, help="seconds to wait for cache commits")
    parser.add_argument("--compare", nargs="+", metavar="JSONL")
    args = parser.parse_args()
    if args.compare:
        return compare(args.compare)
    if not (args.base_url and args.items):
        parser.error("--base-url and --items are required unless --compare is used")
    return run(args)


if __name__ == "__main__":
    sys.exit(main())
